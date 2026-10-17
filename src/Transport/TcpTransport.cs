using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FreeWorldMultiplayer
{
    /// <summary>Independent managed TCP transport. Does not initialize or invoke Steam/Unity.</summary>
    public sealed class TcpTransport : ITransport, ITransportDrain, ITransportPeerAdmission
    {
        private sealed class Peer
        {
            public string Id;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly Queue<byte[]> Outgoing = new Queue<byte[]>();
            public readonly object Gate = new object();
            public long OutgoingBytes;
            public int InFlightBytes;
            public bool Closed;
            public bool Authenticated;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        private readonly TransportQueue events = new TransportQueue();
        private TcpListener listener;
        private TcpClient pendingClient;
        private volatile bool running;
        private volatile bool draining;
        private bool host;
        private int maxPeers;
        private int generation;
        public string Name { get { return "TCP/IP"; } }
        public string LocalPeerId { get; private set; }
        public bool IsRunning { get { return running; } }
        public void MarkAuthenticated(string peerId)
        {
            if (!host || peerId == null) return;
            Peer peer;
            lock (gate) if (!peers.TryGetValue(peerId, out peer)) return;
            lock (peer.Gate) if (!peer.Closed) peer.Authenticated = true;
        }


        public void StartHost(TransportOptions options)
        {
            if (running) throw new InvalidOperationException("TCP transport is already running.");
            Validate(options);
            host = true;
            draining = false;
            maxPeers = Math.Max(1, options.MaxPlayers - 1);
            LocalPeerId = "host";
            IPAddress bind;
            if (String.IsNullOrWhiteSpace(options.Address) || options.Address == "*") bind = IPAddress.Any;
            else if (!IPAddress.TryParse(options.Address, out bind)) throw new ArgumentException("监听地址必须是本机 IP 或 *。");
            listener = new TcpListener(bind, options.Port);
            listener.Start(maxPeers);
            running = true;
            int epoch = Interlocked.Increment(ref generation);
            Worker("FWM TCP accept", () => AcceptLoop(epoch));
        }

        public void Connect(TransportOptions options)
        {
            if (running) throw new InvalidOperationException("TCP transport is already running.");
            Validate(options);
            host = false;
            draining = false;
            LocalPeerId = "client:" + Guid.NewGuid().ToString("N");
            running = true;
            int epoch = Interlocked.Increment(ref generation);
            Worker("FWM TCP connect", () =>
            {
                TcpClient client = new TcpClient();
                try
                {
                    lock (gate) { if (!running || epoch != generation) { client.Close(); return; } pendingClient = client; }
                    var pending = client.BeginConnect(options.Address, options.Port, null, null);
                    using (pending.AsyncWaitHandle)
                    {
                        if (!pending.AsyncWaitHandle.WaitOne(15000)) throw new TimeoutException("连接房主超时（15 秒）。");
                        client.EndConnect(pending);
                    }
                    if (!running || epoch != generation) { client.Close(); return; }
                    AddPeer(client, "host", epoch);
                }
                catch (Exception ex)
                {
                    client.Close();
                    if (epoch == generation)
                    {
                        running = false;
                        events.Enqueue(new TransportEvent { Kind = TransportEventKind.Error, PeerId = "host", Error = "连接失败：" + ex.Message });
                    }
                }
                finally { lock (gate) if (pendingClient == client) pendingClient = null; }
            });
        }

        private static void Validate(TransportOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (options.Port < 1 || options.Port > 65535) throw new ArgumentOutOfRangeException("Port", "端口必须为 1～65535。");
            if (options.MaxPlayers < 2 || options.MaxPlayers > 64) throw new ArgumentOutOfRangeException("MaxPlayers", "房间人数必须为 2～64。");
        }

        private void AcceptLoop(int epoch)
        {
            try
            {
                while (running && epoch == generation)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    lock (gate)
                    {
                        if (!running || draining || epoch != generation || peers.Count >= maxPeers) { client.Close(); continue; }
                        AddPeer(client, "tcp:" + Guid.NewGuid().ToString("N"), epoch);
                    }
                }
            }
            catch (Exception ex)
            {
                if (running && !draining && epoch == generation)
                {
                    running = false;
                    events.Enqueue(new TransportEvent { Kind = TransportEventKind.Error, Error = "房间监听失败：" + ex.Message });
                }
            }
        }

        private void AddPeer(TcpClient client, string id, int epoch)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 90000;
            client.SendTimeout = 30000;
            var peer = new Peer { Id = id, Client = client, Stream = client.GetStream() };
            lock (gate) peers.Add(id, peer);
            if (!events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, PeerId = id })) { ClosePeer(peer, "接收队列已满。", epoch); return; }
            Worker("FWM TCP receive", () => ReadLoop(peer, epoch));
            Worker("FWM TCP send", () => WriteLoop(peer, epoch));
        }

        private static void Worker(string name, ThreadStart action) { new Thread(action) { IsBackground = true, Name = name }.Start(); }

        private void ReadLoop(Peer peer, int epoch)
        {
            try
            {
                byte[] header = new byte[4];
                while (running && epoch == generation)
                {
                    ReadExact(peer.Stream, header, header.Length);
                    int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                    if (length <= 0 || length > TransportQueue.MaxMessageBytes) throw new InvalidDataException("收到非法消息长度：" + length);
                    bool authenticated;
                    lock (peer.Gate) authenticated = peer.Authenticated;
                    if (!UnauthenticatedFramePolicy.AllowsFrame(host, authenticated, length))
                        throw new InvalidDataException("尚未完成身份认证，消息帧大小超出握手上限。");
                    if (!events.TryReserve(length)) throw new IOException("接收与重组总容量已满。");
                    bool reservation = true;
                    try
                    {
                        byte[] payload = new byte[length];
                        ReadExact(peer.Stream, payload, length);
                        if (!running || epoch != generation) return;
                        lock (peer.Gate) if (peer.Closed) return;
                        reservation = false;
                        if (!events.EnqueueReserved(new TransportEvent { Kind = TransportEventKind.Message, PeerId = peer.Id, Data = payload })) throw new IOException("接收队列超出容量，已断开以避免丢失权威变更。");
                    }
                    finally { if (reservation) events.ReleaseReservation(length); }
                }
            }
            catch (Exception ex) { ClosePeer(peer, ex.Message, epoch); }
        }

        private static void ReadExact(Stream stream, byte[] buffer, int length)
        {
            int offset = 0;
            while (offset < length)
            {
                int count = stream.Read(buffer, offset, length - offset);
                if (count == 0) throw new EndOfStreamException("对端关闭了连接。");
                offset += count;
            }
        }

        private void WriteLoop(Peer peer, int epoch)
        {
            try
            {
                while (running && epoch == generation)
                {
                    byte[] payload = null;
                    lock (peer.Gate)
                    {
                        if (peer.Closed) return;
                        if (peer.Outgoing.Count > 0) { payload = peer.Outgoing.Dequeue(); peer.OutgoingBytes -= payload.Length; peer.InFlightBytes = payload.Length; }
                        else Monitor.Wait(peer.Gate, 1000);
                    }
                    if (payload == null) continue;
                    int length = payload.Length;
                    byte[] header = { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length };
                    peer.Stream.Write(header, 0, 4);
                    peer.Stream.Write(payload, 0, length);
                    lock (peer.Gate) peer.InFlightBytes = 0;
                }
            }
            catch (Exception ex) { ClosePeer(peer, ex.Message, epoch); }
        }

        public void Send(string peerId, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > TransportQueue.MaxMessageBytes) throw new ArgumentOutOfRangeException("data");
            Peer peer;
            lock (gate) if (!peers.TryGetValue(peerId, out peer)) throw new InvalidOperationException("连接不存在：" + peerId);
            lock (peer.Gate)
            {
                if (peer.Closed) throw new IOException("连接已关闭：" + peerId);
                if (peer.Outgoing.Count >= 512 || peer.OutgoingBytes + data.Length > TransportQueue.MaxQueuedBytes) throw new IOException("发送队列已满：" + peerId);
                peer.Outgoing.Enqueue((byte[])data.Clone());
                peer.OutgoingBytes += data.Length;
                Monitor.PulseAll(peer.Gate);
            }
        }

        public void Disconnect(string peerId, string reason)
        {
            Peer peer;
            lock (gate) if (!peers.TryGetValue(peerId, out peer)) return;
            ClosePeer(peer, reason, generation);
        }

        private void ClosePeer(Peer peer, string reason, int epoch)
        {
            lock (peer.Gate)
            {
                if (peer.Closed) return;
                peer.Closed = true;
                peer.Outgoing.Clear();
                peer.OutgoingBytes = 0;
                peer.InFlightBytes = 0;
                Monitor.PulseAll(peer.Gate);
            }
            peer.Client.Close();
            lock (gate) peers.Remove(peer.Id);
            if (epoch != generation) return;
            if (!host) running = false;
            events.Enqueue(new TransportEvent { Kind = TransportEventKind.Disconnected, PeerId = peer.Id, Error = reason });
        }

        public bool TryDequeue(out TransportEvent item) { return events.TryDequeue(out item); }
        internal TransportQueueSnapshot ReadQueueSnapshot() { return events.Snapshot(); }
        public void BeginDrain()
        {
            draining = true;
            if (listener != null) listener.Stop();
        }
        public bool IsDrained
        {
            get
            {
                lock (gate) foreach (Peer peer in peers.Values) lock (peer.Gate) if (peer.Outgoing.Count != 0 || peer.InFlightBytes != 0) return false;
                return true;
            }
        }
        public void Dispose()
        {
            running = false;
            Interlocked.Increment(ref generation);
            if (listener != null) { listener.Stop(); listener = null; }
            Peer[] all;
            lock (gate) { all = new List<Peer>(peers.Values).ToArray(); peers.Clear(); if (pendingClient != null) { pendingClient.Close(); pendingClient = null; } }
            foreach (Peer peer in all) ClosePeer(peer, "房间已关闭。", -1);
            events.Clear();
        }
    }
}
