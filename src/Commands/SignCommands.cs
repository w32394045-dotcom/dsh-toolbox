using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using DshToolbox.Core;

namespace DshToolbox.Commands
{
    // ==================================================================== 最小 DER 读取器
    internal sealed class DerReader
    {
        readonly byte[] Data;
        int Pos;

        public DerReader(byte[] data, int pos) { Data = data; Pos = pos; }
        public int Position { get { return Pos; } }
        public bool Eof { get { return Pos >= Data.Length; } }

        public int ReadTag() { return Data[Pos++]; }

        public static int ReadLength(byte[] d, ref int pos)
        {
            int b = d[pos++];
            if ((b & 0x80) == 0) return b;
            int n = b & 0x7F;
            int len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | d[pos++];
            return len;
        }

        public byte[] ReadElement(out int tag)
        {
            tag = ReadTag();
            int len = ReadLength(Data, ref Pos);
            var content = new byte[len];
            Array.Copy(Data, Pos, content, 0, len);
            Pos += len;
            return content;
        }

        public void Skip()
        {
            ReadTag();
            int len = ReadLength(Data, ref Pos);
            Pos += len;
        }
    }

    // ==================================================================== WinVerifyTrust
    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;            // WTD_UI_NONE = 2
        public uint fdwRevocationChecks;   // WTD_REVOKE_NONE=0 / WTD_REVOKE_WHOLECHAIN=1
        public uint dwUnionChoice;         // WTD_CHOICE_FILE = 1
        public IntPtr pUnion;              // -> WinTrustFileInfo
        public uint dwStateAction;         // VERIFY=1 / CLOSE=2
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;           // WTD_UICONTEXT_EXECUTE = 0
        public IntPtr pSignatureSettings;
    }

    // ==================================================================== 结果模型
    internal sealed class SignVerifyResult
    {
        public string Path;
        public long FileSize;
        public bool IsPe;
        public string SignatureKind = "none";       // embedded | catalog | none
        public string CatalogPath;
        public bool Verdict;
        public string Status = "UnknownError";
        public string Reason;
        public uint WvtResult;
        public string WvtName;
        public uint EffectiveWvtResult;          // 内嵌签名 / 目录签名任一通过即为 0
        public uint CatalogWvtResult;
        public string CatalogWvtName;
        public bool CatalogVerified;
        public long CatalogWvtMs;
        public string SignerSubject, SignerIssuer, SignerThumbprint, SignerSerial;
        public DateTime? SignerNotBefore, SignerNotAfter;
        public string SignatureAlgorithm, DigestAlgorithm, DigestOid;
        public string ExpectedDigestBase64, ComputedDigestHex;
        public bool DigestMatches;
        public bool DigestSkipped;
        public string DigestSkipReason;
        public bool Pkcs7Verified;
        public string Pkcs7Error;
        public bool ChainValid;
        public string RevocationMode = "online";
        public bool RevocationChecked;
        public bool RevocationStatusUnknown;
        public string TrustLevel;
        public readonly List<object> ChainElements = new List<object>();
        public readonly List<string> ChainStatus = new List<string>();
        public readonly List<string> WvtAttempts = new List<string>();
        public bool Timestamped;
        public DateTime? TimestampTime;
        public string TimestampSubject, TimestampKind, TimestampError;
        public readonly List<string> Errors = new List<string>();
        public long CertTableOffset, CertTableSize;
        public long WvtMs, PeMs, DetailMs, ChainMs, TotalMs;
    }

    // ==================================================================== 校验内核
    internal static class SignCore
    {
        // ---------------------------------------------------------- P/Invoke
        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, SetLastError = false)]
        static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CryptCATAdminAcquireContext(out IntPtr phCatAdmin, IntPtr pgSubsystem, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern bool CryptCATAdminCalcHashFromFileHandle(IntPtr hFile, ref uint pcbHash, byte[] pbHash, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, ref IntPtr phPrevCatInfo);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CatalogInfo psCatInfo, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct CatalogInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string wszCatalogFile;
        }

        public const uint WVTRUST_OK = 0x00000000;
        public const uint TRUST_E_NOSIGNATURE = 0x800B0100;
        public const uint TRUST_E_BAD_DIGEST = 0x80096010;

        static readonly Dictionary<uint, string> WvtNames = new Dictionary<uint, string>
        {
            { 0x00000000, "ERROR_SUCCESS" },
            { 0x800B0001, "TRUST_E_PROVIDER_UNKNOWN" },
            { 0x800B0003, "TRUST_E_SUBJECT_FORM_UNKNOWN" },
            { 0x800B0004, "TRUST_E_SUBJECT_NOT_TRUSTED" },
            { 0x800B0100, "TRUST_E_NOSIGNATURE" },
            { 0x800B0101, "CERT_E_EXPIRED" },
            { 0x800B0102, "CERT_E_VALIDITYPERIODNESTING" },
            { 0x800B0106, "CERT_E_PURPOSE" },
            { 0x800B0107, "CERT_E_ISSUERCHAINING" },
            { 0x800B0109, "CERT_E_UNTRUSTEDROOT" },
            { 0x800B010A, "CERT_E_CHAINING" },
            { 0x800B010B, "TRUST_E_FAIL" },
            { 0x800B010C, "CERT_E_REVOKED" },
            { 0x800B010D, "CERT_E_UNTRUSTEDTESTROOT" },
            { 0x800B010E, "CERT_E_REVOCATION_FAILURE" },
            { 0x800B0110, "CERT_E_WRONG_USAGE" },
            { 0x800B0111, "TRUST_E_EXPLICIT_DISTRUST" },
            { 0x800B0112, "CERT_E_INVALID_NAME" },
            { 0x80096004, "TRUST_E_CERT_SIGNATURE" },
            { 0x80096005, "TRUST_E_TIME_STAMP" },
            { 0x80096010, "TRUST_E_BAD_DIGEST" },
            { 0x80092026, "CRYPT_E_SECURITY_SETTINGS" },
            { 0x800B010F, "CERT_E_UNTRUSTEDCA" }
        };

        public static string WvtNameOf(uint code)
        {
            string n;
            if (WvtNames.TryGetValue(code, out n)) return n;
            return "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// WinVerifyTrust(GENERIC_VERIFY_V2)——与 Windows 自身判定一致，自动覆盖
        /// 内嵌签名 + 目录签名（catalog）。本机 System32 下大量文件是目录签名，
        /// 只看 PE 里的 PKCS#7 会误判为 NotSigned（假阴性）。
        /// </summary>
        public static uint WinVerifyTrustFile(string path, string mode)
        {
            var fileInfo = new WinTrustFileInfo();
            fileInfo.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
            fileInfo.pcwszFilePath = path;
            fileInfo.hFile = IntPtr.Zero;
            fileInfo.pgKnownSubject = IntPtr.Zero;

            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            IntPtr pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);

                var data = new WinTrustData();
                data.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
                data.dwUIChoice = 2;          // WTD_UI_NONE
                data.dwUnionChoice = 1;       // WTD_CHOICE_FILE
                data.pUnion = pFile;
                data.dwStateAction = 1;       // WTD_STATEACTION_VERIFY
                data.dwUIContext = 0;         // WTD_UICONTEXT_EXECUTE

                // 网络/吊销策略（显式，且在结果里回报）
                if (mode == "none")
                {
                    data.fdwRevocationChecks = 0;                    // WTD_REVOKE_NONE
                    data.dwProvFlags = 0x10 | 0x1000;                // REVOCATION_CHECK_NONE | CACHE_ONLY_URL_RETRIEVAL
                }
                else if (mode == "cache" || mode == "offline")
                {
                    data.fdwRevocationChecks = 1;                    // WTD_REVOKE_WHOLECHAIN
                    data.dwProvFlags = 0x40 | 0x1000;                // REVOCATION_CHECK_CHAIN | CACHE_ONLY_URL_RETRIEVAL
                }
                else
                {
                    data.fdwRevocationChecks = 1;                    // WTD_REVOKE_WHOLECHAIN
                    data.dwProvFlags = 0x40;                         // REVOCATION_CHECK_CHAIN（允许联网，严格档）
                }

                pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustData)));
                Marshal.StructureToPtr(data, pData, false);

                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
                uint rc = WinVerifyTrust(IntPtr.Zero, ref action, pData);

                // 释放 WVT 状态
                var close = (WinTrustData)Marshal.PtrToStructure(pData, typeof(WinTrustData));
                close.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
                Marshal.StructureToPtr(close, pData, false);
                WinVerifyTrust(IntPtr.Zero, ref action, pData);
                return rc;
            }
            finally
            {
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
                Marshal.FreeHGlobal(pFile);
            }
        }

        /// <summary>用目录（catalog）API 找到覆盖该文件的 .cat 文件路径。</summary>
        public static string FindCatalogPath(string filePath)
        {
            IntPtr hCatAdmin = IntPtr.Zero, hCatInfo = IntPtr.Zero;
            try
            {
                if (!CryptCATAdminAcquireContext(out hCatAdmin, IntPtr.Zero, 0)) return null;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    uint hashSize = 0;
                    CryptCATAdminCalcHashFromFileHandle(fs.SafeFileHandle.DangerousGetHandle(), ref hashSize, null, 0);
                    if (hashSize == 0 || hashSize > 1024) return null;
                    var hash = new byte[hashSize];
                    if (!CryptCATAdminCalcHashFromFileHandle(fs.SafeFileHandle.DangerousGetHandle(), ref hashSize, hash, 0)) return null;
                    IntPtr prev = IntPtr.Zero;
                    hCatInfo = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, hashSize, 0, ref prev);
                    if (hCatInfo == IntPtr.Zero) return null;
                    var ci = new CatalogInfo();
                    ci.cbStruct = (uint)Marshal.SizeOf(typeof(CatalogInfo));
                    if (!CryptCATCatalogInfoFromContext(hCatInfo, ref ci, 0)) return null;
                    return string.IsNullOrEmpty(ci.wszCatalogFile) ? null : ci.wszCatalogFile;
                }
            }
            catch { return null; }
            finally
            {
                if (hCatInfo != IntPtr.Zero) { try { CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0); } catch { } }
                if (hCatAdmin != IntPtr.Zero) { try { CryptCATAdminReleaseContext(hCatAdmin, 0); } catch { } }
            }
        }

        // ---------------------------------------------------------- 哈希/工具
        public static string AlgoFromOid(string oid)
        {
            switch (oid)
            {
                case "1.3.14.3.2.26": return "sha1";
                case "2.16.840.1.101.3.4.2.1": return "sha256";
                case "2.16.840.1.101.3.4.2.2": return "sha384";
                case "2.16.840.1.101.3.4.2.3": return "sha512";
                case "1.2.840.113549.1.1.5": return "sha1";
                case "1.2.840.113549.1.1.11": return "sha256";
                case "1.2.840.113549.1.1.12": return "sha384";
                case "1.2.840.113549.1.1.13": return "sha512";
                default: return null;
            }
        }

        public static HashAlgorithm CreateHash(string algo)
        {
            switch ((algo ?? "").ToLowerInvariant())
            {
                case "sha1": return SHA1.Create();
                case "sha256": return SHA256.Create();
                case "sha384": return SHA384.Create();
                case "sha512": return SHA512.Create();
                default: return null;
            }
        }

        public static string DecodeOid(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            var sb = new StringBuilder();
            int first = b[0];
            sb.Append(first / 40).Append('.').Append(first % 40);
            int val = 0;
            for (int i = 1; i < b.Length; i++)
            {
                val = (val << 7) | (b[i] & 0x7F);
                if ((b[i] & 0x80) == 0) { sb.Append('.').Append(val); val = 0; }
            }
            return sb.ToString();
        }

        public static bool TryExtractDigestInfo(byte[] spcIndirectData, out string oid, out byte[] digest)
        {
            oid = null; digest = null;
            try
            {
                int tag;
                var r = new DerReader(spcIndirectData, 0);
                byte[] outer = r.ReadElement(out tag);
                if (tag != 0x30) return false;
                var o = new DerReader(outer, 0);
                o.Skip();
                byte[] digestInfo = o.ReadElement(out tag);
                if (tag != 0x30) return false;
                var di = new DerReader(digestInfo, 0);
                byte[] algId = di.ReadElement(out tag);
                if (tag != 0x30) return false;
                var a = new DerReader(algId, 0);
                byte[] oidBytes = a.ReadElement(out tag);
                if (tag != 0x06) return false;
                oid = DecodeOid(oidBytes);
                byte[] dig = di.ReadElement(out tag);
                if (tag != 0x04) return false;
                digest = dig;
                return true;
            }
            catch { return false; }
        }

        internal static byte[] ReadAt(FileStream fs, long offset, int length)
        {
            if (offset < 0 || length <= 0 || offset + length > fs.Length) return null;
            var buf = new byte[length];
            fs.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < length)
            {
                int n = fs.Read(buf, read, length - read);
                if (n <= 0) break;
                read += n;
            }
            return read == length ? buf : null;
        }

        public static bool TryParsePe(FileStream fs, out long checksumOffset, out long secDirOffset,
                                     out long certOffset, out long certSize, out string error)
        {
            checksumOffset = secDirOffset = certOffset = certSize = 0;
            error = null;
            try
            {
                var dos = ReadAt(fs, 0, 0x40);
                if (dos == null || dos[0] != 'M' || dos[1] != 'Z') { error = "不是 PE 文件（缺少 MZ）"; return false; }
                int peOff = BitConverter.ToInt32(dos, 0x3C);
                var pe = ReadAt(fs, peOff, 24);
                if (pe == null || pe[0] != 'P' || pe[1] != 'E' || pe[2] != 0 || pe[3] != 0)
                { error = "不是 PE 文件（缺少 PE 签名）"; return false; }
                int optSize = BitConverter.ToUInt16(pe, 20);
                long optOff = peOff + 24;
                var magicBuf = ReadAt(fs, optOff, 2);
                if (magicBuf == null) { error = "可选头缺失"; return false; }
                int magic = BitConverter.ToUInt16(magicBuf, 0);
                bool pe32Plus = magic == 0x20B;
                if (magic != 0x10B && !pe32Plus) { error = "未知的可选头 magic=0x" + magic.ToString("X"); return false; }

                checksumOffset = optOff + 64;
                long dataDirOffset = optOff + (pe32Plus ? 112 : 96);
                if (optSize > 0 && dataDirOffset + 8 * 5 > optOff + optSize) { error = "数据目录越界"; return false; }
                secDirOffset = dataDirOffset + 8 * 4;
                var dir = ReadAt(fs, secDirOffset, 8);
                if (dir == null) { error = "无法读取安全目录项"; return false; }
                certOffset = (uint)BitConverter.ToInt32(dir, 0);
                certSize = (uint)BitConverter.ToInt32(dir, 4);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        static void CopyRange(FileStream fs, HashAlgorithm h, long start, long end)
        {
            if (end <= start) return;
            if (start < 0) start = 0;
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[1024 * 1024];
            long remaining = end - start;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buf.Length, remaining);
                int n = fs.Read(buf, 0, want);
                if (n <= 0) break;
                h.TransformBlock(buf, 0, n, buf, 0);
                remaining -= n;
            }
        }

        public static string ComputePeHash(FileStream fs, string algo, long checksumOffset, long secDirOffset,
                                           long certOffset, long certSize)
        {
            using (var h = CreateHash(algo))
            {
                if (h == null) return null;
                long eof = fs.Length;
                CopyRange(fs, h, 0, checksumOffset);
                CopyRange(fs, h, checksumOffset + 4, secDirOffset);
                long afterSecDir = secDirOffset + 8;
                if (certSize <= 0 || certOffset <= 0 || certOffset > eof) CopyRange(fs, h, afterSecDir, eof);
                else
                {
                    CopyRange(fs, h, afterSecDir, certOffset);
                    long afterCert = certOffset + certSize;
                    if (afterCert < eof) CopyRange(fs, h, afterCert, eof);
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                return Fs.ToHex(h.Hash);
            }
        }

        // ---------------------------------------------------------- PKCS#7 细节
        static void FillSigner(SignVerifyResult r, X509Certificate2 c)
        {
            if (c == null) return;
            try { r.SignerSubject = c.Subject; } catch { }
            try { r.SignerIssuer = c.Issuer; } catch { }
            try { r.SignerThumbprint = c.Thumbprint; } catch { }
            try { r.SignerSerial = c.SerialNumber; } catch { }
            try { r.SignerNotBefore = c.NotBefore; } catch { }
            try { r.SignerNotAfter = c.NotAfter; } catch { }
        }

        /// <summary>解码 PKCS#7：签名者、算法、签名自校验、时间戳（RFC3161 / 传统计数器签名）。</summary>
        static SignedCms DecodeCms(byte[] pkcs7, SignVerifyResult r)
        {
            try
            {
                var cms = new SignedCms();
                cms.Decode(pkcs7);
                if (cms.SignerInfos.Count == 0)
                {
                    r.Errors.Add("PKCS#7 中没有 SignerInfo");
                    return null;
                }
                var si = cms.SignerInfos[0];
                X509Certificate2 signer = null;
                try { signer = si.Certificate; } catch { }
                FillSigner(r, signer);
                try { r.SignatureAlgorithm = si.SignatureAlgorithm != null ? si.SignatureAlgorithm.FriendlyName : null; } catch { }
                try { r.DigestAlgorithm = si.DigestAlgorithm != null ? si.DigestAlgorithm.FriendlyName : null; } catch { }
                try
                {
                    if (cms.ContentInfo != null && cms.ContentInfo.Content != null)
                    {
                        string oid;
                        byte[] dig;
                        if (TryExtractDigestInfo(cms.ContentInfo.Content, out oid, out dig))
                        {
                            r.DigestOid = oid;
                            r.ExpectedDigestBase64 = Convert.ToBase64String(dig);
                        }
                    }
                }
                catch (Exception ex) { r.Errors.Add("解析摘要信息失败：" + ex.Message); }

                try { cms.CheckSignature(true); r.Pkcs7Verified = true; }
                catch (Exception ex)
                {
                    r.Pkcs7Verified = false;
                    r.Pkcs7Error = ex.Message;
                    r.Errors.Add("PKCS#7 签名校验失败：" + ex.Message);
                }

                // RFC3161 时间戳（.NET FW 不暴露 unsignedAttrs，直接扫 DER）
                try
                {
                    byte[] token = FindRfc3161Token(pkcs7);
                    if (token != null)
                    {
                        r.Timestamped = true;
                        r.TimestampKind = "RFC3161";
                        var ts = new SignedCms();
                        ts.Decode(token);
                        if (ts.SignerInfos.Count > 0 && ts.SignerInfos[0].Certificate != null)
                            r.TimestampSubject = ts.SignerInfos[0].Certificate.Subject;
                        r.TimestampTime = FindGeneralizedTime(ts.ContentInfo != null ? ts.ContentInfo.Content : null);
                    }
                }
                catch (Exception ex) { r.TimestampError = ex.Message; }

                try
                {
                    foreach (SignerInfo cs in si.CounterSignerInfos)
                    {
                        r.Timestamped = true;
                        if (r.TimestampKind == null) r.TimestampKind = "Authenticode-CounterSignature";
                        if (!r.TimestampTime.HasValue) r.TimestampTime = TimeFromSignedAttributes(cs);
                        if (cs.Certificate != null && r.TimestampSubject == null) r.TimestampSubject = cs.Certificate.Subject;
                    }
                }
                catch (Exception ex) { if (r.TimestampError == null) r.TimestampError = ex.Message; }

                return cms;
            }
            catch (Exception ex)
            {
                r.Pkcs7Error = "PKCS#7 解码失败：" + ex.Message;
                r.Errors.Add(r.Pkcs7Error);
                return null;
            }
        }

        static void BuildChain(SignVerifyResult r, X509Certificate2 signer, SignedCms cms)
        {
            if (signer == null) return;
            var chainWatch = Stopwatch.StartNew();
            try
            {
                using (var chain = new X509Chain())
                {
                    X509RevocationMode rm = X509RevocationMode.Online;
                    if (r.RevocationMode == "none") rm = X509RevocationMode.NoCheck;
                    else if (r.RevocationMode == "cache" || r.RevocationMode == "offline") rm = X509RevocationMode.Offline;
                    chain.ChainPolicy.RevocationMode = rm;
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                    chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(rm == X509RevocationMode.NoCheck ? 2 : 15);
                    if (cms != null) { try { chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates); } catch { } }
                    r.ChainValid = chain.Build(signer);
                    foreach (var st in chain.ChainStatus)
                        if (st.Status == X509ChainStatusFlags.RevocationStatusUnknown ||
                            st.Status == X509ChainStatusFlags.OfflineRevocation)
                            r.RevocationStatusUnknown = true;

                    int level = 0;
                    foreach (var el in chain.ChainElements)
                    {
                        var statuses = new List<string>();
                        foreach (var st in el.ChainElementStatus)
                            statuses.Add(st.Status + (string.IsNullOrEmpty(st.StatusInformation) ? "" : (": " + st.StatusInformation.Trim())));
                        var cert = el.Certificate;
                        r.ChainElements.Add(Json.Obj(
                            "level", level++,
                            "subject", cert != null ? cert.Subject : null,
                            "issuer", cert != null ? cert.Issuer : null,
                            "thumbprint", cert != null ? cert.Thumbprint : null,
                            "notBefore", SafeDate(cert, true),
                            "notAfter", SafeDate(cert, false),
                            "status", statuses.Count == 0 ? "ok" : string.Join("; ", statuses.ToArray()),
                            "isRoot", cert != null && cert.Subject == cert.Issuer));
                        foreach (var s in statuses) r.ChainStatus.Add(s);
                    }
                    if (r.ChainStatus.Count == 0) r.ChainStatus.Add("NoError");
                }
            }
            catch (Exception ex)
            {
                r.ChainValid = false;
                r.Errors.Add("证书链构建失败：" + ex.Message);
            }
            chainWatch.Stop();
            r.ChainMs = chainWatch.ElapsedMilliseconds;
        }

        // ---------------------------------------------------------- 主流程
        public static SignVerifyResult Verify(string path, string revocationMode, bool forceDigest, bool verbose)
        {
            var r = new SignVerifyResult();
            r.Path = path;
            r.RevocationMode = string.IsNullOrEmpty(revocationMode) ? "online" : revocationMode;
            r.RevocationChecked = r.RevocationMode != "none";
            var total = Stopwatch.StartNew();
            r.FileSize = new FileInfo(path).Length;

            long checksumOffset = 0, secDirOffset = 0, certOffset = 0, certSize = 0;
            SignedCms cms = null;
            X509Certificate2 signer = null;

            var wvtWatch = Stopwatch.StartNew();
            try { r.WvtResult = WinVerifyTrustFile(path, r.RevocationMode); }
            catch (Exception ex)
            {
                r.WvtResult = 0xFFFFFFFF;
                r.Errors.Add("WinVerifyTrust 调用失败：" + ex.Message);
            }
            wvtWatch.Stop();
            r.WvtMs = wvtWatch.ElapsedMilliseconds;
            r.WvtName = WvtNameOf(r.WvtResult);
            r.WvtAttempts.Add("file:" + Path.GetFileName(path) + "(" + r.RevocationMode + ") -> " + r.WvtName);
            r.EffectiveWvtResult = r.WvtResult;

            var detailWatch = Stopwatch.StartNew();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.RandomAccess))
                {
                    var peWatch = Stopwatch.StartNew();
                    string peError;
                    r.IsPe = TryParsePe(fs, out checksumOffset, out secDirOffset, out certOffset, out certSize, out peError);
                    peWatch.Stop();
                    r.PeMs = peWatch.ElapsedMilliseconds;
                    r.CertTableOffset = certOffset;
                    r.CertTableSize = certSize;
                    if (!r.IsPe) r.Errors.Add(peError ?? "非 PE 文件");

                    if (r.IsPe && certSize > 0 && certOffset > 0)
                    {
                        r.SignatureKind = "embedded";
                        try
                        {
                            var hdr = ReadAt(fs, certOffset, 8);
                            if (hdr == null) throw new Exception("证书表头越界");
                            int dwLength = BitConverter.ToInt32(hdr, 0);
                            int bodyLen = dwLength - 8;
                            if (bodyLen <= 0 || bodyLen > 64 * 1024 * 1024) throw new Exception("证书表长度异常：" + dwLength);
                            byte[] pkcs7 = ReadAt(fs, certOffset + 8, bodyLen);
                            if (pkcs7 == null) throw new Exception("无法读取 PKCS#7 数据");
                            cms = DecodeCms(pkcs7, r);
                            if (cms != null && cms.SignerInfos.Count > 0)
                            {
                                try { signer = cms.SignerInfos[0].Certificate; } catch { }
                                string digestAlgo = AlgoFromOid(r.DigestOid);
                                // 大文件默认不再自己算一遍 PE 摘要：WinVerifyTrust 通过时它已经校验过文件摘要，
                                // 而 289MB 安装包再读一遍在老机械盘上要好几秒。需要自带摘要请加 --digest。
                                bool wantDigest = forceDigest || fs.Length <= 64L * 1024 * 1024 || r.WvtResult != WVTRUST_OK;
                                if (digestAlgo != null && wantDigest)
                                {
                                    r.ComputedDigestHex = ComputePeHash(fs, digestAlgo, checksumOffset, secDirOffset, certOffset, certSize);
                                    r.DigestAlgorithm = digestAlgo + (r.DigestAlgorithm != null ? " (" + r.DigestAlgorithm + ")" : "");
                                    if (r.ExpectedDigestBase64 != null)
                                    {
                                        r.DigestMatches = string.Equals(
                                            Fs.ToHex(Convert.FromBase64String(r.ExpectedDigestBase64)),
                                            r.ComputedDigestHex, StringComparison.OrdinalIgnoreCase);
                                        if (!r.DigestMatches) r.Errors.Add("文件摘要与签名内摘要不一致（HashMismatch）");
                                    }
                                }
                                else if (digestAlgo != null)
                                {
                                    r.DigestSkipped = true;
                                    r.DigestSkipReason = "文件 >64MB 且 WinVerifyTrust 已通过（它的校验本身就覆盖文件摘要）；加 --digest 可强制自带比对";
                                }
                                else r.Errors.Add("无法确定摘要算法（OID=" + (r.DigestOid ?? "?") + "）");
                            }
                        }
                        catch (Exception ex) { r.Errors.Add("提取内嵌签名失败：" + ex.Message); }
                    }
                    else if (r.IsPe || r.WvtResult == WVTRUST_OK)
                    {
                        // 没有内嵌签名：可能是目录签名（System32 下大量系统文件）。
                        // WinVerifyTrust(WTD_CHOICE_FILE) 不会自动回退到目录签名，必须自己按文件哈希找 .cat
                        // （CryptCATAdminEnumCatalogFromHash 本身就是"按文件哈希命中目录"，即已证明成员关系），
                        // 再用 WinVerifyTrust 校验该 .cat 是否被可信链正确签名。两者合起来 = 目录签名有效。
                        r.CatalogPath = FindCatalogPath(path);
                        if (r.CatalogPath != null)
                        {
                            r.SignatureKind = "catalog";
                            var catWatch = Stopwatch.StartNew();
                            try { r.CatalogWvtResult = WinVerifyTrustFile(r.CatalogPath, r.RevocationMode); }
                            catch (Exception ex)
                            {
                                r.CatalogWvtResult = 0xFFFFFFFF;
                                r.Errors.Add("校验目录签名文件失败：" + ex.Message);
                            }
                            catWatch.Stop();
                            r.CatalogWvtMs = catWatch.ElapsedMilliseconds;
                            r.CatalogWvtName = WvtNameOf(r.CatalogWvtResult);
                            r.WvtAttempts.Add("catalog:" + Path.GetFileName(r.CatalogPath) + "(" + r.RevocationMode + ") -> " + r.CatalogWvtName);
                            r.CatalogVerified = r.CatalogWvtResult == WVTRUST_OK;
                            if (r.CatalogVerified)
                                r.EffectiveWvtResult = WVTRUST_OK;
                            else if (r.WvtResult != WVTRUST_OK)
                                r.EffectiveWvtResult = r.CatalogWvtResult;
                            try
                            {
                                byte[] catBytes = File.ReadAllBytes(r.CatalogPath);
                                cms = DecodeCms(catBytes, r);
                                if (cms != null && cms.SignerInfos.Count > 0)
                                {
                                    try { signer = cms.SignerInfos[0].Certificate; } catch { }
                                }
                            }
                            catch (Exception ex) { r.Errors.Add("读取目录签名失败：" + ex.Message); }
                        }
                    }
                    if (r.SignatureKind == "none" && r.WvtResult == TRUST_E_NOSIGNATURE && certSize > 0) r.SignatureKind = "embedded";
                    if (r.SignatureKind == "none" && (r.WvtResult == WVTRUST_OK)) r.SignatureKind = "catalog";
                }
            }
            catch (UnauthorizedAccessException) { throw ToolException.Denied("无权限读取：" + path); }
            catch (IOException ex) { throw new ToolException("E_IO", "读取文件失败：" + ex.Message, "确认文件未被独占锁定", ExitCodes.Error); }
            detailWatch.Stop();
            r.DetailMs = detailWatch.ElapsedMilliseconds;

            BuildChain(r, signer, cms);

            // ---- 结论：WinVerifyTrust（内嵌优先，目录签名需 .cat 自身校验通过）为准；
            //      摘要自校验可把结论细化为 HashMismatch
            if (r.EffectiveWvtResult == WVTRUST_OK)
            {
                r.Status = "Valid";
                r.Verdict = true;
                r.Reason = null;
            }
            else if (r.ExpectedDigestBase64 != null && r.ComputedDigestHex != null && !r.DigestMatches)
            {
                r.Status = "HashMismatch";
                r.Verdict = false;
                r.Reason = "文件摘要与签名内摘要不一致（" + r.WvtName + "）";
            }
            else
            {
                switch (r.EffectiveWvtResult)
                {
                    case TRUST_E_NOSIGNATURE:
                        r.Status = "NotSigned";
                        r.Reason = "未找到 Authenticode 签名（内嵌与目录签名都没有）";
                        break;
                    case TRUST_E_BAD_DIGEST:
                        r.Status = "HashMismatch";
                        r.Reason = "签名存在但文件摘要不匹配（文件可能被篡改）";
                        break;
                    case 0x800B0101: r.Status = "NotTrusted"; r.Reason = "签名证书已过期（CERT_E_EXPIRED）"; break;
                    case 0x800B0109: r.Status = "NotTrusted"; r.Reason = "证书链根不受信任（CERT_E_UNTRUSTEDROOT）"; break;
                    case 0x800B010A: r.Status = "NotTrusted"; r.Reason = "证书链构建失败（CERT_E_CHAINING）"; break;
                    case 0x800B0111: r.Status = "NotTrusted"; r.Reason = "该证书被显式吊销/不信任（TRUST_E_EXPLICIT_DISTRUST）"; break;
                    case 0x800B010C: r.Status = "NotTrusted"; r.Reason = "签名证书已被吊销（CERT_E_REVOKED）"; break;
                    case 0x800B010E: r.Status = "NotTrusted"; r.Reason = "吊销状态检查失败（CERT_E_REVOCATION_FAILURE）"; break;
                    case 0x80096005: r.Status = "NotTrusted"; r.Reason = "时间戳签名无效（TRUST_E_TIME_STAMP）"; break;
                    case 0x80096004: r.Status = "NotTrusted"; r.Reason = "证书签名无效（TRUST_E_CERT_SIGNATURE）"; break;
                    default:
                        r.Status = "UnknownError";
                        r.Reason = "WinVerifyTrust 返回 " + WvtNameOf(r.EffectiveWvtResult) +
                                   "（0x" + r.EffectiveWvtResult.ToString("X8", CultureInfo.InvariantCulture) + "）";
                        break;
                }
                r.Verdict = false;
            }

            if (r.Status == "Valid")
            {
                if (r.RevocationMode == "none") r.TrustLevel = "chain-valid-no-revocation-check";
                else if (r.RevocationStatusUnknown) r.TrustLevel = "chain-valid-revocation-unknown";
                else r.TrustLevel = "full";
            }
            else if (r.Status == "NotTrusted" || r.Status == "HashMismatch") r.TrustLevel = "untrusted";
            else r.TrustLevel = "unverified";

            r.TotalMs = total.ElapsedMilliseconds;
            return r;
        }

        internal static DateTime? SafeDate(X509Certificate2 c, bool from)
        {
            if (c == null) return null;
            try { return from ? c.NotBefore : c.NotAfter; } catch { return null; }
        }

        static readonly byte[] Rfc3161OidDer = new byte[]
        { 0x06, 0x0B, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x03, 0x03, 0x01 };

        public static byte[] FindRfc3161Token(byte[] der)
        {
            if (der == null) return null;
            for (int i = 0; i + Rfc3161OidDer.Length + 4 < der.Length; i++)
            {
                bool hit = true;
                for (int k = 0; k < Rfc3161OidDer.Length; k++)
                    if (der[i + k] != Rfc3161OidDer[k]) { hit = false; break; }
                if (!hit) continue;
                try
                {
                    int p = i + Rfc3161OidDer.Length;
                    if (der[p] != 0x31) continue;
                    p++;
                    DerReader.ReadLength(der, ref p);
                    if (der[p] != 0x04) continue;
                    p++;
                    int len = DerReader.ReadLength(der, ref p);
                    if (len <= 0 || p + len > der.Length) continue;
                    var token = new byte[len];
                    Array.Copy(der, p, token, 0, len);
                    return token;
                }
                catch { }
            }
            return null;
        }

        public static DateTime? TimeFromSignedAttributes(SignerInfo si)
        {
            try
            {
                foreach (CryptographicAttributeObject a in si.SignedAttributes)
                {
                    if (a.Oid == null) continue;
                    if (a.Oid.Value != "1.2.840.113549.1.9.5" && a.Oid.Value != "1.3.6.1.4.1.311.3.3.1") continue;
                    foreach (AsnEncodedData v in a.Values)
                    {
                        var t = ParseDerTime(v.RawData);
                        if (t.HasValue) return t;
                    }
                }
            }
            catch { }
            return null;
        }

        static DateTime? ParseDerTime(byte[] raw)
        {
            if (raw == null || raw.Length < 15) return null;
            int tag = raw[0];
            if (tag != 0x17 && tag != 0x18) return null;
            int p = 1;
            int len = DerReader.ReadLength(raw, ref p);
            if (len <= 0 || p + len > raw.Length) return null;
            string s = Encoding.ASCII.GetString(raw, p, len).TrimEnd('Z');
            string[] formats = tag == 0x17
                ? new[] { "yyMMddHHmmss", "yyMMddHHmm" }
                : new[] { "yyyyMMddHHmmss", "yyyyMMddHHmm" };
            DateTime dt;
            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                return dt;
            return null;
        }

        static DateTime? FindGeneralizedTime(byte[] der)
        {
            if (der == null) return null;
            for (int i = 0; i + 15 < der.Length; i++)
            {
                if (der[i] != 0x18) continue;
                int len = der[i + 1];
                if (len < 14 || len > 20 || i + 2 + len > der.Length) continue;
                string s = Encoding.ASCII.GetString(der, i + 2, len);
                DateTime dt;
                if (DateTime.TryParseExact(s.TrimEnd('Z'), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                    return dt;
            }
            return null;
        }
    }

    // ==================================================================== sign.*
    /// <summary>
    /// 签名与完整性（sysdev 拥有）。
    /// 招牌能力 sign.verify：判定用 WinVerifyTrust（与 Windows/Get-AuthenticodeSignature 一致，
    /// 覆盖内嵌签名 + 目录签名），细节用 SignedCms/X509Chain 自己算——不 spawn PowerShell、不设 20s 超时。
    /// 本机实测 Get-AuthenticodeSignature 对 289MB 安装包要 84.3 秒，electron-updater 硬编码 20 秒超时
    /// 因此必然失败；耗时与吊销检查档位都在结果里回报。
    /// </summary>
    public static class SignCommands
    {
        public static void Register()
        {
            Registry.Add("sign.verify", "Authenticode 校验：内嵌/目录签名、状态、签名者、有效期、时间戳、证书链、耗时",
                "sign verify --file <exe> [--publisher <s>] [--revocation online|cache|none] [--no-revocation] [--digest] [--json]",
                RunVerify,
                aliases: new[] { "authenticode" },
                examples: new[] { "dsh-toolbox sign verify --file C:\\Windows\\System32\\notepad.exe --json",
                                 "dsh-toolbox sign verify --file setup.exe --publisher \"DeepSeek\"",
                                 "dsh-toolbox sign verify --file setup.exe --no-revocation --json   # 不做吊销检查（快）" });

            Registry.Add("sign.chain", "只做证书链构建，逐级输出 ChainStatus",
                "sign chain --file <exe> [--revocation online|cache|none] [--no-revocation]",
                RunChain,
                examples: new[] { "dsh-toolbox sign chain --file C:\\Windows\\System32\\notepad.exe --json" });

            Registry.Add("sign.hash", "计算 sha1/sha256/sha512 并与 --expected 比对（支持 electron-builder base64 sha512 / latest.yml）",
                "sign hash --file <f> [--algo sha256|all]... [--expected <hex|base64|sha512-base64>] [--feed <latest.yml>]",
                RunHash,
                aliases: new[] { "hash" },
                examples: new[] { "dsh-toolbox sign hash --file setup.exe --algo all --json",
                                 "dsh-toolbox sign hash --file setup.exe --feed latest.yml --json" });

            Registry.Add("sign.motw", "查看/移除文件的 Zone.Identifier（Mark of the Web 下载来源标记）",
                "sign motw --file <f> [--remove --dry-run|--yes]",
                RunMotw,
                aliases: new[] { "motw" },
                examples: new[] { "dsh-toolbox sign motw --file setup.exe --json" });
        }

        static string ResolveFile(Ctx ctx, bool required)
        {
            string f = ctx.Get("file") ?? ctx.Get("path");
            if (string.IsNullOrEmpty(f) && ctx.Args.Positional.Count > 0) f = ctx.Args.Positional[0];
            if (string.IsNullOrEmpty(f))
            {
                if (required) throw ToolException.Usage("缺少 --file", "例：sign verify --file C:\\Windows\\System32\\notepad.exe");
                return null;
            }
            string full = Fs.Expand(f, ctx.Cwd);
            if (Directory.Exists(full)) throw ToolException.Usage("--file 指向目录：" + full, "请给出具体文件");
            if (!File.Exists(full)) throw ToolException.NotFound("文件不存在：" + full);
            return full;
        }

        /// <summary>吊销检查档位：默认 online（严格）；跳过必须显式 --no-revocation。</summary>
        static string ResolveRevocationMode(Ctx ctx)
        {
            string m = (ctx.Get("revocation") ?? "").Trim().ToLowerInvariant();
            if (m == "")
            {
                if (ctx.Flag("no-revocation")) m = "none";
                else if (ctx.Flag("revocation-cache") || ctx.Flag("cache-only")) m = "cache";
                else m = "online";
            }
            if (m == "offline") m = "cache";
            if (m != "online" && m != "cache" && m != "none")
                throw ToolException.Usage("--revocation 只支持 online|cache|none：" + m);
            return m;
        }

        static List<string> RevocationWarnings(SignVerifyResult r)
        {
            var w = new List<string>();
            if (r.RevocationMode == "none")
                w.Add("已显式跳过吊销检查（--no-revocation）：verdict=true 只代表【签名有效 + 证书链可信 + 文件摘要匹配】，不代表证书未被吊销（trustLevel=" + r.TrustLevel + "）");
            else if (r.RevocationMode == "cache")
                w.Add("吊销检查只用本地缓存（--revocation cache），未联网；miss 的证书会标记 revocationStatusUnknown");
            else if (r.RevocationStatusUnknown)
                w.Add("吊销状态未知（吊销服务器不可达/超时），chain.status 里已标注 RevocationStatusUnknown");
            return w;
        }

        // ---------------------------------------------------------------- verify
        static int RunVerify(Ctx ctx)
        {
            string path = ResolveFile(ctx, true);
            string revMode = ResolveRevocationMode(ctx);
            string publisher = ctx.Get("publisher");
            string publisherRe = ctx.Get("publisher-regex");

            SignVerifyResult r;
            try { r = SignCore.Verify(path, revMode, ctx.Flag("digest"), ctx.Verbose); }
            catch (UnauthorizedAccessException ex) { throw ToolException.Denied("无权限读取：" + path + " —— " + ex.Message); }

            bool? publisherMatched = null;
            if (!string.IsNullOrEmpty(publisher))
                publisherMatched = r.SignerSubject != null &&
                                   r.SignerSubject.IndexOf(publisher, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!string.IsNullOrEmpty(publisherRe))
            {
                Regex re = SysProcs.Compile(publisherRe, "--publisher-regex");
                bool m = r.SignerSubject != null && re.IsMatch(r.SignerSubject);
                publisherMatched = publisherMatched.HasValue ? (publisherMatched.Value && m) : m;
            }

            bool verdict = r.Verdict;
            string reason = r.Reason;
            if (verdict && publisherMatched.HasValue && !publisherMatched.Value)
            {
                verdict = false;
                reason = "签名有效，但签名者与 --publisher 断言不匹配：期望包含 \"" + publisher + "\"，实际 " + (r.SignerSubject ?? "(未知)");
            }

            bool nowBeyondExpiry = r.SignerNotAfter.HasValue && DateTime.Now > r.SignerNotAfter.Value;
            bool timestampCoversExpiry = r.Timestamped && r.TimestampTime.HasValue &&
                                         r.SignerNotBefore.HasValue && r.SignerNotAfter.HasValue &&
                                         r.TimestampTime.Value >= r.SignerNotBefore.Value &&
                                         r.TimestampTime.Value <= r.SignerNotAfter.Value;

            ctx.Out.Result("sign.verify", Json.Obj(
                "file", path,
                "fileSize", r.FileSize,
                "verdict", verdict,
                "reason", reason,
                "status", r.Status,
                "valid", verdict,
                "signatureKind", r.SignatureKind,
                "catalogPath", r.CatalogPath,
                "signed", r.SignatureKind != "none",
                "isPe", r.IsPe,
                "elapsedMs", r.TotalMs,
                "phaseMs", Json.Obj("winVerifyTrust", r.WvtMs, "catalogWinVerifyTrust", r.CatalogWvtMs,
                                    "pe", r.PeMs, "details", r.DetailMs, "chain", r.ChainMs),
                "winVerifyTrust", Json.Obj(
                    "result", "0x" + r.EffectiveWvtResult.ToString("X8", CultureInfo.InvariantCulture),
                    "name", SignCore.WvtNameOf(r.EffectiveWvtResult),
                    "fileResult", "0x" + r.WvtResult.ToString("X8", CultureInfo.InvariantCulture),
                    "fileResultName", r.WvtName,
                    "catalogResult", r.CatalogPath != null ? "0x" + r.CatalogWvtResult.ToString("X8", CultureInfo.InvariantCulture) : null,
                    "catalogResultName", r.CatalogWvtName,
                    "catalogVerified", r.CatalogPath != null ? (object)r.CatalogVerified : null,
                    "attempts", r.WvtAttempts),
                "signer", Json.Obj(
                    "subject", r.SignerSubject,
                    "issuer", r.SignerIssuer,
                    "thumbprint", r.SignerThumbprint,
                    "serial", r.SignerSerial,
                    "notBefore", r.SignerNotBefore,
                    "notAfter", r.SignerNotAfter,
                    "expired", nowBeyondExpiry,
                    "signatureAlgorithm", r.SignatureAlgorithm,
                    "digestAlgorithm", r.DigestAlgorithm,
                    "digestOid", r.DigestOid),
                "timestamp", Json.Obj(
                    "timestamped", r.Timestamped,
                    "kind", r.TimestampKind,
                    "time", r.TimestampTime,
                    "tsa", r.TimestampSubject,
                    "coversCertValidity", timestampCoversExpiry,
                    "error", r.TimestampError),
                "integrity", Json.Obj(
                    "pkcs7Verified", r.Pkcs7Verified,
                    "digestMatches", r.DigestMatches,
                    "digestSkipped", r.DigestSkipped,
                    "digestSkipReason", r.DigestSkipReason,
                    "expectedDigestBase64", r.ExpectedDigestBase64,
                    "computedDigestHex", r.ComputedDigestHex,
                    "pkcs7Error", r.Pkcs7Error),
                "chain", Json.Obj(
                    "valid", r.ChainValid,
                    "revocationMode", r.RevocationMode,
                    "revocationChecked", r.RevocationChecked,
                    "revocationStatusUnknown", r.RevocationStatusUnknown,
                    "trustLevel", r.TrustLevel,
                    "elements", r.ChainElements,
                    "elementCount", r.ChainElements.Count,
                    "status", r.ChainStatus),
                "certTable", Json.Obj("offset", r.CertTableOffset, "size", r.CertTableSize),
                "publisherAssertion", Json.Obj("expected", publisher, "regex", publisherRe, "matched", publisherMatched),
                "errors", r.Errors,
                "columns", new[] { "level", "subject", "status" }), RevocationWarnings(r));

            return verdict ? ExitCodes.Ok : ExitCodes.Error;
        }

        // ---------------------------------------------------------------- chain
        static int RunChain(Ctx ctx)
        {
            string path = ResolveFile(ctx, true);
            string revMode = ResolveRevocationMode(ctx);
            SignVerifyResult r = SignCore.Verify(path, revMode, ctx.Flag("digest"), ctx.Verbose);

            if (r.SignatureKind == "none" || r.SignerSubject == null)
            {
                ctx.Out.Result("sign.chain", Json.Obj(
                    "file", path, "signed", false, "verdict", false,
                    "reason", r.Reason ?? "文件没有可用的签名证书（无内嵌签名，也没有目录签名）",
                    "status", r.Status, "signatureKind", r.SignatureKind,
                    "items", new List<object>(), "elements", new List<object>(), "elementCount", 0,
                    "errors", r.Errors, "elapsedMs", r.TotalMs), RevocationWarnings(r));
                return ExitCodes.Error;
            }

            ctx.Out.Result("sign.chain", Json.Obj(
                "file", path,
                "signed", true,
                "verdict", r.ChainValid,
                "reason", r.ChainValid ? null : "证书链构建未通过：" + string.Join("; ", r.ChainStatus.ToArray()),
                "signatureKind", r.SignatureKind,
                "catalogPath", r.CatalogPath,
                "chainValid", r.ChainValid,
                "revocationMode", r.RevocationMode,
                "revocationChecked", r.RevocationChecked,
                "revocationStatusUnknown", r.RevocationStatusUnknown,
                "trustLevel", r.TrustLevel,
                "items", r.ChainElements,
                "elements", r.ChainElements,
                "elementCount", r.ChainElements.Count,
                "chainStatus", r.ChainStatus,
                "signerSubject", r.SignerSubject,
                "elapsedMs", r.TotalMs,
                "chainMs", r.ChainMs,
                "errors", r.Errors,
                "columns", new[] { "level", "subject", "issuer", "status" }), RevocationWarnings(r));
            return r.ChainValid ? ExitCodes.Ok : ExitCodes.Error;
        }

        // ---------------------------------------------------------------- hash
        static int RunHash(Ctx ctx)
        {
            string path = ResolveFile(ctx, true);
            var algos = new List<string>();
            var wanted = ctx.GetAll("algo");
            if (wanted.Length == 0) algos.Add("sha256");
            else
            {
                foreach (var a in wanted)
                {
                    string x = a.Trim().ToLowerInvariant();
                    if (x == "all") algos.AddRange(new[] { "sha1", "sha256", "sha512" });
                    else algos.Add(x);
                }
            }
            algos = algos.Distinct().ToList();

            string expected = ctx.Get("expected");
            string feed = ctx.Get("feed");
            string feedSha512 = null, feedName = null;
            if (!string.IsNullOrEmpty(feed))
            {
                string fp = Fs.Expand(feed, ctx.Cwd);
                if (!File.Exists(fp)) throw ToolException.NotFound("--feed 文件不存在：" + fp);
                var lines = File.ReadAllLines(fp);
                string curUrl = null, topSha = null, topPath = null;
                var pairs = new List<KeyValuePair<string, string>>();
                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (line.StartsWith("url:", StringComparison.OrdinalIgnoreCase)) curUrl = line.Substring(4).Trim().Trim('"', '\'');
                    else if (line.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(5).Trim().Trim('"', '\'');
                        if (topPath == null) topPath = v;
                    }
                    else if (line.StartsWith("sha512:", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(7).Trim().Trim('"', '\'');
                        if (topSha == null) topSha = v;
                        if (curUrl != null) { pairs.Add(new KeyValuePair<string, string>(curUrl, v)); curUrl = null; }
                    }
                }
                string fileName = Path.GetFileName(path);
                var pick = pairs.FirstOrDefault(p => string.Equals(Path.GetFileName(p.Key), fileName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(pick.Key)) { feedSha512 = pick.Value; feedName = pick.Key; }
                else if (pairs.Count > 0) { feedSha512 = pairs[0].Value; feedName = pairs[0].Key; }
                else { feedSha512 = topSha; feedName = topPath; }

                if (expected == null) expected = feedSha512;
                if (feedName != null && !string.Equals(Path.GetFileName(feedName), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                    ctx.Out.Warn("feed 里的文件名与目标不一致：feed=" + feedName + " file=" + Path.GetFileName(path));
            }

            var items = new List<object>();
            bool anyMatch = false;
            bool anyExpected = !string.IsNullOrEmpty(expected);
            foreach (var algo in algos)
            {
                ctx.ThrowIfCancelled();
                string hex;
                var sw = Stopwatch.StartNew();
                try { hex = Fs.HashFile(path, algo, ctx.Cancel); }
                catch (UnauthorizedAccessException ex) { throw ToolException.Denied("无权限读取：" + path + " —— " + ex.Message); }
                catch (Exception ex) { throw new ToolException("E_IO", "计算 " + algo + " 失败：" + ex.Message, null, ExitCodes.Error); }
                sw.Stop();
                string b64 = null;
                try { b64 = Convert.ToBase64String(HexToBytes(hex)); } catch { }
                bool? match = null;
                if (anyExpected) { match = HashMatches(expected, hex, algo); if (match.Value) anyMatch = true; }
                items.Add(Json.Obj("algo", algo, "hex", hex, "base64", b64, "expected", expected,
                                   "match", match, "elapsedMs", sw.ElapsedMilliseconds));
            }

            bool? verdict = anyExpected ? (bool?)anyMatch : null;
            ctx.Out.Result("sign.hash", Json.Obj(
                "file", path,
                "fileSize", new FileInfo(path).Length,
                "verdict", verdict,
                "reason", anyExpected && !anyMatch ? "哈希与 --expected 不匹配" : null,
                "items", items,
                "count", items.Count,
                "expected", expected,
                "expectedSource", !string.IsNullOrEmpty(feed) ? "feed" : (!string.IsNullOrEmpty(expected) ? "cli" : null),
                "feed", feed,
                "feedFile", feedName,
                "feedSha512", feedSha512,
                "match", anyExpected ? (object)anyMatch : null,
                "columns", new[] { "algo", "hex", "match" }));

            if (anyExpected && !anyMatch) return ExitCodes.Error;
            return ExitCodes.Ok;
        }

        static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return new byte[0];
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        /// <summary>比对 --expected：支持 hex、base64、以及 "sha512-&lt;base64&gt;" 形式。</summary>
        public static bool HashMatches(string expected, string hashHex, string algo)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(hashHex)) return false;
            string e = expected.Trim();
            int dash = e.IndexOf('-');
            if (dash > 0 && dash < 16)
            {
                string prefix = e.Substring(0, dash).ToLowerInvariant().Replace("-", "");
                if (prefix.StartsWith("sha") || prefix.StartsWith("md5")) e = e.Substring(dash + 1).Trim();
            }
            if (string.Equals(e, hashHex, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.Length % 4 == 0 && e.Length >= 16 && e.IndexOf(' ') < 0 && !e.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bytes = Convert.FromBase64String(e);
                    if (string.Equals(Fs.ToHex(bytes), hashHex, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
            return false;
        }

        // ---------------------------------------------------------------- motw
        const string ZoneStream = ":Zone.Identifier";

        static int RunMotw(Ctx ctx)
        {
            var files = new List<string>();
            files.AddRange(ctx.GetAll("file"));
            string single = ctx.Get("file");
            if (files.Count == 0 && !string.IsNullOrEmpty(single)) files.Add(single);
            if (files.Count == 0) files.AddRange(ctx.Args.Positional);
            if (files.Count == 0) throw ToolException.Usage("缺少 --file", "例：sign motw --file setup.exe --json");

            bool remove = ctx.Flag("remove") || ctx.Flag("delete");
            var resolved = new List<string>();
            foreach (var f in files)
            {
                string full = Fs.Expand(f, ctx.Cwd);
                if (!File.Exists(full)) throw ToolException.NotFound("文件不存在：" + full);
                resolved.Add(full);
            }

            if (!remove)
            {
                var items = new List<object>();
                foreach (var full in resolved)
                {
                    var zone = ReadZone(full);
                    items.Add(Json.Obj(
                        "file", full,
                        "hasMotw", zone != null,
                        "zoneId", zone != null && zone.ContainsKey("ZoneId") ? zone["ZoneId"] : null,
                        "referrerUrl", zone != null && zone.ContainsKey("ReferrerUrl") ? zone["ReferrerUrl"] : null,
                        "hostUrl", zone != null && zone.ContainsKey("HostUrl") ? zone["HostUrl"] : null,
                        "raw", zone));
                }
                ctx.Out.Result("sign.motw", Json.Obj(
                    "items", items, "count", items.Count, "action", "view",
                    "columns", new[] { "file", "hasMotw", "zoneId", "hostUrl" }));
                return ExitCodes.Ok;
            }

            if (!ctx.ConfirmDestructive("移除 Mark-of-the-Web（Zone.Identifier）：" + resolved.Count + " 个文件"))
            {
                var plan = resolved.Select(f => (object)Json.Obj("file", f, "action", "delete Zone.Identifier",
                                                                 "hasMotw", ReadZone(f) != null)).ToList();
                ctx.Out.Result("sign.motw", Json.Obj(
                    "dryRun", true, "plan", plan, "items", plan, "count", plan.Count, "action", "remove",
                    "columns", new[] { "file", "action", "hasMotw" }));
                return ExitCodes.Ok;
            }

            var results = new List<object>();
            var failures = new List<object>();
            foreach (var full in resolved)
            {
                ctx.ThrowIfCancelled();
                var rec = Json.Obj("file", full, "removed", false, "error", null);
                try
                {
                    File.Delete(full + ZoneStream);
                    rec["removed"] = true;
                }
                catch (UnauthorizedAccessException ex)
                {
                    rec["error"] = ex.Message;
                    failures.Add(Json.Obj("file", full, "error", "拒绝访问：" + ex.Message));
                }
                catch (Exception ex)
                {
                    rec["error"] = ex.Message;
                    failures.Add(Json.Obj("file", full, "error", ex.Message));
                }
                rec["stillHasMotw"] = ReadZone(full) != null;
                results.Add(rec);
            }

            ctx.Out.Result("sign.motw", Json.Obj(
                "action", "remove",
                "items", results,
                "count", results.Count,
                "removedCount", results.OfType<Dictionary<string, object>>().Count(o => Convert.ToBoolean(o["removed"])),
                "failures", failures,
                "columns", new[] { "file", "removed", "stillHasMotw", "error" }));
            return failures.Count == 0 ? ExitCodes.Ok : ExitCodes.Partial;
        }

        static Dictionary<string, string> ReadZone(string path)
        {
            try
            {
                if (!File.Exists(path + ZoneStream)) return null;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(path + ZoneStream))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                return d.Count > 0 ? d : null;
            }
            catch { return null; }
        }
    }
}
