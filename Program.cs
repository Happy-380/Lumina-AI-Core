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

        // 模型配置：三种模式
        public enum ModelMode
        {
            Fast,      // 1.7B
            Balanced,  // 4B
            Quality    // 8B
        }

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
        private AppConfig.ModelMode _currentMode;

        // 语言风格转换服务（Miya）
        private StyleTransferService _styleTransfer;

        // 角色身份模板服务（问候/身份/自我介绍/个人偏好，不走 AI）
        private readonly CharacterIdentityService _identity = new();

        // 当前选择的角色（上下文不记录角色，仅用于决定是否风格转换）
        public ChatRole SelectedRole { get; set; } = ChatRole.MiyaBonsai;

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

        // 构造函数：指定初始模式
        public LlamaChatService(AppConfig.ModelMode initialMode = AppConfig.ModelMode.Balanced, int? manualContextSize = null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            _currentMode = initialMode;

            // 计算上下文大小（与模型无关）
            if (manualContextSize.HasValue && manualContextSize.Value > 0)
                _contextSize = manualContextSize.Value;
            else if (AppConfig.ManualContextSize > 0)
                _contextSize = AppConfig.ManualContextSize;
            else
            {
                double avPhysGB = GetAvailablePhysicalMemoryGB();
                const double modelOverheadGB = 4.1;
                double maxAllocGB = Math.Min(24, avPhysGB * 0.85);
                double kvCacheBudgetGB = Math.Max(0, maxAllocGB - modelOverheadGB);
                _contextSize = (int)(kvCacheBudgetGB * 1024 / 0.286);
                _contextSize = Math.Min(Math.Max(_contextSize, 10240), 32768);
                ConsoleHelper.Info($"系统可用内存 {avPhysGB:F2} GB → 自动计算上下文: {_contextSize}");
            }

            _context = new ConversationContext(
                maxContextTokens: _contextSize,
                maxResponseTokens: AppConfig.MaxResponseTokens,
                reserveTokens: AppConfig.ReserveTokens,
                charPerToken: AppConfig.CharPerToken
            );
            _cache = new SemanticCache(
                similarityThreshold: AppConfig.SimilarityThreshold,
                maxEntries: AppConfig.MaxCacheEntries
            );
            _retriever = new HistoryRetriever(topK: AppConfig.HistoryRetrievalTopK);

            // 启动对应模式的 llama-server
            StartServerForMode(_currentMode);

            // +MCP: 初始化 MCP
            InitializeMcpAsync().GetAwaiter().GetResult();

            // 语言风格转换服务（Miya）：懒加载，仅在需要转换时启动服务器
            _styleTransfer = new StyleTransferService();
        }

        // ---- 将一段文本转换为 Miya 风格（自动检测语言） ----
        public async Task<string> ConvertStyleAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            return await _styleTransfer.ConvertMarkdownAsync(text);
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
        }

        // ---- 根据模式启动服务器 ----
        private void StartServerForMode(AppConfig.ModelMode mode)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string llamaDir = Path.Combine(baseDir, AppConfig.LlamaFolderName);
            string serverExe = Path.Combine(llamaDir, "llama-server.exe");
            string modelFile = AppConfig.ModelFiles[mode];
            string modelPath = Path.Combine(llamaDir, modelFile);
            int port = AppConfig.ModelPorts[mode];

            if (!File.Exists(serverExe))
            {
                ConsoleHelper.Error($"未找到 llama-server.exe: {serverExe}");
                throw new FileNotFoundException($"未找到 {serverExe}");
            }
            if (!File.Exists(modelPath))
            {
                ConsoleHelper.Error($"未找到模型文件: {modelPath}");
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

            ConsoleHelper.Prompt($"正在启动 llama-server (模式: {mode}, 端口: {port})...");
            bool ready = WaitForServerAsync(port).GetAwaiter().GetResult();
            if (!ready)
            {
                ConsoleHelper.Error($"llama-server (端口 {port}) 启动超时。");
                throw new TimeoutException($"llama-server 启动超时 (端口 {port})。");
            }
            ConsoleHelper.Success($"llama-server 已就绪！(模式: {mode}, 端口: {port})");
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

        // ---- 静态方法：杀死所有 llama-server 进程 ----
        private static void KillAllLlamaServers()
        {
            var processes = Process.GetProcessesByName("llama-server");
            if (processes.Length == 0) return;
            ConsoleHelper.Warning($"发现 {processes.Length} 个残留 llama-server 进程，正在终止...");
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
            ConsoleHelper.Success("已清理所有 llama-server 进程。");
        }

        // ---- 切换模型模式（异步） ----
        public async Task SwitchModeAsync(AppConfig.ModelMode newMode)
        {
            if (newMode == _currentMode)
            {
                ConsoleHelper.Info($"当前已经是 {newMode} 模式，无需切换。");
                return;
            }

            ConsoleHelper.Prompt($"正在从 {_currentMode} 切换至 {newMode} ...");
            StopCurrentServer();        // 杀死当前及残留进程
            _currentMode = newMode;
            StartServerForMode(newMode); // 启动新服务器
            await Task.CompletedTask;    // 因为启动是同步的，但为了接口异步，留空
        }

        // ---- 获取当前模式 ----
        public AppConfig.ModelMode CurrentMode => _currentMode;

        // ---- 获取端口 ----
        public int CurrentPort => AppConfig.ModelPorts[_currentMode];

        // +MCP: 初始化 MCP 客户端
        private async Task InitializeMcpAsync()
        {
            try
            {
                string mcpExe = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    AppConfig.McpFolderName,
                    AppConfig.McpExeName
                );
                if (!File.Exists(mcpExe))
                {
                    ConsoleHelper.Error($"未找到 WindowsMcp.exe: {mcpExe}");
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
                ConsoleHelper.Success($"MCP 已就绪，加载了 {_mcpTools.Count} 个工具");
            }
            catch (Exception ex)
            {
                ConsoleHelper.Error($"MCP 初始化失败: {ex.Message}");
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
            ConsoleHelper.Success($"已导入 {count} 条历史消息。");
        }

        public void ImportHistoryFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            if (!File.Exists(filePath))
            {
                ConsoleHelper.Warning($"历史文件不存在: {filePath}");
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
            ConsoleHelper.Prompt("对话历史已清除（包含检索索引）。");
        }

        public string GetCacheStats() => _cache.GetStats();

        // ---- 核心方法：发送消息（postProcess：可选的后处理钩子，用于 Miya 风格转换；上下文筛选/提示词建构逻辑不变） ----
        public async Task<string> SendMessageAsync(string userInput, Func<string, Task<string>>? postProcess = null)
        {
            if (string.IsNullOrEmpty(userInput)) return string.Empty;

            // 1. 语义缓存
            if (AppConfig.EnableSemanticCache)
            {
                string cached = _cache.GetCachedAnswer(userInput);
                if (cached != null)
                {
                    ConsoleHelper.Info("⚡ 缓存命中，直接返回。");
                    _context.AddMessage("user", userInput);
                    _context.AddMessage("assistant", cached);
                    _retriever.AddMessage("user", userInput);
                    _retriever.AddMessage("assistant", cached);
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
                ConsoleHelper.Info($"检索到 {relevantHistories.Count} 条相关历史记录（已排除最近对话）。");
            else
                ConsoleHelper.Info("未检索到相关历史记录。");

            // 用户交互：是否允许操控
            bool allowControl = false;
            if (_isMcpReady)
            {
                ConsoleHelper.Warning("是否允许 AI 操控电脑？(y/n)");
                var key = Program.SafeReadKey();
                Console.WriteLine();
                if (key.KeyChar == 'y' || key.KeyChar == 'Y')
                {
                    allowControl = true;
                    ConsoleHelper.Success("已允许 AI 操控电脑。");
                }
                else
                {
                    ConsoleHelper.Info("已禁止 AI 操控电脑，本次只进行普通对话。");
                }
            }
            else
            {
                ConsoleHelper.Info("MCP 未就绪，无法操控电脑。");
            }

            // 4. 判断是否与最近 N 轮相关（决定是否携带滑动窗口）
            bool useWindowContext = true;
            int checkRounds = AppConfig.RelevanceCheckRounds;
            if (checkRounds > 0)
            {
                var lastUserMessages = _context.GetLastUserMessages(checkRounds);
                if (lastUserMessages.Any())
                {
                    double maxSim = lastUserMessages.Max(prev => SemanticCache.ComputeSimilarity(prev, userInput));
                    if (maxSim < AppConfig.RelevanceThreshold)
                    {
                        useWindowContext = false;
                        ConsoleHelper.Info($"与最近 {checkRounds} 轮不相关（最大相似度 {maxSim:F2} < {AppConfig.RelevanceThreshold}），将不带最近对话上下文，但保留检索到的背景。");
                    }
                    else
                    {
                        ConsoleHelper.Info($"与最近对话相关（最大相似度 {maxSim:F2} >= {AppConfig.RelevanceThreshold}），将携带滑动窗口。");
                    }
                }
                else
                {
                    useWindowContext = false;
                    ConsoleHelper.Info("尚无历史消息，将不带最近对话上下文。");
                }
            }

            // 5. 构建消息列表
            var messages = new JArray();

            // 5.1 系统提示
            string systemPrompt = allowControl
                ? @"你是一个能操控 Windows 的 AI 助手。如果用户想操控电脑（如打开程序、移动鼠标、读写文件、上网购物等），你必须使用提供的工具来完成，不要用文字描述如何操作。如果用户只是普通聊天或提问，则直接回答。"
                : @"你是一个叫做Lumina的AI助手，你的职责是与用户进行自然语言对话。";
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
            int maxIterations = 10;
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
                    ["max_tokens"] = AppConfig.MaxResponseTokens,
                    ["stream"] = false
                };

                if (allowControl && _isMcpReady)
                {
                    requestBody["tools"] = BuildToolsJson();
                }

                ConsoleHelper.Info("========== 请求（发送给模型） ==========");
                ConsoleHelper.Info(requestBody.ToString(Formatting.Indented));
                ConsoleHelper.Info("==========================================");

                JObject response;
                try
                {
                    response = await PostChatCompletionAsync(requestBody);
                }
                catch (Exception ex)
                {
                    ConsoleHelper.Error($"调用模型失败: {ex.Message}");
                    return $"错误: {ex.Message}";
                }

                var choice = response["choices"]?[0];
                if (choice == null)
                {
                    ConsoleHelper.Error("模型返回异常：无 choices");
                    return "模型返回异常。";
                }
                var message = choice["message"];
                var content = message["content"]?.ToString() ?? "";
                var toolCalls = message["tool_calls"] as JArray;

                ConsoleHelper.Info("========== 响应（来自模型） ==========");
                ConsoleHelper.Info(response.ToString(Formatting.Indented));
                ConsoleHelper.Info("=======================================");

                if (toolCalls != null && toolCalls.Count > 0)
                {
                    if (!allowControl)
                    {
                        ConsoleHelper.Warning("模型请求了工具调用，但用户未允许操控。已忽略工具调用，请重新输入。");
                        return "AI 尝试使用工具但被拒绝。";
                    }

                    isControlMode = true;

                    if (isFirstToolCall)
                    {
                        ConsoleHelper.Warning("即将执行电脑操控操作，是否继续？(y/n)");
                        var key = Program.SafeReadKey();
                        Console.WriteLine();
                        if (key.KeyChar != 'y' && key.KeyChar != 'Y')
                        {
                            ConsoleHelper.Info("用户取消了操作。");
                            userCancelled = true;
                            string cancelMsg = "用户取消了操作。";
                            _context.AddMessage("user", userInput);
                            _context.AddMessage("assistant", cancelMsg);
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
                            ConsoleHelper.Warning($"即将执行危险操作：{toolName}，参数：{argsJson}，是否继续？(y/n)");
                            var key = Program.SafeReadKey();
                            Console.WriteLine();
                            if (key.KeyChar != 'y' && key.KeyChar != 'Y')
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

                        ConsoleHelper.Success($"正在执行：{toolName}，参数：{argsJson}");

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
                            ConsoleHelper.Info($"执行结果：{resultContent}");
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
                            ConsoleHelper.Error($"执行 {toolName} 失败：{ex.Message}");
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
                    return finalMsg;
                }
                else
                {
                    // 应用后处理（Miya 风格转换），上下文记录最终输出（角色本身不记录）
                    string final = content;
                    if (postProcess != null) final = await postProcess(content);
                    _context.AddMessage("user", userInput);
                    _context.AddMessage("assistant", final);
                    if (AppConfig.EnableSemanticCache)
                        _cache.AddEntry(userInput, final);
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
                    ConsoleHelper.Warning("MCP 客户端释放超时（子进程可能未响应），已跳过。");
                    // 若 disposeTask 仍在运行（后台），等待一小段时间后强制终止进程，避免退出挂起
                    var finish = await Task.WhenAny(disposeTask, Task.Delay(3000));
                    ConsoleHelper.Warning($"MCP 释放任务状态: {(disposeTask.IsCompleted ? "完成" : disposeTask.Status)} ");
                    if (finish != disposeTask)
                    {
                        ConsoleHelper.Warning("MCP 释放任务仍挂起，强制退出。");
                        Environment.Exit(0);
                    }
                }
            }
            ConsoleHelper.Warning("DisposeAsync 完成");
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

    // ===================================================================
    // 控制台入口（支持模式切换命令）
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

            // 初始模式（可从命令行参数读取，或默认平衡）
            var initialMode = AppConfig.DefaultMode;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mode" && i + 1 < args.Length)
                {
                    if (Enum.TryParse<AppConfig.ModelMode>(args[i + 1], true, out var mode))
                        initialMode = mode;
                    break;
                }
            }

            // 历史文件参数
            string historyFilePath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--history" && i + 1 < args.Length)
                {
                    historyFilePath = args[++i];
                    break;
                }
            }

            // 注意：不要注册 ProcessExit -> Environment.Exit 的处理器（Environment.Exit 会再次触发 ProcessExit，导致挂起）
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Environment.Exit(0);
            };

            // 创建服务
            await using var service = new LlamaChatService(initialMode);

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
                        if (Enum.TryParse<AppConfig.ModelMode>(parts[1], true, out var newMode))
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
                    string templateReply = service.TryGetTemplateReply(input, role);
                    if (templateReply != null)
                    {
                        // 模板回复也按现有规则记录上下文（user/assistant，不记录角色）
                        service.RecordTemplateReply(input, templateReply);
                        ConsoleHelper.UserContent($"助手: {templateReply}");
                        Console.WriteLine();
                        continue;
                    }

                    // 仅 Miya-Bonsai 时做语言风格转换；Ewin 直接输出
                    Func<string, Task<string>>? post = null;
                    if (role == LlamaChatService.ChatRole.MiyaBonsai)
                        post = text => service.ConvertStyleAsync(text);

                    string reply = await service.SendMessageAsync(input, post);
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
}