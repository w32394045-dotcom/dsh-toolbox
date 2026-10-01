using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>
    /// scanner-dev 拥有：hash.* 命令（见 docs/ARCHITECTURE.md §3）。
    /// 单文件哈希一律走 Core\Fs.cs 的 Fs.HashFile；目录汇总哈希是对规范化清单文本做一次哈希。
    /// </summary>
    public static class HashCommands
    {
        // ============================================================ 注册
        public static void Register()
        {
            Registry.Add("hash.file", "单文件多算法哈希（--algo 可重复），可选 --expected 比对",
                "hash file --path <file> [--algo <algo>]... [--expected <hex>]",
                ctx => ScanCommands.Guarded(ctx, RunFile),
                examples: new[]
                {
                    "dsh-toolbox hash file --path readme.md --json",
                    "dsh-toolbox hash file --path setup.exe --algo md5 --algo sha1 --algo sha256",
                    "dsh-toolbox hash file --path a.bin --algo sha256 --expected 9f86d081... --json"
                });

            Registry.Add("hash.dir", "目录整体哈希清单 + 汇总哈希（判断目录是否变化）",
                "hash dir --path <dir> [--algo <algo>] [--out <file>] [--include <glob>]... [--exclude <glob>]... [--exclude-dir <name>]... " +
                "[--ext <ext>]... [--depth <n>] [--hidden] [--follow] [--max-results <n>] [--parallel <n>] [--relative]",
                ctx => ScanCommands.Guarded(ctx, RunDir),
                aliases: new[] { "hash.tree" },
                examples: new[]
                {
                    "dsh-toolbox hash dir --path C:\\data --json",
                    "dsh-toolbox hash dir --path . --algo sha256 --out var\\dir-hash.json --json"
                });

            Registry.Add("hash.compare", "比对两个文件或两个哈希清单",
                "hash compare <a> <b> | --a <a> --b <b> [--algo <algo>]... [--max-results <n>]",
                ctx => ScanCommands.Guarded(ctx, RunCompare),
                aliases: new[] { "hash.diff" },
                examples: new[]
                {
                    "dsh-toolbox hash compare a.txt b.txt --json",
                    "dsh-toolbox hash compare --a var\\before.json --b var\\after.json --json"
                });
        }

        // ============================================================ 通用工具
        static bool Supported(string algo)
        {
            foreach (var s in Fs.SupportedHashes())
                if (string.Equals(s, algo, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static List<string> AlgoList(Ctx ctx, params string[] def)
        {
            var algos = new List<string>();
            foreach (var a in ctx.GetAll("algo"))
            {
                string c = ScanCommands.CanonAlgo(a);
                if (c.Length > 0 && !algos.Contains(c)) algos.Add(c);
            }
            string h = ScanCommands.NormAlgoOrNull(ctx.Get("hash"));
            if (h != null && !algos.Contains(h)) algos.Add(h);
            if (algos.Count == 0) algos.AddRange(def);
            foreach (var a in algos)
                if (!Supported(a)) throw ToolException.Usage("不支持的哈希算法：" + a, "支持 " + string.Join("/", Fs.SupportedHashes()));
            return algos;
        }

        static List<object> DeniedDirs(FsScanOptions o)
        {
            var list = new List<object>();
            int n = 0;
            foreach (var d in o.Denied) { if (n++ >= 100) break; list.Add(d); }
            return list;
        }

        static void DeniedWarn(Ctx ctx, FsScanOptions o)
        {
            if (o.Denied.Count > 0) ctx.Out.Warn("跳过 " + o.Denied.Count + " 个无权限目录（见 data.deniedDirs）");
        }

        static long WriteJsonFile(Ctx ctx, string outPath, object payload)
        {
            if (File.Exists(Fs.LongPath(outPath)) && !ctx.Yes)
                throw ToolException.NeedsYes("覆盖已存在的文件 " + outPath);
            string dir = null;
            try { dir = Path.GetDirectoryName(outPath); } catch { }
            Fs.EnsureDir(dir);
            try
            {
                File.WriteAllText(Fs.LongPath(outPath), Json.Write(payload, true), new UTF8Encoding(false));
                return new FileInfo(Fs.LongPath(outPath)).Length;
            }
            catch (UnauthorizedAccessException) { throw ToolException.Denied("无法写入文件：" + outPath); }
            catch (Exception ex) { throw new ToolException("E_IO", "写入文件失败：" + ex.Message, "检查 --out 路径", ExitCodes.Error); }
        }

        // ============================================================ hash.file
        static int RunFile(Ctx ctx)
        {
            var failures = new List<object>();
            var paths = new List<string>();
            string p = ctx.Get("path");
            if (!string.IsNullOrWhiteSpace(p)) paths.Add(Fs.Expand(p, ctx.Cwd));
            foreach (var x in ctx.Args.Positional)
                if (!string.IsNullOrWhiteSpace(x)) paths.Add(Fs.Expand(x, ctx.Cwd));
            if (paths.Count == 0)
                throw ToolException.Usage("缺少 --path <文件>", "例：hash file --path a.txt --algo sha256 --json");
            if (paths.Count > 1)
                throw ToolException.Usage("hash.file 一次只处理一个文件（收到 " + paths.Count + " 个）", "多文件请用 hash dir");

            string path = paths[0];
            if (!File.Exists(Fs.LongPath(path))) throw ToolException.NotFound("文件不存在：" + path);

            var algos = AlgoList(ctx, "md5", "sha1", "sha256");

            string expected = ctx.Get("expected");
            string expectedAlgo = null;
            if (!string.IsNullOrWhiteSpace(expected))
            {
                expected = expected.Trim();
                int colon = expected.IndexOf(':');
                if (colon > 0)
                {
                    string maybe = ScanCommands.CanonAlgo(expected.Substring(0, colon));
                    if (Supported(maybe)) { expectedAlgo = maybe; expected = expected.Substring(colon + 1).Trim(); }
                }
            }

            long size = 0;
            DateTime mtime = DateTime.MinValue;
            try
            {
                var fi = new FileInfo(Fs.LongPath(path));
                size = fi.Length;
                mtime = fi.LastWriteTimeUtc;
            }
            catch (Exception ex) { failures.Add(Json.Obj("path", path, "error", ex.GetType().Name + ": " + ex.Message, "op", "stat")); }

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var items = new List<object>();
            object anyMatch = null;
            foreach (var a in algos)
            {
                ctx.ThrowIfCancelled();
                string hex = null;
                try { hex = Fs.HashFile(path, a, ctx.Cancel); }
                catch (OperationCanceledException) { throw; }
                catch (ToolException) { throw; }
                catch (UnauthorizedAccessException) { throw ToolException.Denied("无法读取文件：" + path); }
                catch (Exception ex) { failures.Add(Json.Obj("path", path, "error", ex.GetType().Name + ": " + ex.Message, "op", "hash-" + a)); }

                if (hex == null) { items.Add(Json.Obj("algo", a, "hash", null, "match", null)); continue; }
                hashes[a] = hex;
                bool? m = null;
                if (expected != null && expected.Length > 0)
                {
                    if (expectedAlgo == null || string.Equals(expectedAlgo, a, StringComparison.OrdinalIgnoreCase))
                    {
                        bool eq = string.Equals(hex, expected, StringComparison.OrdinalIgnoreCase);
                        m = eq;
                        if (eq) anyMatch = true;
                        else if (anyMatch == null) anyMatch = false;
                    }
                }
                items.Add(Json.Obj("algo", a, "hash", hex, "match", (object)m));
            }

            if (expected != null && expected.Length > 0 && anyMatch != null && !(bool)anyMatch)
                ctx.Out.Warn("哈希与 --expected 不匹配");

            string primaryAlgo = hashes.ContainsKey("sha256") ? "sha256" : (hashes.Count > 0 ? hashes.Keys.First() : null);
            var data = Json.Obj(
                "path", path,
                "name", Path.GetFileName(path),
                "size", size,
                "sizeHuman", Fs.FormatSize(size),
                "mtime", mtime == DateTime.MinValue ? (object)null : mtime.ToLocalTime(),
                "algos", algos.ToArray(),
                "hashes", hashes,
                "primary", Json.Obj("algo", primaryAlgo, "hash", primaryAlgo == null ? null : hashes[primaryAlgo]),
                "expected", expected,
                "expectedAlgo", expectedAlgo,
                "matched", anyMatch,
                "verdict", anyMatch,
                "items", items,
                "count", items.Count,
                "columns", new[] { "algo", "hash", "match" },
                "failures", failures,
                "partial", failures.Count > 0);

            ctx.Out.Result("hash.file", data);
            if (failures.Count > 0) return ExitCodes.Partial;
            if (anyMatch != null && !(bool)anyMatch) return ExitCodes.Error;   // §4.1 断言为否 → 1
            return ExitCodes.Ok;
        }

        // ============================================================ hash.dir
        static int RunDir(Ctx ctx)
        {
            var failures = new List<object>();
            var roots = ScanCommands.Roots(ctx, failures);
            if (roots.Count > 1) throw ToolException.Usage("hash.dir 一次只接受一个根目录（收到 " + roots.Count + " 个）");
            string root = roots[0];
            if (!Directory.Exists(Fs.LongPath(root))) throw ToolException.NotFound("目录不存在：" + root);

            string algo = ScanCommands.CanonAlgo(ScanCommands.NormAlgoOrNull(ctx.Get("algo")) ?? "sha256");
            if (!Supported(algo))
                throw ToolException.Usage("不支持的哈希算法：" + algo, "支持 " + string.Join("/", Fs.SupportedHashes()));
            if (algo == "crc32")
                throw ToolException.Usage("hash.dir 的汇总哈希不支持 crc32", "请用 md5/sha1/sha256/sha384/sha512");

            int parallel = ctx.Args.GetInt("parallel", 0);
            long maxResults = ctx.Args.GetLong("max-results", 0);
            bool relative = ctx.Flag("relative");

            var o = FsScanOptions.FromArgs(ctx);
            o.MaxResults = 0; o.FilesOnly = true; o.DirsOnly = false;
            int userDepth = ctx.Args.GetInt("depth", 0);

            var entries = new List<FsEntry>();
            bool truncated = false;
            foreach (var e in ScanCommands.WalkOne(root, o, userDepth))
            {
                ctx.ThrowIfCancelled();
                if (maxResults > 0 && entries.Count >= maxResults) { truncated = true; break; }
                entries.Add(e);
            }

            var hashes = new string[entries.Count];
            if (entries.Count > 0)
            {
                var hs = Fs.ParallelMap(entries, parallel, e => ScanCommands.HashProbe(e, algo, ctx.Cancel), ctx.Cancel);
                for (int i = 0; i < entries.Count; i++)
                {
                    if (hs[i].Error != null) failures.Add(Json.Obj("path", entries[i].FullPath, "error", hs[i].Error, "op", "hash"));
                    else hashes[i] = hs[i].Hash;
                }
            }

            long totalSize = 0;
            var items = new List<object>();           // 信封/表格：契约 §5，path 默认绝对，--relative 转相对
            var fileEntries = new List<object>();     // 清单文件：path 恒为相对根路径（可移植、跨机可校验）
            var manifestRows = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                totalSize += e.Size;
                string rel = ScanCommands.NormRel(e.RelPath);
                long ms = ScanCommands.EpochMs(e.LastWriteUtc);
                items.Add(Json.Obj(
                    "path", relative ? e.RelPath : e.FullPath,
                    "rel", rel,
                    "size", e.Size,
                    "sizeHuman", Fs.FormatSize(e.Size),
                    "mtimeMs", ms,
                    "mtime", e.LastWriteUtc.ToLocalTime(),
                    "hash", hashes[i]));
                fileEntries.Add(Json.Obj(
                    "path", rel,
                    "full", e.FullPath,
                    "size", e.Size,
                    "mtimeMs", ms,
                    "mtime", e.LastWriteUtc,
                    "hash", hashes[i]));
                if (hashes[i] != null)
                    manifestRows.Add(new KeyValuePair<string, string>(rel, e.Size.ToString(CultureInfo.InvariantCulture) + "|" + hashes[i]));
            }

            manifestRows.Sort((a, b) =>
            {
                int c = string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            var sb = new StringBuilder();
            foreach (var r in manifestRows) sb.Append(r.Key.Replace('\\', '/')).Append('\t').Append(r.Value).Append('\n');
            string treeHash = ScanCommands.HashBytes(new UTF8Encoding(false).GetBytes(sb.ToString()), algo);
            bool complete = !truncated && failures.Count == 0;

            var manifest = Json.Obj(
                "tool", ToolInfo.Name,
                "version", ToolInfo.Version,
                "kind", "hash.dir",
                "created", DateTime.Now,
                "root", root,
                "algo", algo,
                "count", fileEntries.Count,
                "totalSize", totalSize,
                "complete", complete,
                "truncated", truncated,
                "treeHash", treeHash,
                "entries", fileEntries);

            string outArg = ctx.Get("out");
            string outPath = null;
            long bytesWritten = 0;
            var plan = (object)null;
            if (!string.IsNullOrWhiteSpace(outArg))
            {
                outPath = Fs.Expand(outArg, ctx.Cwd);
                bool exists = File.Exists(Fs.LongPath(outPath));
                plan = Json.Obj("action", exists ? "overwrite" : "create", "out", outPath, "count", manifestRows.Count, "treeHash", treeHash);
                if (ctx.DryRun) ctx.Out.Warn("--dry-run：未写任何文件");
                else bytesWritten = WriteJsonFile(ctx, outPath, manifest);
            }

            ctx.Out.Truncated = truncated;
            DeniedWarn(ctx, o);

            var data = Json.Obj(
                "root", root,
                "out", outPath,
                "dryRun", ctx.DryRun,
                "plan", plan,
                "items", items,
                "count", items.Count,
                "columns", new[] { "path", "sizeHuman", "hash" },
                "algo", algo,
                "treeHash", treeHash,
                "manifest", sb.Length.ToString(CultureInfo.InvariantCulture) + " 字节清单文本",
                "complete", complete,
                "totalSize", totalSize,
                "totalHuman", Fs.FormatSize(totalSize),
                "bytesWritten", bytesWritten,
                "scanned", o.ScannedFiles,
                "matched", manifestRows.Count,
                "skipped", Math.Max(0, o.ScannedFiles - manifestRows.Count),
                "denied", o.Denied.Count,
                "deniedDirs", DeniedDirs(o),
                "failures", failures,
                "partial", failures.Count > 0,
                "truncatedAt", truncated ? "max-results" : null,
                "parallel", parallel > 0 ? parallel : Environment.ProcessorCount);

            ctx.Out.Result("hash.dir", data);
            return failures.Count > 0 ? ExitCodes.Partial : ExitCodes.Ok;
        }

        // ============================================================ hash.compare
        static int RunCompare(Ctx ctx)
        {
            var failures = new List<object>();
            string a = ctx.Get("a");
            string b = ctx.Get("b");
            var pos = ctx.Args.Positional;
            if (string.IsNullOrWhiteSpace(a) && pos.Count >= 1) a = pos[0];
            if (string.IsNullOrWhiteSpace(b) && pos.Count >= 2) b = pos[1];
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                throw ToolException.Usage("需要两个输入：hash compare <a> <b> 或 --a <x> --b <y>", "两者要么都是文件，要么都是清单 JSON");
            a = Fs.Expand(a, ctx.Cwd);
            b = Fs.Expand(b, ctx.Cwd);
            if (!File.Exists(Fs.LongPath(a))) throw ToolException.NotFound("文件不存在：" + a);
            if (!File.Exists(Fs.LongPath(b))) throw ToolException.NotFound("文件不存在：" + b);

            var ma = TryLoadManifest(a);
            var mb = TryLoadManifest(b);
            if ((ma != null) != (mb != null))
                throw ToolException.Usage("两个输入类型不一致：一个是清单 JSON，另一个是普通文件");
            if (ma != null) return CompareManifests(ctx, a, b, ma, mb, failures);
            return CompareFiles(ctx, a, b, failures);
        }

        static Dictionary<string, object> TryLoadManifest(string path)
        {
            string text;
            try { text = File.ReadAllText(Fs.LongPath(path), Encoding.UTF8); }
            catch { return null; }
            if (text.Length == 0) return null;
            string t = text.TrimStart();
            if (t.Length == 0 || t[0] != '{') return null;
            try
            {
                var d = Json.ParseObject(text);
                if (!d.ContainsKey("entries")) return null;
                if (!(d["entries"] is System.Collections.IEnumerable) || d["entries"] is string) return null;
                return d;
            }
            catch { return null; }
        }

        static Dictionary<string, Dictionary<string, object>> ManifestEntries(Dictionary<string, object> m)
        {
            var map = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            object raw = m.ContainsKey("entries") ? m["entries"] : null;
            foreach (var en in ScanCommands.ObjList(raw))
            {
                string p = Json.GetString(en, "path");
                if (string.IsNullOrEmpty(p)) continue;
                map[ScanCommands.NormRel(p)] = en;
            }
            return map;
        }

        static int CompareFiles(Ctx ctx, string a, string b, List<object> failures)
        {
            var algos = AlgoList(ctx, "sha256");
            long sizeA = 0, sizeB = 0;
            DateTime mA = DateTime.MinValue, mB = DateTime.MinValue;
            try { var fa = new FileInfo(Fs.LongPath(a)); sizeA = fa.Length; mA = fa.LastWriteTimeUtc; }
            catch (Exception ex) { failures.Add(Json.Obj("path", a, "error", ex.GetType().Name + ": " + ex.Message, "op", "stat")); }
            try { var fb = new FileInfo(Fs.LongPath(b)); sizeB = fb.Length; mB = fb.LastWriteTimeUtc; }
            catch (Exception ex) { failures.Add(Json.Obj("path", b, "error", ex.GetType().Name + ": " + ex.Message, "op", "stat")); }

            var hashesA = new Dictionary<string, string>(StringComparer.Ordinal);
            var hashesB = new Dictionary<string, string>(StringComparer.Ordinal);
            var items = new List<object>();
            if (sizeA != sizeB) items.Add(Json.Obj("field", "size", "a", sizeA, "b", sizeB, "equal", false));

            foreach (var algo in algos)
            {
                ctx.ThrowIfCancelled();
                string ha = HashOne(a, algo, failures, "a", ctx.Cancel);
                string hb = HashOne(b, algo, failures, "b", ctx.Cancel);
                if (ha != null) hashesA[algo] = ha;
                if (hb != null) hashesB[algo] = hb;
                bool eq = ha != null && hb != null && string.Equals(ha, hb, StringComparison.OrdinalIgnoreCase);
                items.Add(Json.Obj("field", algo, "a", ha, "b", hb, "equal", eq));
            }

            bool equal = sizeA == sizeB && items.Count > 0;
            foreach (var it in items)
            {
                var d = it as Dictionary<string, object>;
                if (d != null && d.ContainsKey("equal") && !(bool)d["equal"]) { equal = false; break; }
            }

            var data = Json.Obj(
                "mode", "file",
                "equal", equal,
                "verdict", equal,
                "a", Json.Obj("path", a, "size", sizeA, "sizeHuman", Fs.FormatSize(sizeA), "mtime", mA == DateTime.MinValue ? (object)null : mA.ToLocalTime(), "hashes", hashesA),
                "b", Json.Obj("path", b, "size", sizeB, "sizeHuman", Fs.FormatSize(sizeB), "mtime", mB == DateTime.MinValue ? (object)null : mB.ToLocalTime(), "hashes", hashesB),
                "algos", algos.ToArray(),
                "items", items,
                "count", items.Count,
                "columns", new[] { "field", "a", "b", "equal" },
                "failures", failures,
                "partial", failures.Count > 0);

            ctx.Out.Result("hash.compare", data);
            if (!equal) ctx.Out.Warn("两个文件不一致");
            if (failures.Count > 0) return ExitCodes.Partial;
            return equal ? ExitCodes.Ok : ExitCodes.Error;   // §4.1
        }

        static string HashOne(string path, string algo, List<object> failures, string tag, System.Threading.CancellationToken ct)
        {
            try { return Fs.HashFile(path, algo, ct); }
            catch (OperationCanceledException) { throw; }
            catch (ToolException) { throw; }
            catch (UnauthorizedAccessException) { throw ToolException.Denied("无法读取文件：" + path); }
            catch (Exception ex)
            {
                failures.Add(Json.Obj("path", path, "error", ex.GetType().Name + ": " + ex.Message, "op", "hash-" + tag));
                return null;
            }
        }

        static int CompareManifests(Ctx ctx, string a, string b, Dictionary<string, object> ma, Dictionary<string, object> mb, List<object> failures)
        {
            string algoA = Json.GetString(ma, "algo", "sha256") ?? "sha256";
            string algoB = Json.GetString(mb, "algo", "sha256") ?? "sha256";
            if (!string.Equals(ScanCommands.CanonAlgo(algoA), ScanCommands.CanonAlgo(algoB), StringComparison.OrdinalIgnoreCase))
                ctx.Out.Warn("两个清单使用的算法不同（" + algoA + " vs " + algoB + "），哈希差异仅供参考");

            var ea = ManifestEntries(ma);
            var eb = ManifestEntries(mb);
            var keys = new List<string>();
            foreach (var k in ea.Keys) keys.Add(k);
            foreach (var k in eb.Keys) if (!ea.ContainsKey(k)) keys.Add(k);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            long maxResults = ctx.Args.GetLong("max-results", 0);
            bool truncated = false;
            var items = new List<object>();
            long added = 0, removed = 0, modified = 0, unchanged = 0;

            foreach (var k in keys)
            {
                ctx.ThrowIfCancelled();
                Dictionary<string, object> da, db;
                bool inA = ea.TryGetValue(k, out da);
                bool inB = eb.TryGetValue(k, out db);
                if (inA && !inB)
                {
                    removed++;
                    if (maxResults <= 0 || items.Count < maxResults)
                        items.Add(Json.Obj("kind", "removed", "path", k, "detail", "仅在 A 中（" + Fs.FormatSize(ScanCommands.GetLong(da, "size", 0)) + "）",
                            "sizeA", ScanCommands.GetLong(da, "size", -1), "hashA", Json.GetString(da, "hash"), "sizeB", null, "hashB", null));
                    else truncated = true;
                    continue;
                }
                if (!inA && inB)
                {
                    added++;
                    if (maxResults <= 0 || items.Count < maxResults)
                        items.Add(Json.Obj("kind", "added", "path", k, "detail", "仅在 B 中（" + Fs.FormatSize(ScanCommands.GetLong(db, "size", 0)) + "）",
                            "sizeA", null, "hashA", null, "sizeB", ScanCommands.GetLong(db, "size", -1), "hashB", Json.GetString(db, "hash")));
                    else truncated = true;
                    continue;
                }

                long sa = ScanCommands.GetLong(da, "size", -1);
                long sb = ScanCommands.GetLong(db, "size", -1);
                string ha = Json.GetString(da, "hash");
                string hb = Json.GetString(db, "hash");
                bool sizeDiff = sa != sb;
                bool hashDiff = !string.IsNullOrEmpty(ha) && !string.IsNullOrEmpty(hb) && !string.Equals(ha, hb, StringComparison.OrdinalIgnoreCase);
                if (sizeDiff || hashDiff)
                {
                    modified++;
                    string detail = sizeDiff
                        ? "大小 " + Fs.FormatSize(sa) + " → " + Fs.FormatSize(sb)
                        : "内容哈希不一致";
                    if (maxResults <= 0 || items.Count < maxResults)
                        items.Add(Json.Obj("kind", "modified", "path", k, "detail", detail,
                            "reason", sizeDiff ? "size" : "content", "sizeA", sa, "sizeB", sb, "hashA", ha, "hashB", hb));
                    else truncated = true;
                }
                else unchanged++;
            }

            string ta = Json.GetString(ma, "treeHash");
            string tb = Json.GetString(mb, "treeHash");
            bool treeEqual = !string.IsNullOrEmpty(ta) && !string.IsNullOrEmpty(tb) &&
                             string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase);
            bool equal = added == 0 && removed == 0 && modified == 0 && (ta == null || tb == null || treeEqual);

            ctx.Out.Truncated = truncated;
            if (!equal) ctx.Out.Warn("两个清单不一致：新增 " + added + " / 删除 " + removed + " / 修改 " + modified);

            var data = Json.Obj(
                "mode", "manifest",
                "equal", equal,
                "verdict", equal,
                "a", Json.Obj("path", a, "kind", Json.GetString(ma, "kind"), "algo", algoA, "count", ea.Count, "treeHash", ta, "root", Json.GetString(ma, "root")),
                "b", Json.Obj("path", b, "kind", Json.GetString(mb, "kind"), "algo", algoB, "count", eb.Count, "treeHash", tb, "root", Json.GetString(mb, "root")),
                "treeHashEqual", ta == null || tb == null ? (object)null : treeEqual,
                "items", items,
                "count", items.Count,
                "columns", new[] { "kind", "path", "detail" },
                "added", added,
                "removed", removed,
                "modified", modified,
                "unchanged", unchanged,
                "totalA", ea.Count,
                "totalB", eb.Count,
                "failures", failures,
                "partial", failures.Count > 0,
                "truncatedAt", truncated ? "max-results" : null);

            ctx.Out.Result("hash.compare", data);
            if (failures.Count > 0) return ExitCodes.Partial;
            return equal ? ExitCodes.Ok : ExitCodes.Error;   // §4.1
        }
    }
}
