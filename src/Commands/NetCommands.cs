using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    // ==================================================================== 端口表（原生 API）
    internal sealed class SysPortRec
    {
        public string Proto;         // TCP / TCPv6 / UDP / UDPv6
        public string LocalAddr;
        public int LocalPort;
        public string RemoteAddr;
        public int RemotePort;
        public string State;         // LISTEN / ESTABLISHED / "" (UDP)
        public int Pid;
        public int ScopeId;

        public string Endpoint
        {
            get { return LocalAddr + ":" + LocalPort.ToString(CultureInfo.InvariantCulture); }
        }
    }

    /// <summary>
    /// 端口 -> 进程 归属。使用 iphlpapi 的 GetExtendedTcpTable / GetExtendedUdpTable（等价 Get-NetTCPConnection），
    /// 不依赖 netstat/PowerShell 进程解析。
    /// </summary>
    internal static class SysPorts
    {
        const int AF_INET = 2;
        const int AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_ALL = 5;
        const int UDP_TABLE_OWNER_PID = 1;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

        static readonly Dictionary<int, string> NameCache = new Dictionary<int, string>();
        static readonly Dictionary<int, string> NameErrorCache = new Dictionary<int, string>();

        /// <summary>
        /// 取进程映像名。**读不到时返回 null 并给出原因**（而不是留一个看起来像"没有进程"的空串）：
        /// 受保护/其它会话进程、或在端口表读取之后已退出的 PID，都可能拿不到名字。
        /// </summary>
        public static string ProcName(int pid, out string error)
        {
            error = null;
            lock (NameCache)
            {
                string n;
                if (NameCache.TryGetValue(pid, out n)) { NameErrorCache.TryGetValue(pid, out error); return n; }
                n = null;
                if (pid <= 0)
                {
                    error = "PID 为 0（System Idle / 内核占位），没有对应进程映像";
                }
                else
                {
                    try { using (var p = Process.GetProcessById(pid)) n = p.ProcessName; }
                    catch (ArgumentException) { error = "端口表读取后该 PID 已退出，无法取到映像名"; }
                    catch (InvalidOperationException) { error = "进程已结束，无法取到映像名"; }
                    catch (Exception ex) { error = "无法读取进程名（权限不足或受保护进程）：" + ex.Message; }
                }
                NameCache[pid] = n;
                NameErrorCache[pid] = error;
                return n;
            }
        }

        public static string ProcName(int pid)
        {
            string err;
            return ProcName(pid, out err);
        }

        /// <summary>MIB_*ROW 里端口是网络字节序，需交换低 16 位的两个字节。</summary>
        static int NetPort(int dw) { return ((dw & 0xFF) << 8) | ((dw >> 8) & 0xFF); }

        static string V4(int dw)
        {
            return new IPAddress(BitConverter.GetBytes(dw)).ToString();
        }

        static string V6(IntPtr p, int scopeId)
        {
            var b = new byte[16];
            Marshal.Copy(p, b, 0, 16);
            var ip = new IPAddress(b);
            if (scopeId != 0)
            {
                try { ip.ScopeId = scopeId; } catch { }
            }
            return ip.ToString();
        }

        static IntPtr Fetch(bool tcp, int af, int cls, out int count)
        {
            count = 0;
            int size = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                int len = size;
                uint err = tcp
                    ? GetExtendedTcpTable(buf, ref len, false, af, cls, 0)
                    : GetExtendedUdpTable(buf, ref len, false, af, cls, 0);
                if (err == 0)
                {
                    count = Marshal.ReadInt32(buf);
                    return buf;
                }
                Marshal.FreeHGlobal(buf);
                if (err != ERROR_INSUFFICIENT_BUFFER) return IntPtr.Zero;
                size = len + 4096;
            }
            return IntPtr.Zero;
        }

        static string TcpState(int s)
        {
            switch (s)
            {
                case 1: return "CLOSED";
                case 2: return "LISTEN";
                case 3: return "SYN_SENT";
                case 4: return "SYN_RCVD";
                case 5: return "ESTABLISHED";
                case 6: return "FIN_WAIT1";
                case 7: return "FIN_WAIT2";
                case 8: return "CLOSE_WAIT";
                case 9: return "CLOSING";
                case 10: return "LAST_ACK";
                case 11: return "TIME_WAIT";
                case 12: return "DELETE_TCB";
                default: return "STATE_" + s.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static List<SysPortRec> Snapshot(bool includeUdp)
        {
            var list = new List<SysPortRec>();
            foreach (var af in new[] { AF_INET, AF_INET6 })
            {
                int n;
                IntPtr buf = Fetch(true, af, TCP_TABLE_OWNER_PID_ALL, out n);
                if (buf != IntPtr.Zero)
                {
                    try
                    {
                        int rowSize = af == AF_INET ? 24 : 56;
                        IntPtr row = (IntPtr)((long)buf + 4);
                        for (int i = 0; i < n; i++, row = (IntPtr)((long)row + rowSize))
                        {
                            var r = new SysPortRec { Proto = af == AF_INET ? "TCP" : "TCPv6" };
                            if (af == AF_INET)
                            {
                                r.State = TcpState(Marshal.ReadInt32(row, 0));
                                r.LocalAddr = V4(Marshal.ReadInt32(row, 4));
                                r.LocalPort = NetPort(Marshal.ReadInt32(row, 8));
                                r.RemoteAddr = V4(Marshal.ReadInt32(row, 12));
                                r.RemotePort = NetPort(Marshal.ReadInt32(row, 16));
                                r.Pid = Marshal.ReadInt32(row, 20);
                            }
                            else
                            {
                                r.LocalAddr = V6(row, Marshal.ReadInt32(row, 16));
                                r.LocalPort = NetPort(Marshal.ReadInt32(row, 20));
                                r.RemoteAddr = V6((IntPtr)((long)row + 24), Marshal.ReadInt32(row, 40));
                                r.RemotePort = NetPort(Marshal.ReadInt32(row, 44));
                                r.State = TcpState(Marshal.ReadInt32(row, 48));
                                r.Pid = Marshal.ReadInt32(row, 52);
                            }
                            list.Add(r);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }

                if (!includeUdp) continue;
                buf = Fetch(false, af, UDP_TABLE_OWNER_PID, out n);
                if (buf != IntPtr.Zero)
                {
                    try
                    {
                        int rowSize = af == AF_INET ? 12 : 28;
                        IntPtr row = (IntPtr)((long)buf + 4);
                        for (int i = 0; i < n; i++, row = (IntPtr)((long)row + rowSize))
                        {
                            var r = new SysPortRec { Proto = af == AF_INET ? "UDP" : "UDPv6", State = "UNCONN", RemoteAddr = "*", RemotePort = 0 };
                            if (af == AF_INET)
                            {
                                r.LocalAddr = V4(Marshal.ReadInt32(row, 0));
                                r.LocalPort = NetPort(Marshal.ReadInt32(row, 4));
                                r.Pid = Marshal.ReadInt32(row, 8);
                            }
                            else
                            {
                                r.LocalAddr = V6(row, Marshal.ReadInt32(row, 16));
                                r.LocalPort = NetPort(Marshal.ReadInt32(row, 20));
                                r.Pid = Marshal.ReadInt32(row, 24);
                            }
                            list.Add(r);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            return list;
        }

        public static List<SysPortRec> OnLocalPort(int port)
        {
            return Snapshot(true).Where(r => r.LocalPort == port).ToList();
        }

        public static HashSet<int> PidsOnLocalPort(int port)
        {
            var set = new HashSet<int>();
            foreach (var r in OnLocalPort(port)) if (r.Pid > 0) set.Add(r.Pid);
            return set;
        }

        public static string[] ProcessNames(IEnumerable<SysPortRec> recs)
        {
            return recs.Select(r => ProcName(r.Pid)).Where(x => x != null).Distinct().ToArray();
        }
    }

    // ==================================================================== net.*
    /// <summary>网络类命令（sysdev 拥有）：net.ports / tcp / http / dns / ip / download。</summary>
    public static class NetCommands
    {
        public static void Register()
        {
            Registry.Add("net.ports", "监听端口列表（TCP LISTEN + UDP，本地地址/端口/PID/进程名）",
                "net ports [--all] [--state <s>] [--port <n>] [--pid <n>] [--proto tcp|udp] [--sort port|pid|name]",
                RunPorts,
                aliases: new[] { "ports" },
                examples: new[] { "dsh-toolbox net ports --json", "dsh-toolbox net ports --all --pid 9096" });

            Registry.Add("net.tcp", "TCP 连通性测试：解析 + 逐 IP 连接，返回连接耗时毫秒",
                "net tcp --host <h> --port <n> [--timeout <dur>] [--all-ips]",
                RunTcp,
                examples: new[] { "dsh-toolbox net tcp --host 127.0.0.1 --port 19387 --json", "dsh-toolbox net tcp --host example.com --port 443 --timeout 5s" });

            Registry.Add("net.http", "HTTP 探测：状态码/耗时/头部/证书主体与有效期（HttpWebRequest）",
                "net http --url <u> [--method <m>] [--header \"K: V\"]... [--body <s>|--body-file <f>] [--timeout <dur>] [--max-body <n>] [--insecure] [--no-redirect]",
                RunHttp,
                examples: new[] { "dsh-toolbox net http --url https://example.com --json", "dsh-toolbox net http --url https://api.github.com --header \"Accept: application/json\" --method GET" });

            Registry.Add("net.dns", "DNS 解析：A/AAAA/CNAME 记录 + 耗时（DnsQuery + System.Net.Dns 对照）",
                "net dns --host <h> [--type A|AAAA|CNAME]... [--server <ip>]",
                RunDns,
                aliases: new[] { "dns" },
                examples: new[] { "dsh-toolbox net dns --host github.com --json", "dsh-toolbox net dns --host localhost --type A" });

            Registry.Add("net.ip", "本机网卡与地址（IPv4/IPv6/网关/DNS/MAC/速率）",
                "net ip [--all] [--json]",
                RunIp,
                examples: new[] { "dsh-toolbox net ip --json" });

            Registry.Add("net.download", "下载到文件：断点续传 + 速率 + 哈希校验",
                "net download --url <u> --out <file> [--resume] [--overwrite] [--hash sha256] [--expected <hex|base64>] [--timeout <dur>]",
                RunDownload,
                examples: new[] { "dsh-toolbox net download --url https://example.com/f.bin --out f.bin --json" });
        }

        // ---------------------------------------------------------------- ports
        static int RunPorts(Ctx ctx)
        {
            bool all = ctx.Flag("all");
            string state = ctx.Get("state");
            string proto = ctx.Get("proto");
            int pidFilter = ctx.Args.GetInt("pid", 0);
            string portArg = ctx.Get("port");
            int portFilter = 0;
            if (!string.IsNullOrEmpty(portArg) &&
                !int.TryParse(portArg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out portFilter))
                throw ToolException.Usage("--port 需要端口号");

            var recs = SysPorts.Snapshot(true);
            IEnumerable<SysPortRec> q = recs;
            if (!all) q = q.Where(r => r.State == "LISTEN" || r.Proto.StartsWith("UDP", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(state)) q = q.Where(r => string.Equals(r.State, state, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(proto)) q = q.Where(r => r.Proto.StartsWith(proto, StringComparison.OrdinalIgnoreCase));
            if (pidFilter > 0) q = q.Where(r => r.Pid == pidFilter);
            if (portFilter > 0) q = q.Where(r => r.LocalPort == portFilter);

            string sort = (ctx.Get("sort") ?? "port").ToLowerInvariant();
            switch (sort)
            {
                case "port": q = q.OrderBy(r => r.LocalPort).ThenBy(r => r.Proto); break;
                case "pid": q = q.OrderBy(r => r.Pid).ThenBy(r => r.LocalPort); break;
                case "name": q = q.OrderBy(r => SysPorts.ProcessNames(new[] { r }).FirstOrDefault() ?? "").ThenBy(r => r.LocalPort); break;
                default: throw ToolException.Usage("--sort 只支持 port|pid|name");
            }
            var list = q.ToList();

            var items = new List<object>();
            foreach (var r in list)
            {
                string err;
                string pname = SysPorts.ProcName(r.Pid, out err);
                items.Add(Json.Obj(
                    "proto", r.Proto,
                    "localAddr", r.LocalAddr,
                    "localPort", r.LocalPort,
                    "remoteAddr", r.RemoteAddr,
                    "remotePort", r.RemotePort,
                    "state", r.State,
                    "pid", r.Pid,
                    "process", pname,
                    "processName", pname,
                    "processNameUnavailable", pname == null,
                    "processNameReason", pname == null ? (err ?? "无法读取进程名") : null));
            }

            ctx.Out.Result("net.ports", Json.Obj(
                "items", items,
                "count", items.Count,
                "total", recs.Count,
                "listening", list.Count(r => r.State == "LISTEN"),
                "udp", list.Count(r => r.Proto.StartsWith("UDP", StringComparison.OrdinalIgnoreCase)),
                "all", all,
                "columns", new[] { "proto", "localAddr", "localPort", "state", "pid", "process" }));
            return ExitCodes.Ok;
        }

        // ---------------------------------------------------------------- tcp
        static int RunTcp(Ctx ctx)
        {
            string host = ctx.Get("host");
            if (string.IsNullOrEmpty(host) && ctx.Args.Positional.Count > 0) host = ctx.Args.Positional[0];
            if (string.IsNullOrEmpty(host)) throw ToolException.Usage("缺少 --host", "例：net tcp --host 127.0.0.1 --port 19387");

            string portArg = ctx.Get("port");
            if (string.IsNullOrEmpty(portArg) && ctx.Args.Positional.Count > 1) portArg = ctx.Args.Positional[1];
            if (string.IsNullOrEmpty(portArg)) throw ToolException.Usage("缺少 --port", "例：net tcp --host 127.0.0.1 --port 19387");
            int port;
            if (!int.TryParse(portArg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw ToolException.Usage("端口非法：" + portArg);

            TimeSpan timeout = ctx.Args.GetSpan("timeout", TimeSpan.FromSeconds(5));
            int timeoutMs = (int)Math.Max(1, Math.Min(timeout.TotalMilliseconds, int.MaxValue));

            var dnsWatch = Stopwatch.StartNew();
            IPAddress[] ips;
            try { ips = Dns.GetHostAddresses(host); }
            catch (Exception ex) { throw ToolException.NotFound("DNS 解析失败：" + host + " —— " + ex.Message); }
            dnsWatch.Stop();

            bool allIps = ctx.Flag("all-ips");
            var items = new List<object>();
            int connected = 0;
            long firstConnectMs = -1;

            foreach (var ip in ips)
            {
                ctx.ThrowIfCancelled();
                var sw = Stopwatch.StartNew();
                bool ok = false;
                string error = null;
                TcpClient client = null;
                try
                {
                    client = new TcpClient(ip.AddressFamily);
                    var ar = client.BeginConnect(ip, port, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        client.EndConnect(ar);
                        ok = true;
                    }
                    else
                    {
                        error = "超时（" + timeoutMs.ToString(CultureInfo.InvariantCulture) + "ms）";
                    }
                }
                catch (SocketException se) { error = se.SocketErrorCode + ": " + se.Message; }
                catch (Exception ex) { error = ex.Message; }
                finally
                {
                    if (client != null) { try { client.Close(); } catch { } }
                }
                sw.Stop();
                if (ok)
                {
                    connected++;
                    if (firstConnectMs < 0) firstConnectMs = sw.ElapsedMilliseconds;
                }
                items.Add(Json.Obj(
                    "ip", ip.ToString(),
                    "family", ip.AddressFamily.ToString(),
                    "port", port,
                    "connected", ok,
                    "elapsedMs", sw.ElapsedMilliseconds,
                    "error", error));
                if (ok && !allIps) break;
            }

            ctx.Out.Result("net.tcp", Json.Obj(
                "host", host,
                "port", port,
                "connected", connected > 0,
                "connectMs", firstConnectMs,
                "dnsMs", dnsWatch.ElapsedMilliseconds,
                "resolved", ips.Select(x => (object)x.ToString()).ToList(),
                "attempts", items.Count,
                "items", items,
                "count", items.Count,
                "timeoutMs", timeoutMs,
                "columns", new[] { "ip", "port", "connected", "elapsedMs", "error" }));
            return connected > 0 ? ExitCodes.Ok : ExitCodes.Error;
        }

        // ---------------------------------------------------------------- http
        static int RunHttp(Ctx ctx)
        {
            string url = ctx.Get("url");
            if (string.IsNullOrEmpty(url) && ctx.Args.Positional.Count > 0) url = ctx.Args.Positional[0];
            if (string.IsNullOrEmpty(url)) throw ToolException.Usage("缺少 --url", "例：net http --url https://example.com");

            string method = (ctx.Get("method") ?? "GET").ToUpperInvariant();
            TimeSpan timeout = ctx.Args.GetSpan("timeout", TimeSpan.FromSeconds(20));
            int timeoutMs = (int)Math.Max(1, Math.Min(timeout.TotalMilliseconds, int.MaxValue));
            bool insecure = ctx.Flag("insecure");
            long maxBody = ctx.Args.GetLong("max-body", 0);

            string body = ctx.Get("body");
            string bodyFile = ctx.Get("body-file");
            if (!string.IsNullOrEmpty(bodyFile))
            {
                if (!File.Exists(bodyFile)) throw ToolException.NotFound("--body-file 不存在：" + bodyFile);
                body = File.ReadAllText(bodyFile);
            }

            string certSubject = null, certIssuer = null, certThumb = null, certSerial = null;
            DateTime? certFrom = null, certTo = null;
            string sslErrors = null;

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            var warnings = new List<string>();
            var sw = Stopwatch.StartNew();
            HttpWebResponse resp = null;
            string failReason = null;
            HttpStatusCode? code = null;
            string statusDesc = null;
            Uri finalUri = null;

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = !ctx.Flag("no-redirect");
                req.UserAgent = "dsh-toolbox/" + ToolInfo.Version;
                req.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    try
                    {
                        var c2 = cert as X509Certificate2 ?? (cert != null ? new X509Certificate2(cert) : null);
                        if (c2 != null)
                        {
                            certSubject = c2.Subject;
                            certIssuer = c2.Issuer;
                            certThumb = c2.Thumbprint;
                            certSerial = c2.SerialNumber;
                            try { certFrom = c2.NotBefore; } catch { }
                            try { certTo = c2.NotAfter; } catch { }
                        }
                    }
                    catch { }
                    sslErrors = errors.ToString();
                    return errors == SslPolicyErrors.None || insecure;
                };

                foreach (var h in ctx.GetAll("header"))
                {
                    int colon = h.IndexOf(':');
                    if (colon <= 0) { warnings.Add("忽略非法 --header：" + h); continue; }
                    string hn = h.Substring(0, colon).Trim();
                    string hv = h.Substring(colon + 1).Trim();
                    try { ApplyHeader(req, hn, hv); }
                    catch (Exception ex) { warnings.Add("无法设置请求头 " + hn + "：" + ex.Message); }
                }

                if (!string.IsNullOrEmpty(body) && method != "GET" && method != "HEAD")
                {
                    var payload = Encoding.UTF8.GetBytes(body);
                    req.ContentLength = payload.Length;
                    if (string.IsNullOrEmpty(req.ContentType)) req.ContentType = "application/json; charset=utf-8";
                    using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
                }

                long headersMs;
                try
                {
                    resp = (HttpWebResponse)req.GetResponse();
                }
                catch (WebException we)
                {
                    resp = we.Response as HttpWebResponse;
                    if (resp == null) failReason = we.Status + ": " + we.Message;
                    else warnings.Add("HTTP 非 2xx：" + we.Message);
                }
                headersMs = sw.ElapsedMilliseconds;

                long bytesRead = 0;
                string bodyHash = null;
                if (resp != null)
                {
                    code = resp.StatusCode;
                    statusDesc = resp.StatusDescription;
                    finalUri = resp.ResponseUri;
                    if (maxBody > 0 || ctx.Flag("show-body"))
                    {
                        long cap = maxBody > 0 ? maxBody : 65536;
                        using (var sha = SHA256.Create())
                        using (var rs = resp.GetResponseStream())
                        {
                            var buf = new byte[65536];
                            int n;
                            while (bytesRead < cap && (n = rs.Read(buf, 0, (int)Math.Min(buf.Length, cap - bytesRead))) > 0)
                            {
                                ctx.ThrowIfCancelled();
                                sha.TransformBlock(buf, 0, n, buf, 0);
                                bytesRead += n;
                            }
                            sha.TransformFinalBlock(buf, 0, 0);
                            if (resp.ContentLength >= 0 && bytesRead >= resp.ContentLength)
                                bodyHash = Fs.ToHex(sha.Hash);
                        }
                    }
                }
                sw.Stop();

                var headers = new List<object>();
                if (resp != null)
                    foreach (string k in resp.Headers.AllKeys)
                        headers.Add(Json.Obj("name", k, "value", resp.Headers[k]));

                var data = Json.Obj(
                    "url", url,
                    "finalUrl", finalUri != null ? finalUri.AbsoluteUri : null,
                    "method", method,
                    "statusCode", code.HasValue ? (object)(int)code.Value : null,
                    "statusText", statusDesc,
                    "ok", code.HasValue && (int)code.Value >= 200 && (int)code.Value < 400,
                    "headersMs", headersMs,
                    "totalMs", sw.ElapsedMilliseconds,
                    "server", resp != null ? resp.Headers["Server"] : null,
                    "contentType", resp != null ? resp.ContentType : null,
                    "contentLength", resp != null ? resp.ContentLength : -1,
                    "bytesRead", bytesRead,
                    "bodySha256", bodyHash,
                    "headers", headers,
                    "cert", Json.Obj(
                        "subject", certSubject,
                        "issuer", certIssuer,
                        "thumbprint", certThumb,
                        "serial", certSerial,
                        "notBefore", certFrom,
                        "notAfter", certTo,
                        "sslPolicyErrors", sslErrors),
                    "error", failReason,
                    "insecure", insecure,
                    "columns", new[] { "name", "value" });

                ctx.Out.Result("net.http", data, warnings);
                if (failReason != null)
                    return failReason.StartsWith("Timeout", StringComparison.OrdinalIgnoreCase) ? ExitCodes.Timeout : ExitCodes.Error;
                return ExitCodes.Ok;
            }
            finally
            {
                if (resp != null) { try { resp.Close(); } catch { } }
            }
        }

        static void ApplyHeader(HttpWebRequest req, string name, string value)
        {
            switch (name.ToLowerInvariant())
            {
                case "host": req.Host = value; break;
                case "content-type": req.ContentType = value; break;
                case "user-agent": req.UserAgent = value; break;
                case "accept": req.Accept = value; break;
                case "referer": case "referrer": req.Referer = value; break;
                case "connection": req.Connection = value; break;
                case "expect": req.Expect = value; break;
                case "date": req.Date = DateTime.Parse(value, CultureInfo.InvariantCulture); break;
                case "if-modified-since": req.IfModifiedSince = DateTime.Parse(value, CultureInfo.InvariantCulture); break;
                default: req.Headers[name] = value; break;
            }
        }

        // ---------------------------------------------------------------- dns
        const ushort DNS_TYPE_A = 1;
        const ushort DNS_TYPE_CNAME = 5;
        const ushort DNS_TYPE_AAAA = 28;

        [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_A", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern int DnsQuery_A(string pszName, ushort wType, uint options, IntPtr pExtra, ref IntPtr ppQueryResults, IntPtr pReserved);

        [DllImport("dnsapi.dll")]
        static extern void DnsRecordListFree(IntPtr pRecordList, int freeType);

        static int RunDns(Ctx ctx)
        {
            string host = ctx.Get("host");
            if (string.IsNullOrEmpty(host) && ctx.Args.Positional.Count > 0) host = ctx.Args.Positional[0];
            if (string.IsNullOrEmpty(host)) throw ToolException.Usage("缺少 --host", "例：net dns --host github.com");

            var wanted = new List<ushort>();
            var types = ctx.GetAll("type");
            if (types.Length == 0) { wanted.Add(DNS_TYPE_A); wanted.Add(DNS_TYPE_AAAA); }
            else
            {
                foreach (var t in types)
                {
                    switch (t.Trim().ToUpperInvariant())
                    {
                        case "A": wanted.Add(DNS_TYPE_A); break;
                        case "AAAA": wanted.Add(DNS_TYPE_AAAA); break;
                        case "CNAME": wanted.Add(DNS_TYPE_CNAME); break;
                        default: throw ToolException.Usage("--type 只支持 A|AAAA|CNAME：" + t);
                    }
                }
            }

            var records = new List<object>();
            var seen = new HashSet<string>();
            var errors = new List<string>();
            string server = ctx.Get("server");
            if (!string.IsNullOrEmpty(server)) ctx.Out.Warn("--server 暂未实现自定义 DNS 服务器，已使用系统默认");

            var sw = Stopwatch.StartNew();
            foreach (var t in wanted)
            {
                ctx.ThrowIfCancelled();
                IntPtr p = IntPtr.Zero;
                int rc;
                try { rc = DnsQuery_A(host, t, 0, IntPtr.Zero, ref p, IntPtr.Zero); }
                catch (Exception ex) { errors.Add("DnsQuery 调用失败：" + ex.Message); continue; }
                if (rc != 0)
                {
                    if (rc != 9501) errors.Add("DnsQuery type=" + t + " 失败 code=" + rc);   // 9501 = DNS_INFO_NO_RECORDS
                    continue;
                }
                try
                {
                    int offType = 2 * IntPtr.Size;
                    int offTtl = offType + 8;
                    int offData = offType + 16;
                    IntPtr rec = p;
                    int guard = 0;
                    while (rec != IntPtr.Zero && guard++ < 4096)
                    {
                        ushort rtype = (ushort)Marshal.ReadInt16(rec, offType);
                        uint ttl = (uint)Marshal.ReadInt32(rec, offTtl);
                        string value = null;
                        string kind = null;
                        if (rtype == DNS_TYPE_A)
                        {
                            var b = new byte[4];
                            Marshal.Copy((IntPtr)((long)rec + offData), b, 0, 4);
                            value = new IPAddress(b).ToString();
                            kind = "A";
                        }
                        else if (rtype == DNS_TYPE_AAAA)
                        {
                            var b = new byte[16];
                            Marshal.Copy((IntPtr)((long)rec + offData), b, 0, 16);
                            value = new IPAddress(b).ToString();
                            kind = "AAAA";
                        }
                        else if (rtype == DNS_TYPE_CNAME || rtype == 12)
                        {
                            IntPtr np = Marshal.ReadIntPtr(rec, offData);
                            if (np != IntPtr.Zero) value = Marshal.PtrToStringAnsi(np);
                            kind = rtype == DNS_TYPE_CNAME ? "CNAME" : "PTR";
                        }
                        if (value != null && seen.Add(kind + "|" + value))
                            records.Add(Json.Obj("type", kind, "value", value, "ttl", ttl));
                        rec = Marshal.ReadIntPtr(rec, 0);
                    }
                }
                finally { DnsRecordListFree(p, 1); }
            }
            sw.Stop();

            // System.Net.Dns 对照（CNAME 无法从 BCL 取得，仅作交叉验证）
            var bclAddrs = new List<object>();
            string bclError = null;
            var sw2 = Stopwatch.StartNew();
            try
            {
                foreach (var ip in Dns.GetHostAddresses(host))
                    bclAddrs.Add(Json.Obj("ip", ip.ToString(), "family", ip.AddressFamily.ToString()));
            }
            catch (Exception ex) { bclError = ex.Message; }
            sw2.Stop();

            var byType = records.GroupBy(o => Convert.ToString(((Dictionary<string, object>)o)["type"]))
                                .ToDictionary(g => g.Key, g => g.Select(x => ((Dictionary<string, object>)x)["value"]).ToList());

            ctx.Out.Result("net.dns", Json.Obj(
                "host", host,
                "items", records,
                "count", records.Count,
                "records", Json.Obj(
                    "A", byType.ContainsKey("A") ? (object)byType["A"] : new List<object>(),
                    "AAAA", byType.ContainsKey("AAAA") ? (object)byType["AAAA"] : new List<object>(),
                    "CNAME", byType.ContainsKey("CNAME") ? (object)byType["CNAME"] : new List<object>()),
                "elapsedMs", sw.ElapsedMilliseconds,
                "bclElapsedMs", sw2.ElapsedMilliseconds,
                "bclAddresses", bclAddrs,
                "bclError", bclError,
                "errors", errors,
                "columns", new[] { "type", "value", "ttl" }));
            if (records.Count == 0) return errors.Count > 0 ? ExitCodes.Error : ExitCodes.NotFound;
            return ExitCodes.Ok;
        }

        // ---------------------------------------------------------------- ip
        static int RunIp(Ctx ctx)
        {
            bool all = ctx.Flag("all");
            var items = new List<object>();
            var dnsServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gateways = new List<string>();
            string primaryV4 = null;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                ctx.ThrowIfCancelled();
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                bool loopback = ni.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                if (!all)
                {
                    if (loopback) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                }

                var v4 = new List<string>();
                var v6 = new List<string>();
                try
                {
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) v4.Add(ua.Address + "/" + ua.PrefixLength);
                        else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(ua.Address + "/" + ua.PrefixLength);
                    }
                }
                catch { }

                var gw = new List<string>();
                try { foreach (var g in props.GatewayAddresses) gw.Add(g.Address.ToString()); } catch { }
                foreach (var g in gw) if (!gateways.Contains(g)) gateways.Add(g);
                try { foreach (var d in props.DnsAddresses) dnsServers.Add(d.ToString()); } catch { }
                if (primaryV4 == null && v4.Count > 0 && !loopback) primaryV4 = v4[0].Split('/')[0];

                long speed = 0;
                try { speed = ni.Speed; } catch { }

                bool dhcp = false;
                try
                {
                    var v4p = props.GetIPv4Properties();
                    if (v4p != null) dhcp = v4p.IsDhcpEnabled;
                }
                catch { }

                items.Add(Json.Obj(
                    "name", ni.Name,
                    "description", ni.Description,
                    "type", ni.NetworkInterfaceType.ToString(),
                    "status", ni.OperationalStatus.ToString(),
                    "speedMbps", speed > 0 ? (object)Math.Round(speed / 1000000.0) : null,
                    "mac", BitConverter.ToString(ni.GetPhysicalAddress().GetAddressBytes()).Replace('-', ':'),
                    "ipv4", v4,
                    "ipv6", v6,
                    "gateways", gw,
                    "dhcp", dhcp,
                    "loopback", loopback));
            }

            ctx.Out.Result("net.ip", Json.Obj(
                "host", Dns.GetHostName(),
                "items", items,
                "count", items.Count,
                "primaryIPv4", primaryV4,
                "gateways", gateways,
                "dnsServers", dnsServers.ToList(),
                "all", all,
                "columns", new[] { "name", "type", "status", "ipv4", "mac" }));
            return ExitCodes.Ok;
        }

        // ---------------------------------------------------------------- download
        static int RunDownload(Ctx ctx)
        {
            string url = ctx.Get("url");
            string outPath = ctx.Get("out");
            if (string.IsNullOrEmpty(outPath)) outPath = ctx.Get("output");
            if (string.IsNullOrEmpty(url)) throw ToolException.Usage("缺少 --url", "例：net download --url https://x/f.bin --out f.bin");
            if (string.IsNullOrEmpty(outPath)) throw ToolException.Usage("缺少 --out", "例：net download --url https://x/f.bin --out f.bin");

            outPath = Fs.Expand(outPath, ctx.Cwd);
            bool resume = ctx.Flag("resume");
            bool overwrite = ctx.Flag("overwrite");
            TimeSpan timeout = ctx.Args.GetSpan("timeout", TimeSpan.FromMinutes(10));
            int timeoutMs = (int)Math.Max(1, Math.Min(timeout.TotalMilliseconds, int.MaxValue));
            string algo = (ctx.Get("hash") ?? "sha256").ToLowerInvariant();

            long existing = 0;
            if (File.Exists(outPath))
            {
                if (!overwrite && !resume)
                    throw ToolException.Usage("目标文件已存在：" + outPath + "（加 --overwrite 覆盖，或 --resume 续传）");
                if (resume) existing = new FileInfo(outPath).Length;
            }
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            var sw = Stopwatch.StartNew();
            HttpWebResponse resp = null;
            long total = -1, written = 0;
            int status = 0;
            string error = null;
            string hashHex = null;

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "dsh-toolbox/" + ToolInfo.Version;
                if (existing > 0) req.AddRange(existing);
                resp = (HttpWebResponse)req.GetResponse();
                status = (int)resp.StatusCode;
                total = resp.ContentLength >= 0 ? resp.ContentLength + existing : -1;

                HashAlgorithm hasher = CreateHasher(algo);
                long baseLen = 0;
                if (existing > 0 && resp.StatusCode == HttpStatusCode.OK)
                {
                    existing = 0;   // 服务端不支持 Range，从头来
                }
                if (existing > 0 && hasher != null)
                {
                    // 续传：先哈希已有内容，保证最终哈希覆盖整份文件
                    using (var fs = new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var buf0 = new byte[1024 * 1024];
                        int n0;
                        while ((n0 = fs.Read(buf0, 0, buf0.Length)) > 0) { hasher.TransformBlock(buf0, 0, n0, buf0, 0); baseLen += n0; }
                    }
                }

                var mode = existing > 0 ? FileMode.Append : FileMode.Create;
                using (hasher)
                using (var fs = new FileStream(outPath, mode, FileAccess.Write, FileShare.Read, 1024 * 1024))
                using (var rs = resp.GetResponseStream())
                {
                    var buf = new byte[1024 * 1024];
                    long lastReport = 0;
                    int n;
                    long sinceReport = 0;
                    long markBytes = written, markMs = 0;
                    while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                    {
                        ctx.ThrowIfCancelled();
                        fs.Write(buf, 0, n);
                        written += n;
                        if (hasher != null) hasher.TransformBlock(buf, 0, n, buf, 0);
                        if (sw.ElapsedMilliseconds - lastReport > 500)
                        {
                            lastReport = sw.ElapsedMilliseconds;
                            double instRate = (written - markBytes) / Math.Max(0.001, (sw.ElapsedMilliseconds - markMs) / 1000.0);
                            markBytes = written; markMs = sw.ElapsedMilliseconds;
                            string pct = total > 0 ? ((existing + written) * 100.0 / total).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "?";
                            ctx.Out.Line(string.Format(CultureInfo.InvariantCulture,
                                "{0} / {1} ({2})  {3}/s", Fs.FormatSize(existing + written),
                                total > 0 ? Fs.FormatSize(total) : "?", pct, Fs.FormatSize((long)instRate)));
                        }
                    }
                    sinceReport = written;
                    if (hasher != null)
                    {
                        hasher.TransformFinalBlock(buf, 0, 0);
                        hashHex = Fs.ToHex(hasher.Hash);
                    }
                    if (sinceReport == 0 && total == 0) hashHex = hasher != null ? Fs.ToHex(hasher.Hash) : null;
                }
                sw.Stop();
            }
            catch (WebException we)
            {
                sw.Stop();
                error = we.Status + ": " + we.Message;
            }
            finally { if (resp != null) { try { resp.Close(); } catch { } } }

            string expected = ctx.Get("expected");
            bool? match = null;
            if (!string.IsNullOrEmpty(expected) && hashHex != null)
                match = SignCommands.HashMatches(expected, hashHex, algo);

            ctx.Out.Result("net.download", Json.Obj(
                "url", url,
                "path", outPath,
                "status", status,
                "resumedFrom", existing,
                "bytes", written,
                "totalBytes", total,
                "elapsedMs", sw.ElapsedMilliseconds,
                "bytesPerSec", sw.ElapsedMilliseconds > 0 ? (long)(written / (sw.ElapsedMilliseconds / 1000.0)) : 0,
                "hashAlgo", algo,
                "hash", hashHex,
                "expected", expected,
                "match", match,
                "error", error,
                "columns", new[] { "path", "bytes", "elapsedMs", "hash" }));

            if (error != null) return ExitCodes.Error;
            if (match.HasValue && !match.Value) return ExitCodes.Error;
            return ExitCodes.Ok;
        }

        static HashAlgorithm CreateHasher(string algo)
        {
            switch ((algo ?? "sha256").ToLowerInvariant())
            {
                case "md5": return MD5.Create();
                case "sha1": return SHA1.Create();
                case "sha256": case "sha2": case "sha-256": return SHA256.Create();
                case "sha384": return SHA384.Create();
                case "sha512": return SHA512.Create();
                case "none": return null;
                default: throw ToolException.Usage("不支持的哈希算法：" + algo, "支持 md5/sha1/sha256/sha384/sha512/none");
            }
        }
    }
}
