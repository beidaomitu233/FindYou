// Native HTTP transport. The discovery server never carries file bytes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FindYou
{
    partial class HttpFileServer
    {
        class DirectPeer
        {
            public string BaseUrl;
            public bool Batch;
            public DateTime Last = DateTime.UtcNow;
        }
        readonly Dictionary<string, DirectPeer> directPeers = new Dictionary<string, DirectPeer>();
        System.Threading.Timer transferCleanup;

        void ReceiveFileStream(NetworkStream stream, Dictionary<string, string> args, long length, byte[] prefix)
        {
            ReceiveBatch batch = null;
            try
            {
                batch = GetBatch(args);
                lock (batch)
                {
                    if (!batch.Remote || batch.Finished || batch.Stream == null || length < 0
                        || Num(args, "offset", -1) != batch.FileDone || length != batch.FileSize - batch.FileDone
                        || (prefix != null && prefix.Length > length)) throw new Exception("文件流与接收清单不一致");
                    byte[] buffer = new byte[128 * 1024];
                    long remaining = length;
                    if (prefix != null && prefix.Length > 0)
                    {
                        batch.Stream.Write(prefix, 0, prefix.Length);
                        batch.FileDone += prefix.Length; batch.Done += prefix.Length; remaining -= prefix.Length;
                    }
                    while (remaining > 0)
                    {
                        if (stop) throw new IOException("应用已退出");
                        int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (count == 0) throw new IOException("传输连接中断");
                        batch.Stream.Write(buffer, 0, count);
                        batch.FileDone += count; batch.Done += count; remaining -= count; batch.Last = DateTime.UtcNow;
                        if (batch.History != null) { batch.History.Progress = batch.Total == 0 ? 0 : (int)Math.Min(99, batch.Done * 100 / batch.Total); batch.History.TransferRate = (long)(batch.Done / Math.Max(.001, batch.Clock.Elapsed.TotalSeconds)); }
                    }
                }
                WriteJson(stream, 200, Json.D("ok", true));
            }
            catch (Exception ex)
            {
                if (batch != null) AbortBatch(batch, "文件流中断");
                WriteJson(stream, 400, Json.D("ok", false, "error", ex.Message));
                throw;
            }
        }

        void CleanupTransfers(object unused)
        {
            ReceiveBatch[] stale;
            lock (workspaceGate)
            {
                stale = batches.Values.Where(x => DateTime.UtcNow - x.Last > TimeSpan.FromMinutes(5)).ToArray();
                foreach (string key in directPeers.Where(x => DateTime.UtcNow - x.Value.Last > TimeSpan.FromHours(2)).Select(x => x.Key).ToArray()) directPeers.Remove(key);
            }
            foreach (ReceiveBatch b in stale) { try { AbortBatch(b, "接收超时"); } catch { } }
            PushIncoming[] old;
            lock (pushGate) old = pushes.Values.Where(x => DateTime.Now - x.Last > TimeSpan.FromMinutes(5)).ToArray();
            foreach (PushIncoming inc in old) { lock (inc) FailPush(inc, "接收超时"); }
        }

        // Probe only: retrying a read across candidate addresses cannot duplicate a transfer.
        void OpenDirect(NetworkStream s, Dictionary<string, string> p)
        {
            PeerInfo peer = disc.Find(Str(p, "peerId"));
            if (peer == null) throw new Exception("对方已离线或不存在");
            foreach (string ip in peer.Ips.ToArray())
            {
                try
                {
                    string url = "http://" + ip + ":" + peer.TcpPort;
                    HttpWebRequest request = PeerClient.Req(url + "/api/info");
                    string info;
                    using (WebResponse response = request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) info = reader.ReadToEnd();
                    string[] lines = info.Replace("\r", "").Split('\n');
                    if (!lines.Contains("id=" + peer.Id)) continue;
                    string id = Guid.NewGuid().ToString("N");
                    bool batch = lines.Contains("transfer=1");
                    lock (workspaceGate) directPeers[id] = new DirectPeer { BaseUrl = url, Batch = batch };
                    WriteJson(s, 200, Json.D("ok", true, "connection", id, "batch", batch));
                    return;
                }
                catch { }
            }
            throw new Exception("局域网直连不可达，请检查对方 IP、端口和防火墙");
        }

        void ReceiveDirect(NetworkStream s, string op, Dictionary<string, string> p, byte[] body)
        {
            try
            {
                if (op == "begin")
                {
                    if (!cfg.AutoAcceptPush) throw new Exception("对方未开启自动接收投送");
                    p["mode"] = "receive";
                    BeginBatch(s, p, true);
                }
                else
                {
                    ReceiveBatch batch = GetBatch(p);
                    if (!batch.Remote) throw new Exception("接收任务不存在");
                    BatchOperation(s, op, p, body);
                }
            }
            catch (Exception ex) { WriteJson(s, 200, Json.D("ok", false, "error", ex.Message)); }
        }

        // Only authenticated local UI calls may use this proxy. Never accepts arbitrary URLs.
        // Each mutation targets the address selected by OpenDirect exactly once.
        void ProxyDirect(NetworkStream s, Dictionary<string, string> p, byte[] body)
        {
            DirectPeer peer;
            lock (workspaceGate)
            {
                if (!directPeers.TryGetValue(Str(p, "connection"), out peer)) throw new Exception("直连会话已过期，请重新连接");
                peer.Last = DateTime.UtcNow;
            }
            string op = Str(p, "op");
            if (!new string[] { "begin", "entry", "chunk", "end-entry", "commit", "abort" }.Contains(op)) throw new Exception("未知传输操作");
            Dictionary<string, string> args = new Dictionary<string, string>(p);
            args.Remove("connection"); args.Remove("op"); args.Remove("k");
            args["peer"] = cfg.DeviceName;
            if (!peer.Batch)
            {
                if (op == "begin" && Str(args, "isDir") == "1") throw new Exception("对方为旧版，暂不支持整夹投送；可共享文件夹让对方下载，或升级对方程序");
                if (op == "entry" || op == "end-entry") { WriteJson(s, 200, Json.D("ok", true)); return; }
                args["from"] = cfg.DeviceName;
                if (op == "chunk") args["seq"] = (Num(args, "offset", 0) / 262144).ToString();
                if (op == "commit") op = "end";
            }
            string form = string.Join("&", args.Select(x => Util.UrlEnc(x.Key) + "=" + Util.UrlEnc(x.Value)).ToArray());
            string route = (peer.Batch ? "/api/transfer/" : "/api/push/") + op;
            bool query = op == "chunk" || !peer.Batch;
            HttpWebRequest request = PeerClient.Req(peer.BaseUrl + route + (query ? "?" + form : ""));
            request.AllowAutoRedirect = false;
            request.Method = "POST";
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            byte[] payload = op == "chunk" ? body : query ? new byte[0] : Encoding.UTF8.GetBytes(form);
            request.ContentType = op == "chunk" ? "application/octet-stream" : "application/x-www-form-urlencoded";
            request.ContentLength = payload.Length;
            if (payload.Length > 0) using (Stream output = request.GetRequestStream()) output.Write(payload, 0, payload.Length);
            string result;
            using (WebResponse response = request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) result = reader.ReadToEnd();
            if (!peer.Batch)
            {
                string[] lines = result.Replace("\r", "").Split('\n');
                if (lines[0] != "OK") throw new Exception(PeerClient.ErrText(lines.Length > 1 ? lines[1] : "ERR"));
                string id = lines.FirstOrDefault(x => x.StartsWith("id="));
                WriteJson(s, 200, Json.D("ok", true, "id", id == null ? "" : id.Substring(3)));
            }
            else
            {
                byte[] data = Encoding.UTF8.GetBytes(result);
                WriteHead(s, 200, "OK", data.Length, new Dictionary<string, string> { { "Content-Type", "application/json; charset=utf-8" } });
                s.Write(data, 0, data.Length);
            }
        }
    }
}
