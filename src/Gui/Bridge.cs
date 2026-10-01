using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using DshToolbox.Core;

namespace DshToolbox.Gui
{
    public sealed class ToolResult
    {
        public int Exit;
        public string Raw = "";
        public Dictionary<string, object> Envelope;
        public bool Ok { get { return Envelope != null && Envelope.ContainsKey("ok") && Convert.ToBoolean(Envelope["ok"]); } }
        public Dictionary<string, object> Data
        {
            get
            {
                if (Envelope == null || !Envelope.ContainsKey("data")) return new Dictionary<string, object>();
                return Envelope["data"] as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
        }
        public string Error
        {
            get
            {
                if (Envelope == null || !Envelope.ContainsKey("error")) return Raw;
                var e = Envelope["error"] as Dictionary<string, object>;
                if (e == null) return Raw;
                return Json.GetString(e, "message", Raw);
            }
        }
        public List<object> Items
        {
            get
            {
                var d = Data;
                if (!d.ContainsKey("items")) return new List<object>();
                var l = d["items"] as System.Collections.IEnumerable;
                return l == null ? new List<object>() : Json.ToList(l);
            }
        }
    }

    /// <summary>
    /// GUI 调用自己的 CLI（子进程 + --json）。好处：
    /// 1) 与 agent 走完全相同的代码路径，GUI 能用就等于通道可用；
    /// 2) 命令崩溃不会带崩界面；
    /// 3) 无需把 Core 的输出目标改成可插拔。
    /// </summary>
    public static class Bridge
    {
        public static string ExePath
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetEntryAssembly();
                    if (asm != null && !string.IsNullOrEmpty(asm.Location)) return asm.Location;
                }
                catch { }
                return Process.GetCurrentProcess().MainModule.FileName;
            }
        }

        public static ToolResult Call(string cmdAndArgs, int timeoutMs = 180000)
        {
            return Call(cmdAndArgs, timeoutMs, false, null);
        }

        public static ToolResult Call(string cmdAndArgs, int timeoutMs, bool jsonl, Action<string> onLine)
        {
            var res = new ToolResult();
            var psi = new ProcessStartInfo(ExePath, cmdAndArgs + (jsonl ? " --jsonl" : " --json"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Environment.CurrentDirectory
            };
            var sb = new StringBuilder();
            var firstJson = new StringBuilder();
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        sb.AppendLine(e.Data);
                        if (!jsonl) firstJson.Append(e.Data);
                        if (onLine != null) { try { onLine(e.Data); } catch { } }
                    };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null && onLine != null) { try { onLine("! " + e.Data); } catch { } } };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        res.Exit = ExitCodes.Timeout;
                        res.Raw = sb.ToString();
                        return res;
                    }
                    try { p.WaitForExit(2000); } catch { }
                    res.Exit = p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                res.Exit = ExitCodes.Error;
                res.Raw = ex.Message;
                return res;
            }
            res.Raw = sb.ToString().Trim();
            string text = jsonl ? FirstJsonlMeta(res.Raw) : firstJson.ToString().Trim();
            if (!string.IsNullOrEmpty(text))
            {
                try { res.Envelope = Json.ParseObject(text); } catch { }
            }
            return res;
        }

        static string FirstJsonlMeta(string raw)
        {
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                try
                {
                    var o = Json.ParseObject(t);
                    string type = Json.GetString(o, "type");
                    if (type == "summary")
                        return Json.Write(Json.Obj("ok", o.ContainsKey("ok") ? o["ok"] : true, "data", o));
                    if (type == "meta") continue;
                }
                catch { }
            }
            return null;
        }

        // ---------------------------------------------------------- 便捷取字段
        public static string S(Dictionary<string, object> d, string key, string def = "")
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            return Compact(d[key], def);
        }

        /// <summary>把任意值渲染成人类可读文本：字符串/布尔/数值直出，嵌套对象与数组转紧凑 JSON。</summary>
        /// <summary>界面显示出口：脱敏开关打开时替换用户名与主目录路径。</summary>
        public static string Compact(object v, string def = "")
        {
            return Redact.Text(CompactRaw(v, def));
        }

        static string CompactRaw(object v, string def = "")
        {
            if (v == null) return def;
            if (v is string) return (string)v;
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is int || v is long || v is double || v is float || v is decimal || v is short || v is byte)
                return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            if (v is DateTime) return ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss");
            try
            {
                string j = Json.Write(v);
                return j.Length > 240 ? j.Substring(0, 237) + "..." : j;
            }
            catch { return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
        }
        public static bool B(Dictionary<string, object> d, string key, bool def = false)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            try { return Convert.ToBoolean(d[key]); } catch { return def; }
        }
        public static long L(Dictionary<string, object> d, string key, long def = 0)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            try { return Convert.ToInt64(d[key]); } catch { return def; }
        }
        public static Dictionary<string, object> Sub(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return new Dictionary<string, object>();
            return d[key] as Dictionary<string, object> ?? new Dictionary<string, object>();
        }
    }
}
