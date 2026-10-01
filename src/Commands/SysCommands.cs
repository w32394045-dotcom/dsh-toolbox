using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using DshToolbox.Core;
using Win32Registry = Microsoft.Win32.Registry;
using Win32RegistryKey = Microsoft.Win32.RegistryKey;

namespace DshToolbox.Commands
{
    /// <summary>服务/系统类命令（sysdev 拥有）：svc.* disk.* eventlog.* installed.* defender.* startup.*。</summary>
    public static class SysCommands
    {
        public static void Register()
        {
            Registry.Add("svc.list", "服务列表：状态/启动类型/可停止性（ServiceController + 注册表 Start）",
                "svc list [--name <s|glob>] [--state all|running|stopped] [--startup auto|manual|disabled|boot|system] [--with-pid]",
                RunSvcList,
                aliases: new[] { "services" },
                examples: new[] { "dsh-toolbox svc list --state running --json", "dsh-toolbox svc list --name WinDefend" });

            Registry.Add("svc.control", "启停服务：start|stop|restart（需 --yes；--dry-run 只预览）",
                "svc control --name <svc> [--name <svc>...] --action start|stop|restart [--timeout <dur>] [--dry-run|--yes]",
                RunSvcControl,
                examples: new[] { "dsh-toolbox svc control --name Spooler --action restart --dry-run --json" });

            Registry.Add("disk.space", "各卷容量/可用/文件系统/使用率",
                "disk space [--path <p>] [--fixed-only]",
                RunDiskSpace,
                aliases: new[] { "df" },
                examples: new[] { "dsh-toolbox disk space --json" });

            Registry.Add("disk.health", "物理磁盘健康概览（WMI 状态 + SMART 预测，尽力而为）",
                "disk health [--json]",
                RunDiskHealth,
                examples: new[] { "dsh-toolbox disk health --json" });

            Registry.Add("eventlog.query", "事件日志查询：LogName/Level/Provider/Id/时间区间/关键字",
                "eventlog query [--log System] [--level error|warning|info|critical|verbose]... [--provider <n>]... [--id <n>]... [--since <-2h|date>] [--until <date>] [--keyword <s>] [--message-regex <re>] [--max <n>] [--oldest] [--no-message]",
                RunEventQuery,
                aliases: new[] { "evt" },
                examples: new[] { "dsh-toolbox eventlog query --log System --level error --max 10 --json", "dsh-toolbox eventlog query --log Application --since -24h --keyword crash" });

            Registry.Add("eventlog.list", "可用事件日志清单（名称/条目数）",
                "eventlog list [--json]",
                RunEventList,
                examples: new[] { "dsh-toolbox eventlog list --json" });

            Registry.Add("installed.list", "已安装程序（HKLM + WOW6432Node + HKCU 的 Uninstall 键）",
                "installed list [--match <re>] [--publisher <s>] [--all] [--sort name|date|size]",
                RunInstalled,
                aliases: new[] { "apps" },
                examples: new[] { "dsh-toolbox installed list --match \"node|python\" --json", "dsh-toolbox installed list --sort size --json" });

            Registry.Add("defender.status", "Windows Defender 状态与排除项（WMI SecurityCenter2 + 注册表）",
                "defender status [--json]",
                RunDefender,
                examples: new[] { "dsh-toolbox defender status --json" });

            Registry.Add("startup.list", "启动项：Run/RunOnce 注册表键 + 启动文件夹",
                "startup list [--all] [--json]",
                RunStartup,
                examples: new[] { "dsh-toolbox startup list --json" });
        }

        // ================================================================ svc.list
        static int RunSvcList(Ctx ctx)
        {
            string namePat = ctx.Get("name");
            string stateWanted = (ctx.Get("state") ?? "all").ToLowerInvariant();
            string startupWanted = (ctx.Get("startup") ?? "").ToLowerInvariant();
            bool withPid = ctx.Flag("with-pid");

            ServiceController[] svcs;
            try { svcs = ServiceController.GetServices(); }
            catch (Exception ex) { throw ToolException.Denied("无法枚举服务（SCM 不可用）：" + ex.Message); }

            Dictionary<string, int> pidByName = null;
            string wmiError = null;
            if (withPid)
            {
                pidByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (var s = new ManagementObjectSearcher("SELECT Name,ProcessId,StartMode,State FROM Win32_Service"))
                    using (var r = s.Get())
                        foreach (ManagementBaseObject mo in r)
                        {
                            try
                            {
                                string n = Convert.ToString(mo["Name"], CultureInfo.InvariantCulture);
                                if (!string.IsNullOrEmpty(n))
                                    pidByName[n] = Convert.ToInt32(mo["ProcessId"] ?? 0, CultureInfo.InvariantCulture);
                            }
                            catch { }
                            finally { try { mo.Dispose(); } catch { } }
                        }
                }
                catch (Exception ex) { wmiError = ex.Message; ctx.Out.Warn("Win32_Service 查询失败：" + ex.Message); }
            }

            var items = new List<object>();
            foreach (var sc in svcs)
            {
                ctx.ThrowIfCancelled();
                string status;
                try { status = sc.Status.ToString(); } catch { status = "Unknown"; }
                if (stateWanted == "running" && !string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)) continue;
                if (stateWanted == "stopped" && string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)) continue;
                if (!SysProcs.MatchName(namePat, sc.ServiceName) &&
                    !SysProcs.MatchText(namePat, sc.DisplayName, null)) continue;

