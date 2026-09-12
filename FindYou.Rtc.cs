using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace FindYou
{
    sealed class NativeRtc : IDisposable
    {
        readonly NativeWorkspace owner;
        readonly NativeSession session;
        readonly RTCPeerConnection pc;
        RTCDataChannel channel;
        readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> replies = new ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
        readonly object receiveGate = new object();
        Task receiveQueue = Task.FromResult(0);
        readonly List<RTCIceCandidateInit> candidates = new List<RTCIceCandidateInit>();
        bool remoteSet, closed;
        int pendingBytes;
        string incomingId;
        long offset;
        bool entry;
        readonly MemoryStream pending = new MemoryStream();
        public bool Connected { get { return !closed && channel != null && channel.readyState == RTCDataChannelState.open; } }

        public NativeRtc(NativeWorkspace owner, NativeSession session)
        {
            this.owner = owner; this.session = session;
            pc = new RTCPeerConnection(new RTCConfiguration {
                iceServers = new List<RTCIceServer> { new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" }, new RTCIceServer { urls = "stun:stun.l.google.com:19302" } },
                X_ICEIncludeAllInterfaceAddresses = true
            });
            pc.onicecandidate += candidate =>
            {
                if (candidate == null || closed) return;
                owner.Signal(session, new Dictionary<string, object> { { "kind", "ice" }, { "candidate", new Dictionary<string, object> { { "candidate", candidate.candidate }, { "sdpMid", candidate.sdpMid }, { "sdpMLineIndex", candidate.sdpMLineIndex } } } }).ContinueWith(t => { var error = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            };
            pc.ondatachannel += Attach;
            pc.onconnectionstatechange += state =>
            {
                if (closed) return;
                if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed) { session.Status = "直连中断，请重新连接"; owner.Disconnect(false); }
                else if (state == RTCPeerConnectionState.disconnected) { session.Status = "连接中断，正在恢复"; owner.Refresh(); }
            };
            Task.Delay(30000).ContinueWith(t => { if (!closed && !Connected) { owner.Notify("加密直连未能建立，请检查双方网络或端口映射"); owner.Disconnect(false); } });
        }
        public async Task Offer()
        {
            Attach(await pc.createDataChannel("findyou-v5", new RTCDataChannelInit { ordered = true }));
            var offer = pc.createOffer(null); await pc.setLocalDescription(offer);
            await owner.Signal(session, new Dictionary<string, object> { { "kind", "offer" }, { "sdp", new Dictionary<string, object> { { "type", "offer" }, { "sdp", offer.sdp } } } });
        }
        public async Task Answer(Dictionary<string, object> description)
        {
            SetRemote(description);
            var answer = pc.createAnswer(null); await pc.setLocalDescription(answer);
            await owner.Signal(session, new Dictionary<string, object> { { "kind", "answer" }, { "sdp", new Dictionary<string, object> { { "type", "answer" }, { "sdp", answer.sdp } } } });
        }
        void SetRemote(Dictionary<string, object> description)
        {
            var result = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = NativeJson.Get(description, "type") == "offer" ? RTCSdpType.offer : RTCSdpType.answer, sdp = NativeJson.Get(description, "sdp") });
            if (result != SetDescriptionResultEnum.OK) throw new IOException("无法协商加密连接: " + result);
            remoteSet = true;
            foreach (var candidate in candidates) pc.addIceCandidate(candidate);
            candidates.Clear();
        }
        public Task Signal(Dictionary<string, object> message)
        {
            if (NativeJson.Get(message, "kind") == "answer") SetRemote((Dictionary<string, object>)message["sdp"]);
            else if (NativeJson.Get(message, "kind") == "ice")
            {
                var raw = (Dictionary<string, object>)message["candidate"];
                var candidate = new RTCIceCandidateInit { candidate = NativeJson.Get(raw, "candidate"), sdpMid = NativeJson.Get(raw, "sdpMid"), sdpMLineIndex = Convert.ToUInt16(raw["sdpMLineIndex"]) };
                if (remoteSet) pc.addIceCandidate(candidate); else if (candidates.Count < 64) candidates.Add(candidate);
            }
            return Task.FromResult(0);
        }
        void Attach(RTCDataChannel data)
        {
            channel = data;
            channel.onopen += () => { session.Status = "已连接 · P2P 加密直传"; owner.Refresh(); };
            channel.onclose += () => { if (!closed) owner.Disconnect(false); };
            channel.onmessage += (dc, protocol, bytes) =>
            {
                if (closed) return;
                bool binary = (uint)protocol == 53 || (uint)protocol == 57;
                Dictionary<string, object> message = null;
                try
                {
                    if (!binary)
                    {
                        if (bytes.Length > 60000) throw new IOException("控制消息过大");
                        message = NativeJson.Parse(Encoding.UTF8.GetString(bytes));
                        if (NativeJson.Get(message, "kind") == "reply")
                        {
                            TaskCompletionSource<Dictionary<string, object>> reply;
                            if (replies.TryRemove(NativeJson.Get(message, "id"), out reply))
                            {
                                string error = NativeJson.Get(message, "error");
                                if (error.Length > 0) reply.TrySetException(new IOException(error));
                                else reply.TrySetResult(message.ContainsKey("result") ? (Dictionary<string, object>)message["result"] : new Dictionary<string, object>());
                            }
                            return;
                        }
                    }
                    if (Interlocked.Add(ref pendingBytes, bytes.Length) > session.WindowBytes * 2) throw new IOException("接收缓冲区已满");
                    lock (receiveGate) receiveQueue = receiveQueue.ContinueWith(t =>
                    {
                        try { if (!closed) Receive(binary ? bytes : null, message); }
                        catch (Exception ex) { owner.Notify(ex.Message); owner.Disconnect(false); }
                        finally { Interlocked.Add(ref pendingBytes, -bytes.Length); }
                    });
                }
                catch (Exception ex) { owner.Notify(ex.Message); owner.Disconnect(false); }
            };
        }
        void SendControl(Dictionary<string, object> value)
        {
            if (!Connected) throw new IOException("加密连接已断开");
            string json = NativeJson.Text(value); if (Encoding.UTF8.GetByteCount(json) > 60000) throw new IOException("控制消息过大");
            channel.send(json);
        }
        async Task<Dictionary<string, object>> Rpc(string op, Dictionary<string, object> args, CancellationToken cancel)
        {
            string id = Guid.NewGuid().ToString("N");
            var reply = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously); replies[id] = reply;
            try
            {
                cancel.ThrowIfCancellationRequested(); SendControl(new Dictionary<string, object> { { "kind", "rpc" }, { "id", id }, { "op", op }, { "args", args } });
                if (await Task.WhenAny(reply.Task, Task.Delay(30000, cancel)) != reply.Task) { cancel.ThrowIfCancellationRequested(); throw new IOException("对方响应超时"); }
                return await reply.Task;
            }
            finally { TaskCompletionSource<Dictionary<string, object>> ignored; replies.TryRemove(id, out ignored); }
        }
        void Receive(byte[] binary, Dictionary<string, object> message)
        {
            if (binary != null)
            {
                if (incomingId == null || !entry || binary.Length > 262144 || pending.Length + binary.Length > session.WindowBytes) throw new IOException("无效文件分块");
                pending.Write(binary, 0, binary.Length); return;
            }
            string kind = NativeJson.Get(message, "kind");
            if (kind == "cancel") { AbortIncoming(); return; }
            if (kind != "rpc") return;
            var reply = new Dictionary<string, object> { { "kind", "reply" }, { "id", NativeJson.Get(message, "id") } };
            try
            {
                string op = NativeJson.Get(message, "op");var raw = (Dictionary<string, object>)message["args"];
                var args = NativeJson.Strings(raw);
                if (args.ContainsKey("isDir")) args["isDir"] = Convert.ToBoolean(raw["isDir"]) ? "1" : "0";
                if (op == "begin")
                {
                    if (incomingId != null) throw new IOException("上一项传输尚未结束");
                    args["peer"] = session.Peer.Name;
                    var result = (Dictionary<string, object>)owner.Server.ReceiveNativeControl("begin", args, null);
                    incomingId = NativeJson.Get(result, "id"); entry = false; pending.SetLength(0);
                }
                else if (op == "list") { reply["result"] = owner.Server.NativeLibrary(NativeJson.Get(raw, "token"), raw.ContainsKey("offset") ? Convert.ToInt32(raw["offset"]) : 0); }
                else if (op == "download") { owner.Send(owner.Server.NativeSharedPath(NativeJson.Get(raw, "token"))); }
                else
                {
                    if (incomingId == null) throw new IOException("接收任务不存在");
                    args["id"] = incomingId;
                    if (op == "entry") { owner.Server.ReceiveNativeControl(op, args, null); offset = 0; entry = args["isDir"] != "1"; }
                    else if (op == "flush")
                    {
                        if (!entry || Convert.ToInt64(raw["offset"]) != offset || Convert.ToInt32(raw["length"]) != pending.Length) throw new IOException("分块顺序不一致");
                        owner.Server.ReceiveNativeControl("chunk", args, pending.ToArray()); offset += pending.Length; pending.SetLength(0);
                    }
                    else if (op == "endEntry") { if (pending.Length > 0) throw new IOException("文件尚未完整写入"); owner.Server.ReceiveNativeControl("end-entry", args, null); entry = false; }
                    else if (op == "commit") { owner.Server.ReceiveNativeControl("commit", args, null); incomingId = null; }
                    else throw new IOException("未知传输操作");
                }
                if (!reply.ContainsKey("result")) reply["result"] = new Dictionary<string, object>();
            }
            catch (Exception ex) { reply["error"] = ex.Message; AbortIncoming(); }
            SendControl(reply); owner.Refresh();
        }
        public async Task SendFiles(NativeSend send, List<NativeFile> files)
        {
            CancellationToken cancel = send.Cancel.Token; var clock = Stopwatch.StartNew();
            try
            {
                await Rpc("begin", new Dictionary<string, object> { { "name", send.History.Name }, { "size", send.History.Size }, { "count", files.Count }, { "isDir", send.IsDir } }, cancel);
                foreach (var file in files)
                {
                    await Rpc("entry", new Dictionary<string, object> { { "path", file.Relative }, { "size", file.Size }, { "isDir", file.IsDir } }, cancel);
                    if (file.IsDir) continue;
                    HttpFileServer.CheckNativePath(file.Full, file.Relative);
                    var info = new FileInfo(file.Full);if (info.Length != file.Size || info.LastWriteTimeUtc != file.Modified) throw new IOException("源文件已变化");
                    using (var input = new FileStream(file.Full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                    {
                        byte[] buffer = new byte[262144]; long offset = 0;
                        while (offset < file.Size)
                        {
                            int window = 0;
                            while (window < session.WindowBytes && offset + window < file.Size)
                            {
                                cancel.ThrowIfCancellationRequested();
                                var waiting = Stopwatch.StartNew();
                                while (channel.bufferedAmount > (ulong)session.WindowBytes)
                                {
                                    if (!Connected || waiting.Elapsed.TotalSeconds > 30) throw new IOException("加密传输通道拥塞或中断");
                                    await Task.Delay(3, cancel);
                                }
                                int count = input.Read(buffer, 0, (int)Math.Min(Math.Min(buffer.Length, session.WindowBytes - window), file.Size - offset - window));
                                if (count == 0) throw new IOException("源文件读取中断");
                                channel.send(buffer, 0, count); window += count;
                            }
                            await Rpc("flush", new Dictionary<string, object> { { "offset", offset }, { "length", window } }, cancel);
                            offset += window; NativeWorkspace.Progress(send, window, clock);
                        }
                    }
                    await Rpc("endEntry", new Dictionary<string, object>(), cancel);
                }
                await Rpc("commit", new Dictionary<string, object>(), cancel);
            }
            catch { try { SendControl(new Dictionary<string, object> { { "kind", "cancel" } }); } catch { } throw; }
        }
        void AbortIncoming()
        {
            if (incomingId == null) return;
            try { owner.Server.ReceiveNativeControl("abort", new Dictionary<string, string> { { "id", incomingId } }, null); } catch { }
            incomingId = null; entry = false; pending.SetLength(0);
        }
        public void Dispose()
        {
            if (closed) return; closed = true;
            foreach (var reply in replies.Values) reply.TrySetException(new IOException("连接已结束")); replies.Clear();
            lock (receiveGate) receiveQueue = receiveQueue.ContinueWith(t => { AbortIncoming(); pending.SetLength(0); pending.Capacity = 0; pending.Dispose(); });
            pc.Close("FindYou disconnected"); pc.Dispose();
        }
    }
    partial class HttpFileServer
    {
        internal object NativeLibrary(string token, int offset)
        {
            var items = Library(token); offset = Math.Max(0, offset);
            return new Dictionary<string, object> { { "items", items.Skip(offset).Take(60).ToArray() }, { "more", offset + 60 < items.Length } };
        }
        internal string NativeSharedPath(string token) { return SharedPath(token); }
    }
}
