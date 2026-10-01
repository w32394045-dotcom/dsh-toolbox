using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace DshToolbox.Core
{
    /// <summary>预序列化片段（把已有的 JSON 文本原样嵌入）。</summary>
    public sealed class JsonRaw
    {
        public readonly string Text;
        public JsonRaw(string text) { Text = text; }
    }

    /// <summary>保序对象：输出键顺序与添加顺序一致（人类可读、便于断言）。</summary>
    public sealed class JsonObject : IEnumerable<KeyValuePair<string, object>>
    {
        public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();
        public JsonObject Add(string key, object value)
        {
            Items.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() { return Items.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return Items.GetEnumerator(); }
    }

    /// <summary>
    /// 零依赖 JSON：写出用自研序列化器（中文不转义、数字格式稳定、保序）；
    /// 解析复用系统自带 JavaScriptSerializer。
    /// </summary>
    public static class Json
    {
        // ---------------------------------------------------------- 构造
        public static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < kv.Length; i += 2)
                d[Convert.ToString(kv[i], CultureInfo.InvariantCulture)] = kv[i + 1];
            return d;
        }

        public static List<object> Arr(params object[] items) { return new List<object>(items ?? new object[0]); }

        public static JsonObject Ordered(params object[] kv)
        {
            var o = new JsonObject();
            for (int i = 0; i + 1 < kv.Length; i += 2)
                o.Add(Convert.ToString(kv[i], CultureInfo.InvariantCulture), kv[i + 1]);
            return o;
        }

        public static List<object> ToList(IEnumerable src)
        {
            var list = new List<object>();
            if (src == null) return list;
            foreach (var x in src) list.Add(x);
            return list;
        }

        // ---------------------------------------------------------- 解析
        public static object Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
            return ser.DeserializeObject(text);
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            var o = Parse(text);
            var d = o as Dictionary<string, object>;
            if (d == null) throw new ToolException("E_JSON", "JSON 顶层不是对象");
            return d;
        }

        public static string GetString(IDictionary<string, object> d, string key, string def = null)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            return Convert.ToString(d[key], CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------- 写出
        public static string Write(object value, bool pretty = false)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, pretty, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v, bool pretty, int depth)
        {
            if (v == null) { sb.Append("null"); return; }

            var raw = v as JsonRaw;
            if (raw != null) { sb.Append(raw.Text); return; }

            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }

            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }

            if (v is char) { WriteString(sb, v.ToString()); return; }

            if (v is DateTime) { WriteString(sb, ((DateTime)v).ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture)); return; }
            if (v is DateTimeOffset) { WriteString(sb, ((DateTimeOffset)v).ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture)); return; }
            if (v is TimeSpan) { sb.Append(((TimeSpan)v).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)); return; }
            if (v is Guid) { WriteString(sb, v.ToString()); return; }
            if (v is Enum) { WriteString(sb, v.ToString()); return; }

            var bytes = v as byte[];
            if (bytes != null) { WriteString(sb, Convert.ToBase64String(bytes)); return; }

            var jo = v as JsonObject;
            if (jo != null) { WriteObject(sb, jo, pretty, depth); return; }

            var dict = v as IDictionary;
            if (dict != null) { WriteDict(sb, dict, pretty, depth); return; }

            if (v is float || v is double || v is decimal)
            {
                double dnum = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(dnum) || double.IsInfinity(dnum)) { sb.Append("null"); return; }
                sb.Append(dnum.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var formattable = v as IFormattable;
            if (formattable != null && !(v is IEnumerable))
            {
                sb.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }

            var en = v as IEnumerable;
            if (en != null) { WriteArray(sb, en, pretty, depth); return; }

            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void WriteObject(StringBuilder sb, JsonObject o, bool pretty, int depth)
        {
            if (o.Items.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            for (int i = 0; i < o.Items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                NewLine(sb, pretty, depth + 1);
                WriteString(sb, o.Items[i].Key);
                sb.Append(':');
                if (pretty) sb.Append(' ');
                WriteValue(sb, o.Items[i].Value, pretty, depth + 1);
            }
            NewLine(sb, pretty, depth);
            sb.Append('}');
        }

        static void WriteDict(StringBuilder sb, IDictionary d, bool pretty, int depth)
        {
            if (d.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            int i = 0;
            foreach (DictionaryEntry e in d)
            {
                if (i++ > 0) sb.Append(',');
                NewLine(sb, pretty, depth + 1);
                WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                sb.Append(':');
                if (pretty) sb.Append(' ');
                WriteValue(sb, e.Value, pretty, depth + 1);
            }
            NewLine(sb, pretty, depth);
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, IEnumerable e, bool pretty, int depth)
        {
            var items = new List<object>();
            foreach (var x in e) items.Add(x);
            if (items.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                NewLine(sb, pretty, depth + 1);
                WriteValue(sb, items[i], pretty, depth + 1);
            }
            NewLine(sb, pretty, depth);
            sb.Append(']');
        }

        static void NewLine(StringBuilder sb, bool pretty, int depth)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
