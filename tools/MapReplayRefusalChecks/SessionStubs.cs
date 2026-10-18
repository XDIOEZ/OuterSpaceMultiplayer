using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    // The test links production MapViewLoading.TickMapLoading,
    // OptionalPeerNotice and RejectedPeerEvictionGate. Only its game/native
    // dependencies and the transport are replaced by controlled fakes.
    internal sealed class MessageRejectionException : Exception
    { internal MessageRejectionException(string reason) : base(reason) { } }
    internal sealed class WireMessage
    {
        internal string Kind, SessionId;
        internal object Payload;
    }
    internal static class JsonData
    {
        internal static T Payload<T>(object value) { return (T)value; }
    }
    internal sealed class MapResyncRetryGate
    {
        internal void Remove(long map) { }
        internal void TrySend(long map, Action send) { send(); }
        internal void Advance(double seconds) { }
        internal void Clear() { }
    }
    internal enum MapAccessMode { Viewer, Simulator }
    internal sealed class MapAccess
    {
        internal long MapId, AuthorityEpoch, Revision;
        internal MapAccessMode Access;
    }
    internal sealed class MapSnapshot
    {
        internal long MapId, Revision;
        internal string NativeJson = "{}";
        internal Dictionary<long, string> EntityOwners = new Dictionary<long, string>();
    }
    internal sealed class PlayerFieldState
    {
        internal string PlayerId;
        internal long PersonalRevision;
    }
    internal sealed class MapView
    {
        internal MapSnapshot Snapshot;
        internal MapAccess Access;
        internal List<PlayerFieldState> Players = new List<PlayerFieldState>();
    }
    internal sealed class MapRecord
    {
        internal long AuthorityEpoch, Revision;
        internal string SimulatorPlayerId;
    }
    internal sealed class PlayerRecord
    {
        internal long CurrentMapId, PersonalRevision;
    }
    internal sealed class WorldState
    {
        internal string WorldId = "world";
        internal double RoomRealSeconds, WorldTimeMM;
        internal Dictionary<long, MapRecord> Maps = new Dictionary<long, MapRecord>();
        internal Dictionary<string, PlayerRecord> Players = new Dictionary<string, PlayerRecord>();
    }
    internal sealed class MapUpdate
    {
        internal string SessionId;
        internal long MapId, AuthorityEpoch, Revision;
        internal List<PlayerUpdate> Players;
    }
    internal sealed class PlayerUpdate
    {
        internal string PlayerId;
        internal long BaseRevision, Revision;
    }
    internal sealed class ClockState
    {
        internal bool Paused;
        internal double Speed, RoomRealSeconds, WorldTimeMM;
    }
    internal sealed class MapReference { internal long MapId; }
    internal sealed class AuthorityReady
    { internal long MapId, AuthorityEpoch, Revision; }

    internal sealed class TestBridge
    {
        internal bool Ready;
        internal bool IsMapReady(long mapId) { return Ready; }
    }
    internal sealed class TestTransport
    {
        internal readonly List<string> Sent = new List<string>();
        internal readonly List<string> Disconnected = new List<string>();
        internal Func<string, string, Exception> SendFailure;
        internal Action<string, string> OnDisconnect;
        internal int DisconnectFailures;
        internal void Send(string peer, string kind, object payload)
        {
            Exception fault = SendFailure == null ? null : SendFailure(peer, kind);
            if (fault != null) throw fault;
            Sent.Add(peer + ":" + kind);
        }
        internal void Disconnect(string peer, string reason)
        {
            Disconnected.Add(peer);
            if (DisconnectFailures > 0)
            {
                DisconnectFailures--;
                throw new IOException("synthetic disconnect failure");
            }
            if (OnDisconnect != null) OnDisconnect(peer, reason);
        }
    }

    public sealed partial class MultiplayerSession
    {
        private readonly TestTransport transport = new TestTransport();
        private readonly TestBridge bridge = new TestBridge();
        private readonly Dictionary<string, string> peers = new Dictionary<string, string>();
        private readonly Dictionary<long, MapAccess> awaitingLocalReady = new Dictionary<long, MapAccess>();
        private readonly List<string> received = new List<string>();
        private bool IsHost = true, IsConnected = true, closing;
        private string SessionId = "session", LocalPlayerId = "host", nativeFault, receivingKind, LastError;
        private long receivingMapId;
        private bool Paused;
        private double Speed = 1;
        private WorldState World = new WorldState();
        private bool HasMapCacheBarrier { get { return false; } }
        private bool HasInboundWorkBudget { get { return true; } }
        private bool FailStatus;
        private int inbound;

        private static void RequireFullCheckpoint(MapSnapshot map, long id)
        { if (map.MapId != id) throw new InvalidDataException("Map mismatch"); }
        private void Send(string peer, string kind, object payload)
        { transport.Send(peer, kind, payload); }
        private void Broadcast(string kind, object payload) { }
        private void SetStatus(string text) { LastError = text; if (FailStatus) throw new IOException("UI status failed"); }
        private void Error(string text) { SetStatus(text); }
        private void CountInboundWork() { inbound++; }
        private void ApplyLocalMapView(MapView view) { received.Add("local-view"); }
        private void ReceiveMapView(MapView view) { received.Add("view"); }
        private void RemovePeer(string peer) { peers.Remove(peer); rejectedPeerEvictions.Forget(peer); }
        private void HandleHostMessage(string peer, WireMessage message)
        {
            if (message.Kind == "reject") throw new MessageRejectionException("already expired");
            if (message.Kind == "native-fault") throw new InvalidDataException("real native commit failed");
            received.Add(peer + ":" + message.Kind);
        }
        private void HandleClientMessage(WireMessage message)
        {
            if (message.Kind == "reject") throw new MessageRejectionException("client expired");
            if (message.Kind == "native-fault") throw new InvalidDataException("real client map failed");
            received.Add("host:" + message.Kind);
        }
        private void MessageStage(string stage, long mapId = -1, string player = null) { }
        private void ReportSlowOperation(string stage, long start, long bytes = -1, long mapId = -1) { }
        private void ReportMessageFault(object item, Exception error) { }
        private void LockNativeFault(string message) { nativeFault = message; }
        private void ConfirmAuthority(string player, AuthorityReady ready) { }
        private bool MapFailing { get { return mapLoadingFailed; } }
        private void ClearMapCacheSession() { }

        internal TestTransport Network { get { return transport; } }
        internal List<string> Received { get { return received; } }
        internal IDictionary<string, string> Peers { get { return peers; } }
        internal bool Failed { get { return MapFailing || nativeFault != null; } }
        internal bool Quarantined(string peer) { return rejectedPeerEvictions.Contains(peer); }
        internal bool ReplayFlag { get { return replayingLoadingMessage; } }
        internal int Pending { get { return loadingMessages.Count; } }
        internal int HandledCount { get { return inbound; } }
        internal long PendingBytes { get { return loadingWireBytes; } }
        internal void Guest() { IsHost = false; }
        internal void StatusFails() { FailStatus = true; }
        internal void Bind(string peer, string player) { peers[peer] = player; }
        internal void NativeBarrier(bool ready)
        {
            bridge.Ready = ready;
            nativeMapLoad = new NativeMapLoad { MapId = 7, Epoch = 1, Revision = 1 };
        }
        internal bool Queue(string peer, string kind, long bytes = 20)
        {
            receivingWireBytes = bytes;
            return DeferMapLoadingMessage(peer,
                new WireMessage { Kind = kind, SessionId = SessionId });
        }
        internal void Ready() { bridge.Ready = true; }
        internal void Tick(double seconds) { TickMapLoading(seconds); }
        internal void EvictionTick(double seconds) { TickRejectedPeerEvictions(seconds); }
        internal void ForgetPeer(string peer) { peers.Remove(peer); rejectedPeerEvictions.Forget(peer); }
    }
}
