// ============================================================================
// FindYou v5 —— Windows 本地双向文件互传工具
//
// 架构：
//   1) 局域网发现：UDP 广播（按真实网卡逐个发包，公告携带全部真实 IP，
//      对端逐个尝试直连 —— 修复虚拟网卡/多网卡导致的发现失败）
//   2) 云端自动匹配：内置 MQTT over WebSocket 客户端（手写协议，连接公共
//      broker），按"相同公网出口 IP"自动分组配对，零部署；支持"配对码"
//      跨网络匹配
//   3) 自托管发现（可选）：配合 findyou-relay.js + relay.html（仅发现与信令）
//   4) 文件传输：原生 HTTP 直传优先，原生 WebRTC DataChannel 回退；
//      文件字节始终在两台设备之间传输
//
// 对外接口：/api/info /api/connect/signal /api/shared /api/file /api/push/* /api/transfer/*
// 对内（仅 127.0.0.1 + 会话令牌）：/api/local/*
//
// 编译：build.bat（Windows 自带 .NET Framework 编译器）
// 数据：%LOCALAPPDATA%\FindYou\
// 参数：/port= /data= /name= /noui
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;

namespace FindYou
{
    // ------------------------------------------------------------------ 入口
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            WindowRuntime.Register();
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            AppConfig cfg = AppConfig.Load(args);
            Log.Init(cfg.DataDir);
            Log.W("===== FindYou v5 启动 设备=" + cfg.DeviceName + " tcp=" + cfg.TcpPort + " udp=" + cfg.UdpPort
                + " 本机IP=" + Util.PrimaryIp() + " 全部IP=" + string.Join(",", Util.RealIPs().ToArray()) + " =====");

