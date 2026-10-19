using System;

namespace FreeWorldMultiplayer
{
    /// <summary>A Steam host also exposes TCP; clients explicitly select one connection method.</summary>
    public sealed class RoomTransport : ITransport, ITransportDrain, ITransportPeerAdmission, ITransportSteamIdentity
    {
        private TcpTransport tcp;
        private SteamTransport steam;
        private bool host;
        private bool clientSteam;
        private bool tcpNext;
        public string Name { get { return steam != null && tcp != null ? "TCP + Steam" : steam != null ? "Steam" : "TCP/IP"; } }
        public string LocalPeerId { get { return host ? "host" : clientSteam ? steam.LocalPeerId : tcp.LocalPeerId; } }
        public bool IsRunning { get { return (tcp != null && tcp.IsRunning) || (steam != null && steam.IsRunning); } }
        // Host identity is resolved before StartHost; callers must first select UseSteam.
        public string GetLocalSteamId() { return SteamTransport.GetCurrentLocalSteamId(); }
        public string GetRemoteSteamId(string peerId)
        {
            // The prefix selects a channel only; the account must come from its socket.
            if (peerId == null || !peerId.StartsWith("steam:", StringComparison.Ordinal)) return null;
            if (!host || steam == null) throw new InvalidOperationException("Steam 连接身份尚未验证或连接已结束。");
            string identity = steam.GetRemoteSteamId(peerId);
            if (identity == null) throw new InvalidOperationException("Steam 连接身份尚未验证或连接已结束。");
            return identity;
        }
        public void MarkAuthenticated(string peerId)
        {
            if (!host || peerId == null) return;
            if (peerId.StartsWith("steam:", StringComparison.Ordinal))
            { if (steam != null) steam.MarkAuthenticated(peerId); }
            else if (tcp != null) tcp.MarkAuthenticated(peerId);
        }
        public string RoomId { get { return steam == null ? "" : steam.RoomId; } }
        public void StartHost(TransportOptions options)
        {
            host = true;
            try
            {
                tcp = new TcpTransport(); tcp.StartHost(options);
                if (options.UseSteam) { steam = new SteamTransport(); steam.StartHost(options); }
            }
            catch { Dispose(); throw; }
        }
        public void Connect(TransportOptions options)
        {
            host = false; clientSteam = options.UseSteam;
            try
            {
                if (clientSteam) { steam = new SteamTransport(); steam.Connect(options); }
                else { tcp = new TcpTransport(); tcp.Connect(options); }
            }
            catch { Dispose(); throw; }
        }
        public void Send(string peerId, byte[] data)
        {
            if ((host && peerId.StartsWith("steam:", StringComparison.Ordinal)) || (!host && clientSteam)) steam.Send(peerId, data);
            else tcp.Send(peerId, data);
        }
        public void Disconnect(string peerId, string reason)
        {
            if ((host && peerId.StartsWith("steam:", StringComparison.Ordinal)) || (!host && clientSteam)) { if (steam != null) steam.Disconnect(peerId, reason); }
            else if (tcp != null) tcp.Disconnect(peerId, reason);
        }
        public bool TryDequeue(out TransportEvent item)
        {
            // Both channels share the main-thread message budget. Always draining Steam
            // first can indefinitely starve TCP when Steam has a sustained backlog.
            // Alternate priority when both have events; keep polling Steam when TCP is busy.
            if (tcpNext && tcp != null && tcp.TryDequeue(out item)) { tcpNext = false; return true; }
            if (steam != null && steam.TryDequeue(out item)) { tcpNext = true; return true; }
            if (!tcpNext && tcp != null && tcp.TryDequeue(out item)) { tcpNext = false; return true; }
            item = null; return false;
        }
        internal TransportQueueSnapshot ReadQueueSnapshot()
        {
            TransportQueueSnapshot tcpStats = tcp == null ? new TransportQueueSnapshot() : tcp.ReadQueueSnapshot();
            TransportQueueSnapshot steamStats = steam == null ? new TransportQueueSnapshot() : steam.ReadQueueSnapshot();
            return new TransportQueueSnapshot
            {
                Events = tcpStats.Events + steamStats.Events,
                QueuedBytes = tcpStats.QueuedBytes + steamStats.QueuedBytes,
                ReservedBytes = tcpStats.ReservedBytes + steamStats.ReservedBytes
            };
        }
        public void InviteFriends() { if (steam == null) throw new InvalidOperationException("此房间未启用 Steam。"); steam.InviteFriends(); }
        public void BeginDrain() { if (tcp != null) tcp.BeginDrain(); if (steam != null) steam.BeginDrain(); }
        public bool IsDrained { get { return (tcp == null || tcp.IsDrained) && (steam == null || steam.IsDrained); } }
        public void Dispose() { if (steam != null) { steam.Dispose(); steam = null; } if (tcp != null) { tcp.Dispose(); tcp = null; } }
    }
}
