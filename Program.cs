using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ModelContextProtocol.Client;          // +MCP
using ModelContextProtocol.Protocol;        // +MCP

namespace LlamaChat
{
    // ===================================================================
    // 控制台颜色辅助类（保持不变）
    // ===================================================================
    public static class ConsoleHelper
    {
        private const string ColorBlue = "\x1b[38;2;0;120;255m";
        private const string ColorGreen = "\x1b[38;2;0;200;80m";
        private const string ColorYellow = "\x1b[38;2;255;180;0m";
        private const string ColorRed = "\x1b[38;2;255;50;50m";
        private const string ColorGray = "\x1b[38;2;128;128;128m";
        private const string ColorWhite = "\x1b[38;2;255;255;255m";
        private const string Reset = "\x1b[0m";

        public static void WriteLine(string message) => Console.WriteLine(message);
        public static void Write(string message) => Console.Write(message);

        public static void Info(string message)
        {
            Console.Write(ColorGray);
            Console.WriteLine(message);
            Console.Write(Reset);
        }

        public static void Prompt(string message)
        {
            Console.Write(ColorBlue);
            Console.WriteLine(message);
            Console.Write(Reset);
        }

        public static void Success(string message)
        {
            Console.Write(ColorGreen);
            Console.WriteLine(message);
            Console.Write(Reset);
        }

        public static void Warning(string message)
        {
            Console.Write(ColorYellow);
            Console.WriteLine(message);
            Console.Write(Reset);
        }

        public static void Error(string message)
        {
            Console.Write(ColorRed);
            Console.WriteLine(message);
            Console.Write(Reset);
        }

        public static void UserContent(string message)
        {
            Console.Write(ColorWhite);
            Console.WriteLine(message);
            Console.Write(Reset);
        }

        public static void UserContentNoNewLine(string message)
        {
            Console.Write(ColorWhite);
            Console.Write(message);
            Console.Write(Reset);
        }
    }

    // ===================================================================
    // 可调参数区域（扩展模型配置）
    // ===================================================================
    public static class AppConfig
    {
        public const int ManualContextSize = 0;
        public const int MaxResponseTokens = 1024;
        public const int ReserveTokens = 200;
        public const double CharPerToken = 4.0;

        public const bool EnableSemanticCache = true;
        public const double SimilarityThreshold = 0.85;
        public const int MaxCacheEntries = 100;

        public const int HistoryRetrievalTopK = 5;

        // 相关判断配置
        public const int RelevanceCheckRounds = 5;
        public const double RelevanceThreshold = 0.3;

        public const string LlamaFolderName = "llama";

        // 模型文件名映射
        public static readonly IReadOnlyDictionary<ModelMode, string> ModelFiles = new Dictionary<ModelMode, string>
        {
            { ModelMode.Fast, "Bonsai-1.7B.gguf" },
            { ModelMode.Balanced, "Bonsai-4B.gguf" },
            { ModelMode.Quality, "Bonsai-8B.gguf" }
        };

        // 端口映射
        public static readonly IReadOnlyDictionary<ModelMode, int> ModelPorts = new Dictionary<ModelMode, int>
        {
            { ModelMode.Fast, 38082 },
            { ModelMode.Balanced, 38081 },
            { ModelMode.Quality, 38080 }
        };

        // 默认模式
        public const ModelMode DefaultMode = ModelMode.Balanced;

        // +MCP: MCP 服务器配置
        public const string McpFolderName = "mcp";
        public const string McpExeName = "WindowsMcp.exe";

        // 语言风格转换（Miya）配置：独立 llama-server（Qwen2.5-0.5B），端口 38090
        public const int StyleTransferPort = 38090;
        public const string StyleTransferModel = "Qwen2.5-0.5B-Q4_K_M.gguf";
        public const int StyleTransferContextSize = 8192;
    }

    // ===================================================================
    // 核心服务类（整合 MCP，支持模型切换）
    // ===================================================================
    public class LlamaChatService : IDisposable, IAsyncDisposable
    {
        // 对话角色（决定回答是否做 Miya 语言风格转换）
        public enum ChatRole
        {
            Ewin,          // 直接输出，不转换
            MiyaBonsai     // 用风格转换模型转换
        }

        private Process _serverProcess;
        private readonly HttpClient _httpClient;
        private readonly int _contextSize;
        private readonly ConversationContext _context;
        private readonly SemanticCache _cache;
        private readonly HistoryRetriever _retriever;
        private bool _isDisposed;

        // 当前模式
        private ModelMode _currentMode;

        // 可调配置（语言风格转换相关除外）
        private readonly LuminaOptions _options;

        // 语言风格转换服务（Miya）：懒加载，仅在需要转换时创建并启动服务器
        private StyleTransferService? _styleTransfer;

        // 自定义系统提示词（null = 使用默认）
        private string? _customSystemPrompt;
        private string? _customControlSystemPrompt;

        // 角色身份模板服务（问候/身份/自我介绍/个人偏好，不走 AI）
        private readonly CharacterIdentityService _identity = new();

        // 当前选择的角色（上下文不记录角色，仅用于决定是否风格转换）
        public ChatRole SelectedRole { get; set; } = ChatRole.MiyaBonsai;

        // 完成一轮对话后触发：(用户输入, 回答)
        public event Action<string, string>? AnswerReceived;

        // +MCP: 相关字段
        private McpClient _mcpClient;
        private List<McpClientTool> _mcpTools;
        private bool _isMcpReady = false;
        private readonly HashSet<string> _dangerousTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "file_write", "file_manage", "process", "service",
            "scheduled_task", "registry_set", "power_action",
            "firewall", "env"
        };

        public JArray CurrentHistory => _context?.GetMessagesForRequest();

