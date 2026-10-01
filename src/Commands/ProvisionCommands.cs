using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    /// <summary>
    /// 依赖自举：**先探测环境，再全自动装依赖，最后用 npm 装 dsh**。
    ///
    /// 设计要点（针对"用户机器上啥都没有 / 安装器会弹交互"这两个现实问题）：
    ///   * 不假设有微软商店 / winget / git / npm；
    ///   * Node 用**官方 zip**装到用户目录——没有 MSI/EXE 安装器，所以没有 UI、没有协议页、不需要管理员；
    ///   * 一律用官方 SHASUMS256.txt 校验，装不上就明确失败，绝不谎报成功；
    ///   * 所有子进程走 Proc（stdin 关闭 + 免交互环境变量 + 提示识别 + 换参数重试），
    ///     因此不会卡在 "Ok to proceed?" / 用户名密码 / 协议确认这类提示上。
    /// </summary>
    public static class ProvisionCommands
    {
        const string NodeIndexUrl = "https://nodejs.org/dist/index.json";
        const string NodeDistBase = "https://nodejs.org/dist";
        const string PkgName = "@deepseek-ai/dsh";

        public static void Register()
        {
            Registry.Add("install.prereq",
                L.T("安装前环境探测：Node/npm/git/商店/网络/磁盘/权限，并给出可全自动执行的安装计划",
                    "Pre-flight environment check: Node/npm/git/Store/network/disk/privileges plus a fully automatic install plan"),
                "install prereq [--fast] [--machine] [--json]",
                RunPrereq,
                examples: new[] { "dsh-toolbox install.prereq --json", "dsh-toolbox install.prereq --fast --json" });

            Registry.Add("install.node",
                L.T("安装 Node.js（官方 zip 直接解压：免商店、免 git、无安装器交互；默认要求管理员，--user-level 装到当前用户，--machine 全机可用）",
                    "Install Node.js (official zip extracted directly: no Store, no git, no installer prompts; requires admin by default, --user-level for per-user, --machine for system-wide)"),
                "install node [--version <v>] [--file <zip>] [--machine] [--user-level] [--force] [--dry-run] [--yes]",
                RunNode,
                examples: new[] { "dsh-toolbox install.node --dry-run --json", "dsh-toolbox install.node --yes" });
        }

        // ================================================================ 路径与探测工具
        // --machine：装成全机可用（%ProgramFiles%\dsh-toolbox），此时 PATH 改系统级；
        // 默认：装到当前用户目录。两种都由调用方决定，界面/agent 用 --machine 显式开启。
        static bool _machine;
        public static string RootDir
        {
            get
            {
                return _machine
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dsh-toolbox")
                    : Paths.Home;
            }
        }
        public static string RuntimeDir { get { return Path.Combine(RootDir, "runtime"); } }
        public static string NpmPrefix { get { return Path.Combine(RuntimeDir, "npm-global"); } }
        static EnvironmentVariableTarget PathScope { get { return _machine ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User; } }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SYSTEM_INFO
        {
            public ushort wProcessorArchitecture;
            public ushort wReserved;
            public uint dwPageSize;
            public IntPtr lpMinimumApplicationAddress;
            public IntPtr lpMaximumApplicationAddress;
            public IntPtr dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType;
            public uint dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool GetNativeSystemInfo(out SYSTEM_INFO lpSystemInfo);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsWow64Process2(IntPtr hProcess, out ushort pProcessMachine, out ushort pNativeMachine);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        /// <summary>宿主原生架构（x64 / arm64 / x86）——仿真进程里也返回真实架构。</summary>
        public static string HostArch()
        {
            try
            {
                // 真实架构的判定顺序（踩过两次坑，别改）：
                //  1) IsWow64Process2 —— 唯一在"仿真进程"里也返回**原生**架构的 API（Win10 1709+）。
                //     .NET Framework 在 Windows ARM64 上是以 x64 仿真运行的：那种进程里
                //     PROCESSOR_ARCHITECTURE=AMD64、ARCHW6432 为空，GetNativeSystemInfo 也按仿真环境
                //     回答 AMD64 —— 只有这个 API 会说 native=ARM64。
                //  2) 机器级 PROCESSOR_ARCHITECTURE（ARM64 机器上就是 ARM64）—— 老系统没有上面那个 API 时兜底。
                //  3) GetNativeSystemInfo → 4) 进程级环境变量 → 5) Is64BitOperatingSystem。
                try
                {
                    if (IsWow64Process2(GetCurrentProcess(), out ushort proc, out ushort native))
                    {
                        if (native == 0xAA64) return "arm64";   // IMAGE_FILE_MACHINE_ARM64
                        if (native == 0x8664) return "x64";     // IMAGE_FILE_MACHINE_AMD64
                        if (native == 0x014C) return "x86";     // IMAGE_FILE_MACHINE_I386
                    }
                }
                catch { }

                string a = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE", EnvironmentVariableTarget.Machine);
                if (string.IsNullOrWhiteSpace(a)) a = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
                if (string.IsNullOrWhiteSpace(a)) a = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
                a = (a ?? "").Trim().ToUpperInvariant();
                if (a.Contains("ARM64")) return "arm64";
                if (a.Contains("AMD64") || a.Contains("IA64")) return "x64";
                if (a.Contains("X86")) return "x86";

                // 再退一步：内核接口（老系统无 IsWow64Process2 时用；注意它在仿真进程里按仿真环境回答）
                try
                {
                    SYSTEM_INFO si;
                    if (GetNativeSystemInfo(out si))
                    {
                        if (si.wProcessorArchitecture == 12) return "arm64";
                        if (si.wProcessorArchitecture == 9) return "x64";
                        if (si.wProcessorArchitecture == 0) return "x86";
                    }
                }
                catch { }
            }
            catch { }
            return Environment.Is64BitOperatingSystem ? "x64" : "x86";
        }
        static string NodeTag { get { return "win-" + HostArch(); } }
        static string NodeDir(string version) { return Path.Combine(RuntimeDir, "node-v" + version + "-" + NodeTag); }

        /// <summary>在 PATH 与几个常见安装位置里找可执行文件。</summary>
        static string FindExe(string exeName, params string[] extraDirs)
        {
            var dirs = new List<string>();
            try { dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')); } catch { }
            dirs.AddRange(extraDirs);
            foreach (var d in dirs)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                try { string p = Path.Combine(d.Trim().Trim('"'), exeName); if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        /// <summary>工具箱自己装的 Node（runtime\node-v*\），优先于系统 PATH。</summary>
        public static string FindRuntimeNode(string preferVersion = null)
        {
            try
            {
                if (!Directory.Exists(RuntimeDir)) return null;
                string best = null;
                foreach (var d in Directory.GetDirectories(RuntimeDir, "node-v*-" + NodeTag))
                {
                    string exe = Path.Combine(d, "node.exe");
                    if (!File.Exists(exe)) continue;
                    if (preferVersion != null && !d.EndsWith("node-v" + preferVersion + "-" + NodeTag, StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || string.CompareOrdinal(d, best) > 0) best = exe;
                }
                return best;
            }
            catch { return null; }
        }

        public static string FindNpmFrom(string nodeExe)
        {
            if (string.IsNullOrEmpty(nodeExe)) return null;
            try
            {
                string dir = Path.GetDirectoryName(nodeExe);
                foreach (var n in new[] { "npm.cmd", "npm.exe", "npm" })
                {
                    string p = Path.Combine(dir, n);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        static string NodeVersionOf(string nodeExe)
        {
            if (string.IsNullOrEmpty(nodeExe) || !File.Exists(nodeExe)) return null;
            string v = ToolVersionCached("node:" + nodeExe, nodeExe, "--version", "node");
            return v != null ? v.TrimStart('v', 'V') : null;
        }

        static int Major(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            var m = Regex.Match(version, @"^(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>当前可用的 Node/npm：工具箱自带的优先，其次系统 PATH。</summary>
        public static void LocateNode(out string nodeExe, out string npmPath, out string nodeVersion, out string source)
        {
            nodeExe = FindRuntimeNode();
            source = "toolbox";
            if (nodeExe == null)
            {
                nodeExe = FindExe("node.exe", @"C:\Program Files\nodejs", @"C:\Program Files (x86)\nodejs");
                source = nodeExe != null ? "system" : "none";
            }
            npmPath = FindNpmFrom(nodeExe) ?? FindExe("npm.cmd", @"C:\Program Files\nodejs", @"C:\Program Files (x86)\nodejs");
            nodeVersion = NodeVersionOf(nodeExe);
        }

        // 版本探测要起子进程（npm --version 在本机约 3~5 秒），因此做磁盘缓存：
        // 同一台机器 10 分钟内重复调用不再重复起进程。
        static string ToolVersionCached(string key, string exe, string args, string toolHint)
        {
            string cacheFile = Path.Combine(Paths.Cache, "tool-versions.json");
            Dictionary<string, object> cache = null;
            try
            {
                if (File.Exists(cacheFile)) cache = Json.ParseObject(File.ReadAllText(cacheFile, Encoding.UTF8));
            }
            catch { }
            if (cache == null) cache = new Dictionary<string, object>();

            string stampKey = key + "_at";
            if (cache.ContainsKey(key) && cache.ContainsKey(stampKey))
            {
                try
                {
                    var at = Convert.ToDateTime(cache[stampKey]);
                    if ((DateTime.Now - at).TotalMinutes < 10) return Convert.ToString(cache[key], CultureInfo.InvariantCulture);
                }
                catch { }
            }

            string v = null;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var r = Proc.Run(exe, args, 30000, toolHint);
                v = (r.Stdout ?? "").Trim();
                if (v.Length == 0) v = null;
            }
            cache[key] = v;
            cache[stampKey] = DateTime.Now;
            try
            {
                Fs.EnsureDir(Paths.Cache);
                File.WriteAllText(cacheFile, Json.Write(cache), new UTF8Encoding(false));
            }
            catch { }
            return v;
        }

        /// <summary>代理信息：环境变量优先，其次 IE/WinINET 设置（npm 只认环境变量，所以要把 IE 代理显式传给它）。</summary>
        public static Dictionary<string, object> ProxyInfo()
        {
            string http = Environment.GetEnvironmentVariable("HTTP_PROXY") ?? Environment.GetEnvironmentVariable("http_proxy");
            string https = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy");
            string no = Environment.GetEnvironmentVariable("NO_PROXY") ?? Environment.GetEnvironmentVariable("no_proxy");
            bool ieEnabled = false; string ieServer = null, iePac = null;
            try
            {
                const string k = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                object en = Microsoft.Win32.Registry.GetValue(k, "ProxyEnable", 0);
                ieEnabled = en != null && Convert.ToInt32(en) == 1;
                ieServer = Convert.ToString(Microsoft.Win32.Registry.GetValue(k, "ProxyServer", null));
                iePac = Convert.ToString(Microsoft.Win32.Registry.GetValue(k, "AutoConfigURL", null));
            }
            catch { }
            string effective = !string.IsNullOrEmpty(https) ? https : (!string.IsNullOrEmpty(http) ? http : (ieEnabled ? ieServer : null));
            return Json.Obj(
                "envHttp", http, "envHttps", https, "envNo", no,
                "ieEnabled", ieEnabled, "ieServer", ieServer, "ieAutoConfig", iePac,
                "effective", effective,
                "source", !string.IsNullOrEmpty(https) || !string.IsNullOrEmpty(http) ? "env"
                          : (ieEnabled ? "ie" : (string.IsNullOrEmpty(iePac) ? "none" : "pac")));
        }

        static string NpmVersionOf(string npm)
        {
            if (string.IsNullOrEmpty(npm) || !File.Exists(npm)) return null;
            return ToolVersionCached("npm:" + npm, npm, "--version", "npm");
        }

        /// <summary>
        /// 安装类操作默认要求管理员（sudo）身份：机器级安装必须；用户级安装按用户偏好也要求，
        /// 可用 --user-level 显式降级为普通用户安装。检测类命令（prereq/dry-run）不受此限制。
        /// </summary>
        public static void RequireElevation(Ctx ctx)
        {
            if (_machine && !HostCommands.IsElevated())
                throw new ToolException("E_ELEVATION_REQUIRED",
                    L.T("--machine（全机安装）必须用管理员权限执行", "--machine (system-wide install) requires administrator rights"),
                    L.T("用 elevate.run 提权执行，例如：elevate.run -- install.cli --machine --yes", "Re-run elevated, e.g. elevate.run -- install.cli --machine --yes"), ExitCodes.Denied);
            if (ctx.Flag("user-level")) return;
            if (HostCommands.IsElevated()) return;
            throw new ToolException("E_ELEVATION_REQUIRED",
                L.T("安装默认在管理员模式下执行（更可靠）", "Installs run in administrator mode by default (more reliable)"),
                L.T("用 elevate.run 提权执行：elevate.run -- install.cli --yes；确实要装到当前用户下再加 --user-level",
                    "Re-run elevated: elevate.run -- install.cli --yes; add --user-level to install per-user instead"), ExitCodes.Denied);
        }

        // ================================================================ install.prereq
        static int RunPrereq(Ctx ctx)
        {
            bool fast = ctx.Flag("fast") || ctx.Flag("offline");
            _machine = ctx.Flag("machine");
            var report = PrereqItems(ctx, !fast);   // --fast：跳过联网与版本探测，只回答"在不在"
            int blocks = 0, warns = 0;
            foreach (var o in report)
            {
                var d = (Dictionary<string, object>)o;
                bool ok = H.B(d, "ok");
                string level = H.S(d, "level");
                if (!ok && level == "block") blocks++;
                else if (!ok && level == "warn") warns++;
            }

            string nodeExe, npm, nodeVer, src;
            LocateNode(out nodeExe, out npm, out nodeVer, out src);
            string cliVersion = H.S(HostCommands.CliInfoCached(ctx.Flag("refresh")), "version");

            var plan = new List<object>();
            if (nodeExe == null) plan.Add(L.T("1) install.node —— 下载官方 Node zip 并解压到用户目录", "1) install.node — download the official Node zip and extract it into the user directory"));
            if (npm == null) plan.Add(L.T("2) 使用随 Node 附带的 npm（同一目录）", "2) use the npm bundled with Node (same directory)"));
            plan.Add(L.T("3) npm install -g " + PkgName + " --prefix <用户目录> --no-fund --no-audit", "3) npm install -g " + PkgName + " --prefix <user dir> --no-fund --no-audit"));
            plan.Add(L.T("4) 把该目录加入**用户** PATH（不需要管理员）", "4) add that directory to the **user** PATH (no admin needed)"));
            plan.Add(L.T("5) 运行 dsh --version 验证", "5) verify by running dsh --version"));

            ctx.Out.Result("install.prereq", Json.Obj(
                "verdict", blocks == 0,
                "canAutoInstall", blocks == 0,
                "blockCount", blocks,
                "warnCount", warns,
                "nodeFound", nodeExe != null, "nodePath", nodeExe, "nodeVersion", nodeVer, "nodeSource", src,
                "npmFound", npm != null, "npmPath", npm,
                "cliVersion", cliVersion,
                "elevated", HostCommands.IsElevated(),
                "hostArch", HostArch(), "nodeTag", NodeTag,
                "proxy", ProxyInfo(),
                "machine", _machine,
                "rootDir", RootDir,
                "pathScope", _machine ? "machine" : "user",
                "needsStore", false,
                "needsGit", false,
                "needsNpm", npm == null,
                "needsNode", nodeExe == null,
                "plan", plan,
                "recipes", new[] { Proc.NonInteractiveRecipe("node"), Proc.NonInteractiveRecipe("npm"), Proc.NonInteractiveRecipe("git"), Proc.NonInteractiveRecipe("winget") },
                "items", report, "count", report.Count,
                "columns", new[] { "id", "name", "ok", "level", "detail" }));
            return ExitCodes.Ok;
        }

        /// <summary>探测项集合（install.prereq 与 install.cli --auto 共用）。</summary>
        public static List<object> PrereqItems(Ctx ctx, bool probeNetwork)
        {
            var items = new List<object>();
            Action<string, string, bool, string, string, string, string> add =
                (id, name, ok, level, detail, fix, fixId) =>
                items.Add(Json.Obj("id", id, "name", name, "ok", ok, "level", level, "detail", detail,
                                   "fix", string.IsNullOrEmpty(fix) ? null : fix, "fixId", string.IsNullOrEmpty(fixId) ? null : fixId));

            // 1) 架构
            bool x64 = Environment.Is64BitOperatingSystem;
            add("arch", L.T("64 位系统", "64-bit OS"), x64, x64 ? "info" : "block",
                x64 ? L.T("x64", "x64") : L.T("32 位系统无法运行官方 CLI", "32-bit systems cannot run the official CLI"), null, null);

            // 2) 磁盘
            long free = 0;
            try { free = new DriveInfo(Path.GetPathRoot(Paths.Home)).AvailableFreeSpace; } catch { }
            bool diskOk = free == 0 || free > 500L * 1024 * 1024;
            add("disk", L.T("可用磁盘空间", "Free disk space"), diskOk, diskOk ? "info" : "block",
                free == 0 ? L.T("读不到", "unreadable") : Fs.FormatSize(free) + L.T("（Node + npm 缓存约需 500 MB）", " (Node + npm cache need about 500 MB)"),
                diskOk ? null : L.T("清理磁盘后重试", "Free up disk space and retry"), null);

            // 3) Node / npm
            string nodeExe, npm, nodeVer, src;
            LocateNode(out nodeExe, out npm, out nodeVer, out src);
            bool nodeOk = nodeExe != null && Major(nodeVer) >= 18;
            add("node", "Node.js", nodeOk, nodeOk ? "info" : "warn",
                nodeExe == null
                    ? L.T("未检测到（将用官方 zip 自动装到用户目录）", "not found (will be installed automatically from the official zip into the user directory)")
                    : nodeVer + (nodeOk ? " (" + src + ")" : L.T("（需要 18 以上）", " (18+ required)")),
                nodeOk ? null : L.T("install.node --yes", "install.node --yes"), nodeOk ? null : "node");

            bool npmOk = npm != null;
            add("npm", "npm", npmOk, npmOk ? "info" : "warn",
                npm == null ? L.T("未检测到（随 Node 一起提供，装完 Node 就有）", "not found (ships with Node; installing Node provides it)")
                            : (NpmVersionOf(npm) ?? "?") + "  " + npm,
                npmOk ? null : L.T("install.node --yes", "install.node --yes"), npmOk ? null : "npm");

            // 3.5) 代理（企业网络下最常见的坑）
            var px = ProxyInfo();
            string pxSource = Convert.ToString(px.ContainsKey("source") ? px["source"] : "none");
            string pxEff = Convert.ToString(px.ContainsKey("effective") ? px["effective"] : null);
            add("proxy", L.T("网络代理", "Network proxy"), true, "info",
                pxSource == "none" ? L.T("未检测到代理（直连）", "no proxy detected (direct)")
                                   : pxSource + ": " + pxEff + (pxSource == "ie" ? L.T("（IE/系统设置；npm 不读它，安装时会显式传入）", " (IE/system setting; npm ignores it, so it is passed explicitly on install)") : ""),
                null, null);

            // 4) git —— 明确"不需要"
            string git = FindExe("git.exe");
            add("git", "git", true, "info",
                git == null ? L.T("未安装，**不影响**（CLI 走 npm 包，不需要 git）", "not installed, **not required** (the CLI ships as an npm package)")
                            : git,
                null, null);

            // 5) 微软商店 / winget —— 明确"不需要"
            string winget = FindExe("winget.exe");
            bool store = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
            add("store", L.T("微软商店 / winget", "Microsoft Store / winget"), true, "info",
                (winget != null ? "winget: " + winget : L.T("winget 不可用", "winget unavailable")) + " · " +
                (store ? L.T("商店存在", "Store present") : L.T("无商店", "no Store")) +
                L.T("；本工具不依赖它（Node 走官方 zip）", "; not required (Node comes from the official zip)"),
                null, null);

            // 6) PowerShell
            string psVer = HostCommands.RegStr(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine", "PowerShellVersion")
                        ?? HostCommands.RegStr(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PowerShell\1\PowerShellEngine", "PowerShellVersion");
            add("powershell", "PowerShell", !string.IsNullOrEmpty(psVer), string.IsNullOrEmpty(psVer) ? "warn" : "info",
                string.IsNullOrEmpty(psVer) ? L.T("未检测到", "not found") : psVer, null, null);

            // 7) 用户 PATH 是否已包含 npm 前缀目录
            string userPath = "";
            try { userPath = Environment.GetEnvironmentVariable("Path", PathScope) ?? ""; } catch { }
            bool prefixOnPath = userPath.IndexOf(NpmPrefix, StringComparison.OrdinalIgnoreCase) >= 0;
            add("userpath", L.T("用户 PATH 含 CLI 目录", "User PATH contains the CLI folder"), prefixOnPath, "info",
                prefixOnPath ? NpmPrefix : L.T("尚未加入：", "not added yet: ") + NpmPrefix,
                prefixOnPath ? null : L.T("install.cli --yes 会自动加入（只改用户级 PATH，不需要管理员）", "install.cli --yes adds it automatically (user-level PATH only, no admin)"),
                prefixOnPath ? null : "userpath");

            // 8) 已安装情况
            var cli = HostCommands.CliInfoCached(ctx != null && ctx.Flag("refresh"));
            add("dsh", PkgName, H.B(cli, "found"), "info",
                H.B(cli, "found") ? H.S(cli, "version") + "  " + H.S(cli, "path") : L.T("未安装（install.cli 可装）", "not installed (install.cli can install it)"),
                null, null);

            // 9) 权限
            bool admin = HostCommands.IsElevated();
            add("admin", L.T("管理员权限", "Administrator"), true, "info",
                admin ? L.T("当前是管理员（CLI 安装并不需要）", "running elevated (not needed for the CLI install)")
                      : L.T("当前是普通用户 —— 够用（全部装到用户目录）", "standard user — sufficient (everything goes to the user directory)"),
                null, null);

            // 10) 网络（可跳过）
            if (probeNetwork)
            {
                string d1, d2;
                bool netNode = HostCommands.ProbeUrl(NodeIndexUrl, 6000, out d1);
                add("net-nodejs", L.T("能访问 nodejs.org", "nodejs.org reachable"), netNode, netNode ? "info" : (nodeExe == null ? "block" : "warn"),
                    d1 + (netNode ? "" : L.T("；可用 --file <zip> 离线安装", "; use --file <zip> for an offline install")), null,
                    netNode ? null : "network");
                bool netNpm = HostCommands.ProbeUrl("https://registry.npmjs.org/-/ping", 6000, out d2);
                add("net-npm", L.T("能访问 npm registry", "npm registry reachable"), netNpm, netNpm ? "info" : "block",
                    d2, null, netNpm ? null : "network");
            }
            else
            {
                add("net", L.T("网络探测", "Network probe"), true, "info", L.T("已跳过（--fast）", "skipped (--fast)"), null, null);
            }

            return items;
        }

        // ================================================================ install.node
        static int RunNode(Ctx ctx)
        {
            _machine = ctx.Flag("machine");
            if (HostArch() == "x86" && string.IsNullOrEmpty(ctx.Get("file")))
                throw new ToolException("E_ARCH_UNSUPPORTED",
                    L.T("32 位 Windows 上官方已不再提供新版 Node 二进制", "Official Node binaries are no longer published for 32-bit Windows"),
                    L.T("请用 --file <zip> 指定 32 位 Node 包（如 LTS 18 的 win-x86），或改装 64 位系统", "Pass --file <zip> with a 32-bit Node build (e.g. an LTS 18 win-x86 zip), or use a 64-bit OS"), ExitCodes.Usage);

            string want = ctx.Get("version", "");
            string file = ctx.Get("file");
            bool force = ctx.Flag("force");

            Directory.CreateDirectory(RuntimeDir);

            // 已是目标版本则跳过
            if (!force && string.IsNullOrEmpty(file))
            {
                string existing = FindRuntimeNode(string.IsNullOrEmpty(want) ? null : want);
                if (existing != null)
                {
                    string v = NodeVersionOf(existing);
                    if (Major(v) >= 18 && (string.IsNullOrEmpty(want) || v == want.TrimStart('v', 'V')))
                    {
                        ctx.Out.Result("install.node", Json.Obj(
                            "verdict", true, "skipped", true,
                            "nodePath", existing, "nodeVersion", v, "npmPath", FindNpmFrom(existing),
                            "detail", L.T("工具箱自带的 Node 已就绪：", "The toolbox's own Node is already ready: ") + existing));
                        return ExitCodes.Ok;
                    }
                }
            }

            // 解析版本：--file 时从 zip 名推断，否则问官方 index.json 拿最新 LTS
            string version = want.TrimStart('v', 'V');
            if (string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(file))
            {
                var mm = Regex.Match(Path.GetFileName(file), @"v(\d+\.\d+\.\d+)");
                if (mm.Success) version = mm.Groups[1].Value;
            }
            if (string.IsNullOrEmpty(version))
            {
                version = ResolveLts(ctx);
                if (string.IsNullOrEmpty(version))
                    throw new ToolException("E_NO_VERSION", L.T("无法确定 Node 版本（拿不到 nodejs.org/dist/index.json）",
                        "Cannot determine the Node version (cannot read nodejs.org/dist/index.json)"),
                        L.T("用 --version <v> 指定版本，或用 --file <zip> 指定离线安装包", "Pass --version <v>, or --file <zip> for an offline package"), ExitCodes.NotFound);
            }

            string nodeFile = "node-v" + version + "-" + NodeTag + ".zip";
            string zipUrl = NodeDistBase + "/v" + version + "/" + nodeFile;
            string sumUrl = NodeDistBase + "/v" + version + "/SHASUMS256.txt";
            string dest = NodeDir(version);

            var plan = Json.Obj(
                "version", version, "url", string.IsNullOrEmpty(file) ? zipUrl : null, "file", file,
                "target", dest, "npmPrefix", NpmPrefix,
                "approach", L.T("官方 zip 直接解压（无 MSI/EXE 安装器 → 无界面、无协议页、无需管理员）",
                                "official zip extracted directly (no MSI/EXE installer → no UI, no licence pages, no admin)"),
                "steps", new[] {
                    L.T("下载 " + nodeFile, "Download " + nodeFile),
                    L.T("用官方 SHASUMS256.txt 校验 SHA-256", "Verify SHA-256 against the official SHASUMS256.txt"),
                    L.T("解压到 " + dest, "Extract into " + dest),
                    L.T("运行 node --version / npm --version 验证", "Verify with node --version / npm --version") });

            if (ctx.DryRun)
            {
                ctx.Out.Result("install.node", Json.Obj("dryRun", true, "plan", plan,
                    "detail", L.T("将安装 Node " + version + " 到 " + dest, "Would install Node " + version + " into " + dest)));
                return ExitCodes.Ok;
            }
            RequireElevation(ctx);
            ctx.ConfirmDestructive(L.T("下载并安装 Node.js（写入 ", "Download and install Node.js (into ") + dest + "）");

            // 1) 取包
            string zip = file;
            if (string.IsNullOrEmpty(zip))
            {
                zip = Path.Combine(Path.GetTempPath(), nodeFile);
                ctx.Out.Line(L.T("下载 ", "Downloading ") + zipUrl);
                Download(zipUrl, zip, ctx, "node");
            }
            if (!File.Exists(zip)) throw ToolException.NotFound(L.T("安装包不存在：", "Package not found: ") + zip);

            // 2) 校验 SHA-256
            string expected = null;
            if (string.IsNullOrEmpty(file))
            {
                try
                {
                    string sums = HttpGet(sumUrl, 20000);
                    var mm = Regex.Match(sums ?? "", @"(?m)^([0-9a-fA-F]{64})\s+\*?" + Regex.Escape(nodeFile) + @"\s*$");
                    if (mm.Success) expected = mm.Groups[1].Value.ToLowerInvariant();
                }
                catch { }
            }
            string actual = Sha256(zip);
            bool verified = expected == null || expected == actual;
            if (!verified)
                throw new ToolException("E_HASH_MISMATCH", L.T("Node 安装包 SHA-256 不匹配", "Node package SHA-256 mismatch"),
                    L.T("期望 " + expected + "，实际 " + actual + "；已中止安装", "expected " + expected + ", got " + actual + "; install aborted"), ExitCodes.Error);

            // 3) 解压（.NET FW 的 ExtractToDirectory 没有覆盖重载，先清目录）
            ctx.Out.EmitItem(Json.Obj("phase", "extract", "percent", 90, "detail", L.T("解压中…", "extracting…")));
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, dest);

            // 4) 验证
            string nodeExe = Path.Combine(dest, "node.exe");
            if (!File.Exists(nodeExe)) throw new ToolException("E_NODE_LAYOUT", L.T("解压后找不到 node.exe", "node.exe not found after extraction"), dest, ExitCodes.Error);
            string nodeVer = NodeVersionOf(nodeExe);
            string npmPath = FindNpmFrom(nodeExe);
            string npmVer = NpmVersionOf(npmPath);
            bool ok = Major(nodeVer) >= 18 && !string.IsNullOrEmpty(npmPath);

            if (string.IsNullOrEmpty(file)) { try { File.Delete(zip); } catch { } }

            var items = new List<object>
            {
                Json.Obj("name", L.T("版本", "Version"), "value", nodeVer ?? "?"),
                Json.Obj("name", "node.exe", "value", nodeExe),
                Json.Obj("name", "npm", "value", npmPath ?? L.T("未找到", "not found")),
                Json.Obj("name", L.T("npm 版本", "npm version"), "value", npmVer ?? "?"),
                Json.Obj("name", L.T("SHA-256 校验", "SHA-256 check"), "value",
                         expected == null ? L.T("官方校验和不可得（已跳过）", "official checksum unavailable (skipped)") : (verified ? L.T("一致", "match") : L.T("不一致", "mismatch")))
            };
            ctx.Out.Result("install.node", Json.Obj(
                "verdict", ok, "version", version, "nodeVersion", nodeVer, "nodePath", nodeExe,
                "npmPath", npmPath, "npmVersion", npmVer, "target", dest,
                "sha256", actual, "sha256Expected", expected, "verified", verified,
                "plan", plan, "items", items, "count", items.Count, "columns", new[] { "name", "value" },
                "reason", ok ? null : L.T("Node 安装后无法运行或版本过低", "Node does not run after install, or the version is too old")));
            if (!ok) return ExitCodes.Error;

            // 让后续步骤（同一个进程内）立刻用上
            try { Environment.SetEnvironmentVariable("DSH_TOOLBOX_NODE", nodeExe); } catch { }
            return ExitCodes.Ok;
        }

        static string ResolveLts(Ctx ctx)
        {
            try
            {
                string json = HttpGet(NodeIndexUrl, 20000);
                if (string.IsNullOrEmpty(json)) return null;
                // index.json 是数组，取第一个 lts != false 的版本（nodejs.org 按新→旧排）
                var m = Regex.Match(json, "\"version\"\\s*:\\s*\"v([0-9.]+)\"[^}]*?\"lts\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
                m = Regex.Match(json, "\"version\"\\s*:\\s*\"v([0-9.]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception ex) { ctx.Out.Warn(L.T("读取 Node 版本列表失败：", "Reading the Node version list failed: ") + ex.Message); return null; }
        }

        // ================================================================ install.cli 全自动链路
        /// <summary>install.cli 的默认路径：探测 → 装 Node（如需）→ npm 装 CLI → 用户 PATH → 验证。</summary>
        public static int InstallCliAuto(Ctx ctx)
        {
            var steps = new List<object>();
            string version = ctx.Get("version", "");
            string registry = ctx.Get("registry");
            _machine = ctx.Flag("machine");
            bool noPath = ctx.Flag("no-path");
            string prefixOverride = ctx.Get("prefix");
            string prefix = string.IsNullOrEmpty(prefixOverride) ? NpmPrefix : Path.GetFullPath(prefixOverride);

            string nodeExe, npm, nodeVer, src;
            LocateNode(out nodeExe, out npm, out nodeVer, out src);
            bool nodeOk = nodeExe != null && Major(nodeVer) >= 18;

            var plan = Json.Obj(
                "needsNode", !nodeOk, "needsNpm", npm == null,
                "npmPrefix", prefix, "package", string.IsNullOrEmpty(version) ? PkgName : PkgName + "@" + version,
                "steps", new[] {
                    L.T("环境探测（install.prereq）", "environment check (install.prereq)"),
                    nodeOk ? L.T("复用已有 Node " + nodeVer, "reuse the existing Node " + nodeVer) : L.T("install.node（官方 zip，用户级）", "install.node (official zip, user-level)"),
                    L.T("npm install -g（--no-fund --no-audit --yes，非交互）", "npm install -g (--no-fund --no-audit --yes, non-interactive)"),
                    noPath ? L.T("跳过 PATH 修改", "skip the PATH change")
                           : (_machine ? L.T("把前缀目录加入系统 PATH（需管理员）", "add the prefix directory to the system PATH (needs admin)")
                                        : L.T("把前缀目录加入用户 PATH", "add the prefix directory to the user PATH")),
                    L.T("运行 dsh --version 验证", "verify with dsh --version") });

            if (ctx.DryRun)
            {
                ctx.Out.Result("install.cli", Json.Obj("dryRun", true, "auto", true, "plan", plan,
                    "detail", L.T("将自动完成：探测 → 装依赖（如需）→ npm 全局装到 ", "Would automatically: check → install dependencies (if needed) → npm global install into ") + prefix));
                return ExitCodes.Ok;
            }

            RequireElevation(ctx);

            // --- 步骤 1：Node
            if (!nodeOk)
            {
                ctx.Out.EmitItem(Json.Obj("phase", "node", "percent", 3, "detail", L.T("未检测到可用的 Node，开始自动安装", "No usable Node found; installing automatically")));
                var nodeCtx = MakeSubCtx(ctx, new[] { "install.node", "--yes", "--json" });
                int rc = RunNode(nodeCtx);
                steps.Add(Json.Obj("step", "install.node", "exit", rc));
                if (rc != 0) return Fail(ctx, "E_NODE_INSTALL", L.T("Node 自动安装失败，无法继续", "Automatic Node install failed; cannot continue"));
                LocateNode(out nodeExe, out npm, out nodeVer, out src);
                if (nodeExe == null || Major(nodeVer) < 18) return Fail(ctx, "E_NODE_INSTALL", L.T("Node 装完仍不可用", "Node is still unusable after install"));
            }
            else
            {
                steps.Add(Json.Obj("step", "reuse-node", "detail", nodeVer + " @ " + nodeExe));
            }

            if (npm == null) return Fail(ctx, "E_NO_NPM", L.T("找不到 npm（Node 目录里应当自带）", "npm not found (it should ship with Node)"));

            // --- 步骤 2：npm 全局安装到用户前缀（非交互 + 疑似交互自动重试）
            string pkgArg = string.IsNullOrEmpty(version) ? PkgName : PkgName + "@" + version;
            Directory.CreateDirectory(prefix);
            string npmArgs = "install -g \"" + pkgArg + "\" --prefix \"" + prefix + "\" --no-fund --no-audit --loglevel=error";
            if (!string.IsNullOrEmpty(registry)) npmArgs += " --registry \"" + registry + "\"";
            var pxCli = ProxyInfo();
            string pxSrc = Convert.ToString(pxCli.ContainsKey("source") ? pxCli["source"] : "none");
            string pxVal = Convert.ToString(pxCli.ContainsKey("effective") ? pxCli["effective"] : null);
            if (pxSrc == "ie" && !string.IsNullOrEmpty(pxVal))
            {
                npmArgs += " --proxy \"" + pxVal + "\" --https-proxy \"" + pxVal + "\"";
                ctx.Out.Line(L.T("检测到 IE/系统代理，已显式传给 npm：", "IE/system proxy detected; passing it to npm explicitly: ") + pxVal);
            }
            ctx.Out.Line("npm " + npmArgs);
            ctx.Out.EmitItem(Json.Obj("phase", "npm", "percent", 40, "detail", L.T("npm 全局安装中…", "npm global install…")));

            Dictionary<string, object> trace;
            var r = Proc.RunHardened(npm, npmArgs, 20 * 60 * 1000, "npm", "--yes", Path.GetDirectoryName(npm), out trace);
            steps.Add(Json.Obj("step", "npm-install", "exit", r.Exit, "elapsedMs", r.ElapsedMs, "promptSuspected", r.PromptSuspected));
            ctx.Out.EmitItem(Json.Obj("phase", "npm", "percent", 80, "detail", L.T("npm 完成（退出码 ", "npm finished (exit ") + r.Exit + ")"));

            if (r.TimedOut || r.PromptSuspected)
                ctx.Out.Warn(L.T("npm 疑似停在交互提示上（已尝试非交互参数重试）：", "npm appears to have stopped at an interactive prompt (retried with non-interactive flags): ") + r.PromptHint);

            // --- 步骤 3：用户 PATH
            bool pathUpdated = false;
            string pathDetail = L.T("未修改", "unchanged");
            if (!noPath)
            {
                string userPath = "";
                try { userPath = Environment.GetEnvironmentVariable("Path", PathScope) ?? ""; } catch { }
                if (userPath.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    try
                    {
                        string joined = string.IsNullOrEmpty(userPath) ? prefix : userPath.TrimEnd(';') + ";" + prefix;
                        Environment.SetEnvironmentVariable("Path", joined, PathScope);
                        pathUpdated = true;
                        pathDetail = L.T("已把 " + prefix + " 加入" + (_machine ? "系统" : "用户") + " PATH（新开的终端才生效）",
                                         "added " + prefix + " to the " + (_machine ? "system" : "user") + " PATH (takes effect in new terminals)");
                    }
                    catch (Exception ex) { pathDetail = L.T("写入用户 PATH 失败：", "writing the user PATH failed: ") + ex.Message; }
                }
                else { pathDetail = L.T("用户 PATH 里已有该目录", "the user PATH already contains it"); }
            }
            steps.Add(Json.Obj("step", "user-path", "updated", pathUpdated, "detail", pathDetail));

            // --- 步骤 4：验证（直接跑装出来的 dsh，不依赖 PATH 刷新生效）
            string shim = null;
            foreach (var n in new[] { "dsh.cmd", "dsh.exe", "dsh" })
            {
                string p = Path.Combine(prefix, n);
                if (File.Exists(p)) { shim = p; break; }
            }
            string cliVersion = null, cliOut = null;
            if (shim != null)
            {
                var vr = Proc.Run(shim, "--version", 60000, "node");
                cliOut = vr.Tail(2000);
                var vm = Regex.Match(cliOut ?? "", @"(\d+\.\d+\.\d+[-\w.]*)");
                cliVersion = vm.Success ? vm.Groups[1].Value : (cliOut ?? "").Trim();
            }
            var after = HostCommands.CliInfoCached(true);
            bool ok2 = shim != null && !string.IsNullOrEmpty(cliVersion);

            var items = new List<object>
            {
                Json.Obj("name", "Node", "value", nodeVer + "  " + (src == "toolbox" ? L.T("（工具箱自带）", "(bundled with the toolbox)") : nodeExe)),
                Json.Obj("name", "npm", "value", npm),
                Json.Obj("name", "CLI", "value", (shim ?? H.S(after, "path", L.T("未找到", "not found")))),
                Json.Obj("name", L.T("CLI 版本", "CLI version"), "value", cliVersion ?? "-"),
                Json.Obj("name", L.T("用户 PATH", "User PATH"), "value", pathDetail)
            };

            ctx.Out.Result("install.cli", Json.Obj(
                "verdict", ok2, "auto", true,
                "nodeVersion", nodeVer, "nodePath", nodeExe, "npmPath", npm,
                "path", shim ?? "", "version", cliVersion ?? "", "installed", ok2,
                "npmPrefix", prefix, "pathUpdated", pathUpdated,
                "elevated", HostCommands.IsElevated(), "machine", _machine,
                "pathScope", _machine ? "machine" : "user",
                "npmExit", r.Exit, "npmElapsedMs", r.ElapsedMs,
                "interactiveTrace", trace,
                "output", r.Tail(4000),
                "plan", plan, "steps", steps,
                "items", items, "count", items.Count, "columns", new[] { "name", "value" },
                "reason", ok2 ? null : L.T("npm 执行完毕但没找到可用的 dsh；可查看 output 定位",
                                          "npm finished but no usable dsh was found; check output")));
            if (!ok2) { ctx.Out.Warn(L.T("npm 输出（尾部）：", "npm output (tail): ") + r.Tail(800)); return ExitCodes.Error; }
            return ExitCodes.Ok;
        }

        static int Fail(Ctx ctx, string code, string message)
        {
            ctx.Out.Fail("install.cli", code, message,
                L.T("查看 install.prereq --json 的探测结果", "See the install.prereq --json report"), ExitCodes.Error);
            return ExitCodes.Error;
        }

        // ================================================================ HTTP 辅助
        static string HttpGet(string url, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "dsh-toolbox/" + ToolInfo.Version;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static void Download(string url, string dest, Ctx ctx, string tool)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 30000; req.ReadWriteTimeout = 300000;
            req.UserAgent = "dsh-toolbox/" + ToolInfo.Version;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                long total = resp.ContentLength, done = 0, last = 0;
                var buf = new byte[1 << 20];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ctx.ThrowIfCancelled();
                    dst.Write(buf, 0, n);
                    done += n;
                    if (done - last > 4L * 1024 * 1024)
                    {
                        last = done;
                        int pct = total > 0 ? (int)(done * 100 / total) : 0;
                        ctx.Out.EmitItem(Json.Obj("phase", "download", "tool", tool,
                            "bytes", done, "totalBytes", total, "percent", pct,
                            "done", Fs.FormatSize(done), "total", total > 0 ? Fs.FormatSize(total) : "?"));
                    }
                }
            }
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(Fs.LongPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, buf, 0);
                sha.TransformFinalBlock(buf, 0, 0);
                var sb = new StringBuilder();
                foreach (var b in sha.Hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        static Ctx MakeSubCtx(Ctx parent, string[] argv)
        {
            var ctx = new Ctx();
            ctx.Args = ArgMap.Parse(argv);
            ctx.Out = parent.Out;
            ctx.Log = parent.Log;
            ctx.Cwd = parent.Cwd;
            ctx.ExePath = parent.ExePath;
            ctx.DryRun = parent.DryRun;
            ctx.Yes = parent.Yes;
            ctx.Cancel = parent.Cancel;
            return ctx;
        }
    }
}
