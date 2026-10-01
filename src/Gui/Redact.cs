using System;

namespace DshToolbox.Gui
{
    /// <summary>
    /// 界面脱敏：设环境变量 DSH_TOOLBOX_REDACT=1 后，界面上显示的一切路径/用户名
    /// 都会替换成 %USERPROFILE% / %APPDATA% / %LOCALAPPDATA% / &lt;user&gt;。
    ///
    /// 用途：截图写文档、给 issue 附截图/日志时不必暴露本机用户名与目录结构。
    /// 只作用于**界面显示**（Bridge 的取值出口 + 关于页的两条路径），不改 CLI 输出、
    /// 不改 JSON 契约、不改任何命令行为——所以对 agent 与脚本零影响。
    /// </summary>
    internal static class Redact
    {
        public static readonly bool Enabled = IsOn();

        static readonly string Profile = Safe(Environment.SpecialFolder.UserProfile);
        static readonly string AppData = Safe(Environment.SpecialFolder.ApplicationData);
        static readonly string LocalAppData = Safe(Environment.SpecialFolder.LocalApplicationData);
        static readonly string User = Environment.UserName ?? "";

        static bool IsOn()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("DSH_TOOLBOX_REDACT");
                if (string.IsNullOrEmpty(v)) return false;
                v = v.Trim().ToLowerInvariant();
                return v == "1" || v == "true" || v == "yes" || v == "on";
            }
            catch { return false; }
        }

        static string Safe(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f) ?? ""; } catch { return ""; }
        }

        /// <summary>按"先长后短"替换，避免 %USERPROFILE% 先命中导致 AppData 路径变丑。</summary>
        public static string Text(string s)
        {
            if (!Enabled || string.IsNullOrEmpty(s)) return s;
            if (LocalAppData.Length > 3) s = s.Replace(LocalAppData, "%LOCALAPPDATA%");
            if (AppData.Length > 3) s = s.Replace(AppData, "%APPDATA%");
            if (Profile.Length > 3) s = s.Replace(Profile, "%USERPROFILE%");
            if (User.Length > 1) s = s.Replace(User, "<user>");
            return s;
        }
    }
}
