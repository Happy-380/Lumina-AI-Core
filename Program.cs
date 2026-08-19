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
        private McpToolSelector _toolSelector;
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
                Log(LogLevel.Info, T("系统可用内存 {0:F2} GB → 自动计算上下文: {1}", "Available memory {0:F2} GB → computed context size: {1}", avPhysGB, _contextSize));
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

        // ---- 中英文翻译（按 _options.Language；Auto 时跟随系统语言） ----
        private string T(string zh, string en)
            => I18n.T(_options.Language, zh, en);

        private string T(string zhFormat, string enFormat, params object[] args)
            => I18n.T(_options.Language, zhFormat, enFormat, args);

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
            _styleTransfer ??= new StyleTransferService(_options.Language);
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
                Log(LogLevel.Error, T("未找到 llama-server.exe: {0}", "llama-server.exe not found: {0}", serverExe));
                throw new FileNotFoundException(T("未找到 {0}", "Not found: {0}", serverExe));
            }
            if (!File.Exists(modelPath))
            {
                Log(LogLevel.Error, T("未找到模型文件: {0}", "Model file not found: {0}", modelPath));
                throw new FileNotFoundException(T("未找到模型文件 {0}", "Model file not found: {0}", modelPath));
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

            Log(LogLevel.Prompt, T("正在启动 llama-server (模式: {0}, 端口: {1})...", "Starting llama-server (mode: {0}, port: {1})...", mode, port));
            bool ready = WaitForServerAsync(port).GetAwaiter().GetResult();
            if (!ready)
            {
                Log(LogLevel.Error, T("llama-server (端口 {0}) 启动超时。", "llama-server (port {0}) startup timed out.", port));
                throw new TimeoutException(T("llama-server 启动超时 (端口 {0})。", "llama-server startup timed out (port {0}).", port));
            }
            Log(LogLevel.Success, T("llama-server 已就绪！(模式: {0}, 端口: {1})", "llama-server ready! (mode: {0}, port: {1})", mode, port));
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
            Log(LogLevel.Warning, T("发现 {0} 个残留 llama-server 进程，正在终止...", "Found {0} leftover llama-server process(es), terminating...", processes.Length));
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
            Log(LogLevel.Success, T("已清理所有 llama-server 进程。", "All llama-server processes cleaned up."));
        }

        // ---- 切换模型模式（异步） ----
        public async Task SwitchModeAsync(ModelMode newMode)
        {
            if (newMode == _currentMode)
            {
                Log(LogLevel.Info, T("当前已经是 {0} 模式，无需切换。", "Already in {0} mode.", newMode));
                return;
            }

            Log(LogLevel.Prompt, T("正在从 {0} 切换至 {1} ...", "Switching from {0} to {1} ...", _currentMode, newMode));
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
                    Log(LogLevel.Error, T("未找到 WindowsMcp.exe: {0}", "WindowsMcp.exe not found: {0}", mcpExe));
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
                _toolSelector = new McpToolSelector(_mcpTools, () => _options.Language);
                _isMcpReady = true;
                Log(LogLevel.Success, T("MCP 已就绪，加载了 {0} 个工具（智能选择：每次预选 {1} 个）", "MCP ready, loaded {0} tool(s) (smart selection: {1} per request)", _mcpTools.Count, _options.SelectedToolsPerRequest));
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, T("MCP 初始化失败: {0}", "MCP initialization failed: {0}", ex.Message));
                _isMcpReady = false;
            }
        }

        // +MCP: 组合工具选择的打分文本：当前输入 + 最近用户消息 + 相关历史
        private static string BuildSelectionQueryText(string userInput, JArray windowMessages, List<(string Role, string Content)> relevantHistories)
        {
            var queryParts = new List<string> { userInput };
            if (windowMessages != null)
            {
                var recentUsers = windowMessages
                    .Where(m => m["role"]?.ToString() == "user")
                    .Select(m => m["content"]?.ToString() ?? "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .TakeLast(3);
                queryParts.AddRange(recentUsers);
            }
            if (relevantHistories != null)
                queryParts.AddRange(relevantHistories.TakeLast(2).Select(h => h.Content));
            return string.Join("\n", queryParts);
        }

        // +MCP: 将 MCP 工具转换为 OpenAI 格式的 tools 数组（全量；仅在禁用智能选择时使用）
        private JArray BuildToolsJson()
        {
            var toolsArray = new JArray();
            foreach (var tool in _mcpTools)
                toolsArray.Add(MakeFunctionTool(tool.Name, tool.Description ?? "", tool.JsonSchema.GetRawText()));
            return toolsArray;
        }

        // +MCP: 智能构建 tools —— 仅预选最可能需要的 N 个工具（完整 schema）+ 2 个本地元工具
        private JArray BuildSmartToolsJson(List<McpClientTool> selected)
        {
            if (_toolSelector == null || selected == null || selected.Count == 0) return new JArray();

            Log(LogLevel.Info, T("智能预选 {0} 个工具: {1}", "Smart-pre-selected {0} tool(s): {1}",
                selected.Count, string.Join(", ", selected.Select(t => t.Name))));

            var toolsArray = new JArray();
            foreach (var tool in selected)
                toolsArray.Add(MakeFunctionTool(tool.Name, tool.Description ?? "", tool.JsonSchema.GetRawText()));

            // 元工具 1：查看全部工具概述（名称 + 一行用途，不含参数）
            toolsArray.Add(MakeFunctionTool(
                "list_tools",
                T("列出全部可用工具的概述（仅名称与用途，不含参数）。当预选工具都不适合当前任务时使用。",
                  "List an overview of ALL available tools (name + purpose only, no parameters). Use when none of the pre-selected tools fit the task."),
                "{\"type\":\"object\",\"properties\":{}}"));

            // 元工具 2：获取指定工具的完整参数说明
            toolsArray.Add(MakeFunctionTool(
                "get_tool_usage",
                T("获取指定工具的完整参数说明（JSON Schema）。参数 tool_name 填工具名（如 file_read）。",
                  "Get the full parameter documentation (JSON Schema) of a specific tool. Pass the exact tool name as tool_name (e.g. file_read)."),
                "{\"type\":\"object\",\"properties\":{\"tool_name\":{\"type\":\"string\",\"description\":\"Exact tool name, e.g. file_read\"}},\"required\":[\"tool_name\"]}"));

            return toolsArray;
        }

        // +MCP: 构造单个 OpenAI 格式 function 工具
        private static JObject MakeFunctionTool(string name, string description, string schemaJson)
        {
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = JObject.Parse(schemaJson)
                }
            };
        }

        // ---- 解析 Claude 风格文本工具调用（Bonsai 等模型可能不在 tool_calls 里输出，
        //      而是在 content 中直接输出 {"name":"xxx","arguments":{...}}</tool_call> 或 XML <invoke>）----
        // isKnownTool：仅当工具名是真实存在的工具时才解析成功，避免误把普通对话 JSON 当工具调用。
        private static bool TryParseTextToolCall(string content, Func<string, bool> isKnownTool, out string toolName, out JObject args)
        {
            toolName = null;
            args = null;
            if (string.IsNullOrWhiteSpace(content)) return false;

            // ---- 形式 1：JSON（容忍 ```json 代码块、</tool_call>、<function_calls> 等包裹）----
            string jsonText = Regex.Replace(content,
                @"```(?:json)?\s*|```|</?tool_call>|</?function_calls?>",
                "", RegexOptions.IgnoreCase);
            int s = jsonText.IndexOf('{');
            int e = jsonText.LastIndexOf('}');
            if (s >= 0 && e > s)
            {
                string json = jsonText.Substring(s, e - s + 1);
                try
                {
                    var obj = JObject.Parse(json);
                    var name = obj["name"]?.ToString();
                    if (!string.IsNullOrEmpty(name) && isKnownTool(name) && obj["arguments"] != null)
                    {
                        toolName = name;
                        args = obj["arguments"] as JObject;
                        if (args == null)
                        {
                            string argsStr = obj["arguments"]?.ToString();
                            try { args = JObject.Parse(argsStr); }
                            catch { args = new JObject(); }
                        }
                        return true;
                    }
                }
                catch { /* 不是合法 JSON，继续尝试 XML 形式 */ }
            }

            // ---- 形式 2：XML <invoke name="xxx"><parameter name="k">v</parameter>...</invoke> ----
            var m = Regex.Match(content,
                @"<invoke\s+name=[""']([A-Za-z0-9_]+)[""']\s*>(.*?)</invoke>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (m.Success && isKnownTool(m.Groups[1].Value))
            {
                toolName = m.Groups[1].Value;
                args = new JObject();
                foreach (Match p in Regex.Matches(m.Groups[2].Value,
                             @"<parameter\s+name=[""']([^""']+)[""']\s*>(.*?)</parameter>",
                             RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    string key = p.Groups[1].Value.Trim();
                    string val = p.Groups[2].Value.Trim();
                    if (val.Length == 0) { args[key] = ""; continue; }
                    if (val[0] == '{' || val[0] == '[' || val[0] == '"' ||
                        val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                        double.TryParse(val, out _))
                    {
                        try { args[key] = JToken.Parse(val); }
                        catch { args[key] = val; }
                    }
                    else
                    {
                        args[key] = val;
                    }
                }
                return true;
            }

            return false;
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
            Log(LogLevel.Success, T("已导入 {0} 条历史消息。", "Imported {0} history message(s).", count));
        }

        public void ImportHistoryFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            if (!File.Exists(filePath))
            {
                Log(LogLevel.Warning, T("历史文件不存在: {0}", "History file not found: {0}", filePath));
                throw new FileNotFoundException(T("历史文件不存在", "History file not found"), filePath);
            }
            string json = File.ReadAllText(filePath);
            JArray history = JArray.Parse(json);
            ImportHistory(history);
        }

        public void ClearHistory()
        {
            _context.Clear();
            _retriever.Clear();
            Log(LogLevel.Prompt, T("对话历史已清除（包含检索索引）。", "Chat history cleared (including retrieval index)."));
        }

        public string GetCacheStats() => _cache.GetStats(_options.Language);

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
                    Log(LogLevel.Info, T("⚡ 缓存命中，直接返回。", "⚡ Cache hit, returning directly."));
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
                Log(LogLevel.Info, T("检索到 {0} 条相关历史记录（已排除最近对话）。", "Retrieved {0} relevant history entrie(s) (excluding recent chat).", relevantHistories.Count));
            else
                Log(LogLevel.Info, T("未检索到相关历史记录。", "No relevant history found."));

            // 用户交互：是否允许操控（经 ConfirmCallback，默认拒绝）
            bool allowControl = false;
            if (_isMcpReady)
            {
                allowControl = await ConfirmAsync(T("是否允许 AI 操控电脑？", "Allow AI to control your computer?"));
                Log(LogLevel.Info, allowControl ? T("已允许 AI 操控电脑。", "AI control allowed.") : T("已禁止 AI 操控电脑，本次只进行普通对话。", "AI control denied; normal chat only."));
            }
            else
            {
                Log(LogLevel.Info, T("MCP 未就绪，无法操控电脑。", "MCP not ready; cannot control your computer."));
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
                        Log(LogLevel.Info, T("与最近 {0} 轮不相关（最大相似度 {1:F2} < {2}），将不带最近对话上下文，但保留检索到的背景。", "Not related to the last {0} turn(s) (max similarity {1:F2} < {2}); skipping recent context but keeping retrieved background.", checkRounds, maxSim, _options.RelevanceThreshold));
                    }
                    else
                    {
                        Log(LogLevel.Info, T("与最近对话相关（最大相似度 {0:F2} >= {1}），将携带滑动窗口。", "Related to recent chat (max similarity {0:F2} >= {1}); carrying sliding window.", maxSim, _options.RelevanceThreshold));
                    }
                }
                else
                {
                    useWindowContext = false;
                    Log(LogLevel.Info, T("尚无历史消息，将不带最近对话上下文。", "No history yet; skipping recent context."));
                }
            }

            // 5. 构建消息列表
            var messages = new JArray();

            // 5.1 系统提示（支持宿主通过 SetSystemPrompt / LuminaOptions 自定义；默认文案按语言）
            string systemPrompt = allowControl
                ? (_customControlSystemPrompt ?? _options.ControlSystemPrompt
                    ?? T("你是一个能操控 Windows 的 AI 助手。如果用户想操控电脑（如打开程序、移动鼠标、读写文件、上网购物等），你必须使用提供的工具来完成，不要用文字描述如何操作。如果用户只是普通聊天或提问，则直接回答。",
                        "You are an AI assistant that can control Windows. If the user wants to control the computer (open programs, move the mouse, read/write files, shop online, etc.), you MUST use the provided tools to do it, and never describe how to do it in words. If the user is just chatting or asking questions, answer directly."))
                : (_customSystemPrompt ?? _options.SystemPrompt
                    ?? T("你是一个叫做Lumina的AI助手，你的职责是与用户进行自然语言对话。", "You are an AI assistant named Lumina. Your job is to chat with the user in natural language."));
            messages.Add(new JObject { ["role"] = "system", ["content"] = systemPrompt });

            // 5.2 检索到的相关历史（始终添加）
            if (relevantHistories.Any())
            {
                var sb = new StringBuilder();
                sb.AppendLine(T("以下是用户之前提到过的相关信息，请参考这些内容来回答当前问题：", "The following are relevant details the user mentioned before. Please reference them when answering:"));
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

            // 5.3b 工具使用引导（仅操控模式且启用智能选择时）：
            // 预选工具在此一次性完成（与请求中的 tools 数组共用同一份），
            // 并把“工具名+用途”以纯文本注入，帮助小模型正确选择。
            List<McpClientTool> smartSelected = null;
            if (allowControl && _isMcpReady && _toolSelector != null && _options.SelectedToolsPerRequest > 0)
            {
                smartSelected = _toolSelector.SelectTopTools(
                    BuildSelectionQueryText(userInput, windowMessages, relevantHistories),
                    _options.SelectedToolsPerRequest);
                messages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = _toolSelector.GetSelectionInstructions(smartSelected)
                });
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
            int metaOperationCount = 0;   // 元工具（list_tools / get_tool_usage）调用计数
            int roundCount = 0;           // 已进行的工具轮数（用于收尾提醒）
            int noProgressRounds = 0;     // 连续无有效结果轮数（用于强制收尾）
            string lastToolResult = "";  // 最后一次工具执行结果（仅日志用）
            var successfulOps = new List<string>(); // 成功操作摘要（结束时让模型据此生成一句话总结）
            var executedCalls = new HashSet<string>(StringComparer.Ordinal); // 防重复调用（tool+args）
            bool expandTools = false;     // 模型请求 list_tools 后，下一轮发送全部工具（弱模型无需两步即可直接调用）
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
                    // 模型请求过 list_tools：本轮发送全部工具（含完整 schema），弱模型可直接调用正确工具
                    requestBody["tools"] = expandTools
                        ? BuildToolsJson()
                        : (smartSelected is { Count: > 0 } ? BuildSmartToolsJson(smartSelected) : BuildToolsJson());
                    expandTools = false;   // 仅放开一轮，之后回到智能预选以节省 token
                }

                Log(LogLevel.Info, T("========== 请求（发送给模型） ==========", "========== Request (sent to model) =========="));
                Log(LogLevel.Info, requestBody.ToString(Formatting.Indented));
                Log(LogLevel.Info, T("==========================================", "==========================================="));

                JObject response;
                try
                {
                    response = await PostChatCompletionAsync(requestBody);
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, T("调用模型失败: {0}", "Model call failed: {0}", ex.Message));
                    string errMsg = T("错误: {0}", "Error: {0}", ex.Message);
                    AnswerReceived?.Invoke(userInput, errMsg);
                    return errMsg;
                }

                var choice = response["choices"]?[0];
                if (choice == null)
                {
                    Log(LogLevel.Error, T("模型返回异常：无 choices", "Unexpected model response: no choices"));
                    string errMsg = T("模型返回异常。", "Unexpected model response.");
                    AnswerReceived?.Invoke(userInput, errMsg);
                    return errMsg;
                }
                var message = choice["message"];
                var content = message["content"]?.ToString() ?? "";
                var toolCalls = message["tool_calls"] as JArray;

                Log(LogLevel.Info, T("========== 响应（来自模型） ==========", "========== Response (from model) ==========="));
                Log(LogLevel.Info, response.ToString(Formatting.Indented));
                Log(LogLevel.Info, T("=======================================", "==========================================="));

                // 文本标记回退：模型可能以纯文本输出预设指令（而非工具调用）
                //   [LIST_TOOLS] / [工具列表]  → 注入全部工具概述；[USAGE:xxx] / [用法:xxx] → 注入某工具用法
                if (allowControl && _toolSelector != null && toolCalls is not { Count: > 0 } && !string.IsNullOrWhiteSpace(content))
                {
                    bool markerHandled = false;
                    var mList = Regex.Match(content, @"\[(?:LIST_TOOLS|ALL_TOOLS|TOOLS|工具列表)\]", RegexOptions.IgnoreCase);
                    if (mList.Success)
                    {
                        messages.Add(new JObject { ["role"] = "system", ["content"] = _toolSelector.GetOverview() });
                        expandTools = true;   // 下一轮提供全部工具 schema
                        messages.Add(new JObject
                        {
                            ["role"] = "user",
                            ["content"] = T(
                                "请从上面的工具中选择一个：直接用合适的工具完成我的请求（全部工具的参数说明已提供）。",
                                "Pick the right tool from the list above and call it directly to fulfill my request (all tool parameter docs are provided).")
                        });
                        markerHandled = true;
                    }
                    else
                    {
                        var mUsage = Regex.Match(content, @"\[(?:USAGE|用法)\s*:\s*([A-Za-z0-9_]+)\]", RegexOptions.IgnoreCase);
                        if (mUsage.Success)
                        {
                            string tname = mUsage.Groups[1].Value;
                            messages.Add(new JObject
                            {
                                ["role"] = "system",
                                ["content"] = _toolSelector.GetUsage(tname)
                                    ?? T("未找到工具 {0}。请先调用 list_tools 查看全部工具名称。", "Tool {0} not found. Call list_tools first to see all tool names.", tname)
                            });
                            markerHandled = true;
                        }
                    }
                    if (markerHandled) continue;
                }

                // 收集待执行的工具调用：优先标准 tool_calls 数组，其次解析 Claude 风格文本 JSON/XML
                //（Bonsai 等模型可能不在 tool_calls 里输出，而是直接输出 {"name":...,"arguments":{...}}</tool_call>）
                var pendingCalls = new List<(string Id, string Name, JObject Args)>();
                if (toolCalls is { Count: > 0 })
                {
                    foreach (var tc in toolCalls)
                    {
                        var tName = tc["function"]?["name"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(tName)) continue;
                        var argsJson = tc["function"]?["arguments"]?.ToString() ?? "{}";
                        JObject tArgs;
                        try { tArgs = JObject.Parse(argsJson); }
                        catch { tArgs = new JObject(); }
                        pendingCalls.Add((tc["id"]?.ToString() ?? Guid.NewGuid().ToString("N"), tName, tArgs));
                    }
                }
                else if (_toolSelector != null && TryParseTextToolCall(content, _toolSelector.IsKnownTool, out var textToolName, out var textArgs))
                {
                    Log(LogLevel.Info, T("检测到文本格式工具调用：{0}（参数：{1}）", "Detected text-format tool call: {0} (args: {1})", textToolName, textArgs.ToString(Formatting.None)));
                    pendingCalls.Add((Guid.NewGuid().ToString("N"), textToolName, textArgs));
                }

                if (pendingCalls.Count > 0)
                {
                    if (!allowControl)
                    {
                        Log(LogLevel.Warning, T("模型请求了工具调用，但用户未允许操控。已忽略工具调用，请重新输入。", "The model requested tool calls, but control is not allowed. Tool calls ignored; please re-enter."));
                        string deniedMsg = T("AI 尝试使用工具但被拒绝。", "AI attempted to use tools but was denied.");
                        AnswerReceived?.Invoke(userInput, deniedMsg);
                        return deniedMsg;
                    }

                    isControlMode = true;

                    if (isFirstToolCall)
                    {
                        bool ok = await ConfirmAsync(T("即将执行电脑操控操作，是否继续？", "About to perform computer control operations. Continue?"));
                        if (!ok)
                        {
                            Log(LogLevel.Info, T("用户取消了操作。", "Operation cancelled by user."));
                            userCancelled = true;
                            string cancelMsg = T("用户取消了操作。", "Operation cancelled by user.");
                            _context.AddMessage("user", userInput);
                            _context.AddMessage("assistant", cancelMsg);
                            AnswerReceived?.Invoke(userInput, cancelMsg);
                            return cancelMsg;
                        }
                        isFirstToolCall = false;
                    }

                    // 先执行所有调用、收集结果；失败的错误结果不进入对话（避免带偏模型），
                    // 并避免悬空的 tool_calls 造成格式错误
                    var executedResults = new List<(string CallId, string ToolName, string Content)>();
                    bool roundMadeProgress = false;   // 本轮是否产生了有效结果
                    foreach (var (callId, toolName, args) in pendingCalls)
                    {
                        var argsJson = args.ToString(Formatting.None);

                        // 防死循环：相同工具 + 相同参数不重复执行，直接提示模型收尾
                        string callKey = $"{toolName}|{argsJson}";
                        if (!executedCalls.Add(callKey))
                        {
                            executedResults.Add((callId, toolName, T(
                                "重复调用：{0} 已用相同参数执行过，结果见上一条。请基于已有结果直接回答用户，不要再重复调用相同工具；如需其他操作请使用不同参数或调用 list_tools。",
                                "Duplicate call: {0} was already executed with identical arguments (see previous result). Answer the user directly based on existing results; do not repeat the same call. Use different arguments or call list_tools if you need something else.",
                                toolName)));
                            continue;
                        }

                        if (_dangerousTools.Contains(toolName))
                        {
                            bool ok = await ConfirmAsync(T("即将执行危险操作：{0}，参数：{1}，是否继续？", "About to perform a DANGEROUS operation: {0}, args: {1}. Continue?", toolName, argsJson));
                            if (!ok)
                            {
                                executedResults.Add((callId, toolName, T("用户取消了危险操作 {0}", "User cancelled dangerous operation {0}", toolName)));
                                continue;
                            }
                        }

                        string resultContent;
                        bool callMadeProgress = false;   // 该调用是否产生了有效结果（用于无进展检测）

                        // 元工具：本地处理（查看全部工具概述 / 查看单个工具用法），不发送给 MCP
                        if (toolName.Equals("list_tools", StringComparison.OrdinalIgnoreCase) && _toolSelector != null)
                        {
                            resultContent = _toolSelector.GetOverview();
                            expandTools = true;   // 下一轮自动提供全部工具 schema，弱模型可直接调用
                            metaOperationCount++;
                            callMadeProgress = true;
                            Log(LogLevel.Info, T("模型请求查看全部工具概述，下一轮将提供全部工具。", "Model requested the full tool overview; all tools will be provided next round."));
                        }
                        else if (toolName.Equals("get_tool_usage", StringComparison.OrdinalIgnoreCase) && _toolSelector != null)
                        {
                            string tname = args["tool_name"]?.ToString() ?? "";
                            resultContent = _toolSelector.GetUsage(tname)
                                ?? T("未找到工具 {0}。请先调用 list_tools 查看全部工具名称。", "Tool {0} not found. Call list_tools first to see all tool names.", tname);
                            metaOperationCount++;
                            callMadeProgress = true;
                            Log(LogLevel.Info, T("模型请求查看工具 {0} 的用法。", "Model requested usage of tool {0}.", tname));
                        }
                        else
                        {
                            Log(LogLevel.Success, T("正在执行：{0}，参数：{1}", "Executing: {0}, args: {1}", toolName, argsJson));
                            try
                            {
                                var argsDict = args.ToObject<Dictionary<string, object?>>();
                                var toolResult = await _mcpClient.CallToolAsync(toolName, argsDict);
                                // Content 是 IList<ContentBlock>，文本内容在 TextContentBlock.Text 中
                                var texts = (toolResult.Content ?? Array.Empty<ContentBlock>())
                                    .OfType<TextContentBlock>()
                                    .Select(c => c.Text)
                                    .Where(s => !string.IsNullOrEmpty(s));
                                resultContent = string.Join("\n", texts);
                                if (string.IsNullOrEmpty(resultContent))
                                {
                                    resultContent = toolResult.IsError == true
                                        ? T("执行失败（无详细输出）", "Execution failed (no detail output)")
                                        : T("执行成功", "Executed successfully");
                                }
                                else if (toolResult.IsError == true)
                                {
                                    resultContent = T("[错误] {0}", "[Error] {0}", resultContent);
                                }
                                Log(LogLevel.Info, T("执行结果：{0}", "Result: {0}", resultContent));
                                operationCount++;
                            }
                            catch (Exception ex)
                            {
                                Log(LogLevel.Error, T("执行 {0} 失败：{1}", "Failed to execute {0}: {1}", toolName, ex.Message));
                                resultContent = T("错误：{0}", "Error: {0}", ex.Message);
                            }
                        }

                        // 判断是否有效进展：非错误、非空、非空数组的结果才算数
                        bool isMetaCall = toolName.Equals("list_tools", StringComparison.OrdinalIgnoreCase)
                                       || toolName.Equals("get_tool_usage", StringComparison.OrdinalIgnoreCase);
                        callMadeProgress = !resultContent.StartsWith("[错误]", StringComparison.Ordinal)
                            && !resultContent.StartsWith("错误：", StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(resultContent)
                            && resultContent != "[]"
                            && !resultContent.Equals(T("执行成功", "Executed successfully"), StringComparison.Ordinal);
                        roundMadeProgress |= callMadeProgress;

                        lastToolResult = resultContent;

                        // 失败/错误的结果不发送给模型（避免模型被错误信息带偏、继续编造），仅记录日志
                        if (resultContent.StartsWith("[错误]", StringComparison.Ordinal) || resultContent.StartsWith("错误：", StringComparison.Ordinal))
                        {
                            Log(LogLevel.Warning, T("操作 {0} 结果为错误，已过滤（不发送给模型）：{1}", "Result of {0} is an error; filtered (not sent to model): {1}", toolName, resultContent));
                            continue;
                        }

                        // 收集成功操作摘要（结束时让模型据此生成一句话总结）
                        if (callMadeProgress && !isMetaCall)
                        {
                            string brief = resultContent.Length > 160 ? resultContent.Substring(0, 160) + "…" : resultContent;
                            successfulOps.Add($"- {toolName}: {brief}");
                        }

                        executedResults.Add((callId, toolName, resultContent));
                    }

                    // 将结果写入对话（错误结果已过滤；助手消息的 tool_calls 与结果一一对应）
                    if (executedResults.Count == 0)
                    {
                        Log(LogLevel.Warning, T("本轮 {0} 个工具调用结果均为错误，已全部过滤，回合未写入对话。", "All {0} tool call(s) in this round returned errors and were filtered; the round was not written to the conversation.", pendingCalls.Count));
                    }
                    else
                    {
                        if (message["tool_calls"] is JArray tcArray && tcArray.Count > 0)
                        {
                            // OpenAI 格式：助手消息中的 tool_calls 必须与 tool 结果一一对应，重建仅保留有结果的调用
                            var keptIds = new HashSet<string>(executedResults.Select(r => r.CallId), StringComparer.Ordinal);
                            var keptCalls = new JArray();
                            foreach (var tc in tcArray)
                            {
                                string tid = tc["id"]?.ToString();
                                if (tid != null && keptIds.Contains(tid)) keptCalls.Add(tc);
                            }
                            if (keptCalls.Count > 0)
                            {
                                var assistantMsg = (JObject)message.DeepClone();
                                assistantMsg["tool_calls"] = keptCalls;
                                messages.Add(assistantMsg);
                            }
                        }
                        else
                        {
                            // Claude 文本格式：助手消息原样加入
                            messages.Add(message);
                        }

                        foreach (var (callId, toolName, resultContent) in executedResults)
                        {
                            messages.Add(new JObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = callId,
                                ["content"] = resultContent
                            });
                        }
                    }

                    // 防死循环：每 3 轮提醒模型尽快收尾
                    roundCount++;
                    if (roundCount >= 3 && roundCount % 3 == 0)
                    {
                        messages.Add(new JObject
                        {
                            ["role"] = "system",
                            ["content"] = T(
                                "提示：如果已经完成用户的请求，请立即给出最终文字回答，不要再调用工具。",
                                "Reminder: if the user's request has been fulfilled, give a final text answer now and stop calling tools.")
                        });
                    }

                    // 防死循环：连续多轮无有效结果 → 提醒一次并强制收尾（不再无谓地烧完所有轮次）
                    if (!roundMadeProgress) noProgressRounds++;
                    else noProgressRounds = 0;
                    if (noProgressRounds == 2)
                    {
                        messages.Add(new JObject
                        {
                            ["role"] = "system",
                            ["content"] = T(
                                "工具调用连续未产生有效结果。请停止继续调用工具，直接基于已有信息回答用户；如果确实需要其他工具，请先调用 list_tools 确认工具名后再调用。",
                                "Consecutive tool calls produced no useful result. Stop calling tools and answer the user directly based on existing information; if you truly need another tool, call list_tools first to confirm the name.")
                        });
                    }
                    if (noProgressRounds >= 3)
                    {
                        Log(LogLevel.Warning, T("连续 {0} 轮无有效结果，强制结束工具循环。", "{0} consecutive rounds with no useful result; forcibly ending the tool loop.", noProgressRounds));
                        string stopMsg = await BuildSummaryAsync(userInput, successfulOps);
                        _context.AddMessage("user", userInput);
                        _context.AddMessage("assistant", stopMsg);
                        AnswerReceived?.Invoke(userInput, stopMsg);
                        return stopMsg;
                    }

                    continue;
                }

                // 没有工具调用
                if (isControlMode)
                {
                    string finalMsg;
                    if (userCancelled)
                        finalMsg = T("操作已取消。", "Operation cancelled.");
                    else if (!string.IsNullOrWhiteSpace(content)
                             && !(_toolSelector != null && TryParseTextToolCall(content, _toolSelector.IsKnownTool, out _, out _)))
                        finalMsg = content.Trim();   // 模型已基于工具结果给出自然总结（如“已为你打开记事本”）
                    else if (operationCount > 0)
                        finalMsg = T("已执行 {0} 个操作，任务完成。", "Executed {0} operation(s). Task complete.", operationCount);
                    else if (metaOperationCount > 0)
                        finalMsg = T("已查看工具信息，未执行实际操作。", "Tool info was viewed; no operations were actually executed.");
                    else
                        finalMsg = T("未执行任何操作，任务可能失败。", "No operations executed; the task may have failed.");
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

            // 循环次数用尽：让模型基于成功执行的操作生成一句话总结（不再展示原始错误结果）
            string limitMsg = await BuildSummaryAsync(userInput, successfulOps);
            _context.AddMessage("user", userInput);
            _context.AddMessage("assistant", limitMsg);
            AnswerReceived?.Invoke(userInput, limitMsg);
            return limitMsg;
        }

        // ---- 工具循环结束时的总结：让模型基于成功执行的操作生成一句话总结 ----
        // 原则：提示词简短；只发送成功操作的结果；错误结果一律不发给模型；
        // 使用最小消息集（不含任何工具调用历史），避免模型继续输出工具调用格式。
        private async Task<string> BuildSummaryAsync(string userInput, List<string> successfulOps)
        {
            var summaryMessages = new JArray();
            summaryMessages.Add(new JObject { ["role"] = "user", ["content"] = userInput });

            if (successfulOps.Count > 0)
            {
                summaryMessages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = T(
                        "工具调用已结束。请用一句话向用户总结你已成功执行的操作（不要提失败或错误，不要输出任何工具调用格式）。",
                        "Tool calls are finished. Summarize in ONE sentence what you successfully did for the user (do not mention failures or errors, and do not output any tool-call format).")
                });
                summaryMessages.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = string.Join("\n", successfulOps)
                });
            }
            else
            {
                summaryMessages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = T(
                        "工具调用已结束，未能完成用户请求。请用一句话向用户说明情况。",
                        "Tool calls ended without completing the user's request. Explain the situation in one sentence.")
                });
            }

            var requestBody = new JObject
            {
                ["messages"] = summaryMessages,
                ["temperature"] = 0.5,
                ["max_tokens"] = _options.MaxResponseTokens,
                ["stream"] = false
            };
            try
            {
                var response = await PostChatCompletionAsync(requestBody);
                string content = response["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                // 若模型仍输出工具调用格式，则回退到自拼总结
                if (!string.IsNullOrWhiteSpace(content)
                    && !(_toolSelector != null && TryParseTextToolCall(content, _toolSelector.IsKnownTool, out _, out _)))
                    return content.Trim();
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, T("生成操作总结失败：{0}", "Failed to generate operation summary: {0}", ex.Message));
            }

            // 回退：自拼简洁总结（仅列出成功操作的工具名）
            if (successfulOps.Count > 0)
            {
                var toolNames = successfulOps
                    .Select(s => s.Trim().TrimStart('-').Trim().Split(':')[0].Trim())
                    .ToList();
                return T("已执行 {0} 个操作：{1}。", "Executed {0} operation(s): {1}.", successfulOps.Count, string.Join("、", toolNames));
            }
            return T("未能完成操作。", "Could not complete the operation.");
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
                    Log(LogLevel.Warning, T("MCP 客户端释放超时（子进程可能未响应），已跳过。", "MCP client dispose timed out (child process unresponsive); skipped."));
                    // 若 disposeTask 仍在运行（后台），等待一小段时间后强制终止进程，避免退出挂起
                    var finish = await Task.WhenAny(disposeTask, Task.Delay(3000));
                    Log(LogLevel.Warning, T("MCP 释放任务状态: {0} ", "MCP dispose task status: {0} ", disposeTask.IsCompleted ? "完成" : disposeTask.Status.ToString()));
                    if (finish != disposeTask)
                    {
                        // 库模式下不再强制 Environment.Exit（会杀死宿主进程），仅记录并跳过
                        Log(LogLevel.Warning, T("MCP 释放任务仍挂起，跳过等待。", "MCP dispose task still pending; skipped waiting."));
                    }
                }
            }
            Log(LogLevel.Warning, T("DisposeAsync 完成", "DisposeAsync complete"));
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

        public string GetStats() => GetStats(AppLanguage.Auto);

        // 带语言参数的统计文本（中文 / 英文）
        public string GetStats(AppLanguage language)
            => I18n.T(language, "缓存条目数: {0}，相似度阈值: {1:P0}", "Cache entries: {0}, similarity threshold: {1:P0}", _entries.Count, _threshold);
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

            // 语言参数（--lang zh|en|auto；默认 Auto = 跟随系统语言）
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--lang" && i + 1 < args.Length)
                {
                    if (I18n.TryParse(args[i + 1], out var lang))
                        options.Language = lang;
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

            // 界面翻译（跟随 options.Language，/lang 切换后即时生效）
            string L(string zh, string en) => I18n.T(options.Language, zh, en);
            string Lf(string zhFormat, string enFormat, params object[] fmtArgs) => I18n.T(options.Language, zhFormat, enFormat, fmtArgs);

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

            // 欢迎消息
            Console.Write("\x1b[38;2;255;192;203m");
            string logo = @"
   __                 _                 
  / / _   _ _ __ ___ (_)_ __   __ _      
 / / | | | | '_ ` _ \| | '_ \ / _` |
/ /__| |_| | | | | | | | | | | (_| |
\____/\__,_|_| |_| |_|_|_| |_|\__,_|     
                                                
        ";
            Console.WriteLine(logo);
            Console.ResetColor();   // 恢复默认颜色

            Console.Write("\x1b[38;2;252;255;175m");
            Console.WriteLine("Welcome to Lumina AI Core!");
            Console.WriteLine("Build 9");
            Console.WriteLine("");


            // 创建服务（仅内存态）并启动 llama-server / MCP
            await using var service = new LlamaChatService(options);
            await service.InitializeAsync();

            // 导入历史
            if (!string.IsNullOrEmpty(historyFilePath))
            {
                if (File.Exists(historyFilePath))
                {
                    try { service.ImportHistoryFromFile(historyFilePath); }
                    catch (Exception ex) { ConsoleHelper.Warning(Lf("导入历史失败: {0}", "Failed to import history: {0}", ex.Message)); }
                }
                else { ConsoleHelper.Warning(Lf("历史文件不存在: {0}", "History file not found: {0}", historyFilePath)); }
            }

            ConsoleHelper.Prompt(L("\n======= Lumina AI Core 已启动 =======", "\n======= Lumina AI Core Started ======="));
            ConsoleHelper.Prompt(Lf("当前模型模式: {0} (端口 {1})", "Model mode: {0} (port {1})", service.CurrentMode, service.CurrentPort));
            ConsoleHelper.Prompt(L(
                "\n命令:\n /mode [fast|balanced|quality] 切换模型\n /lang [zh|en|auto] 切换语言\n /clear 清除历史\n /stats 缓存统计\n exit 退出",
                "\nCommands:\n /mode [fast|balanced|quality] switch model\n /lang [zh|en|auto] switch language\n /clear clear history\n /stats cache stats\n exit quit"));
            ConsoleHelper.Prompt(L("你也可以直接输入对话内容。\n", "You can also chat directly.\n"));

            while (true)
            {
                // 显示当前模式
                ConsoleHelper.Info(Lf("当前模式: {0} (端口 {1})", "Mode: {0} (port {1})", service.CurrentMode, service.CurrentPort));
                ConsoleHelper.UserContentNoNewLine(L("用户: ", "You: "));
                string input = Console.ReadLine();
                if (string.IsNullOrEmpty(input)) continue;

                string cmd = input.Trim().ToLowerInvariant();

                // 处理命令（中英文均可）
                if (cmd is "exit" or "退出") break;
                if (cmd is "clear" or "清除") { service.ClearHistory(); continue; }
                if (cmd is "stats" or "统计") { ConsoleHelper.Info(service.GetCacheStats()); continue; }

                // 模式切换命令：/mode fast|balanced|quality
                if (cmd.StartsWith("/mode ") || cmd.StartsWith("模式 "))
                {
                    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        if (Enum.TryParse<ModelMode>(parts[1], true, out var newMode))
                        {
                            try
                            {
                                await service.SwitchModeAsync(newMode);
                                ConsoleHelper.Success(Lf("已切换至 {0} 模式 (端口 {1})", "Switched to {0} mode (port {1})", newMode, service.CurrentPort));
                            }
                            catch (Exception ex)
                            {
                                ConsoleHelper.Error(Lf("切换失败: {0}", "Switch failed: {0}", ex.Message));
                            }
                        }
                        else
                        {
                            ConsoleHelper.Warning(Lf("未知模式: {0}，可用: fast, balanced, quality", "Unknown mode: {0}. Available: fast, balanced, quality", parts[1]));
                        }
                    }
                    else
                    {
                        ConsoleHelper.Warning(L("用法: /mode [fast|balanced|quality]", "Usage: /mode [fast|balanced|quality]"));
                    }
                    continue;
                }

                // 语言切换命令：/lang zh|en|auto
                if (cmd.StartsWith("/lang ") || cmd.StartsWith("语言 "))
                {
                    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        if (I18n.TryParse(parts[1], out var lang))
                        {
                            options.Language = lang;
                            ConsoleHelper.Success(Lf("已切换语言: {0}", "Language switched to: {0}", I18n.DisplayName(lang)));
                        }
                        else
                        {
                            ConsoleHelper.Warning(Lf("未知语言: {0}，可用: zh, en, auto", "Unknown language: {0}. Available: zh, en, auto", parts[1]));
                        }
                    }
                    else
                    {
                        ConsoleHelper.Warning(Lf("用法: /lang [zh|en|auto]（当前: {0}）", "Usage: /lang [zh|en|auto] (current: {0})", I18n.DisplayName(options.Language)));
                    }
                    continue;
                }

                // 普通对话
                try
                {
                    // 询问角色：Ewin（不转换直接输出） / Miya-Bonsai（用风格转换模型转换）
                    ConsoleHelper.Warning(L(
                        "选择回答角色：1) Ewin  2) Miya-Bonsai  [回车默认 Miya-Bonsai]",
                        "Choose response role: 1) Ewin  2) Miya-Bonsai  [Enter = default Miya-Bonsai]"));
                    string roleInput = Console.ReadLine()?.Trim() ?? "";
                    var role = roleInput == "1"
                        ? LlamaChatService.ChatRole.Ewin
                        : LlamaChatService.ChatRole.MiyaBonsai;
                    service.SelectedRole = role;
                    ConsoleHelper.Success(Lf("已选择角色: {0}", "Role selected: {0}", role));

                    // 角色模板直接回复（问候/身份询问/自我介绍/个人偏好）—— 不走任何 AI 路径
                    string templateReply = service.GetTemplateReply(input, role);
                    if (templateReply != null)
                    {
                        ConsoleHelper.UserContent(Lf("助手: {0}", "Assistant: {0}", templateReply));
                        Console.WriteLine();
                        continue;
                    }

                    // 发送消息（SendMessageAsync 内部自动按角色决定是否做 Miya 风格转换）
                    string reply = await service.SendMessageAsync(input, role);
                    ConsoleHelper.UserContent(Lf("助手: {0}", "Assistant: {0}", reply));
                }
                catch (Exception ex)
                {
                    ConsoleHelper.Error(Lf("错误: {0}", "Error: {0}", ex.Message));
                }
                Console.WriteLine();
            }

            ConsoleHelper.Prompt(L("正在退出...", "Exiting..."));
            // 显式释放资源（llama-server 清理、MCP 释放），再强制退出：
            // MCP 库的子进程线程可能阻止进程自然结束
            await service.DisposeAsync();
            ConsoleHelper.Prompt(L("退出完成，再见！", "Goodbye!"));
            Environment.Exit(0);
        }
    }
#endif
}