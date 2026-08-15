using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LlamaChat
{
    // ===================================================================
    // Miya 语言风格转换服务（C# 版，移植自 Python example.py）
    // 使用 llama.cpp llama-server（端口 38090，Qwen2.5-0.5B-Q4_K_M.gguf）
    // 通过 /completion + cache_prompt 增量生成，在客户端实现语义漂移停止准则
    // ===================================================================
    public class StyleTransferService : IDisposable
    {
        // ---- 配置 ----
        public const int Port = 38090;
        public const string ModelFile = "Qwen2.5-0.5B-Q4_K_M.gguf";
        public const int ContextSize = 8192;

        private const string ZhPrompt = "将以下中文句子转换为口语化、可爱、女性化、略带撒娇的风格。";
        private const string EnPrompt = "Convert the following English sentence into a colloquial, cute, feminine, and slightly coquettish style.";

        // ---- 正则（移植自 Python）----
        private static readonly Regex SentenceEndZh = new("[。！？!?～~]");
        private static readonly Regex SentenceEndEn = new("[。！？!?～~.]");

        private static readonly Regex EnStageDirectionRe = new(
            @"\(\s*(?:(?:she|he|i|we|you|they)\s+)?(?:yawn|blush|giggle|smile|nod|laugh|sigh|wink|pout|frown|grin|shrug|murmur|cough|gasp|whisper)(?:s|es|ing|ed)?\s*(?:softly|slightly|a\s+bit|a\s+little|quietly|happily|shyly)?\s*\)",
            RegexOptions.IgnoreCase);

        private static readonly Regex EnTemplateArtifactRe = new(@"\(\s*insert\s+[^)]*here\s*\)", RegexOptions.IgnoreCase);

        private static readonly Regex EnTrailingFillerRe = new(
            @",\s*(?:like|y'?know|you\s+know|right|huh|okay|and\s+stuff|or\s+something|i\s+think|i\s+guess|and\s+all|or\s+so|i\s+really|i\s+just)\s*$",
            RegexOptions.IgnoreCase);

        private static readonly HashSet<string> EnStopwords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a","an","the","and","or","but","nor","for","so","yet","to","in","on","at","by","with","from","of","as",
            "is","are","was","were","be","been","being","am","do","does","did","have","has","had","will","would",
            "shall","should","can","could","may","might","must","not","no","yes","very","just","about","into","over",
            "under","above","below","up","down","out","off","again","further","once","here","there","when","where",
            "why","how","all","any","both","each","few","more","most","other","some","such","only","own","same",
            "than","too","then","also","really","like","because","if","while","during","after","before","between",
            "among","through","your","my","our","their","his","her","its","you","i","he","she","we","they","me",
            "him","us","them","it","this","that","these","those","what","who","whom"
        };

        private const string PunctChars = ".,!?;:'\"()[]{}<>-–—~`^#$%&*+=|\\/@_。，！？；：、·「」『』（）【】《》…";
        private const string SentencePunctChars = "。！？.!?～~";

        // ---- 服务器状态 ----
        private Process _serverProcess;
        private readonly HttpClient _http;
        private bool _serverReady = false;

        // 界面语言（仅用于启动消息；Auto = 跟随系统）
        private readonly AppLanguage _language;

        // ---- token 预热缓存 ----
        private HashSet<int> _stopwordTokenIds = new();
        private HashSet<int> _punctTokenIds = new();
        private HashSet<int> _sentencePunctTokenIds = new();

        public StyleTransferService(AppLanguage language = AppLanguage.Auto)
        {
            _language = language;
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            EnsureServer();
            WarmupAsync().GetAwaiter().GetResult();
        }

        // ===================================================================
        // 服务器管理（惰性启动 + 健康检查，被其他进程误杀后可自动恢复）
        // ===================================================================
        public void EnsureServer()
        {
            try
            {
                var resp = _http.GetAsync($"http://127.0.0.1:{Port}/health").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) { _serverReady = true; return; }
            }
            catch { }

            _serverReady = false;
            StartServer();
        }

        private void StartServer()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string llamaDir = Path.Combine(baseDir, "llama");
            string serverExe = Path.Combine(llamaDir, "llama-server.exe");
            string modelPath = Path.Combine(llamaDir, ModelFile);

            if (!File.Exists(serverExe))
                throw new FileNotFoundException(I18n.T(_language, "未找到 {0}", "Not found: {0}", serverExe));
            if (!File.Exists(modelPath))
                throw new FileNotFoundException(I18n.T(_language, "未找到风格转换模型 {0}", "Style-transfer model not found: {0}", modelPath));

            int threads = Environment.ProcessorCount;
            string args = $"-m \"{modelPath}\" --host 127.0.0.1 --port {Port} -c {ContextSize} -t {threads}";

            var si = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _serverProcess = new Process { StartInfo = si };
            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            ConsoleHelper.Prompt(I18n.T(_language, "正在启动风格转换 llama-server (端口 {0})...", "Starting style-transfer llama-server (port {0})...", Port));
            bool ready = WaitForServerAsync().GetAwaiter().GetResult();
            if (!ready)
                throw new TimeoutException(I18n.T(_language, "风格转换 llama-server (端口 {0}) 启动超时。", "Style-transfer llama-server (port {0}) startup timed out.", Port));
            _serverReady = true;
            ConsoleHelper.Success(I18n.T(_language, "风格转换 llama-server 已就绪！(端口 {0})", "Style-transfer llama-server ready! (port {0})", Port));
        }

        private async Task<bool> WaitForServerAsync(int maxSeconds = 120)
        {
            string url = $"http://127.0.0.1:{Port}/health";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            for (int i = 0; i < maxSeconds; i++)
            {
                try
                {
                    var resp = await client.GetAsync(url);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch { }
                await Task.Delay(1000);
            }
            return false;
        }

        // ===================================================================
        // llama.cpp HTTP 辅助
        // ===================================================================
        private async Task<List<int>> TokenizeAsync(string text)
        {
            var body = new JObject { ["content"] = text };
            var resp = await PostJsonAsync("/tokenize", body);
            return (resp["tokens"] as JArray)?.Select(t => t.Value<int>()).ToList() ?? new List<int>();
        }

        private async Task<JObject> PostJsonAsync(string path, JObject body)
        {
            var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"http://127.0.0.1:{Port}{path}", content);
            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync();
                throw new Exception($"HTTP {resp.StatusCode} {path}: {err}");
            }
            return JObject.Parse(await resp.Content.ReadAsStringAsync());
        }

        private async Task<(string Content, int TokensPredicted)> CompleteAsync(string prompt, int nPredict, double temperature, double topP, double rp)
        {
            var body = new JObject
            {
                ["prompt"] = prompt,
                ["n_predict"] = nPredict,
                ["temperature"] = temperature,
                ["top_p"] = topP,
                ["repeat_penalty"] = rp,
                ["cache_prompt"] = true,
                ["stream"] = false
            };
            var resp = await PostJsonAsync("/completion", body);
            string c = resp["content"]?.ToString() ?? "";
            int tp = resp["tokens_predicted"]?.Value<int>() ?? 0;
            return (c, tp);
        }

        // 启动时预热：停用词/标点 -> token id（供漂移检测用）
        private async Task WarmupAsync()
        {
            var stopTasks = EnStopwords.Select(async w =>
            {
                var ids = await TokenizeAsync(w);
                return ids.Count == 1 ? ids[0] : (int?)null;
            }).ToList();
            var stopResults = await Task.WhenAll(stopTasks);
            _stopwordTokenIds = stopResults.Where(r => r.HasValue).Select(r => r.Value).ToHashSet();

            var punctTasks = PunctChars.Select(async c =>
            {
                var ids = await TokenizeAsync(c.ToString());
                return ids.Count == 1 ? ids[0] : (int?)null;
            }).ToList();
            var punctResults = await Task.WhenAll(punctTasks);
            _punctTokenIds = punctResults.Where(r => r.HasValue).Select(r => r.Value).ToHashSet();

            var sentTasks = SentencePunctChars.Select(async c =>
            {
                var ids = await TokenizeAsync(c.ToString());
                return ids.Count == 1 ? ids[0] : (int?)null;
            }).ToList();
            var sentResults = await Task.WhenAll(sentTasks);
            _sentencePunctTokenIds = sentResults.Where(r => r.HasValue).Select(r => r.Value).ToHashSet();
        }

        private bool IsContentToken(int tid) => !_stopwordTokenIds.Contains(tid) && !_punctTokenIds.Contains(tid);

        // ===================================================================
        // 语言检测
        // ===================================================================
        public static string DetectLang(string text)
            => text.Any(c => c >= 0x4E00 && c <= 0x9FFF) ? "zh" : "en";

        // ===================================================================
        // 入口：转换整段 Markdown（自动检测语言）
        // ===================================================================
        public async Task<string> ConvertMarkdownAsync(string markdown)
        {
            EnsureServer(); // 若被误杀则自动重启
            string lang = DetectLang(markdown);
            int minNewTokens = lang == "en" ? 6 : 8;
            int windowSize = lang == "en" ? 15 : 20;
            return await ConvertMarkdownLineByLineAsync(markdown, lang, minNewTokens, windowSize);
        }

        // ===================================================================
        // 裁剪到最后一个完整句子
        // ===================================================================
        private static string TrimToLastCompleteSentence(string text, string lang)
        {
            if (lang != "en") text = text.Replace(" ", "");
            text = text.Trim();
            var re = lang == "en" ? SentenceEndEn : SentenceEndZh;
            var matches = re.Matches(text);
            if (matches.Count > 0)
            {
                var last = matches[^1];
                return text[..(last.Index + last.Length)];
            }
            return text;
        }

        // ===================================================================
        // 增量生成 + 语义漂移停止准则（客户端实现，对应 Python StoppingCriteria）
        // ===================================================================
        private async Task<string> GenerateWithDriftAsync(string prompt, string sourceText, string lang,
            int minNewTokens, int windowSize, double temperature, double topP, double rp)
        {
            var srcTokens = await TokenizeAsync(sourceText);
            var sourceSet = new HashSet<int>(srcTokens.Skip(Math.Max(0, srcTokens.Count - 50)));
            if (lang == "en") sourceSet = sourceSet.Where(IsContentToken).ToHashSet();

            int maxNew = Math.Min(512, Math.Max(srcTokens.Count * 3 + 20, 50));

            string generated = "";
            int step = 0;
            double maxSimilarity = 0.0;
            bool stopPending = false;
            int pendingTokens = 0;

            while (true)
            {
                var (chunk, tokPred) = await CompleteAsync(prompt + generated, windowSize, temperature, topP, rp);
                if (string.IsNullOrEmpty(chunk)) break;
                generated += chunk;
                step += tokPred;

                if (step < minNewTokens)
                {
                    if (step >= maxNew) break;
                    continue;
                }

                var genTokens = await TokenizeAsync(generated);
                if (genTokens.Count >= windowSize)
                {
                    var window = genTokens.Skip(genTokens.Count - windowSize).ToList();
                    var windowSet = new HashSet<int>(window);
                    if (lang == "en") windowSet = windowSet.Where(IsContentToken).ToHashSet();

                    double currentSim = 0;
                    int intersect = sourceSet.Count(t => windowSet.Contains(t));
                    int union = sourceSet.Count + windowSet.Count - intersect;
                    if (union > 0) currentSim = (double)intersect / union;
                    if (currentSim > maxSimilarity) maxSimilarity = currentSim;

                    double diversity = (double)window.Distinct().Count() / window.Count;
                    bool stop = false;
                    if (diversity < 0.35) stop = true;
                    if (!stop && step > minNewTokens + 5 && maxSimilarity > 0.05 && currentSim < maxSimilarity * 0.70)
                        stop = true;
                    if (!stop && step > minNewTokens + 10 && maxSimilarity > 0.10)
                    {
                        int pc = window.Count(t => _sentencePunctTokenIds.Contains(t));
                        if (pc >= 2 && currentSim < maxSimilarity * 0.85) stop = true;
                    }

                    // 边界等待（en 专用）
                    if (stop && lang == "en") stopPending = true;
                    if (stopPending)
                    {
                        pendingTokens += tokPred;
                        if (pendingTokens > windowSize) break;
                        if (diversity < 0.30) break;
                        if (genTokens.Skip(Math.Max(0, genTokens.Count - 4)).Any(t => _sentencePunctTokenIds.Contains(t)))
                            break;
                    }
                    else if (stop) break;
                }

                if (step >= maxNew) break;
            }
            return generated;
        }

        // ===================================================================
        // 转换单个文本段（对应 Python convert_text_segment）
        // ===================================================================
        public async Task<string> ConvertTextSegmentAsync(string textSegment, string lang, int minNewTokens, int windowSize)
        {
            textSegment = textSegment.Trim();
            if (string.IsNullOrEmpty(textSegment)) return textSegment;
            if (textSegment.Length < 20) return textSegment;

            string systemPrompt = lang == "zh" ? ZhPrompt : EnPrompt;
            string prompt = $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n{textSegment}<|im_end|>\n<|im_start|>assistant\n";

            // 采样参数：zh 用调好的 0.6/0.9/1.05；en 20-35 字符短句降低随机性
            double temperature = 0.6, topP = 0.9, rp = 1.05;
            if (lang == "en" && textSegment.Length >= 20 && textSegment.Length <= 35)
            {
                temperature = 0.35; topP = 0.75; rp = 1.1;
            }

            string raw = await GenerateWithDriftAsync(prompt, textSegment, lang, minNewTokens, windowSize, temperature, topP, rp);

            // 后处理
            if (lang == "en")
            {
                raw = Regex.Replace(raw, @"\s+", " ").Trim();
                raw = EnStageDirectionRe.Replace(raw, " ");
                raw = EnTemplateArtifactRe.Replace(raw, " ");
                raw = Regex.Replace(raw, @"\s+", " ").Trim();
            }
            string converted = TrimToLastCompleteSentence(raw, lang);
            if (lang == "en")
            {
                if (!SentenceEndEn.IsMatch(converted))
                {
                    int idx = converted.LastIndexOf(',');
                    if (idx > 0) converted = converted[..idx].TrimEnd();
                }
                converted = EnTrailingFillerRe.Replace(converted, "").TrimEnd();
                converted = converted.TrimEnd(',').TrimEnd();
                if (converted.Count(c => c == '(') > converted.Count(c => c == ')'))
                {
                    int idx = converted.LastIndexOf('(');
                    if (idx >= 0) converted = converted[..idx].TrimEnd();
                }
            }
            return converted;
        }

        // ===================================================================
        // 带链接保护的转换入口（对应 Python convert_text_with_links_preserved）
        // ===================================================================
        public async Task<string> ConvertTextWithLinksPreservedAsync(string textBlock, string lang, int minNewTokens, int windowSize)
        {
            if (string.IsNullOrWhiteSpace(textBlock)) return textBlock;
            if (lang == "en")
                return await ConvertTextEnProtectedAsync(textBlock, minNewTokens, windowSize);

            // zh：有链接 -> 只转换链接描述；无链接 -> 带结构保护的转换
            var linkPattern = new Regex(@"\[([^\]]+)\]\(([^)]+)\)");
            var matches = linkPattern.Matches(textBlock).ToList();
            if (matches.Count == 0)
                return await ConvertTextZhProtectedAsync(textBlock, minNewTokens, windowSize);

            string result = textBlock;
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var m = matches[i];
                string desc = m.Groups[1].Value;
                string url = m.Groups[2].Value;
                string convertedDesc = await ConvertTextSegmentAsync(desc, lang, minNewTokens, windowSize);
                result = result[..m.Index] + $"[{convertedDesc}]({url})" + result[(m.Index + m.Length)..];
            }
            return result;
        }

        // ===================================================================
        // en 专用：占位符方案（对应 Python convert_text_en_protected）
        // ===================================================================
        public async Task<string> ConvertTextEnProtectedAsync(string textBlock, int minNewTokens, int windowSize)
        {
            // 行首粗体标签作为前缀原样保留
            string prefixBold = "";
            var m0 = Regex.Match(textBlock, @"^\*\*[^*]+\*\*(?::\s*|\s*)");
            if (m0.Success)
            {
                prefixBold = m0.Value;
                textBlock = textBlock[m0.Length..];
            }

            var pattern = new Regex(
                @"(\[[^\]]+\]\([^)]+\)" +   // 链接
                @"|\*\*[^*]+\*\*" +         // 粗体
                @"|\*(?!\*)[^*\n]+\*(?!\*)" + // 斜体
                @"|~~[^~\n]+~~" +           // 删除线
                @"|`[^`]+`" +               // 行内代码
                @"|\[\^[^\]]+\]" +          // 脚注引用
                @"|""[^""]+"")");           // 双引号内容

            var mapping = new Dictionary<string, string>();
            var counters = new Dictionary<string, int>
            {
                ["LINK"] = 0, ["BOLD"] = 0, ["ITAL"] = 0, ["STRIKE"] = 0, ["CODE"] = 0, ["NOTE"] = 0, ["QUOTE"] = 0
            };
            var sb = new StringBuilder();
            int pos = 0;
            foreach (Match m in pattern.Matches(textBlock))
            {
                if (m.Index > pos) sb.Append(textBlock, pos, m.Index - pos);
                string seg = m.Value;
                string tag;
                if (seg.StartsWith("**")) tag = "BOLD";
                else if (seg.StartsWith("*")) tag = "ITAL";
                else if (seg.StartsWith("~~")) tag = "STRIKE";
                else if (seg.StartsWith("`")) tag = "CODE";
                else if (seg.StartsWith("[^")) tag = "NOTE";
                else if (seg.StartsWith("[")) tag = "LINK";
                else tag = "QUOTE";
                string ph = $"{tag}{counters[tag]}";
                counters[tag]++;
                mapping[ph] = seg;
                sb.Append(ph);
                pos = m.Index + m.Length;
            }
            if (pos < textBlock.Length) sb.Append(textBlock, pos, textBlock.Length - pos);
            string phText = sb.ToString();

            string converted = await ConvertTextSegmentAsync(phText, "en", minNewTokens, windowSize);

            // 还原（大小写不敏感），丢掉的元素补到句末
            var missing = mapping.Keys.Where(ph => !Regex.IsMatch(converted, Regex.Escape(ph), RegexOptions.IgnoreCase)).ToList();
            foreach (var (ph, orig) in mapping)
                converted = Regex.Replace(converted, Regex.Escape(ph), m => orig, RegexOptions.IgnoreCase);
            foreach (var ph in missing)
                converted = converted.TrimEnd() + " " + mapping[ph];

            // 移除模型新造的行内代码
            var origCode = new HashSet<string>(mapping.Values.Where(v => v.StartsWith("`") && v.EndsWith("`")));
            if (origCode.Count > 0 || converted.Contains('`'))
            {
                converted = Regex.Replace(converted, @"`[^`]+`", m => origCode.Contains(m.Value) ? m.Value : "");
                if (converted.Count(c => c == '`') % 2 == 1)
                {
                    int idx = converted.LastIndexOf('`');
                    if (idx >= 0) converted = converted[..idx];
                }
                converted = Regex.Replace(converted, @"\s{2,}", " ").Trim();
            }

            return prefixBold + converted;
        }

        // ===================================================================
        // zh 专用：Z 占位符方案（对应 Python convert_text_zh_protected）
        // ===================================================================
        public async Task<string> ConvertTextZhProtectedAsync(string textBlock, int minNewTokens, int windowSize)
        {
            // 行首保护元素作为前缀原样保留
            string prefixBold = "";
            var m0 = Regex.Match(textBlock,
                @"^(?:\*\*[^*]+\*\*|\*(?!\*)[^*\n]+\*(?!\*)" +   // 粗体/斜体
                @"|~~[^~\n]+~~|`[^`]+`|\[\^[^\]]+\]|""[^""]+"")" + // 删除线/代码/脚注/引号
                @"(?::\s*|\s*)");
            if (m0.Success)
            {
                prefixBold = m0.Value;
                textBlock = textBlock[m0.Length..];
            }

            var pattern = new Regex(
                @"(\*\*[^*]+\*\*" +          // 粗体
                @"|\*(?!\*)[^*\n]+\*(?!\*)" + // 斜体
                @"|~~[^~\n]+~~" +            // 删除线
                @"|`[^`]+`" +                // 行内代码
                @"|\[\^[^\]]+\]" +           // 脚注引用
                @"|""[^""]+"")");            // 双引号内容

            // Z 系列无义占位符（避免 CODE0 等英文词被中文模型翻译）
            var mapping = new Dictionary<string, string>();
            var sb = new StringBuilder();
            int pos = 0, zIdx = 0;
            foreach (Match m in pattern.Matches(textBlock))
            {
                if (m.Index > pos) sb.Append(textBlock, pos, m.Index - pos);
                string ph = $"Z{zIdx++}";
                mapping[ph] = m.Value;
                sb.Append(ph);
                pos = m.Index + m.Length;
            }
            if (pos < textBlock.Length) sb.Append(textBlock, pos, textBlock.Length - pos);
            string phText = sb.ToString();

            string converted = await ConvertTextSegmentAsync(phText, "zh", minNewTokens, windowSize);

            var missing = mapping.Keys.Where(ph => !Regex.IsMatch(converted, Regex.Escape(ph), RegexOptions.IgnoreCase)).ToList();
            foreach (var (ph, orig) in mapping)
                converted = Regex.Replace(converted, Regex.Escape(ph), m => orig, RegexOptions.IgnoreCase);
            foreach (var ph in missing)
                converted = converted.TrimEnd() + " " + mapping[ph];

            // 移除模型新造的行内代码
            var origCode = new HashSet<string>(mapping.Values.Where(v => v.StartsWith("`") && v.EndsWith("`")));
            if (origCode.Count > 0 || converted.Contains('`'))
            {
                converted = Regex.Replace(converted, @"`[^`]+`", m => origCode.Contains(m.Value) ? m.Value : "");
                if (converted.Count(c => c == '`') % 2 == 1)
                {
                    int idx = converted.LastIndexOf('`');
                    if (idx >= 0) converted = converted[..idx];
                }
                converted = Regex.Replace(converted, @"\s{2,}", " ").Trim();
            }

            return prefixBold + converted;
        }

        // ===================================================================
        // 逐行转换 Markdown（对应 Python convert_markdown_line_by_line）
        // ===================================================================
        public async Task<string> ConvertMarkdownLineByLineAsync(string rawMarkdown, string lang, int minNewTokens, int windowSize)
        {
            var lines = rawMarkdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            var convertedLines = new List<string>();
            int i = 0, n = lines.Count;

            while (i < n)
            {
                string line = lines[i];
                string stripped = line.Trim();

                // ---- 空行 ----
                if (stripped.Length == 0)
                {
                    convertedLines.Add(line);
                    i++;
                    continue;
                }

                // ---- 代码块 ----
                if (stripped.StartsWith("```"))
                {
                    var codeBlock = new List<string> { line };
                    i++;
                    while (i < n && !lines[i].Trim().StartsWith("```"))
                    {
                        codeBlock.Add(lines[i]);
                        i++;
                    }
                    if (i < n) { codeBlock.Add(lines[i]); i++; }
                    convertedLines.Add(string.Join("\n", codeBlock));
                    continue;
                }

                // ---- 表格 ----
                if (stripped.StartsWith("|"))
                {
                    var tableLines = new List<string> { line };
                    i++;
                    while (i < n)
                    {
                        string nxt = lines[i].Trim();
                        if (nxt.StartsWith("|") || nxt.Contains("---"))
                        {
                            tableLines.Add(lines[i]);
                            i++;
                        }
                        else break;
                    }
                    if (tableLines.Count >= 2)
                    {
                        convertedLines.Add(string.Join("\n", tableLines));
                        continue;
                    }
                    else
                    {
                        i--; // 回退
                    }
                }

                // ---- 图片 ----
                if (Regex.IsMatch(stripped, @"!\[.*\]\(.*\)"))
                {
                    convertedLines.Add(line);
                    i++;
                    continue;
                }

                // ---- 脚注定义 ----
                var footMatch = Regex.Match(stripped, @"^(\[\^[^\]]+\]:\s*)(.*)$");
                if (footMatch.Success)
                {
                    string footPrefix = footMatch.Groups[1].Value;
                    string footContent = footMatch.Groups[2].Value.Trim();
                    if (footContent.Length > 0)
                    {
                        string footConverted = await ConvertTextWithLinksPreservedAsync(footContent, lang, minNewTokens, windowSize);
                        convertedLines.Add(footPrefix + footConverted);
                    }
                    else
                    {
                        convertedLines.Add(line);
                    }
                    i++;
                    continue;
                }

                // ---- 前缀提取 ----
                string prefix = "";
                string content = stripped;
                bool isHeading = false;

                // 标题
                var hm = Regex.Match(stripped, @"^(#{1,6})\s+(.*)$");
                if (hm.Success)
                {
                    prefix = hm.Groups[1].Value + " ";
                    content = hm.Groups[2].Value;
                    isHeading = true;
                    // en：保护编号前缀（3. / 2.4）
                    if (lang == "en")
                    {
                        var nm = Regex.Match(content, @"^(\d+(?:\.\d+)*[\.\)]?\s+)(.*)$");
                        if (nm.Success)
                        {
                            prefix += nm.Groups[1].Value;
                            content = nm.Groups[2].Value;
                        }
                    }
                }
                else
                {
                    // 任务列表（必须先于无序列表）
                    var tm = Regex.Match(stripped, @"^(\- \[[ xX]\])\s+(.*)$");
                    if (tm.Success)
                    {
                        prefix = tm.Groups[1].Value + " ";
                        content = tm.Groups[2].Value;
                    }
                    else
                    {
                        // 无序列表
                        var um = Regex.Match(stripped, @"^([\-\*\+])\s+(.*)$");
                        if (um.Success)
                        {
                            prefix = um.Groups[1].Value + " ";
                            content = um.Groups[2].Value;
                        }
                        else
                        {
                            // 有序列表
                            var om = Regex.Match(stripped, @"^(\d+\.)\s+(.*)$");
                            if (om.Success)
                            {
                                prefix = om.Groups[1].Value + " ";
                                content = om.Groups[2].Value;
                            }
                            else
                            {
                                // 引用
                                var qm = Regex.Match(stripped, @"^(>)\s*(.*)$");
                                if (qm.Success)
                                {
                                    prefix = qm.Groups[1].Value + " ";
                                    content = qm.Groups[2].Value;
                                }
                            }
                        }
                    }
                }

                if (content.Length == 0)
                {
                    convertedLines.Add(line);
                    i++;
                    continue;
                }

                // en 标题用更紧的停止参数
                int lineMinNt = minNewTokens, lineWindow = windowSize;
                if (lang == "en" && isHeading)
                {
                    lineMinNt = 4;
                    lineWindow = 10;
                }

                string convertedContent = await ConvertTextWithLinksPreservedAsync(content, lang, lineMinNt, lineWindow);

                // 重新组合：行首缩进 + 前缀 + 转换内容
                string leading = line[..(line.Length - line.TrimStart().Length)];
                convertedLines.Add(leading + prefix + convertedContent);
                i++;
            }

            return string.Join("\n", convertedLines);
        }

        // ===================================================================
        // 释放资源
        // ===================================================================
        public void Dispose()
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                try
                {
                    _serverProcess.Kill();
                    _serverProcess.WaitForExit(5000);
                }
                catch { }
                _serverProcess.Dispose();
                _serverProcess = null;
            }
            _http?.Dispose();
        }
    }
}
