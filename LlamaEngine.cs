using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using LlamaChat.Native;

namespace LlamaChat
{
    /// <summary>单次（或累计）推理的耗时与速度统计。</summary>
    public struct EngineStats
    {
        public long PromptTokens;      // 已处理的提示词 token 数（仅新增未缓存部分）
        public double PromptMs;        // 提示词处理耗时
        public long GeneratedTokens;   // 生成的 token 数
        public double GeneratedMs;     // 生成耗时

        public bool HasData => PromptTokens > 0 || GeneratedTokens > 0;

        public double PromptTokensPerSec => PromptMs > 0 ? PromptTokens * 1000.0 / PromptMs : 0.0;
        public double GeneratedTokensPerSec => GeneratedMs > 0 ? GeneratedTokens * 1000.0 / GeneratedMs : 0.0;
    }

    // ===================================================================
    // 单模型原生推理引擎（直接调用 Lumina-Engine.dll，无 llama-server / 无 HTTP）
    //
    // 提供三类能力：
    //   1) Tokenize / Detokenize
    //   2) Complete：原始补全（带 KV 前缀复用，供 StyleTransferService 使用）
    //   3) Chat：ChatML（Qwen 系）对话 + <tool_call> 工具调用解析
    // ===================================================================
    public sealed class LlamaEngine : IDisposable
    {
        // ---- 采样参数（默认值对齐 llama-server 常用默认） ----
        public sealed class SamplingOptions
        {
            public int MaxTokens = 512;
            public float Temperature = 0.8f;
            public int TopK = 40;
            public float TopP = 0.95f;
            public float MinP = 0.05f;
            public int RepeatLastN = 64;
            public float RepeatPenalty = 1.0f;
            public uint Seed = LlamaNative.LLAMA_DEFAULT_SEED;
        }

        // 对话消息（与 OpenAI 消息结构一一对应；ToolCalls 仅 assistant 使用）
        public sealed class ChatTurn
        {
            public string Role = "user";
            public string Content = "";
            public List<(string Name, string Arguments)> ToolCalls;

            public static ChatTurn System(string c) => new ChatTurn { Role = "system", Content = c ?? "" };
            public static ChatTurn User(string c) => new ChatTurn { Role = "user", Content = c ?? "" };
            public static ChatTurn Assistant(string c) => new ChatTurn { Role = "assistant", Content = c ?? "" };
            public static ChatTurn Tool(string c) => new ChatTurn { Role = "tool", Content = c ?? "" };
        }

        private static readonly object BackendLock = new object();
        private static bool _backendInitialized;
        private static LlamaNative.ggml_log_callback _logCallback;   // 防止被 GC 回收

        /// <summary>llama.cpp 内部日志输出（由宿主设置，例如接到应用日志）。</summary>
        public static Action<string> LogSink;

        private IntPtr _model = IntPtr.Zero;
        private IntPtr _ctx = IntPtr.Zero;
        private IntPtr _vocab = IntPtr.Zero;
        private LlamaNative.llama_batch _batch;
        private bool _batchAllocated;

        private readonly object _lock = new object();
        private readonly int _nCtx;
        private readonly int _nBatch = 512;
        private readonly List<int> _cache = new List<int>();   // 当前 KV 中的 token 序列（seq 0）
        private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
        private bool _disposed;

        // ---- 速度统计（自上次 ResetStats 起累计）----
        private EngineStats _stats;

        /// <summary>当前累计统计。</summary>
        public EngineStats Stats => _stats;

        /// <summary>清零统计（每次回答/转换开始前调用）。</summary>
        public void ResetStats()
        {
            _stats = default;
        }

        /// <summary>是否为 Qwen3 系（生成提示需预填空 &lt;think&gt; 块）。</summary>
        public bool ThinkPrefill { get; }
        public int NCtx => _nCtx;
        public int VocabSize => _vocab == IntPtr.Zero ? 0 : (int)LlamaNative.llama_vocab_n_tokens(_vocab);
        public string ModelPath { get; }