            bool createdNew;
            Mutex mtx = new Mutex(true, "FindYou_" + Util.Hash(cfg.DataDir.ToLowerInvariant()), out createdNew);
            if (!createdNew)
            {
                if (!cfg.AutoOpenUi) return;
                try
                {
                    using (EventWaitHandle show = EventWaitHandle.OpenExisting(Util.ShowEventName(cfg.DataDir))) show.Set();
                    return;
                }
                catch (WaitHandleCannotBeOpenedException) { }
                MessageBox.Show("检测到另一个位置或旧版本的 FindYou 仍在后台运行。\n\n请从任务栏托盘彻底退出旧程序，再打开当前文件。",
                    "FindYou 无法启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Log.W("异常: " + e.Exception);
            };
            Application.Run(new TrayApp(cfg, mtx));
        }
    }

    // ------------------------------------------------------------------ 日志
    static class Log
    {
        static object gate = new object();
        static string dir = "";
        public static void Init(string d) { dir = d; try { Directory.CreateDirectory(d); } catch { } }
        public static void W(string msg)
        {
            try
            {
                lock (gate)
                {
                    if (dir.Length == 0) return;
                    string p = Path.Combine(dir, "findyou.log");
                    if (File.Exists(p) && new FileInfo(p).Length > 300 * 1024)
                    {
                        string old = p + ".old";
                        try { if (File.Exists(old)) File.Delete(old); } catch { }
                        try { File.Move(p, old); } catch { }
                    }
                    File.AppendAllText(p, DateTime.Now.ToString("MM-dd HH:mm:ss ") + msg + "\r\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ JSON 序列化（仅输出）
    static class Json
    {
        public static string Of(object o)
        {
            StringBuilder sb = new StringBuilder(256);
            Write(sb, o);
            return sb.ToString();
        }
        static void Write(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { WriteStr(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is int) { sb.Append(((int)o).ToString()); return; }
            if (o is long) { sb.Append(((long)o).ToString()); return; }
            if (o is Dictionary<string, object>)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteStr(sb, kv.Key); sb.Append(':'); Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (o is IEnumerable<object>)
            {
                sb.Append('[');
                bool first2 = true;
                foreach (object x in (IEnumerable<object>)o)
                {
                    if (!first2) sb.Append(',');
                    first2 = false;
                    Write(sb, x);
                }
                sb.Append(']');
                return;
            }
            WriteStr(sb, o.ToString());
        }
        static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
        public static Dictionary<string, object> D(params object[] kv)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }

    // ------------------------------------------------------------------ 工具
    static class Util
    {
        public static string UrlEnc(string s) { return s == null ? "" : Uri.EscapeDataString(s); }
        public static string UrlDec(string s)
        {
            try { return Uri.UnescapeDataString((s ?? "").Replace('+', ' ')); }
            catch { return s ?? ""; }
        }
        public static string Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
        public static string FormatSize(long b)
        {
            if (b < 0) return "";
            double d = b;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (d >= 1024 && i < 4) { d /= 1024; i++; }
            if (i == 0) return b + " B";
            return d.ToString("0.#") + " " + u[i];
        }
        public static string FormatTime(DateTime t)
        {
            DateTime now = DateTime.Now;
            if (t.Date == now.Date) return "今天 " + t.ToString("HH:mm");
            if (t.Date == now.Date.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
            if (t.Year == now.Year) return t.ToString("MM-dd HH:mm");
            return t.ToString("yyyy-MM-dd HH:mm");
        }
        public static string ContentType(string ext)
        {
            string e = (ext ?? "").ToLowerInvariant();
            switch (e)
            {
                case ".txt": case ".log": case ".md": case ".ini": case ".bat": case ".sh":
                case ".cs": case ".h": case ".c": case ".cpp": case ".java": case ".py":
                case ".xml": case ".yaml": case ".csv":
                    return "text/plain; charset=utf-8";
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css";
                case ".js": return "text/javascript";
                case ".json": return "application/json";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                case ".ico": return "image/x-icon";
                case ".svg": return "image/svg+xml";
                case ".pdf": return "application/pdf";
                case ".mp4": case ".m4v": return "video/mp4";
                case ".mkv": return "video/x-matroska";
                case ".avi": return "video/x-msvideo";
                case ".mov": return "video/quicktime";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".flac": return "audio/flac";
                case ".m4a": return "audio/mp4";
                case ".zip": return "application/zip";
                case ".7z": return "application/x-7z-compressed";
                case ".rar": return "application/x-rar-compressed";
                case ".doc": case ".docx": return "application/msword";
                case ".xls": case ".xlsx": return "application/vnd.ms-excel";
                case ".ppt": case ".pptx": return "application/vnd.ms-powerpoint";
                default: return "application/octet-stream";
            }
        }
        public static string SanitizeName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "shared";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in n) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "shared" : r;
        }

        public static string UniquePath(string dirPath, string name)
        {
            string full = Path.Combine(dirPath, name);
            if (!File.Exists(full) && !Directory.Exists(full)) return full;
            string ext = Path.GetExtension(name);
            string stem = Path.GetFileNameWithoutExtension(name);
            for (int i = 1; i < 1000; i++)
            {
                string cand = Path.Combine(dirPath, stem + " (" + i + ")" + ext);
                if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
            }
            return Path.Combine(dirPath, Guid.NewGuid().ToString("N") + ext);
        }

        // ---------------- 真实网卡识别（排除 TUN/虚拟/APIPA 地址） ----------------
        public class IpEntry { public string Ip; public string Mask; public int Pri; }
        static object netGate = new object();
        static List<IpEntry> cachedNics;
        static DateTime nicTime = DateTime.MinValue;

        static bool UsableIp(IPAddress a)
        {
            if (a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            if (b[0] == 127 || b[0] == 169) return false;                       // 回环 / APIPA
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;        // 198.18/15 基准网段（Clash TUN 等）
            return true;
        }
        static int PriOf(byte[] b)
        {
            if (b[0] == 192 && b[1] == 168) return 0;
            if (b[0] == 10) return 0;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return 0;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return 1;             // CGNAT/Tailscale
            return 2;
        }
        public static List<IpEntry> RealNics()
        {
            lock (netGate)
            {
                if (cachedNics != null && (DateTime.Now - nicTime).TotalSeconds < 15) return cachedNics;
                List<IpEntry> list = new List<IpEntry>();
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (!UsableIp(ua.Address)) continue;
                            byte[] raw = ua.Address.GetAddressBytes();
                            string mask = ua.IPv4Mask != null ? ua.IPv4Mask.ToString() : "255.255.255.0";
                            list.Add(new IpEntry { Ip = ua.Address.ToString(), Mask = mask, Pri = PriOf(raw) });
                        }
                    }
                }
                catch (Exception ex) { Log.W("网卡枚举失败 " + ex.Message); }
                list.Sort(delegate(IpEntry a, IpEntry b) { return a.Pri.CompareTo(b.Pri); });
                cachedNics = list;
                nicTime = DateTime.Now;
                return list;
            }
        }
        public static List<string> RealIPs()
        {
            List<string> l = new List<string>();
            foreach (IpEntry e in RealNics()) l.Add(e.Ip);
            return l;
        }
        public static string PrimaryIp()
        {
            List<IpEntry> l = RealNics();
            return l.Count > 0 ? l[0].Ip : "127.0.0.1";
        }

        static readonly string[] FN_A = { "闪电", "快乐", "勇敢", "机灵", "温柔", "跳跳", "闪闪", "呼呼", "悄悄", "咕咕" };
        static readonly string[] FN_B = { "小猫", "小狗", "小熊", "狐狸", "海豚", "熊猫", "云雀", "刺猬", "水獭", "兔子" };
        public static string FriendlyName()
        {
            Random r = new Random();
            return FN_A[r.Next(FN_A.Length)] + FN_B[r.Next(FN_B.Length)];
        }

        public static Dictionary<string, string> ParseQuery(string q)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(q)) return d;
            foreach (string pair in q.Split('&'))
            {
                if (pair.Length == 0) continue;
                int i = pair.IndexOf('=');
                string k, v;
                if (i < 0) { k = UrlDec(pair); v = ""; }
                else { k = UrlDec(pair.Substring(0, i)); v = UrlDec(pair.Substring(i + 1)); }
                d[k] = v;
            }
            return d;
        }

        // ---------------- 开机自启 ----------------
        public static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("FindYou", "\"" + Application.ExecutablePath + "\" /noui");
                    else k.DeleteValue("FindYou", false);
                }
            }
            catch (Exception ex) { Log.W("开机自启设置失败 " + ex.Message); }
        }
        public static bool GetAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    return k != null && k.GetValue("FindYou") != null;
                }
            }
            catch { return false; }
        }

        public static string ShowEventName(string dataDir)
        {
            string exe;
            try { exe = Path.GetFullPath(Application.ExecutablePath); }
            catch { exe = Application.ExecutablePath; }
            return "FindYou_Show_V2_" + Hash(dataDir.ToLowerInvariant() + "|" + exe.ToLowerInvariant());
        }

        public static void UpdateAutoStartCommand(string dataDir)
        {
            try
            {
                string defaultData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FindYou");
                if (!string.Equals(Path.GetFullPath(dataDir).TrimEnd('\\'), Path.GetFullPath(defaultData).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null && k.GetValue("FindYou") != null)
                        k.SetValue("FindYou", "\"" + Application.ExecutablePath + "\" /noui");
                }
            }
            catch { }
        }

        // ---------------- 防火墙（首次运行自动放行） ----------------
        static bool QueryRule(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + name + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    return outp != null && outp.Contains(name);
                }
            }
            catch { return false; }
        }
        static void FirewallNames(out string tcp, out string udp)
        {
            string exe;
            try { exe = Path.GetFullPath(Application.ExecutablePath); }
            catch { exe = Application.ExecutablePath; }
            string tag = Hash(exe.ToLowerInvariant());
            tcp = "FindYou " + tag + " TCP";
            udp = "FindYou " + tag + " UDP";
        }

        public static bool FirewallRulesReady()
        {
            string tcp, udp;
            FirewallNames(out tcp, out udp);
            return QueryRule(tcp) && QueryRule(udp);
        }

        public static bool EnsureFirewallRules()
        {
            FirewallAutoDone = false;
            try
            {
                string exe = Application.ExecutablePath;
                string nT, nU;
                FirewallNames(out nT, out nU);
                bool tcpReady = QueryRule(nT), udpReady = QueryRule(nU);
                if (tcpReady && udpReady) { FirewallAutoDone = true; Log.W("当前程序的防火墙规则已存在"); return true; }
                bool ok = (tcpReady || RunElevated("netsh", "advfirewall firewall add rule name=\"" + nT + "\" dir=in action=allow program=\"" + exe + "\" protocol=TCP enable=yes"))
                       && (udpReady || RunElevated("netsh", "advfirewall firewall add rule name=\"" + nU + "\" dir=in action=allow program=\"" + exe + "\" protocol=UDP enable=yes"));
                FirewallAutoDone = ok && QueryRule(nT) && QueryRule(nU);
                Log.W("防火墙自动放行 " + (FirewallAutoDone ? "完成" : "被取消或失败"));
                return FirewallAutoDone;
            }
            catch (Exception ex) { Log.W("防火墙自动放行失败 " + ex.Message); return false; }
        }
        public static bool FirewallAutoDone;
        public static bool RunElevated(string exe, string args)
        {
            try
            {
                using (Process pr = Process.Start(new ProcessStartInfo(exe, args)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (pr == null || !pr.WaitForExit(20000)) return false;
                    return pr.ExitCode == 0;
                }
            }
            catch (Exception ex) { Log.W("提权执行失败 " + ex.Message); return false; }
        }
    }

    // ------------------------------------------------------------------ 配置
    class ShareItem
    {
        public Guid Id;
        public string Path;
    }

    class AppConfig
    {
        public string DataDir = "";
        public string PeerId = Guid.NewGuid().ToString("N");
        public string DeviceName = "";
        public int TcpPort = 52718;
        public int UdpPort = 52719;
        public bool SharingEnabled = true;
        public bool AutoOpenUi = true;
        public string ApiToken = "";       // 仅用于自动化诊断，不写入配置或日志
        public string DownloadDir = "";
        public List<ShareItem> SharedItems = new List<ShareItem>();
        public bool CloudEnabled = true;      // 云端自动匹配（公共 MQTT）
        public string RoomCode = "";          // 配对码（跨网络）
        public string RelayUrl = "";          // 中转服务器（findyou-relay.js）
        public bool RelayFileRelay = false;   // 文件经服务器中转（默认关，省服务器带宽）
        public string RelayAuth = Guid.NewGuid().ToString("N");
        public bool AutoAcceptPush = true;    // 自动接收对方投送的文件
        public bool FirewallDone = false;

        string FilePath { get { return System.IO.Path.Combine(DataDir, "config.txt"); } }

        public static AppConfig Load(string[] args)
        {
            AppConfig c = new AppConfig();
            c.DataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FindYou");
            c.DownloadDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FindYou");

            string dataArg = null, nameArg = null;
            int portArg = 0, udpArg = 0;
            foreach (string raw in args)
            {
                string a = raw.Trim();
                string la = a.ToLowerInvariant();
                if (la.StartsWith("/data=")) dataArg = a.Substring(6);
                else if (la.StartsWith("/port=")) int.TryParse(a.Substring(6), out portArg);
                else if (la.StartsWith("/udp=")) int.TryParse(a.Substring(5), out udpArg);
                else if (la.StartsWith("/name=")) nameArg = a.Substring(6);
                else if (la.StartsWith("/apitoken="))
                {
                    string token = a.Substring(10);
                    if (token.Length == 32 && token.All(Uri.IsHexDigit)) c.ApiToken = token.ToLowerInvariant();
                }
                else if (la == "/noui") c.AutoOpenUi = false;
            }
            if (!string.IsNullOrEmpty(dataArg)) c.DataDir = dataArg;
            try { Directory.CreateDirectory(c.DataDir); } catch { }

            // 首次运行给一个短昵称（可在设置里改名）
            bool fresh = !File.Exists(System.IO.Path.Combine(c.DataDir, "config.txt"));
            if (fresh) c.DeviceName = Util.FriendlyName();
            else c.DeviceName = Environment.MachineName;

            try
            {
                if (File.Exists(c.FilePath))
                {
                    foreach (string line0 in File.ReadAllLines(c.FilePath, Encoding.UTF8))
                    {
                        string line = line0.Trim();
                        if (line.Length == 0) continue;
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i), v = line.Substring(i + 1);
                        if (k == "id") { if (v.Length > 8) c.PeerId = v; }
                        else if (k == "name") c.DeviceName = Util.UrlDec(v);
                        else if (k == "tcp") int.TryParse(v, out c.TcpPort);
                        else if (k == "udp") int.TryParse(v, out c.UdpPort);
                        else if (k == "sharing") c.SharingEnabled = v != "0";
                        else if (k == "dl") { string dd = Util.UrlDec(v); if (dd.Length > 0) c.DownloadDir = dd; }
                        else if (k == "cloud") c.CloudEnabled = v != "0";
                        else if (k == "room") c.RoomCode = Util.UrlDec(v);
                        else if (k == "relay") c.RelayUrl = Util.UrlDec(v);
                        else if (k == "frelay") c.RelayFileRelay = false;
                        else if (k == "apush") c.AutoAcceptPush = v != "0";
                        else if (k == "fw") c.FirewallDone = v == "1";
                        else if (k == "share")
                        {
                            int pi = v.IndexOf('|');
                            if (pi > 0)
                            {
                                Guid g;
                                if (Guid.TryParse(v.Substring(0, pi), out g))
                                {
                                    string p = Util.UrlDec(v.Substring(pi + 1));
                                    if (p.Length > 0) c.SharedItems.Add(new ShareItem { Id = g, Path = p });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.W("配置加载失败 " + ex.Message); }

            if (fresh && (c.DeviceName == Environment.MachineName || c.DeviceName.Length == 0))
                c.DeviceName = Util.FriendlyName();
            if (!string.IsNullOrEmpty(nameArg)) c.DeviceName = nameArg;
            if (portArg > 0) c.TcpPort = portArg;
            if (udpArg > 0) c.UdpPort = udpArg;
            return c;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("FindYouConfig2\n");
                sb.Append("id=").Append(PeerId).Append('\n');
                sb.Append("name=").Append(Util.UrlEnc(DeviceName)).Append('\n');
                sb.Append("tcp=").Append(TcpPort).Append('\n');
                sb.Append("udp=").Append(UdpPort).Append('\n');
                sb.Append("sharing=").Append(SharingEnabled ? "1" : "0").Append('\n');
                sb.Append("dl=").Append(Util.UrlEnc(DownloadDir)).Append('\n');
                sb.Append("cloud=").Append(CloudEnabled ? "1" : "0").Append('\n');
                sb.Append("room=").Append(Util.UrlEnc(RoomCode)).Append('\n');
                sb.Append("relay=").Append(Util.UrlEnc(RelayUrl)).Append('\n');
                sb.Append("frelay=").Append(RelayFileRelay ? "1" : "0").Append('\n');
                sb.Append("apush=").Append(AutoAcceptPush ? "1" : "0").Append('\n');
                sb.Append("fw=").Append(FirewallDone ? "1" : "0").Append('\n');
                lock (SharedItems)
                {
                    foreach (ShareItem si in SharedItems)
                        sb.Append("share=").Append(si.Id.ToString()).Append('|').Append(Util.UrlEnc(si.Path)).Append('\n');
                }
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.W("配置保存失败 " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ 数据模型
    class PeerInfo
    {
        public string Id = "";
        public string Name = "";
        public string Ip = "";                          // 首选 IP（兼容显示）
        public List<string> Ips = new List<string>();   // 全部候选 IP（首项优先）
        public int TcpPort;
        public int FileCount;
        public bool Sharing = true;
        public DateTime LastSeen = DateTime.Now;
        public bool Manual;
        public bool Online = true;
        public string Source = "lan";                   // lan / cloud / relay
    }

    class PeerEntry
    {
        public string Name;
        public long Size;
        public DateTime Mtime;
        public bool IsDir;
        public string Token;
    }

    class HistoryEntry
    {
        public string Id = Guid.NewGuid().ToString("N");
        public int Type;              // 0=下载  1=浏览(查看)
        public string Name = "";
        public string Peer = "";
        public long Size;
        public long TransferRate;
        public long TimeFile = DateTime.Now.ToFileTime();
        public string Status = "";
        public int Progress = -1;
        public string LocalPath = "";
        public string Host = "";
        public int Port;
        public string Token = "";
        public DateTime Time { get { try { return DateTime.FromFileTime(TimeFile); } catch { return DateTime.Now; } } }
    }

    class HistoryStore
    {
        object gate = new object();
        public List<HistoryEntry> Items = new List<HistoryEntry>();
        string path;

        public HistoryStore(string dataDir)
        {
            path = System.IO.Path.Combine(dataDir, "history.txt");
            Load();
        }

        void Load()
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (string line0 in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = line0.Trim();
                    if (line.Length == 0) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 10) continue;
                    HistoryEntry e = new HistoryEntry();
                    e.Type = (int)ParseLong(f[0], 0);
                    e.Name = Util.UrlDec(f[1]);
                    e.Size = ParseLong(f[2], 0);
                    e.Peer = Util.UrlDec(f[3]);
                    e.TimeFile = ParseLong(f[4], DateTime.Now.ToFileTime());
                    e.Status = Util.UrlDec(f[5]);
                    e.Progress = (int)ParseLong(f[6], -1);
                    e.LocalPath = Util.UrlDec(f[7]);
                    e.Host = Util.UrlDec(f[8]);
                    e.Port = (int)ParseLong(f[9], 0);
                    e.Token = f.Length > 10 ? Util.UrlDec(f[10]) : "";
                    Items.Add(e);
                }
            }
            catch (Exception ex) { Log.W("历史记录加载失败 " + ex.Message); }
        }

        static long ParseLong(string s, long def)
        {
            long v; if (long.TryParse(s, out v)) return v; return def;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                lock (gate)
                {
                    foreach (HistoryEntry e in Items)
                    {
                        sb.Append(e.Type).Append('\t').Append(Util.UrlEnc(e.Name)).Append('\t').Append(e.Size)
                          .Append('\t').Append(Util.UrlEnc(e.Peer)).Append('\t').Append(e.TimeFile)
                          .Append('\t').Append(Util.UrlEnc(e.Status)).Append('\t').Append(e.Progress)
                          .Append('\t').Append(Util.UrlEnc(e.LocalPath)).Append('\t').Append(Util.UrlEnc(e.Host))
                          .Append('\t').Append(e.Port).Append('\t').Append(Util.UrlEnc(e.Token)).Append('\n');
                    }
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.W("历史记录保存失败 " + ex.Message); }
        }

        public HistoryEntry Add(HistoryEntry e)
        {
            lock (gate) { Items.Insert(0, e); if (Items.Count > 2000) Items.RemoveRange(2000, Items.Count - 2000); }
            Save();
            return e;
        }
        public HistoryEntry Find(string id)
        {
            lock (gate) { foreach (HistoryEntry e in Items) if (e.Id == id) return e; }
            return null;
        }
        public void Clear(int type)
        {
            lock (gate) { for (int i = Items.Count - 1; i >= 0; i--) if (Items[i].Type == type) Items.RemoveAt(i); }
            Save();
        }
        public void Remove(string id)
        {
            lock (gate) { for (int i = Items.Count - 1; i >= 0; i--) if (Items[i].Id == id) Items.RemoveAt(i); }
            Save();
        }
        public void Touch(HistoryEntry e) { Save(); }
        public List<HistoryEntry> Snapshot()
        {
            lock (gate) { return new List<HistoryEntry>(Items); }
        }
    }

    // ------------------------------------------------------------------ 发现服务（UDP 广播）
    class DiscoveryService
    {
        AppConfig cfg;
        Socket sock;
        Dictionary<string, PeerInfo> peers = new Dictionary<string, PeerInfo>();
        object gate = new object();
        volatile bool stop;

        public DiscoveryService(AppConfig c) { cfg = c; }

        public void Start()
        {
            int udp = cfg.UdpPort;
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    sock.Bind(new IPEndPoint(IPAddress.Any, udp));
                    cfg.UdpPort = udp;
                    break;
                }
                catch
                {
                    try { if (sock != null) sock.Close(); } catch { }
                    sock = null; udp++;
                }
            }
            if (sock == null) { Log.W("UDP 端口绑定失败，局域网自动发现不可用"); return; }
            sock.EnableBroadcast = true;
            StartThread(RecvLoop);
            StartThread(AnnounceLoop);
            StartThread(ProbeLoop);
            Log.W("发现服务已启动 udp=" + cfg.UdpPort);
        }

        static void StartThread(ThreadStart ts)
        {
            Thread t = new Thread(ts);
            t.IsBackground = true;
            t.Start();
        }

        string AnnounceText()
        {
            string ips = string.Join(",", Util.RealIPs().ToArray());
            return "FINDYOU2|id=" + cfg.PeerId + "|name=" + Util.UrlEnc(cfg.DeviceName) + "|tcp=" + cfg.TcpPort
                 + "|ips=" + Util.UrlEnc(ips)
                 + "|files=" + (cfg.SharingEnabled ? cfg.SharedItems.Count : 0)
                 + "|share=" + (cfg.SharingEnabled ? "1" : "0");
        }

        void AnnounceLoop()
        {
            Send();
            int n = 0;
            while (!stop)
            {
                Thread.Sleep(3000);
                if (stop) break;
                Send();
                n++;
                if (n % 3 == 0) Cleanup();
            }
        }

        // 按真实网卡逐个发包：保证广播包源地址 = 该网卡真实 IP
        void Send()
        {
            string pkt = AnnounceText();
            byte[] b = Encoding.UTF8.GetBytes(pkt);
            foreach (Util.IpEntry nic in Util.RealNics())
            {
                Socket s = null;
                try
                {
                    s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.Bind(new IPEndPoint(IPAddress.Parse(nic.Ip), 0));
                    s.EnableBroadcast = true;
                    byte[] raw = IPAddress.Parse(nic.Ip).GetAddressBytes();
                    byte[] mraw = IPAddress.Parse(nic.Mask).GetAddressBytes();
                    uint ip = ((uint)raw[0] << 24) | ((uint)raw[1] << 16) | ((uint)raw[2] << 8) | raw[3];
                    uint mask = ((uint)mraw[0] << 24) | ((uint)mraw[1] << 16) | ((uint)mraw[2] << 8) | mraw[3];
                    uint bc = ip | ~mask;
                    try { s.SendTo(b, new IPEndPoint(new IPAddress((long)bc), cfg.UdpPort)); } catch { }
                    try { s.SendTo(b, new IPEndPoint(IPAddress.Broadcast, cfg.UdpPort)); } catch { }
                }
                catch { }
                finally { try { if (s != null) s.Close(); } catch { } }
            }
        }

        public void SendQuery() { if (sock != null) Send(); }

        void RecvLoop()
        {
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            byte[] buf = new byte[8192];
            while (!stop)
            {
                try
                {
                    int n = sock.ReceiveFrom(buf, ref ep);
                    HandlePacket(Encoding.UTF8.GetString(buf, 0, n), (IPEndPoint)ep);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { }
                catch { }
            }
        }

        void HandlePacket(string text, IPEndPoint ep)
        {
            bool v2 = text.StartsWith("FINDYOU2|");
            if (!v2 && !text.StartsWith("FINDYOU1|")) return;
            string id = null, name = null, ipsStr = "";
            int tcp = 0, files = 0, share = 1;
            foreach (string seg in text.Split('|'))
            {
                int i = seg.IndexOf('=');
                if (i <= 0) continue;
                string k = seg.Substring(0, i), v = seg.Substring(i + 1);
                if (k == "id") id = v;
                else if (k == "name") name = Util.UrlDec(v);
                else if (k == "tcp") int.TryParse(v, out tcp);
                else if (k == "ips") ipsStr = Util.UrlDec(v);
                else if (k == "files") int.TryParse(v, out files);
                else if (k == "share") share = v == "1" ? 1 : 0;
            }
            if (id == null || id == cfg.PeerId || tcp <= 0) return;

            List<string> ips = new List<string>();
            if (ipsStr.Length > 0)
                foreach (string s in ipsStr.Split(',')) if (s.Trim().Length > 0) ips.Add(s.Trim());
            string src = ep.Address.ToString();
            ips.RemoveAll(x => x == src);
            ips.Insert(0, src);   // 广播实际到达的源 IP 可达性最确定，放最前

            UpsertPeer(id, name, ips, tcp, files, share == 1, "lan", false);
        }

        public void UpsertPeer(string id, string name, List<string> ips, int tcp, int files, bool sharing, string source, bool manual)
        {
            lock (gate)
            {
                PeerInfo p;
                bool isNew = !peers.TryGetValue(id, out p);
                if (isNew)
                {
                    p = new PeerInfo();
                    p.Id = id;
                    peers[id] = p;
                    Log.W("发现电脑[" + source + "] " + name + " (" + string.Join(",", ips.ToArray()) + ":" + tcp + ")");
                }
                if (!p.Online) { p.Online = true; Log.W("电脑上线 " + name); }
                p.Name = name; p.Ips = ips ?? new List<string>(); p.TcpPort = tcp;
                p.Ip = p.Ips.Count > 0 ? p.Ips[0] : "";
                p.FileCount = files; p.Sharing = sharing; p.Source = source;
                p.LastSeen = DateTime.Now;
                if (manual) p.Manual = true;
            }
        }

        void Cleanup()
        {
            lock (gate)
            {
                DateTime now = DateTime.Now;
                List<string> rm = new List<string>();
                foreach (KeyValuePair<string, PeerInfo> kv in peers)
                {
                    PeerInfo p = kv.Value;
                    double gone = (now - p.LastSeen).TotalSeconds;
                    if (p.Manual) { if (gone > 25) p.Online = false; }
                    else if (gone > 12) p.Online = false;
                    if (!p.Manual && gone > 150) rm.Add(kv.Key);
                }
                foreach (string k in rm) { Log.W("移除失联电脑 " + peers[k].Name); peers.Remove(k); }
            }
        }

        void ProbeLoop()
        {
            while (!stop)
            {
                Thread.Sleep(15000);
                if (stop) break;
                List<PeerInfo> mans = new List<PeerInfo>();
                lock (gate)
                {
                    foreach (PeerInfo p in peers.Values) if (p.Manual && !p.Online) mans.Add(p);
                }
                foreach (PeerInfo m in mans)
                {
                    try
                    {
                        PeerInfo info = PeerClient.Info(new List<string> { m.Ip }, m.TcpPort);
                        List<string> ips = info.Ips; ips.Insert(0, m.Ip);
                        UpsertPeer(m.Id, info.Name, ips, m.TcpPort, info.FileCount, info.Sharing, m.Source, true);
                    }
                    catch { }
                }
            }
        }

        public string AddManual(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return "请输入对方的 IP 地址";
            string host = text;
            int port = 52718;
            int ci = text.IndexOf(':');
            if (ci > 0)
            {
                host = text.Substring(0, ci).Trim();
                int.TryParse(text.Substring(ci + 1).Trim(), out port);
            }
            if (host.Length == 0) return "IP 地址不正确";
            if (port <= 0 || port > 65535) return "端口不正确";
            try
            {
                PeerInfo info = PeerClient.Info(new List<string> { host }, port);
                List<string> ips = info.Ips; ips.Insert(0, host);
                UpsertPeer(info.Id, info.Name, ips, port, info.FileCount, info.Sharing, "lan", true);
                return null;
            }
            catch (Exception ex)
            {
                Log.W("手动添加失败 " + host + ":" + port + " " + ex.Message);
                return "无法连接 " + host + ":" + port + "。请确认对方已运行 FindYou、端口正确且防火墙已放行";
            }
        }

        public List<PeerInfo> GetPeers()
        {
            lock (gate)
            {
                List<PeerInfo> l = new List<PeerInfo>(peers.Values);
                l.Sort(delegate(PeerInfo a, PeerInfo b)
                {
                    if (a.Online != b.Online) return a.Online ? -1 : 1;
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                });
                return l;
            }
        }

        public PeerInfo Find(string id)
        {
            lock (gate)
            {
                PeerInfo p;
                if (peers.TryGetValue(id, out p)) return p;
                return null;
            }
        }

        public void Stop()
        {
            stop = true;
            try { if (sock != null) sock.Close(); } catch { }
        }
    }

    // ------------------------------------------------------------------ 对端客户端（多 IP 自动尝试）
    class ListResult
    {
        public string Parent = "";
        public List<PeerEntry> Items = new List<PeerEntry>();
        public string Error = "";
    }

    class PeerClient
    {
        public static HttpWebRequest Req(string url)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.Proxy = null;                       // 直连，不走系统代理
            r.Timeout = 4000;
            r.ReadWriteTimeout = 15000;
            r.UserAgent = "FindYou/5";
            return r;
        }

        static Dictionary<string, string> ParseKV(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (text == null) return d;
            foreach (string ln in text.Replace("\r", "").Split('\n'))
            {
                int i = ln.IndexOf('=');
                if (i <= 0) continue;
                d[ln.Substring(0, i)] = ln.Substring(i + 1);
            }
            return d;
        }
        static string Get(Dictionary<string, string> d, string k, string def)
        {
            string v;
            if (d.TryGetValue(k, out v)) return v;
            return def;
        }

        static string ParseIps(string v)
        {
            return (v ?? "").Trim();
        }

        // 依次尝试每个候选 IP，返回第一个成功解析的 KV 文本
        static string GetText(List<string> ips, int port, string pathAndQuery)
        {
            Exception last = null;
            List<string> cand = ips != null ? new List<string>(ips) : new List<string>();
            if (cand.Count == 0) cand.Add("127.0.0.1");
            foreach (string ip in cand)
            {
                try
                {
                    HttpWebRequest req = Req("http://" + ip + ":" + port + pathAndQuery);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string t = sr.ReadToEnd();
                        if (t.Length > 0) { Log.W("直连成功 " + ip + ":" + port + " " + pathAndQuery.Split('?')[0]); return t; }
                    }
                }
                catch (Exception ex) { last = ex; }
            }
            if (last != null) Log.W("直连全部失败 " + string.Join(",", cand.ToArray()) + ":" + port + " : " + last.Message);
            throw last != null ? last : new Exception("无法连接对方");
        }

        public static PeerInfo Info(List<string> ips, int port)
        {
            Dictionary<string, string> kv = ParseKV(GetText(ips, port, "/api/info"));
            PeerInfo p = new PeerInfo();
            p.Id = Get(kv, "id", Guid.NewGuid().ToString("N"));
            p.Name = Util.UrlDec(Get(kv, "name", ""));
            if (p.Name.Length == 0) p.Name = ips != null && ips.Count > 0 ? ips[0] : "未知";
            string ipList = ParseIps(Get(kv, "ips", ""));
            p.Ips = new List<string>();
            foreach (string s in ipList.Split(',')) if (s.Trim().Length > 0) p.Ips.Add(s.Trim());
            p.TcpPort = port;
            int.TryParse(Get(kv, "files", "0"), out p.FileCount);
            p.Sharing = Get(kv, "share", "1") == "1";
            return p;
        }

        public static ListResult List(List<string> ips, int port, string dirToken)
        {
            ListResult r = new ListResult();
            try
            {
                string url = "/api/shared";
                if (!string.IsNullOrEmpty(dirToken)) url = url + "?dir=" + Util.UrlEnc(dirToken);
                string[] lines = GetText(ips, port, url).Replace("\r", "").Split('\n');
                if (lines.Length == 0 || lines[0] != "OK")
                {
                    r.Error = lines.Length > 1 ? ErrText(lines[1]) : "对方响应异常";
                    return r;
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    if (ln.StartsWith("parent=")) r.Parent = Util.UrlDec(ln.Substring(7));
                    else if (ln.StartsWith("item="))
                    {
                        string[] f = ln.Substring(5).Split('|');
                        if (f.Length < 5) continue;
                        PeerEntry it = new PeerEntry();
                        it.Name = Util.UrlDec(f[0]);
                        long.TryParse(f[1], out it.Size);
                        long mt;
                        if (long.TryParse(f[2], out mt)) { try { it.Mtime = DateTime.FromFileTime(mt); } catch { } }
                        it.IsDir = f[3] == "1";
                        it.Token = Util.UrlDec(f[4]);
                        r.Items.Add(it);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.W("列取共享失败 : " + ex.Message);
                r.Error = "无法连接对方（可能已离线）";
            }
            return r;
        }

        public static string ErrText(string code)
        {
            if (code == "SHARING_OFF") return "对方已关闭共享";
            if (code == "GONE") return "对方共享列表已变化，请刷新重试";
            if (code == "NOTFOUND") return "文件不存在或共享已被移除";
            if (code == "NOTDIR") return "该条目不是文件夹";
            if (code == "NEEDCONFIRM") return "对方开启了手动确认投送，请对方在设置里开启自动接收";
            if (code == "BUSY") return "对方正在接收其他文件";
            return "错误: " + code;
        }
    }

    // ------------------------------------------------------------------ 下载管理（直连优先，中转兜底）
    class DownloadManager
    {
        AppConfig cfg;
        HistoryStore hist;
        Action<string, string> balloon;
        RelayService relay;
        DateTime lastSave = DateTime.Now;

        public DownloadManager(AppConfig c, HistoryStore h, Action<string, string> balloonFn, RelayService relaySv)
        {
            cfg = c; hist = h; balloon = balloonFn; relay = relaySv;
        }
        public void SetRelay(RelayService r) { relay = r; }

        public HistoryEntry Start(List<string> ips, int port, string token, string name, long size, int type, string peerName, string peerCid)
        {
            HistoryEntry e = new HistoryEntry();
            e.Type = type;
            e.Name = name;
            e.Size = size;
            e.Peer = peerName;
            e.Host = ips != null && ips.Count > 0 ? ips[0] : "";
            e.Port = port;
            e.Token = token;
            e.Status = type == 0 ? "连接中" : "获取中";
            e.Progress = 0;
            hist.Add(e);
            ThreadPool.QueueUserWorkItem(delegate(object o) { Run((HistoryEntry)o, ips, port, peerCid); }, e);
            return e;
        }

        public int ActiveCount()
        {
            int n = 0;
            foreach (HistoryEntry e in hist.Snapshot())
                if (e.Status == "下载中" || e.Status == "连接中" || e.Status == "获取中" || e.Status == "中转接收中" || e.Status == "中转连接中") n++;
            return n;
        }

        void Run(HistoryEntry e, List<string> ips, int port, string peerCid)
        {
            List<string> cand = ips != null ? new List<string>(ips) : new List<string>();
            if (cand.Count == 0 && e.Host.Length > 0) cand.Add(e.Host);
            foreach (string ip in cand)
            {
                if (RunDirect(e, ip, port)) return;
                if (e.Type == 0) { e.Status = "连接中"; e.Progress = 0; hist.Touch(e); }
            }
            // 直连全部失败 → 中转兜底
            if (cfg.RelayFileRelay && relay != null && relay.Ready() && peerCid != null && peerCid.Length > 0)
            {
                Log.W("直连失败，切换中转下载 " + e.Name);
                relay.RequestDownload(e, peerCid);
                return;
            }
            e.Status = "失败:无法直连对方，请检查 IP、端口和防火墙，或切换同一网络";
            e.Progress = -1;
            hist.Touch(e);
        }

        // 单 IP 直连传输；成功返回 true
        // 把 (ip,port,token) 的文件写到 destFinal（支持 .part 断点续传）
        void DownloadTo(HistoryEntry e, string ip, int port, string token, string destFinal, long sizeHint)
        {
            string part = destFinal + ".part";
            long existing = 0;
            try { if (File.Exists(part)) existing = new FileInfo(part).Length; } catch { }
            if (existing > 0) Log.W("断点续传 " + Path.GetFileName(destFinal) + " 从 " + existing + " 字节继续");

            string url = "http://" + ip + ":" + port + "/api/file?token=" + Util.UrlEnc(token) + "&dl=1";
            HttpWebRequest req = PeerClient.Req(url);
            req.Timeout = 15000;
            req.ReadWriteTimeout = 60000;
            if (existing > 0) req.AddRange(existing);

            long done = existing;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                bool append = resp.StatusCode == HttpStatusCode.PartialContent && existing > 0;
                if (!append) done = 0;
                long total = (append ? existing : 0) + resp.ContentLength;
                if (total <= 0) total = sizeHint;
                if (e.Size < total) e.Size = total;
                e.Host = ip;
                e.Status = e.Type == 0 ? "下载中" : "获取中";
                using (Stream rs = resp.GetResponseStream())
                using (FileStream fs = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[256 * 1024];
                    int n;
                    DateTime lastUi = DateTime.Now;
                    while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        done += n;
                        if ((DateTime.Now - lastUi).TotalMilliseconds > 400)
                        {
                            lastUi = DateTime.Now;
                            if (total > 0) e.Progress = (int)Math.Min(100, done * 100 / total);
                            if ((DateTime.Now - lastSave).TotalSeconds > 2) { lastSave = DateTime.Now; hist.Touch(e); }
                        }
                    }
                }
            }
            if (sizeHint > 0 && new FileInfo(part).Length != sizeHint) throw new IOException("文件未完整接收或源文件已变化，请重试");
            if (File.Exists(destFinal)) destFinal = Util.UniquePath(Path.GetDirectoryName(destFinal), Path.GetFileName(destFinal));
            try { File.Move(part, destFinal); }
            catch { destFinal = Util.UniquePath(Path.GetDirectoryName(destFinal), Path.GetFileName(destFinal)); File.Move(part, destFinal); }
        }

        bool RunDirect(HistoryEntry e, string ip, int port)
        {
            string destDir = e.Type == 0 ? cfg.DownloadDir : Path.Combine(Path.GetTempPath(), "FindYouPreview");
            try
            {
                Directory.CreateDirectory(destDir);
                string final = Util.UniquePath(destDir, e.Name);
                DownloadTo(e, ip, port, e.Token, final, 0);
                e.LocalPath = final;
                e.Progress = 100;
                if (e.Type == 0)
                {
                    e.Status = "已完成";
                    if (balloon != null) balloon("下载完成", e.Name);
                }
                else
                {
                    e.Status = "已打开";
                    try { Process.Start(final); }
                    catch (Exception ex) { e.Status = "已下载(无法自动打开)"; Log.W("预览打开失败 " + ex.Message); }
                }
                Log.W((e.Type == 0 ? "下载完成 " : "查看完成 ") + e.Name + " → " + final);
                hist.Touch(e);
                return true;
            }
            catch (Exception ex)
            {
                Log.W("直连下载失败[" + ip + "] " + e.Name + " : " + ex.Message);
                return false;
            }
        }

        void SetProgress(HistoryEntry e, long done, long total)
        {
            e.Status = e.Type == 0 ? "下载中" : "获取中";
            if (total > 0)
            {
                int pct = (int)(done * 100 / total);
                if (pct > 100) pct = 100;
                e.Progress = pct;
            }
            if ((DateTime.Now - lastSave).TotalSeconds > 2)
            {
                lastSave = DateTime.Now;
                hist.Touch(e);
            }
        }

        class FItem { public string Token; public string Rel; public long Size; }

        // 整个文件夹递归下载（保持目录结构；仅直连）
        public HistoryEntry StartFolder(List<string> ips, int port, string dirToken, string folderName, string peerName, string peerCid)
        {
            HistoryEntry e = new HistoryEntry();
            e.Type = 0;
            e.Name = folderName + "（整个文件夹）";
            e.Peer = peerName;
            e.Host = ips != null && ips.Count > 0 ? ips[0] : "";
            e.Port = port;
            e.Token = dirToken;
            e.Status = "正在列举文件夹";
            e.Progress = 0;
            hist.Add(e);
            ThreadPool.QueueUserWorkItem(delegate(object o) { RunFolder((HistoryEntry)o, ips, port, folderName); }, e);
            return e;
        }

        void RunFolder(HistoryEntry e, List<string> ips, int port, string folderName)
        {
            try
            {
                List<FItem> files = new List<FItem>();
                List<string> folders = new List<string>();
                long total = 0;
                Queue<FItem> dirs = new Queue<FItem>();
                dirs.Enqueue(new FItem { Token = e.Token, Rel = folderName });
                string workIp = ips != null && ips.Count > 0 ? ips[0] : "";
                while (dirs.Count > 0)
                {
                    FItem d = dirs.Dequeue();
                    folders.Add(d.Rel);
                    if (files.Count + folders.Count + dirs.Count > 100000) throw new Exception("文件夹超过 100000 项，请分批下载");
                    ListResult r = null;
                    string err = "";
                    foreach (string ip in ips)
                    {
                        r = PeerClient.List(new List<string> { ip }, port, d.Token);
                        if (r.Error.Length == 0) { workIp = ip; break; }
                        err = r.Error;
                    }
                    if (r == null || r.Error.Length > 0) throw new Exception("列举失败: " + err);
                    foreach (PeerEntry it in r.Items)
                    {
                        if (it.Name.Length == 0 || it.Name != Util.SanitizeName(it.Name) || it.Name == "." || it.Name == "..") throw new Exception("对方返回了无效文件名");
                        string rel = d.Rel + "\\" + it.Name;
                        if (it.IsDir) dirs.Enqueue(new FItem { Token = it.Token, Rel = rel });
                        else { files.Add(new FItem { Token = it.Token, Rel = rel, Size = it.Size }); total += it.Size; }
                    }
                }

                e.Size = total;
                e.Status = "下载中 0/" + files.Count;
                hist.Touch(e);

                string destTop = Util.UniquePath(cfg.DownloadDir, Util.SanitizeName(folderName));
                Directory.CreateDirectory(destTop);
                long doneBytes = 0;
                int idx = 0;
                string skipPrefix = folderName + "\\";
                foreach (string folder in folders)
                {
                    string sub = folder.StartsWith(skipPrefix, StringComparison.OrdinalIgnoreCase) ? folder.Substring(skipPrefix.Length) : "";
                    Directory.CreateDirectory(Path.Combine(destTop, sub));
                }
                foreach (FItem f in files)
                {
                    string relUnder = f.Rel.StartsWith(skipPrefix, StringComparison.OrdinalIgnoreCase) ? f.Rel.Substring(skipPrefix.Length) : f.Rel;
                    string dest = destTop + (relUnder.Length > 0 ? "\\" + relUnder : "");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    DownloadTo(e, workIp, port, f.Token, dest, f.Size);
                    idx++;
                    doneBytes += f.Size;
                    e.LocalPath = destTop;
                    e.Status = "下载中 " + idx + "/" + files.Count;
                    if (total > 0) e.Progress = (int)Math.Min(99, doneBytes * 100 / total);
                    hist.Touch(e);
                }
                e.LocalPath = destTop;
                e.Status = "已完成";
                e.Progress = 100;
                if (balloon != null) balloon("文件夹下载完成", folderName + "（" + files.Count + " 个文件）");
                Log.W("文件夹下载完成 " + folderName + " " + files.Count + " 个文件 → " + destTop);
                hist.Touch(e);
            }
            catch (Exception ex)
            {
                e.Status = "失败:" + ex.Message;
                e.Progress = -1;
                hist.Touch(e);
                Log.W("文件夹下载失败 " + e.Name + " : " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------ MQTT over WebSocket（手写协议，用于云端自动匹配）
    class MqttWsClient
    {
        public Action<string, string> OnPublish;      // topic, payload
        string host; int port; string path; bool tls;
        string clientId = "fy" + Guid.NewGuid().ToString("N").Substring(0, 14);
        List<string> wantTopics = new List<string>();
        List<string> subscribed = new List<string>();
        TcpClient tcp; Stream ns; object wlock = new object();
        volatile bool stop; volatile bool connected;
        Thread loop; int brokerIdx = 0;

        static string[][] Brokers = {
            new string[] { "broker.emqx.io", "8084", "/mqtt", "wss" },
            new string[] { "broker.emqx.io", "8083", "/mqtt", "ws" },
            new string[] { "test.mosquitto.org", "8081", "/", "wss" }
        };

        public bool Connected { get { return connected; } }

        public void Start()
        {
            loop = new Thread(Loop);
            loop.IsBackground = true;
            loop.Start();
        }
        public void Stop()
        {
            stop = true;
            try { if (tcp != null) tcp.Close(); } catch { }
        }
        public void SetTopics(List<string> topics)
        {
            lock (wantTopics)
            {
                foreach (string t in topics)
                    if (!wantTopics.Contains(t)) wantTopics.Add(t);
            }
        }

        void Loop()
        {
            while (!stop)
            {
                string[] bk = Brokers[brokerIdx % Brokers.Length];
                host = bk[0]; port = int.Parse(bk[1]); path = bk[2]; tls = bk[3] == "wss";
                try
                {
                    ConnectAndRun();
                }
                catch (Exception ex)
                {
                    Log.W("云端匹配[" + host + "]断开: " + ex.Message);
                }
                connected = false;
                lock (subscribed) { subscribed.Clear(); }
                try { if (tcp != null) tcp.Close(); } catch { }
                if (stop) break;
                brokerIdx++;
                for (int i = 0; i < 5 && !stop; i++) Thread.Sleep(1000);
            }
        }

        void ConnectAndRun()
        {
            Log.W("云端匹配连接 " + host + ":" + port + " …");
            tcp = new TcpClient();
            IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(8000)) { try { tcp.Close(); } catch { } throw new Exception("连接超时"); }
            tcp.EndConnect(ar);
            tcp.NoDelay = true;
            Stream raw = (Stream)tcp.GetStream();
            if (tls)
            {
                // 公共匿名 broker：允许自签/被代理劫持的证书（公告仅含设备名与 IP）
                SslStream ssl = new SslStream(raw, false,
                    (s, cert, chain, err) => true);
                ssl.AuthenticateAsClient(host);
                raw = ssl;
            }
            ns = raw;

            // WebSocket 握手
            string key = Convert.ToBase64String(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N").Substring(0, 22)));
            string hs = "GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                      + "Sec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: mqtt\r\n\r\n";
            byte[] hsb = Encoding.ASCII.GetBytes(hs);
            ns.Write(hsb, 0, hsb.Length);
            string resp = ReadHttpHead(ns);
            if (!resp.Contains("101")) throw new Exception("WS 握手被拒绝");

            // MQTT CONNECT
            byte[] cidb = Encoding.UTF8.GetBytes(clientId);
            byte[] body = new byte[10 + 2 + cidb.Length];
            body[0] = 0; body[1] = 4; body[2] = (byte)'M'; body[3] = (byte)'Q'; body[4] = (byte)'T'; body[5] = (byte)'T';
            body[6] = 4; body[7] = 0x02; body[8] = 0; body[9] = 60;
            body[10] = (byte)(cidb.Length >> 8); body[11] = (byte)(cidb.Length & 0xff);
            Array.Copy(cidb, 0, body, 12, cidb.Length);
            SendMqtt(0x10, body);

            List<byte> acc = new List<byte>();
            connected = true;
            Log.W("云端匹配已连接 " + host);
            EnsureSubscribed();

            Thread ka = new Thread(KeepAlive);
            ka.IsBackground = true;
            ka.Start();

            DateTime lastRecv = DateTime.Now;
            while (!stop)
            {
                int opcode;
                byte[] payload = ReadFrame(acc, ns, ref lastRecv);
                if (payload == null) continue;
                opcode = lastOpcode;
                HandleWsFrame(opcode, payload, acc);
            }
        }

        int lastOpcode;

        void HandleWsFrame(int opcode, byte[] payload, List<byte> acc)
        {
            if (opcode == 0x8) throw new Exception("WS 关闭");
            if (opcode == 0x9) { SendFrame(0xA, payload); return; }   // ping → pong
            if (opcode == 0xA) return;                                 // pong
            lock (acc)
            {
                acc.AddRange(payload);
                ParseMqtt(acc);
            }
        }

        void ParseMqtt(List<byte> buf)
        {
            while (true)
            {
                if (buf.Count < 2) return;
                int type = (buf[0] >> 4) & 0x0f;
                int remlen = 0, mul = 1, idx = 1, hb;
                do
                {
                    if (idx >= buf.Count) return;
                    hb = buf[idx];
                    remlen += (hb & 0x7f) * mul;
                    mul *= 128;
                    idx++;
                } while ((hb & 0x80) != 0 && idx < 5);
                if (buf.Count < idx + remlen) return;
                byte[] pkt = buf.GetRange(idx, remlen).ToArray();
                buf.RemoveRange(0, idx + remlen);

                if (type == 2 && pkt.Length >= 3)
                if (type == 3 && pkt.Length > 2)   // PUBLISH (qos0)
                {
                    int tl = (pkt[0] << 8) | pkt[1];
                    if (pkt.Length >= 2 + tl)
                    {
                        string topic = Encoding.UTF8.GetString(pkt, 2, tl);
                        string payload = Encoding.UTF8.GetString(pkt, 2 + tl, pkt.Length - 2 - tl);
                        try { if (OnPublish != null) OnPublish(topic, payload); } catch (Exception ex) { Log.W("云端广播处理失败 " + ex.Message); }
                    }
                }
                // CONNACK(2)/SUBACK(9)/PINGRESP(13) 忽略
            }
        }

        void KeepAlive()
        {
            while (!stop)
            {
                for (int i = 0; i < 5 && !stop; i++) Thread.Sleep(1000);
                if (stop || !connected) break;
                try { SendMqtt(0xC0, new byte[0]); } catch { break; }   // PINGREQ
            }
        }

        public void Publish(string topic, string payload)
        {
            if (!connected) return;
            try
            {
                byte[] tb = Encoding.UTF8.GetBytes(topic);
                byte[] pb = Encoding.UTF8.GetBytes(payload);
                byte[] body = new byte[2 + tb.Length + pb.Length];
                body[0] = (byte)(tb.Length >> 8); body[1] = (byte)(tb.Length & 0xff);
                Array.Copy(tb, 0, body, 2, tb.Length);
                Array.Copy(pb, 0, body, 2 + tb.Length, pb.Length);
                SendMqtt(0x30, body);
            }
            catch { }
        }

        // 返回是否真正发出（需已连接）
        bool SubscribeNow(string topic)
        {
            if (!connected) return false;
            try
            {
                byte[] tb = Encoding.UTF8.GetBytes(topic);
                byte[] body = new byte[2 + 2 + tb.Length + 1];
                body[0] = 0; body[1] = 1;   // packet id
                body[2] = (byte)(tb.Length >> 8); body[3] = (byte)(tb.Length & 0xff);
                Array.Copy(tb, 0, body, 4, tb.Length);
                body[4 + tb.Length] = 0;    // qos0
                SendMqtt(0x82, body);
                Log.W("云端订阅 " + topic);
                return true;
            }
            catch { return false; }
        }

        void EnsureSubscribed()
        {
            List<string> want;
            lock (wantTopics) { want = new List<string>(wantTopics); }
            lock (subscribed)
            {
                foreach (string t in want)
                    if (!subscribed.Contains(t))
                    {
                        if (SubscribeNow(t)) subscribed.Add(t);
                    }
            }
        }
        public void PumpSubscriptions() { EnsureSubscribed(); }

        void SendMqtt(int type, byte[] body)
        {
            List<byte> pkt = new List<byte>();
            pkt.Add((byte)type);
            int rem = body.Length;
            do
            {
                byte d = (byte)(rem % 128);
                rem /= 128;
                if (rem > 0) d |= 0x80;
                pkt.Add(d);
            } while (rem > 0);
            pkt.AddRange(body);
            SendFrame(0x2, pkt.ToArray());
        }

        void SendFrame(int opcode, byte[] payload)
        {
            if (ns == null) return;
            lock (wlock)
            {
                List<byte> f = new List<byte>();
                f.Add((byte)(0x80 | opcode));
                int len = payload.Length;
                if (len < 126) f.Add((byte)(0x80 | len));
                else if (len < 65536) { f.Add((byte)(0x80 | 126)); f.Add((byte)(len >> 8)); f.Add((byte)(len & 0xff)); }
                else
                {
                    f.Add((byte)(0x80 | 127));
                    long l = len;
                    for (int i = 7; i >= 0; i--) f.Add((byte)((l >> (8 * i)) & 0xff));
                }
                byte[] mask = new byte[4];
                new Random().NextBytes(mask);
                f.AddRange(mask);
                for (int i = 0; i < payload.Length; i++) f.Add((byte)(payload[i] ^ mask[i & 3]));
                byte[] fb = f.ToArray();
                ns.Write(fb, 0, fb.Length);
                ns.Flush();
            }
        }

        static string ReadHttpHead(Stream s)
        {
            byte[] buf = new byte[8192];
            int len = 0;
            while (len < buf.Length)
            {
                int n = s.Read(buf, len, buf.Length - len);
                if (n <= 0) break;
                len += n;
                for (int i = 0; i + 3 < len; i++)
                    if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                        return Encoding.ASCII.GetString(buf, 0, i);
            }
            return "";
        }

        // 读取一个完整 WS 帧（阻塞）；返回 payload，帧类型记录到 lastOpcode
        byte[] ReadFrame(List<byte> acc, Stream s, ref DateTime lastRecv)
        {
            // 先尝试从已缓冲数据解析
            byte[] r = TryTakeFrame(acc);
            if (r != null) return r;
            byte[] buf = new byte[8192];
            int n = s.Read(buf, 0, buf.Length);
            if (n <= 0) throw new Exception("连接关闭");
            lastRecv = DateTime.Now;
            for (int i = 0; i < n; i++) acc.Add(buf[i]);
            r = TryTakeFrame(acc);
            return r;   // 可能为 null（还不完整）→ 调用方继续循环
        }

        byte[] TryTakeFrame(List<byte> buf)
        {
            if (buf.Count < 2) return null;
            int b0 = buf[0], b1 = buf[1];
            bool masked = (b1 & 0x80) != 0;
            int len = b1 & 0x7f;
            int off = 2;
            if (len == 126) { if (buf.Count < 4) return null; len = (buf[2] << 8) | buf[3]; off = 4; }
            else if (len == 127)
            {
                if (buf.Count < 10) return null;
                long l = 0;
                for (int i = 2; i < 10; i++) l = (l << 8) | buf[i];
                if (l > int.MaxValue) l = int.MaxValue;
                len = (int)l; off = 10;
            }
            int maskLen = masked ? 4 : 0;
            if (buf.Count < off + maskLen + len) return null;
            byte[] payload = buf.GetRange(off + maskLen, len).ToArray();
            if (masked)
            {
                byte[] mk = buf.GetRange(off, 4).ToArray();
                for (int i = 0; i < payload.Length; i++) payload[i] ^= mk[i & 3];
            }
            buf.RemoveRange(0, off + maskLen + len);
            lastOpcode = b0 & 0x0f;
            return payload;
        }
    }

    // ------------------------------------------------------------------ 云端自动匹配服务
    class CloudService
    {
        AppConfig cfg;
        DiscoveryService disc;
        MqttWsClient mq;
        volatile bool stopped;
        string pubIp = "";
        DateTime pubIpTime = DateTime.MinValue;
        DateTime lastPub = DateTime.MinValue;

        public CloudService(AppConfig c, DiscoveryService d)
        {
            cfg = c; disc = d;
            mq = new MqttWsClient();
            mq.OnPublish = OnPublish;
        }

        public void Start()
        {
            mq.Start();
            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Start();
            Log.W("云端自动匹配已启用（自动=相同公网出口 IP；配对码=" + (cfg.RoomCode.Length > 0 ? "已设置" : "未设置") + "）");
        }

        public void Stop() { stopped = true; mq.Stop(); }
        public bool SendWorkspace(string to, string data)
        {
            if (!mq.Connected) return false;
            string payload = "type=workspace&id=" + cfg.PeerId + "&to=" + Util.UrlEnc(to) + "&data=" + Util.UrlEnc(data);
            foreach (string topic in CurrentTopics()) mq.Publish(topic, payload);
            return CurrentTopics().Count > 0;
        }
        public bool Connected() { return mq.Connected; }

        List<string> CurrentTopics()
        {
            List<string> t = new List<string>();
            if (pubIp.Length > 0) t.Add("fy3/p/" + Util.Hash(pubIp));
            if (cfg.RoomCode.Trim().Length > 0) t.Add("fy3/r/" + cfg.RoomCode.Trim().ToLowerInvariant());
            return t;
        }

        void Loop()
        {
            while (!stopped)
            {
                try
                {
                    if ((DateTime.Now - pubIpTime).TotalMinutes > 10) FetchPublicIp();
                    List<string> topics = CurrentTopics();
                    if (topics.Count > 0)
                    {
                        mq.SetTopics(topics);
                        mq.PumpSubscriptions();
                        if ((DateTime.Now - lastPub).TotalSeconds >= 5)
                        {
                            lastPub = DateTime.Now;
                            // KV 行格式（urlencoded），与中转公告保持一致
                            StringBuilder p = new StringBuilder();
                            p.Append("id=").Append(Util.UrlEnc(cfg.PeerId))
                             .Append("&n=").Append(Util.UrlEnc(cfg.DeviceName))
                             .Append("&ips=").Append(Util.UrlEnc(string.Join(",", Util.RealIPs().ToArray())))
                             .Append("&p=").Append(cfg.TcpPort)
                             .Append("&f=").Append(cfg.SharingEnabled ? cfg.SharedItems.Count : 0)
                             .Append("&s=").Append(cfg.SharingEnabled ? "1" : "0")
                             .Append("&t=").Append(DateTime.Now.ToFileTime());
                            foreach (string t in topics) mq.Publish(t, p.ToString());
                        }
                    }
                }
                catch (Exception ex) { Log.W("云端匹配循环异常 " + ex.Message); }
                Thread.Sleep(2000);
            }
        }

        void FetchPublicIp()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://api.ipify.org/");
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;
                req.UserAgent = "FindYou/5";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                {
                    string ip = (sr.ReadToEnd() ?? "").Trim();
                    if (ip.Length > 0 && ip.Length < 40 && ip[0] != '<')
                    {
                        if (ip != pubIp) Log.W("公网出口 IP: " + ip);
                        pubIp = ip;
                        pubIpTime = DateTime.Now;
                    }
                }
            }
            catch (Exception ex) { Log.W("公网 IP 获取失败 " + ex.Message); pubIpTime = DateTime.Now; }
        }

        void OnPublish(string topic, string payload)
        {
            try
            {
                // payload 为 KV 行格式（urlencoded），与 JSON 序列化的对象格式二选一——这里用 KV
                Dictionary<string, string> kv = Util.ParseQuery(payload);
                string id = Kv(kv, "id"); if (id.Length == 0 || id == cfg.PeerId) return;
                if (Kv(kv, "type") == "workspace") { if (Kv(kv, "to") == cfg.PeerId) WorkspaceSignals.Add(id, Kv(kv, "data")); return; }
                string name = Kv(kv, "n"); if (name.Length == 0) name = "未知设备";
                List<string> ips = new List<string>();
                foreach (string s in Kv(kv, "ips").Split(',')) if (s.Trim().Length > 0) ips.Add(s.Trim());
                int port, files, sharing;
                int.TryParse(Kv(kv, "p"), out port);
                int.TryParse(Kv(kv, "f"), out files);
                sharing = Kv(kv, "s") == "1" ? 1 : 0;
                if (port <= 0) return;
                disc.UpsertPeer(id, name, ips, port, files, sharing == 1, "cloud", false);
            }
            catch { }
        }
        static string Kv(Dictionary<string, string> d, string k)
        {
            string v;
            return d.TryGetValue(k, out v) ? (v ?? "") : "";
        }
    }

    // ------------------------------------------------------------------ 中转服务（findyou-relay.js 客户端）
    class RelayService
    {
        AppConfig cfg;
        DiscoveryService disc;
        HistoryStore hist;
        Action<string, string> balloon;
        Thread sseThread;
        HttpWebRequest activeSse;
        volatile bool stop;
        volatile bool ready;
        string baseUrl = "";
        long lastSave = DateTime.Now.Ticks;

        class Incoming
        {
            public string Xid; public HistoryEntry Entry;
            public FileStream Fs; public string Part; public string Final;
            public long Received; public DateTime Last = DateTime.Now;
        }
        class PendingWant
        {
            public HistoryEntry Entry; public ManualResetEvent Done =
                new ManualResetEvent(false); public DateTime Started = DateTime.Now;
        }
        class PendingOffer
        {
            public HistoryEntry Entry; public string Path; public long Size;
            public ManualResetEvent Done = new ManualResetEvent(false); public DateTime Started = DateTime.Now;
        }
        Dictionary<string, Incoming> incoming = new Dictionary<string, Incoming>();
        Dictionary<string, PendingWant> pending = new Dictionary<string, PendingWant>();
        Dictionary<string, PendingOffer> offers = new Dictionary<string, PendingOffer>();
        Dictionary<string, List<object[]>> earlyChunks = new Dictionary<string, List<object[]>>();
        object gate = new object();

        public RelayService(AppConfig c, DiscoveryService d, HistoryStore h, Action<string, string> balloonFn,
            Action<Dictionary<string, string>, HistoryEntry> pushAcceptedFn)
        {
            cfg = c; disc = d; hist = h; balloon = balloonFn; pushAccepted = pushAcceptedFn;
            baseUrl = cfg.RelayUrl.Trim();
            if (baseUrl.EndsWith("/")) baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
        }
        Action<Dictionary<string, string>, HistoryEntry> pushAccepted;   // 中转 offer 已接受回调

        public bool Ready() { return ready && baseUrl.Length > 0; }

        public void Start()
        {
            if (baseUrl.Length == 0) return;
            sseThread = new Thread(SseLoop);
            sseThread.IsBackground = true;
            sseThread.Start();
            Thread watchdog = new Thread(Watchdog);
            watchdog.IsBackground = true;
            watchdog.Start();
            Log.W("中转服务已启用 " + baseUrl);
        }

        public void Stop() { stop = true; ready = false; try { if (activeSse != null) activeSse.Abort(); } catch { } }

        void SseLoop()
        {
            while (!stop)
            {
                try
                {
                    string url = baseUrl + "/hub?cid=" + Util.UrlEnc(cfg.PeerId)
                        + "&auth=" + cfg.RelayAuth
                        + "&room=" + Util.UrlEnc(cfg.RoomCode.Trim())
                        + "&name=" + Util.UrlEnc(cfg.DeviceName)
                        + "&ips=" + Util.UrlEnc(string.Join(",", Util.RealIPs().ToArray()))
                        + "&port=" + cfg.TcpPort;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 15000;             // 连接建立超时
                    activeSse = req;
                    req.ReadWriteTimeout = 300000;   // SSE 心跳 15s 喂狗，容忍网络抖动
                    req.Proxy = null;
                    req.UserAgent = "FindYou/5";
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        ready = true;
                        Log.W("中转通道已连接");
                        string ev = "", data = "";
                        while (!stop)
                        {
                            string line = sr.ReadLine();
                            if (line == null) break;
                            if (line.StartsWith("event:")) { ev = line.Substring(6).Trim(); continue; }
                            if (line.StartsWith("data:"))
                            {
                                data = line.Length > 5 ? line.Substring(5).Trim() : "";
                                continue;
                            }
                            if (line.Length == 0 && ev.Length > 0)
                            {
                                HandleEvent(ev, data);
                                ev = ""; data = "";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!stop) Log.W("中转通道断开: " + ex.Message);
                }
                ready = false;
                if (!stop) for (int i = 0; i < 5 && !stop; i++) Thread.Sleep(1000);
            }
        }

        void HandleEvent(string ev, string data)
        {
            try
            {
                if (ev == "signal" || ev == "hello")
                {
                    if (data.Length == 0) return;
                    Dictionary<string, string> kv = Util.ParseQuery(data);
                    string type = Kv(kv, "type");
                    if (type == "workspace") { WorkspaceSignals.Add(Kv(kv, "from"), Kv(kv, "data")); return; }
                    if (type != "announce") return;
                    if (type == "announce")
                    {
                        string id = Kv(kv, "id");
                        if (id.Length == 0 || id == cfg.PeerId) return;
                        List<string> ips = new List<string>();
                        foreach (string s in Kv(kv, "ips").Split(',')) if (s.Trim().Length > 0) ips.Add(s.Trim());
                        int port, files, sharing;
                        int.TryParse(Kv(kv, "port"), out port);
                        int.TryParse(Kv(kv, "files"), out files);
                        sharing = Kv(kv, "sharing") == "1" ? 1 : 0;
                        if (port > 0) disc.UpsertPeer(id, Kv(kv, "name"), ips, port, files, sharing == 1, "relay", false);
                    }
                    else if (type == "want") HandleWant(kv);
                    else if (type == "xstart") HandleXStart(kv);
                    else if (type == "xerr") HandleXErr(kv);
                }
                else if (ev == "chunk")
                {
                    if (!cfg.RelayFileRelay) return;
                    if (data.Length == 0) return;
                    Dictionary<string, string> kv = Util.ParseQuery(data);
                    HandleChunk(Kv(kv, "xid"), (int)ParseLong(Kv(kv, "seq"), 0),
                        Kv(kv, "last") == "1", Kv(kv, "b64"));
                }
            }
            catch (Exception ex) { Log.W("中转事件处理失败 " + ex.Message); }
        }
        static string Kv(Dictionary<string, string> d, string k)
        {
            string v;
            return d.TryGetValue(k, out v) ? (v ?? "") : "";
        }
        static long ParseLong(string s, long def) { long v; if (long.TryParse(s, out v)) return v; return def; }

        // 发送信号
        public bool PostSignal(string to, Dictionary<string, string> fields)
        {
            try
            {
                StringBuilder body = new StringBuilder();
                body.Append("{\"to\":").Append(JsonStr(to)).Append(",\"from\":").Append(JsonStr(cfg.PeerId))
                    .Append(",\"auth\":").Append(JsonStr(cfg.RelayAuth))
                    .Append(",\"data\":").Append(JsonStr(EncodeKv(fields))).Append("}");
                byte[] b = Encoding.UTF8.GetBytes(body.ToString());
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(baseUrl + "/signal");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.ContentLength = b.Length;
                req.Timeout = 8000;
                req.Proxy = null;
                using (Stream s = req.GetRequestStream()) { s.Write(b, 0, b.Length); }
                using (req.GetResponse()) { }
                return true;
            }
            catch (Exception ex) { Log.W("信号发送失败 " + ex.Message); return false; }
        }
        static string JsonStr(string s)
        {
            return Json.Of(s == null ? "" : s);
        }
        static string EncodeKv(Dictionary<string, string> f)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in f)
                sb.Append(Util.UrlEnc(kv.Key)).Append('=').Append(Util.UrlEnc(kv.Value)).Append('&');
            return sb.ToString();
        }

        // 广播我的信息（供中转分组内其他电脑发现）
        public void Announce()
        {
            if (!Ready()) return;
            Dictionary<string, string> f = new Dictionary<string, string>();
            f["type"] = "announce";
            f["id"] = cfg.PeerId;
            f["name"] = cfg.DeviceName;
            f["ips"] = string.Join(",", Util.RealIPs().ToArray());
            f["port"] = cfg.TcpPort.ToString();
            f["files"] = (cfg.SharingEnabled ? cfg.SharedItems.Count : 0).ToString();
            f["sharing"] = cfg.SharingEnabled ? "1" : "0";
            PostSignal("*", f);
        }

        // ---------- 下载方：请求对方经中转推送文件 ----------
        public void RequestDownload(HistoryEntry e, string ownerCid)
        {
            string key = ownerCid + "|" + e.Token;
            PendingWant pw = new PendingWant();
            pw.Entry = e;
            lock (gate) { pending[key] = pw; }
            e.Status = "中转连接中";
            e.Peer = disc.Find(ownerCid) != null ? disc.Find(ownerCid).Name : e.Peer;
            hist.Touch(e);
            Dictionary<string, string> f = new Dictionary<string, string>();
            f["type"] = "want";
            f["token"] = e.Token;
            f["name"] = e.Name;
            f["size"] = e.Size.ToString();
            f["type1"] = e.Type == 1 ? "1" : "0";
            bool sent = PostSignal(ownerCid, f);
            if (!sent)
            {
                e.Status = "失败:中转服务器不可达";
                e.Progress = -1;
                hist.Touch(e);
                lock (gate) { pending.Remove(key); }
                return;
            }
            bool ok = pw.Done.WaitOne(30000);
            if (!ok && e.Status == "中转连接中")
            {
                e.Status = "失败:对方无响应（中转超时）";
                e.Progress = -1;
                hist.Touch(e);
            }
            lock (gate) { pending.Remove(key); }
        }

        // ---------- 文件方：收到 want，主动推送（token=共享文件；pid=投送文件） ----------
        void HandleWant(Dictionary<string, string> kv)
        {
            string fromCid = Kv(kv, "from");
            if (fromCid.Length == 0 || fromCid == cfg.PeerId) return;
            string pid = Kv(kv, "pid");
            if (pid.Length > 0)
            {
                PendingOffer po;
                lock (gate) { if (!offers.TryGetValue(pid, out po)) return; }
                ThreadPool.QueueUserWorkItem(delegate(object o) { PushViaRelay(po, fromCid, pid); }, null);
                return;
            }
            string token = Kv(kv, "token"), name = Kv(kv, "name");
            long size = ParseLong(Kv(kv, "size"), 0);
            bool isPreview = Kv(kv, "type1") == "1";
            if (token.Length == 0 || name.Length == 0) return;
            if (!cfg.SharingEnabled) return;

            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                try
                {
                    ShareItem si; string rel; string fullF;
                    if (!srvResolve(token, out si, out rel, out fullF)) return;
                    FileInfo fi = new FileInfo(fullF);
                    if (!fi.Exists) return;
                    string xid = Guid.NewGuid().ToString("N");
                    Log.W("中转推送开始 " + fi.Name + " → " + fromCid);
                    Dictionary<string, string> xs = new Dictionary<string, string>();
                    xs["type"] = "xstart"; xs["xid"] = xid; xs["name"] = fi.Name;
                    xs["size"] = fi.Length.ToString(); xs["to"] = fromCid; xs["dd"] = "1";
                    PostSignal(fromCid, xs);

                    using (FileStream fs = fi.OpenRead())
                    {
                        byte[] buf = new byte[96 * 1024];
                        int seq = 0, n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            byte[] chunk = new byte[n];
                            Array.Copy(buf, chunk, n);
                            bool last = fs.Position >= fs.Length;
                            if (!PostChunk(fromCid, xid, seq, last, chunk))
                                throw new Exception("中转通道中断");
                            seq++;
                            if ((seq & 7) == 0) Thread.Sleep(30);   // 给服务器喘息
                        }
                    }
                    Log.W("中转推送完成 " + fi.Name);
                }
                catch (Exception ex)
                {
                    Log.W("中转推送失败 " + ex.Message);
                    Dictionary<string, string> xe = new Dictionary<string, string>();
                    xe["type"] = "xerr"; xe["to"] = fromCid; xe["msg"] = ex.Message;
                    PostSignal(fromCid, xe);
                }
            }, null);
        }

        bool srvResolve(string token, out ShareItem si, out string rel, out string fullF)
        {
            si = null; rel = ""; fullF = "";
            try
            {
                int i = token.IndexOf('|');
                if (i <= 0) return false;
                Guid g;
                if (!Guid.TryParse(token.Substring(0, i), out g)) return false;
                rel = token.Substring(i + 1).Replace('/', '\\').TrimEnd('\\');
                if (rel.Contains("..")) return false;
                lock (cfg.SharedItems)
                {
                    foreach (ShareItem x in cfg.SharedItems) if (x.Id == g) { si = x; break; }
                }
                if (si == null) return false;
                string rootFull = Path.GetFullPath(si.Path);
                fullF = Path.GetFullPath(rel.Length == 0 ? rootFull : Path.Combine(rootFull, rel));
                if (rel.Length == 0) return fullF.Equals(rootFull, StringComparison.OrdinalIgnoreCase) && File.Exists(fullF);
                string pref = rootFull.EndsWith("\\") ? rootFull : rootFull + "\\";
                return fullF.StartsWith(pref, StringComparison.OrdinalIgnoreCase) && File.Exists(fullF);
            }
            catch { return false; }
        }

        bool PostChunk(string toCid, string xid, int seq, bool last, byte[] data)
        {
            try
            {
                string url = baseUrl + "/relay/" + Util.UrlEnc(toCid)
                    + "?xid=" + Util.UrlEnc(xid) + "&seq=" + seq + (last ? "&last=1" : "")
                    + "&from=" + Util.UrlEnc(cfg.PeerId);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/octet-stream";
                req.ContentLength = data.Length;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.Proxy = null;
                using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
                using (req.GetResponse()) { }
                return true;
            }
            catch (Exception ex) { Log.W("分块发送失败 " + ex.Message); return false; }
        }

        // ---------- 投送方：直连失败时，向对方提供文件（对方自动回 want） ----------
        public void OfferFile(HistoryEntry e, string toCid, string filePath, string name, long size)
        {
            string pid = Guid.NewGuid().ToString("N");
            PendingOffer po = new PendingOffer();
            po.Entry = e; po.Path = filePath; po.Size = size;
            lock (gate) { offers[pid] = po; }
            e.Status = "中转连接中";
            hist.Touch(e);
            Dictionary<string, string> f = new Dictionary<string, string>();
            f["type"] = "offer";
            f["pid"] = pid;
            f["name"] = name;
            f["size"] = size.ToString();
            bool sent = PostSignal(toCid, f);
            if (!sent)
            {
                e.Status = "失败:中转服务器不可达";
                e.Progress = -1;
                hist.Touch(e);
                lock (gate) { offers.Remove(pid); }
                return;
            }
            po.Done.WaitOne(60000);
            lock (gate) { offers.Remove(pid); }
            if (e.Status == "中转连接中")
            {
                e.Status = "失败:对方无响应（中转超时）";
                e.Progress = -1;
                hist.Touch(e);
            }
        }

        // ---------- 接收方：收到 offer（自动接收模式），回 want 接收 ----------
        void HandleOffer(Dictionary<string, string> kv)
        {
            string fromCid = Kv(kv, "from");
            string pid = Kv(kv, "pid"), name = Kv(kv, "name");
            long size = ParseLong(Kv(kv, "size"), 0);
            if (fromCid.Length == 0 || pid.Length == 0 || name.Length == 0) return;
            if (!cfg.AutoAcceptPush) return;   // 手动确认模式：忽略（对方超时）
            HistoryEntry e = new HistoryEntry();
            e.Type = 0;
            e.Name = name;
            e.Size = size;
            PeerInfo p = disc.Find(fromCid);
            e.Peer = p != null ? p.Name : "远程设备";
            e.Status = "中转连接中";
            e.Progress = 0;
            hist.Add(e);
            if (pushAccepted != null) pushAccepted(kv, e);
            Dictionary<string, string> f = new Dictionary<string, string>();
            f["type"] = "want";
            f["pid"] = pid;
            f["name"] = name;
            f["size"] = size.ToString();
            PostSignal(fromCid, f);
        }

        void PushViaRelay(PendingOffer po, string toCid, string pid)
        {
            try
            {
                FileInfo fi = new FileInfo(po.Path);
                if (!fi.Exists) throw new Exception("文件不存在");
                string xid = Guid.NewGuid().ToString("N");
                Log.W("中转投送开始 " + fi.Name + " → " + toCid);
                Dictionary<string, string> xs = new Dictionary<string, string>();
                xs["type"] = "xstart"; xs["xid"] = xid; xs["name"] = fi.Name;
                xs["size"] = fi.Length.ToString(); xs["to"] = toCid; xs["dd"] = "1";
                PostSignal(toCid, xs);

                using (FileStream fs = fi.OpenRead())
                {
                    byte[] buf = new byte[96 * 1024];
                    int seq = 0, n;
                    HistoryEntry e = po.Entry;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        byte[] chunk = new byte[n];
                        Array.Copy(buf, chunk, n);
                        bool last = fs.Position >= fs.Length;
                        if (!PostChunk(toCid, xid, seq, last, chunk))
                            throw new Exception("中转通道中断");
                        seq++;
                        e.Status = "推送中";
                        if (fi.Length > 0) e.Progress = (int)(fs.Position * 100 / fi.Length);
                        if ((seq & 7) == 0) { hist.Touch(e); Thread.Sleep(30); }
                    }
                }
                HistoryEntry e2 = po.Entry;
                e2.Status = "已发送";
                e2.Progress = 100;
                hist.Touch(e2);
                if (balloon != null) balloon("投送成功", fi.Name);
                Log.W("中转投送完成 " + fi.Name);
            }
            catch (Exception ex)
            {
                Log.W("中转投送失败 " + ex.Message);
                HistoryEntry e = po.Entry;
                e.Status = "失败:" + ex.Message;
                e.Progress = -1;
                hist.Touch(e);
                Dictionary<string, string> xe = new Dictionary<string, string>();
                xe["type"] = "xerr"; xe["to"] = toCid; xe["msg"] = ex.Message;
                PostSignal(toCid, xe);
            }
        }

        // ---------- 接收方 ----------
        void HandleXStart(Dictionary<string, string> kv)
        {
            string xid = Kv(kv, "xid"), name = Kv(kv, "name");
            long size = ParseLong(Kv(kv, "size"), 0);
            string fromCid = Kv(kv, "from");
            bool dlDir = Kv(kv, "dd") == "1";
            if (xid.Length == 0 || name.Length == 0) return;
            HistoryEntry e = new HistoryEntry();
            e.Name = name; e.Size = size; e.Host = "(中转)";
            PeerInfo p = disc.Find(fromCid);
            e.Peer = p != null ? p.Name : "远程设备";
            e.Status = "中转接收中";
            e.Progress = 0;
            hist.Add(e);
            Incoming inc = new Incoming();
            inc.Xid = xid; inc.Entry = e;
            string destDir = Path.Combine(Path.GetTempPath(), "FindYouPreview");
            // 判断是否是 pending want 发起的（下载目录）还是预览（临时目录）
            PendingWant hit = null;
            lock (gate)
            {
                foreach (KeyValuePair<string, PendingWant> kv2 in pending)
                    if (kv2.Value.Entry.Status == "中转连接中") { hit = kv2.Value; break; }
            }
            if (hit != null)
            {
                destDir = cfg.DownloadDir;
                inc.Entry.Type = 0;
                lock (gate)
                {
                    hit.Entry = e;
                    hit.Done.Set();
                }
            }
            else inc.Entry.Type = 1;
            if (dlDir && inc.Entry.Type == 1) inc.Entry.Type = 0;   // 明确标记为投送落盘
            if (dlDir) destDir = cfg.DownloadDir;
            try
            {
                Directory.CreateDirectory(destDir);
                inc.Final = Util.UniquePath(destDir, name);
                inc.Part = inc.Final + ".part";
                inc.Fs = new FileStream(inc.Part, FileMode.Create, FileAccess.Write);
                lock (gate) { incoming[xid] = inc; }
                Log.W("中转接收开始 " + name);
                // 回放早到的分块（/signal 与 /relay 路径可能乱序）
                List<object[]> early = null;
                lock (gate)
                {
                    if (earlyChunks.TryGetValue(xid, out early)) earlyChunks.Remove(xid);
                }
                if (early != null)
                    foreach (object[] c in early)
                        HandleChunk(xid, (int)c[0], (bool)c[1], (string)c[2]);
            }
            catch (Exception ex) { Log.W("中转接收初始化失败 " + ex.Message); }
        }

        void HandleChunk(string xid, int seq, bool last, string b64)
        {
            Incoming inc;
            lock (gate)
            {
                if (!incoming.TryGetValue(xid, out inc))
                {
                    // 尚未收到 xstart → 暂存等回放
                    List<object[]> l;
                    if (!earlyChunks.TryGetValue(xid, out l)) { l = new List<object[]>(); earlyChunks[xid] = l; }
                    l.Add(new object[] { seq, last, b64 });
                    return;
                }
            }
            try
            {
                byte[] data = Convert.FromBase64String(b64);
                lock (inc)
                {
                    if (inc.Fs != null)
                    {
                        inc.Fs.Write(data, 0, data.Length);
                        inc.Received += data.Length;
                        inc.Last = DateTime.Now;
                        HistoryEntry e = inc.Entry;
                        e.Status = "中转接收中";
                        if (e.Size > 0) e.Progress = (int)Math.Min(100, inc.Received * 100 / e.Size);
                        if ((DateTime.Now.Ticks - lastSave) > 30000000) { lastSave = DateTime.Now.Ticks; hist.Touch(e); }
                    }
                }
                if (last)
                {
                    lock (inc)
                    {
                        try { if (inc.Fs != null) inc.Fs.Close(); } catch { }
                        string final = inc.Final;
                        try { File.Move(inc.Part, final); }
                        catch { final = Util.UniquePath(Path.GetDirectoryName(inc.Final), Path.GetFileName(inc.Final)); File.Move(inc.Part, final); }
                        inc.Entry.LocalPath = final;
                        inc.Entry.Status = "已完成";
                        inc.Entry.Progress = 100;
                        if (inc.Entry.Type == 0 && balloon != null) balloon("下载完成", inc.Entry.Name);
                        else if (inc.Entry.Type == 1)
                        {
                            inc.Entry.Status = "已打开";
                            try { Process.Start(final); } catch { }
                        }
                        Log.W("中转接收完成 " + inc.Entry.Name + " → " + final);
                    }
                    lock (gate) { incoming.Remove(xid); }
                    hist.Touch(inc.Entry);
                }
            }
            catch (Exception ex)
            {
                Log.W("中转接收分块失败 " + ex.Message);
                try { if (inc.Fs != null) inc.Fs.Close(); } catch { }
                inc.Entry.Status = "失败:接收中断";
                inc.Entry.Progress = -1;
                hist.Touch(inc.Entry);
                lock (gate) { incoming.Remove(xid); }
            }
        }

        void HandleXErr(Dictionary<string, string> kv)
        {
            string xid = Kv(kv, "xid");
            Incoming inc;
            lock (gate) { if (!incoming.TryGetValue(xid, out inc)) return; }
            try { if (inc.Fs != null) inc.Fs.Close(); } catch { }
            inc.Entry.Status = "失败:对方推送中断";
            inc.Entry.Progress = -1;
            hist.Touch(inc.Entry);
            lock (gate) { incoming.Remove(xid); }
        }

        void Watchdog()
        {
            while (!stop)
            {
                Thread.Sleep(5000);
                DateTime now = DateTime.Now;
                List<string> deadIn = new List<string>();
                lock (gate)
                {
                    foreach (KeyValuePair<string, Incoming> kv in incoming)
                        if ((now - kv.Value.Last).TotalSeconds > 45) deadIn.Add(kv.Key);
                }
                foreach (string xid in deadIn) HandleXErr(new Dictionary<string, string> { { "xid", xid } });

                List<string> deadPw = new List<string>();
                lock (gate)
                {
                    foreach (KeyValuePair<string, PendingWant> kv in pending)
                        if ((now - kv.Value.Started).TotalSeconds > 35)
                        {
                            deadPw.Add(kv.Key);
                            if (kv.Value.Entry.Status == "中转连接中")
                            {
                                kv.Value.Entry.Status = "失败:中转超时";
                                kv.Value.Entry.Progress = -1;
                                hist.Touch(kv.Value.Entry);
                            }
                        }
                }
                foreach (string k in deadPw) { PendingWant pw; lock (gate) { pending.TryGetValue(k, out pw); } if (pw != null) pw.Done.Set(); lock (gate) { pending.Remove(k); } }
            }
        }
    }

    // ------------------------------------------------------------------ 投送服务（P2P push，直连优先；中转仅信号）
    class PushService
    {
        AppConfig cfg;
        HistoryStore hist;
        DiscoveryService disc;
        Action<string, string> balloon;
        RelayService relay;

        public PushService(AppConfig c, HistoryStore h, DiscoveryService d, Action<string, string> balloonFn, RelayService r)
        {
            cfg = c; hist = h; disc = d; balloon = balloonFn; relay = r;
        }

        public HistoryEntry StartSend(string peerId, string filePath)
        {
            PeerInfo p = disc.Find(peerId);
            if (p == null) throw new Exception("对方已离线或不存在");
            FileInfo fi = new FileInfo(filePath);
            if (!fi.Exists) throw new Exception("文件不存在: " + filePath);
            HistoryEntry e = new HistoryEntry();
            e.Type = 2;                              // 发送记录
            e.Name = fi.Name;
            e.Size = fi.Length;
            e.Peer = p.Name;
            e.Status = "准备发送";
            e.Progress = 0;
            e.LocalPath = filePath;
            hist.Add(e);
            ThreadPool.QueueUserWorkItem(delegate(object o) { RunSend(e, p, filePath, fi.Name, fi.Length); }, e);
            return e;
        }

        void RunSend(HistoryEntry e, PeerInfo p, string filePath, string name, long size)
        {
            foreach (string ip in p.Ips)
            {
                if (TryPushDirect(e, ip, p.TcpPort, filePath, name, size)) return;
                e.Status = "准备发送"; e.Progress = 0; hist.Touch(e);
            }
            if (relay != null && relay.Ready() && cfg.RelayFileRelay)
            {
                Log.W("投送直连失败，走中转 offer " + name);
                relay.OfferFile(e, p.Id, filePath, name, size);
                return;
            }
            e.Status = "失败:无法直连对方（网络隔离）";
            e.Progress = -1;
            hist.Touch(e);
        }

        bool TryPushDirect(HistoryEntry e, string ip, int port, string filePath, string name, long size)
        {
            try
            {
                Log.W("投送直连 " + name + " → " + ip + ":" + port);
                HttpWebRequest req = PeerClient.Req("http://" + ip + ":" + port + "/api/push/begin?name=" + Util.UrlEnc(name)
                    + "&size=" + size + "&from=" + Util.UrlEnc(cfg.DeviceName));
                req.Method = "POST";
                req.ContentLength = 0;
                string respText;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    respText = sr.ReadToEnd();
                string[] rl = respText.Replace("\r", "").Split('\n');
                if (rl[0] != "OK") throw new Exception(PeerClient.ErrText(rl.Length > 1 ? rl[1] : "ERR"));
                string xid = rl.Length > 1 && rl[1].StartsWith("id=") ? rl[1].Substring(3) : "";
                if (xid.Length == 0) throw new Exception("对方响应异常");

                e.Status = "发送中";
                e.Host = ip;
                hist.Touch(e);
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] buf = new byte[256 * 1024];
                    int seq = 0, n;
                    long done = 0;
                    DateTime lastUi = DateTime.Now;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        byte[] chunk = new byte[n];
                        Array.Copy(buf, chunk, n);
                        bool last = fs.Position >= fs.Length;
                        HttpWebRequest cr = PeerClient.Req("http://" + ip + ":" + port + "/api/push/chunk?id=" + Util.UrlEnc(xid) + "&seq=" + seq + (last ? "&last=1" : ""));
                        cr.Method = "POST";
                        cr.ContentType = "application/octet-stream";
                        cr.ContentLength = chunk.Length;
                        using (Stream s = cr.GetRequestStream()) s.Write(chunk, 0, chunk.Length);
                        using (WebResponse response = cr.GetResponse())
                        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                            if (reader.ReadToEnd().Trim() != "OK") throw new IOException("对方写入文件分块失败");
                        seq++;
                        done += n;
                        if ((DateTime.Now - lastUi).TotalMilliseconds > 400)
                        {
                            lastUi = DateTime.Now;
                            e.Status = "发送中";
                            if (size > 0) e.Progress = (int)Math.Min(99, done * 100 / size);
                            if ((seq & 7) == 0) hist.Touch(e);
                        }
                    }
                }
                HttpWebRequest er = PeerClient.Req("http://" + ip + ":" + port + "/api/push/end?id=" + Util.UrlEnc(xid));
                er.Method = "POST";
                er.ContentLength = 0;
                using (WebResponse response = er.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    if (reader.ReadToEnd().Trim() != "OK") throw new IOException("对方未确认文件完整保存");
                e.Status = "已发送";
                e.Progress = 100;
                hist.Touch(e);
                if (balloon != null) balloon("投送成功", name + " → " + e.Peer);
                Log.W("投送完成 " + name + " → " + e.Peer + " (" + ip + ")");
                return true;
            }
            catch (Exception ex)
            {
                Log.W("投送直连失败[" + ip + "] " + name + " : " + ex.Message);
                return false;
            }
        }
    }

    // ------------------------------------------------------------------ 原生对话框（现代 IFileDialog，文件名框可粘贴路径）
    static class NativeDialogs
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr hwndOwner);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        const uint FOS_PICKFOLDERS = 0x20;
        const uint FOS_FORCEFILESYSTEM = 0x40;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        public static string PickFolder(IWin32Window owner, string title)
        {
            try
            {
                IFileOpenDialog dlg = (IFileOpenDialog)new FileOpenDialogRCW();
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);
                int hr = dlg.Show(owner == null ? IntPtr.Zero : owner.Handle);
                if (hr != 0) return null;
                IShellItem item;
                dlg.GetResult(out item);
                if (item == null) return null;
                string path;
                item.GetDisplayName(SIGDN_FILESYSPATH, out path);
                return path;
            }
            catch (Exception ex) { Log.W("文件夹对话框失败 " + ex.Message); return null; }
        }

        public static string[] PickFiles(IWin32Window owner, string title)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = title;
                d.Multiselect = true;
                d.CheckFileExists = true;
                if (d.ShowDialog(owner) == DialogResult.OK) return d.FileNames;
                return null;
            }
        }
    }

    // ------------------------------------------------------------------ 拖放窗口（从资源管理器拖入即可添加共享）
    class DropWindow : Form
    {
        public Action<List<string>> OnDropped;

        public DropWindow()
        {
            Text = "FindYou · 拖放添加共享";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - 340, Screen.PrimaryScreen.WorkingArea.Bottom - 240);
            Size = new Size(320, 190);
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10F);

            Label l1 = new Label();
            l1.Text = "把文件 / 文件夹拖到这里";
            l1.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            l1.Dock = DockStyle.Fill;
            l1.TextAlign = ContentAlignment.MiddleCenter;
            l1.ForeColor = Color.FromArgb(60, 70, 90);

            Label l2 = new Label();
            l2.Text = "拖入后立即加入共享列表";
            l2.Dock = DockStyle.Bottom;
            l2.Height = 40;
            l2.TextAlign = ContentAlignment.TopCenter;
            l2.ForeColor = Color.Gray;

            Controls.Add(l1);
            Controls.Add(l2);
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0 && OnDropped != null) OnDropped(new List<string>(files));
            };
        }
    }

    // ------------------------------------------------------------------ HTTP 服务（对外传输 + 受令牌保护的本机管理接口）
    partial class HttpFileServer
    {
        AppConfig cfg;
        DiscoveryService disc;
        HistoryStore hist;
        DownloadManager dl;
        TcpListener lsn;
        volatile bool stop;
        public string SessionToken;

        PushService push;

        public HttpFileServer(AppConfig c, DiscoveryService discSv, HistoryStore h, DownloadManager dlm, PushService ps)
        {
            cfg = c; disc = discSv; hist = h; dl = dlm; push = ps;
            SessionToken = c.ApiToken.Length == 32 ? c.ApiToken : Guid.NewGuid().ToString("N");
        }

        public Action QuitRequested;
        public Action CloudRefresh;   // 设置变更后让云端立即重新广播
        public Func<string> PickFolderDlg;
        public Func<string[]> PickFilesDlg;
        public Action ShowDropWindow;
        // 把动作编组到托盘 STA 线程执行（由 TrayApp 注入）
        public Action<Action> StaInvoke;

        public void Start()
        {
            int port = cfg.TcpPort;
            for (int i = 0; i < 10; i++)
            {
                try { lsn = new TcpListener(IPAddress.Any, port); lsn.Start(); cfg.TcpPort = port; break; }
                catch { lsn = null; port++; }
            }
            if (lsn == null) { Log.W("TCP 端口绑定失败，文件服务不可用"); return; }
            Log.W("文件服务已启动 http://" + Util.PrimaryIp() + ":" + cfg.TcpPort);
            Log.W("本机控制服务 http://127.0.0.1:" + cfg.TcpPort);
            transferCleanup = new System.Threading.Timer(CleanupTransfers, null, 30000, 30000);
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Start();
        }

        void AcceptLoop()
        {
            while (!stop)
            {
                TcpClient c;
                try { c = lsn.AcceptTcpClient(); }
                catch (Exception ex) { if (stop) break; Log.W("HTTP accept retry: " + ex.Message); Thread.Sleep(500); continue; }
                ThreadPool.QueueUserWorkItem(delegate(object o) { HandleClient((TcpClient)o); }, c);
            }
        }

        void HandleClient(TcpClient c)
        {
            string logRoute = "";
            try
            {
                c.ReceiveTimeout = 15000;
                c.SendTimeout = 120000;
                c.NoDelay = true;
                NetworkStream s = c.GetStream();
                while (!stop)
                {
                logRoute = "";
                byte[] head; byte[] extra;
                if (!ReadHead(s, out head, out extra)) return;
                string text = Encoding.ASCII.GetString(head);
                string[] lines = text.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0], target = parts[1];

                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int ci = lines[i].IndexOf(':');
                    if (ci > 0) headers[lines[i].Substring(0, ci).Trim()] = lines[i].Substring(ci + 1).Trim();
                }

                string path = target, query = "";
                int qi = target.IndexOf('?');
                if (qi >= 0) { path = target.Substring(0, qi); query = target.Substring(qi + 1); }
                logRoute = method + " " + path;
                Dictionary<string, string> qs = Util.ParseQuery(query);

                byte[] bodyRaw = new byte[0];
                long cl = 0;
                string cls;
                if (headers.TryGetValue("Content-Length", out cls)) long.TryParse(cls, out cl);
                if (path == "/api/transfer/stream" && method == "POST" && !headers.ContainsKey("Transfer-Encoding"))
                {
                    ReceiveFileStream(s, qs, cl, extra);
                    continue;
                }
                if (cl < 0 || cl > 4 * 1024 * 1024 || headers.ContainsKey("Transfer-Encoding")) { WriteText(s, 413, "Unsupported request body"); return; }
                if (extra != null && extra.Length > cl) { WriteText(s, 400, "Pipelined requests are not supported"); return; }
                if (cl > 0 && cl <= 4 * 1024 * 1024)
                {
                    MemoryStream ms = new MemoryStream();
                    if (extra != null && extra.Length > 0) ms.Write(extra, 0, extra.Length);
                    byte[] buf = new byte[65536];
                    while (ms.Length < cl)
                    {
                        int n = s.Read(buf, 0, (int)Math.Min((long)buf.Length, cl - ms.Length));
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                    }
                    bodyRaw = ms.ToArray();
                    if (bodyRaw.Length != cl) { WriteText(s, 400, "Incomplete request body"); return; }
                }
                string body = path.EndsWith("/chunk") ? "" : new UTF8Encoding(false).GetString(bodyRaw);

                bool loopback = IsLoopback(c);

                if (path == "/api/info") WriteText(s, 200, InfoText() + "\ntransfer=1\nstream=1\n");
                else if (path == "/api/shared" && method == "GET") HandleShared(s, qs);
                else if (path == "/api/file" && (method == "GET" || method == "HEAD")) HandleFile(s, qs, headers, method == "HEAD");
                else if (path == "/api/push/begin" && method == "POST") HandlePushBegin(s, qs);
                else if (path == "/api/push/chunk" && method == "POST") HandlePushChunk(s, qs, bodyRaw);
                else if (path == "/api/push/end" && method == "POST") HandlePushEnd(s, qs);
                else if (path == "/api/push/abort" && method == "POST") HandlePushAbort(s, qs);
                else if (path.StartsWith("/api/transfer/") && method == "POST") ReceiveDirect(s, path.Substring(14), path.EndsWith("/chunk") ? qs : Util.ParseQuery(body), bodyRaw);
                else if (path == "/api/connect/signal" && method == "POST") ReceiveSignal(s, Util.ParseQuery(body), ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString());
                else if (loopback && path.StartsWith("/api/local/"))
                    HandleLocal(s, method, path, qs, Util.ParseQuery(body), bodyRaw, headers);
                else if (loopback && path == "/favicon.ico") WriteHead(s, 204, "No Content", 0, null);
                else if (loopback) WriteText(s, 403, "FindYou 服务运行中（设备: " + cfg.DeviceName + "）\n\n请从托盘图标打开控制界面。");
                else WriteText(s, 404, "ERR\nNOTFOUND");
                }
            }
            catch (Exception ex) { if (logRoute.Length > 0) Log.W("HTTP " + logRoute + " : " + ex.Message); }
            finally { try { c.Close(); } catch { } }
        }

        static bool IsLoopback(TcpClient c)
        {
            try
            {
                IPEndPoint ep = (IPEndPoint)c.Client.RemoteEndPoint;
                return ep != null && ep.Address.Equals(IPAddress.Loopback);
            }
            catch { return false; }
        }

        // ============ 传输服务 ============
    // ------------------------------------------------------------------ 投送接收（对方 P2P push，开放接口）
    class PushIncoming
    {
        public string Xid; public HistoryEntry Entry;
        public FileStream Fs; public string Part; public string Final;
        public long Received; public int Sequence; public DateTime Last = DateTime.Now;
    }
    Dictionary<string, PushIncoming> pushes = new Dictionary<string, PushIncoming>();
    object pushGate = new object();
    internal Action<string, string> balloonSink;
    long lastSaveTicks = DateTime.Now.Ticks;

    void HandlePushBegin(NetworkStream s, Dictionary<string, string> qs)
    {
        string name = Util.SanitizeName(Str(qs, "name"));
        long size = Num(qs, "size", 0);
        string from = Str(qs, "from");
        if (name.Length == 0 || size < 0) { WriteText(s, 200, "ERR\nBAD"); return; }
        if (!cfg.AutoAcceptPush) { WriteText(s, 200, "ERR\nNEEDCONFIRM"); return; }
        string xid = Guid.NewGuid().ToString("N");
        HistoryEntry e = new HistoryEntry();
        e.Type = 0;
        e.Name = name;
        e.Size = size;
        e.Peer = from.Length > 0 ? from : "局域网设备";
        e.Status = "接收中";
        e.Progress = 0;
        hist.Add(e);
        try
        {
            Directory.CreateDirectory(cfg.DownloadDir);
            PushIncoming inc = new PushIncoming();
            inc.Xid = xid; inc.Entry = e;
            inc.Final = Util.UniquePath(cfg.DownloadDir, name);
            inc.Part = Path.Combine(cfg.DownloadDir, ".findyou-" + xid + ".part");
            inc.Fs = new FileStream(inc.Part, FileMode.CreateNew, FileAccess.Write);
            lock (pushGate) { pushes[xid] = inc; }
            WriteText(s, 200, "OK\nid=" + xid);
        }
        catch (Exception ex)
        {
            e.Status = "失败:" + ex.Message;
            e.Progress = -1;
            hist.Touch(e);
            WriteText(s, 200, "ERR\nBUSY");
        }
    }

    PushIncoming PushFind(Dictionary<string, string> qs)
    {
        lock (pushGate)
        {
            PushIncoming inc;
            if (pushes.TryGetValue(Str(qs, "id"), out inc)) { inc.Last = DateTime.Now; return inc; }
        }
        return null;
    }

    void HandlePushChunk(NetworkStream s, Dictionary<string, string> qs, byte[] bodyRaw)
    {
        PushIncoming inc = PushFind(qs);
        if (inc == null) { WriteText(s, 200, "ERR\nNOTFOUND"); return; }
        try
        {
            lock (inc)
            {
                if (Num(qs, "seq", -1) != inc.Sequence || bodyRaw.Length > inc.Entry.Size - inc.Received) throw new IOException("分块顺序或大小不一致");
                inc.Fs.Write(bodyRaw, 0, bodyRaw.Length);
                inc.Received += bodyRaw.Length;
                inc.Sequence++;
                HistoryEntry e = inc.Entry;
                if (e.Size > 0) e.Progress = (int)Math.Min(99, inc.Received * 100 / e.Size);
                if ((DateTime.Now.Ticks - lastSaveTicks) > 30000000) { lastSaveTicks = DateTime.Now.Ticks; hist.Touch(e); }
            }
            WriteText(s, 200, "OK");
        }
        catch (Exception ex)
        {
            FailPush(inc, "接收失败: " + ex.Message);
            WriteText(s, 200, "ERR\nWRITE");
        }
    }

    void HandlePushEnd(NetworkStream s, Dictionary<string, string> qs)
    {
        PushIncoming inc = PushFind(qs);
        if (inc == null) { WriteText(s, 200, "ERR\nNOTFOUND"); return; }
        if (inc.Received != inc.Entry.Size) { FailPush(inc, "文件未接收完整"); WriteText(s, 200, "ERR\nINCOMPLETE"); return; }
        try { if (inc.Fs != null) { inc.Fs.Flush(true); inc.Fs.Close(); } } catch { FailPush(inc, "保存失败"); WriteText(s, 200, "ERR\nWRITE"); return; }
        string final = inc.Final;
        try { File.Move(inc.Part, final); }
        catch { final = Util.UniquePath(Path.GetDirectoryName(inc.Final), Path.GetFileName(inc.Final)); File.Move(inc.Part, final); }
        inc.Entry.LocalPath = final;
        inc.Entry.Status = "已完成";
        inc.Entry.Progress = 100;
        hist.Touch(inc.Entry);
        lock (pushGate) { pushes.Remove(inc.Xid); }
        if (balloonSink != null) balloonSink("收到投送", inc.Entry.Name + " ← " + inc.Entry.Peer);
        WriteText(s, 200, "OK");
    }

    void HandlePushAbort(NetworkStream s, Dictionary<string, string> qs)
    {
        PushIncoming inc = PushFind(qs);
        if (inc == null) { WriteText(s, 200, "OK"); return; }
        FailPush(inc, "对方取消了投送");
        WriteText(s, 200, "OK");
    }

    void FailPush(PushIncoming inc, string msg)
    {
        try { if (inc.Fs != null) inc.Fs.Close(); } catch { }
        try { if (inc.Part != null && File.Exists(inc.Part)) File.Delete(inc.Part); } catch { }
        inc.Entry.Status = "失败:" + msg;
        inc.Entry.Progress = -1;
        hist.Touch(inc.Entry);
        lock (pushGate) { pushes.Remove(inc.Xid); }
    }

    // ---------------- 原生选择与粘贴路径（在 STA 线程执行） ----------------
    void LocalPickDir(NetworkStream s)
    {
        string path = null;
        ManualResetEvent done = new ManualResetEvent(false);
        StaInvoke(delegate
        {
            path = PickFolderDlg != null ? PickFolderDlg() : null;
            done.Set();
        });
        if (!done.WaitOne(600000)) { WriteJson(s, 200, Json.D("ok", false, "error", "选择超时")); return; }
        if (path == null) WriteJson(s, 200, Json.D("ok", false, "error", "已取消"));
        else WriteJson(s, 200, Json.D("ok", true, "path", path));
    }

    void LocalPickFiles(NetworkStream s)
    {
        string[] paths = null;
        ManualResetEvent done = new ManualResetEvent(false);
        StaInvoke(delegate
        {
            paths = PickFilesDlg != null ? PickFilesDlg() : null;
            done.Set();
        });
        if (!done.WaitOne(600000)) { WriteJson(s, 200, Json.D("ok", false, "error", "选择超时")); return; }
        if (paths == null) WriteJson(s, 200, Json.D("ok", false, "error", "已取消"));
        else WriteJson(s, 200, Json.D("ok", true, "paths", paths));
    }

    void LocalSharePaste(NetworkStream s)
    {
        List<string> added = new List<string>();
        List<string> failed = new List<string>();
        Exception err = null;
        ManualResetEvent done = new ManualResetEvent(false);
        try
        {
            StaInvoke(delegate
            {
                try
                {
                    List<string> paths = new List<string>();
                    if (Clipboard.ContainsFileDropList())
                        foreach (string f in Clipboard.GetFileDropList()) paths.Add(f);
                    else if (Clipboard.ContainsText())
                    {
                        string t = Clipboard.GetText().Trim();
                        if (t.Length > 0) foreach (string line in t.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) paths.Add(line.Trim());
                    }
                    foreach (string p in paths)
                    {
                        string e = AddSharePath(p);
                        if (e == null) added.Add(p); else failed.Add(p + "：" + e);
                    }
                }
                catch (Exception ex) { err = ex; }
                done.Set();
            });
            done.WaitOne(5000);
        }
        catch (Exception ex) { err = ex; }
        if (err != null) { WriteJson(s, 200, Json.D("ok", false, "error", err.Message)); return; }
        WriteJson(s, 200, Json.D("ok", true, "added", added.Count, "failed", failed.Count,
            "detail", string.Join("\n", failed.ToArray())));
    }

    // ---------------- 暂存区（浏览器拖入的文件内容） ----------------
    string stageDir;
    Dictionary<string, string> stageFiles = new Dictionary<string, string>();
    object stageGate = new object();

    void LocalStageBegin(NetworkStream s, Dictionary<string, string> qs)
    {
        try
        {
            string name = Util.SanitizeName(Str(qs, "name"));
            if (stageDir == null) stageDir = Path.Combine(cfg.DataDir, "staging");
            Directory.CreateDirectory(stageDir);
            string sid = Guid.NewGuid().ToString("N");
            string path = Path.Combine(stageDir, sid + "_" + name);
            lock (stageGate) { stageFiles[sid] = path; }
            WriteJson(s, 200, Json.D("ok", true, "sid", sid));
        }
        catch (Exception ex) { WriteJson(s, 200, Json.D("ok", false, "error", ex.Message)); }
    }

    void LocalStageChunk(NetworkStream s, Dictionary<string, string> qs, byte[] bodyRaw)
    {
        string sid = Str(qs, "sid");
        string path;
        lock (stageGate) { if (!stageFiles.TryGetValue(sid, out path)) { WriteJson(s, 200, Json.D("ok", false, "error", "sid 不存在")); return; } }
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write))
                fs.Write(bodyRaw, 0, bodyRaw.Length);
            WriteJson(s, 200, Json.D("ok", true));
        }
        catch (Exception ex) { WriteJson(s, 200, Json.D("ok", false, "error", ex.Message)); }
    }

    void LocalStageEnd(NetworkStream s, Dictionary<string, string> qs)
    {
        string sid = Str(qs, "sid");
        string path;
        lock (stageGate) { if (!stageFiles.TryGetValue(sid, out path)) { WriteJson(s, 200, Json.D("ok", false, "error", "sid 不存在")); return; } }
        lock (stageGate) { stageFiles.Remove(sid); }
        WriteJson(s, 200, Json.D("ok", true, "path", path));
    }

        bool ValidCookie(Dictionary<string, string> headers)
        {
            string ck;
            if (!headers.TryGetValue("Cookie", out ck)) return false;
            foreach (string part in ck.Split(';'))
            {
                string p = part.Trim();
                if (p.StartsWith("fyu=")) return p.Substring(4) == SessionToken;
            }
            return false;
        }

        bool ValidSession(Dictionary<string, string> qs, Dictionary<string, string> headers)
        {
            string k;
            if (qs.TryGetValue("k", out k) && k == SessionToken) return true;
            return ValidCookie(headers);
        }

        // ============ 本机管理 API ============
        void HandleLocal(NetworkStream s, string method, string path, Dictionary<string, string> qs,
            Dictionary<string, string> post, byte[] bodyRaw, Dictionary<string, string> headers)
        {
            if (!ValidSession(qs, headers))
            {
                WriteJson(s, 401, Json.D("ok", false, "error", "未授权，请从托盘重新打开控制界面"));
                return;
            }
            try
            {
                if (path.StartsWith("/api/local/native/")) { NativeApi(s, path.Substring(18), method == "GET" ? qs : post); return; }
                if (path.StartsWith("/api/local/workspace/")) { WorkspaceApi(s, path.Substring(21), method == "GET" ? qs : post, qs, bodyRaw); return; }
                switch (path)
                {
                    case "/api/local/state": LocalState(s); break;
                    case "/api/local/refresh": disc.SendQuery(); WriteJson(s, 200, Json.D("ok", true)); break;
                    case "/api/local/history": LocalHistory(s, qs); break;
                    case "/api/local/history/clear": hist.Clear((int)Num(post, "type", 0)); WriteJson(s, 200, Json.D("ok", true)); break;
                    case "/api/local/history/delete": hist.Remove(Str(post, "id")); WriteJson(s, 200, Json.D("ok", true)); break;
                    case "/api/local/open": LocalOpen(s, post); break;
                    case "/api/local/download/start": LocalDownload(s, post); break;
                    case "/api/local/preview": LocalPreview(s, post); break;
                    case "/api/local/peer/files": LocalPeerFiles(s, qs); break;
                    case "/api/local/peers/add": LocalPeerAdd(s, post); break;
                    case "/api/local/config": LocalConfig(s, method, post); break;
                    case "/api/local/shares/add": LocalShareAdd(s, post); break;
                    case "/api/local/shares/paste": LocalSharePaste(s); break;
                    case "/api/local/shares/remove": LocalShareRemove(s, post); break;
                    case "/api/local/pick/dir": LocalPickDir(s); break;
                    case "/api/local/pick/files": LocalPickFiles(s); break;
                    case "/api/local/dropwindow": if (ShowDropWindow != null) ShowDropWindow(); WriteJson(s, 200, Json.D("ok", true)); break;
                    case "/api/local/stage/begin": LocalStageBegin(s, qs); break;
                    case "/api/local/stage/chunk": LocalStageChunk(s, qs, bodyRaw); break;
                    case "/api/local/stage/end": LocalStageEnd(s, qs); break;
                    case "/api/local/send/start": LocalSendStart(s, post); break;
                    case "/api/local/fs": LocalFs(s, qs); break;
                    case "/api/local/reveal-dl": RevealPath(cfg.DownloadDir, false); WriteJson(s, 200, Json.D("ok", true)); break;
                    case "/api/local/reveal": RevealPath(Str(post, "path"), true); WriteJson(s, 200, Json.D("ok", true)); break;
                    case "/api/local/firewall": WriteJson(s, 200, Json.D("ok", FirewallFix())); break;
                    case "/api/local/quit":
                        WriteJson(s, 200, Json.D("ok", true));
                        if (QuitRequested != null)
                        {
                            if (StaInvoke != null) StaInvoke(QuitRequested);
                            else QuitRequested();
                        }
                        break;
                    default: WriteJson(s, 404, Json.D("ok", false, "error", "未知接口")); break;
                }
            }
            catch (Exception ex)
            {
                Log.W("本机接口 " + path + " : " + ex.Message);
                WriteJson(s, 200, Json.D("ok", false, "error", "内部错误: " + ex.Message));
            }
        }

        static string Str(Dictionary<string, string> d, string k)
        {
            string v;
            return d.TryGetValue(k, out v) ? (v ?? "") : "";
        }
        static long Num(Dictionary<string, string> d, string k, long def)
        {
            string v;
            if (d.TryGetValue(k, out v)) { long r; if (long.TryParse(v, out r)) return r; }
            return def;
        }

        void LocalState(NetworkStream s)
        {
            List<object> peers = new List<object>();
            foreach (PeerInfo p in disc.GetPeers())
            {
                peers.Add(Json.D("id", p.Id, "name", p.Name, "ip", p.Ips.Count > 0 ? p.Ips[0] : "", "port", p.TcpPort,
                    "files", p.FileCount, "sharing", p.Sharing, "online", p.Online, "manual", p.Manual,
                    "source", p.Source, "seen", Util.FormatTime(p.LastSeen)));
            }
            int shareCount;
            lock (cfg.SharedItems) { shareCount = cfg.SharedItems.Count; }
            object me = Json.D("id", cfg.PeerId, "name", cfg.DeviceName, "ip", Util.PrimaryIp(), "ips", Util.RealIPs().ToArray(),
                "port", cfg.TcpPort, "sharing", cfg.SharingEnabled, "shareCount", shareCount,
                "downloadDir", cfg.DownloadDir, "cloud", cfg.CloudEnabled,
                "cloudOk", cloud != null && cloud.Connected(), "relayOk", relay != null && relay.Ready());
            WriteJson(s, 200, Json.D("ok", true, "me", me, "peers", peers, "active", dl.ActiveCount()));
        }

        CloudService cloud;
        RelayService relay;
        public void SetServices(CloudService cs, RelayService rs) { cloud = cs; relay = rs; }

        void LocalHistory(NetworkStream s, Dictionary<string, string> qs)
        {
            int type = (int)Num(qs, "type", 0);
            List<object> items = new List<object>();
            foreach (HistoryEntry e in hist.Snapshot())
            {
                if (e.Type != type) continue;
                items.Add(Json.D("id", e.Id, "name", e.Name, "peer", e.Peer, "size", e.Size,
                    "time", Util.FormatTime(e.Time), "timeKey", e.TimeFile / 10000, "status", e.Status, "progress", e.Progress,
                    "localPath", e.LocalPath, "host", e.Host, "port", e.Port));
            }
            WriteJson(s, 200, Json.D("ok", true, "items", items));
        }

        void LocalOpen(NetworkStream s, Dictionary<string, string> post)
        {
            HistoryEntry e = hist.Find(Str(post, "id"));
            if (e == null) { WriteJson(s, 200, Json.D("ok", false, "error", "记录不存在")); return; }
            string p = e.LocalPath;
            if (Str(post, "mode") == "folder")
            {
                if (p.Length > 0 && File.Exists(p))
                {
                    try { Process.Start("explorer.exe", "/select,\"" + p + "\""); WriteJson(s, 200, Json.D("ok", true)); return; }
                    catch (Exception ex) { WriteJson(s, 200, Json.D("ok", false, "error", ex.Message)); return; }
                }
                WriteJson(s, 200, Json.D("ok", false, "error", "文件不存在（可能已被移动或删除）"));
                return;
            }
            if (p.Length > 0 && File.Exists(p))
            {
                try { Process.Start(p); WriteJson(s, 200, Json.D("ok", true)); }
                catch (Exception ex) { WriteJson(s, 200, Json.D("ok", false, "error", "无法打开: " + ex.Message)); }
            }
            else WriteJson(s, 200, Json.D("ok", false, "error", "文件不存在（可能已被移动或删除）"));
        }

        void LocalDownload(NetworkStream s, Dictionary<string, string> post)
        {
            string histId = Str(post, "histId");
            string host = Str(post, "host");
            List<string> ips = new List<string>();
            if (host.Length > 0) ips.Add(host);
            string token, name, peerName, peerCid;
            int port;
            long size;
            if (histId.Length > 0)
            {
                HistoryEntry e = hist.Find(histId);
                if (e == null) { WriteJson(s, 200, Json.D("ok", false, "error", "记录不存在")); return; }
                if (e.Host.Length == 0 || e.Token.Length == 0) { WriteJson(s, 200, Json.D("ok", false, "error", "该记录缺少来源信息，无法重新下载")); return; }
                if (e.Host != "(中转)") ips = new List<string> { e.Host };
                token = e.Token; name = e.Name; size = e.Size; peerName = e.Peer;
                PeerInfo pp = null;
                foreach (PeerInfo x in disc.GetPeers()) if (x.Name == e.Peer) { pp = x; break; }
                peerCid = pp != null ? pp.Id : "";
                port = pp != null ? pp.TcpPort : e.Port;
            }
            else
            {
                PeerInfo p = disc.Find(Str(post, "peerId"));
                if (p == null) { WriteJson(s, 200, Json.D("ok", false, "error", "对方已离线或不存在")); return; }
                ips = p.Ips;
                peerCid = p.Id; peerName = p.Name; port = p.TcpPort;
                token = Str(post, "token");
                name = Str(post, "name");
                if (token.Length == 0 || name.Length == 0) { WriteJson(s, 200, Json.D("ok", false, "error", "参数不完整")); return; }
                size = Num(post, "size", 0);
            }
            if (Str(post, "isDir") == "1")
            {
                HistoryEntry ne = dl.StartFolder(ips, port, token, name, peerName, peerCid);
                WriteJson(s, 200, Json.D("ok", true, "id", ne.Id));
            }
            else
            {
                HistoryEntry ne = dl.Start(ips, port, token, name, size, 0, peerName, peerCid);
                WriteJson(s, 200, Json.D("ok", true, "id", ne.Id));
            }
        }

        void LocalPreview(NetworkStream s, Dictionary<string, string> post)
        {
            PeerInfo p = disc.Find(Str(post, "peerId"));
            if (p == null) { WriteJson(s, 200, Json.D("ok", false, "error", "对方已离线或不存在")); return; }
            string token = Str(post, "token"), name = Str(post, "name");
            if (token.Length == 0 || name.Length == 0) { WriteJson(s, 200, Json.D("ok", false, "error", "参数不完整")); return; }
            HistoryEntry ne = dl.Start(p.Ips, p.TcpPort, token, name, Num(post, "size", 0), 1, p.Name, p.Id);
            WriteJson(s, 200, Json.D("ok", true, "id", ne.Id));
        }

        void LocalPeerFiles(NetworkStream s, Dictionary<string, string> qs)
        {
            PeerInfo p = disc.Find(Str(qs, "peerId"));
            if (p == null) { WriteJson(s, 200, Json.D("ok", false, "error", "对方已离线或不存在")); return; }
            ListResult r = PeerClient.List(p.Ips, p.TcpPort, Str(qs, "dir"));
            if (r.Error.Length > 0) { WriteJson(s, 200, Json.D("ok", false, "error", r.Error)); return; }
            List<object> items = new List<object>();
            foreach (PeerEntry it in r.Items)
            {
                items.Add(Json.D("name", it.Name, "size", it.Size, "sizeStr", it.IsDir ? "" : Util.FormatSize(it.Size),
                    "mtime", Util.FormatTime(it.Mtime), "isDir", it.IsDir, "token", it.Token));
            }
            WriteJson(s, 200, Json.D("ok", true, "parent", r.Parent, "items", items));
        }

        void LocalPeerAdd(NetworkStream s, Dictionary<string, string> post)
        {
            string err = disc.AddManual(Str(post, "host"));
            if (err == null) WriteJson(s, 200, Json.D("ok", true));
            else WriteJson(s, 200, Json.D("ok", false, "error", err));
        }

        void LocalConfig(NetworkStream s, string method, Dictionary<string, string> post)
        {
            if (method == "POST")
            {
                string err = ApplyConfig(post);
                if (err != null) { WriteJson(s, 200, Json.D("ok", false, "error", err)); return; }
            }
            List<object> shares = new List<object>();
            lock (cfg.SharedItems)
            {
                foreach (ShareItem si in cfg.SharedItems)
                {
                    bool isF = File.Exists(si.Path), isD = Directory.Exists(si.Path);
                    shares.Add(Json.D("id", si.Id.ToString(), "path", si.Path,
                        "type", isF ? "文件" : (isD ? "文件夹" : "已失效"), "valid", isF || isD));
                }
            }
            WriteJson(s, 200, Json.D("ok", true, "name", cfg.DeviceName, "downloadDir", cfg.DownloadDir,
                "sharing", cfg.SharingEnabled, "autostart", Util.GetAutoStart(),
                "cloud", cfg.CloudEnabled, "room", cfg.RoomCode, "relay", cfg.RelayUrl,
                "frelay", cfg.RelayFileRelay, "apush", cfg.AutoAcceptPush,
                "tcp", cfg.TcpPort, "udp", cfg.UdpPort, "dataDir", cfg.DataDir, "shares", shares,
                "primaryIp", Util.PrimaryIp(), "allIps", Util.RealIPs().ToArray()));
        }

        string ApplyConfig(Dictionary<string, string> post)
        {
            if (post.ContainsKey("name"))
            {
                string nm = Str(post, "name").Trim();
                if (nm.Length == 0 || nm.Length > 24) return "设备名称应为 1–24 字";
                cfg.DeviceName = nm;
            }
            if (post.ContainsKey("dlDir"))
            {
                string dd = Str(post, "dlDir").Trim();
                if (dd.Length > 0)
                {
                    try { Directory.CreateDirectory(dd); cfg.DownloadDir = dd; }
                    catch { return "下载目录不可用，已保持原设置"; }
                }
            }
            if (post.ContainsKey("sharing")) cfg.SharingEnabled = Str(post, "sharing") == "1";
            if (post.ContainsKey("autostart")) Util.SetAutoStart(Str(post, "autostart") == "1");
            if (post.ContainsKey("cloud")) cfg.CloudEnabled = Str(post, "cloud") == "1";
            if (post.ContainsKey("room")) cfg.RoomCode = Str(post, "room").Trim();
            if (post.ContainsKey("relay"))
            {
                string value = Str(post, "relay").Trim(); Uri uri;
                if (value.Length > 0 && (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))) return "服务器地址需要以 http:// 或 https:// 开头";
                cfg.RelayUrl = value;
            }
            cfg.RelayFileRelay = false;
            if (post.ContainsKey("apush")) cfg.AutoAcceptPush = Str(post, "apush") == "1";
            cfg.Save();
            if (CloudRefresh != null) CloudRefresh();
            return null;
        }

        string AddSharePath(string p)
        {
            p = (p ?? "").Trim().Trim('"');
            bool isF = p.Length > 0 && File.Exists(p), isD = p.Length > 0 && Directory.Exists(p);
            if (!isF && !isD) return "路径不存在: " + p;
            lock (cfg.SharedItems)
            {
                foreach (ShareItem x in cfg.SharedItems)
                    if (string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))
                        return "该路径已在共享列表中";
                cfg.SharedItems.Add(new ShareItem { Id = Guid.NewGuid(), Path = p });
                cfg.SharingEnabled = true;
            }
            cfg.Save();
            Log.W("新增共享 " + p);
            return null;
        }

        void LocalShareAdd(NetworkStream s, Dictionary<string, string> post)
        {
            string err = AddSharePath(Str(post, "path"));
            if (err == null) WriteJson(s, 200, Json.D("ok", true));
            else WriteJson(s, 200, Json.D("ok", false, "error", err));
        }

        void LocalSendStart(NetworkStream s, Dictionary<string, string> post)
        {
            try
            {
                HistoryEntry ne = push.StartSend(Str(post, "peerId"), Str(post, "path"));
                WriteJson(s, 200, Json.D("ok", true, "id", ne.Id));
            }
            catch (Exception ex)
            {
                WriteJson(s, 200, Json.D("ok", false, "error", ex.Message));
            }
        }

        void LocalShareRemove(NetworkStream s, Dictionary<string, string> post)
        {
            Guid g;
            if (!Guid.TryParse(Str(post, "id"), out g)) { WriteJson(s, 200, Json.D("ok", false, "error", "参数错误")); return; }
            lock (cfg.SharedItems)
            {
                for (int i = cfg.SharedItems.Count - 1; i >= 0; i--)
                    if (cfg.SharedItems[i].Id == g) cfg.SharedItems.RemoveAt(i);
            }
            cfg.Save();
            WriteJson(s, 200, Json.D("ok", true));
        }

        void LocalFs(NetworkStream s, Dictionary<string, string> qs)
        {
            string p = Str(qs, "path");
            List<object> dirs = new List<object>();
            List<object> files = new List<object>();
            string parent = "";
            if (p.Length == 0)
            {
                try
                {
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                    {
                        if (d.IsReady && d.DriveType == DriveType.Fixed)
                            dirs.Add(Json.D("name", d.Name, "path", d.Name));
                    }
                }
                catch { }
            }
            else
            {
                try
                {
                    if (p.Length == 2 && p[1] == ':') p = p + "\\";
                    DirectoryInfo di = new DirectoryInfo(p);
                    parent = di.Parent != null ? di.Parent.FullName : "";
                    foreach (DirectoryInfo sub in di.GetDirectories())
                        dirs.Add(Json.D("name", sub.Name, "path", sub.FullName));
                    foreach (FileInfo f in di.GetFiles())
                        files.Add(Json.D("name", f.Name, "size", f.Length, "path", f.FullName));
                }
                catch (Exception ex)
                {
                    WriteJson(s, 200, Json.D("ok", false, "error", "无法读取该目录: " + ex.Message));
                    return;
                }
            }
            WriteJson(s, 200, Json.D("ok", true, "path", p, "parent", parent, "dirs", dirs, "files", files));
        }

        void RevealPath(string p, bool select)
        {
            try
            {
                if (string.IsNullOrEmpty(p)) return;
                if (select && File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
                else
                {
                    if (Directory.Exists(p)) Directory.CreateDirectory(p);
                    if (Directory.Exists(p)) Process.Start(p);
                    else if (File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
                }
            }
            catch (Exception ex) { Log.W("打开位置失败 " + ex.Message); }
        }

        bool FirewallFix()
        {
            cfg.FirewallDone = Util.EnsureFirewallRules();
            cfg.Save();
            return cfg.FirewallDone;
        }

        string InfoText()
        {
            int files;
            lock (cfg.SharedItems) { files = cfg.SharedItems.Count; }
            return "app=FindYou\nver=4\nid=" + cfg.PeerId + "\nname=" + Util.UrlEnc(cfg.DeviceName)
                 + "\nips=" + Util.UrlEnc(string.Join(",", Util.RealIPs().ToArray()))
                 + "\ntcp=" + cfg.TcpPort + "\nfiles=" + (cfg.SharingEnabled ? files : 0)
                 + "\nshare=" + (cfg.SharingEnabled ? "1" : "0") + "\n";
        }

        bool ReadHead(NetworkStream s, out byte[] head, out byte[] extra)
        {
            head = null; extra = null;
            byte[] buf = new byte[16384];
            int len = 0;
            while (len < buf.Length)
            {
                int n = s.Read(buf, len, buf.Length - len);
                if (n <= 0) return false;
                len += n;
                int idx = IndexOfDoubleCRLF(buf, len);
                if (idx >= 0)
                {
                    head = new byte[idx];
                    Array.Copy(buf, 0, head, 0, idx);
                    extra = new byte[len - idx - 4];
                    Array.Copy(buf, idx + 4, extra, 0, len - idx - 4);
                    return true;
                }
            }
            return false;
        }

        static int IndexOfDoubleCRLF(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        void WriteHead(NetworkStream s, int code, string status, long contentLen, Dictionary<string, string> extra)
        {
            StringBuilder h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(code).Append(' ').Append(status).Append("\r\n");
            if (extra != null) foreach (KeyValuePair<string, string> kv in extra) h.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            h.Append("Content-Length: ").Append(contentLen).Append("\r\n");
            h.Append("Access-Control-Allow-Origin: *\r\nConnection: keep-alive\r\nKeep-Alive: timeout=15\r\n\r\n");
            byte[] hb = Encoding.ASCII.GetBytes(h.ToString());
            s.Write(hb, 0, hb.Length);
        }

        void WriteText(NetworkStream s, int code, string body)
        {
            byte[] b = new UTF8Encoding(false).GetBytes(body);
            Dictionary<string, string> extra = new Dictionary<string, string>();
            extra["Content-Type"] = "text/plain; charset=utf-8";
            WriteHead(s, code, code == 200 ? "OK" : "Error", b.Length, extra);
            s.Write(b, 0, b.Length);
        }

        void WriteJson(NetworkStream s, int code, object o)
        {
            byte[] b = new UTF8Encoding(false).GetBytes(Json.Of(o));
            Dictionary<string, string> extra = new Dictionary<string, string>();
            extra["Content-Type"] = "application/json; charset=utf-8";
            extra["Cache-Control"] = "no-store";
            WriteHead(s, code, code == 200 ? "OK" : "Error", b.Length, extra);
            s.Write(b, 0, b.Length);
        }

        void AppendItem(StringBuilder sb, string name, long size, long mtimeUtc, bool isDir, string token)
        {
            sb.Append("item=").Append(Util.UrlEnc(name)).Append('|').Append(size).Append('|').Append(mtimeUtc)
              .Append('|').Append(isDir ? "1" : "0").Append('|').Append(Util.UrlEnc(token)).Append('\n');
        }

        void HandleShared(NetworkStream s, Dictionary<string, string> qs)
        {
            if (!cfg.SharingEnabled) { WriteText(s, 200, "ERR\nSHARING_OFF"); return; }
            string dir;
            qs.TryGetValue("dir", out dir);
            StringBuilder sb = new StringBuilder();

            if (string.IsNullOrEmpty(dir))
            {
                sb.Append("OK\nparent=\n");
                lock (cfg.SharedItems)
                {
                    foreach (ShareItem si in cfg.SharedItems)
                    {
                        bool isDir = Directory.Exists(si.Path), isFile = File.Exists(si.Path);
                        if (!isDir && !isFile) continue;
                        string nm = "";
                        try { nm = isDir ? new DirectoryInfo(si.Path).Name : Path.GetFileName(si.Path); } catch { }
                        if (nm.Length == 0) nm = si.Path;
                        long mt = 0;
                        try { mt = (isDir ? Directory.GetLastWriteTimeUtc(si.Path) : File.GetLastWriteTimeUtc(si.Path)).ToFileTime(); } catch { }
                        long sz = 0;
                        if (isFile) { try { sz = new FileInfo(si.Path).Length; } catch { } }
                        AppendItem(sb, nm, sz, mt, isDir, isDir ? si.Id + "|\\" : si.Id + "|");
                    }
                }
                WriteText(s, 200, sb.ToString());
                return;
            }

            int pi = dir.IndexOf('|');
            if (pi <= 0) { WriteText(s, 200, "ERR\nBADTOKEN"); return; }
            Guid g;
            if (!Guid.TryParse(dir.Substring(0, pi), out g)) { WriteText(s, 200, "ERR\nBADTOKEN"); return; }
            string rel = dir.Substring(pi + 1).Replace('/', '\\');
            if (rel.Contains("..")) { WriteText(s, 200, "ERR\nBAD"); return; }
            string relN = rel.TrimEnd('\\');
            ShareItem si2 = FindShare(g);
            if (si2 == null) { WriteText(s, 200, "ERR\nGONE"); return; }
            if (!Directory.Exists(si2.Path)) { WriteText(s, 200, "ERR\nNOTDIR"); return; }

            string rootFull;
            try { rootFull = Path.GetFullPath(si2.Path); } catch { WriteText(s, 200, "ERR\nGONE"); return; }
            string baseFull;
            try { baseFull = Path.GetFullPath(relN.Length == 0 ? rootFull : Path.Combine(rootFull, relN)); }
            catch { WriteText(s, 200, "ERR\nBAD"); return; }
            string pref = rootFull.EndsWith("\\") ? rootFull : rootFull + "\\";
            if (relN.Length == 0)
            {
                if (!string.Equals(baseFull, rootFull, StringComparison.OrdinalIgnoreCase)) { WriteText(s, 200, "ERR\nBAD"); return; }
            }
            else if (!baseFull.StartsWith(pref, StringComparison.OrdinalIgnoreCase)) { WriteText(s, 200, "ERR\nBAD"); return; }

            sb.Append("OK\n");
            if (rel == "\\") sb.Append("parent=@\n");
            else if (relN.Length == 0) sb.Append("parent=\n");
            else
            {
                string pr = "";
                try { string d = Path.GetDirectoryName(relN); if (d != null) pr = d; } catch { }
                if (pr.Length == 0) pr = "\\";
                sb.Append("parent=").Append(Util.UrlEnc(si2.Id + "|" + pr)).Append('\n');
            }
            try
            {
                List<string> dirs = new List<string>(Directory.GetDirectories(baseFull));
                dirs.Sort(StringComparer.CurrentCultureIgnoreCase);
                foreach (string d in dirs)
                {
                    long mt = 0;
                    try { mt = Directory.GetLastWriteTimeUtc(d).ToFileTime(); } catch { }
                    AppendItem(sb, Path.GetFileName(d), 0, mt, true, si2.Id + "|" + RelOf(d, rootFull));
                }
                List<string> fs2 = new List<string>(Directory.GetFiles(baseFull));
                fs2.Sort(StringComparer.CurrentCultureIgnoreCase);
                foreach (string f in fs2)
                {
                    FileInfo fi = new FileInfo(f);
                    AppendItem(sb, fi.Name, fi.Length, fi.LastWriteTimeUtc.ToFileTime(), false, si2.Id + "|" + RelOf(f, rootFull));
                }
            }
            catch (Exception ex) { Log.W("列目录失败 " + ex.Message); }
            WriteText(s, 200, sb.ToString());
        }

        static string RelOf(string full, string root)
        {
            string pref = root.EndsWith("\\") ? root : root + "\\";
            if (full.StartsWith(pref, StringComparison.OrdinalIgnoreCase)) return full.Substring(pref.Length);
            return "";
        }

        ShareItem FindShare(Guid g)
        {
            lock (cfg.SharedItems)
            {
                foreach (ShareItem x in cfg.SharedItems) if (x.Id == g) return x;
            }
            return null;
        }

        bool ResolveToken(Dictionary<string, string> qs, out ShareItem si, out string rel)
        {
            si = null; rel = "";
            string tok;
            if (!qs.TryGetValue("token", out tok) || tok.Length == 0) return false;
            int i = tok.IndexOf('|');
            if (i <= 0) return false;
            Guid g;
            if (!Guid.TryParse(tok.Substring(0, i), out g)) return false;
            rel = tok.Substring(i + 1).Replace('/', '\\');
            si = FindShare(g);
            return si != null;
        }

        void HandleFile(NetworkStream s, Dictionary<string, string> qs, Dictionary<string, string> headers, bool headOnly)
        {
            ShareItem si; string rel;
            if (!ResolveToken(qs, out si, out rel)) { WriteText(s, 404, "ERR\nGONE"); return; }
            if (rel.Contains("..")) { WriteText(s, 403, "ERR\nBAD"); return; }
            rel = rel.TrimEnd('\\');
            if (!cfg.SharingEnabled) { WriteText(s, 404, "ERR\nSHARING_OFF"); return; }

            string rootFull;
            try { rootFull = Path.GetFullPath(si.Path); } catch { WriteText(s, 404, "ERR\nGONE"); return; }
            string fullF;
            try { fullF = Path.GetFullPath(rel.Length == 0 ? rootFull : Path.Combine(rootFull, rel)); }
            catch { WriteText(s, 404, "ERR\nBAD"); return; }

            bool ok;
            if (rel.Length == 0)
                ok = string.Equals(fullF, rootFull, StringComparison.OrdinalIgnoreCase) && File.Exists(fullF);
            else
            {
                string pref = rootFull.EndsWith("\\") ? rootFull : rootFull + "\\";
                ok = fullF.StartsWith(pref, StringComparison.OrdinalIgnoreCase) && File.Exists(fullF);
            }
            if (!ok) { WriteText(s, 404, "ERR\nNOTFOUND"); return; }

            FileInfo fi;
            try { fi = new FileInfo(fullF); } catch { WriteText(s, 404, "ERR\nNOTFOUND"); return; }
            long size = fi.Length;

            long start = 0, end = size - 1;
            bool partial = false;
            string range;
            if (headers.TryGetValue("Range", out range) && range != null && range.StartsWith("bytes="))
            {
                string spec = range.Substring(6);
                int dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    string a = spec.Substring(0, dash).Trim();
                    string b = spec.Substring(dash + 1).Trim();
                    long sa, sb2;
                    if (a.Length == 0)
                    {
                        long suf;
                        if (long.TryParse(b, out suf) && suf > 0) { start = Math.Max(0, size - suf); end = size - 1; partial = true; }
                    }
                    else if (long.TryParse(a, out sa))
                    {
                        start = Math.Max(0, sa);
                        end = size - 1;
                        if (b.Length > 0 && long.TryParse(b, out sb2)) end = Math.Min(sb2, size - 1);
                        if (start >= size || start > end) { WriteText(s, 416, "ERR\nRANGE"); return; }
                        partial = true;
                    }
                }
            }

            string dlv;
            bool dlFlag = qs.TryGetValue("dl", out dlv) && dlv == "1";
            Dictionary<string, string> extra = new Dictionary<string, string>();
            extra["Content-Type"] = Util.ContentType(fi.Extension);
            extra["Accept-Ranges"] = "bytes";
            if (partial) extra["Content-Range"] = "bytes " + start + "-" + end + "/" + size;
            extra["Content-Disposition"] = (dlFlag ? "attachment" : "inline")
                + "; filename*=UTF-8''" + Util.UrlEnc(Path.GetFileName(fullF));
            WriteHead(s, partial ? 206 : 200, partial ? "Partial Content" : "OK", end - start + 1, extra);
            if (headOnly) return;

            try
            {
                using (FileStream fs = new FileStream(fullF, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[256 * 1024];
                    long remain = end - start + 1;
                    while (remain > 0)
                    {
                        int n = fs.Read(buf, 0, (int)Math.Min((long)buf.Length, remain));
                        if (n <= 0) break;
                        s.Write(buf, 0, n);
                        remain -= n;
                    }
                }
            }
            catch (Exception ex) { Log.W("文件传输中断 " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ 托盘应用（主入口）
    class TrayApp : ApplicationContext
    {
        AppConfig cfg;
        Mutex mtx;
        HistoryStore hist;
        DiscoveryService disc;
        HttpFileServer srv;
        DownloadManager dl;
        CloudService cloud;
        RelayService relay;
        PushService push;
        NotifyIcon tray;
        Icon trayIcon;
        Form syncForm;
        DropWindow dropWin;
        AppWindow appWindow;
        NativeWorkspace nativeWorkspace;
        EventWaitHandle showWindow;
        RegisteredWaitHandle showWindowWait;
        int firewallRepairing;

        public TrayApp(AppConfig c, Mutex m)
        {
            cfg = c;
            mtx = m;

            syncForm = new Form();
            syncForm.ShowInTaskbar = false;
            syncForm.FormBorderStyle = FormBorderStyle.None;
            syncForm.WindowState = FormWindowState.Minimized;
            syncForm.Visible = false;
            IntPtr h = syncForm.Handle;

            hist = new HistoryStore(cfg.DataDir);
            disc = new DiscoveryService(cfg);
            disc.Start();
            relay = new RelayService(cfg, disc, hist, BalloonSafe, RelayPushAccepted);
            dl = new DownloadManager(cfg, hist, BalloonSafe, relay);
            push = new PushService(cfg, hist, disc, BalloonSafe, relay);
            srv = new HttpFileServer(cfg, disc, hist, dl, push);
            nativeWorkspace = new NativeWorkspace(cfg, disc, srv, hist);
            srv.Native = nativeWorkspace;
            srv.QuitRequested = Quit;
            srv.PickFolderDlg = delegate { return NativeDialogs.PickFolder(appWindow, "选择文件夹 · 可在地址栏粘贴路径"); };
            srv.PickFilesDlg = delegate { return NativeDialogs.PickFiles(appWindow, "选择文件 · 可在地址栏粘贴路径，支持多选"); };
            srv.ShowDropWindow = ShowDropWindow;
            srv.balloonSink = BalloonSafe;
            srv.StaInvoke = delegate(Action a)
            {
                try { syncForm.BeginInvoke((MethodInvoker)delegate { try { a(); } catch (Exception ex) { Log.W("STA " + ex.Message); } }); }
                catch (Exception ex) { Log.W("STA 编组失败 " + ex.Message); }
            };
            srv.Start();
            cfg.Save();

            if (cfg.CloudEnabled)
            {
                cloud = new CloudService(cfg, disc);
                cloud.Start();
            }
            srv.SetServices(cloud, relay);
            if (cfg.RelayUrl.Trim().Length > 0) relay.Start();
            srv.CloudRefresh = RefreshCloud;

            tray = new NotifyIcon();
            tray.Icon = trayIcon = CreateTrayIcon();
            tray.Text = "FindYou — " + cfg.DeviceName + " 运行中";
            ContextMenuStrip tmenu = new ContextMenuStrip();
            tmenu.Items.Add("打开控制界面", null, delegate { OpenUi(); });
            tmenu.Items.Add("打开下载文件夹", null, delegate
            {
                try { Directory.CreateDirectory(cfg.DownloadDir); Process.Start(cfg.DownloadDir); } catch { }
            });
            tmenu.Items.Add(new ToolStripSeparator());
            tmenu.Items.Add("退出", null, delegate { Quit(); });
            tray.ContextMenuStrip = tmenu;
            tray.DoubleClick += delegate { OpenUi(); };
            tray.Visible = true;

            showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, Util.ShowEventName(cfg.DataDir));
            showWindowWait = ThreadPool.RegisterWaitForSingleObject(showWindow, delegate
            {
                try { syncForm.BeginInvoke((MethodInvoker)delegate { OpenUi(); }); } catch { }
            }, null, Timeout.Infinite, false);
            Util.UpdateAutoStartCommand(cfg.DataDir);

            if (cfg.AutoOpenUi) OpenUi();
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

        static Icon CreateTrayIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
            using (Graphics g = Graphics.FromImage(bitmap))
            using (GraphicsPath shape = new GraphicsPath())
            using (Pen arrow = new Pen(Color.FromArgb(13, 16, 18), 2.3f))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(125, 239, 219)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                shape.AddArc(2, 2, 28, 28, 180, 90);
                shape.AddArc(2, 2, 28, 28, 270, 90);
                shape.AddArc(2, 2, 28, 28, 0, 90);
                shape.AddArc(2, 2, 28, 28, 90, 90);
                shape.CloseFigure();
                g.FillPath(fill, shape);
                g.DrawLine(arrow, 7, 11, 24, 11);
                g.DrawLine(arrow, 19, 7, 24, 11);
                g.DrawLine(arrow, 19, 15, 24, 11);
                g.DrawLine(arrow, 25, 21, 8, 21);
                g.DrawLine(arrow, 13, 17, 8, 21);
                g.DrawLine(arrow, 13, 25, 8, 21);
            }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon source = Icon.FromHandle(handle)) return (Icon)source.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        void RefreshCloud()
        {
            try
            {
                // 云端：配置变化时重建
                if (cloud != null) { cloud.Stop(); cloud = null; }
                if (cfg.CloudEnabled) { cloud = new CloudService(cfg, disc); cloud.Start(); }
                srv.SetServices(cloud, relay);
            }
            catch (Exception ex) { Log.W("云端服务重建失败 " + ex.Message); }
            try
            {
                // 中转：URL 变化时重建
                bool needRelay = cfg.RelayUrl.Trim().Length > 0;
                if (relay != null) { relay.Stop(); relay = null; }
                if (needRelay) { relay = new RelayService(cfg, disc, hist, BalloonSafe, RelayPushAccepted); relay.Start(); }
                dl.SetRelay(relay);
                srv.SetServices(cloud, relay);
            }
            catch (Exception ex) { Log.W("中转服务重建失败 " + ex.Message); }
        }

        T StaFunc<T>(Func<T> f)
        {
            T result = default(T);
            ManualResetEvent done = new ManualResetEvent(false);
            try
            {
                syncForm.BeginInvoke((MethodInvoker)delegate { try { result = f(); } finally { done.Set(); } });
                done.WaitOne();
            }
            catch (Exception ex) { Log.W("STA 调用失败 " + ex.Message); }
            return result;
        }

        // 中转 offer 已被对方接受（RelayService 已建接收记录并回 want）
        void RelayPushAccepted(Dictionary<string, string> kv, HistoryEntry e)
        {
            // 记录已由 RelayService 处理；此回调保留给未来的手动确认模式
        }

        void ShowDropWindow()
        {
            try
            {
                syncForm.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        if (dropWin != null && dropWin.Visible) { dropWin.Activate(); return; }
                        dropWin = new DropWindow();
                        dropWin.OnDropped = delegate(List<string> files)
                        {
                            int ok = 0, fail = 0;
                            foreach (string f in files)
                            {
                                bool isF = File.Exists(f), isD = Directory.Exists(f);
                                if (!isF && !isD) { fail++; continue; }
                                lock (cfg.SharedItems)
                                {
                                    bool dup = false;
                                    foreach (ShareItem x in cfg.SharedItems)
                                        if (string.Equals(x.Path, f, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                                    if (dup) { fail++; continue; }
                                    cfg.SharedItems.Add(new ShareItem { Id = Guid.NewGuid(), Path = f });
                                }
                                ok++;
                            }
                            if (ok > 0) cfg.Save();
                            BalloonSafe("拖放添加共享", "成功 " + ok + " 项" + (fail > 0 ? "，失败 " + fail + " 项" : ""));
                        };
                        dropWin.Show();
                    }
                    catch { }
                });
            }
            catch { }
        }

        void BalloonSafe(string title, string text)
        {
            try
            {
                syncForm.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        tray.BalloonTipTitle = title;
                        tray.BalloonTipText = text;
                        tray.ShowBalloonTip(2500);
                    }
                    catch { }
                });
            }
            catch { }
        }

        public void OpenUi()
        {
            try
            {
                if (appWindow == null || appWindow.IsDisposed) appWindow = new AppWindow(nativeWorkspace, trayIcon);
                appWindow.ShowWorkspace();
                RepairFirewallForCurrentExecutable();
            }
            catch (Exception ex) { Log.W("打开界面失败 " + ex.Message); }
        }

        void RepairFirewallForCurrentExecutable()
        {
            if (Util.FirewallRulesReady())
            {
                if (!cfg.FirewallDone) { cfg.FirewallDone = true; cfg.Save(); }
                return;
            }
            if (Interlocked.Exchange(ref firewallRepairing, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ready = Util.EnsureFirewallRules();
                cfg.FirewallDone = ready;
                cfg.Save();
                if (nativeWorkspace != null) nativeWorkspace.Notify(ready ? "当前程序已获得局域网访问权限" : "防火墙放行未完成，可在设置中重试");
                if (!ready) Interlocked.Exchange(ref firewallRepairing, 0);
            });
        }

        void Quit()
        {
            try
            {
                if (showWindowWait != null) showWindowWait.Unregister(null);
                if (showWindow != null) showWindow.Dispose();
                if (appWindow != null) appWindow.Dispose();
                if (nativeWorkspace != null) nativeWorkspace.Dispose();
                if (cloud != null) cloud.Stop();
                if (relay != null) relay.Stop();
                disc.Stop();
                srv.Stop();
                cfg.Save();
                hist.Save();
                tray.Visible = false;
                tray.Dispose();
                if (trayIcon != null) trayIcon.Dispose();
                syncForm.Dispose();
                mtx.ReleaseMutex();
            }
            catch { }
            Log.W("===== FindYou 退出 =====");
            Application.Exit();
        }
    }
}