                bool delayed;
                string startType = StartTypeOf(sc.ServiceName, out delayed);
                if (!string.IsNullOrEmpty(startupWanted) && !string.Equals(startType, startupWanted, StringComparison.OrdinalIgnoreCase)) continue;

                bool canStop = false, canPause = false;
                try { canStop = sc.CanStop; } catch { }
                try { canPause = sc.CanPauseAndContinue; } catch { }

                var d = Json.Obj(
                    "name", sc.ServiceName,
                    "displayName", sc.DisplayName,
                    "status", status,
                    "startType", startType,
                    "delayedAutoStart", delayed,
                    "canStop", canStop,
                    "canPause", canPause,
                    "serviceType", sc.ServiceType.ToString());
                if (pidByName != null)
                {
                    int pid;
                    bool has = pidByName.TryGetValue(sc.ServiceName, out pid);
                    d["pid"] = has && pid > 0 ? (object)pid : null;
                }
                items.Add(d);
                try { sc.Dispose(); } catch { }
            }

            var data = Json.Obj(
                "items", items,
                "count", items.Count,
                "total", svcs.Length,
                "state", stateWanted,
                "wmiError", wmiError,
                "columns", new[] { "name", "displayName", "status", "startType", "canStop" });
            if (pidByName != null) data["withPid"] = true;

            ctx.Out.Result("svc.list", data);
            return items.Count > 0 ? ExitCodes.Ok : ExitCodes.NotFound;
        }

        static string StartTypeOf(string name, out bool delayed)
        {
            delayed = false;
            try
            {
                using (var k = Win32Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                {
                    if (k == null) return null;
                    object v = k.GetValue("Start");
                    object d = k.GetValue("DelayedAutostart");
                    delayed = d != null && Convert.ToInt32(d, CultureInfo.InvariantCulture) == 1;
                    if (v == null) return null;
                    switch (Convert.ToInt32(v, CultureInfo.InvariantCulture))
                    {
                        case 0: return "boot";
                        case 1: return "system";
                        case 2: return "auto";
                        case 3: return "manual";
                        case 4: return "disabled";
                        default: return "unknown";
                    }
                }
            }
            catch { return null; }
        }

        // ================================================================ svc.control
        static int RunSvcControl(Ctx ctx)
        {
            var names = ctx.GetAll("name").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (names.Count == 0) names.AddRange(ctx.Args.Positional.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (names.Count == 0) throw ToolException.Usage("缺少 --name", "例：svc control --name Spooler --action restart --yes");

            string action = (ctx.Get("action") ?? ctx.Get("do") ?? "").ToLowerInvariant();
            if (action != "start" && action != "stop" && action != "restart")
                throw ToolException.Usage("缺少或非法的 --action", "--action 只支持 start|stop|restart");

            TimeSpan timeout = ctx.Args.GetSpan("timeout", TimeSpan.FromSeconds(30));

            var plan = new List<object>();
            foreach (var n in names)
            {
                string status = null;
                bool canStop = false;
                try
                {
                    using (var sc = new ServiceController(n))
                    {
                        status = sc.Status.ToString();
                        canStop = sc.CanStop;
                    }
                }
                catch (InvalidOperationException ex) { throw ToolException.NotFound("服务不存在或无法打开：" + n + " —— " + ex.Message); }
                catch (Exception ex) { throw ToolException.Denied("无法访问服务 " + n + "：" + ex.Message); }
                plan.Add(Json.Obj("name", n, "currentStatus", status, "targetAction", action, "canStop", canStop));
            }

            if (!ctx.ConfirmDestructive(string.Format("{0} 服务：{1}", action, string.Join(", ", names.ToArray()))))
            {
                ctx.Out.Result("svc.control", Json.Obj(
                    "dryRun", true, "action", action, "plan", plan, "items", plan, "count", plan.Count,
                    "timeout", timeout, "columns", new[] { "name", "currentStatus", "targetAction", "canStop" }));
                return ExitCodes.Ok;
            }

            var results = new List<object>();
            var failures = new List<object>();
            int denied = 0;

            foreach (var n in names)
            {
                ctx.ThrowIfCancelled();
                var rec = Json.Obj("name", n, "action", action, "ok", false, "finalStatus", null, "skipped", false, "error", null);
                try
                {
                    using (var sc = new ServiceController(n))
                    {
                        if (action == "start")
                        {
                            if (sc.Status == ServiceControllerStatus.Running) { rec["skipped"] = true; rec["ok"] = true; }
                            else { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, timeout); rec["ok"] = true; }
                        }
                        else if (action == "stop")
                        {
                            if (sc.Status == ServiceControllerStatus.Stopped) { rec["skipped"] = true; rec["ok"] = true; }
                            else
                            {
                                if (!sc.CanStop) throw new InvalidOperationException("该服务不允许停止");
                                sc.Stop();
                                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                                rec["ok"] = true;
                            }
                        }
                        else
                        {
                            if (sc.Status != ServiceControllerStatus.Stopped)
                            {
                                if (!sc.CanStop) throw new InvalidOperationException("该服务不允许停止，无法 restart");
                                sc.Stop();
                                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                            }
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                            rec["ok"] = true;
                        }
                        try { sc.Refresh(); rec["finalStatus"] = sc.Status.ToString(); } catch { }
                    }
                }
                catch (System.ServiceProcess.TimeoutException)
                {
                    rec["error"] = "等待状态变化超时（" + timeout + "）";
                    failures.Add(Json.Obj("name", n, "error", "timeout"));
                }
                catch (System.ComponentModel.Win32Exception wex)
                {
                    rec["error"] = wex.Message + "（Win32 " + wex.NativeErrorCode + "）";
                    if (wex.NativeErrorCode == 5) denied++;
                    failures.Add(Json.Obj("name", n, "error", rec["error"]));
                }
                catch (UnauthorizedAccessException ex)
                {
                    rec["error"] = ex.Message;
                    denied++;
                    failures.Add(Json.Obj("name", n, "error", ex.Message));
                }
                catch (InvalidOperationException ex)
                {
                    rec["error"] = ex.Message;
                    failures.Add(Json.Obj("name", n, "error", ex.Message));
                }
                catch (Exception ex)
                {
                    rec["error"] = ex.Message;
                    failures.Add(Json.Obj("name", n, "error", ex.Message));
                }
                results.Add(rec);
            }

            int exit = ExitCodes.Ok;
            if (failures.Count > 0) exit = denied > 0 && denied == failures.Count ? ExitCodes.Denied : ExitCodes.Partial;

            ctx.Out.Result("svc.control", Json.Obj(
                "action", action,
                "items", results,
                "count", results.Count,
                "okCount", results.OfType<Dictionary<string, object>>().Count(o => Convert.ToBoolean(o["ok"])),
                "failures", failures,
                "plan", plan,
                "timeout", timeout,
                "columns", new[] { "name", "action", "ok", "finalStatus", "error" }));
            return exit;
        }

        // ================================================================ disk.space
        static int RunDiskSpace(Ctx ctx)
        {
            string pathFilter = ctx.Get("path");
            bool fixedOnly = ctx.Flag("fixed-only");
            string rootFilter = null;
            if (!string.IsNullOrEmpty(pathFilter))
            {
                try { rootFilter = Path.GetPathRoot(Fs.Expand(pathFilter, ctx.Cwd)); } catch { }
            }

            var items = new List<object>();
            foreach (var d in DriveInfo.GetDrives())
            {
                ctx.ThrowIfCancelled();
                if (!string.IsNullOrEmpty(rootFilter) && !string.Equals(d.Name, rootFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (fixedOnly && d.DriveType != DriveType.Fixed) continue;

                bool ready = false;
                try { ready = d.IsReady; } catch { }
                if (!ready)
                {
                    items.Add(Json.Obj("name", d.Name, "ready", false, "type", d.DriveType.ToString(),
                                       "label", null, "fs", null, "totalBytes", null, "freeBytes", null, "usedPct", null));
                    continue;
                }
                long total = 0, free = 0;
                string label = null, fs = null;
                try { total = d.TotalSize; } catch { }
                try { free = d.AvailableFreeSpace; } catch { }
                try { label = d.VolumeLabel; } catch { }
                try { fs = d.DriveFormat; } catch { }
                items.Add(Json.Obj(
                    "name", d.Name,
                    "ready", true,
                    "type", d.DriveType.ToString(),
                    "label", label,
                    "fs", fs,
                    "totalBytes", total,
                    "total", Fs.FormatSize(total),
                    "freeBytes", free,
                    "free", Fs.FormatSize(free),
                    "usedBytes", total - free,
                    "usedPct", total > 0 ? (object)Math.Round((total - free) * 100.0 / total, 1) : null));
            }

            ctx.Out.Result("disk.space", Json.Obj(
                "items", items,
                "count", items.Count,
                "columns", new[] { "name", "label", "fs", "total", "free", "usedPct" }));
            return items.Count > 0 ? ExitCodes.Ok : ExitCodes.NotFound;
        }

        // ================================================================ disk.health
        static int RunDiskHealth(Ctx ctx)
        {
            var items = new List<object>();
            var notes = new List<string>();

            Dictionary<string, List<string>> diskToParts = new Dictionary<string, List<string>>();
            Dictionary<string, List<string>> partToVols = new Dictionary<string, List<string>>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDriveToDiskPartition"))
                using (var r = s.Get())
                    foreach (ManagementBaseObject mo in r)
                    {
                        try
                        {
                            string disk = ExtractDeviceId(Convert.ToString(mo["Antecedent"], CultureInfo.InvariantCulture));
                            string part = ExtractDeviceId(Convert.ToString(mo["Dependent"], CultureInfo.InvariantCulture));
                            if (disk == null || part == null) continue;
                            List<string> l;
                            if (!diskToParts.TryGetValue(disk, out l)) { l = new List<string>(); diskToParts[disk] = l; }
                            l.Add(part);
                        }
                        catch { }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition"))
                using (var r = s.Get())
                    foreach (ManagementBaseObject mo in r)
                    {
                        try
                        {
                            string part = ExtractDeviceId(Convert.ToString(mo["Antecedent"], CultureInfo.InvariantCulture));
                            string vol = ExtractDeviceId(Convert.ToString(mo["Dependent"], CultureInfo.InvariantCulture));
                            if (part == null || vol == null) continue;
                            List<string> l;
                            if (!partToVols.TryGetValue(part, out l)) { l = new List<string>(); partToVols[part] = l; }
                            l.Add(vol);
                        }
                        catch { }
                        finally { try { mo.Dispose(); } catch { } }
                    }
            }
            catch (Exception ex) { notes.Add("卷映射不可用：" + ex.Message); }

            bool smartAvailable = false;
            var smart = new Dictionary<int, string>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\wmi");
                scope.Connect();
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictStatus")))
                using (var r = s.Get())
                {
                    int i = 0;
                    foreach (ManagementBaseObject mo in r)
                    {
                        try
                        {
                            object pf = mo["PredictFailure"];
                            object reason = mo["Reason"];
                            smart[i] = "PredictFailure=" + Convert.ToString(pf, CultureInfo.InvariantCulture) +
                                       " Reason=" + Convert.ToString(reason, CultureInfo.InvariantCulture);
                            smartAvailable = true;
                        }
                        catch { }
                        finally { try { mo.Dispose(); } catch { } }
                        i++;
                    }
                }
            }
            catch (Exception ex) { notes.Add("SMART（MSStorageDriver_FailurePredictStatus）不可用：" + ex.Message); }

            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT Model,InterfaceType,Size,Status,SerialNumber,MediaType,Partitions,DeviceID,BytesPerSector FROM Win32_DiskDrive"))
                using (var r = s.Get())
                {
                    int idx = 0;
                    foreach (ManagementBaseObject mo in r)
                    {
                        try
                        {
                            string devId = Convert.ToString(mo["DeviceID"], CultureInfo.InvariantCulture);
                            long size = 0;
                            try { size = Convert.ToInt64(mo["Size"] ?? 0, CultureInfo.InvariantCulture); } catch { }
                            var vols = new List<string>();
                            List<string> parts;
                            if (devId != null && diskToParts.TryGetValue(devId, out parts))
                                foreach (var p in parts)
                                {
                                    List<string> vv;
                                    if (partToVols.TryGetValue(p, out vv)) vols.AddRange(vv);
                                }
                            string smartInfo;
                            smart.TryGetValue(idx, out smartInfo);
                            items.Add(Json.Obj(
                                "deviceId", devId,
                                "model", Convert.ToString(mo["Model"], CultureInfo.InvariantCulture),
                                "interface", Convert.ToString(mo["InterfaceType"], CultureInfo.InvariantCulture),
                                "mediaType", Convert.ToString(mo["MediaType"], CultureInfo.InvariantCulture),
                                "serial", Convert.ToString(mo["SerialNumber"], CultureInfo.InvariantCulture),
                                "sizeBytes", size,
                                "size", Fs.FormatSize(size),
                                "partitions", mo["Partitions"],
                                "status", Convert.ToString(mo["Status"], CultureInfo.InvariantCulture),
                                "smart", smartInfo,
                                "volumes", vols));
                        }
                        catch { }
                        finally { try { mo.Dispose(); } catch { } }
                        idx++;
                    }
                }
            }
            catch (Exception ex) { notes.Add("Win32_DiskDrive 查询失败：" + ex.Message); }

            ctx.Out.Result("disk.health", Json.Obj(
                "items", items,
                "count", items.Count,
                "smartAvailable", smartAvailable,
                "notes", notes,
                "columns", new[] { "deviceId", "model", "size", "status", "smart" }));
            return ExitCodes.Ok;
        }

        static string ExtractDeviceId(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int i = path.IndexOf("DeviceID=\"", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return path;
            int start = i + 10;
            int end = path.IndexOf('"', start);
            if (end < 0) return path.Substring(start);
            return path.Substring(start, end - start);
        }

        // ================================================================ eventlog
        static int RunEventQuery(Ctx ctx)
        {
            string logName = ctx.Get("log") ?? ctx.Get("logname") ?? "System";
            int max = ctx.Args.GetInt("max", ctx.Args.GetInt("max-results", 50));
            bool withMessage = !ctx.Flag("no-message");
            bool oldest = ctx.Flag("oldest");
            int maxMsgLen = ctx.Args.GetInt("max-message-len", 500);
            DateTime? since = ctx.Args.GetDate("since");
            DateTime? until = ctx.Args.GetDate("until");

            var levels = new List<int>();
            foreach (var l in ctx.GetAll("level"))
            {
                switch (l.Trim().ToLowerInvariant())
                {
                    case "critical": case "fatal": case "1": levels.Add(1); break;
                    case "error": case "2": levels.Add(2); break;
                    case "warning": case "warn": case "3": levels.Add(3); break;
                    case "information": case "info": case "4": levels.Add(4); break;
                    case "verbose": case "5": levels.Add(5); break;
                    default: throw ToolException.Usage("--level 只支持 critical|error|warning|info|verbose：" + l);
                }
            }
            var ids = new List<long>();
            foreach (var s in ctx.GetAll("id"))
            {
                long v;
                if (!long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    throw ToolException.Usage("--id 需要数字：" + s);
                ids.Add(v);
            }
            var providers = ctx.GetAll("provider").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            Regex msgRe = SysProcs.Compile(ctx.Get("message-regex"), "--message-regex");
            string keyword = ctx.Get("keyword");

            var conds = new List<string>();
            if (levels.Count > 0) conds.Add("(" + string.Join(" or ", levels.Select(x => "Level=" + x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ")");
            if (ids.Count > 0) conds.Add("(" + string.Join(" or ", ids.Select(x => "EventID=" + x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ")");
            if (providers.Count > 0)
                conds.Add("(" + string.Join(" or ", providers.Select(p => "Provider[@Name='" + p.Replace("'", "&apos;") + "']").ToArray()) + ")");
            if (since.HasValue || until.HasValue)
            {
                var t = new StringBuilder("TimeCreated[");
                if (since.HasValue) t.Append("@SystemTime>='" + XPathTime(since.Value) + "'");
                if (since.HasValue && until.HasValue) t.Append(" and ");
                if (until.HasValue) t.Append("@SystemTime<='" + XPathTime(until.Value) + "'");
                t.Append(']');
                conds.Add(t.ToString());
            }
            string xpath = "*[System[" + string.Join(" and ", conds.ToArray()) + "]]";

            var items = new List<object>();
            var warnings = new List<string>();
            long scanned = 0;
            bool reverseFailed = false;

            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, xpath);
                query.TolerateQueryErrors = true;
                using (var reader = new EventLogReader(query))
                {
                    if (!oldest)
                    {
                        try { reader.Seek(SeekOrigin.End, 0); }
                        catch (Exception ex) { reverseFailed = true; warnings.Add("该日志不支持倒序读取，已改为正序截取：" + ex.Message); }
                    }

                    EventRecord evt;
                    long scanCap = reverseFailed ? Math.Max(200, max * 50L) : long.MaxValue;
                    while ((evt = reader.ReadEvent()) != null)
                    {
                        ctx.ThrowIfCancelled();
                        scanned++;
                        if (scanned > scanCap && !oldest) break;
                        using (evt)
                        {
                            string message = null;
                            if (withMessage)
                            {
                                try
                                {
                                    message = evt.FormatDescription();
                                    if (message != null && maxMsgLen > 0 && message.Length > maxMsgLen)
                                        message = message.Substring(0, maxMsgLen) + "...(截断)";
                                }
                                catch { message = null; }
                            }
                            if (keyword != null && (message == null || message.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                            if (msgRe != null && (message == null || !msgRe.IsMatch(message))) continue;

                            string userId = null;
                            try { userId = evt.UserId != null ? evt.UserId.Value : null; } catch { }
                            DateTime? tc = null;
                            try { tc = evt.TimeCreated; } catch { }

                            items.Add(Json.Obj(
                                "time", tc,
                                "id", evt.Id,
                                "level", evt.Level.HasValue ? (object)evt.Level.Value : null,
                                "levelName", SafeLevelName(evt),
                                "provider", evt.ProviderName,
                                "log", evt.LogName,
                                "machine", evt.MachineName,
                                "recordId", evt.RecordId,
                                "processId", evt.ProcessId,
                                "threadId", evt.ThreadId,
                                "task", evt.Task.HasValue ? (object)evt.Task.Value : null,
                                "opcode", evt.OpcodeDisplayName,
                                "userId", userId,
                                "message", message));
                        }
                        if (items.Count >= max) break;
                    }
                }
            }
            catch (EventLogNotFoundException ex) { throw ToolException.NotFound("事件日志不存在：" + logName + " —— " + ex.Message, "用 eventlog list 查看可用日志"); }
            catch (UnauthorizedAccessException ex) { throw ToolException.Denied("无权限读取日志 " + logName + "：" + ex.Message); }
            catch (Exception ex)
            {
                if (ex is ToolException) throw;
                throw ToolException.Denied("读取日志 " + logName + " 失败：" + ex.Message, "部分安全日志需要管理员权限");
            }

            ctx.Out.Result("eventlog.query", Json.Obj(
                "log", logName,
                "xpath", xpath,
                "items", items,
                "count", items.Count,
                "scanned", scanned,
                "max", max,
                "order", oldest ? "oldest-first" : "newest-first",
                "since", since,
                "until", until,
                "columns", new[] { "time", "levelName", "id", "provider", "message" }), warnings);
            return items.Count > 0 ? ExitCodes.Ok : ExitCodes.NotFound;
        }

        static string SafeLevelName(EventRecord evt)
        {
            try { return evt.LevelDisplayName; }
            catch
            {
                if (!evt.Level.HasValue) return null;
                switch (evt.Level.Value)
                {
                    case 1: return "Critical";
                    case 2: return "Error";
                    case 3: return "Warning";
                    case 4: return "Information";
                    case 5: return "Verbose";
                    default: return evt.Level.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        static string XPathTime(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        static int RunEventList(Ctx ctx)
        {
            var items = new List<object>();
            EventLog[] logs;
            try { logs = EventLog.GetEventLogs(); }
            catch (Exception ex) { throw ToolException.Denied("无法枚举事件日志：" + ex.Message); }
            foreach (var l in logs)
            {
                ctx.ThrowIfCancelled();
                int count = -1;
                long maxKb = -1;
                try { count = l.Entries.Count; } catch { }
                try { maxKb = l.MaximumKilobytes; } catch { }
                items.Add(Json.Obj(
                    "name", l.Log,
                    "displayName", l.LogDisplayName,
                    "machine", l.MachineName,
                    "entries", count,
                    "maxSizeKB", maxKb));
                try { l.Close(); } catch { }
            }
            items = items.OrderBy(o => Convert.ToString(((Dictionary<string, object>)o)["name"])).ToList();
            ctx.Out.Result("eventlog.list", Json.Obj(
                "items", items, "count", items.Count,
                "columns", new[] { "name", "entries", "maxSizeKB" }));
            return ExitCodes.Ok;
        }

        // ================================================================ installed
        static int RunInstalled(Ctx ctx)
        {
            Regex re = SysProcs.Compile(ctx.Get("match"), "--match");
            string publisherPat = ctx.Get("publisher");
            bool includeSystem = ctx.Flag("all");

            var items = new List<object>();
            ScanUninstall(Win32Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "hklm", items, includeSystem);
            ScanUninstall(Win32Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "hklm-wow6432", items, includeSystem);
            ScanUninstall(Win32Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "hkcu", items, includeSystem);

            var filtered = new List<object>();
            foreach (var o in items)
            {
                var d = (Dictionary<string, object>)o;
                string name = Convert.ToString(d["name"]);
                string pub = Convert.ToString(d["publisher"]);
                if (re != null && !re.IsMatch(name ?? "")) continue;
                if (!string.IsNullOrEmpty(publisherPat) && (pub == null || pub.IndexOf(publisherPat, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                filtered.Add(o);
            }

            string sort = (ctx.Get("sort") ?? "name").ToLowerInvariant();
            switch (sort)
            {
                case "name": filtered = filtered.OrderBy(o => Convert.ToString(((Dictionary<string, object>)o)["name"]), StringComparer.OrdinalIgnoreCase).ToList(); break;
                case "date": filtered = filtered.OrderByDescending(o => Convert.ToString(((Dictionary<string, object>)o)["installDate"]) ?? "").ToList(); break;
                case "size": filtered = filtered.OrderByDescending(o => Convert.ToInt64(((Dictionary<string, object>)o)["sizeBytes"] ?? 0L)).ToList(); break;
                default: throw ToolException.Usage("--sort 只支持 name|date|size");
            }

            ctx.Out.Result("installed.list", Json.Obj(
                "items", filtered,
                "count", filtered.Count,
                "total", items.Count,
                "sort", sort,
                "columns", new[] { "name", "version", "publisher", "installDate", "source" }));
            return ExitCodes.Ok;
        }

        static void ScanUninstall(Win32RegistryKey root, string sub, string source, List<object> items, bool includeSystem)
        {
            try
            {
                using (var key = root.OpenSubKey(sub))
                {
                    if (key == null) return;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        try
                        {
                            using (var sk = key.OpenSubKey(name))
                            {
                                if (sk == null) continue;
                                string display = sk.GetValue("DisplayName") as string;
                                if (string.IsNullOrWhiteSpace(display)) continue;
                                bool sysComp = false;
                                try { sysComp = Convert.ToInt32(sk.GetValue("SystemComponent") ?? 0, CultureInfo.InvariantCulture) == 1; } catch { }
                                if (sysComp && !includeSystem) continue;

                                long sizeKb = 0;
                                try { sizeKb = Convert.ToInt64(sk.GetValue("EstimatedSize") ?? 0L, CultureInfo.InvariantCulture); } catch { }

                                items.Add(Json.Obj(
                                    "name", display.Trim(),
                                    "version", (sk.GetValue("DisplayVersion") as string),
                                    "publisher", (sk.GetValue("Publisher") as string),
                                    "installDate", (sk.GetValue("InstallDate") as string),
                                    "installLocation", (sk.GetValue("InstallLocation") as string),
                                    "uninstallString", (sk.GetValue("UninstallString") as string),
                                    "sizeBytes", sizeKb * 1024,
                                    "size", sizeKb > 0 ? Fs.FormatSize(sizeKb * 1024) : null,
                                    "systemComponent", sysComp,
                                    "source", source,
                                    "key", name));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception) { }
        }

        // ================================================================ defender
        /// <summary>
        /// 注册表"读不到"探究：OpenSubKey 在无权访问时返回 null（不抛异常），
        /// 所以必须区分「键确实不存在」与「键存在但无权读」，否则会输出误导性的"空"。
        /// </summary>
        static void ProbeKey(string path, out bool opened, out bool exists, out string reason, out string[] valueNames)
        {
            opened = false; exists = false; reason = null; valueNames = new string[0];
            try
            {
                using (var k = Win32Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k != null)
                    {
                        opened = true; exists = true;
                        valueNames = k.GetValueNames();
                        return;
                    }
                }
            }
            catch (UnauthorizedAccessException ex) { reason = "拒绝访问：" + ex.Message; }
            catch (Exception ex) { reason = ex.Message; }

            // 打开失败：用父键的子键名判断是"不存在"还是"不可读"
            int slash = path.LastIndexOf('\\');
            if (slash > 0)
            {
                string parent = path.Substring(0, slash);
                string leaf = path.Substring(slash + 1);
                try
                {
                    using (var pk = Win32Registry.LocalMachine.OpenSubKey(parent))
                    {
                        if (pk != null)
                        {
                            foreach (var n in pk.GetSubKeyNames())
                                if (string.Equals(n, leaf, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                            if (exists)
                                reason = "HKLM\\" + path + " 存在但当前用户无权读取（Windows 对 Defender 配置的保护；需要管理员）";
                            else
                                reason = "HKLM\\" + path + " 不存在（确实没有配置）";
                            return;
                        }
                    }
                    reason = "HKLM\\" + path + " 与上级键 " + parent + " 均不可读（需要管理员）";
                }
                catch (Exception ex)
                {
                    reason = "HKLM\\" + path + " 不可读（需要管理员）：" + ex.Message;
                }
            }
        }

        static int RunDefender(Ctx ctx)
        {
            var notes = new List<string>();
            var sources = new List<object>();
            var avProducts = new List<object>();

            bool sc2Ok = false;
            string sc2Error = null;
            try
            {
                var scope = new ManagementScope(@"\\.\root\SecurityCenter2");
                scope.Connect();
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM AntiVirusProduct")))
                using (var r = s.Get())
                    foreach (ManagementBaseObject mo in r)
                    {
                        try
                        {
                            int state = 0;
                            try { state = Convert.ToInt32(mo["productState"] ?? 0, CultureInfo.InvariantCulture); } catch { }
                            int mid = (state >> 8) & 0xFF;
                            int top = (state >> 16) & 0xFF;
                            avProducts.Add(Json.Obj(
                                "displayName", Convert.ToString(mo["displayName"], CultureInfo.InvariantCulture),
                                "instanceGuid", Convert.ToString(mo["instanceGuid"], CultureInfo.InvariantCulture),
                                "pathToSignedProductExe", Convert.ToString(mo["pathToSignedProductExe"], CultureInfo.InvariantCulture),
                                "productState", "0x" + state.ToString("X6", CultureInfo.InvariantCulture),
                                "realTimeProtectionEnabled", (mid & 0x10) != 0,
                                "definitionsUpToDate", top == 0x00,
                                "stateDecodeNote", "productState 按 SecurityCenter2 常见位解释，仅作参考"));
                        }
                        catch { }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                sc2Ok = true;
            }
            catch (Exception ex) { sc2Error = ex.Message; }
            sources.Add(Json.Obj("source", "WMI root\\SecurityCenter2 AntiVirusProduct", "ok", sc2Ok, "error", sc2Error));
            if (sc2Error != null) notes.Add("SecurityCenter2 不可用：" + sc2Error);

            string serviceStatus = null;
            string serviceError = null;
            try
            {
                using (var sc = new ServiceController("WinDefend"))
                    serviceStatus = sc.Status.ToString();
            }
            catch (Exception ex) { serviceError = ex.Message; notes.Add("WinDefend 服务不可用：" + ex.Message); }
            sources.Add(Json.Obj("source", "ServiceController WinDefend", "ok", serviceStatus != null, "error", serviceError));

            // ---- HKLM\SOFTWARE\Microsoft\Windows Defender
            var reg = new Dictionary<string, object>(StringComparer.Ordinal);
            bool regOpened, regExists;
            string regReason;
            string[] regNames;
            ProbeKey(@"SOFTWARE\Microsoft\Windows Defender", out regOpened, out regExists, out regReason, out regNames);
            reg["readable"] = regOpened;
            reg["exists"] = regExists;
            reg["reason"] = regOpened ? null : regReason;
            if (regOpened)
            {
                try
                {
                    using (var k = Win32Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender"))
                        foreach (var n in new[] { "DisableAntiSpyware", "DisableAntiVirus", "DisableRoutinelyTakingAction", "PassiveMode", "ServiceStartup" })
                        {
                            object v = null;
                            try { v = k.GetValue(n); } catch { }
                            if (v != null) reg[n] = v;
                        }
                }
                catch (Exception ex) { reg["reason"] = "读取值失败：" + ex.Message; }
            }
            sources.Add(Json.Obj("source", @"HKLM\SOFTWARE\Microsoft\Windows Defender", "ok", regOpened, "error", regOpened ? null : regReason));

            // ---- 策略键
            var policy = new Dictionary<string, object>(StringComparer.Ordinal);
            var policyReasons = new List<object>();
            foreach (var path in new[]
            {
                @"SOFTWARE\Policies\Microsoft\Windows Defender",
                @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection"
            })
            {
                bool opened, exists; string reason; string[] names;
                ProbeKey(path, out opened, out exists, out reason, out names);
                sources.Add(Json.Obj("source", "HKLM\\" + path, "ok", opened, "exists", exists,
                                     "error", opened ? null : reason));
                if (opened)
                {
                    try
                    {
                        using (var k = Win32Registry.LocalMachine.OpenSubKey(path))
                            foreach (var n in k.GetValueNames())
                                try { policy[path.Substring(path.LastIndexOf('\\') + 1) + "." + n] = k.GetValue(n); } catch { }
                    }
                    catch { }
                }
                else if (exists) policyReasons.Add(Json.Obj("path", "HKLM\\" + path, "reason", reason));
            }

            // ---- 排除项：区分「确实没有」与「读不到」
            var exclusions = new Dictionary<string, object>(StringComparer.Ordinal);
            var unreadableSources = new List<object>();
            foreach (var sub in new[] { "Paths", "Extensions", "Processes", "IpAddresses" })
            {
                var vals = new List<string>();
                bool anyReadable = false;
                var attempts = new List<object>();
                foreach (var basePath in new[]
                {
                    @"SOFTWARE\Microsoft\Windows Defender\Exclusions\",
                    @"SOFTWARE\Policies\Microsoft\Windows Defender\Exclusions\"
                })
                {
                    string full = basePath + sub;
                    bool opened, exists; string reason; string[] names;
                    ProbeKey(full, out opened, out exists, out reason, out names);
                    attempts.Add(Json.Obj("source", "HKLM\\" + full, "readable", opened, "exists", exists,
                                          "count", opened ? names.Length : 0, "error", opened ? null : reason));
                    if (opened) { anyReadable = true; vals.AddRange(names); }
                    else if (exists) unreadableSources.Add(Json.Obj("path", "HKLM\\" + full, "reason", reason));
                }
                var distinct = vals.Distinct().ToList();
                string reason2 = null;
                if (!anyReadable)
                    reason2 = "两处来源都不可读或不存在；若某处存在即代表读不到（需要管理员）——" +
                              "当前结果为空不能证明没有排除项";
                exclusions[sub] = Json.Obj(
                    "items", distinct,
                    "count", distinct.Count,
                    "readable", anyReadable,
                    "reason", anyReadable ? null : reason2,
                    "sources", attempts);
            }

            // ---- 签名版本
            var signatures = new Dictionary<string, object>(StringComparer.Ordinal);
            bool sigOpened, sigExists; string sigReason; string[] sigNames;
            ProbeKey(@"SOFTWARE\Microsoft\Windows Defender\Signature Updates", out sigOpened, out sigExists, out sigReason, out sigNames);
            if (sigOpened)
            {
                try
                {
                    using (var k = Win32Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Signature Updates"))
                        foreach (var n in new[] { "AVSignatureVersion", "ASSignatureVersion", "NISSignatureVersion", "SignaturesLastUpdated" })
                        {
                            object v = null;
                            try { v = k.GetValue(n); } catch { }
                            if (v != null) signatures[n] = v;
                        }
                }
                catch { }
            }
            signatures["readable"] = sigOpened;
            signatures["reason"] = sigOpened ? null : sigReason;
            sources.Add(Json.Obj("source", @"HKLM\SOFTWARE\Microsoft\Windows Defender\Signature Updates",
                                 "ok", sigOpened, "error", sigOpened ? null : sigReason));

            bool rtKnown = avProducts.Count > 0;
            bool? realtime = rtKnown
                ? (bool?)avProducts.OfType<Dictionary<string, object>>().Any(o => Convert.ToBoolean(o["realTimeProtectionEnabled"]))
                : null;
            bool exclusionsReadable = exclusions.Values.OfType<Dictionary<string, object>>().Any(o => Convert.ToBoolean(o["readable"]));

            ctx.Out.Result("defender.status", Json.Obj(
                "readable", rtKnown || regOpened || exclusionsReadable,
                "serviceStatus", serviceStatus,
                "realTimeProtection", realtime,
                "realTimeProtectionSource", rtKnown ? "WMI SecurityCenter2 AntiVirusProduct.productState" : null,
                "realtimeEnabled", realtime,
                "avProducts", avProducts,
                "avProductCount", avProducts.Count,
                "registry", reg,
                "policy", policy,
                "policyUnreadable", policyReasons,
                "exclusions", exclusions,
                "exclusionsReadable", exclusionsReadable,
                "exclusionsNote", exclusionsReadable
                    ? null
                    : "排除项读不到（普通用户对 HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Exclusions 无读权限）；空列表 ≠ 没有排除项，请以 exclusions.*.readable 为准",
                "unreadableSources", unreadableSources,
                "signatures", signatures,
                "sources", sources,
                "notes", notes,
                "columns", new[] { "source", "ok", "error" }));
            return ExitCodes.Ok;
        }

        // ================================================================ startup
        static int RunStartup(Ctx ctx)
        {
            bool all = ctx.Flag("all");
            var items = new List<object>();

            var runKeys = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "hklm-run"),
                new KeyValuePair<string, string>(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "hklm-runonce"),
                new KeyValuePair<string, string>(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "hklm-wow-run"),
                new KeyValuePair<string, string>(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "hklm-wow-runonce")
            };
            foreach (var kv in runKeys) ReadRunKey(Win32Registry.LocalMachine, kv.Key, kv.Value, items);

            ReadRunKey(Win32Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "hkcu-run", items);
            ReadRunKey(Win32Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "hkcu-runonce", items);

            // 启动文件夹
            foreach (var pair in new[]
            {
                new KeyValuePair<Environment.SpecialFolder, string>(Environment.SpecialFolder.Startup, "startup-user"),
                new KeyValuePair<Environment.SpecialFolder, string>(Environment.SpecialFolder.CommonStartup, "startup-common")
            })
            {
                try
                {
                    string dir = Environment.GetFolderPath(pair.Key);
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var fi = new FileInfo(f);
                        if (!all && fi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        items.Add(Json.Obj(
                            "source", pair.Value,
                            "name", Path.GetFileNameWithoutExtension(fi.Name),
                            "command", fi.FullName,
                            "target", ShortcutTarget(fi.FullName),
                            "enabled", ApprovedState(fi.FullName),
                            "kind", fi.Extension.ToLowerInvariant() == ".lnk" ? "shortcut" : "file"));
                    }
                }
                catch { }
            }

            ctx.Out.Result("startup.list", Json.Obj(
                "items", items,
                "count", items.Count,
                "columns", new[] { "source", "name", "command", "enabled" }));
            return ExitCodes.Ok;
        }

        static void ReadRunKey(Win32RegistryKey root, string sub, string source, List<object> items)
        {
            try
            {
                using (var k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    foreach (var n in k.GetValueNames())
                    {
                        string cmd = null;
                        try { cmd = Convert.ToString(k.GetValue(n), CultureInfo.InvariantCulture); } catch { }
                        items.Add(Json.Obj(
                            "source", source,
                            "name", n,
                            "command", cmd,
                            "target", null,
                            "enabled", "unknown",
                            "kind", "registry"));
                    }
                }
            }
            catch (Exception) { }
        }

        static string ShortcutTarget(string path)
        {
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
            object sh = null, lnk = null;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                sh = Activator.CreateInstance(t);
                lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { path });
                if (lnk == null) return null;
                object tp = lnk.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null);
                return tp as string;
            }
            catch { return null; }
            finally
            {
                if (lnk != null) { try { Marshal.ReleaseComObject(lnk); } catch { } }
                if (sh != null) { try { Marshal.ReleaseComObject(sh); } catch { } }
            }
        }

        static string ApprovedState(string path)
        {
            // StartupApproved 里第 0 字节 0x02=启用、0x03=禁用（按文件名匹配）
            try
            {
                string name = Path.GetFileName(path);
                foreach (var sub in new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
                })
                {
                    using (var k = Win32Registry.CurrentUser.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        var v = k.GetValue(name) as byte[];
                        if (v != null && v.Length > 0) return v[0] == 0x02 ? "enabled" : "disabled";
                        var v2 = k.GetValue(Path.GetFileNameWithoutExtension(path)) as byte[];
                        if (v2 != null && v2.Length > 0) return v2[0] == 0x02 ? "enabled" : "disabled";
                    }
                }
            }
            catch { }
            return "unknown";
        }
    }
}
