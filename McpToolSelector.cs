using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;

namespace LlamaChat
{
    // ===================================================================
    // MCP 工具智能选择器
    // 目标：不再把全部 ~60 个工具（约 9300 tokens）每次发给模型，而是：
    //   1) 根据当前输入 + 最近上下文，用关键词/词法匹配（毫秒级，无额外 LLM 调用）
    //      快速推断最可能需要的 N 个工具（默认 4），仅发送这 N 个的完整参数 schema，
    //      另附 2 个元工具：list_tools（全部工具概述）与 get_tool_usage（单工具用法）。
    //   2) 若所需工具不在预选列表，模型调用 list_tools 拿到"名称+一行用途"的全部概述；
    //   3) 选定工具后调用 get_tool_usage 拿到完整参数说明，再调用该工具。
    // 元工具由宿主（Program.cs）本地处理，不转发给 MCP 服务器。
    // ===================================================================
    public class McpToolSelector
    {
        // 无任何关键词命中时的兜底常用工具（覆盖面最广的一组）
        private static readonly string[] FallbackTools =
            { "launch", "file_read", "file_search", "powershell" };

        // 同分时优先排前的常用工具（越靠前越优先）
        private static readonly string[] PriorityTools =
        {
            "launch", "file_read", "file_write", "file_search", "file_info", "file_manage",
            "powershell", "process", "screenshot", "click", "type", "window", "clipboard",
            "system_info", "network", "registry_get", "registry_set", "disk_inspect", "archive", "audio",
            "find_element", "interact_element", "get_state", "key", "shortcut", "service",
            "scheduled_task", "startup_report", "event_log", "wmi_query", "env", "scrape", "http_request",
            "verify_signature", "file_hash", "integrity", "security_audit", "defender_status", "cert_store"
        };

        // 英文停用词（描述索引与查询匹配时忽略，减少噪声）
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "to", "in", "on", "for", "with", "and", "or", "by", "at",
            "from", "is", "are", "be", "it", "its", "as", "that", "this", "your", "you", "then",
            "than", "was", "were", "will", "can", "not", "no", "all", "any", "each", "does", "do"
        };

        // 工具分类（仅用于概述分组展示，与"提供的工具.txt"分类保持一致）
        private static readonly (string Zh, string En, string[] Tools)[] Categories =
        {
            ("输入与控制", "Input & Control",
                new[] { "click", "drag", "type", "key", "shortcut", "scroll", "clipboard", "hover", "audio" }),
            ("屏幕与窗口", "Screen & Window",
                new[] { "screenshot", "ocr", "window", "launch", "focus", "switch_to_window", "file_dialog", "multi_monitor" }),
            ("UI 自动化", "UI Automation",
                new[] { "find_element", "get_element", "get_state", "get_text", "get_table", "interact_element", "assert_element", "wait_for" }),
            ("进程与 Shell", "Process & Shell",
                new[] { "process", "process_inspect", "service", "scheduled_task", "powershell", "start_process" }),
            ("文件与磁盘", "File & Disk",
                new[] { "file_search", "file_read", "file_write", "file_info", "file_manage", "file_hash", "file_streams", "fs_changes", "archive", "disk_inspect", "storage_health", "watch" }),
            ("系统与安全", "System & Security",
                new[] { "system_info", "notification", "power_action", "verify_signature", "defender_status", "security_audit", "registry_get", "registry_set", "event_log", "reliability", "driver_list", "startup_report", "wmi_query", "integrity", "cert_store", "env" }),
            ("网络与 Web", "Network & Web",
                new[] { "network", "firewall", "scrape", "http_request" }),
        };