        // 构造函数：仅初始化内存态，不启动任何进程（需调用 InitializeAsync）
        public LlamaChatService(LuminaOptions? options = null)
        {
            _options = options ?? new LuminaOptions();
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            _currentMode = _options.InitialMode;

            // 计算上下文大小（与模型无关）
            if (_options.ManualContextSize.HasValue && _options.ManualContextSize.Value > 0)
                _contextSize = _options.ManualContextSize.Value;
            else
            {
                double avPhysGB = GetAvailablePhysicalMemoryGB();
                const double modelOverheadGB = 4.1;
                double maxAllocGB = Math.Min(24, avPhysGB * 0.85);
                double kvCacheBudgetGB = Math.Max(0, maxAllocGB - modelOverheadGB);
                _contextSize = (int)(kvCacheBudgetGB * 1024 / 0.286);
                _contextSize = Math.Min(Math.Max(_contextSize, 10240), 32768);
                Log(LogLevel.Info, $"系统可用内存 {avPhysGB:F2} GB → 自动计算上下文: {_contextSize}");
            }

            _context = new ConversationContext(
                maxContextTokens: _contextSize,
                maxResponseTokens: _options.MaxResponseTokens,
                reserveTokens: _options.ReserveTokens,
                charPerToken: _options.CharPerToken
            );
            _cache = new SemanticCache(
                similarityThreshold: _options.SimilarityThreshold,
                maxEntries: _options.MaxCacheEntries
            );
            _retriever = new HistoryRetriever(topK: _options.HistoryRetrievalTopK);
        }

        // 兼容旧签名（向后兼容）
        public LlamaChatService(ModelMode initialMode, int? manualContextSize = null)
            : this(new LuminaOptions { InitialMode = initialMode, ManualContextSize = manualContextSize })
        {
        }

        // ---- 启动 llama-server 与 MCP（库模式建议显式调用） ----
        public async Task InitializeAsync()
        {
            StartServerForMode(_currentMode);
            await InitializeMcpAsync();
        }

        // ---- 日志（经 LogCallback 输出，UI 无关；未设置回调时不输出） ----
        private void Log(LogLevel level, string message)
        {
            _options.LogCallback?.Invoke(level, message);
        }

        // ---- 用户确认（经 ConfirmCallback，UI 无关；未设置回调时默认拒绝，安全） ----
        private Task<bool> ConfirmAsync(string prompt)
        {
            return _options.ConfirmCallback == null
                ? Task.FromResult(false)
                : _options.ConfirmCallback(prompt);
        }

