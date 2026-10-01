using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DshToolbox.Core
{
    /// <summary>
    /// 用户设置（语言 / 主题），落盘到 &lt;Home&gt;\settings.json，GUI 与 CLI 共用。
    /// **默认跟随系统**：Lang 为空、Theme 为 "auto" 时按系统语言与系统深浅色决定。
    /// 优先级：--lang/--theme &gt; 环境变量 DSH_TOOLBOX_LANG &gt; settings.json &gt; 系统。
    /// </summary>
    public static class Settings
    {
        /// <summary>空字符串 = 跟随系统。</summary>
        public static string Lang = "";
        /// <summary>auto | light | dark；auto = 跟随系统。</summary>
        public static string Theme = "auto";

        public static string FilePath { get { return Path.Combine(Paths.Home, "settings.json"); } }

        public static bool LangIsAuto { get { return string.IsNullOrWhiteSpace(Lang) || Lang.Equals("auto", StringComparison.OrdinalIgnoreCase); } }
        public static bool ThemeIsAuto { get { return string.IsNullOrWhiteSpace(Theme) || Theme.Equals("auto", StringComparison.OrdinalIgnoreCase); } }

        public static void Load()
        {
            try
            {
                string f = FilePath;
                if (!File.Exists(f)) return;
                var o = Json.ParseObject(File.ReadAllText(f, Encoding.UTF8));
                string l = Json.GetString(o, "lang");
                if (l != null) Lang = l.Trim();                       // 允许存成 "" 或 "auto" 表示跟随系统
                string t = Json.GetString(o, "theme");
                if (!string.IsNullOrEmpty(t)) Theme = t.Trim();
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Paths.Ensure();
                File.WriteAllText(FilePath,
                    Json.Write(Json.Obj(
                        "lang", LangIsAuto ? "auto" : Lang,
                        "theme", ThemeIsAuto ? "auto" : Theme,
                        "systemLang", SystemLang(),
                        "systemTheme", SystemTheme(),
                        "updated", DateTime.Now)),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>系统（Windows 显示语言）推断出的语言代码。</summary>
        public static string SystemLang()
        {
            foreach (var c in new[] { CultureInfo.CurrentUICulture, CultureInfo.InstalledUICulture, CultureInfo.CurrentCulture })
            {
                try
                {
                    if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                    return c.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
                }
                catch { }
            }
            return "zh-CN";
        }

        /// <summary>系统深浅色（Windows 设置 → 个性化 → 颜色 → 应用模式）。读不到时按浅色。</summary>
        public static string SystemTheme()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", null);
                if (v != null) return Convert.ToInt32(v) == 0 ? "dark" : "light";
            }
            catch { }
            return "light";
        }

        public static string EffectiveLang() { return LangIsAuto ? SystemLang() : Lang; }
        public static string EffectiveTheme() { return ThemeIsAuto ? SystemTheme() : Theme; }
    }

    /// <summary>
    /// 极简双语：调用点直接写「中文 / English」对照，不维护 key 表。
    /// 语言在进程启动早期确定（--lang &gt; 环境变量 &gt; settings.json &gt; 系统语言）。
    /// </summary>
    public static class L
    {
        public static string Current = "zh-CN";

        public static bool IsZh
        {
            get { return Current != null && Current.StartsWith("zh", StringComparison.OrdinalIgnoreCase); }
        }

        public static string EnvVar { get { return Environment.GetEnvironmentVariable("DSH_TOOLBOX_LANG"); } }

        public static void Use(string lang)
        {
            string pick = lang;
            if (string.IsNullOrWhiteSpace(pick)) pick = EnvVar;
            if (string.IsNullOrWhiteSpace(pick)) pick = Settings.Lang;          // 空 = 跟随系统
            if (string.IsNullOrWhiteSpace(pick) || pick.Equals("auto", StringComparison.OrdinalIgnoreCase))
                pick = Settings.SystemLang();                                   // 自动检测系统语言
            pick = pick.Trim();
            if (pick.Equals("zh", StringComparison.OrdinalIgnoreCase) || pick.Equals("cn", StringComparison.OrdinalIgnoreCase) ||
                pick.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)) Current = "zh-CN";
            else if (pick.Equals("en", StringComparison.OrdinalIgnoreCase) || pick.StartsWith("en-", StringComparison.OrdinalIgnoreCase)) Current = "en-US";
            else Current = pick;
        }

        public static string T(string zh, string en) { return IsZh ? zh : en; }

        public static string T(string zh, string en, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, IsZh ? zh : en, args);
        }

        public static string[] Languages { get { return new[] { "auto", "zh-CN", "en-US" }; } }
    }
}
