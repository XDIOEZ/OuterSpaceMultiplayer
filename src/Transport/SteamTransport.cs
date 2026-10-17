using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Steamworks;

namespace FreeWorldMultiplayer
{
    /// <summary>Steam lobby discovery and reliable SteamNetworkingSockets P2P. Main thread only.</summary>
    public sealed class SteamTransport : ITransport, ITransportDrain, ITransportPeerAdmission
    {
        private const int VirtualPort = 17321;
        private const int FragmentPayload = 48 * 1024;
        private const int HeaderLength = 16;
        private const int Magic = 0x46574D31;
        private sealed class SendItem { public byte[] Data; public int Offset; public int Id; }
        private sealed class Peer
        {
            public string Id;
            public HSteamNetConnection Connection;
            public bool Connected;
            public bool Authenticated;
            public readonly Queue<SendItem> Outgoing = new Queue<SendItem>();
            public long OutgoingBytes;
            public byte[] Incoming;
            public int IncomingId;
            public int IncomingOffset;
            public long IncomingStarted;
        }
        private readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        private readonly TransportQueue events = new TransportQueue();
        private readonly IntPtr[] incomingPointers = new IntPtr[32];
        private HSteamListenSocket listener;
        private HSteamNetPollGroup pollGroup;
        private CSteamID lobby;
        private CSteamID owner;
        private Callback<SteamNetConnectionStatusChangedCallback_t> connections;
        private CallResult<LobbyCreated_t> created;
        private CallResult<LobbyEnter_t> joined;
        private bool host;
        private bool running;
        private bool polling;
        private bool draining;
        private int maxPeers;
        private int nextMessage;
        private long incomingBytes;
        public string Name { get { return "Steam Lobby / SteamNetworkingSockets"; } }
        public string LocalPeerId { get; private set; }
        public bool IsRunning { get { return running; } }
        public void MarkAuthenticated(string peerId)
        {
            if (!host || peerId == null) return;
            Peer peer;
            if (peers.TryGetValue(peerId, out peer) && peer.Connected) peer.Authenticated = true;
        }
        public string RoomId { get { return lobby.m_SteamID == 0 ? "" : lobby.m_SteamID.ToString(); } }

        private void Initialize(TransportOptions options, bool asHost)
        {
            if (running) throw new InvalidOperationException("Steam transport is already running.");
            if (options.MaxPlayers < 2 || options.MaxPlayers > 64) throw new ArgumentOutOfRangeException("MaxPlayers");
            // No SteamAPI.Init/RestartAppIfNecessary here: TCP must remain independent of Steam.
            if (!SteamAPI.IsSteamRunning() || !SteamUser.BLoggedOn()) throw new InvalidOperationException("Steam 未登录或游戏尚未完成 Steam 初始化；可选择 IP 直连。");
            host = asHost;
            draining = false;
            LocalPeerId = host ? "host" : "steam:" + SteamUser.GetSteamID().m_SteamID;
            maxPeers = options.MaxPlayers - 1;
            pollGroup = SteamNetworkingSockets.CreatePollGroup();
            if (pollGroup == HSteamNetPollGroup.Invalid) throw new IOException("创建 Steam 消息接收组失败。");
            connections = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(ConnectionChanged);
            running = true;
        }

