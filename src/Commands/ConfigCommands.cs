using System;
using System.Collections.Generic;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>
    /// 设置读写：语言与主题。**默认跟随系统**；GUI 的语言/主题切换与 agent 都走这两个命令。
    /// lang 取值：auto | zh-CN | en-US     theme 取值：auto | light | dark
    /// </summary>
    public static class ConfigCommands
    {
        public static void Register()
        {
            Registry.Add("config.get",
                L.T("读取工具箱设置（语言 / 主题 / 系统检测值 / 路径）", "Read toolbox settings (language / theme / detected system values / paths)"),
                "config get [--json]",
                RunGet,
                examples: new[] { "dsh-toolbox config.get --json" });

            Registry.Add("config.set",
                L.T("修改设置：--lang auto|zh-CN|en-US，--theme auto|light|dark（auto = 跟随系统）",
                    "Change settings: --lang auto|zh-CN|en-US, --theme auto|light|dark (auto = follow the system)"),
                "config set [--lang <auto|zh-CN|en-US>] [--theme <auto|light|dark>]",
                RunSet,
                examples: new[] { "dsh-toolbox config.set --theme dark --json", "dsh-toolbox config.set --lang auto --json" });
        }

        static int RunGet(Ctx ctx)
        {
            var items = new List<object>
            {
                Json.Obj("name", L.T("语言设置", "Language setting"), "value", Settings.LangIsAuto ? L.T("自动（跟随系统）", "auto (follow system)") : Settings.Lang),
                Json.Obj("name", L.T("本次生效", "Effective in this process"), "value", L.Current),
                Json.Obj("name", L.T("系统语言", "Detected system language"), "value", Settings.SystemLang()),
                Json.Obj("name", L.T("主题设置", "Theme setting"), "value", Settings.ThemeIsAuto ? L.T("自动（跟随系统）", "auto (follow system)") : Settings.Theme),
                Json.Obj("name", L.T("生效主题", "Effective theme"), "value", Settings.EffectiveTheme()),
                Json.Obj("name", L.T("系统深浅色", "Detected system theme"), "value", Settings.SystemTheme()),
                Json.Obj("name", L.T("设置文件", "Settings file"), "value", Settings.FilePath),
                Json.Obj("name", L.T("数据目录", "Data home"), "value", Paths.Home)
            };
            ctx.Out.Result("config.get", Json.Obj(
                "lang", Settings.LangIsAuto ? "auto" : Settings.Lang,
                "langAuto", Settings.LangIsAuto,
                "effectiveLang", L.Current,                              // 本次进程真正生效（含 --lang / 环境变量覆盖）
                "settingResolvesTo", Settings.EffectiveLang(),
                "systemLang", Settings.SystemLang(),
                "theme", Settings.ThemeIsAuto ? "auto" : Settings.Theme,
                "themeAuto", Settings.ThemeIsAuto,
                "effectiveTheme", Settings.EffectiveTheme(),
                "systemTheme", Settings.SystemTheme(),
                "persisted", System.IO.File.Exists(Settings.FilePath),
                "settingsFile", Settings.FilePath,
                "home", Paths.Home,
                "languages", L.Languages,
                "themes", new[] { "auto", "light", "dark" },
                "items", items, "count", items.Count,
                "columns", new[] { "name", "value" }));
            return ExitCodes.Ok;
        }

        static int RunSet(Ctx ctx)
        {
            string lang = ctx.Get("lang");
            string theme = ctx.Get("theme");
            var changed = new List<string>();

            if (!string.IsNullOrEmpty(lang))
            {
                string norm = lang.Trim();
                if (norm.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    Settings.Lang = "";
                    changed.Add("lang=auto");
                }
                else if (norm.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    Settings.Lang = "zh-CN"; changed.Add("lang=zh-CN");
                }
                else if (norm.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    Settings.Lang = "en-US"; changed.Add("lang=en-US");
                }
                else
                {
                    throw ToolException.Usage(L.T("不支持的语言：" + lang, "Unsupported language: " + lang), "auto | zh-CN | en-US");
                }
            }

            if (!string.IsNullOrEmpty(theme))
            {
                string norm = theme.Trim().ToLowerInvariant();
                if (norm == "auto") { Settings.Theme = "auto"; changed.Add("theme=auto"); }
                else if (norm == "light" || norm == "dark") { Settings.Theme = norm; changed.Add("theme=" + norm); }
                else throw ToolException.Usage(L.T("不支持的主题：" + theme, "Unsupported theme: " + theme), "auto | light | dark");
            }

            if (changed.Count == 0)
                throw ToolException.Usage(L.T("没有要修改的项", "Nothing to change"), "config.set --theme dark | --lang auto");

            Settings.Save();
            ctx.Out.Result("config.set", Json.Obj(
                "ok", true,
                "lang", Settings.LangIsAuto ? "auto" : Settings.Lang,
                "settingResolvesTo", Settings.EffectiveLang(),
                "currentProcess", L.Current,
                "systemLang", Settings.SystemLang(),
                "theme", Settings.ThemeIsAuto ? "auto" : Settings.Theme,
                "effectiveTheme", Settings.EffectiveTheme(),
                "systemTheme", Settings.SystemTheme(),
                "changed", changed,
                "settingsFile", Settings.FilePath,
                "detail", L.T("已保存到 settings.json；GUI 会立即生效（语言切换会重建界面，auto 表示跟随系统）",
                              "Saved to settings.json; the GUI applies it immediately (a language switch rebuilds the window; auto follows the system)")));
            return ExitCodes.Ok;
        }
    }
}
