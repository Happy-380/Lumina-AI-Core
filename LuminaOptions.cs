using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LlamaChat
{
    // ===================================================================
    // 日志级别（与控制台颜色方法一一对应）
    // ===================================================================
    public enum LogLevel
    {
        Info,
        Warning,
        Success,
        Error,
        Prompt
    }

    // ===================================================================
    // 模型模式（Fast 1.7B / Balanced 4B / Quality 8B）
    // ===================================================================
    public enum ModelMode
    {
        Fast,
        Balanced,
        Quality
    }

    // ===================================================================
    // 可调配置（语言风格转换器相关配置除外，风格转换参数由 StyleTransferService 内部管理）
    //
    // 宿主程序在创建 LlamaChatService 之前自由设置本类属性，
    // 即可替代原 AppConfig 静态常量对服务行为的调控。
    // ===================================================================
    public class LuminaOptions
    {
        // ---- 模型与上下文 ----
        public ModelMode InitialMode { get; set; } = AppConfig.DefaultMode;
        public int? ManualContextSize { get; set; } = AppConfig.ManualContextSize > 0 ? AppConfig.ManualContextSize : null;

        // ---- 语言（Auto = 跟随系统语言，仅支持中 / 英文） ----
        public AppLanguage Language { get; set; } = AppLanguage.Auto;

        // ---- 生成参数 ----
        public int MaxResponseTokens { get; set; } = AppConfig.MaxResponseTokens;
        public int ReserveTokens { get; set; } = AppConfig.ReserveTokens;
        public double CharPerToken { get; set; } = AppConfig.CharPerToken;

        // ---- 语义缓存 ----
        public bool EnableSemanticCache { get; set; } = AppConfig.EnableSemanticCache;
        public double SimilarityThreshold { get; set; } = AppConfig.SimilarityThreshold;
        public int MaxCacheEntries { get; set; } = AppConfig.MaxCacheEntries;

        // ---- 历史检索 ----
        public int HistoryRetrievalTopK { get; set; } = AppConfig.HistoryRetrievalTopK;

        // ---- 相关性判断 ----
        public int RelevanceCheckRounds { get; set; } = AppConfig.RelevanceCheckRounds;
        public double RelevanceThreshold { get; set; } = AppConfig.RelevanceThreshold;

        // ---- 工具调用 ----
        public int MaxToolCallIterations { get; set; } = 10;

        // ---- 目录 / 可执行文件 ----
        public string LlamaFolderName { get; set; } = AppConfig.LlamaFolderName;
        public string McpFolderName { get; set; } = AppConfig.McpFolderName;
        public string McpExeName { get; set; } = AppConfig.McpExeName;

        // ---- 模型文件 / 端口映射（null = 使用 AppConfig 默认值） ----
        public IReadOnlyDictionary<ModelMode, string>? ModelFiles { get; set; }
        public IReadOnlyDictionary<ModelMode, int>? ModelPorts { get; set; }

        // ---- 系统提示词（null = 使用内置默认） ----
        /// <summary>普通对话模式（不允许操控电脑）的系统提示词。</summary>
        public string? SystemPrompt { get; set; }
        /// <summary>允许 AI 操控电脑时的系统提示词。</summary>
        public string? ControlSystemPrompt { get; set; }

        // ---- 回调（UI 无关，库模式下由宿主注入；未设置时采用安全默认） ----
        /// <summary>
        /// 用户确认回调（是否允许操控 / 危险操作确认）。参数为提示文本，返回 true = 允许。
        /// 未设置时默认拒绝（安全）。
        /// </summary>
        public Func<string, Task<bool>>? ConfirmCallback { get; set; }

        /// <summary>日志回调。未设置时不输出。</summary>
        public Action<LogLevel, string>? LogCallback { get; set; }
    }
}
