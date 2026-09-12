// FindYou 5: validated filesystem operations shared by HTTP and native P2P sessions.
// Local filesystem access requires the UI token; Direct.cs exposes only bounded receive batches.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FindYou
{
    static class WorkspaceSignals
    {
        public static Action<string, string> Consumer;
        static readonly Queue<object> inbox = new Queue<object>();
        public static void Add(string from, string data)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(data) || data.Length > 60000) return;
            var consumer = Consumer;
            if (consumer != null) { consumer(from, data); return; }
            lock (inbox) { while (inbox.Count >= 128) inbox.Dequeue(); inbox.Enqueue(Json.D("from", from, "data", data)); }
        }
        public static object[] Drain() { lock (inbox) { object[] r = inbox.ToArray(); inbox.Clear(); return r; } }
    }

    partial class HttpFileServer
    {
        class SourceTicket { public string Path; public string ShareToken; public long Size; public DateTime Modified; public DateTime Last = DateTime.UtcNow; }
        class ReceiveBatch
        {
            public string Id, Root, Name, Mode, Peer, Final;
            public bool IsDir, Remote, Finished;
            public long Total, Done, FileSize, FileDone;
            public int Expected, Entries;
            public FileStream Stream;
            public HistoryEntry History;
            public DateTime Last = DateTime.UtcNow;
            public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        }
        readonly Dictionary<string, SourceTicket> sources = new Dictionary<string, SourceTicket>();
        readonly Dictionary<string, ReceiveBatch> batches = new Dictionary<string, ReceiveBatch>();
        readonly object workspaceGate = new object();

        public void Stop()
        {
            stop = true;
            if (transferCleanup != null) transferCleanup.Dispose();
            try { if (lsn != null) lsn.Stop(); } catch { }
            ReceiveBatch[] active; lock (workspaceGate) active = batches.Values.ToArray();
            foreach (ReceiveBatch b in active) { try { AbortBatch(b, "应用已退出"); } catch { } }
        }

        void ReceiveSignal(NetworkStream s, Dictionary<string, string> post, string remoteIp)
        {
            if (Str(post, "to") != cfg.PeerId || Str(post, "data").Length > 60000) { WriteText(s, 400, "bad signal"); return; }
            int port = (int)Num(post, "port", 0);
            if (Str(post, "from").Length >= 8 && port > 0 && port <= 65535)
                disc.UpsertPeer(Str(post, "from"), Str(post, "name"), new List<string> { remoteIp }, port, 0, true, "lan", true);
            WorkspaceSignals.Add(Str(post, "from"), Str(post, "data"));
            WriteJson(s, 200, Json.D("ok", true));
        }
        void SendWorkspaceSignal(Dictionary<string, string> p)
        {
            string peerId = Str(p, "peerId"), data = Str(p, "data");
            if (data.Length == 0 || data.Length > 60000) throw new Exception("连接信令过大");
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["type"] = "workspace"; fields["data"] = data;
            if (relay != null && relay.Ready() && relay.PostSignal(peerId, fields)) return;
            PeerInfo peer = disc.Find(peerId);
            if (peer != null)
            {
                foreach (string ip in peer.Ips)
                {
                    try
                    {
                        byte[] body = Encoding.UTF8.GetBytes("from=" + cfg.PeerId + "&to=" + Util.UrlEnc(peerId) + "&name=" + Util.UrlEnc(cfg.DeviceName) + "&port=" + cfg.TcpPort + "&data=" + Util.UrlEnc(data));
                        HttpWebRequest req = PeerClient.Req("http://" + ip + ":" + peer.TcpPort + "/api/connect/signal");
                        req.Timeout = 1800; req.Method = "POST"; req.ContentType = "application/x-www-form-urlencoded"; req.ContentLength = body.Length;
                        using (Stream st = req.GetRequestStream()) st.Write(body, 0, body.Length);
                        using (req.GetResponse()) { }
                        return;
                    }
                    catch { }
                }
            }
            if (cloud != null && cloud.SendWorkspace(peerId, data)) return;
            throw new Exception("无法发送连接请求，请确认对方在线，或配置相同的发现服务器与配对码");
        }

        // Validate every component, including Windows device names, ADS, and junctions.
        static string SafeRelative(string value)
        {
            if (string.IsNullOrEmpty(value) || Path.IsPathRooted(value)) throw new Exception("无效相对路径");
            string[] parts = value.Replace('\\', '/').Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." || part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new Exception("文件名包含不支持的字符");
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '0' && stem[3] <= '9'))
                    throw new Exception("不支持的 Windows 文件名");
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }
        static void NoLinks(string path)
        {
            string at = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(at))
            {
                if ((File.Exists(at) || Directory.Exists(at)) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                    throw new Exception("暂不支持符号链接或目录联接，请选择原始文件夹");
                at = Path.GetDirectoryName(at);
            }
        }
        string SharedPath(string token)
        {
            if (!cfg.SharingEnabled) throw new Exception("共享已暂停");
            int i = token.IndexOf('|'); Guid id;
            if (i < 1 || !Guid.TryParse(token.Substring(0, i), out id)) throw new Exception("共享已移除");
            ShareItem share = FindShare(id);
            if (share == null) throw new Exception("共享已移除");
            string root = Path.GetFullPath(share.Path), rel = token.Substring(i + 1);
            string full = rel.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, SafeRelative(rel)));
            if (rel.Length > 0 && !full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("路径越界");
            NoLinks(full);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new Exception("文件已移动或删除");
            return full;
        }
        object FileDescription(string path, string token)
        {
            bool dir = Directory.Exists(path);
            return Json.D("name", Path.GetFileName(path.TrimEnd('\\')), "isDir", dir,
                "size", dir ? 0L : new FileInfo(path).Length, "token", token);
        }
        object[] Library(string token)
        {
            List<object> items = new List<object>();
            if (token.Length == 0)
            {
                if (!cfg.SharingEnabled) return items.ToArray();
                lock (cfg.SharedItems)
                    foreach (ShareItem si in cfg.SharedItems)
                        if (File.Exists(si.Path) || Directory.Exists(si.Path)) items.Add(FileDescription(si.Path, si.Id + "|"));
            }
            else
            {
                string root = SharedPath(token);
                if (!Directory.Exists(root)) throw new Exception("不是文件夹");
                string prefix = token.EndsWith("|") ? token : token + "/";
                foreach (string path in Directory.GetFileSystemEntries(root).OrderBy(x => !Directory.Exists(x)).ThenBy(x => x))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    items.Add(FileDescription(path, prefix + Path.GetFileName(path)));
                }
            }
            return items.ToArray();
        }
        void ManifestWalk(string path, string relative, string sharedToken, List<object> items, ref long total)
        {
            if (items.Count >= 100000) throw new Exception("单次最多支持 100000 个文件和文件夹，请分批选择");
            NoLinks(path);
            bool dir = Directory.Exists(path);
            long size = dir ? 0 : new FileInfo(path).Length;
            string ticket = "";
            if (!dir)
            {
                ticket = Guid.NewGuid().ToString("N");
                lock (workspaceGate) sources[ticket] = new SourceTicket { Path = path, ShareToken = sharedToken, Size = size, Modified = File.GetLastWriteTimeUtc(path) };
                total = checked(total + size);
            }
            items.Add(Json.D("path", relative, "isDir", dir, "size", size, "ticket", ticket));
            if (dir)
                foreach (string child in Directory.GetFileSystemEntries(path).OrderBy(x => x))
                    ManifestWalk(child, relative + "/" + Path.GetFileName(child), sharedToken, items, ref total);
        }
        void ReadSource(NetworkStream s, Dictionary<string, string> p)
        {
            SourceTicket ticket;
            lock (workspaceGate) { if (!sources.TryGetValue(Str(p, "ticket"), out ticket)) throw new Exception("文件读取授权已过期，请重新选择"); ticket.Last = DateTime.UtcNow; }
            if (!string.IsNullOrEmpty(ticket.ShareToken)) SharedPath(ticket.ShareToken);
            NoLinks(ticket.Path);
            FileInfo current = new FileInfo(ticket.Path);
            if (current.Length != ticket.Size || current.LastWriteTimeUtc != ticket.Modified) throw new Exception("源文件已变化，请重新发送");
            long offset = Num(p, "offset", 0);
            using (FileStream f = new FileStream(ticket.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (offset < 0 || offset > f.Length) throw new Exception("读取位置无效");
                f.Position = offset;
                byte[] data = new byte[(int)Math.Min(256 * 1024, f.Length - offset)]; int done = 0;
                while (done < data.Length) { int n = f.Read(data, done, data.Length - done); if (n == 0) break; done += n; }
                WriteHead(s, 200, "OK", done, new Dictionary<string, string> { { "Content-Type", "application/octet-stream" }, { "Cache-Control", "no-store" } });
                s.Write(data, 0, done);
            }
        }
        void BeginBatch(NetworkStream s, Dictionary<string, string> p, bool remote = false)
        {
            WriteJson(s, 200, BeginBatchCore(p, remote));
        }
        internal object BeginBatchCore(Dictionary<string, string> p, bool remote)
        {
            string name = SafeRelative(Str(p, "name"));
            if (name.IndexOf('\\') >= 0) throw new Exception("无效顶层名称");
            long total = Num(p, "size", -1), count = Num(p, "count", 0);
            if (total < 0 || count < 1 || count > 100000) throw new Exception("无效文件清单");
            string mode = Str(p, "mode") == "share" ? "share" : "receive";
            string target = mode == "share" ? Path.Combine(cfg.DataDir, "SharedDrop") : cfg.DownloadDir;
            NoLinks(target); Directory.CreateDirectory(target);
            ReceiveBatch b = new ReceiveBatch { Id = Guid.NewGuid().ToString("N"), Name = name, Mode = mode, Peer = Str(p, "peer"),
                IsDir = Str(p, "isDir") == "1", Remote = remote, Total = total, Expected = (int)count };
            b.Root = Path.Combine(target, ".findyou-" + b.Id);
            Directory.CreateDirectory(b.Root);
            if (mode == "receive")
            {
                b.History = new HistoryEntry { Name = name, Peer = b.Peer, Size = total, Type = 0, Status = "接收中", Progress = 0 };
                hist.Add(b.History);
            }
            lock (workspaceGate) batches.Add(b.Id, b);
            return Json.D("ok", true, "id", b.Id);
        }
        ReceiveBatch GetBatch(Dictionary<string, string> p)
        {
            lock (workspaceGate) { ReceiveBatch b; if (!batches.TryGetValue(Str(p, "id"), out b)) throw new Exception("传输已结束或过期"); b.Last = DateTime.UtcNow; return b; }
        }
        void AbortBatch(ReceiveBatch b, string reason)
        {
            lock (b)
            {
                if (b.Finished) return;
                b.Finished = true;
                if (b.Stream != null) { b.Stream.Dispose(); b.Stream = null; }
                // Root is created by this process from a random ID within the chosen target directory.
                if (Directory.Exists(b.Root)) Directory.Delete(b.Root, true);
                if (b.History != null) { b.History.Status = "失败:" + reason; b.History.Progress = -1; hist.Touch(b.History); }
                lock (workspaceGate) batches.Remove(b.Id);
            }
        }
        void BatchOperation(NetworkStream s, string op, Dictionary<string, string> p, byte[] body)
        {
            WriteJson(s, 200, BatchOperationCore(op, p, body));
        }
        internal object BatchOperationCore(string op, Dictionary<string, string> p, byte[] body)
        {
            ReceiveBatch b = GetBatch(p);
            lock (b)
            {
                if (b.Finished) throw new Exception("传输已结束");
                if (op == "abort") { AbortBatch(b, "连接中断或已取消"); return Json.D("ok", true); }
                if (op == "entry")
                {
                    if (b.Stream != null || b.Entries >= b.Expected) throw new Exception("传输顺序错误");
                    string rel = SafeRelative(Str(p, "path"));
                    if (!string.Equals(rel, b.Name, StringComparison.Ordinal) && !(b.IsDir && rel.StartsWith(b.Name + "\\", StringComparison.Ordinal)))
                        throw new Exception("文件路径超出传输文件夹");
                    string full = Path.GetFullPath(Path.Combine(b.Root, rel));
                    if (!full.StartsWith(b.Root + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("路径越界");
                    NoLinks(full);
                    bool dir = Str(p, "isDir") == "1";
                    if (dir) { if (!b.IsDir) throw new Exception("传输类型错误"); Directory.CreateDirectory(full); }
                    else
                    {
                        b.FileSize = Num(p, "size", -1); b.FileDone = 0;
                        if (b.FileSize < 0 || b.FileSize > b.Total - b.Done) throw new Exception("文件大小不一致");
                        Directory.CreateDirectory(Path.GetDirectoryName(full));
                        // Network reads can be only a few KB; coalesce them before writing to disk.
                        b.Stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
                    }
                    b.Entries++;
                }
                else if (op == "chunk")
                {
                    if (b.Stream == null || Num(p, "offset", -1) != b.FileDone || body.Length > b.FileSize - b.FileDone) throw new Exception("文件分块顺序或大小错误");
                    b.Stream.Write(body, 0, body.Length); b.FileDone += body.Length; b.Done += body.Length;
                    if (b.History != null) { b.History.Progress = b.Total == 0 ? 0 : (int)Math.Min(99, b.Done * 100 / b.Total); b.History.TransferRate = (long)(b.Done / Math.Max(.001, b.Clock.Elapsed.TotalSeconds)); }
                }
                else if (op == "end-entry")
                {
                    if (b.Stream == null || b.FileDone != b.FileSize) throw new Exception("文件尚未接收完整");
                    b.Stream.Flush(true); b.Stream.Dispose(); b.Stream = null;
                }
                else if (op == "commit")
                {
                    if (b.Stream != null || b.Entries != b.Expected || b.Done != b.Total) throw new Exception("传输不完整，不能标记成功");
                    string source = Path.Combine(b.Root, b.Name);
                    b.Final = Util.UniquePath(Path.GetDirectoryName(b.Root), b.Name);
                    if (b.IsDir) Directory.Move(source, b.Final); else File.Move(source, b.Final);
                    Directory.Delete(b.Root);
                    b.Finished = true;
                    if (b.Mode == "share") { string err = AddSharePath(b.Final); if (err != null) throw new Exception(err); }
                    if (b.History != null) { b.History.LocalPath = b.Final; b.History.Progress = 100; b.History.Status = "已完成"; hist.Touch(b.History); }
                    lock (workspaceGate) batches.Remove(b.Id);
                    if (b.Mode == "receive" && balloonSink != null) balloonSink("文件已收到", b.Name + " ← " + b.Peer);
                    return Json.D("ok", true, "path", b.Final);
                }
                else throw new Exception("未知传输操作");
                return Json.D("ok", true);
            }
        }
        void WorkspaceApi(NetworkStream s, string route, Dictionary<string, string> p, Dictionary<string, string> qs, byte[] body)
        {
            if (route == "inbox")
            {
                // Cleanup interrupted transfers; live large transfers refresh Last on every chunk.
                ReceiveBatch[] stale;
                lock (workspaceGate)
                {
                    stale = batches.Values.Where(x => DateTime.UtcNow - x.Last > TimeSpan.FromMinutes(5)).ToArray();
                    foreach (string key in sources.Where(x => DateTime.UtcNow - x.Value.Last > TimeSpan.FromHours(2)).Select(x => x.Key).ToArray()) sources.Remove(key);
                }
                foreach (ReceiveBatch b in stale) { try { AbortBatch(b, "接收超时"); } catch { } }
                WriteJson(s, 200, Json.D("ok", true, "messages", WorkspaceSignals.Drain()));
            }
            else if (route == "direct-open") OpenDirect(s, p);
            else if (route == "direct" || route == "direct/chunk") ProxyDirect(s, route.EndsWith("/chunk") ? qs : p, body);
            else if (route == "signal") { SendWorkspaceSignal(p); WriteJson(s, 200, Json.D("ok", true)); }
            else if (route == "library") WriteJson(s, 200, Json.D("ok", true, "items", Library(Str(p, "token"))));
            else if (route == "manifest")
            {
                string token = Str(p, "token"), path = token.Length > 0 ? SharedPath(token) : Path.GetFullPath(Str(p, "path").Trim('"'));
                string name = Path.GetFileName(path.TrimEnd('\\')); SafeRelative(name);
                List<object> items = new List<object>(); long total = 0;
                ManifestWalk(path, name, token, items, ref total);
                WriteJson(s, 200, Json.D("ok", true, "name", name, "isDir", Directory.Exists(path), "size", total, "items", items));
            }
            else if (route == "read") ReadSource(s, p);
            else if (route == "begin") BeginBatch(s, p);
            else if (route == "report")
            {
                string status = Str(p, "status");
                Log.W("P2P " + status + " peer=" + Str(p, "peerId"));
                if (relay != null && relay.Ready()) relay.PostSignal("*", new Dictionary<string, string> { { "type", "status" }, { "status", status }, { "peer", Str(p, "peerId") } });
                WriteJson(s, 200, Json.D("ok", true));
            }
            else if (route == "sent")
            {
                HistoryEntry entry = new HistoryEntry { Type = 2, Name = Str(p, "name"), Peer = Str(p, "peer"), Size = Num(p, "size", 0),
                    Status = Str(p, "error").Length > 0 ? "失败:" + Str(p, "error") : "已发送", Progress = Str(p, "error").Length > 0 ? -1 : 100 };
                hist.Add(entry); WriteJson(s, 200, Json.D("ok", true));
            }
            else BatchOperation(s, route, route == "chunk" ? qs : p, body);
        }
    }
}