        public LlamaEngine(string modelPath, string modelFile, int nCtx, int nThreads)
        {
            if (!System.IO.File.Exists(modelPath))
                throw new System.IO.FileNotFoundException($"模型文件不存在: {modelPath}", modelPath);

            ModelPath = modelPath;

            lock (BackendLock)
            {
                if (!_backendInitialized)
                {
                    _logCallback = (level, text, userData) =>
                    {
                        try
                        {
                            if (text == IntPtr.Zero) return;
                            string s = Marshal.PtrToStringUTF8(text);
                            if (string.IsNullOrEmpty(s)) return;
                            LogSink?.Invoke(s.TrimEnd('\r', '\n'));
                        }
                        catch { }
                    };
                    LlamaNative.llama_log_set(_logCallback, IntPtr.Zero);
                    LlamaNative.llama_backend_init();

                    // 加载 ggml 后端（CPU 后端在 ggml-cpu-*.dll，需从 Lumina-Engine 目录动态加载；
                    // ggml 会依据 CPUID 自动选择最匹配的 ggml-cpu-<arch>.dll）
                    string llamaDir = LlamaNative.NativeDirectory;
                    try
                    {
                        if (!string.IsNullOrEmpty(llamaDir) && System.IO.Directory.Exists(llamaDir))
                            LlamaNative.ggml_backend_load_all_from_path(Utf8Z(llamaDir));
                        else
                            LlamaNative.ggml_backend_load_all();
                    }
                    catch
                    {
                        try { LlamaNative.ggml_backend_load_all(); } catch { }
                    }

                    _backendInitialized = true;
                }
            }

            byte[] pathUtf8 = Encoding.UTF8.GetBytes(modelPath);
            var mparams = LlamaNative.llama_model_default_params();
            mparams.n_gpu_layers = 0;         // 纯 CPU（与之前的 llama-server 默认一致）
            mparams.use_extra_bufts = 0;

            _model = LlamaNative.llama_model_load_from_file(pathUtf8, mparams);
            if (_model == IntPtr.Zero)
                throw new InvalidOperationException($"加载模型失败: {modelPath}");

            _vocab = LlamaNative.llama_model_get_vocab(_model);
            if (_vocab == IntPtr.Zero)
                throw new InvalidOperationException("获取模型词表失败。");

            var cparams = LlamaNative.llama_context_default_params();
            cparams.n_ctx = (uint)nCtx;
            cparams.n_batch = (uint)_nBatch;
            cparams.n_ubatch = (uint)_nBatch;
            cparams.n_seq_max = 1;
            cparams.n_threads = nThreads > 0 ? nThreads : Environment.ProcessorCount;
            cparams.n_threads_batch = cparams.n_threads;
            cparams.no_perf = 1;

            _ctx = LlamaNative.llama_init_from_model(_model, cparams);
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException($"创建推理上下文失败 (n_ctx={nCtx})。");

            _nCtx = (int)LlamaNative.llama_n_ctx(_ctx);

            _batch = LlamaNative.llama_batch_init(_nBatch, 0, 1);
            _batchAllocated = true;

            // 生成提示是否需要预填空 <think> 块（Qwen3 系模板有此特征）
            ThinkPrefill = false;
            try
            {
                IntPtr tp = LlamaNative.llama_model_chat_template(_model, IntPtr.Zero);
                if (tp != IntPtr.Zero)
                {
                    string tmpl = Marshal.PtrToStringUTF8(tp) ?? "";
                    ThinkPrefill = tmpl.Contains("<think>");
                }
            }
            catch { }
        }

        private static byte[] Utf8Z(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            byte[] z = new byte[b.Length + 1];
            Array.Copy(b, z, b.Length);
            return z;
        }