        // ---- 将一段文本转换为 Miya 风格（自动检测语言；服务器懒加载） ----
        public async Task<string> ConvertStyleAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            _styleTransfer ??= new StyleTransferService();
            return await _styleTransfer.ConvertMarkdownAsync(text);
        }

        // ---- 设置自定义系统提示词（传 null 恢复内置默认） ----
        public void SetSystemPrompt(string? systemPrompt, string? controlSystemPrompt = null)
        {
            _customSystemPrompt = systemPrompt;
            _customControlSystemPrompt = controlSystemPrompt;
        }

        // ---- 从外部导入单条上下文消息（同时写入检索索引） ----
        public void AddContextMessage(string role, string content)
        {
            if (string.IsNullOrEmpty(role) || content == null) return;
            _context.AddMessage(role, content);
            _retriever.AddMessage(role, content);
        }

        // ---- 发送消息（按 SelectedRole 自动决定是否做 Miya 风格转换） ----
        public async Task<string> SendMessageAsync(string userInput)
        {
            var post = SelectedRole == ChatRole.MiyaBonsai
                ? (Func<string, Task<string>>)(text => ConvertStyleAsync(text))
                : null;
            return await SendMessageAsync(userInput, post);
        }

        // ---- 发送消息（显式指定角色；同时更新 SelectedRole） ----
        public async Task<string> SendMessageAsync(string userInput, ChatRole role)
        {
            SelectedRole = role;
            return await SendMessageAsync(userInput);
        }

        // ---- 尝试用角色模板直接回复；命中时自动记录上下文并触发 AnswerReceived ----
        public string? GetTemplateReply(string userInput, ChatRole role)
        {
            string? reply = TryGetTemplateReply(userInput, role);
            if (reply != null)
                RecordTemplateReply(userInput, reply);
            return reply;
        }

        // ---- 角色 -> 模板角色名（埃文/米娅） ----
        private static string RoleToCharacterName(ChatRole role)
            => role == ChatRole.Ewin ? "埃文" : "米娅";

        // ---- 尝试用角色模板直接回复（问候/身份询问/自我介绍/个人偏好）—— 不走任何 AI 路径 ----
        // 匹配时返回模板回答；不匹配返回 null（继续走 AI）
        public string TryGetTemplateReply(string userInput, ChatRole role)
        {
            if (string.IsNullOrWhiteSpace(userInput)) return null;

            string name = RoleToCharacterName(role);
            if (_identity.IsGreeting(userInput))
                return _identity.HandleGreeting(name);
            if (_identity.IsIdentityQuestion(userInput))
                return _identity.HandleIdentityQuestion(name, userInput);
            if (_identity.IsSelfIntroduction(userInput))
                return _identity.HandleSelfIntroduction(name);
            if (_identity.IsPersonalInfoQuestion(userInput))
                return _identity.HandlePersonalQuestion(name);
            return null;
        }

        // ---- 记录模板回复到上下文（符合现有上下文保存规则：user/assistant 消息；不进语义缓存，保持模板随机性） ----
        public void RecordTemplateReply(string userInput, string reply)
        {
            _context.AddMessage("user", userInput);
            _context.AddMessage("assistant", reply);
            AnswerReceived?.Invoke(userInput, reply);
        }

        // ---- 模型文件 / 端口解析（优先 options，缺省回退 AppConfig 默认） ----
        private string GetModelFile(ModelMode mode)
            => _options.ModelFiles != null && _options.ModelFiles.TryGetValue(mode, out var f) ? f : AppConfig.ModelFiles[mode];

        private int GetModelPort(ModelMode mode)
            => _options.ModelPorts != null && _options.ModelPorts.TryGetValue(mode, out var p) ? p : AppConfig.ModelPorts[mode];

        // ---- 根据模式启动服务器 ----
        private void StartServerForMode(ModelMode mode)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string llamaDir = Path.Combine(baseDir, _options.LlamaFolderName);
            string serverExe = Path.Combine(llamaDir, "llama-server.exe");
            string modelFile = GetModelFile(mode);
            string modelPath = Path.Combine(llamaDir, modelFile);
            int port = GetModelPort(mode);

            if (!File.Exists(serverExe))
            {
                Log(LogLevel.Error, $"未找到 llama-server.exe: {serverExe}");
                throw new FileNotFoundException($"未找到 {serverExe}");
            }
            if (!File.Exists(modelPath))
            {
                Log(LogLevel.Error, $"未找到模型文件: {modelPath}");
                throw new FileNotFoundException($"未找到模型文件 {modelPath}");
            }

            // 先确保没有残留的 llama-server 进程（避免端口冲突）
            KillAllLlamaServers();

            int threads = Environment.ProcessorCount;
            string args = $"-m \"{modelPath}\" --host 127.0.0.1 --port {port} -c {_contextSize} -t {threads}";

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
            _serverProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.WriteLine($"[llama] {e.Data}"); };
            _serverProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.WriteLine($"[llama-err] {e.Data}"); };
            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            Log(LogLevel.Prompt, $"正在启动 llama-server (模式: {mode}, 端口: {port})...");
            bool ready = WaitForServerAsync(port).GetAwaiter().GetResult();
            if (!ready)
            {
                Log(LogLevel.Error, $"llama-server (端口 {port}) 启动超时。");
                throw new TimeoutException($"llama-server 启动超时 (端口 {port})。");
            }
            Log(LogLevel.Success, $"llama-server 已就绪！(模式: {mode}, 端口: {port})");
        }

        // 等待服务器就绪（指定端口）
        private async Task<bool> WaitForServerAsync(int port, int maxSeconds = 6000)
        {
            string url = $"http://127.0.0.1:{port}/health";
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

        // ---- 停止当前服务器 ----
        private void StopCurrentServer()
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
            // 额外杀所有残留进程（确保端口释放）
            KillAllLlamaServers();
        }

        // ---- 杀死所有 llama-server 进程 ----
        private void KillAllLlamaServers()
        {
            var processes = Process.GetProcessesByName("llama-server");
            if (processes.Length == 0) return;
            Log(LogLevel.Warning, $"发现 {processes.Length} 个残留 llama-server 进程，正在终止...");
            foreach (var p in processes)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                    p.Dispose();
                }
                catch { }
            }
            Log(LogLevel.Success, "已清理所有 llama-server 进程。");
        }

        // ---- 切换模型模式（异步） ----
        public async Task SwitchModeAsync(ModelMode newMode)
        {
            if (newMode == _currentMode)
            {
                Log(LogLevel.Info, $"当前已经是 {newMode} 模式，无需切换。");
                return;
            }

            Log(LogLevel.Prompt, $"正在从 {_currentMode} 切换至 {newMode} ...");
            StopCurrentServer();        // 杀死当前及残留进程
            _currentMode = newMode;
            StartServerForMode(newMode); // 启动新服务器
            await Task.CompletedTask;    // 因为启动是同步的，但为了接口异步，留空
        }

        // ---- 获取当前模式 ----
        public ModelMode CurrentMode => _currentMode;

        // ---- 获取端口 ----
        public int CurrentPort => GetModelPort(_currentMode);

        // +MCP: 初始化 MCP 客户端
        private async Task InitializeMcpAsync()
        {
            try
            {
                string mcpExe = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    _options.McpFolderName,
                    _options.McpExeName
                );
                if (!File.Exists(mcpExe))
                {
                    Log(LogLevel.Error, $"未找到 WindowsMcp.exe: {mcpExe}");
                    _isMcpReady = false;
                    return;
                }

                var options = new StdioClientTransportOptions
                {
                    Command = mcpExe,
                    Arguments = Array.Empty<string>()
                };
                var transport = new StdioClientTransport(options);
                _mcpClient = await McpClient.CreateAsync(transport);
                var toolsResult = await _mcpClient.ListToolsAsync();
                _mcpTools = toolsResult.ToList();
                _isMcpReady = true;
                Log(LogLevel.Success, $"MCP 已就绪，加载了 {_mcpTools.Count} 个工具");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"MCP 初始化失败: {ex.Message}");
                _isMcpReady = false;
            }
        }

        // +MCP: 将 MCP 工具转换为 OpenAI 格式的 tools 数组
        private JArray BuildToolsJson()
        {
            var toolsArray = new JArray();
            foreach (var tool in _mcpTools)
            {
                var toolObj = new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description ?? "",
                        ["parameters"] = JObject.Parse(tool.JsonSchema.GetRawText())
                    }
                };
                toolsArray.Add(toolObj);
            }
            return toolsArray;
        }

        // +MCP: 封装 HTTP 调用（根据当前端口）
        private async Task<JObject> PostChatCompletionAsync(JObject requestBody)
        {
            int port = CurrentPort;
            string apiUrl = $"http://127.0.0.1:{port}/v1/chat/completions";
            var json = requestBody.ToString(Formatting.None);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(apiUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"HTTP {response.StatusCode}: {err}");
            }
            string jsonResponse = await response.Content.ReadAsStringAsync();
            return JObject.Parse(jsonResponse);
        }

        // ---- 导入历史 ----
        public void ImportHistory(JArray history)
        {
            if (history == null) throw new ArgumentNullException(nameof(history));
            int count = 0;
            foreach (var item in history)
            {
                string role = item["role"]?.ToString();
                string msgContent = item["content"]?.ToString();
                if (!string.IsNullOrEmpty(role) && msgContent != null)
                {
                    _context.AddMessage(role, msgContent);
                    _retriever.AddMessage(role, msgContent);
                    count++;
                }
            }
            Log(LogLevel.Success, $"已导入 {count} 条历史消息。");
        }

        public void ImportHistoryFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            if (!File.Exists(filePath))
            {
                Log(LogLevel.Warning, $"历史文件不存在: {filePath}");
                throw new FileNotFoundException("历史文件不存在", filePath);
            }
            string json = File.ReadAllText(filePath);
            JArray history = JArray.Parse(json);
            ImportHistory(history);
        }

        public void ClearHistory()
        {
            _context.Clear();
            _retriever.Clear();
            Log(LogLevel.Prompt, "对话历史已清除（包含检索索引）。");
        }

        public string GetCacheStats() => _cache.GetStats();

        // ---- 核心方法：发送消息（postProcess：可选的后处理钩子，用于 Miya 风格转换；上下文筛选/提示词建构逻辑不变） ----
        public async Task<string> SendMessageAsync(string userInput, Func<string, Task<string>>? postProcess = null)
        {
            if (string.IsNullOrEmpty(userInput)) return string.Empty;

            // 1. 语义缓存
            if (_options.EnableSemanticCache)
            {
                string cached = _cache.GetCachedAnswer(userInput);
                if (cached != null)
                {
                    Log(LogLevel.Info, "⚡ 缓存命中，直接返回。");
                    _context.AddMessage("user", userInput);
                    _context.AddMessage("assistant", cached);
                    _retriever.AddMessage("user", userInput);
                    _retriever.AddMessage("assistant", cached);
                    AnswerReceived?.Invoke(userInput, cached);
                    return cached;
                }
            }

            // 2. 获取滑动窗口消息
            var windowMessages = _context.GetMessagesForRequest();
            var windowContents = new HashSet<string>(
                windowMessages.Select(m => m["content"]?.ToString() ?? ""),
                StringComparer.OrdinalIgnoreCase
            );

            // 3. 检索相关历史（始终执行）
            var relevantHistories = _retriever.Retrieve(userInput)
                .Where(h => !windowContents.Contains(h.Content))
                .ToList();

            if (relevantHistories.Any())
                Log(LogLevel.Info, $"检索到 {relevantHistories.Count} 条相关历史记录（已排除最近对话）。");
            else
                Log(LogLevel.Info, "未检索到相关历史记录。");

            // 用户交互：是否允许操控（经 ConfirmCallback，默认拒绝）
            bool allowControl = false;
            if (_isMcpReady)
            {
                allowControl = await ConfirmAsync("是否允许 AI 操控电脑？");
                Log(LogLevel.Info, allowControl ? "已允许 AI 操控电脑。" : "已禁止 AI 操控电脑，本次只进行普通对话。");
            }
            else
            {
                Log(LogLevel.Info, "MCP 未就绪，无法操控电脑。");
            }

            // 4. 判断是否与最近 N 轮相关（决定是否携带滑动窗口）
            bool useWindowContext = true;
            int checkRounds = _options.RelevanceCheckRounds;
            if (checkRounds > 0)
            {
                var lastUserMessages = _context.GetLastUserMessages(checkRounds);
                if (lastUserMessages.Any())
                {
                    double maxSim = lastUserMessages.Max(prev => SemanticCache.ComputeSimilarity(prev, userInput));
                    if (maxSim < _options.RelevanceThreshold)
                    {
                        useWindowContext = false;
                        Log(LogLevel.Info, $"与最近 {checkRounds} 轮不相关（最大相似度 {maxSim:F2} < {_options.RelevanceThreshold}），将不带最近对话上下文，但保留检索到的背景。");
                    }
                    else
                    {
                        Log(LogLevel.Info, $"与最近对话相关（最大相似度 {maxSim:F2} >= {_options.RelevanceThreshold}），将携带滑动窗口。");
                    }
                }
                else
                {
                    useWindowContext = false;
                    Log(LogLevel.Info, "尚无历史消息，将不带最近对话上下文。");
                }
            }

            // 5. 构建消息列表
            var messages = new JArray();

            // 5.1 系统提示（支持宿主通过 SetSystemPrompt / LuminaOptions 自定义）
            string systemPrompt = allowControl
                ? (_customControlSystemPrompt ?? _options.ControlSystemPrompt ?? @"你是一个能操控 Windows 的 AI 助手。如果用户想操控电脑（如打开程序、移动鼠标、读写文件、上网购物等），你必须使用提供的工具来完成，不要用文字描述如何操作。如果用户只是普通聊天或提问，则直接回答。")
                : (_customSystemPrompt ?? _options.SystemPrompt ?? @"你是一个叫做Lumina的AI助手，你的职责是与用户进行自然语言对话。");
            messages.Add(new JObject { ["role"] = "system", ["content"] = systemPrompt });

            // 5.2 检索到的相关历史（始终添加）
            if (relevantHistories.Any())
            {
                var sb = new StringBuilder();
                sb.AppendLine("以下是用户之前提到过的相关信息，请参考这些内容来回答当前问题：");
                foreach (var (role, histContent) in relevantHistories)
                    sb.AppendLine($"- {role}: {histContent}");
                messages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = sb.ToString()
                });
            }

            // 5.3 滑动窗口（仅当相关）
            if (useWindowContext)
            {
                foreach (var msg in windowMessages)
                    messages.Add(msg);
            }

            // 5.4 当前用户输入
            messages.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = userInput
            });

            // 6. 工具调用循环
            int maxIterations = _options.MaxToolCallIterations;
            int operationCount = 0;
            bool isFirstToolCall = true;
            bool isControlMode = false;
            bool userCancelled = false;

            while (maxIterations-- > 0)
            {
                var requestBody = new JObject
                {
                    ["messages"] = messages,
                    ["temperature"] = 0.7,
                    ["max_tokens"] = _options.MaxResponseTokens,
                    ["stream"] = false
                };

                if (allowControl && _isMcpReady)
                {
                    requestBody["tools"] = BuildToolsJson();
                }

                Log(LogLevel.Info, "========== 请求（发送给模型） ==========");
                Log(LogLevel.Info, requestBody.ToString(Formatting.Indented));
                Log(LogLevel.Info, "==========================================");

                JObject response;
                try
                {
                    response = await PostChatCompletionAsync(requestBody);
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, $"调用模型失败: {ex.Message}");
                    string errMsg = $"错误: {ex.Message}";
                    AnswerReceived?.Invoke(userInput, errMsg);
                    return errMsg;
                }

                var choice = response["choices"]?[0];
                if (choice == null)
                {
                    Log(LogLevel.Error, "模型返回异常：无 choices");
                    string errMsg = "模型返回异常。";
                    AnswerReceived?.Invoke(userInput, errMsg);
                    return errMsg;
                }
                var message = choice["message"];
                var content = message["content"]?.ToString() ?? "";
                var toolCalls = message["tool_calls"] as JArray;

                Log(LogLevel.Info, "========== 响应（来自模型） ==========");
                Log(LogLevel.Info, response.ToString(Formatting.Indented));
                Log(LogLevel.Info, "=======================================");

                if (toolCalls != null && toolCalls.Count > 0)
                {
                    if (!allowControl)
                    {
                        Log(LogLevel.Warning, "模型请求了工具调用，但用户未允许操控。已忽略工具调用，请重新输入。");
                        string deniedMsg = "AI 尝试使用工具但被拒绝。";
                        AnswerReceived?.Invoke(userInput, deniedMsg);
                        return deniedMsg;
                    }

                    isControlMode = true;

                    if (isFirstToolCall)
                    {
                        bool ok = await ConfirmAsync("即将执行电脑操控操作，是否继续？");
                        if (!ok)
                        {
                            Log(LogLevel.Info, "用户取消了操作。");
                            userCancelled = true;
                            string cancelMsg = "用户取消了操作。";
                            _context.AddMessage("user", userInput);
                            _context.AddMessage("assistant", cancelMsg);
                            AnswerReceived?.Invoke(userInput, cancelMsg);
                            return cancelMsg;
                        }
                        isFirstToolCall = false;
                    }

                    messages.Add(message);

                    var toolResultMessages = new JArray();
                    foreach (var tc in toolCalls)
                    {
                        var toolName = tc["function"]["name"].ToString();
                        var argsJson = tc["function"]["arguments"].ToString();
                        var args = JObject.Parse(argsJson);

                        if (_dangerousTools.Contains(toolName))
                        {
                            bool ok = await ConfirmAsync($"即将执行危险操作：{toolName}，参数：{argsJson}，是否继续？");
                            if (!ok)
                            {
                                var failResult = new JObject
                                {
                                    ["role"] = "tool",
                                    ["tool_call_id"] = tc["id"].ToString(),
                                    ["content"] = $"用户取消了危险操作 {toolName}"
                                };
                                toolResultMessages.Add(failResult);
                                continue;
                            }
                        }

                        Log(LogLevel.Success, $"正在执行：{toolName}，参数：{argsJson}");

                        try
                        {
                            var argsDict = args.ToObject<Dictionary<string, object?>>();
                            var toolResult = await _mcpClient.CallToolAsync(toolName, argsDict);
                            string resultContent;
                            if (toolResult.Content is JArray arr && arr.Count > 0)
                            {
                                var texts = arr.Select(c => c["text"]?.ToString()).Where(s => !string.IsNullOrEmpty(s));
                                resultContent = string.Join("\n", texts);
                            }
                            else
                            {
                                resultContent = toolResult.Content?.ToString() ?? "执行成功";
                            }
                            Log(LogLevel.Info, $"执行结果：{resultContent}");
                            operationCount++;

                            toolResultMessages.Add(new JObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = tc["id"].ToString(),
                                ["content"] = resultContent
                            });
                        }
                        catch (Exception ex)
                        {
                            Log(LogLevel.Error, $"执行 {toolName} 失败：{ex.Message}");
                            toolResultMessages.Add(new JObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = tc["id"].ToString(),
                                ["content"] = $"错误：{ex.Message}"
                            });
                        }
                    }

                    foreach (var tm in toolResultMessages)
                        messages.Add(tm);

                    continue;
                }

                // 没有工具调用
                if (isControlMode)
                {
                    string finalMsg;
                    if (userCancelled)
                        finalMsg = "操作已取消。";
                    else if (operationCount > 0)
                        finalMsg = $"已执行 {operationCount} 个操作，任务完成。";
                    else
                        finalMsg = "未执行任何操作，任务可能失败。";
                    _context.AddMessage("user", userInput);
                    _context.AddMessage("assistant", finalMsg);
                    AnswerReceived?.Invoke(userInput, finalMsg);
                    return finalMsg;
                }
                else
                {
                    // 应用后处理（Miya 风格转换），上下文记录最终输出（角色本身不记录）
                    string final = content;
                    if (postProcess != null) final = await postProcess(content);
                    _context.AddMessage("user", userInput);
                    _context.AddMessage("assistant", final);
                    if (_options.EnableSemanticCache)
                        _cache.AddEntry(userInput, final);
                    AnswerReceived?.Invoke(userInput, final);
                    return final;
                }
            }

            throw new Exception("工具调用循环超过最大次数，可能陷入死循环。");
        }

        // ---- 释放资源 ----
        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            StopCurrentServer();
            _httpClient?.Dispose();
            _styleTransfer?.Dispose();
            if (_mcpClient != null)
            {
                // MCP 客户端释放可能因子进程不响应而挂起，加 5 秒超时保护
                var disposeTask = _mcpClient.DisposeAsync().AsTask();
                var completed = await Task.WhenAny(disposeTask, Task.Delay(5000));
                if (completed != disposeTask)
                {
                    Log(LogLevel.Warning, "MCP 客户端释放超时（子进程可能未响应），已跳过。");
                    // 若 disposeTask 仍在运行（后台），等待一小段时间后强制终止进程，避免退出挂起
                    var finish = await Task.WhenAny(disposeTask, Task.Delay(3000));
                    Log(LogLevel.Warning, $"MCP 释放任务状态: {(disposeTask.IsCompleted ? "完成" : disposeTask.Status)} ");
                    if (finish != disposeTask)
                    {
                        // 库模式下不再强制 Environment.Exit（会杀死宿主进程），仅记录并跳过
                        Log(LogLevel.Warning, "MCP 释放任务仍挂起，跳过等待。");
                    }
                }
            }
            Log(LogLevel.Warning, "DisposeAsync 完成");
        }

        // ---- Windows 内存查询 ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() => dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        private static double GetAvailablePhysicalMemoryGB()
        {
            var memStatus = new MEMORYSTATUSEX();
            return GlobalMemoryStatusEx(memStatus) ? memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0) : 0;
        }
    }

    // ===================================================================
    // 滑动窗口上下文管理器（扩展 GetLastUserMessages）
    // ===================================================================
    public class ConversationContext
    {
        private readonly List<JObject> _messages = new();
        private readonly int _maxContextTokens;
        private readonly int _maxResponseTokens;
        private readonly int _reserveTokens;
        private readonly double _charPerToken;

        public ConversationContext(int maxContextTokens, int maxResponseTokens, int reserveTokens, double charPerToken)
        {
            _maxContextTokens = maxContextTokens;
            _maxResponseTokens = maxResponseTokens;
            _reserveTokens = reserveTokens;
            _charPerToken = charPerToken;
        }

        public void AddMessage(string role, string content)
        {
            _messages.Add(new JObject { ["role"] = role, ["content"] = content });
            TruncateIfNeeded();
        }

        public JArray GetMessagesForRequest()
        {
            TruncateIfNeeded();
            var arr = new JArray();
            foreach (var msg in _messages) arr.Add(msg);
            return arr;
        }

        public List<string> GetLastUserMessages(int count)
        {
            var result = new List<string>();
            for (int i = _messages.Count - 1; i >= 0 && result.Count < count; i--)
            {
                if (_messages[i]["role"]?.ToString() == "user")
                {
                    string content = _messages[i]["content"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(content))
                        result.Add(content);
                }
            }
            result.Reverse();
            return result;
        }

        public void Clear() => _messages.Clear();

        private int EstimateTokens()
        {
            long total = 0;
            foreach (var msg in _messages)
                total += (msg["content"]?.ToString() ?? "").Length;
            return (int)(total / _charPerToken);
        }

        private void TruncateIfNeeded()
        {
            int maxAllowed = _maxContextTokens - _maxResponseTokens - _reserveTokens;
            if (maxAllowed <= 0) return;

            int systemIdx = -1;
            for (int i = 0; i < _messages.Count; i++)
            {
                if (_messages[i]["role"]?.ToString() == "system")
                {
                    systemIdx = i;
                    break;
                }
            }

            while (EstimateTokens() > maxAllowed && _messages.Count > 0)
            {
                int removeIdx = (systemIdx == 0 && _messages.Count > 1) ? 1 : 0;
                if (removeIdx >= _messages.Count) break;
                _messages.RemoveAt(removeIdx);
                if (systemIdx > removeIdx) systemIdx--;
            }
        }
    }

    // ===================================================================
    // 语义缓存（包含静态相似度方法）
    // ===================================================================
    public class SemanticCache
    {
        private readonly List<CacheEntry> _entries = new();
        private readonly double _threshold;
        private readonly int _maxEntries;

        private class CacheEntry
        {
            public string Question { get; }
            public string Answer { get; }
            public HashSet<string> TrigramSet { get; }
            public int Length { get; }

            public CacheEntry(string q, string a)
            {
                Question = q;
                Answer = a;
                Length = q.Length;
                TrigramSet = GenerateTrigramSet(q);
            }

            private static HashSet<string> GenerateTrigramSet(string text)
            {
                var set = new HashSet<string>();
                string cleaned = text.ToLowerInvariant();
                if (cleaned.Length < 3)
                {
                    set.Add(cleaned);
                    return set;
                }
                for (int i = 0; i <= cleaned.Length - 3; i++)
                    set.Add(cleaned.Substring(i, 3));
                return set;
            }

            public double Similarity(HashSet<string> queryTrigramSet, int queryLength)
            {
                int minLen = Math.Min(Length, queryLength);
                int maxLen = Math.Max(Length, queryLength);
                if (minLen > 0 && (double)minLen / maxLen < 0.5)
                    return 0.0;

                if (TrigramSet.Count == 0 && queryTrigramSet.Count == 0)
                    return Question.Equals(Question, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
                if (TrigramSet.Count == 0 || queryTrigramSet.Count == 0)
                    return 0.0;

                var intersect = new HashSet<string>(TrigramSet);
                intersect.IntersectWith(queryTrigramSet);
                var union = new HashSet<string>(TrigramSet);
                union.UnionWith(queryTrigramSet);
                return (double)intersect.Count / union.Count;
            }
        }

        public SemanticCache(double similarityThreshold = 0.85, int maxEntries = 100)
        {
            _threshold = similarityThreshold;
            _maxEntries = maxEntries;
        }

        public void AddEntry(string question, string answer)
        {
            if (_entries.Count >= _maxEntries)
                _entries.RemoveAt(0);
            _entries.Add(new CacheEntry(question, answer));
        }

        public string GetCachedAnswer(string question)
        {
            if (string.IsNullOrEmpty(question)) return null;
            var queryTrigramSet = GenerateTrigramSet(question);
            int queryLength = question.Length;

            foreach (var entry in _entries)
            {
                if (entry.Similarity(queryTrigramSet, queryLength) >= _threshold)
                    return entry.Answer;
            }
            return null;
        }

        private static HashSet<string> GenerateTrigramSet(string text)
        {
            var set = new HashSet<string>();
            string cleaned = text.ToLowerInvariant();
            if (cleaned.Length < 3)
            {
                set.Add(cleaned);
                return set;
            }
            for (int i = 0; i <= cleaned.Length - 3; i++)
                set.Add(cleaned.Substring(i, 3));
            return set;
        }

        public static double ComputeSimilarity(string text1, string text2)
        {
            var set1 = GenerateTrigramSetStatic(text1);
            var set2 = GenerateTrigramSetStatic(text2);
            if (set1.Count == 0 && set2.Count == 0)
                return text1.Equals(text2, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            if (set1.Count == 0 || set2.Count == 0)
                return 0.0;
            var intersect = new HashSet<string>(set1);
            intersect.IntersectWith(set2);
            var union = new HashSet<string>(set1);
            union.UnionWith(set2);
            return (double)intersect.Count / union.Count;
        }

        private static HashSet<string> GenerateTrigramSetStatic(string text)
        {
            var set = new HashSet<string>();
            string cleaned = text.ToLowerInvariant();
            if (cleaned.Length < 3)
            {
                set.Add(cleaned);
                return set;
            }
            for (int i = 0; i <= cleaned.Length - 3; i++)
                set.Add(cleaned.Substring(i, 3));
            return set;
        }

        public string GetStats() => $"缓存条目数: {_entries.Count}，相似度阈值: {_threshold:P0}";
    }

    // ===================================================================
    // 历史检索器（保持不变）
    // ===================================================================
    public class HistoryRetriever
    {
        private readonly List<HistoryItem> _items = new();
        private readonly Dictionary<string, HashSet<int>> _invertedIndex = new();
        private int _totalDocs = 0;
        private readonly int _topK;
        private double _avgFieldLength = 20;

        private class HistoryItem
        {
            public int Id { get; set; }
            public string Role { get; set; }
            public string Content { get; set; }
            public int Length => Content?.Length ?? 0;
        }

        public HistoryRetriever(int topK = 5)
        {
            _topK = topK;
        }

        public void AddMessage(string role, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;

            var item = new HistoryItem
            {
                Id = _items.Count,
                Role = role,
                Content = content
            };
            _items.Add(item);
            _totalDocs = _items.Count;

            var grams = GetNGrams(content);
            foreach (var gram in grams)
            {
                if (!_invertedIndex.ContainsKey(gram))
                    _invertedIndex[gram] = new HashSet<int>();
                _invertedIndex[gram].Add(item.Id);
            }

            _avgFieldLength = (_avgFieldLength * (_totalDocs - 1) + content.Length) / _totalDocs;
        }

        public List<(string Role, string Content)> Retrieve(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _items.Count == 0)
                return new List<(string, string)>();

            var queryGrams = GetNGrams(query);
            if (queryGrams.Count == 0) return new List<(string, string)>();

            var candidateDocIds = new HashSet<int>();
            foreach (var gram in queryGrams)
            {
                if (_invertedIndex.TryGetValue(gram, out var docIds))
                {
                    foreach (var id in docIds)
                        candidateDocIds.Add(id);
                }
            }
            if (candidateDocIds.Count == 0) return new List<(string, string)>();

            var scores = new Dictionary<int, double>();
            foreach (var docId in candidateDocIds)
                scores[docId] = ComputeBM25(docId, queryGrams);

            var topIds = scores.OrderByDescending(kv => kv.Value)
                               .Take(_topK)
                               .Select(kv => kv.Key)
                               .OrderBy(id => id)
                               .ToList();

            var result = new List<(string, string)>();
            foreach (var id in topIds)
            {
                var item = _items[id];
                result.Add((item.Role, item.Content));
            }
            return result;
        }

        private double ComputeBM25(int docId, HashSet<string> queryGrams)
        {
            const double k1 = 1.2;
            const double b = 0.75;

            var doc = _items[docId];
            double docLength = doc.Length;
            double score = 0;

            var docGramFreq = new Dictionary<string, int>();
            var docGrams = GetNGrams(doc.Content);
            foreach (var gram in docGrams)
                docGramFreq[gram] = docGramFreq.TryGetValue(gram, out var c) ? c + 1 : 1;

            foreach (var gram in queryGrams)
            {
                if (!docGramFreq.TryGetValue(gram, out int tf)) continue;

                double idf = Math.Log((_totalDocs - _invertedIndex[gram].Count + 0.5) /
                                      (_invertedIndex[gram].Count + 0.5) + 1.0);
                double tfNorm = tf * (k1 + 1) / (tf + k1 * (1 - b + b * (docLength / _avgFieldLength)));
                score += idf * tfNorm;
            }
            return score;
        }

        private HashSet<string> GetNGrams(string text)
        {
            var set = new HashSet<string>();
            string cleaned = Regex.Replace(text, @"[^\u4e00-\u9fa5a-zA-Z0-9]", " ");

            foreach (char c in cleaned)
                if (!char.IsWhiteSpace(c))
                    set.Add(c.ToString());

            for (int i = 0; i < cleaned.Length - 1; i++)
            {
                if (!char.IsWhiteSpace(cleaned[i]) && !char.IsWhiteSpace(cleaned[i + 1]))
                    set.Add(cleaned.Substring(i, 2));
            }
            return set;
        }

        public void Clear()
        {
            _items.Clear();
            _invertedIndex.Clear();
            _totalDocs = 0;
            _avgFieldLength = 20;
        }
    }

#if !LIBRARY_MODE
    // ===================================================================
    // 控制台入口（仅控制台应用模式编译；类库模式 -p:BuildAsLibrary=true 时排除）
    // ===================================================================
    class Program
    {
        // 兼容输入重定向/管道：无控制台时 ReadKey 会抛异常，此时返回 Enter（默认选择）
        public static ConsoleKeyInfo SafeReadKey()
        {
            try { return Console.ReadKey(); }
            catch (InvalidOperationException)
            {
                return new ConsoleKeyInfo('\n', ConsoleKey.Enter, false, false, false);
            }
        }

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // 从命令行参数构建可调配置（LuminaOptions）
            var options = new LuminaOptions();

            // 初始模式（--mode）
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mode" && i + 1 < args.Length)
                {
                    if (Enum.TryParse<ModelMode>(args[i + 1], true, out var mode))
                        options.InitialMode = mode;
                    break;
                }
            }

            // 历史文件参数（--history）
            string historyFilePath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--history" && i + 1 < args.Length)
                {
                    historyFilePath = args[++i];
                    break;
                }
            }

            // 注入控制台交互回调（库模式下由宿主自行实现）
            options.ConfirmCallback = prompt =>
            {
                Console.WriteLine();
                ConsoleHelper.Warning($"{prompt} (y/n)");
                var key = SafeReadKey();
                Console.WriteLine();
                return Task.FromResult(key.KeyChar == 'y' || key.KeyChar == 'Y');
            };
            options.LogCallback = (level, message) =>
            {
                switch (level)
                {
                    case LogLevel.Info: ConsoleHelper.Info(message); break;
                    case LogLevel.Warning: ConsoleHelper.Warning(message); break;
                    case LogLevel.Success: ConsoleHelper.Success(message); break;
                    case LogLevel.Error: ConsoleHelper.Error(message); break;
                    case LogLevel.Prompt: ConsoleHelper.Prompt(message); break;
                    default: ConsoleHelper.Info(message); break;
                }
            };

            // 注意：不要注册 ProcessExit -> Environment.Exit 的处理器（Environment.Exit 会再次触发 ProcessExit，导致挂起）
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Environment.Exit(0);
            };

            // 创建服务（仅内存态）并启动 llama-server / MCP
            await using var service = new LlamaChatService(options);
            await service.InitializeAsync();

            // 导入历史
            if (!string.IsNullOrEmpty(historyFilePath))
            {
                if (File.Exists(historyFilePath))
                {
                    try { service.ImportHistoryFromFile(historyFilePath); }
                    catch (Exception ex) { ConsoleHelper.Warning($"导入历史失败: {ex.Message}"); }
                }
                else { ConsoleHelper.Warning($"历史文件不存在: {historyFilePath}"); }
            }

            ConsoleHelper.Prompt("\n======= 全能 AI 助手已启动 =======");
            ConsoleHelper.Prompt($"当前模型模式: {service.CurrentMode} (端口 {service.CurrentPort})");
            ConsoleHelper.Prompt("命令: /mode [fast|balanced|quality] 切换模型, /clear 清除历史, /stats 缓存统计, exit 退出");
            ConsoleHelper.Prompt("你也可以直接输入对话内容。\n");

            while (true)
            {
                // 显示当前模式
                ConsoleHelper.Info($"当前模式: {service.CurrentMode} (端口 {service.CurrentPort})");
                ConsoleHelper.UserContentNoNewLine("用户: ");
                string input = Console.ReadLine();
                if (string.IsNullOrEmpty(input)) continue;

                // 处理命令
                if (input.ToLower() == "exit") break;
                if (input.ToLower() == "clear") { service.ClearHistory(); continue; }
                if (input.ToLower() == "stats") { ConsoleHelper.Info(service.GetCacheStats()); continue; }

                // 模式切换命令：/mode fast|balanced|quality
                if (input.StartsWith("/mode ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        if (Enum.TryParse<ModelMode>(parts[1], true, out var newMode))
                        {
                            try
                            {
                                await service.SwitchModeAsync(newMode);
                                ConsoleHelper.Success($"已切换至 {newMode} 模式 (端口 {service.CurrentPort})");
                            }
                            catch (Exception ex)
                            {
                                ConsoleHelper.Error($"切换失败: {ex.Message}");
                            }
                        }
                        else
                        {
                            ConsoleHelper.Warning($"未知模式: {parts[1]}，可用: fast, balanced, quality");
                        }
                    }
                    else
                    {
                        ConsoleHelper.Warning("用法: /mode [fast|balanced|quality]");
                    }
                    continue;
                }

                // 普通对话
                try
                {
                    // 询问角色：Ewin（不转换直接输出） / Miya-Bonsai（用风格转换模型转换）
                    ConsoleHelper.Warning("选择回答角色：1) Ewin  2) Miya-Bonsai  [回车默认 Miya-Bonsai]");
                    string roleInput = Console.ReadLine()?.Trim() ?? "";
                    var role = roleInput == "1"
                        ? LlamaChatService.ChatRole.Ewin
                        : LlamaChatService.ChatRole.MiyaBonsai;
                    service.SelectedRole = role;
                    ConsoleHelper.Success($"已选择角色: {role}");

                    // 角色模板直接回复（问候/身份询问/自我介绍/个人偏好）—— 不走任何 AI 路径
                    string templateReply = service.GetTemplateReply(input, role);
                    if (templateReply != null)
                    {
                        ConsoleHelper.UserContent($"助手: {templateReply}");
                        Console.WriteLine();
                        continue;
                    }

                    // 发送消息（SendMessageAsync 内部自动按角色决定是否做 Miya 风格转换）
                    string reply = await service.SendMessageAsync(input, role);
                    ConsoleHelper.UserContent($"助手: {reply}");
                }
                catch (Exception ex)
                {
                    ConsoleHelper.Error($"错误: {ex.Message}");
                }
                Console.WriteLine();
            }

            ConsoleHelper.Prompt("正在退出...");
            // 显式释放资源（llama-server 清理、MCP 释放），再强制退出：
            // MCP 库的子进程线程可能阻止进程自然结束
            await service.DisposeAsync();
            ConsoleHelper.Prompt("退出完成，再见！");
            Environment.Exit(0);
        }
    }
#endif
}