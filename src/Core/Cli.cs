using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DshToolbox.Core
{
    /// <summary>
    /// 选项解析器。规则见 docs\CLI-CONTRACT.md：
    /// --name value / --name=value / -n value；布尔选项在 BoolNames 白名单里；可重复选项累加；不做逗号分割。
    /// </summary>
    public sealed class ArgMap
    {
        readonly Dictionary<string, List<string>> _vals = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Positional = new List<string>();

        static readonly HashSet<string> BoolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "json","jsonl","quiet","no-color","color","verbose","debug","dry-run","dryrun","force","yes",
            "all","hidden","follow","recursive","recurse","pretty","stream","append","follow-links",   // 注意：tail/wait 会带数值，不能登记为布尔
            "absolute","relative","help","version","parents","invert","ignore-case","files-only","dirs-only",
            "fail-fast","skip-errors","no-progress","stdin","null-terminated","unique","reverse","count-only",
            "no-header","csv","table","text","plain","raw","overwrite","create-dirs","shell","wait","detach",
            "elevate","long","short","no-log","checksum","link","system","hidden-only","preview","summary",
            // 2026-10-01 补充：明确不接受值的开关（漏登记会导致"该开关 + 位置参数"被误解析）
            "fast","show-secrets","tree","no-revocation","cache-only","strict","no-follow","no-cache",
            "interactive","force-run","desc","reap","no-hash","skip-hash","all-users","system-only","latest","oldest",
            "no-retry","follow-symlink","dedupe","mirror","delete","prune","keep-going","no-clobber","full"
        };

        static readonly Dictionary<string, string> ShortAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "h", "help" }, { "j", "json" }, { "q", "quiet" }, { "v", "verbose" }, { "y", "yes" },
            { "n", "max-results" }, { "o", "output" }, { "p", "path" }, { "r", "recursive" }, { "d", "depth" }
        };

        public static ArgMap Parse(IEnumerable<string> args)
        {
            var m = new ArgMap();
            var list = new List<string>(args ?? new string[0]);
            bool literal = false;
            for (int i = 0; i < list.Count; i++)
            {
                string a = list[i];
                if (literal) { m.Positional.Add(a); continue; }
                if (a == "--") { literal = true; continue; }

                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    string body = a.Substring(2);
                    int eq = body.IndexOf('=');
                    if (eq >= 0)
                    {
                        m.AddValue(body.Substring(0, eq), body.Substring(eq + 1));
                        continue;
                    }
                    if (BoolNames.Contains(body)) { m.AddValue(body, "true"); continue; }
                    if (i + 1 < list.Count && !LooksLikeOption(list[i + 1])) { m.AddValue(body, list[++i]); continue; }
                    m.AddValue(body, "true");   // 末尾裸开关视为 true
                    continue;
                }

                if (a.Length > 1 && a[0] == '-' && !char.IsDigit(a[1]))
                {
                    string shortName = a.Substring(1);
                    string longName;
                    if (!ShortAliases.TryGetValue(shortName, out longName)) longName = shortName;
                    if (BoolNames.Contains(longName)) { m.AddValue(longName, "true"); continue; }
                    if (i + 1 < list.Count && !LooksLikeOption(list[i + 1])) { m.AddValue(longName, list[++i]); continue; }
                    m.AddValue(longName, "true");
                    continue;
                }

                m.Positional.Add(a);
            }
            return m;
        }

        static bool LooksLikeOption(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s == "--") return true;
            if (s[0] != '-') return false;
            if (s.Length > 1 && char.IsDigit(s[1])) return false;   // -5 视为负数值
            return true;
        }

        void AddValue(string name, string value)
        {
            List<string> l;
            if (!_vals.TryGetValue(name, out l)) { l = new List<string>(); _vals[name] = l; }
            l.Add(value);
        }

        public void Set(string name, string value)
        {
            var l = new List<string>();
            l.Add(value);
            _vals[Normalize(name)] = l;
        }

        static string Normalize(string name)
        {
            if (name == null) return "";
            return name.TrimStart('-');
        }

        public bool Has(string name) { return _vals.ContainsKey(Normalize(name)); }

        public string Get(string name, string def = null)
        {
            List<string> l;
            if (!_vals.TryGetValue(Normalize(name), out l) || l.Count == 0) return def;
            return l[l.Count - 1];
        }

        public string[] GetAll(string name)
        {
            List<string> l;
            if (!_vals.TryGetValue(Normalize(name), out l)) return new string[0];
            return l.ToArray();
        }

        public bool Flag(string name, bool def = false)
        {
            string v = Get(name);
            if (v == null) return def;
            if (v.Length == 0) return true;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (v == "1") return true;
            if (v == "0") return false;
            return true;
        }

        public int GetInt(string name, int def)
        {
            string v = Get(name);
            int r;
            if (v == null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return def;
            return r;
        }

        public long GetLong(string name, long def)
        {
            string v = Get(name);
            if (v == null) return def;
            long r;
            if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return Fs.ParseSize(v, def);
        }

        public double GetDouble(string name, double def)
        {
            string v = Get(name);
            double r;
            if (v == null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return def;
            return r;
        }

        /// <summary>时长/时间：30s 5m 2h 3d 1w 1h30m；纯数字=秒。</summary>
        public TimeSpan GetSpan(string name, TimeSpan def)
        {
            string v = Get(name);
            if (v == null) return def;
            TimeSpan ts;
            if (TryParseDuration(v, out ts)) return ts;
            throw ToolException.Usage(string.Format("选项 --{0} 的时长格式非法：{1}", Normalize(name), v), "例：30s / 5m / 2h / 3d / 1w");
        }

        /// <summary>时间点：2026-10-01 / ISO8601 / 相对 -2h（表示"现在减 2 小时"）。</summary>
        public DateTime? GetDate(string name)
        {
            string v = Get(name);
            DateTime? d = TryParseDate(v);
            if (v != null && d == null)
                throw ToolException.Usage(string.Format("选项 --{0} 的时间格式非法：{1}", Normalize(name), v), "例：2026-10-01 / 2026-10-01T12:00:00 / -2h");
            return d;
        }

        public string Require(string name)
        {
            string v = Get(name);
            if (string.IsNullOrEmpty(v))
                throw ToolException.Usage(string.Format("缺少必填选项 --{0}", Normalize(name)));
            return v;
        }

        public static bool TryParseDuration(string s, out TimeSpan ts)
        {
            ts = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            double plain;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out plain)) { ts = TimeSpan.FromSeconds(plain); return true; }

            var re = new Regex(@"^\s*(?:(\d+(?:\.\d+)?)\s*(ms|s|m|h|d|w|y))+\s*$", RegexOptions.IgnoreCase);
            if (!re.IsMatch(s)) return false;
            double total = 0;
            foreach (Match m in Regex.Matches(s, @"(\d+(?:\.\d+)?)\s*(ms|s|m|h|d|w|y)", RegexOptions.IgnoreCase))
            {
                double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                switch (m.Groups[2].Value.ToLowerInvariant())
                {
                    case "ms": total += n / 1000.0; break;
                    case "s": total += n; break;
                    case "m": total += n * 60; break;
                    case "h": total += n * 3600; break;
                    case "d": total += n * 86400; break;
                    case "w": total += n * 604800; break;
                    case "y": total += n * 31536000; break;
                }
            }
            ts = TimeSpan.FromSeconds(total);
            return true;
        }

        public static DateTime? TryParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s[0] == '-')
            {
                TimeSpan rel;
                if (TryParseDuration(s.Substring(1), out rel)) return DateTime.Now - rel;
                return null;
            }
            if (s[0] == '+')
            {
                TimeSpan rel;
                if (TryParseDuration(s.Substring(1), out rel)) return DateTime.Now + rel;
                return null;
            }
            // 裸时长（必须带单位，避免把 2026 当成 2026 秒）按"现在减去该时长"处理，符合 CLI-CONTRACT §7：--newer 2h = 最近 2 小时
            if (Regex.IsMatch(s, @"^\s*\d+(?:\.\d+)?\s*(ms|s|m|h|d|w|y)\s*$", RegexOptions.IgnoreCase))
            {
                TimeSpan bare;
                if (TryParseDuration(s, out bare)) return DateTime.Now - bare;
            }
            DateTime d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out d)) return d;
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out d)) return d;
            return null;
        }

        /// <summary>把解析结果摊平成 JSON（供 manifest / 日志记录）。</summary>
        public Dictionary<string, object> ToDictionary()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in _vals) d[kv.Key] = kv.Value.Count == 1 ? (object)kv.Value[0] : kv.Value.ToArray();
            if (Positional.Count > 0) d["_positional"] = Positional.ToArray();
            return d;
        }
    }
}