        // 人工关键词 → 候选工具（中英文；命中越多分数越高，用于提高中文指令的准确率）
        private static readonly (string Keyword, string[] Tools)[] KeywordMap =
        {
            // ---- 输入与控制 ----
            ("截图", new[] { "screenshot", "ocr" }),
            ("截屏", new[] { "screenshot" }),
            ("屏幕", new[] { "screenshot", "ocr", "multi_monitor" }),
            ("screen", new[] { "screenshot", "ocr", "multi_monitor" }),
            ("显示器", new[] { "multi_monitor", "screenshot" }),
            ("多屏", new[] { "multi_monitor" }),
            ("monitor", new[] { "watch", "multi_monitor" }),
            ("点击", new[] { "click" }),
            ("双击", new[] { "click" }),
            ("右键", new[] { "click" }),
            ("click", new[] { "click" }),
            ("double click", new[] { "click" }),
            ("right click", new[] { "click" }),
            ("鼠标", new[] { "click", "hover", "drag", "scroll" }),
            ("mouse", new[] { "click", "hover", "drag", "scroll" }),
            ("悬停", new[] { "hover" }),
            ("hover", new[] { "hover" }),
            ("拖拽", new[] { "drag" }),
            ("拖动", new[] { "drag" }),
            ("drag", new[] { "drag" }),
            ("输入", new[] { "type", "interact_element", "file_dialog" }),
            ("打字", new[] { "type" }),
            ("键入", new[] { "type" }),
            ("type", new[] { "type", "interact_element" }),
            ("按键", new[] { "key", "shortcut" }),
            ("快捷键", new[] { "shortcut", "key" }),
            ("key", new[] { "key", "shortcut" }),
            ("shortcut", new[] { "shortcut", "key" }),
            ("press", new[] { "key", "shortcut" }),
            ("滚动", new[] { "scroll" }),
            ("scroll", new[] { "scroll" }),
            ("复制", new[] { "clipboard", "file_manage" }),
            ("复制文件", new[] { "file_manage" }),
            ("粘贴", new[] { "clipboard" }),
            ("剪切板", new[] { "clipboard" }),
            ("剪贴板", new[] { "clipboard" }),
            ("clipboard", new[] { "clipboard" }),
            ("copy", new[] { "clipboard", "file_manage" }),
            ("paste", new[] { "clipboard" }),
            ("截取", new[] { "screenshot" }),
            // ---- 屏幕与窗口 ----
            ("打开", new[] { "launch", "start_process", "file_read" }),
            ("启动", new[] { "launch", "start_process", "scheduled_task" }),
            ("运行", new[] { "launch", "start_process", "powershell" }),
            ("程序", new[] { "launch", "start_process", "process" }),
            ("应用", new[] { "launch", "start_process" }),
            ("软件", new[] { "launch", "start_process" }),
            ("open", new[] { "launch", "start_process", "file_read" }),
            ("launch", new[] { "launch", "start_process" }),
            ("start", new[] { "launch", "start_process", "scheduled_task", "service" }),
            ("run", new[] { "launch", "start_process", "powershell" }),
            ("execute", new[] { "powershell", "start_process", "scheduled_task" }),
            ("app", new[] { "launch", "start_process" }),
            ("program", new[] { "launch", "start_process", "process" }),
            ("application", new[] { "launch", "start_process" }),
            ("窗口", new[] { "window", "focus", "switch_to_window", "get_state" }),
            ("window", new[] { "window", "focus", "switch_to_window" }),
            ("聚焦", new[] { "focus", "switch_to_window" }),
            ("focus", new[] { "focus", "switch_to_window" }),
            ("最小化", new[] { "window" }),
            ("最大化", new[] { "window" }),
            ("minimize", new[] { "window" }),
            ("maximize", new[] { "window" }),
            ("关闭窗口", new[] { "window" }),
            ("关闭", new[] { "window", "process", "service", "power_action" }),
            ("close", new[] { "window", "process", "service", "power_action" }),
            ("对话框", new[] { "file_dialog" }),
            ("dialog", new[] { "file_dialog" }),
            // ---- UI 自动化 ----
            ("元素", new[] { "find_element", "get_element", "get_state", "interact_element", "assert_element", "wait_for" }),
            ("界面", new[] { "get_state", "find_element", "get_element", "ocr", "screenshot" }),
            ("ui", new[] { "find_element", "get_element", "interact_element", "get_state" }),
            ("element", new[] { "find_element", "get_element", "get_state", "interact_element", "assert_element", "wait_for" }),
            ("等待", new[] { "wait_for" }),
            ("wait", new[] { "wait_for" }),
            ("表格", new[] { "get_table" }),
            ("table", new[] { "get_table" }),
            ("文本", new[] { "get_text", "ocr", "file_read" }),
            ("text", new[] { "get_text", "ocr", "file_read" }),
            // ---- 进程与 Shell ----
            ("进程", new[] { "process", "process_inspect" }),
            ("process", new[] { "process", "process_inspect" }),
            ("pid", new[] { "process", "process_inspect" }),
            ("杀掉", new[] { "process" }),
            ("结束", new[] { "process", "service", "window" }),
            ("kill", new[] { "process" }),
            ("任务管理器", new[] { "process" }),
            ("task manager", new[] { "process" }),
            ("服务", new[] { "service" }),
            ("service", new[] { "service" }),
            ("计划任务", new[] { "scheduled_task" }),
            ("scheduled", new[] { "scheduled_task" }),
            ("task", new[] { "scheduled_task" }),
            ("powershell", new[] { "powershell" }),
            ("ps", new[] { "powershell" }),
            ("命令", new[] { "powershell", "wmi_query", "start_process" }),
            ("command", new[] { "powershell", "wmi_query", "start_process" }),
            ("执行", new[] { "powershell", "start_process", "scheduled_task" }),
            ("shell", new[] { "powershell" }),
            ("脚本", new[] { "powershell" }),
            ("script", new[] { "powershell" }),
            // ---- 文件与磁盘 ----
            ("文件", new[] { "file_read", "file_write", "file_info", "file_search", "file_manage", "file_hash" }),
            ("files", new[] { "file_search", "file_read", "file_info" }),
            ("file", new[] { "file_read", "file_write", "file_info", "file_search", "file_manage", "file_hash" }),
            ("文件夹", new[] { "file_search", "file_info", "file_manage", "file_dialog" }),
            ("目录", new[] { "file_search", "file_info", "disk_inspect" }),
            ("folder", new[] { "file_search", "file_info", "file_manage", "file_dialog" }),
            ("directory", new[] { "file_search", "file_info", "disk_inspect" }),
            ("读取", new[] { "file_read" }),
            ("读写", new[] { "file_read", "file_write" }),
            ("写入", new[] { "file_write" }),
            ("read", new[] { "file_read" }),
            ("write", new[] { "file_write" }),
            ("保存", new[] { "file_write", "clipboard" }),
            ("save", new[] { "file_write" }),
            ("新建", new[] { "file_write", "scheduled_task" }),
            ("创建", new[] { "file_write", "scheduled_task", "registry_set" }),
            ("create", new[] { "file_write", "scheduled_task", "registry_set" }),
            ("搜索", new[] { "file_search", "find_element" }),
            ("查找", new[] { "file_search", "find_element", "registry_get" }),
            ("search", new[] { "file_search", "find_element" }),
            ("find", new[] { "file_search", "find_element" }),
            ("删除", new[] { "file_manage", "process", "registry_set", "scheduled_task" }),
            ("delete", new[] { "file_manage", "process", "registry_set", "scheduled_task" }),
            ("remove", new[] { "file_manage", "process", "firewall", "scheduled_task" }),
            ("移动", new[] { "file_manage" }),
            ("move", new[] { "file_manage" }),
            ("rename", new[] { "file_manage" }),
            ("压缩", new[] { "archive" }),
            ("解压", new[] { "archive" }),
            ("zip", new[] { "archive" }),
            ("unzip", new[] { "archive" }),
            ("archive", new[] { "archive" }),
            ("compress", new[] { "archive" }),
            ("磁盘", new[] { "disk_inspect", "storage_health" }),
            ("硬盘", new[] { "storage_health", "disk_inspect" }),
            ("存储", new[] { "storage_health", "disk_inspect" }),
            ("disk", new[] { "disk_inspect", "storage_health" }),
            ("drive", new[] { "disk_inspect", "storage_health" }),
            ("storage", new[] { "storage_health", "disk_inspect" }),
            ("空间", new[] { "disk_inspect" }),
            ("space", new[] { "disk_inspect" }),
            ("回收站", new[] { "disk_inspect" }),
            ("recycle", new[] { "disk_inspect" }),
            ("缓存", new[] { "disk_inspect" }),
            ("cache", new[] { "disk_inspect" }),
            ("临时文件", new[] { "disk_inspect" }),
            ("temp", new[] { "disk_inspect" }),
            ("容量", new[] { "disk_inspect", "storage_health" }),
            ("健康", new[] { "storage_health" }),
            ("health", new[] { "storage_health" }),
            ("smart", new[] { "storage_health" }),
            ("usage", new[] { "disk_inspect" }),
            ("大文件", new[] { "file_search", "disk_inspect" }),
            // ---- 系统与安全 ----
            ("系统信息", new[] { "system_info", "wmi_query" }),
            ("系统", new[] { "system_info", "wmi_query", "security_audit", "event_log" }),
            ("电脑", new[] { "system_info", "wmi_query" }),
            ("system", new[] { "system_info", "wmi_query", "security_audit", "event_log" }),
            ("wmi", new[] { "wmi_query" }),
            ("info", new[] { "system_info", "file_info" }),
            ("通知", new[] { "notification" }),
            ("提示", new[] { "notification" }),
            ("notification", new[] { "notification" }),
            ("notify", new[] { "notification" }),
            ("音量", new[] { "audio" }),
            ("声音", new[] { "audio" }),
            ("静音", new[] { "audio" }),
            ("audio", new[] { "audio" }),
            ("mute", new[] { "audio" }),
            ("unmute", new[] { "audio" }),
            ("sound", new[] { "audio" }),
            ("volume", new[] { "audio" }),
            ("电源", new[] { "power_action" }),
            ("关机", new[] { "power_action" }),
            ("重启", new[] { "power_action" }),
            ("注销", new[] { "power_action" }),
            ("锁屏", new[] { "power_action" }),
            ("睡眠", new[] { "power_action" }),
            ("休眠", new[] { "power_action" }),
            ("power", new[] { "power_action" }),
            ("shutdown", new[] { "power_action" }),
            ("reboot", new[] { "power_action" }),
            ("restart", new[] { "power_action", "service" }),
            ("logoff", new[] { "power_action" }),
            ("lock", new[] { "power_action" }),
            ("sleep", new[] { "power_action" }),
            ("hibernate", new[] { "power_action" }),
            ("签名", new[] { "verify_signature" }),
            ("验证", new[] { "verify_signature", "file_hash" }),
            ("正版", new[] { "verify_signature", "cert_store" }),
            ("核实", new[] { "verify_signature", "file_hash" }),
            ("signature", new[] { "verify_signature" }),
            ("证书", new[] { "cert_store", "verify_signature" }),
            ("certificate", new[] { "cert_store", "verify_signature" }),
            ("cert", new[] { "cert_store" }),
            ("杀毒", new[] { "defender_status" }),
            ("defender", new[] { "defender_status" }),
            ("antivirus", new[] { "defender_status" }),
            ("安全", new[] { "security_audit", "defender_status", "firewall" }),
            ("security", new[] { "security_audit", "defender_status", "firewall" }),
            ("审计", new[] { "security_audit" }),
            ("audit", new[] { "security_audit" }),
            ("注册表", new[] { "registry_get", "registry_set" }),
            ("registry", new[] { "registry_get", "registry_set" }),
            ("环境变量", new[] { "env" }),
            ("env", new[] { "env" }),
            ("environment", new[] { "env" }),
            ("日志", new[] { "event_log", "reliability" }),
            ("事件日志", new[] { "event_log" }),
            ("log", new[] { "event_log", "reliability" }),
            ("event", new[] { "event_log" }),
            ("崩溃", new[] { "reliability" }),
            ("蓝屏", new[] { "reliability" }),
            ("crash", new[] { "reliability" }),
            ("bsod", new[] { "reliability" }),
            ("minidump", new[] { "reliability" }),
            ("驱动", new[] { "driver_list" }),
            ("driver", new[] { "driver_list" }),
            ("启动项", new[] { "startup_report" }),
            ("自启动", new[] { "startup_report" }),
            ("开机自启", new[] { "startup_report" }),
            ("开机", new[] { "startup_report", "scheduled_task", "launch" }),
            ("自动启动", new[] { "startup_report", "scheduled_task" }),
            ("恶意软件", new[] { "startup_report", "security_audit", "verify_signature", "defender_status", "integrity" }),
            ("木马", new[] { "verify_signature", "integrity", "security_audit", "startup_report" }),
            ("可疑", new[] { "verify_signature", "startup_report", "integrity", "security_audit", "process" }),
            ("查杀", new[] { "defender_status", "security_audit" }),
            ("安全扫描", new[] { "security_audit", "defender_status" }),
            ("startup", new[] { "startup_report" }),
            ("boot", new[] { "startup_report" }),
            ("哈希", new[] { "file_hash" }),
            ("校验", new[] { "file_hash", "verify_signature" }),
            ("hash", new[] { "file_hash" }),
            ("md5", new[] { "file_hash" }),
            ("sha", new[] { "file_hash" }),
            ("完整性", new[] { "integrity" }),
            ("篡改", new[] { "integrity" }),
            ("integrity", new[] { "integrity" }),
            ("tripwire", new[] { "integrity" }),
            ("监听", new[] { "watch" }),
            ("监控", new[] { "watch", "event_log" }),
            ("watch", new[] { "watch" }),
            ("usn", new[] { "fs_changes" }),
            ("变更日志", new[] { "fs_changes" }),
            ("journal", new[] { "fs_changes" }),
            ("流", new[] { "file_streams" }),
            ("ads", new[] { "file_streams" }),
            ("stream", new[] { "file_streams" }),
            // ---- 网络与 Web ----
            ("网络", new[] { "network" }),
            ("无线", new[] { "network" }),
            ("代理", new[] { "registry_get", "network" }),
            ("proxy", new[] { "registry_get", "network" }),
            ("服务器", new[] { "network", "system_info", "service" }),
            ("network", new[] { "network" }),
            ("wifi", new[] { "network" }),
            ("wireless", new[] { "network" }),
            ("ping", new[] { "network" }),
            ("dns", new[] { "network" }),
            ("ip", new[] { "network", "system_info" }),
            ("防火墙", new[] { "firewall" }),
            ("firewall", new[] { "firewall" }),
            ("网页", new[] { "scrape", "http_request" }),
            ("网址", new[] { "scrape", "http_request" }),
            ("抓取", new[] { "scrape" }),
            ("爬取", new[] { "scrape" }),
            ("web", new[] { "scrape", "http_request" }),
            ("url", new[] { "scrape", "http_request" }),
            ("http", new[] { "http_request", "scrape" }),
            ("scrape", new[] { "scrape" }),
            ("下载", new[] { "http_request" }),
            ("上传", new[] { "http_request" }),
            ("download", new[] { "http_request" }),
            ("upload", new[] { "http_request" }),
        };