        // -------------------------------------------------------------------
        // 分词 / 反分词
        // -------------------------------------------------------------------
        public int[] Tokenize(string text, bool addSpecial = false, bool parseSpecial = true)
        {
            if (text == null) text = "";
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int need = LlamaNative.llama_tokenize(_vocab, bytes, bytes.Length, IntPtr.Zero, 0, addSpecial, parseSpecial);
            if (need == 0) return Array.Empty<int>();
            int cap = need < 0 ? -need : need;
            cap = Math.Max(cap + 8, 16);

            IntPtr buf = Marshal.AllocHGlobal(cap * sizeof(int));
            try
            {
                int n = LlamaNative.llama_tokenize(_vocab, bytes, bytes.Length, buf, cap, addSpecial, parseSpecial);
                if (n < 0) throw new InvalidOperationException("分词失败（缓冲区不足）。");
                var tokens = new int[n];
                Marshal.Copy(buf, tokens, 0, n);
                return tokens;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>单 token → 文本片段（增量 UTF-8 解码，正确处理跨 token 的多字节字符）。</summary>
        public string TokenToText(int token)
        {
            int cap = 256;
            IntPtr buf = Marshal.AllocHGlobal(cap);
            try
            {
                int n = LlamaNative.llama_token_to_piece(_vocab, token, buf, cap, 0, false);
                if (n < 0)
                {
                    cap = -n + 8;
                    Marshal.FreeHGlobal(buf);
                    buf = Marshal.AllocHGlobal(cap);
                    n = LlamaNative.llama_token_to_piece(_vocab, token, buf, cap, 0, false);
                    if (n < 0) return "";
                }
                if (n == 0) return "";
                byte[] bytes = new byte[n];
                Marshal.Copy(buf, bytes, 0, n);

                char[] chars = new char[Encoding.UTF8.GetMaxCharCount(n) + 4];
                int cn = _utf8.GetChars(bytes, 0, n, chars, 0, false);
                return cn <= 0 ? "" : new string(chars, 0, cn);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // -------------------------------------------------------------------
        // 解码辅助
        // -------------------------------------------------------------------
        private void Decode(int[] tokens, int offset, int count, int posStart, bool logitsOnLast)
        {
            _batch.n_tokens = count;
            for (int i = 0; i < count; i++)
            {
                Marshal.WriteInt32(_batch.token, i * 4, tokens[offset + i]);
                Marshal.WriteInt32(_batch.pos, i * 4, posStart + i);
                Marshal.WriteInt32(_batch.n_seq_id, i * 4, 1);
                // llama_batch_init 为每个 token 预分配了 seq_id 数组（长度 n_seq_max=1）；
                // 写入 seq_id 值 0，而不是覆盖数组指针，否则 llama_batch_free 会重复 free 同一指针。
                IntPtr seqIds = Marshal.ReadIntPtr(_batch.seq_id, i * IntPtr.Size);
                Marshal.WriteInt32(seqIds, 0);
                Marshal.WriteByte(_batch.logits, i, (byte)((logitsOnLast && i == count - 1) ? 1 : 0));
            }

            int rc = LlamaNative.llama_decode(_ctx, _batch);
            if (rc != 0)
                throw new InvalidOperationException($"llama_decode 失败，返回码 {rc}（上下文可能已满）。");
        }

        private static int CommonPrefix(IReadOnlyList<int> a, int[] b)
        {
            int n = Math.Min(a.Count, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        // -------------------------------------------------------------------
        // 原始补全（带 KV 前缀复用）
        // -------------------------------------------------------------------
        public (string Text, int TokensPredicted) Complete(string prompt, SamplingOptions opt)
            => Complete(Tokenize(prompt), opt);

        public (string Text, int TokensPredicted) Complete(int[] promptTokens, SamplingOptions opt)
        {
            opt ??= new SamplingOptions();
            lock (_lock)
            {
                if (_disposed || _ctx == IntPtr.Zero) throw new ObjectDisposedException(nameof(LlamaEngine));

                int[] prompt = promptTokens;
                bool forceFull = false;

                // 上下文保护：超长则保留头部（系统提示）+ 尾部（最近对话）
                int reserve = Math.Max(8, opt.MaxTokens);
                int maxPrompt = _nCtx - reserve - 4;
                if (maxPrompt < 16) maxPrompt = Math.Max(16, _nCtx / 2);
                if (prompt.Length > maxPrompt)
                {
                    int head = Math.Min(256, Math.Max(16, maxPrompt / 4));
                    int tail = maxPrompt - head;
                    var trimmed = new int[head + tail];
                    Array.Copy(prompt, 0, trimmed, 0, head);
                    Array.Copy(prompt, prompt.Length - tail, trimmed, head, tail);
                    prompt = trimmed;
                    forceFull = true;
                }

                // KV 前缀复用：只解码新增后缀
                IntPtr mem = LlamaNative.llama_get_memory(_ctx);
                int common = forceFull ? 0 : CommonPrefix(_cache, prompt);
                if (common < _cache.Count)
                {
                    // 移除位置 common 之后的 KV（含可能的尾部多余）
                    LlamaNative.llama_memory_seq_rm(mem, 0, common, -1);
                }

                var prefillSw = Stopwatch.StartNew();
                for (int i = common; i < prompt.Length; i += _nBatch)
                {
                    int cnt = Math.Min(_nBatch, prompt.Length - i);
                    Decode(prompt, i, cnt, i, logitsOnLast: true);
                }
                prefillSw.Stop();
                _stats.PromptTokens += prompt.Length - common;
                _stats.PromptMs += prefillSw.Elapsed.TotalMilliseconds;

                _cache.Clear();
                _cache.AddRange(prompt);

                // 采样
                var genSw = Stopwatch.StartNew();
                IntPtr chain = BuildSampler(opt);
                _utf8.Reset();
                var sb = new StringBuilder();
                int produced = 0;
                int pos = prompt.Length;
                try
                {
                    for (int step = 0; step < opt.MaxTokens; step++)
                    {
                        // 重复惩罚：直接修改 logits（见 ApplyRepeatPenalty）
                        ApplyRepeatPenalty(opt);

                        // 注意：llama_sampler_sample 内部已经调用 llama_sampler_accept，
                        // 调用方不得再手动 accept。
                        int tok = LlamaNative.llama_sampler_sample(chain, _ctx, -1);
                        if (LlamaNative.llama_vocab_is_eog(_vocab, tok)) break;

                        string piece = TokenToText(tok);
                        if (piece.Contains("<|im_end|>")) break;

                        sb.Append(piece);
                        produced++;

                        var one = new[] { tok };
                        Decode(one, 0, 1, pos, logitsOnLast: true);
                        _cache.Add(tok);
                        pos++;
                    }
                }
                finally
                {
                    LlamaNative.llama_sampler_free(chain);
                }
                genSw.Stop();
                _stats.GeneratedTokens += produced;
                _stats.GeneratedMs += genSw.Elapsed.TotalMilliseconds;

                return (sb.ToString(), produced);
            }
        }

        // ---- 重复惩罚：直接对最后一个 token 的 logits 施加惩罚 ----
        // 惩罚窗口 = 当前序列（prompt + 已生成）的最后 RepeatLastN 个 token；
        // 公式与 llama.cpp 一致：正 logit 除以惩罚，非正 logit 乘以惩罚（每个 token 只算一次）。
        private void ApplyRepeatPenalty(SamplingOptions opt)
        {
            if (opt.RepeatPenalty <= 1.0f || opt.RepeatLastN <= 0 || _cache.Count == 0) return;

            IntPtr logits = LlamaNative.llama_get_logits_ith(_ctx, -1);
            if (logits == IntPtr.Zero) return;

            int nVocab = VocabSize;
            int n = Math.Min(opt.RepeatLastN, _cache.Count);
            var seen = new HashSet<int>();

            for (int i = _cache.Count - n; i < _cache.Count; i++)
            {
                int tid = _cache[i];
                if (tid < 0 || tid >= nVocab || !seen.Add(tid)) continue;

                IntPtr cell = logits + tid * sizeof(float);
                float v = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(cell));
                v = v <= 0f ? v * opt.RepeatPenalty : v / opt.RepeatPenalty;
                Marshal.WriteInt32(cell, BitConverter.SingleToInt32Bits(v));
            }
        }

        private IntPtr BuildSampler(SamplingOptions opt)
        {
            var cp = LlamaNative.llama_sampler_chain_default_params();
            cp.no_perf = 1;
            IntPtr chain = LlamaNative.llama_sampler_chain_init(cp);

            // 贪心：temperature <= 0 时直接用 greedy
            if (opt.Temperature <= 0.01f)
            {
                LlamaNative.llama_sampler_chain_add(chain, LlamaNative.llama_sampler_init_greedy());
                return chain;
            }

            // 注：不使用 llama.cpp 的 penalties 采样器（在本用法下行为异常），
            // 重复惩罚改由 ApplyRepeatPenalty() 直接修改 logits 完成。
            if (opt.TopK > 0)
                LlamaNative.llama_sampler_chain_add(chain, LlamaNative.llama_sampler_init_top_k(opt.TopK));
            if (opt.TopP > 0f && opt.TopP < 1f)
                LlamaNative.llama_sampler_chain_add(chain, LlamaNative.llama_sampler_init_top_p(opt.TopP, 1));
            if (opt.MinP > 0f)
                LlamaNative.llama_sampler_chain_add(chain, LlamaNative.llama_sampler_init_min_p(opt.MinP, 1));

            LlamaNative.llama_sampler_chain_add(chain, LlamaNative.llama_sampler_init_temp(opt.Temperature));
            LlamaNative.llama_sampler_chain_add(chain, LlamaNative.llama_sampler_init_dist(opt.Seed));
            return chain;
        }

        // -------------------------------------------------------------------
        // 对话（ChatML）+ 工具调用
        // -------------------------------------------------------------------
        private static readonly Regex ToolCallRegex =
            new Regex(@"<tool_call>\s*([\s\S]*?)\s*</tool_call>", RegexOptions.Compiled);
        private static readonly Regex ThinkRegex =
            new Regex(@"^\s*<think>[\s\S]*?</think>\s*", RegexOptions.Compiled);

        public (string Content, List<(string Name, string Arguments)> ToolCalls) Chat(
            IReadOnlyList<ChatTurn> turns, IReadOnlyList<string> tools, int maxTokens, float temperature,
            float topP = 0.95f, float repeatPenalty = 1.0f, int repeatLastN = 64)
        {
            string prompt = FormatChat(turns, tools, ThinkPrefill);
            var opt = new SamplingOptions
            {
                MaxTokens = maxTokens,
                Temperature = temperature,
                TopP = topP,
                RepeatPenalty = repeatPenalty,
                RepeatLastN = repeatLastN
            };

            var (text, _) = Complete(Tokenize(prompt), opt);
            return ParseAssistantOutput(text);
        }

        /// <summary>把模型输出拆分为正文 + 工具调用（Qwen 系 &lt;tool_call&gt;{...}&lt;/tool_call&gt;）。</summary>
        public static (string Content, List<(string Name, string Arguments)> ToolCalls) ParseAssistantOutput(string text)
        {
            var calls = new List<(string Name, string Arguments)>();
            string content = text ?? "";

            // 去掉推理块（若模型输出了 <think>...</think>）
            content = ThinkRegex.Replace(content, "");

            foreach (Match m in ToolCallRegex.Matches(content))
            {
                string body = m.Groups[1].Value.Trim();
                try
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(body);
                    string name = obj["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) name = obj["function"]?["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    var argsTok = obj["arguments"] ?? obj["function"]?["arguments"];
                    string args = argsTok == null
                        ? "{}"
                        : (argsTok.Type == Newtonsoft.Json.Linq.JTokenType.String
                            ? argsTok.ToString()
                            : argsTok.ToString(Newtonsoft.Json.Formatting.None));

                    calls.Add((name, string.IsNullOrWhiteSpace(args) ? "{}" : args));
                }
                catch
                {
                    // 解析失败则保留原样在正文里，交给上层文本解析兜底
                }
            }

            if (calls.Count > 0)
                content = ToolCallRegex.Replace(content, "").Trim();

            return (content.Trim(), calls);
        }

        /// <summary>
        /// 按 Qwen（ChatML）模板拼接对话提示词。
        /// tools 为 OpenAI 格式工具定义的紧凑 JSON 字符串列表；为空则不启用工具。
        /// </summary>
        public static string FormatChat(IReadOnlyList<ChatTurn> msgs, IReadOnlyList<string> tools, bool thinkPrefill)
        {
            var sb = new StringBuilder();
            bool hasFirst = msgs != null && msgs.Count > 0;
            bool toolsMode = tools != null && tools.Count > 0;

            if (toolsMode)
            {
                sb.Append("<|im_start|>system\n");
                if (hasFirst && msgs[0].Role == "system")
                    sb.Append(msgs[0].Content).Append("\n\n");

                sb.Append("# Tools\n\nYou may call one or more functions to assist with the user query.\n\n");
                sb.Append("You are provided with function signatures within <tools></tools> XML tags:\n<tools>");
                foreach (var t in tools) sb.Append('\n').Append(t);
                sb.Append("\n</tools>\n\nFor each function call, return a json object with function name and arguments within <tool_call></tool_call> XML tags:\n");
                sb.Append("<tool_call>\n{\"name\": <function-name>, \"arguments\": <args-json-object>}\n</tool_call><|im_end|>\n");
            }
            else if (hasFirst && msgs[0].Role == "system")
            {
                sb.Append("<|im_start|>system\n").Append(msgs[0].Content).Append("<|im_end|>\n");
            }

            if (msgs != null)
            {
                for (int i = 0; i < msgs.Count; i++)
                {
                    var m = msgs[i];
                    switch (m.Role)
                    {
                        case "system":
                            if (i == 0) continue;   // 已在头部渲染
                            sb.Append("<|im_start|>system\n").Append(m.Content).Append("<|im_end|>\n");
                            break;

                        case "user":
                            sb.Append("<|im_start|>user\n").Append(m.Content).Append("<|im_end|>\n");
                            break;

                        case "assistant":
                            sb.Append("<|im_start|>assistant\n");
                            if (!string.IsNullOrEmpty(m.Content)) sb.Append(m.Content);
                            if (m.ToolCalls != null)
                            {
                                foreach (var tc in m.ToolCalls)
                                {
                                    sb.Append("\n<tool_call>\n{\"name\": \"").Append(tc.Name)
                                      .Append("\", \"arguments\": ").Append(tc.Arguments).Append("}\n</tool_call>");
                                }
                            }
                            sb.Append("<|im_end|>\n");
                            break;

                        case "tool":
                            // 连续的工具结果合并进同一个 user 块
                            bool prevIsTool = i > 0 && msgs[i - 1].Role == "tool";
                            bool nextIsTool = i + 1 < msgs.Count && msgs[i + 1].Role == "tool";
                            if (!prevIsTool) sb.Append("<|im_start|>user");
                            sb.Append("\n<tool_response>\n").Append(m.Content).Append("\n</tool_response>");
                            if (!nextIsTool) sb.Append("<|im_end|>\n");
                            break;
                    }
                }
            }

            sb.Append(thinkPrefill ? "<|im_start|>assistant\n<think>\n\n</think>\n\n" : "<|im_start|>assistant\n");
            return sb.ToString();
        }

        // -------------------------------------------------------------------
        // 释放
        // -------------------------------------------------------------------
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                if (_batchAllocated)
                {
                    try { LlamaNative.llama_batch_free(_batch); } catch { }
                    _batchAllocated = false;
                }
                if (_ctx != IntPtr.Zero)
                {
                    try { LlamaNative.llama_free(_ctx); } catch { }
                    _ctx = IntPtr.Zero;
                }
                if (_model != IntPtr.Zero)
                {
                    try { LlamaNative.llama_model_free(_model); } catch { }
                    _model = IntPtr.Zero;
                }
                _vocab = IntPtr.Zero;
                _cache.Clear();
            }
        }
    }
}
