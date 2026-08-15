using System;
using System.Globalization;

namespace LlamaChat
{
    // ===================================================================
    // 语言设置（Auto = 跟随系统语言）
    // ===================================================================
    public enum AppLanguage
    {
        Auto,
        Chinese,
        English
    }

    // ===================================================================
    // 中英文双语工具（控制台界面 + 服务消息）
    // 仅支持中文 / 英文两种语言；Auto 时按系统当前语言自动选择。
    // ===================================================================
    public static class I18n
    {
        /// <summary>检测系统语言：zh* → Chinese，其余 → English</summary>
        public static AppLanguage DetectSystemLanguage()
        {
            string name = CultureInfo.CurrentUICulture?.Name
                          ?? CultureInfo.CurrentCulture?.Name
                          ?? "en";
            return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.Chinese
                : AppLanguage.English;
        }

        /// <summary>将 Auto 解析为实际语言；非 Auto 原样返回</summary>
        public static AppLanguage Resolve(AppLanguage setting)
            => setting == AppLanguage.Auto ? DetectSystemLanguage() : setting;

        /// <summary>是否使用中文（Auto 时按系统语言判定）</summary>
        public static bool IsChinese(AppLanguage setting)
            => Resolve(setting) == AppLanguage.Chinese;

        /// <summary>按语言返回中 / 英文文本</summary>
        public static string T(AppLanguage setting, string zh, string en)
            => IsChinese(setting) ? zh : en;

        /// <summary>按语言返回格式化文本（同 string.Format）</summary>
        public static string T(AppLanguage setting, string zhFormat, string enFormat, params object[] args)
            => string.Format(T(setting, zhFormat, enFormat), args);

        /// <summary>解析语言字符串："zh"/"chinese"/"cn" → Chinese，"en"/"english" → English，"auto"/"system" → Auto</summary>
        public static bool TryParse(string text, out AppLanguage language)
        {
            language = AppLanguage.Auto;
            if (string.IsNullOrWhiteSpace(text)) return false;

            switch (text.Trim().ToLowerInvariant())
            {
                case "zh":
                case "cn":
                case "chinese":
                case "中文":
                    language = AppLanguage.Chinese;
                    return true;
                case "en":
                case "english":
                case "英文":
                    language = AppLanguage.English;
                    return true;
                case "auto":
                case "system":
                case "自动":
                    language = AppLanguage.Auto;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>语言显示名（用于 /lang 反馈）</summary>
        public static string DisplayName(AppLanguage setting)
        {
            switch (Resolve(setting))
            {
                case AppLanguage.Chinese: return "中文 (Chinese)";
                case AppLanguage.English: return "English (英文)";
                default: return setting.ToString();
            }
        }
    }
}
