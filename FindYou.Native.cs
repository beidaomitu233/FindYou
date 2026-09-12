using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FindYou
{
    static class NativeJson
    {
        public static Dictionary<string, object> Parse(string text) { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(text); }
        public static string Text(object value) { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(value); }
        public static string Get(Dictionary<string, object> value, string key) { object result; return value.TryGetValue(key, out result) ? Convert.ToString(result) : ""; }
        public static Dictionary<string, string> Strings(Dictionary<string, object> value) { return value.ToDictionary(x => x.Key, x => Convert.ToString(x.Value)); }
        public static Dictionary<string, object> Checked(string text)
        {
            var result = Parse(text);
            if (Get(result, "ok") != "True") throw new IOException(Get(result, "error"));
            return result;
        }
    }

    sealed class NativeSession
    {
        public string Id = Guid.NewGuid().ToString("N"), Url = "", Status = "正在连接";
        public PeerInfo Peer;
        public bool Streaming, Closed;
        public int WindowBytes = 262144;
        public NativeRtc Rtc;
        public DateTime Started = DateTime.Now;
        public SemaphoreSlim SendGate = new SemaphoreSlim(2);
        public bool Connected { get { return !Closed && (Url.Length > 0 || (Rtc != null && Rtc.Connected)); } }
    }

    sealed class NativeSend
    {
        public HistoryEntry History;
        public NativeSession Session;
        public string Source;
        public bool IsDir;
        public long Done, Rate;
        public long ReadTicks, WriteTicks, SaveMs, ElapsedMs;
        public CancellationTokenSource Cancel = new CancellationTokenSource();
        public HttpWebRequest Request;
        public Task Completion;
    }

    sealed class NativeFile
    {
        public string Full, Relative;
        public bool IsDir;
        public long Size;
        public DateTime Modified;
    }

    sealed class NativeWorkspace : IDisposable
    {
        internal readonly AppConfig Config;
        internal readonly DiscoveryService Discovery;
        internal readonly HttpFileServer Server;
        internal readonly HistoryStore History;
        readonly object gate = new object();
        readonly List<NativeSend> sends = new List<NativeSend>();
        Task signalQueue = Task.FromResult(0);
        readonly Dictionary<string, List<Dictionary<string, object>>> earlyIce = new Dictionary<string, List<Dictionary<string, object>>>();
        public NativeSession Session;
        public event Action Changed;
        public event Action<string> Notice;
        bool disposed, connecting;

        public NativeWorkspace(AppConfig config, DiscoveryService discovery, HttpFileServer server, HistoryStore history)
        {
            Config = config; Discovery = discovery; Server = server; History = history;
            WorkspaceSignals.Consumer = delegate(string from, string json)
            {
                lock (gate) signalQueue = signalQueue.ContinueWith(async t => { try { await ReceiveSignal(from, NativeJson.Parse(json)); } catch (Exception ex) { Notify(ex.Message); } }).Unwrap();
            };
        }
        internal void Refresh() { var callback = Changed; if (callback != null) callback(); }
        internal void Notify(string text) { var callback = Notice; if (callback != null) callback(text); }
        public List<NativeSend> Sends { get { lock (gate) return sends.ToList(); } }
        public async Task Connect(string id, bool p2p = false)
        {
            lock (gate) { if (Session != null || connecting) return; connecting = true; }
            try
            {
                PeerInfo peer = Discovery.Find(id);
                if (peer == null) throw new IOException("设备已离线");
                var session = new NativeSession { Peer = peer };
                Session = session; Refresh();
                if (!p2p) await Task.Run(() => Probe(session));
                if (session.Closed || disposed) return;
                if (session.Url.Length > 0)
                {
                    session.Status = "已连接 · 局域网直传";
                    await Signal(session, new Dictionary<string, object> { { "kind", "http-open" } });
                }
                else
                {
                    session.Status = "正在建立加密直连";
                    session.SendGate = new SemaphoreSlim(1);
                    session.Rtc = new NativeRtc(this, session);
                    await session.Rtc.Offer();
                }
                Refresh();
            }
            catch (Exception ex) { Log.W("原生连接初始化失败 " + ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message); Disconnect(false); throw; }
            finally { connecting = false; }
        }
        void Probe(NativeSession session)
        {
            foreach (string ip in session.Peer.Ips.ToArray())
            {
                try
                {
                    string url = "http://" + ip + ":" + session.Peer.TcpPort;
                    var request = PeerClient.Req(url + "/api/info"); request.Timeout = 1500;
                    using (var response = request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string[] info = reader.ReadToEnd().Replace("\r", "").Split('\n');
                        if (!info.Contains("id=" + session.Peer.Id) || !info.Contains("transfer=1")) continue;
                        session.Url = url; session.Streaming = info.Contains("stream=1"); return;
                    }
                }
                catch { }
            }
        }
        internal Task Signal(NativeSession session, Dictionary<string, object> message)
        {
            message["sid"] = session.Id; message["id"] = Config.PeerId; message["name"] = Config.DeviceName; message["window"] = 4 * 1024 * 1024;
            return Task.Run(() => Server.SendNativeSignal(session.Peer.Id, NativeJson.Text(message)));
        }
        async Task ReceiveSignal(string from, Dictionary<string, object> message)
        {
            if (disposed || NativeJson.Get(message, "id") != from || from == Config.PeerId) return;
            string kind = NativeJson.Get(message, "kind"), id = NativeJson.Get(message, "sid");
            if (id.Length == 0) return;
            NativeSession session = Session;
            if (kind == "ice" && (session == null || session.Id != id))
            {
                string key = from + "|" + id;
                List<Dictionary<string, object>> pending;
                if (!earlyIce.TryGetValue(key, out pending))
                {
                    if (earlyIce.Count >= 16) earlyIce.Clear();
                    pending = new List<Dictionary<string, object>>(); earlyIce[key] = pending;
                }
                if (pending.Count < 64) pending.Add(message);
                return;
            }
            if (kind == "http-open" || kind == "offer")
            {
                if (session != null && session.Peer.Id == from && session.Id == id) return;
                if (kind == "http-open" && session != null && session.Peer.Id == from && session.Url.Length > 0)
                {
                    if (string.CompareOrdinal(Config.PeerId, from) > 0) session.Id = id;
                    Refresh(); return;
                }
                if (session != null && session.Peer.Id == from && !session.Connected)
                {
                    if (string.CompareOrdinal(Config.PeerId, from) < 0) return;
                    Disconnect(false); session = null;
                }
                if (session != null) return;
                PeerInfo peer = Discovery.Find(from);
                if (peer == null) return;
                session = new NativeSession { Id = id, Peer = peer }; Session = session;
                int window; if (int.TryParse(NativeJson.Get(message, "window"), out window) && window >= 262144) session.WindowBytes = Math.Min(4 * 1024 * 1024, window);
                if (kind == "http-open")
                {
                    await Task.Run(() => Probe(session));
                    if (session.Url.Length == 0) { Disconnect(false); return; }
                    session.Status = "已连接 · 局域网直传";
                }
                else
                {
                    session.SendGate = new SemaphoreSlim(1);
                    session.Rtc = new NativeRtc(this, session);
                    await session.Rtc.Answer((Dictionary<string, object>)message["sdp"]);
                    string key = from + "|" + id;
                    List<Dictionary<string, object>> pending;
                    if (earlyIce.TryGetValue(key, out pending)) { earlyIce.Remove(key); foreach (var ice in pending) await session.Rtc.Signal(ice); }
                }
                Refresh(); return;
            }
            if (session == null || session.Id != id || session.Peer.Id != from) return;
            if (kind == "bye" || kind == "reject") { Disconnect(false); Notify("对方已断开连接"); }
            else if (session.Rtc != null) { int window; if (int.TryParse(NativeJson.Get(message, "window"), out window) && window >= 262144) session.WindowBytes = Math.Min(4 * 1024 * 1024, window); await session.Rtc.Signal(message); }
        }
        public void Disconnect(bool notify = true)
        {
            var session = Session; if (session == null) return;
            if (notify) Signal(session, new Dictionary<string, object> { { "kind", "bye" } }).ContinueWith(t => { var error = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            session.Closed = true;
            foreach (var send in Sends.Where(s => s.Session == session && (s.Completion == null || !s.Completion.IsCompleted))) Cancel(send.History.Id);
            if (session.Rtc != null) session.Rtc.Dispose();
            Session = null; Refresh();
        }
        public NativeSend Send(string path)
        {
            var session = Session;
            if (session == null || !session.Connected) throw new IOException("请先连接设备");
            string full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new IOException("文件已不存在");
            var send = new NativeSend { Source = full, Session = session, IsDir = Directory.Exists(full), History = new HistoryEntry { Type = 2, Name = Path.GetFileName(full.TrimEnd('\\')), Peer = session.Peer.Name, Progress = 0, Status = "等待发送" } };
            lock (gate)
            {
                sends.RemoveAll(s => s.Completion != null && s.Completion.IsCompleted && !History.Snapshot().Contains(s.History));
                sends.Add(send);
            }
            History.Add(send.History);
            send.Completion = Task.Run(() => RunSend(send)); Refresh(); return send;
        }
        public void Cancel(string id)
        {
            var send = Sends.FirstOrDefault(s => s.History.Id == id);
            if (send == null) return;
            send.Cancel.Cancel(); var request = send.Request; if (request != null) request.Abort();
        }
        static void Walk(string full, string relative, List<NativeFile> files, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            HttpFileServer.CheckNativePath(full, relative);
            if (files.Count >= 100000) throw new IOException("一次最多发送 100000 项");
            bool dir = Directory.Exists(full);var info = new FileInfo(full);
            files.Add(new NativeFile { Full = full, Relative = relative, IsDir = dir, Size = dir ? 0 : info.Length, Modified = dir ? DateTime.MinValue : info.LastWriteTimeUtc });
            if (dir) foreach (string child in Directory.EnumerateFileSystemEntries(full)) Walk(child, relative + "/" + Path.GetFileName(child), files, cancel);
        }
        async Task RunSend(NativeSend send)
        {
            string remoteId = null; bool acquired = false; var token = send.Cancel.Token;
            Stopwatch totalClock = null;
            try
            {
                await send.Session.SendGate.WaitAsync(token); acquired = true;
                totalClock = Stopwatch.StartNew();
                var files = new List<NativeFile>(); Walk(send.Source, send.History.Name, files, token);
                send.History.Size = files.Sum(f => f.Size); send.History.Status = "发送中";
                if (send.Session.Rtc != null) { await send.Session.Rtc.SendFiles(send, files); }
                else
                {
                    var result = await Control(send, "begin", new Dictionary<string, string> { { "name", send.History.Name }, { "size", send.History.Size.ToString() }, { "count", files.Count.ToString() }, { "isDir", send.IsDir ? "1" : "0" } });
                    remoteId = NativeJson.Get(result, "id");
                    var clock = Stopwatch.StartNew();
                    foreach (var file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        await Control(send, "entry", new Dictionary<string, string> { { "id", remoteId }, { "path", file.Relative }, { "size", file.Size.ToString() }, { "isDir", file.IsDir ? "1" : "0" } });
                        if (file.IsDir) continue;
                        await Task.Run(() => Upload(send, file, remoteId, clock));
                        long saveStart = Stopwatch.GetTimestamp();
                        await Control(send, "end-entry", new Dictionary<string, string> { { "id", remoteId } });
                        send.SaveMs += (Stopwatch.GetTimestamp() - saveStart) * 1000 / Stopwatch.Frequency;
                    }
                    send.History.Status = "等待保存"; Refresh();
                    long commitStart = Stopwatch.GetTimestamp();
                    await Control(send, "commit", new Dictionary<string, string> { { "id", remoteId } }); remoteId = null;
                    send.SaveMs += (Stopwatch.GetTimestamp() - commitStart) * 1000 / Stopwatch.Frequency;
                }
                send.ElapsedMs = totalClock.ElapsedMilliseconds;
                send.History.TransferRate = (long)(send.History.Size / Math.Max(.001, totalClock.Elapsed.TotalSeconds));
                send.History.Progress = 100; send.History.Status = "已送达"; send.History.LocalPath = send.Source;
                Log.W("传输统计 transport=" + (send.Session.Rtc != null ? "p2p" : send.Session.Streaming ? "http-stream" : "http-chunk")
                    + " bytes=" + send.History.Size + " elapsedMs=" + send.ElapsedMs
                    + " readMs=" + send.ReadTicks * 1000 / Stopwatch.Frequency + " writeMs=" + send.WriteTicks * 1000 / Stopwatch.Frequency + " saveMs=" + send.SaveMs);
            }
            catch (Exception ex)
            {
                send.History.Progress = -1; send.History.Status = send.Cancel.IsCancellationRequested ? "已取消" : "失败: " + ex.Message;
                if (remoteId != null) try { Control(send, "abort", new Dictionary<string, string> { { "id", remoteId } }, true).GetAwaiter().GetResult(); } catch { }
            }
            finally { send.Request = null; send.Rate = 0; if (acquired) send.Session.SendGate.Release(); send.Session = null; History.Touch(send.History); Refresh(); }
        }
        internal static HttpWebRequest Request(string url, long length)
        {
            var request = PeerClient.Req(url); request.Method = "POST"; request.AllowAutoRedirect = false;
            request.Timeout = 60000; request.ReadWriteTimeout = 30000; request.AllowWriteStreamBuffering = false;
            request.ServicePoint.Expect100Continue = false; request.ServicePoint.UseNagleAlgorithm = false;
            request.ContentLength = length; return request;
        }
        async Task<Dictionary<string, object>> Control(NativeSend send, string operation, Dictionary<string, string> args, bool cleanup = false)
        {
            if (!cleanup) send.Cancel.Token.ThrowIfCancellationRequested();
            args["peer"] = Config.DeviceName;
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join("&", args.Select(x => Util.UrlEnc(x.Key) + "=" + Util.UrlEnc(x.Value))));
            var request = Request(send.Session.Url + "/api/transfer/" + operation, bytes.Length);
            request.ContentType = "application/x-www-form-urlencoded";
            if (!cleanup) send.Request = request;
            using (var output = await request.GetRequestStreamAsync()) await output.WriteAsync(bytes, 0, bytes.Length);
            using (var response = await request.GetResponseAsync())
            using (var reader = new StreamReader(response.GetResponseStream())) return NativeJson.Checked(await reader.ReadToEndAsync());
        }
        void Upload(NativeSend send, NativeFile file, string id, Stopwatch clock)
        {
            HttpFileServer.CheckNativePath(file.Full, file.Relative);
            var info = new FileInfo(file.Full);
            if (info.Length != file.Size || info.LastWriteTimeUtc != file.Modified) throw new IOException("源文件已变化");
            using (var input = new FileStream(file.Full, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[send.Session.Streaming ? 128 * 1024 : 1024 * 1024]; long offset = 0;
                if (send.Session.Streaming)
                {
                    var request = Request(send.Session.Url + "/api/transfer/stream?id=" + id + "&offset=0", file.Size); send.Request = request;
                    request.ContentType = "application/octet-stream";
                    using (var output = request.GetRequestStream())
                    {
                        while (offset < file.Size)
                        {
                            send.Cancel.Token.ThrowIfCancellationRequested();
                            long readStart = Stopwatch.GetTimestamp();
                            int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, file.Size - offset));
                            send.ReadTicks += Stopwatch.GetTimestamp() - readStart;
                            if (count == 0) throw new IOException("源文件读取中断");
                            long writeStart = Stopwatch.GetTimestamp();
                            output.Write(buffer, 0, count);
                            send.WriteTicks += Stopwatch.GetTimestamp() - writeStart;
                            offset += count; Progress(send, count, clock);
                        }
                    }
                    using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream())) NativeJson.Checked(reader.ReadToEnd());
                }
                else
                {
                    while (offset < file.Size)
                    {
                        send.Cancel.Token.ThrowIfCancellationRequested();
                        int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, file.Size - offset));
                        if (count == 0) throw new IOException("源文件读取中断");
                        var request = Request(send.Session.Url + "/api/transfer/chunk?id=" + id + "&offset=" + offset, count); send.Request = request; request.ContentType = "application/octet-stream";
                        using (var output = request.GetRequestStream()) output.Write(buffer, 0, count);
                        using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream())) NativeJson.Checked(reader.ReadToEnd());
                        offset += count; Progress(send, count, clock);
                    }
                }
            }
        }
        internal static void Progress(NativeSend send, int count, Stopwatch clock)
        {
            send.Done += count; send.Rate = (long)(send.Done / Math.Max(.001, clock.Elapsed.TotalSeconds));
            send.History.Progress = send.History.Size == 0 ? 0 : (int)Math.Min(99, send.Done * 100 / send.History.Size);
        }
        public object State()
        {
            var s = Session;
            return Json.D("ok", true, "connected", s != null && s.Connected, "session", s == null ? "" : s.Id, "peer", s == null ? "" : s.Peer.Id,
                "status", s == null ? "" : s.Status, "transport", s == null ? "" : s.Rtc == null ? "http" : "p2p", "streaming", s != null && s.Streaming,
                "sends", Sends.Select(x => (object)Json.D("id", x.History.Id, "name", x.History.Name, "progress", x.History.Progress, "status", x.History.Status, "rate", x.Rate,
                    "elapsedMs", x.ElapsedMs, "readMs", x.ReadTicks * 1000 / Stopwatch.Frequency, "writeMs", x.WriteTicks * 1000 / Stopwatch.Frequency, "saveMs", x.SaveMs)).ToArray());
        }
        public void Dispose()
        {
            disposed = true; WorkspaceSignals.Consumer = null; Disconnect();
            try { Task.WaitAll(Sends.Where(s => s.Completion != null).Select(s => s.Completion).ToArray(), 5000); } catch { }
        }
    }

    partial class HttpFileServer
    {
        internal NativeWorkspace Native;
        internal static void CheckNativePath(string full, string relative) { SafeRelative(relative); NoLinks(full); }
        internal void SendNativeSignal(string peer, string message) { SendWorkspaceSignal(new Dictionary<string, string> { { "peerId", peer }, { "data", message } }); }
        internal object ReceiveNativeControl(string op, Dictionary<string, string> args, byte[] bytes)
        {
            if (op == "begin") { if (!cfg.AutoAcceptPush) throw new IOException("对方未开启自动接收"); args["mode"] = "receive"; return BeginBatchCore(args, true); }
            return BatchOperationCore(op, args, bytes ?? new byte[0]);
        }
        internal string SaveNativeConfig(Dictionary<string, string> values) { return ApplyConfig(values); }
        internal void AddNativePeer(string host) { string error = disc.AddManual(host); if (error != null) throw new IOException(error); }
        void NativeApi(NetworkStream stream, string route, Dictionary<string, string> args)
        {
            if (Native == null) throw new IOException("原生工作区尚未启动");
            if (route == "connect") Native.Connect(Str(args, "peerId"), Str(args, "p2p") == "1").GetAwaiter().GetResult();
            else if (route == "disconnect") Native.Disconnect();
            else if (route == "send") { var send = Native.Send(Str(args, "path")); WriteJson(stream, 200, Json.D("ok", true, "id", send.History.Id)); return; }
            else if (route == "cancel") Native.Cancel(Str(args, "id"));
            else if (route != "state") throw new IOException("未知操作");
            WriteJson(stream, 200, Native.State());
        }
    }
}
