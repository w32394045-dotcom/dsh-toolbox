using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>
    /// scanner-dev 拥有：scan.* 文件扫描命令（见 docs/ARCHITECTURE.md §3）。
    /// glob / 遍历 / 哈希 / 大小格式化一律复用 Core\Fs.cs，不重复实现第二套。
    /// 输出统一走 ctx.Out.Result；长循环检查 ctx.Cancel；读失败记入 data.failures 并返回 exit 6。
    /// </summary>
    public static class ScanCommands
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ============================================================ 注册
        public static void Register()
        {
            Registry.Add("scan.find", "按条件递归扫描文件（glob / 正则 / 大小 / 时间 / 内容）",
                "scan find --path <dir>... [--include <glob>]... [--exclude <glob>]... [--exclude-dir <name>]... [--ext <ext>]... " +
                "[--name-regex <re>] [--content-regex <re>] [--min-size <size>] [--max-size <size>] [--newer <dur|date>] [--older <dur|date>] " +
                "[--depth <n>] [--hidden] [--follow] [--max-results <n>] [--parallel <n>] [--sort name|path|size|mtime] [--reverse] " +
                "[--hash [algo]] [--relative] [--jsonl]",
                ctx => Guarded(ctx, RunFind),
                examples: new[]
                {
                    "dsh-toolbox scan find --path . --include \"**/*.log\" --json",
                    "dsh-toolbox scan find --path C:\\data --ext txt --ext md --newer 2h --sort mtime --reverse --json",
                    "dsh-toolbox scan find --path . --content-regex \"TODO|FIXME\" --ignore-case --max-results 20 --jsonl"
                });

            Registry.Add("scan.size", "目录体积排行（top-N，含占比百分比）",
                "scan size --path <dir> [--top <n>] [--parallel <n>] [--exclude-dir <name>]... [--hidden] [--dirs-only] [--relative]",
                ctx => Guarded(ctx, RunSize),
                examples: new[]
                {
                    "dsh-toolbox scan size --path C:\\data --top 15 --json",
                    "dsh-toolbox scan size --path . --exclude-dir node_modules --dirs-only"
                });

            Registry.Add("scan.tree", "目录树（限深/限条数，含每层大小汇总）",
                "scan tree --path <dir> [--depth <n>] [--max-results <n>] [--exclude <glob>]... [--exclude-dir <name>]... [--hidden] [--dirs-only] [--relative]",
                ctx => Guarded(ctx, RunTree),
                examples: new[]
                {
                    "dsh-toolbox scan tree --path . --depth 2 --max-results 100 --json",
                    "dsh-toolbox scan tree --path C:\\data --depth 3 --exclude-dir node_modules"
                });

            Registry.Add("scan.dup", "重复文件检测（size 分组 → 快速指纹 → 全量哈希确认）",
                "scan dup --path <dir>... [--algo <algo>] [--min-size <size>] [--min-count <n>] [--exclude-dir <name>]... [--hidden] [--parallel <n>] [--max-results <n>] [--relative]",
                ctx => Guarded(ctx, RunDup),
                aliases: new[] { "scan.dupes", "scan.duplicate" },
                examples: new[]
                {
                    "dsh-toolbox scan dup --path . --json",
                    "dsh-toolbox scan dup --path D:\\photos --algo sha256 --min-size 1KB --json"
                });

            Registry.Add("scan.snapshot", "生成目录快照清单（path/size/mtime/hash）写入 JSON 文件",
                "scan snapshot --path <dir> --out <file> [--algo <algo>] [--no-hash] [--exclude-dir <name>]... [--hidden] [--parallel <n>] [--max-results <n>]",
                ctx => Guarded(ctx, RunSnapshot),
                examples: new[]
                {
                    "dsh-toolbox scan snapshot --path C:\\data --out var\\snap.json --json",
                    "dsh-toolbox scan snapshot --path . --out var\\snap.json --dry-run --json"
                });

            Registry.Add("scan.verify", "用快照校验目录（新增/删除/修改/损坏）",
                "scan verify --snapshot <file> [--path <dir>] [--full] [--tolerance <dur>] [--parallel <n>] [--max-results <n>] [--relative]",
                ctx => Guarded(ctx, RunVerify),
                examples: new[]
                {
                    "dsh-toolbox scan verify --snapshot var\\snap.json --json",
                    "dsh-toolbox scan verify --snapshot var\\snap.json --path C:\\data --full --json"
                });

            Registry.Add("scan.recent", "最近修改的文件（默认最近 24 小时）",
                "scan recent --path <dir>... [--newer <dur|date>] [--max-results <n>] [--sort name|path|size|mtime] [--reverse] [--exclude-dir <name>]... [--hidden] [--relative]",
                ctx => Guarded(ctx, RunRecent),
                examples: new[]
                {
                    "dsh-toolbox scan recent --path . --json",
                    "dsh-toolbox scan recent --path C:\\work --newer 2h --max-results 20"
                });

            Registry.Add("scan.empty-dirs", "空目录查找（含无文件子树统计）",
                "scan empty-dirs --path <dir> [--depth <n>] [--exclude-dir <name>]... [--hidden] [--max-results <n>] [--relative]",
                ctx => Guarded(ctx, RunEmptyDirs),
                aliases: new[] { "scan.emptydirs" },
                examples: new[]
                {
                    "dsh-toolbox scan empty-dirs --path C:\\data --json",
                    "dsh-toolbox scan empty-dirs --path . --exclude-dir .git"
                });
        }

        // ============================================================ 通用工具
        internal static int Guarded(Ctx ctx, Func<Ctx, int> body)
        {
            try { return body(ctx); }
            catch (OperationCanceledException)
            {
                throw ToolException.Timeout("操作被取消或超时（--timeout / Ctrl+C）");
            }
        }

        // --newer 2h / --older 30m 之类的裸时长由 Core 的 ArgMap.TryParseDate 直接归一化
        // （Lead 已修 Cli.cs），本模块不再自己解析时间，避免出现第二套语义。

        /// <summary>
        /// 探测当前 Core 的 --jsonl 收尾是否会重复发送"已经 EmitItem 过"的条目。
        /// 原理：用一个一次性 Output 实例（JsonLines=true）先 EmitItem 一条，再 Result 收尾，
        /// 把 Console.Out 临时重定向到 StringWriter 数一下 type=item 的条数：
        ///   1 条  → 收尾只在未流式时才补发（理想），可以放心边扫边发；
        ///   ≥2 条 → ItemsOf 在 _items 非空时返回 _items 本身，收尾会把流式条目再发一遍，此时必须关闭流式；
        ///   抛异常 → 旧版 Core（"集合已修改"），同样关闭流式。
        /// 全程只用公开 API，不产生任何真实 stdout，也不改 Core。
        /// </summary>
        internal static bool JsonlFlushDuplicatesStreamed()
        {
            var saved = Console.Out;
            try
            {
                var probe = new Output { JsonLines = true };
                using (var sw = new StringWriter())
                {
                    Console.SetOut(sw);                       // 必须先重定向，EmitItem 在 jsonl 下会立刻写 stdout
                    probe.EmitItem(Json.Obj("probe", 1));
                    probe.Result("probe", Json.Obj("items", Json.Arr()));
                    string text = sw.ToString();
                    int n = 0, idx = 0;
                    while ((idx = text.IndexOf("\"type\":\"item\"", idx, StringComparison.Ordinal)) >= 0) { n++; idx += 5; }
                    return n != 1;
                }
            }
            catch { return true; }
            finally { Console.SetOut(saved); }
        }

        /// <summary>扫描根：--path（可重复）优先，其次位置参数。已展开为绝对路径。</summary>
        internal static List<string> Roots(Ctx ctx, List<object> failures)
        {
            var raw = new List<string>();
            raw.AddRange(ctx.GetAll("path"));
            raw.AddRange(ctx.Args.Positional);
            var list = new List<string>();
            foreach (var p in raw)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                string full = Fs.Expand(p, ctx.Cwd);
                if (full != null && !list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
            }
            if (list.Count == 0)
                throw ToolException.Usage("缺少扫描根目录 --path", "例：scan find --path . --include \"**/*.log\"");
            return list;
        }

        internal static List<string> ValidateRoots(List<string> roots, List<object> failures)
        {
            var ok = new List<string>();
            foreach (var r in roots)
            {
                if (Directory.Exists(Fs.LongPath(r))) { ok.Add(r); continue; }
                if (File.Exists(Fs.LongPath(r))) { failures.Add(Json.Obj("path", r, "error", "不是目录（扫描根必须是目录）")); continue; }
                failures.Add(Json.Obj("path", r, "error", "目录不存在"));
            }
            return ok;
        }

        internal static FsScanOptions CloneOpts(FsScanOptions o)
        {
            var c = new FsScanOptions();
            c.Include = new List<string>(o.Include);
            c.Exclude = new List<string>(o.Exclude);
            c.ExcludeDirs = new List<string>(o.ExcludeDirs);
            c.Exts = new List<string>(o.Exts);
            c.NameRegex = o.NameRegex;
            c.MaxDepth = o.MaxDepth;
            c.Hidden = o.Hidden;
            c.FollowLinks = o.FollowLinks;
            c.FilesOnly = o.FilesOnly;
            c.DirsOnly = o.DirsOnly;
            c.MinSize = o.MinSize;
            c.MaxSize = o.MaxSize;
            c.NewerThan = o.NewerThan;
            c.OlderThan = o.OlderThan;
            c.MaxResults = o.MaxResults;
            c.IgnoreCase = o.IgnoreCase;
            return c;
        }

        internal static string CanonAlgo(string a)
        {
            switch ((a ?? "").Trim().ToLowerInvariant())
            {
                case "sha2": case "sha-256": return "sha256";
                case "sha-1": return "sha1";
                case "sha-384": return "sha384";
                case "sha-512": return "sha512";
                default: return (a ?? "").Trim().ToLowerInvariant();
            }
        }

        /// <summary>--hash / --algo / --hash 裸开关（值可能是 "true"）统一归一化。</summary>
        internal static string NormAlgoOrNull(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(v.Trim(), "false", StringComparison.OrdinalIgnoreCase)) return null;
            return CanonAlgo(v);
        }

        internal static HashAlgorithm CreateHasher(string algo)
        {
            switch (CanonAlgo(algo))
            {
                case "md5": return MD5.Create();
                case "sha1": return SHA1.Create();
                case "sha256": return SHA256.Create();
                case "sha384": return SHA384.Create();
                case "sha512": return SHA512.Create();
                default:
                    throw ToolException.Usage("不支持的哈希算法：" + algo, "支持 " + string.Join("/", Fs.SupportedHashes()));
            }
        }

        internal static string HashBytes(byte[] data, string algo)
        {
            using (var h = CreateHasher(algo)) return Fs.ToHex(h.ComputeHash(data));
        }

        internal static ScanHashResult HashProbe(FsEntry e, string algo, CancellationToken ct)
        {
            try { return new ScanHashResult { Hash = Fs.HashFile(e.FullPath, algo, ct) }; }
            catch (OperationCanceledException) { throw; }
            catch (ToolException) { throw; }
            catch (Exception ex) { return new ScanHashResult { Error = ex.GetType().Name + ": " + ex.Message }; }
        }

        internal static long EpochMs(DateTime utc) { return (long)(utc.ToUniversalTime() - Epoch).TotalMilliseconds; }

        internal static DateTime? FromEpochMs(object v)
        {
            if (v == null) return null;
            try { return Epoch.AddMilliseconds(Convert.ToInt64(v, CultureInfo.InvariantCulture)); }
            catch { return null; }
        }

        internal static DateTime? AsDate(object v)
        {
            if (v == null) return null;
            if (v is DateTime) return (DateTime)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(s)) return null;
            DateTime d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) return d;
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        internal static List<Dictionary<string, object>> ObjList(object v)
        {
            var list = new List<Dictionary<string, object>>();
            var en = v as IEnumerable;
            if (en == null || v is string) return list;
            foreach (var x in en)
            {
                var d = x as Dictionary<string, object>;
                if (d != null) list.Add(d);
            }
            return list;
        }

        internal static string NormRel(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            return p.Replace('/', '\\').TrimStart('\\');
        }

        /// <summary>相对根的层数：a → 1，a\b → 2。</summary>
        internal static int RelDepth(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return 0;
            return NormRel(rel).Split('\\').Length;
        }

        // 说明：Core 的 Fs.Enumerate 在 --depth n 下会多吐一层（栈里 depth ≤ n 的目录会被扫描，
        // 目录内的条目在"是否继续下推"判断之前就已经 yield），即实际产出深度到 n+1。
        // 这里按契约 §7「--depth <n> 最大深度」做一次上界过滤，保证用户看到的就是 n 层。
        internal static IEnumerable<FsEntry> WalkMany(IEnumerable<string> roots, FsScanOptions o, int maxDepth)
        {
            foreach (var e in Fs.EnumerateMany(roots, o))
            {
                if (maxDepth > 0 && RelDepth(e.RelPath) > maxDepth) continue;
                yield return e;
            }
        }

        internal static IEnumerable<FsEntry> WalkOne(string root, FsScanOptions o, int maxDepth)
        {
            foreach (var e in Fs.Enumerate(root, o))
            {
                if (maxDepth > 0 && RelDepth(e.RelPath) > maxDepth) continue;
                yield return e;
            }
        }

        internal static long GetLong(Dictionary<string, object> d, string key, long def)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return def;
            try { return Convert.ToInt64(d[key], CultureInfo.InvariantCulture); } catch { return def; }
        }

        internal static long? GetLongOrNull(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return null;
            try { return Convert.ToInt64(d[key], CultureInfo.InvariantCulture); } catch { return null; }
        }

        internal static string FormatAge(DateTime mtimeUtc)
        {
            var span = DateTime.UtcNow - mtimeUtc.ToUniversalTime();
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            if (span.TotalSeconds < 90) return span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s";
            if (span.TotalMinutes < 90) return span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + "m";
            if (span.TotalHours < 48) return span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + "h";
            return span.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + "d";
        }

        static void AddDeniedWarnings(Ctx ctx, FsScanOptions o)
        {
            if (o.Denied.Count > 0) ctx.Out.Warn("跳过 " + o.Denied.Count + " 个无权限目录（见 data.deniedDirs）");
        }

        static List<object> DeniedDirs(FsScanOptions o)
        {
            var list = new List<object>();
            int n = 0;
            foreach (var d in o.Denied)
            {
                if (n++ >= 100) break;
                list.Add(d);
            }
            return list;
        }

        static Dictionary<string, object> HashMap(string algo)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var a in Fs.SupportedHashes()) d[a] = true;
            d["requested"] = algo;
            return d;
        }

        // ============================================================ scan.find
        static int RunFind(Ctx ctx)
        {
            var failures = new List<object>();
            var roots = Roots(ctx, failures);
            var valid = ValidateRoots(roots, failures);
            if (valid.Count == 0) throw ToolException.NotFound("扫描根目录不存在：" + roots[0], "确认 --path 指向已存在的目录");

            var o = FsScanOptions.FromArgs(ctx);
            o.MaxResults = 0;                       // 由本命令按"命中数"统一截断
            int userDepth = ctx.Args.GetInt("depth", 0);
            long maxResults = ctx.Args.GetLong("max-results", 0);
            bool relative = ctx.Flag("relative");
            bool dirsOnly = ctx.Flag("dirs-only");
            bool wantHash = ctx.Args.Has("hash") && !dirsOnly;
            string algo = NormAlgoOrNull(ctx.Get("hash")) ?? "sha256";
            if (wantHash) { using (var probeHasher = CreateHasher(algo)) { } }
            int parallel = ctx.Args.GetInt("parallel", 0);
            bool reverse = ctx.Flag("reverse");

            string sortKey = (ctx.Get("sort") ?? "").Trim().ToLowerInvariant();
            if (sortKey == "time" || sortKey == "modified" || sortKey == "date") sortKey = "mtime";
            if (sortKey.Length > 0 && sortKey != "name" && sortKey != "path" && sortKey != "size" && sortKey != "mtime")
                throw ToolException.Usage("--sort 只支持 name|path|size|mtime，收到：" + sortKey);

            Regex contentRe = null;
            string crx = ctx.Get("content-regex");
            if (!string.IsNullOrWhiteSpace(crx))
            {
                try
                {
                    contentRe = new Regex(crx, RegexOptions.Compiled | (ctx.Flag("ignore-case") ? RegexOptions.IgnoreCase : RegexOptions.None),
                        TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) { throw ToolException.Usage("--content-regex 正则非法：" + ex.Message); }
            }

            // 真流式：扫描过程中就用 Out.EmitItem 逐条发。只有确认 Core 的 jsonl 收尾不会
            // 把已流式的条目再发一遍（见 JsonlFlushDuplicatesStreamed）时才启用，保证输出无重复。
            bool stream = ctx.Out.JsonLines && contentRe == null && sortKey.Length == 0 && !wantHash
                          && !JsonlFlushDuplicatesStreamed();
            var rows = new List<ScanFindRow>();
            long streamed = 0;
            bool truncated = false;
            string truncatedAt = null;
            if (stream) ctx.Out.BeginStream("scan.find");   // 契约 §3：meta 必须是第一帧

            if (contentRe == null)
            {
                foreach (var e in WalkMany(valid, o, userDepth))
                {
                    ctx.ThrowIfCancelled();
                    long have = stream ? streamed : rows.Count;
                    if (maxResults > 0 && have >= maxResults) { truncated = true; truncatedAt = "max-results"; break; }
                    if (stream)
                    {
                        ctx.Out.EmitItem(FindItem(e, relative, null));
                        streamed++;
                    }
                    else rows.Add(new ScanFindRow { Entry = e });
                }
            }
            else
            {
                var batch = new List<FsEntry>();
                bool stop = false;
                foreach (var e in WalkMany(valid, o, userDepth))
                {
                    ctx.ThrowIfCancelled();
                    batch.Add(e);
                    if (batch.Count >= 64)
                    {
                        stop = FlushContent(ctx, batch, contentRe, parallel, maxResults, rows, failures, ref truncated, ref truncatedAt);
                        batch.Clear();
                        if (stop) break;
                    }
                }
                if (!stop && batch.Count > 0)
                    FlushContent(ctx, batch, contentRe, parallel, maxResults, rows, failures, ref truncated, ref truncatedAt);
            }

            if (wantHash && rows.Count > 0)
            {
                var src = new List<FsEntry>();
                foreach (var r in rows) src.Add(r.Entry);
                var hs = Fs.ParallelMap(src, parallel, e => HashProbe(e, algo, ctx.Cancel), ctx.Cancel);
                for (int i = 0; i < rows.Count; i++)
                {
                    if (hs[i].Error != null)
                        failures.Add(Json.Obj("path", src[i].FullPath, "error", hs[i].Error, "op", "hash"));
                    else rows[i].Hash = hs[i].Hash;
                }
            }

            if (sortKey.Length > 0)
            {
                Comparison<ScanFindRow> cmp;
                if (sortKey == "name") cmp = (a, b) => string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase);
                else if (sortKey == "size") cmp = (a, b) => a.Entry.Size.CompareTo(b.Entry.Size);
                else if (sortKey == "mtime") cmp = (a, b) => a.Entry.LastWriteUtc.CompareTo(b.Entry.LastWriteUtc);
                else cmp = (a, b) => string.Compare(a.Entry.FullPath, b.Entry.FullPath, StringComparison.OrdinalIgnoreCase);
                rows.Sort(cmp);
            }
            if (reverse) rows.Reverse();

            var items = new List<object>();
            foreach (var r in rows) items.Add(FindItem(r.Entry, relative, r.Hash));

            long matched = stream ? streamed : items.Count;
            long scanned = dirsOnly ? o.ScannedDirs : o.ScannedFiles;
            long skipped = Math.Max(0, scanned - matched);
            ctx.Out.Truncated = truncated;
            AddDeniedWarnings(ctx, o);

            var cols = wantHash ? new[] { "path", "sizeHuman", "mtime", "hash" } : new[] { "path", "sizeHuman", "mtime" };
            var data = Json.Obj(
                "roots", valid.ToArray(),
                "items", items,
                "count", items.Count,
                "columns", cols,
                "scanned", scanned,
                "scannedDirs", (long)o.ScannedDirs,
                "matched", matched,
                "skipped", skipped,
                "denied", o.Denied.Count,
                "deniedDirs", DeniedDirs(o),
                "failures", failures,
                "partial", failures.Count > 0,
                "streamed", stream,
                "truncatedAt", truncatedAt,
                "hashAlgo", wantHash ? algo : null,
                "hashSupported", wantHash ? HashMap(algo) : null,
                "sort", sortKey.Length > 0 ? sortKey : null,
                "reverse", reverse,
                "parallel", parallel > 0 ? parallel : Environment.ProcessorCount);

            ctx.Out.Result("scan.find", data);
            return failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }

        static bool FlushContent(Ctx ctx, List<FsEntry> batch, Regex re, int parallel, long maxResults,
                                 List<ScanFindRow> rows, List<object> failures, ref bool truncated, ref string truncatedAt)
        {
            var src = new List<FsEntry>(batch);
            var res = Fs.ParallelMap(src, parallel, e => ContentProbe(e, re), ctx.Cancel);
            for (int i = 0; i < src.Count; i++)
            {
                ctx.ThrowIfCancelled();
                if (res[i].Error != null)
                {
                    failures.Add(Json.Obj("path", src[i].FullPath, "error", res[i].Error, "op", "content-regex"));
                    continue;
                }
                if (!res[i].Matched) continue;
                if (maxResults > 0 && rows.Count >= maxResults) { truncated = true; truncatedAt = "max-results"; return true; }
                rows.Add(new ScanFindRow { Entry = src[i] });
            }
            return false;
        }

        static ScanContentResult ContentProbe(FsEntry e, Regex re)
        {
            try
            {
                const long limit = 8 * 1024 * 1024;      // 单文件最多读 8MB 文本
                var sb = new StringBuilder();
                using (var fs = new FileStream(Fs.LongPath(e.FullPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true, 64 * 1024))
                {
                    var buf = new char[64 * 1024];
                    long total = 0;
                    int n;
                    while ((n = sr.Read(buf, 0, buf.Length)) > 0)
                    {
                        sb.Append(buf, 0, n);
                        total += n;
                        if (total >= limit) break;
                    }
                }
                return new ScanContentResult { Matched = re.IsMatch(sb.ToString()) };
            }
            catch (RegexMatchTimeoutException)
            {
                return new ScanContentResult { Error = "正则匹配超时(>5s)" };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new ScanContentResult { Error = ex.GetType().Name + ": " + ex.Message }; }
        }

        static Dictionary<string, object> FindItem(FsEntry e, bool relative, string hash)
        {
            var d = Json.Obj(
                "path", relative ? e.RelPath : e.FullPath,
                "name", e.Name,
                "ext", e.Ext,
                "size", e.Size,
                "sizeHuman", Fs.FormatSize(e.Size),
                "mtime", e.LastWriteUtc.ToLocalTime(),
                "dir", e.IsDir);
            if (!string.IsNullOrEmpty(hash)) d["hash"] = hash;
            return d;
        }

        // ============================================================ scan.size
        static int RunSize(Ctx ctx)
        {
            var failures = new List<object>();
            var roots = Roots(ctx, failures);
            string root = roots[0];
            if (!Directory.Exists(Fs.LongPath(root))) throw ToolException.NotFound("目录不存在：" + root);

            int top = ctx.Args.GetInt("top", 20);
            if (top <= 0) top = 20;
            int parallel = ctx.Args.GetInt("parallel", 0);
            bool relative = ctx.Flag("relative");
            bool dirsOnly = ctx.Flag("dirs-only");

            var o = FsScanOptions.FromArgs(ctx);
            o.MaxResults = 0; o.MinSize = -1; o.MaxSize = -1; o.NewerThan = null; o.OlderThan = null;
            o.MaxDepth = 0; o.DirsOnly = false; o.FilesOnly = true;

            FileSystemInfo[] children;
            try { children = new DirectoryInfo(Fs.LongPath(root)).GetFileSystemInfos(); }
            catch (UnauthorizedAccessException) { throw ToolException.Denied("无法读取目录：" + root); }
            catch (Exception ex) { throw new ToolException("E_IO", "读取目录失败：" + ex.Message, null, ExitCodes.Error); }

            var list = new List<FileSystemInfo>();
            foreach (var c in children)
            {
                if (!o.Hidden && (c.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if ((c.Attributes & FileAttributes.ReparsePoint) != 0 && !o.FollowLinks) continue;
                list.Add(c);
            }

            var probe = new List<FileSystemInfo>(list);
            var stats = Fs.ParallelMap(probe, parallel, c => ChildStatOf(c, o, ctx.Cancel), ctx.Cancel);

            var pairs = new List<KeyValuePair<FileSystemInfo, ScanChildStat>>();
            for (int i = 0; i < probe.Count; i++) pairs.Add(new KeyValuePair<FileSystemInfo, ScanChildStat>(probe[i], stats[i]));
            pairs.Sort((a, b) => b.Value.Size.CompareTo(a.Value.Size));

            long total = 0; long files = 0;
            foreach (var p in pairs)
            {
                total += p.Value.Size;
                files += p.Value.Files;
                foreach (var f in p.Value.Failures) failures.Add(f);
                if (p.Value.Denied.Count > 0 && o.Denied.Count < 5000) o.Denied.AddRange(p.Value.Denied);
            }

            if (dirsOnly)
            {
                var keep = new List<KeyValuePair<FileSystemInfo, ScanChildStat>>();
                foreach (var p in pairs)
                    if ((p.Key.Attributes & FileAttributes.Directory) != 0) keep.Add(p);
                pairs = keep;
            }

            int shown = Math.Min(top, pairs.Count);
            var items = new List<object>();
            for (int i = 0; i < shown; i++)
            {
                var c = pairs[i].Key;
                var st = pairs[i].Value;
                bool isDir = (c.Attributes & FileAttributes.Directory) != 0;
                double percent = total > 0 ? Math.Round(st.Size * 100.0 / total, 2) : 0.0;
                items.Add(Json.Obj(
                    "name", c.Name,
                    "path", relative ? RelOf(root, c.FullName) : c.FullName,
                    "type", isDir ? "dir" : "file",
                    "size", st.Size,
                    "sizeHuman", Fs.FormatSize(st.Size),
                    "percent", percent,
                    "files", st.Files));
            }

            bool truncated = pairs.Count > shown;
            ctx.Out.Truncated = truncated;
            AddDeniedWarnings(ctx, o);

            var data = Json.Obj(
                "root", root,
                "items", items,
                "count", items.Count,
                "columns", new[] { "type", "name", "sizeHuman", "percent", "files" },
                "totalSize", total,
                "totalHuman", Fs.FormatSize(total),
                "childCount", pairs.Count,
                "scanned", files,
                "scannedDirs", (long)o.ScannedDirs,
                "matched", items.Count,
                "skipped", Math.Max(0, pairs.Count - shown),
                "denied", o.Denied.Count,
                "deniedDirs", DeniedDirs(o),
                "failures", failures,
                "partial", failures.Count > 0,
                "top", top,
                "dirsOnly", dirsOnly,
                "truncatedAt", truncated ? "top" : null,
                "parallel", parallel > 0 ? parallel : Environment.ProcessorCount);

            ctx.Out.Result("scan.size", data);
            return failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }

        static ScanChildStat ChildStatOf(FileSystemInfo c, FsScanOptions proto, CancellationToken ct)
        {
            var st = new ScanChildStat();
            var fi = c as FileInfo;
            if (fi != null) { st.Size = fi.Length; st.Files = 1; return st; }
            var opts = CloneOpts(proto);
            try
            {
                foreach (var e in Fs.Enumerate(c.FullName, opts))
                {
                    ct.ThrowIfCancellationRequested();
                    st.Size += e.Size;
                    st.Files++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { st.Failures.Add(Json.Obj("path", c.FullName, "error", ex.GetType().Name + ": " + ex.Message)); }
            st.Denied.AddRange(opts.Denied);
            return st;
        }

        static string RelOf(string root, string full)
        {
            string prefix = root.TrimEnd('\\') + "\\";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return full.Substring(prefix.Length);
            return full;
        }

        // ============================================================ scan.tree
        static int RunTree(Ctx ctx)
        {
            var failures = new List<object>();
            var roots = Roots(ctx, failures);
            string root = roots[0];
            if (!Directory.Exists(Fs.LongPath(root))) throw ToolException.NotFound("目录不存在：" + root);

            int depth = ctx.Args.GetInt("depth", 3);
            if (depth < 0) depth = 0;
            long maxNodes = ctx.Args.GetLong("max-results", 200);
            if (maxNodes <= 0) maxNodes = 200;
            bool relative = ctx.Flag("relative");
            bool dirsOnly = ctx.Flag("dirs-only");
            string[] excludes = ctx.GetAll("exclude");

            var o = FsScanOptions.FromArgs(ctx);
            o.MaxResults = 0;
            o.DirsOnly = true; o.FilesOnly = false;
            o.MaxDepth = depth;
            int userDepth = depth;                     // 树的最大层数就是 --depth
            o.Include.Clear(); o.Exts.Clear(); o.NameRegex = null;
            o.MinSize = -1; o.MaxSize = -1; o.NewerThan = null; o.OlderThan = null;

            var dirs = new List<FsEntry>();
            bool truncated = false;
            foreach (var e in WalkOne(root, o, userDepth))
            {
                ctx.ThrowIfCancelled();
                if (dirs.Count >= maxNodes) { truncated = true; break; }
                dirs.Add(e);
            }

            var nodes = new Dictionary<string, ScanTreeNode>(StringComparer.OrdinalIgnoreCase);
            var rootNode = new ScanTreeNode { Path = root, Name = new DirectoryInfo(Fs.LongPath(root)).Name, Rel = "", Depth = 0, IsDir = true };
            if (string.IsNullOrEmpty(rootNode.Name)) rootNode.Name = root;
            nodes[root] = rootNode;
            foreach (var d in dirs)
            {
                var n = new ScanTreeNode
                {
                    Path = d.FullPath,
                    Name = d.Name,
                    Rel = d.RelPath,
                    Depth = d.RelPath.Split('\\').Length,
                    IsDir = true
                };
                nodes[n.Path] = n;
            }
            foreach (var n in nodes.Values)
            {
                if (n.Depth == 0) continue;
                string parent = Path.GetDirectoryName(n.Path);
                ScanTreeNode p;
                if (parent != null && nodes.TryGetValue(parent, out p)) p.Children.Add(n);
                else rootNode.Children.Add(n);
            }

            foreach (var n in nodes.Values)
            {
                if (dirsOnly) continue;
                ctx.ThrowIfCancelled();
                n.TotalSize = 0;
                try
                {
                    var di = new DirectoryInfo(Fs.LongPath(n.Path));
                    foreach (var f in di.GetFiles())
                    {
                        if (!o.Hidden && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        string rel = n.Rel.Length == 0 ? f.Name : n.Rel + "\\" + f.Name;
                        if (excludes.Length > 0 && Glob.MatchAny(excludes, f.Name, rel)) continue;
                        n.Files++;
                        n.TotalSize += f.Length;
                    }
                }
                catch (UnauthorizedAccessException) { failures.Add(Json.Obj("path", n.Path, "error", "拒绝访问")); }
                catch (Exception ex) { failures.Add(Json.Obj("path", n.Path, "error", ex.GetType().Name + ": " + ex.Message)); }
            }

            var order = new List<ScanTreeNode>(nodes.Values);
            order.Sort((a, b) => b.Depth.CompareTo(a.Depth));
            foreach (var n in order)
            {
                n.SubtreeSize = n.TotalSize;
                n.SubtreeFiles = n.Files;
                foreach (var ch in n.Children)
                {
                    n.SubtreeSize += ch.SubtreeSize;
                    n.SubtreeFiles += ch.SubtreeFiles;
                }
            }

            var items = new List<object>();
            var levels = new Dictionary<int, long[]>();
            var stack = new Stack<ScanTreeNode>();
            stack.Push(rootNode);
            while (stack.Count > 0)
            {
                ctx.ThrowIfCancelled();
                var n = stack.Pop();
                long[] lv;
                if (!levels.TryGetValue(n.Depth, out lv)) { lv = new long[3]; levels[n.Depth] = lv; }
                lv[0]++;
                lv[1] += n.Files;
                lv[2] += n.TotalSize;

                items.Add(Json.Obj(
                    "tree", new string(' ', Math.Min(n.Depth, 24) * 2) + n.Name + "/",
                    "name", n.Name + "/",
                    "path", relative ? (n.Depth == 0 ? "." : n.Rel) : n.Path,
                    "rel", n.Rel,
                    "depth", n.Depth,
                    "dir", true,
                    "size", n.SubtreeSize,
                    "sizeHuman", Fs.FormatSize(n.SubtreeSize),
                    "files", n.SubtreeFiles,
                    "directFiles", n.Files,
                    "childDirs", n.Children.Count));

                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
                if (items.Count >= maxNodes + 1) { truncated = items.Count < nodes.Count; break; }
            }

            var levelItems = new List<object>();
            var levelKeys = new List<int>(levels.Keys);
            levelKeys.Sort();
            foreach (var k in levelKeys)
            {
                var lv = levels[k];
                levelItems.Add(Json.Obj("depth", k, "dirs", lv[0], "files", lv[1], "size", lv[2], "sizeHuman", Fs.FormatSize(lv[2])));
            }

            ctx.Out.Truncated = truncated;
            AddDeniedWarnings(ctx, o);

            var data = Json.Obj(
                "root", root,
                "items", items,
                "count", items.Count,
                "columns", new[] { "tree", "type", "sizeHuman", "files" },
                "levels", levelItems,
                "depthLimit", depth,
                "dirCount", dirs.Count,
                "scanned", (long)o.ScannedDirs,
                "scannedDirs", (long)o.ScannedDirs,
                "matched", items.Count,
                "skipped", Math.Max(0, o.ScannedDirs - items.Count),
                "denied", o.Denied.Count,
                "deniedDirs", DeniedDirs(o),
                "failures", failures,
                "partial", failures.Count > 0,
                "truncatedAt", truncated ? "max-results" : null,
                "dirsOnly", dirsOnly);

            ctx.Out.Result("scan.tree", data);
            return failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }

        // ============================================================ scan.dup
        static int RunDup(Ctx ctx)
        {
            var failures = new List<object>();
            var roots = Roots(ctx, failures);
            var valid = ValidateRoots(roots, failures);
            if (valid.Count == 0) throw ToolException.NotFound("扫描根目录不存在：" + roots[0]);

            var o = FsScanOptions.FromArgs(ctx);
            o.MaxResults = 0;
            o.FilesOnly = true; o.DirsOnly = false;
            if (!ctx.Args.Has("min-size")) o.MinSize = 1;              // 默认跳过 0 字节噪声
            int userDepth = ctx.Args.GetInt("depth", 0);
            long minCount = ctx.Args.GetLong("min-count", 2);
            if (minCount < 2) minCount = 2;
            long maxGroups = ctx.Args.GetLong("max-results", 0);
            int parallel = ctx.Args.GetInt("parallel", 0);
            string algo = CanonAlgo(NormAlgoOrNull(ctx.Get("algo")) ?? NormAlgoOrNull(ctx.Get("hash")) ?? "sha256");
            if (algo == "crc32") throw ToolException.Usage("scan.dup 不支持 crc32 确认", "请用 md5/sha1/sha256/sha384/sha512");
            bool relative = ctx.Flag("relative");

            var files = new List<FsEntry>();
            foreach (var e in WalkMany(valid, o, userDepth))
            {
                ctx.ThrowIfCancelled();
                files.Add(e);
            }

            // 阶段 1：按 size 分组
            var bySize = new Dictionary<long, List<FsEntry>>();
            foreach (var f in files)
            {
                List<FsEntry> l;
                if (!bySize.TryGetValue(f.Size, out l)) { l = new List<FsEntry>(); bySize[f.Size] = l; }
                l.Add(f);
            }
            var sizeGroups = new List<List<FsEntry>>();
            foreach (var kv in bySize) if (kv.Value.Count >= minCount) sizeGroups.Add(kv.Value);

            // 阶段 2：Fs.QuickHash 预筛
            var quickGroups = new List<List<FsEntry>>();
            long quickHashed = 0;
            foreach (var g in sizeGroups)
            {
                ctx.ThrowIfCancelled();
                var src = g;
                var qs = Fs.ParallelMap(src, parallel, e => QuickProbe(e, ctx.Cancel), ctx.Cancel);
                quickHashed += src.Count;
                var map = new Dictionary<string, List<FsEntry>>(StringComparer.Ordinal);
                for (int i = 0; i < src.Count; i++)
                {
                    if (qs[i].Error != null)
                    {
                        failures.Add(Json.Obj("path", src[i].FullPath, "error", qs[i].Error, "op", "quick-hash"));
                        continue;
                    }
                    List<FsEntry> l;
                    if (!map.TryGetValue(qs[i].Hash, out l)) { l = new List<FsEntry>(); map[qs[i].Hash] = l; }
                    l.Add(src[i]);
                }
                foreach (var kv in map) if (kv.Value.Count >= minCount) quickGroups.Add(kv.Value);
            }

            // 阶段 3：全量哈希确认
            var groups = new List<Dictionary<string, object>>();
            long fullHashed = 0;
            long duplicateFiles = 0;
            long reclaimable = 0;
            foreach (var g in quickGroups)
            {
                ctx.ThrowIfCancelled();
                var src = g;
                var hs = Fs.ParallelMap(src, parallel, e => HashProbe(e, algo, ctx.Cancel), ctx.Cancel);
                fullHashed += src.Count;
                var map = new Dictionary<string, List<FsEntry>>(StringComparer.Ordinal);
                for (int i = 0; i < src.Count; i++)
                {
                    if (hs[i].Error != null)
                    {
                        failures.Add(Json.Obj("path", src[i].FullPath, "error", hs[i].Error, "op", "hash"));
                        continue;
                    }
                    List<FsEntry> l;
                    if (!map.TryGetValue(hs[i].Hash, out l)) { l = new List<FsEntry>(); map[hs[i].Hash] = l; }
                    l.Add(src[i]);
                }
                foreach (var kv in map)
                {
                    if (kv.Value.Count < minCount) continue;
                    var names = new List<object>();
                    foreach (var f in kv.Value) names.Add(relative ? f.RelPath : f.FullPath);
                    names.Sort((x, y) => string.Compare(Convert.ToString(x), Convert.ToString(y), StringComparison.OrdinalIgnoreCase));
                    long size = kv.Value[0].Size;
                    long waste = size * (kv.Value.Count - 1);
                    duplicateFiles += kv.Value.Count;
                    reclaimable += waste;
                    groups.Add(Json.Obj(
                        "hash", kv.Key,
                        "algo", algo,
                        "size", size,
                        "sizeHuman", Fs.FormatSize(size),
                        "count", kv.Value.Count,
                        "reclaimable", waste,
                        "reclaimableHuman", Fs.FormatSize(waste),
                        "files", names));
                }
            }

            groups.Sort((a, b) => GetLong((Dictionary<string, object>)b, "reclaimable", 0).CompareTo(GetLong((Dictionary<string, object>)a, "reclaimable", 0)));

            bool truncated = false;
            var outGroups = new List<object>();
            foreach (var g in groups)
            {
                if (maxGroups > 0 && outGroups.Count >= maxGroups) { truncated = true; break; }
                outGroups.Add(g);
            }

            long scanned = o.ScannedFiles;
            ctx.Out.Truncated = truncated;
            AddDeniedWarnings(ctx, o);

            var data = Json.Obj(
                "roots", valid.ToArray(),
                "items", outGroups,
                "count", outGroups.Count,
                "columns", new[] { "sizeHuman", "count", "reclaimableHuman", "hash", "files" },
                "groupCount", groups.Count,
                "duplicateFiles", duplicateFiles,
                "duplicatedBytes", reclaimable,
                "reclaimable", reclaimable,
                "reclaimableHuman", Fs.FormatSize(reclaimable),
                "scanned", scanned,
                "scannedDirs", (long)o.ScannedDirs,
                "matched", duplicateFiles,
                "skipped", Math.Max(0, scanned - duplicateFiles),
                "sizeCandidates", sizeGroups.Count,
                "quickCandidates", quickGroups.Count,
                "quickHashed", quickHashed,
                "fullHashed", fullHashed,
                "algo", algo,
                "minSize", o.MinSize,
                "minCount", minCount,
                "denied", o.Denied.Count,
                "deniedDirs", DeniedDirs(o),
                "failures", failures,
                "partial", failures.Count > 0,
                "truncatedAt", truncated ? "max-results" : null,
                "parallel", parallel > 0 ? parallel : Environment.ProcessorCount);

            ctx.Out.Result("scan.dup", data);
            return failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }

        static ScanHashResult QuickProbe(FsEntry e, CancellationToken ct)
        {
            try { return new ScanHashResult { Hash = Fs.QuickHash(e.FullPath, ct) }; }
            catch (OperationCanceledException) { throw; }
            catch (ToolException) { throw; }
            catch (Exception ex) { return new ScanHashResult { Error = ex.GetType().Name + ": " + ex.Message }; }
        }

        // ============================================================ scan.snapshot
        static int RunSnapshot(Ctx ctx)
        {
            var failures = new List<object>();
            var roots = Roots(ctx, failures);
            if (roots.Count > 1) throw ToolException.Usage("scan.snapshot 一次只接受一个根目录（收到 " + roots.Count + " 个）");
            string root = roots[0];
            if (!Directory.Exists(Fs.LongPath(root))) throw ToolException.NotFound("目录不存在：" + root);

            string outArg = ctx.Get("out");
            if (string.IsNullOrWhiteSpace(outArg))
                throw ToolException.Usage("缺少 --out <文件>", "例：scan snapshot --path . --out var\\snap.json");
            string outPath = Fs.Expand(outArg, ctx.Cwd);

            bool noHash = ctx.Flag("no-hash");
            string algo = CanonAlgo(NormAlgoOrNull(ctx.Get("algo")) ?? NormAlgoOrNull(ctx.Get("hash")) ?? "sha256");
            if (!noHash) CanonAlgo(algo);
            int parallel = ctx.Args.GetInt("parallel", 0);
            long maxResults = ctx.Args.GetLong("max-results", 0);

            var o = FsScanOptions.FromArgs(ctx);
            o.MaxResults = 0; o.FilesOnly = true; o.DirsOnly = false;
            int userDepth = ctx.Args.GetInt("depth", 0);
            bool relative = ctx.Flag("relative");

            var entries = new List<FsEntry>();
            bool truncated = false;
            foreach (var e in WalkOne(root, o, userDepth))
            {
                ctx.ThrowIfCancelled();
                if (maxResults > 0 && entries.Count >= maxResults) { truncated = true; break; }
                entries.Add(e);
            }

            var hashes = new string[entries.Count];
            if (!noHash && entries.Count > 0)
            {
                var hs = Fs.ParallelMap(entries, parallel, e => HashProbe(e, algo, ctx.Cancel), ctx.Cancel);
                for (int i = 0; i < entries.Count; i++)
                {
                    if (hs[i].Error != null) failures.Add(Json.Obj("path", entries[i].FullPath, "error", hs[i].Error, "op", "hash"));
                    else hashes[i] = hs[i].Hash;
                }
            }

            long totalSize = 0;
            var fileEntries = new List<object>();   // 写入快照文件：path 恒为相对根路径（可移植、可跨机校验）
            var items = new List<object>();         // 信封/表格：按契约 §5，path 默认绝对，--relative 转相对
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                totalSize += e.Size;
                string rel = NormRel(e.RelPath);
                long ms = EpochMs(e.LastWriteUtc);
                var fe = Json.Obj(
                    "path", rel,
                    "full", e.FullPath,
                    "size", e.Size,
                    "mtimeMs", ms,
                    "mtime", e.LastWriteUtc,
                    "attrs", e.Attributes.ToString());
                var it = Json.Obj(
                    "path", relative ? e.RelPath : e.FullPath,
                    "rel", rel,
                    "size", e.Size,
                    "sizeHuman", Fs.FormatSize(e.Size),
                    "mtimeMs", ms,
                    "mtime", e.LastWriteUtc.ToLocalTime());
                if (!noHash && hashes[i] != null) { fe["hash"] = hashes[i]; it["hash"] = hashes[i]; }
                fileEntries.Add(fe);
                items.Add(it);
            }

            var snapshot = Json.Obj(
                "tool", ToolInfo.Name,
                "version", ToolInfo.Version,
                "kind", "scan.snapshot",
                "created", DateTime.Now,
                "root", root,
                "algo", noHash ? null : algo,
                "hashed", !noHash,
                "count", fileEntries.Count,
                "totalSize", totalSize,
                "truncated", truncated,
                "entries", fileEntries);

            bool exists = File.Exists(Fs.LongPath(outPath));
            var plan = Json.Obj("action", exists ? "overwrite" : "create", "out", outPath, "count", items.Count, "totalSize", totalSize);

            long bytesWritten = 0;
            if (ctx.DryRun)
            {
                ctx.Out.Warn("--dry-run：未写任何文件");
            }
            else
            {
                if (exists && !ctx.Yes)
                    throw ToolException.NeedsYes("覆盖已存在的快照文件 " + outPath);
                string dir = null;
                try { dir = Path.GetDirectoryName(outPath); } catch { }
                Fs.EnsureDir(dir);
                try
                {
                    File.WriteAllText(Fs.LongPath(outPath), Json.Write(snapshot, true), new UTF8Encoding(false));
                    bytesWritten = new FileInfo(Fs.LongPath(outPath)).Length;
                }
                catch (UnauthorizedAccessException) { throw ToolException.Denied("无法写入快照文件：" + outPath); }
                catch (Exception ex) { throw new ToolException("E_IO", "写入快照失败：" + ex.Message, "检查 --out 路径", ExitCodes.Error); }
            }

            ctx.Out.Truncated = truncated;
            AddDeniedWarnings(ctx, o);

            var data = Json.Obj(
                "root", root,
                "out", outPath,
                "dryRun", ctx.DryRun,
                "plan", plan,
                "items", items,
                "count", items.Count,
                "columns", new[] { "path", "size", "mtime", "hash" },
                "totalSize", totalSize,
                "totalHuman", Fs.FormatSize(totalSize),
                "bytesWritten", bytesWritten,
                "algo", noHash ? null : algo,
                "hashed", !noHash,
                "scanned", o.ScannedFiles,
                "matched", items.Count,
                "skipped", Math.Max(0, o.ScannedFiles - items.Count),
                "denied", o.Denied.Count,
                "deniedDirs", DeniedDirs(o),
                "failures", failures,
                "partial", failures.Count > 0,
                "truncatedAt", truncated ? "max-results" : null,
                "parallel", parallel > 0 ? parallel : Environment.ProcessorCount);

            ctx.Out.Result("scan.snapshot", data);
            return failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }

        // ============================================================ scan.verify
        static int RunVerify(Ctx ctx)
        {
            var failures = new List<object>();
            string snapArg = ctx.Get("snapshot");
            if (string.IsNullOrWhiteSpace(snapArg)) snapArg = ctx.Get("from");
            if (string.IsNullOrWhiteSpace(snapArg))
                throw ToolException.Usage("缺少 --snapshot <文件>", "先用 scan snapshot --path <dir> --out <文件> 生成快照");
            string snapPath = Fs.Expand(snapArg, ctx.Cwd);
            if (!File.Exists(Fs.LongPath(snapPath))) throw ToolException.NotFound("快照文件不存在：" + snapPath);

            Dictionary<string, object> snap;
            try { snap = Json.ParseObject(File.ReadAllText(Fs.LongPath(snapPath), Encoding.UTF8)); }
            catch (Exception ex) { throw new ToolException("E_JSON", "快照文件不是合法 JSON：" + ex.Message, "重新生成快照", ExitCodes.Error); }

            var candidates = new List<string>();
            foreach (var p in ctx.GetAll("path")) if (!string.IsNullOrWhiteSpace(p)) candidates.Add(Fs.Expand(p, ctx.Cwd));
            foreach (var p in ctx.Args.Positional) if (!string.IsNullOrWhiteSpace(p)) candidates.Add(Fs.Expand(p, ctx.Cwd));
            string snapRoot = Json.GetString(snap, "root");
            string root = candidates.Count > 0 ? candidates[0] : (string.IsNullOrWhiteSpace(snapRoot) ? null : Fs.Expand(snapRoot, ctx.Cwd));
            if (string.IsNullOrWhiteSpace(root)) throw ToolException.Usage("快照中没有 root，请用 --path 指定目录");
            if (!Directory.Exists(Fs.LongPath(root))) throw ToolException.NotFound("目录不存在：" + root);

            string algo = CanonAlgo(Json.GetString(snap, "algo", "sha256") ?? "sha256");
            bool deep = ctx.Flag("full") || ctx.Flag("checksum");
            // 默认 0 容差：NTFS 时间戳精度 100ns，任何真实修改都会改 mtime。
            // 若在 FAT 等粗粒度时间戳卷上出现误报，可显式 --tolerance 2s。
            TimeSpan tol = ctx.Args.GetSpan("tolerance", TimeSpan.Zero);
            long tolMs = (long)tol.TotalMilliseconds;
            long maxResults = ctx.Args.GetLong("max-results", 0);
            int parallel = ctx.Args.GetInt("parallel", 0);
            bool relative = ctx.Flag("relative");

            var expected = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var entriesRaw = snap.ContainsKey("entries") ? snap["entries"] : null;
            foreach (var en in ObjList(entriesRaw))
            {
                string p = Json.GetString(en, "path");
                if (string.IsNullOrEmpty(p)) continue;
                expected[NormRel(p)] = en;
            }

            var o = FsScanOptions.FromArgs(ctx);
            o.MaxResults = 0; o.FilesOnly = true; o.DirsOnly = false;
            o.MinSize = -1; o.MaxSize = -1; o.NewerThan = null; o.OlderThan = null;
            int userDepth = ctx.Args.GetInt("depth", 0);

            var current = new List<FsEntry>();
            foreach (var e in WalkOne(root, o, userDepth))
            {
                ctx.ThrowIfCancelled();
                current.Add(e);
            }
            var curMap = new Dictionary<string, FsEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in current) curMap[NormRel(e.RelPath)] = e;

            var rows = new List<KeyValuePair<string, object>>();
            long added = 0, removed = 0, modified = 0, corrupted = 0, unchanged = 0;
            var toHash = new List<FsEntry>();
            var toHashExpected = new List<Dictionary<string, object>>();

            foreach (var e in current)
            {
                ctx.ThrowIfCancelled();
                string rel = NormRel(e.RelPath);
                string disp = relative ? e.RelPath : e.FullPath;
                Dictionary<string, object> old;
                if (!expected.TryGetValue(rel, out old))
                {
                    added++;
                    rows.Add(new KeyValuePair<string, object>(rel, Json.Obj(
                        "kind", "added", "path", disp, "rel", rel, "detail", "新增（" + Fs.FormatSize(e.Size) + "）",
                        "size", e.Size, "sizeHuman", Fs.FormatSize(e.Size), "mtime", e.LastWriteUtc.ToLocalTime())));
                    continue;
                }

                long oldSize = GetLong(old, "size", -1);
                long? oldMs = GetLongOrNull(old, "mtimeMs");
                if (oldMs == null)
                {
                    var d0 = AsDate(old.ContainsKey("mtime") ? old["mtime"] : null);
                    if (d0.HasValue) oldMs = EpochMs(d0.Value.ToUniversalTime());
                }
                long curMs = EpochMs(e.LastWriteUtc);
                string oldHash = Json.GetString(old, "hash");

                if (oldSize >= 0 && oldSize != e.Size)
                {
                    modified++;
                    rows.Add(new KeyValuePair<string, object>(rel, Json.Obj(
                        "kind", "modified", "path", disp, "rel", rel,
                        "detail", "大小 " + Fs.FormatSize(oldSize) + " → " + Fs.FormatSize(e.Size),
                        "reason", "size", "size", e.Size, "oldSize", oldSize, "mtime", e.LastWriteUtc.ToLocalTime())));
                }
                else if (oldMs.HasValue && Math.Abs(curMs - oldMs.Value) > tolMs)
                {
                    modified++;
                    rows.Add(new KeyValuePair<string, object>(rel, Json.Obj(
                        "kind", "modified", "path", disp, "rel", rel,
                        "detail", "mtime 变化 " + oldMs.Value + " → " + curMs,
                        "reason", "mtime", "mtimeMs", curMs, "oldMtimeMs", oldMs.Value, "size", e.Size,
                        "mtime", e.LastWriteUtc.ToLocalTime())));
                }
                else if (deep && !string.IsNullOrEmpty(oldHash))
                {
                    toHash.Add(e);
                    toHashExpected.Add(old);
                }
                else unchanged++;
            }

            if (toHash.Count > 0)
            {
                var hs = Fs.ParallelMap(toHash, parallel, e => HashProbe(e, algo, ctx.Cancel), ctx.Cancel);
                for (int i = 0; i < toHash.Count; i++)
                {
                    var e = toHash[i];
                    string rel = NormRel(e.RelPath);
                    string disp = relative ? e.RelPath : e.FullPath;
                    if (hs[i].Error != null)
                    {
                        failures.Add(Json.Obj("path", e.FullPath, "error", hs[i].Error, "op", "hash"));
                        continue;
                    }
                    string exp = Json.GetString(toHashExpected[i], "hash");
                    if (!string.Equals(hs[i].Hash, exp, StringComparison.OrdinalIgnoreCase))
                    {
                        corrupted++;
                        rows.Add(new KeyValuePair<string, object>(rel, Json.Obj(
                            "kind", "corrupted", "path", disp, "rel", rel,
                            "detail", "内容哈希不一致（size 相同）",
                            "reason", "content", "size", e.Size, "hash", hs[i].Hash, "oldHash", exp,
                            "mtime", e.LastWriteUtc.ToLocalTime())));
                    }
                    else unchanged++;
                }
            }

            foreach (var kv in expected)
            {
                ctx.ThrowIfCancelled();
                if (curMap.ContainsKey(kv.Key)) continue;
                removed++;
                long oldSize = GetLong(kv.Value, "size", -1);
                string disp = relative ? kv.Key : Path.Combine(root, kv.Key);
                rows.Add(new KeyValuePair<string, object>(kv.Key, Json.Obj(
                    "kind", "removed", "path", disp, "rel", kv.Key,
                    "detail", "已删除" + (oldSize >= 0 ? "（原 " + Fs.FormatSize(oldSize) + "）" : ""),
                    "oldSize", oldSize, "oldHash", Json.GetString(kv.Value, "hash"))));
            }

            rows.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

            bool truncated = false;
            var items = new List<object>();
            foreach (var r in rows)
            {
                if (maxResults > 0 && items.Count >= maxResults) { truncated = true; break; }
                items.Add(r.Value);
            }

            bool consistent = added == 0 && removed == 0 && modified == 0 && corrupted == 0;
            ctx.Out.Truncated = truncated;
            AddDeniedWarnings(ctx, o);
            if (!consistent) ctx.Out.Warn("检测到差异：新增 " + added + " / 删除 " + removed + " / 修改 " + modified + " / 损坏 " + corrupted);

            var data = Json.Obj(
                "snapshot", snapPath,
                "root", root,
                "algo", algo,
                "deep", deep,
                "toleranceMs", tolMs,
                "consistent", consistent,
                "verdict", consistent,
                "reason", consistent ? null : ("新增 " + added + " / 删除 " + removed + " / 修改 " + modified + " / 损坏 " + corrupted),
                "items", items,
                "count", items.Count,
                "columns", new[] { "kind", "path", "detail" },
                "added", added,
                "removed", removed,
                "modified", modified,
                "corrupted", corrupted,
                "unchanged", unchanged,
                "snapshotCount", expected.Count,
                "currentCount", current.Count,
                "scanned", current.Count,
                "matched", rows.Count,
                "skipped", unchanged,
                "denied", o.Denied.Count,
                "deniedDirs", DeniedDirs(o),
                "failures", failures,
                "partial", failures.Count > 0,
                "truncatedAt", truncated ? "max-results" : null,
                "parallel", parallel > 0 ? parallel : Environment.ProcessorCount);

            ctx.Out.Result("scan.verify", data);
            if (failures.Count > 0) return ExitCodes.Partial;      // I/O 失败 → 6
            return consistent ? ExitCodes.Ok : ExitCodes.Error;    // §4.1 结论为否 → 1
        }

        // ============================================================ scan.recent
        static int RunRecent(Ctx ctx)
        {
            var failures = new List<object>();
            var roots = Roots(ctx, failures);
            var valid = ValidateRoots(roots, failures);
            if (valid.Count == 0) throw ToolException.NotFound("扫描根目录不存在：" + roots[0]);

            var o = FsScanOptions.FromArgs(ctx);
            o.MaxResults = 0; o.FilesOnly = true; o.DirsOnly = false;
            long userMax = ctx.Args.GetLong("max-results", 0);
            long limit = userMax > 0 ? userMax : 50;
            int userDepth = ctx.Args.GetInt("depth", 0);
            DateTime since = o.NewerThan.HasValue ? o.NewerThan.Value : DateTime.Now.AddHours(-24);
            o.NewerThan = since;

            string sortKey = (ctx.Get("sort") ?? "mtime").Trim().ToLowerInvariant();
            if (sortKey == "time" || sortKey == "date" || sortKey == "modified") sortKey = "mtime";
            if (sortKey != "name" && sortKey != "path" && sortKey != "size" && sortKey != "mtime")
                throw ToolException.Usage("--sort 只支持 name|path|size|mtime，收到：" + sortKey);
            bool reverse = ctx.Flag("reverse");
            bool relative = ctx.Flag("relative");

            var rows = new List<FsEntry>();
            foreach (var e in WalkMany(valid, o, userDepth))
            {
                ctx.ThrowIfCancelled();
                rows.Add(e);
            }

            Comparison<FsEntry> cmp;
            if (sortKey == "name") cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            else if (sortKey == "path") cmp = (a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
            else if (sortKey == "size") cmp = (a, b) => a.Size.CompareTo(b.Size);
            else cmp = (a, b) => a.LastWriteUtc.CompareTo(b.LastWriteUtc);
            rows.Sort(cmp);
            bool desc = sortKey == "mtime";
            if (reverse) desc = !desc;
            if (desc) rows.Reverse();

            bool truncated = rows.Count > limit;
            var items = new List<object>();
            for (int i = 0; i < rows.Count && i < limit; i++)
            {
                var e = rows[i];
                items.Add(Json.Obj(
                    "path", relative ? e.RelPath : e.FullPath,
                    "name", e.Name,
                    "ext", e.Ext,
                    "size", e.Size,
                    "sizeHuman", Fs.FormatSize(e.Size),
                    "mtime", e.LastWriteUtc.ToLocalTime(),
                    "ageSeconds", Math.Max(0, (long)(DateTime.UtcNow - e.LastWriteUtc.ToUniversalTime()).TotalSeconds),
                    "ageHuman", FormatAge(e.LastWriteUtc)));
            }

            ctx.Out.Truncated = truncated;
            AddDeniedWarnings(ctx, o);

            var data = Json.Obj(
                "roots", valid.ToArray(),
                "since", since,
                "items", items,
                "count", items.Count,
                "columns", new[] { "path", "sizeHuman", "mtime", "ageHuman" },
                "scanned", o.ScannedFiles,
                "scannedDirs", (long)o.ScannedDirs,
                "matched", rows.Count,
                "skipped", Math.Max(0, o.ScannedFiles - rows.Count),
                "denied", o.Denied.Count,
                "deniedDirs", DeniedDirs(o),
                "failures", failures,
                "partial", failures.Count > 0,
                "sort", sortKey,
                "reverse", reverse,
                "limit", limit,
                "truncatedAt", truncated ? "max-results" : null);

            ctx.Out.Result("scan.recent", data);
            return failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }

        // ============================================================ scan.empty-dirs
        static int RunEmptyDirs(Ctx ctx)
        {
            var failures = new List<object>();
            var roots = Roots(ctx, failures);
            string root = roots[0];
            if (!Directory.Exists(Fs.LongPath(root))) throw ToolException.NotFound("目录不存在：" + root);

            long maxResults = ctx.Args.GetLong("max-results", 0);
            bool relative = ctx.Flag("relative");

            var o = FsScanOptions.FromArgs(ctx);
            o.MaxResults = 0; o.DirsOnly = true; o.FilesOnly = false;
            o.Include.Clear(); o.Exts.Clear(); o.NameRegex = null;
            o.MinSize = -1; o.MaxSize = -1; o.NewerThan = null; o.OlderThan = null;
            int userDepth = ctx.Args.GetInt("depth", 0);

            var dirs = new List<FsEntry>();
            bool truncated = false;
            foreach (var e in WalkOne(root, o, userDepth))
            {
                ctx.ThrowIfCancelled();
                dirs.Add(e);
            }

            // 每个目录的直接文件数（-1 = 读取失败，保守视为非空）
            var direct = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                ctx.ThrowIfCancelled();
                try { direct[d.FullPath] = new DirectoryInfo(Fs.LongPath(d.FullPath)).GetFiles().Length; }
                catch (UnauthorizedAccessException) { direct[d.FullPath] = -1; failures.Add(Json.Obj("path", d.FullPath, "error", "拒绝访问")); }
                catch (Exception ex) { direct[d.FullPath] = -1; failures.Add(Json.Obj("path", d.FullPath, "error", ex.GetType().Name + ": " + ex.Message)); }
            }

            var childMap = new Dictionary<string, List<FsEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                string parent = Path.GetDirectoryName(d.FullPath);
                if (string.IsNullOrEmpty(parent)) continue;
                List<FsEntry> l;
                if (!childMap.TryGetValue(parent, out l)) { l = new List<FsEntry>(); childMap[parent] = l; }
                l.Add(d);
            }

            var memo = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Func<FsEntry, bool> fileless = null;
            fileless = d =>
            {
                bool cached;
                if (memo.TryGetValue(d.FullPath, out cached)) return cached;
                memo[d.FullPath] = false;
                int n;
                bool hasFiles = false;
                if (direct.TryGetValue(d.FullPath, out n)) hasFiles = n != 0;
                bool res = !hasFiles;
                if (res)
                {
                    List<FsEntry> kids;
                    if (childMap.TryGetValue(d.FullPath, out kids))
                        foreach (var k in kids) if (!fileless(k)) { res = false; break; }
                }
                memo[d.FullPath] = res;
                return res;
            };

            var items = new List<object>();
            var filelessList = new List<object>();
            long emptyCount = 0, filelessCount = 0;
            foreach (var d in dirs)
            {
                ctx.ThrowIfCancelled();
                int n;
                direct.TryGetValue(d.FullPath, out n);
                List<FsEntry> kids;
                childMap.TryGetValue(d.FullPath, out kids);
                bool strictEmpty = n == 0 && (kids == null || kids.Count == 0);
                bool noFiles = fileless(d);
                if (strictEmpty) emptyCount++;
                if (noFiles)
                {
                    filelessCount++;
                    if (filelessList.Count < 200) filelessList.Add(relative ? d.RelPath : d.FullPath);
                }
                if (strictEmpty)
                {
                    items.Add(Json.Obj(
                        "path", relative ? d.RelPath : d.FullPath,
                        "name", d.Name,
                        "rel", d.RelPath,
                        "depth", d.RelPath.Split('\\').Length,
                        "empty", true,
                        "fileless", noFiles,
                        "entries", 0,
                        "mtime", d.LastWriteUtc.ToLocalTime()));
                }
            }

            if (maxResults > 0 && items.Count > maxResults)
            {
                truncated = true;
                items.RemoveRange((int)maxResults, items.Count - (int)maxResults);
            }

            ctx.Out.Truncated = truncated;
            AddDeniedWarnings(ctx, o);

            var data = Json.Obj(
                "root", root,
                "items", items,
                "count", items.Count,
                "columns", new[] { "path", "depth", "fileless", "mtime" },
                "emptyCount", emptyCount,
                "filelessCount", filelessCount,
                "filelessDirs", filelessList,
                "scanned", (long)o.ScannedDirs,
                "scannedDirs", (long)o.ScannedDirs,
                "matched", items.Count,
                "skipped", Math.Max(0, o.ScannedDirs - items.Count),
                "denied", o.Denied.Count,
                "deniedDirs", DeniedDirs(o),
                "failures", failures,
                "partial", failures.Count > 0,
                "truncatedAt", truncated ? "max-results" : null);

            ctx.Out.Result("scan.empty-dirs", data);
            return failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }
    }

    // ================================================================ 共享辅助类型
    internal sealed class ScanHashResult
    {
        public string Hash;
        public string Error;
    }

    internal sealed class ScanContentResult
    {
        public bool Matched;
        public string Error;
    }

    internal sealed class ScanFindRow
    {
        public FsEntry Entry;
        public string Hash;
    }

    internal sealed class ScanChildStat
    {
        public long Size;
        public long Files;
        public readonly List<string> Denied = new List<string>();
        public readonly List<object> Failures = new List<object>();
    }

    internal sealed class ScanTreeNode
    {
        public string Path;
        public string Name;
        public string Rel;
        public int Depth;
        public bool IsDir;
        public long TotalSize;      // 直接文件大小
        public long SubtreeSize;    // 含子树
        public int Files;           // 直接文件数
        public long SubtreeFiles;
        public readonly List<ScanTreeNode> Children = new List<ScanTreeNode>();
    }
}