        private readonly List<McpClientTool> _tools;
        private readonly Dictionary<string, McpClientTool> _byName;
        private readonly Dictionary<string, HashSet<string>> _index;
        private readonly Dictionary<string, int> _priority;
        private readonly Func<AppLanguage> _languageGetter;

        public McpToolSelector(List<McpClientTool> tools, Func<AppLanguage> languageGetter)
        {
            _tools = tools ?? new List<McpClientTool>();
            _languageGetter = languageGetter ?? (() => AppLanguage.Auto);
            _byName = new Dictionary<string, McpClientTool>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _tools)
                if (t?.Name != null) _byName[t.Name] = t;

            _priority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < PriorityTools.Length; i++)
                if (_byName.ContainsKey(PriorityTools[i])) _priority[PriorityTools[i]] = i;

            _index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            // 1) 工具名（含下划线拆分：file_read → file_read / file / read）
            foreach (var name in _byName.Keys)
            {
                AddToIndex(name, name);
                foreach (var part in Regex.Split(name, "[_\\- ]"))
                    if (part.Length > 1) AddToIndex(part, name);
            }
            // 2) 描述中的英文单词（broad 覆盖，避免漏选；停用词除外）
            foreach (var t in _tools)
            {
                if (string.IsNullOrEmpty(t.Description)) continue;
                foreach (var tok in Tokenize(t.Description))
                    if (!StopWords.Contains(tok)) AddToIndex(tok, t.Name);
            }
            // 3) 人工关键词映射（中英文，重点提升中文指令准确率）
            foreach (var (kw, names) in KeywordMap)
                foreach (var n in names)
                    AddToIndex(kw, n);
        }

        private void AddToIndex(string key, string toolName)
        {
            if (string.IsNullOrEmpty(key) || !_byName.ContainsKey(toolName)) return;
            AddKeyToIndex(key, toolName);

            // 中文长关键词（≥3 字，如“开机自启”“注册表”）：补充全部二元组，
            // 使查询分词（单字+二元组）也能命中。
            var cjk = key.Where(c => c >= 0x4e00 && c <= 0x9fff).ToList();
            if (cjk.Count >= 3)
            {
                for (int i = 0; i + 1 < cjk.Count; i++)
                    AddKeyToIndex(string.Concat(cjk[i], cjk[i + 1]), toolName);
            }
        }

        private void AddKeyToIndex(string key, string toolName)
        {
            if (!_index.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _index[key] = set;
            }
            set.Add(toolName);
        }

        // ---- 分词：英文单词（≥2 字符）+ 中文单字与相邻二元组 ----
        private static IEnumerable<string> Tokenize(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            string lower = text.ToLowerInvariant();
            foreach (Match m in Regex.Matches(lower, @"[a-z0-9_]+"))
            {
                if (m.Value.Length < 2) continue;
                yield return m.Value;
            }
            var cjk = lower.Where(c => c >= 0x4e00 && c <= 0x9fff).ToList();
            for (int i = 0; i < cjk.Count; i++)
            {
                yield return cjk[i].ToString();
                if (i + 1 < cjk.Count)
                    yield return string.Concat(cjk[i], cjk[i + 1]);
            }
        }

        // ---- 推断最可能需要的 N 个工具（纯词法打分，毫秒级） ----
        public List<McpClientTool> SelectTopTools(string query, int count)
        {
            count = Math.Max(1, count);
            var score = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var explicitNames = new List<string>();

            foreach (var token in Tokenize(query))
            {
                // 忽略纯数字与停用词（它们是参数而非工具指示词）
                if (token.Length >= 2 && token.All(char.IsDigit)) continue;
                if (StopWords.Contains(token)) continue;

                // 显式提到工具名（如 file_read）→ 必定入选
                if (_byName.ContainsKey(token) && !explicitNames.Contains(token))
                    explicitNames.Add(token);

                if (_index.TryGetValue(token, out var names))
                {
                    foreach (var n in names)
                        score[n] = score.TryGetValue(n, out var v) ? v + 1 : 1;
                }
            }

            var result = new List<McpClientTool>();
            foreach (var n in explicitNames)
                if (_byName.TryGetValue(n, out var t)) result.Add(t);

            foreach (var kv in score.OrderByDescending(kv => kv.Value)
                                    .ThenBy(kv => _priority.TryGetValue(kv.Key, out var p) ? p : int.MaxValue)
                                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (result.Count >= count) break;
                if (result.Any(r => r.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                if (_byName.TryGetValue(kv.Key, out var t)) result.Add(t);
            }

            // 无命中 → 兜底常用工具
            foreach (var n in FallbackTools)
            {
                if (result.Count >= count) break;
                if (result.Any(r => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
                if (_byName.TryGetValue(n, out var t)) result.Add(t);
            }

            // 极端情况：从全部工具补齐
            if (result.Count < count)
                foreach (var t in _tools)
                {
                    if (result.Count >= count) break;
                    if (!result.Contains(t)) result.Add(t);
                }

            return result.Take(count).ToList();
        }

        // ---- 全部工具概述（仅名称 + 一行用途，按分类分组；动态生成以跟随语言切换） ----
        public string GetOverview()
        {
            bool zh = I18n.IsChinese(_languageGetter());
            var sb = new StringBuilder();
            sb.AppendLine(zh
                ? "全部可用工具（名称 + 一行用途，不含参数）。请从中挑选一个最合适的工具名："
                : "All available tools (name + one-line purpose, no parameters). Pick the most suitable tool name:");

            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (zhc, enc, names) in Categories)
            {
                var lines = names
                    .Where(n => _byName.ContainsKey(n))
                    .Select(n => FormatOverviewLine(n))
                    .Where(l => l != null)
                    .ToList();
                if (lines.Count == 0) continue;
                foreach (var n in names) placed.Add(n);
                sb.AppendLine();
                sb.AppendLine("◆ " + (zh ? zhc : enc));
                foreach (var l in lines) sb.AppendLine(l!);
            }

            var rest = _byName.Keys.Where(n => !placed.Contains(n)).ToList();
            if (rest.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(zh ? "◆ 其他" : "◆ Others");
                foreach (var n in rest)
                {
                    var l = FormatOverviewLine(n);
                    if (l != null) sb.AppendLine(l);
                }
            }
            return sb.ToString();
        }

        private string? FormatOverviewLine(string toolName)
        {
            if (!_byName.TryGetValue(toolName, out var tool)) return null;
            return $"- {tool.Name}: {Shorten(tool.Description ?? "")}";
        }

        // 截断描述为第一句（容错：'.' 仅在空格/结尾前才视为句号，避免截断路径/URL）
        private static string Shorten(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return "";
            int cut = text.Length;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool isEnd = i == text.Length - 1;
                if (c == '。' || c == '；' || c == '\n' || c == ';' ||
                    (c == '.' && (isEnd || char.IsWhiteSpace(text[i + 1]))))
                {
                    cut = i;
                    break;
                }
            }
            string first = text.Substring(0, cut).Trim();
            const int max = 90;
            if (first.Length > max) first = first.Substring(0, max).TrimEnd() + "…";
            return first;
        }

        // ---- 单个工具的完整用法（用途 + JSON Schema） ----
        public string? GetUsage(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;
            if (!_byName.TryGetValue(toolName.Trim(), out var tool)) return null;

            bool zh = I18n.IsChinese(_languageGetter());
            var sb = new StringBuilder();
            sb.AppendLine(zh ? $"工具：{tool.Name}" : $"Tool: {tool.Name}");
            if (!string.IsNullOrEmpty(tool.Description))
                sb.AppendLine((zh ? "用途：" : "Purpose: ") + tool.Description);
            sb.AppendLine(zh ? "参数（JSON Schema）：" : "Parameters (JSON Schema):");
            sb.AppendLine(tool.JsonSchema.GetRawText());
            return sb.ToString();
        }

        // ---- 注入系统提示词的工具使用引导：列出预选工具（名称+用途）并说明回退路径 ----
        public string GetSelectionInstructions(IReadOnlyList<McpClientTool> selected)
        {
            bool zh = I18n.IsChinese(_languageGetter());
            var sb = new StringBuilder();
            sb.AppendLine(zh
                ? $"已按当前请求预选以下 {selected.Count} 个最可能需要的工具："
                : $"Pre-selected the {selected.Count} tools most likely needed for this request:");
            foreach (var t in selected)
                sb.AppendLine($"- {t.Name}: {Shorten(t.Description ?? "")}");
            sb.AppendLine(zh
                ? "提示：参数值请用英文可执行名或真实路径（如打开记事本 → app_name=\"notepad\"），不要用中文界面名称。"
                : "Tip: use English executable names or real paths for parameter values (e.g. app_name=\"notepad\" to open Notepad), not localized display names.");
            sb.Append(zh
                ? "请优先从上面选择最合适的工具完成操作。若所需工具不在预选列表中，请按以下步骤：\n"
                : "Prefer the most suitable tool above to complete the task. If the tool you need is not among them, follow these steps:\n");
            sb.Append(zh
                ? "1) 调用 list_tools 查看全部工具的概述（仅名称与用途，不含参数）；\n" +
                  "2) 找到合适工具后，调用 get_tool_usage（参数 tool_name 填工具名）获取其完整参数说明；\n" +
                  "3) 然后直接调用该工具完成操作。"
                : "1) Call list_tools to see an overview of ALL tools (name + purpose only, no parameters);\n" +
                  "2) Once you find a suitable tool, call get_tool_usage with the tool_name parameter to get its full parameter docs;\n" +
                  "3) Then call that tool to complete the task.");
            return sb.ToString();
        }

        public int ToolCount => _tools.Count;

        /// <summary>工具名是否为已知工具（含两个元工具）。用于文本格式工具调用的可信校验。</summary>
        public bool IsKnownTool(string toolName)
        {
            return !string.IsNullOrEmpty(toolName)
                   && (_byName.ContainsKey(toolName)
                       || toolName.Equals("list_tools", StringComparison.OrdinalIgnoreCase)
                       || toolName.Equals("get_tool_usage", StringComparison.OrdinalIgnoreCase));
        }
    }
}
