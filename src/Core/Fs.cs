using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshToolbox.Core
{
    /// <summary>glob → Regex。支持 **、*、?、[abc]、{a,b}。匹配前统一把 \ 换成 /。</summary>
    public static class Glob
    {
        static readonly Dictionary<string, Regex> Cache = new Dictionary<string, Regex>(StringComparer.Ordinal);

        public static Regex ToRegex(string glob, bool ignoreCase = true)
        {
            if (glob == null) glob = "*";
            string key = (ignoreCase ? "i:" : "s:") + glob;
            lock (Cache)
            {
                Regex hit;
                if (Cache.TryGetValue(key, out hit)) return hit;
                var re = new Regex("^" + Translate(glob) + "$", RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
                Cache[key] = re;
                return re;
            }
        }

        static string Translate(string g)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < g.Length)
            {
                char c = g[i];
                if (c == '*')
                {
                    bool dbl = i + 1 < g.Length && g[i + 1] == '*';
                    if (dbl)
                    {
                        // **/  -> 任意层级（可为 0 层）
                        if (i + 2 < g.Length && (g[i + 2] == '/' || g[i + 2] == '\\')) { sb.Append("(?:.*[/\\\\])?"); i += 3; }
                        else { sb.Append(".*"); i += 2; }
                    }
                    else { sb.Append("[^/\\\\]*"); i++; }
                }
                else if (c == '?') { sb.Append("[^/\\\\]"); i++; }
                else if (c == '{')
                {
                    int end = g.IndexOf('}', i);
                    if (end < 0) { sb.Append(Regex.Escape("{")); i++; }
                    else
                    {
                        var opts = g.Substring(i + 1, end - i - 1).Split(',');
                        sb.Append("(?:");
                        for (int k = 0; k < opts.Length; k++)
                        {
                            if (k > 0) sb.Append('|');
                            sb.Append(Translate(opts[k]));
                        }
                        sb.Append(')');
                        i = end + 1;
                    }
                }
                else if (c == '[')
                {
                    int end = g.IndexOf(']', i + 1);
                    if (end < 0) { sb.Append(Regex.Escape("[")); i++; }
                    else { sb.Append(g.Substring(i, end - i + 1)); i = end + 1; }
                }
                else { sb.Append(Regex.Escape(c.ToString())); i++; }
            }
            return sb.ToString();
        }

        public static bool IsMatch(string glob, string value, bool ignoreCase = true)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return ToRegex(glob, ignoreCase).IsMatch(value.Replace('\\', '/'));
        }

        /// <summary>glob 里若含路径分隔符或 **，则按相对路径匹配；否则只按文件名匹配。</summary>
        public static bool MatchAny(IEnumerable<string> globs, string name, string relPath)
        {
            if (globs == null) return false;
            string rel = (relPath ?? "").Replace('\\', '/');
            foreach (var g in globs)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                bool pathLike = g.IndexOf('/') >= 0 || g.IndexOf('\\') >= 0 || g.Contains("**");
                if (pathLike) { if (Glob.IsMatch(g, rel)) return true; }
                else { if (Glob.IsMatch(g, name)) return true; }
            }
            return false;
        }
    }

    public sealed class FsEntry
    {
        public string FullPath;
        public string RelPath;
        public string Name;
        public string Ext;
        public long Size;
        public DateTime LastWriteUtc;
        public DateTime CreatedUtc;
        public FileAttributes Attributes;
        public bool IsDir;

        public Dictionary<string, object> ToDictionary(bool relative)
        {
            return Json.Obj(
                "path", relative ? RelPath : FullPath,
                "name", Name,
                "ext", Ext,
                "size", Size,
                "mtime", LastWriteUtc.ToLocalTime(),
                "ctime", CreatedUtc.ToLocalTime(),
                "dir", IsDir,
                "attrs", Attributes.ToString()
            );
        }
    }

    public sealed class FsScanOptions
    {
        public List<string> Include = new List<string>();
        public List<string> Exclude = new List<string>();
        public List<string> ExcludeDirs = new List<string>();
        public List<string> Exts = new List<string>();
        public Regex NameRegex;
        public int MaxDepth = 0;             // 0 = 不限
        public bool Hidden = false;
        public bool FollowLinks = false;
        public bool FilesOnly = true;
        public bool DirsOnly = false;
        public long MinSize = -1;
        public long MaxSize = -1;
        public DateTime? NewerThan;
        public DateTime? OlderThan;
        public long MaxResults = 0;          // 0 = 不限
        public bool IgnoreCase = true;

        // 统计与诊断（由 Enumerate 填充）
        public int ScannedDirs;
        public long ScannedFiles;
        public readonly List<string> Denied = new List<string>();
        public bool Truncated;

        public static FsScanOptions FromArgs(Ctx ctx)
        {
            var o = new FsScanOptions();
            o.Include.AddRange(ctx.GetAll("include"));
            o.Exclude.AddRange(ctx.GetAll("exclude"));
            o.ExcludeDirs.AddRange(ctx.GetAll("exclude-dir"));
            foreach (var e in ctx.GetAll("ext"))
            {
                string x = e.Trim();
                if (x.StartsWith(".")) x = x.Substring(1);
                if (x.Length > 0) o.Exts.Add(x);
            }
            string nrx = ctx.Get("name-regex");
            if (!string.IsNullOrEmpty(nrx))
            {
                try { o.NameRegex = new Regex(nrx, RegexOptions.Compiled | (ctx.Flag("ignore-case") ? RegexOptions.IgnoreCase : RegexOptions.None)); }
                catch (Exception ex) { throw ToolException.Usage("--name-regex 正则非法：" + ex.Message); }
            }
            o.MaxDepth = ctx.Args.GetInt("depth", 0);
            o.Hidden = ctx.Flag("hidden");
            o.FollowLinks = ctx.Flag("follow") || ctx.Flag("follow-links") || ctx.Flag("follow-symlink");
            o.MinSize = ctx.Args.GetLong("min-size", -1);
            o.MaxSize = ctx.Args.GetLong("max-size", -1);
            o.NewerThan = ctx.Args.GetDate("newer");
            o.OlderThan = ctx.Args.GetDate("older");
            o.MaxResults = ctx.Args.GetLong("max-results", 0);
            if (ctx.Flag("dirs-only")) { o.DirsOnly = true; o.FilesOnly = false; }
            return o;
        }

        public bool Accept(FsEntry e)
        {
            if (e.IsDir && FilesOnly && !DirsOnly) return false;
            if (!e.IsDir && DirsOnly) return false;
            if (!Hidden && (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
            if (!e.IsDir)
            {
                if (Exts.Count > 0)
                {
                    string x = e.Ext.StartsWith(".") ? e.Ext.Substring(1) : e.Ext;
                    if (!Exts.Any(t => string.Equals(t, x, StringComparison.OrdinalIgnoreCase))) return false;
                }
                if (MinSize >= 0 && e.Size < MinSize) return false;
                if (MaxSize >= 0 && e.Size > MaxSize) return false;
            }
            if (NameRegex != null && !NameRegex.IsMatch(e.Name)) return false;
            if (NewerThan.HasValue && e.LastWriteUtc < NewerThan.Value.ToUniversalTime()) return false;
            if (OlderThan.HasValue && e.LastWriteUtc > OlderThan.Value.ToUniversalTime()) return false;
            if (Include.Count > 0 && !Glob.MatchAny(Include, e.Name, e.RelPath)) return false;
            if (Exclude.Count > 0 && Glob.MatchAny(Exclude, e.Name, e.RelPath)) return false;
            return true;
        }
    }

    public static class Fs
    {
        // ---------------------------------------------------------- 路径
        public static string Expand(string path, string cwd)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string p = Environment.ExpandEnvironmentVariables(path.Trim());
            if (p == "~") p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            else if (p.StartsWith("~/") || p.StartsWith("~\\"))
                p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Substring(2));
            if (!Path.IsPathRooted(p)) p = Path.Combine(string.IsNullOrEmpty(cwd) ? Environment.CurrentDirectory : cwd, p);
            try { p = Path.GetFullPath(p); } catch { }
            return p.TrimEnd('\\', '/') == "" ? p : p;
        }

        /// <summary>超长路径加 \\?\ 前缀（配合 manifest 的 longPathAware）。</summary>
        public static string LongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\")) return path;
            string full;
            try { full = Path.GetFullPath(path); } catch { return path; }
            if (full.Length < 240) return full;
            return @"\\?\" + full;
        }

        public static bool IsSubPath(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
            string c = child.TrimEnd('\\').ToLowerInvariant();
            string p = parent.TrimEnd('\\').ToLowerInvariant();
            return c == p || c.StartsWith(p + "\\");
        }

        public static bool TryStat(FileSystemInfo fsi, out FileAttributes attr, out long size, out DateTime mtime, out DateTime ctime)
        {
            attr = 0; size = 0; mtime = DateTime.MinValue; ctime = DateTime.MinValue;
            try
            {
                attr = fsi.Attributes;
                mtime = fsi.LastWriteTimeUtc;
                ctime = fsi.CreationTimeUtc;
                if ((attr & FileAttributes.Directory) == 0)
                {
                    var fi = fsi as FileInfo;
                    if (fi != null) size = fi.Length;
                }
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------- 大小/格式
        public static long ParseSize(string s, long def = 0)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim();
            var m = Regex.Match(s, @"^(\d+(?:\.\d+)?)\s*(b|kb|k|mb|m|gb|g|tb|t|pb|p)?$", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                long plain;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out plain)) return plain;
                return def;
            }
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "": case "b": return (long)n;
                case "k": case "kb": return (long)(n * 1024);
                case "m": case "mb": return (long)(n * 1024 * 1024);
                case "g": case "gb": return (long)(n * 1024L * 1024 * 1024);
                case "t": case "tb": return (long)(n * 1024L * 1024 * 1024 * 1024);
                case "p": case "pb": return (long)(n * 1024L * 1024 * 1024 * 1024 * 1024);
            }
            return def;
        }

        public static string FormatSize(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return i == 0 ? bytes + " B" : v.ToString("0.##", CultureInfo.InvariantCulture) + " " + u[i];
        }

        // ---------------------------------------------------------- 遍历
        public static IEnumerable<FsEntry> Enumerate(string root, FsScanOptions o)
        {
            if (string.IsNullOrEmpty(root)) yield break;
            string rootFull;
            try { rootFull = Path.GetFullPath(root); } catch { yield break; }
            if (!Directory.Exists(LongPath(rootFull))) yield break;
            string rootPrefix = rootFull.TrimEnd('\\') + "\\";

            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(rootFull, 0));

            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                o.ScannedDirs++;
                FileSystemInfo[] entries;
                try
                {
                    var di = new DirectoryInfo(LongPath(cur.Key));
                    entries = di.GetFileSystemInfos();
                }
                catch (UnauthorizedAccessException) { o.Denied.Add(cur.Key); continue; }
                catch (Exception) { o.Denied.Add(cur.Key); continue; }

                Array.Sort(entries, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                foreach (var fsi in entries)
                {
                    FileAttributes attr; long size; DateTime mtime, ctime;
                    if (!Fs.TryStat(fsi, out attr, out size, out mtime, out ctime)) continue;

                    bool isDir = (attr & FileAttributes.Directory) != 0;
                    bool isLink = (attr & FileAttributes.ReparsePoint) != 0;
                    if (isLink && !o.FollowLinks) continue;

                    string full = fsi.FullName;
                    string rel = full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(rootPrefix.Length) : fsi.Name;

                    if (isDir && o.ExcludeDirs.Count > 0)
                    {
                        bool skip = false;
                        foreach (var d in o.ExcludeDirs)
                            if (string.Equals(d, fsi.Name, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                        if (skip) continue;
                    }

                    var e = new FsEntry
                    {
                        FullPath = full,
                        RelPath = rel,
                        Name = fsi.Name,
                        Ext = Path.GetExtension(fsi.Name) ?? "",
                        Size = size,
                        LastWriteUtc = mtime,
                        CreatedUtc = ctime,
                        Attributes = attr,
                        IsDir = isDir
                    };
                    if (!isDir) o.ScannedFiles++;

                    if (isDir)
                    {
                        if (o.DirsOnly && o.Accept(e)) yield return e;
                    }
                    else if (o.Accept(e)) yield return e;

                    if (isDir && (o.MaxDepth == 0 || cur.Value + 1 <= o.MaxDepth))
                        stack.Push(new KeyValuePair<string, int>(full, cur.Value + 1));
                }
            }
        }

        public static IEnumerable<FsEntry> EnumerateMany(IEnumerable<string> roots, FsScanOptions o)
        {
            long emitted = 0;
            foreach (var r in roots)
            {
                foreach (var e in Enumerate(r, o))
                {
                    yield return e;
                    emitted++;
                    if (o.MaxResults > 0 && emitted >= o.MaxResults) { o.Truncated = true; yield break; }
                }
            }
        }

        // ---------------------------------------------------------- 哈希
        public static string[] SupportedHashes() { return new[] { "md5", "sha1", "sha256", "sha384", "sha512", "crc32" }; }

        public static string HashFile(string path, string algo, CancellationToken ct = default(CancellationToken))
        {
            string a = (algo ?? "sha256").ToLowerInvariant();
            if (a == "crc32") return Crc32Hex(path, ct);

            HashAlgorithm hasher;
            switch (a)
            {
                case "md5": hasher = MD5.Create(); break;
                case "sha1": hasher = SHA1.Create(); break;
                case "sha256": case "sha2": case "sha-256": hasher = SHA256.Create(); break;
                case "sha384": hasher = SHA384.Create(); break;
                case "sha512": hasher = SHA512.Create(); break;
                default: throw ToolException.Usage("不支持的哈希算法：" + algo, "支持 " + string.Join("/", SupportedHashes()));
            }
            using (hasher)
            using (var fs = new FileStream(LongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan))
            {
                var buf = new byte[1024 * 1024];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hasher.TransformBlock(buf, 0, n, buf, 0);
                }
                hasher.TransformFinalBlock(buf, 0, 0);
                return ToHex(hasher.Hash);
            }
        }

        /// <summary>查重用的快速指纹：大小 + 头尾各 64KB。用于避免全量哈希。</summary>
        public static string QuickHash(string path, CancellationToken ct = default(CancellationToken))
        {
            using (var fs = new FileStream(LongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                long len = fs.Length;
                var head = new byte[Math.Min(65536, len)];
                fs.Read(head, 0, head.Length);
                var tail = new byte[Math.Min(65536, len)];
                if (len > head.Length)
                {
                    fs.Seek(-tail.Length, SeekOrigin.End);
                    fs.Read(tail, 0, tail.Length);
                }
                var buf = new byte[8 + head.Length + tail.Length];
                BitConverter.GetBytes(len).CopyTo(buf, 0);
                head.CopyTo(buf, 8);
                tail.CopyTo(buf, 8 + head.Length);
                return ToHex(sha.ComputeHash(buf));
            }
        }

        static string Crc32Hex(string path, CancellationToken ct)
        {
            uint[] table = CrcTable.Value;
            uint crc = 0xFFFFFFFF;
            using (var fs = new FileStream(LongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan))
            {
                var buf = new byte[1024 * 1024];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < n; i++) crc = (crc >> 8) ^ table[(crc ^ buf[i]) & 0xFF];
                }
            }
            return (crc ^ 0xFFFFFFFF).ToString("x8", CultureInfo.InvariantCulture);
        }

        static readonly Lazy<uint[]> CrcTable = new Lazy<uint[]>(() =>
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        });

        public static string ToHex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // ---------------------------------------------------------- 并行
        public static List<TOut> ParallelMap<TIn, TOut>(IList<TIn> input, int parallel, Func<TIn, TOut> fn, CancellationToken ct)
        {
            var output = new TOut[input.Count];
            int deg = Math.Max(1, Math.Min(parallel <= 0 ? Environment.ProcessorCount : parallel, 32));
            var opts = new ParallelOptions { MaxDegreeOfParallelism = deg, CancellationToken = ct };
            try
            {
                Parallel.For(0, input.Count, opts, i => { output[i] = fn(input[i]); });
            }
            catch (AggregateException ae)
            {
                var inner = ae.Flatten().InnerExceptions.FirstOrDefault();
                if (inner is OperationCanceledException) throw inner;
                throw new ToolException("E_PARALLEL", inner != null ? inner.Message : ae.Message);
            }
            return new List<TOut>(output);
        }

        public static void EnsureDir(string dir)
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(LongPath(dir))) Directory.CreateDirectory(LongPath(dir));
        }

        public static bool CanWrite(string dir)
        {
            try
            {
                if (!Directory.Exists(LongPath(dir))) return false;
                string probe = Path.Combine(dir, ".dsh-write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