        public void StartHost(TransportOptions options)
        {
            try
            {
                Initialize(options, true);
                listener = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
                if (listener == HSteamListenSocket.Invalid) throw new IOException("创建 Steam P2P 监听失败。");
                created = CallResult<LobbyCreated_t>.Create((result, failed) =>
                {
                    if (!running) return;
                    if (failed || result.m_eResult != EResult.k_EResultOK) { Fail("创建 Steam 房间失败：" + result.m_eResult); return; }
                    lobby = new CSteamID(result.m_ulSteamIDLobby);
                    owner = SteamUser.GetSteamID();
                    bool ok = SteamMatchmaking.SetLobbyData(lobby, "fwm.protocol", "1") &&
                        SteamMatchmaking.SetLobbyData(lobby, "fwm.owner", owner.m_SteamID.ToString());
                    if (!ok) Fail("Steam 房间元数据写入失败。");
                });
                created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, options.MaxPlayers));
            }
            catch { Dispose(); throw; }
        }

        public void Connect(TransportOptions options)
        {
            ulong room;
            if (!UInt64.TryParse(options.RoomId, out room) || room == 0) throw new ArgumentException("请输入有效 Steam 房间号。");
            try
            {
                Initialize(options, false);
                joined = CallResult<LobbyEnter_t>.Create((result, failed) =>
                {
                    if (!running) return;
                    if (failed || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) { Fail("加入 Steam 房间失败：" + result.m_EChatRoomEnterResponse); return; }
                    lobby = new CSteamID(result.m_ulSteamIDLobby);
                    if (SteamMatchmaking.GetLobbyData(lobby, "fwm.protocol") != "1") { Fail("该房间不是兼容的 FreeWorldMultiplayer V1 房间。"); return; }
                    owner = SteamMatchmaking.GetLobbyOwner(lobby);
                    var identity = new SteamNetworkingIdentity();
                    identity.SetSteamID(owner);
                    var connection = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
                    if (connection == HSteamNetConnection.Invalid) { Fail("连接 Steam 房主失败。"); return; }
                    if (!SteamNetworkingSockets.SetConnectionPollGroup(connection, pollGroup)) { SteamNetworkingSockets.CloseConnection(connection, 1000, "Poll group failed", false); Fail("加入 Steam 接收组失败。"); return; }
                    peers.Add("host", new Peer { Id = "host", Connection = connection });
                });
                joined.Set(SteamMatchmaking.JoinLobby(new CSteamID(room)));
            }
            catch { Dispose(); throw; }
        }

        public void InviteFriends()
        {
            if (!running || !host || lobby.m_SteamID == 0) throw new InvalidOperationException("Steam 房间尚未创建完成。");
            SteamFriends.ActivateGameOverlayInviteDialog(lobby);
        }

        private bool IsLobbyMember(CSteamID id)
        {
            if (lobby.m_SteamID == 0) return false;
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; i++) if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == id) return true;
            return false;
        }

        private void ConnectionChanged(SteamNetConnectionStatusChangedCallback_t change)
        {
            if (!running) return;
            var state = change.m_info.m_eState;
            Peer peer = null;
            foreach (var candidate in peers.Values) if (candidate.Connection == change.m_hConn) { peer = candidate; break; }
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting && host && change.m_info.m_hListenSocket == listener)
            {
                CSteamID remote = change.m_info.m_identityRemote.GetSteamID();
                string id = "steam:" + remote.m_SteamID;
                if (draining || peers.Count >= maxPeers || peers.ContainsKey(id) || !IsLobbyMember(remote)) { SteamNetworkingSockets.CloseConnection(change.m_hConn, 1001, "Room full or membership invalid", false); return; }
                if (SteamNetworkingSockets.AcceptConnection(change.m_hConn) != EResult.k_EResultOK || !SteamNetworkingSockets.SetConnectionPollGroup(change.m_hConn, pollGroup)) { SteamNetworkingSockets.CloseConnection(change.m_hConn, 1002, "Accept failed", false); return; }
                peer = new Peer { Id = id, Connection = change.m_hConn };
                peers.Add(id, peer);
            }
            if (peer == null) return;
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                if (!peer.Connected) { peer.Connected = true; events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, PeerId = peer.Id }); }
            }
            else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                Disconnect(peer.Id, "Steam 连接结束：" + change.m_info.m_eEndReason + " " + change.m_info.m_szEndDebug);
        }

        public void Send(string peerId, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > TransportQueue.MaxMessageBytes) throw new ArgumentOutOfRangeException("data", "消息须为 1 字节～128 MiB。");
            Peer peer;
            if (!peers.TryGetValue(peerId, out peer) || !peer.Connected) throw new IOException("Steam 连接尚未就绪：" + peerId);
            if (peer.Outgoing.Count >= 512 || peer.OutgoingBytes + data.Length > TransportQueue.MaxQueuedBytes) throw new IOException("Steam 发送队列已满：" + peerId);
            peer.Outgoing.Enqueue(new SendItem { Data = (byte[])data.Clone(), Id = ++nextMessage });
            peer.OutgoingBytes += data.Length;
        }

        private void Poll()
        {
            if (!running || polling) return;
            polling = true;
            try
            {
                // Lobby callbacks are dispatched by the game's existing SteamManager.Update.
                SteamNetworkingSockets.RunCallbacks();
                foreach (Peer peer in new List<Peer>(peers.Values))
                {
                    if (peer.Incoming != null && (Stopwatch.GetTimestamp() - peer.IncomingStarted) / (double)Stopwatch.Frequency > 60) { Disconnect(peer.Id, "Steam 大消息重组超时。"); continue; }
                    Flush(peer, 32);
                }
                int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, incomingPointers, incomingPointers.Length);
                if (count < 0) { Fail("Steam 消息读取失败。"); return; }
                for (int i = 0; i < count; i++)
                {
                    IntPtr pointer = incomingPointers[i];
                    try
                    {
                        var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        Peer peer = null;
                        foreach (var p in peers.Values) if (p.Connection == message.m_conn) { peer = p; break; }
                        if (peer == null) continue;
                        if (message.m_cbSize < HeaderLength || message.m_cbSize > HeaderLength + FragmentPayload) { Disconnect(peer.Id, "Steam 分片长度无效。"); continue; }
                        byte[] bytes = new byte[message.m_cbSize];
                        Marshal.Copy(message.m_pData, bytes, 0, bytes.Length);
                        Receive(peer, bytes);
                    }
                    finally { SteamNetworkingMessage_t.Release(pointer); incomingPointers[i] = IntPtr.Zero; }
                }
            }
            catch (Exception ex) { Fail("Steam 通道异常：" + ex.Message); }
            finally
            {
                // An exception while decoding one packet must also release the remainder of this native batch.
                for (int i = 0; i < incomingPointers.Length; i++) if (incomingPointers[i] != IntPtr.Zero) { SteamNetworkingMessage_t.Release(incomingPointers[i]); incomingPointers[i] = IntPtr.Zero; }
                polling = false;
            }
        }

        private void Flush(Peer peer, int budget)
        {
            while (peer.Connected && peer.Outgoing.Count != 0 && budget-- > 0)
            {
                SendItem item = peer.Outgoing.Peek();
                int size = Math.Min(FragmentPayload, item.Data.Length - item.Offset);
                byte[] packet = new byte[HeaderLength + size];
                PutInt(packet, 0, Magic); PutInt(packet, 4, item.Id); PutInt(packet, 8, item.Data.Length); PutInt(packet, 12, item.Offset);
                Buffer.BlockCopy(item.Data, item.Offset, packet, HeaderLength, size);
                IntPtr memory = Marshal.AllocHGlobal(packet.Length);
                EResult result;
                try { Marshal.Copy(packet, 0, memory, packet.Length); long message; result = SteamNetworkingSockets.SendMessageToConnection(peer.Connection, memory, (uint)packet.Length, Constants.k_nSteamNetworkingSend_Reliable, out message); }
                finally { Marshal.FreeHGlobal(memory); }
                if (result == EResult.k_EResultLimitExceeded) return; // Backpressure: keep the exact fragment queued.
                if (result != EResult.k_EResultOK) { Disconnect(peer.Id, "Steam 消息发送失败：" + result); return; }
                item.Offset += size;
                if (item.Offset == item.Data.Length) { peer.Outgoing.Dequeue(); peer.OutgoingBytes -= item.Data.Length; }
            }
        }

        private void Receive(Peer peer, byte[] packet)
        {
            int magic = GetInt(packet, 0), id = GetInt(packet, 4), total = GetInt(packet, 8), offset = GetInt(packet, 12), length = packet.Length - HeaderLength;
            if (magic != Magic || total <= 0 || total > TransportQueue.MaxMessageBytes || offset < 0 || offset > total - length || length == 0) { Disconnect(peer.Id, "Steam 分片头无效。"); return; }
            // Check the announced total before reserving memory for reassembly.
            if (!UnauthenticatedFramePolicy.AllowsFrame(host, peer.Authenticated, total))
            { Disconnect(peer.Id, "Steam 未认证消息超过握手大小上限。"); return; }
            if (peer.Incoming == null)
            {
                if (offset != 0 || !events.TryReserve(total)) { Disconnect(peer.Id, "Steam 重组顺序或容量无效。"); return; }
                try { peer.Incoming = new byte[total]; }
                catch { events.ReleaseReservation(total); throw; }
                peer.IncomingId = id; peer.IncomingOffset = 0; peer.IncomingStarted = Stopwatch.GetTimestamp(); incomingBytes += total;
            }
            if (peer.IncomingId != id || peer.Incoming.Length != total || peer.IncomingOffset != offset) { Disconnect(peer.Id, "Steam 分片不连续。"); return; }
            Buffer.BlockCopy(packet, HeaderLength, peer.Incoming, offset, length);
            peer.IncomingOffset += length;
            if (peer.IncomingOffset == total)
            {
                byte[] data = peer.Incoming; peer.Incoming = null; incomingBytes -= total;
                if (!events.EnqueueReserved(new TransportEvent { Kind = TransportEventKind.Message, PeerId = peer.Id, Data = data })) Disconnect(peer.Id, "接收队列已满。");
            }
        }

        private static void PutInt(byte[] data, int offset, int value) { data[offset] = (byte)(value >> 24); data[offset + 1] = (byte)(value >> 16); data[offset + 2] = (byte)(value >> 8); data[offset + 3] = (byte)value; }
        private static int GetInt(byte[] data, int offset) { return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]; }

        public bool TryDequeue(out TransportEvent item) { Poll(); return events.TryDequeue(out item); }
        internal TransportQueueSnapshot ReadQueueSnapshot() { return events.Snapshot(); }
        public void BeginDrain()
        {
            draining = true;
            if (host && lobby.m_SteamID != 0) SteamMatchmaking.SetLobbyJoinable(lobby, false);
        }
        public bool IsDrained
        {
            get
            {
                Poll();
                foreach (Peer peer in peers.Values)
                {
                    if (peer.Outgoing.Count != 0) return false;
                    if (!peer.Connected) continue;
                    var status = new SteamNetConnectionRealTimeStatus_t { reserved = new uint[16] };
                    var lane = new SteamNetConnectionRealTimeLaneStatus_t { reserved = new uint[10] };
                    var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(peer.Connection, ref status, 0, ref lane);
                    if (result != EResult.k_EResultOK || status.m_cbPendingReliable != 0 || status.m_cbSentUnackedReliable != 0) return false;
                }
                return true;
            }
        }
        public void Disconnect(string peerId, string reason)
        {
            Peer peer;
            if (!peers.TryGetValue(peerId, out peer)) return;
            peers.Remove(peerId);
            if (peer.Incoming != null) { incomingBytes -= peer.Incoming.Length; events.ReleaseReservation(peer.Incoming.Length); }
            peer.Incoming = null; peer.Outgoing.Clear(); peer.Connected = false;
            SteamNetworkingSockets.CloseConnection(peer.Connection, 1000, "FreeWorldMultiplayer disconnect", false);
            events.Enqueue(new TransportEvent { Kind = TransportEventKind.Disconnected, PeerId = peerId, Error = reason });
            if (!host) running = false;
        }
        private void Fail(string reason) { events.Enqueue(new TransportEvent { Kind = TransportEventKind.Error, Error = reason }); running = false; }
        public void Dispose()
        {
            running = false;
            foreach (Peer peer in peers.Values)
            {
                SteamNetworkingSockets.CloseConnection(peer.Connection, 1000, "Room closed", false);
                if (peer.Incoming != null) events.ReleaseReservation(peer.Incoming.Length);
            }
            peers.Clear(); incomingBytes = 0;
            if (listener != HSteamListenSocket.Invalid) { SteamNetworkingSockets.CloseListenSocket(listener); listener = HSteamListenSocket.Invalid; }
            if (pollGroup != HSteamNetPollGroup.Invalid) { SteamNetworkingSockets.DestroyPollGroup(pollGroup); pollGroup = HSteamNetPollGroup.Invalid; }
            if (lobby.m_SteamID != 0) { SteamMatchmaking.LeaveLobby(lobby); lobby = new CSteamID(); }
            if (connections != null) { connections.Dispose(); connections = null; }
            if (created != null) { created.Dispose(); created = null; }
            if (joined != null) { joined.Dispose(); joined = null; }
            events.Clear();
        }
    }
}
