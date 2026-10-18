using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FreeWorldMultiplayer
{
    // This project links actual production MapSnapshotCache + MapCacheExpiry.
    // The surrounding Unity world and transport are minimal, controllable fakes.
    internal sealed class MapCacheManifest { internal List<MapCacheChunkReference> Chunks = new List<MapCacheChunkReference>(); }
    internal sealed class MapCacheChunkReference { internal string Hash; }
    internal sealed class MapCacheChunk { internal string Hash; internal byte[] Data; }
    internal sealed class MapCacheTransfer
    {
        internal MapCacheManifest Manifest = new MapCacheManifest();
        internal List<MapCacheChunk> ChangedChunks = new List<MapCacheChunk>();
    }
    internal sealed class MapCacheQuery
    {
        internal string ViewId, WorldId;
        internal long MapId, AuthorityEpoch, Revision;
        internal bool FullTerrain, IgnoreCache;
    }
    internal sealed class MapCacheOffer
    {
        internal string WorldId, ViewId;
        internal long MapId;
        internal bool FullTerrain;
        internal List<string> AvailableChunks = new List<string>();
    }
    internal sealed class MapCacheRetry
    {
        internal string ViewId;
        internal long MapId;
        internal bool FullTerrain;
    }
    internal sealed class MapSnapshot
    {
        internal long MapId, Revision;
        internal string NativeJson = "{}";
    }
    internal sealed class MapAccess { internal long MapId, AuthorityEpoch, Revision; }
    internal sealed class PlayerFieldState
    {
        internal string PlayerId = "player", NativeFieldsJson = "{}";
        internal long PersonalRevision;
    }
    internal sealed class MapView
    {
        internal MapSnapshot Snapshot;
        internal MapAccess Access;
        internal List<PlayerFieldState> Players = new List<PlayerFieldState>();
        internal string ViewId;
        internal bool FullTerrain;
        internal MapCacheTransfer Cache;
    }
    internal sealed class MapRecord
    {
        internal long MapId, AuthorityEpoch;
        internal List<string> Visitors = new List<string>();
    }
    internal sealed class WorldState
    {
        internal string WorldId = "world";
        internal Dictionary<long, MapRecord> Maps = new Dictionary<long, MapRecord>();
    }
    internal sealed class BridgeResult
    {
        internal bool SeedMismatch, NativeUnchanged;
        internal string PayloadJson;
    }
    internal sealed class TestBridge
    { internal MapSnapshot PrepareNetworkMap(MapSnapshot map) { return map; } }
    internal sealed class WireMessage
    {
        internal string Kind, SessionId;
        internal object Payload;
    }
    internal sealed class MessageRejectionException : Exception
    { internal MessageRejectionException(string message) : base(message) { } }
    internal static class JsonData
    {
        internal static T Payload<T>(object payload) { return (T)payload; }
        internal static T Read<T>(string value) { throw new InvalidDataException("synthetic test never restores JSON"); }
    }
    internal sealed class MapCacheStore
    {
        internal MapCacheStore(string a, string b) { }
        internal MapCacheManifest TryReadManifest(string world, long map) { return null; }
        internal bool ContainsContent(string hash) { return false; }
        internal byte[] TryReadContent(string hash) { return null; }
        internal void WriteContent(string hash, byte[] content) { }
        internal void WriteManifest(string world, long map, MapCacheManifest manifest) { }
    }
    internal static class MapSnapshotDeltaCodec
    {
        internal const int MaxChunks = 256;
        internal static bool FailReassembly;
        internal sealed class Encoded { }
        internal static bool IsHash(string hash) { return !string.IsNullOrEmpty(hash); }
        internal static Encoded Encode(string world, MapSnapshot snapshot) { return new Encoded(); }
        internal static MapCacheTransfer MakeTransfer(Encoded encoded, List<string> chunks) { return new MapCacheTransfer(); }
        internal static MapSnapshot ReassembleFromVerifiedCache(MapCacheTransfer cache, string world, long map,
            Func<string, byte[]> read)
        {
            if (FailReassembly) throw new InvalidDataException("synthetic corrupted chunk");
            return new MapSnapshot { MapId = map, Revision = 1 };
        }
    }
    internal sealed class TestTransport
    {
        internal readonly List<string> Sent = new List<string>();
        internal readonly List<byte[]> SentBytes = new List<byte[]>();
        internal Func<string, byte[], Exception> Failure;
        internal void Send(string peer, byte[] bytes)
        {
            Exception error = Failure == null ? null : Failure(peer, bytes);
            if (error != null) throw error;
            Sent.Add(peer + ":" + Encoding.UTF8.GetString(bytes).Split('#')[0]);
            SentBytes.Add(bytes);
        }
        internal void Disconnect(string peer, string reason) { }
    }
    public sealed partial class MultiplayerSession
    {
        private readonly string storageRoot = "synthetic";
        private readonly TestTransport transport = new TestTransport();
        private readonly TestBridge bridge = new TestBridge();
        private readonly Dictionary<string, string> peers = new Dictionary<string, string>();
        private readonly Dictionary<string, string> playerPeers = new Dictionary<string, string>();
        private readonly List<LoadingMessage> loadingMessages = new List<LoadingMessage>();
        private readonly Dictionary<long, LoadingResync> loadingResyncs = new Dictionary<long, LoadingResync>();
        private readonly Dictionary<long, object> awaitingLocalReady = new Dictionary<long, object>();
        private sealed class LoadingMessage
        {
            internal string CacheViewId;
            internal long WireBytes;
            internal MapView NetworkView;
        }
        private sealed class LoadingResync
        {
            internal long Epoch, Revision;
            internal Dictionary<string, long> Players = new Dictionary<string, long>();
        }
        private sealed class NativeMapLoad { internal long MapId; }
        private NativeMapLoad nativeMapLoad;
        private long loadingWireBytes, receivingWireBytes;
        private string SessionId = "session", nativeFault;
        private WorldState World = new WorldState();
        private bool IsHost, IsConnected = true;
        private int serial;
        internal event Action<string> MessageFaulted;

        private byte[] EncodeMessage(string kind, object payload, string requestId)
        { return Encoding.UTF8.GetBytes(kind + "#" + (++serial)); }
        private void Send(string peer, string kind, object payload)
        { transport.Send(peer, EncodeMessage(kind, payload, null)); }
        private void RequireFullCheckpoint(MapSnapshot snapshot, long map)
        {
            if (snapshot == null || snapshot.MapId != map) throw new InvalidDataException("wrong checkpoint");
        }
        private static void ValidateLoadingView(MapView view)
        { if (view == null || view.Snapshot == null || view.Access == null) throw new InvalidDataException("bad view"); }
        private void EnqueueLoadingMessage(LoadingMessage item)
        { loadingMessages.Add(item); loadingWireBytes += item.WireBytes; }
        private void CoalesceLoadingViews(MapView view, int index = -1) { }
        private void ReportSlowOperation(string stage, long started, long bytes = -1, long mapId = -1) { }
        private void RecordMapCacheTransfer(MapCacheTransfer transfer) { }
        private void SendMapView(MapRecord map, string player, bool includeAll, bool fullTerrain, bool ignoreCache)
        {
            string peer;
            if (!playerPeers.TryGetValue(player, out peer)) throw new IOException("synthetic peer missing");
            SendNetworkMapView(peer, player, new MapView {
                Access = new MapAccess { MapId = map.MapId, AuthorityEpoch = map.AuthorityEpoch, Revision = 1 },
                Snapshot = new MapSnapshot { MapId = map.MapId, Revision = 1 }
            }, fullTerrain, ignoreCache);
        }
        private void FailMapLoading(Exception error) { throw error; }
        private static void Require(BridgeResult result, string action) { }

        internal static MapView MakeView(long mapId, string viewId, long epoch = 1)
        {
            return new MapView {
                ViewId = viewId, Access = new MapAccess { MapId = mapId, Revision = 1, AuthorityEpoch = epoch },
                Snapshot = new MapSnapshot { MapId = mapId, Revision = 1 },
                Cache = new MapCacheTransfer()
            };
        }
        internal void ConfigureHost()
        {
            IsHost = true;
            World.Maps[7] = new MapRecord { MapId = 7, AuthorityEpoch = 1, Visitors = new List<string> { "good", "bad" } };
            playerPeers["good"] = "good-peer"; playerPeers["bad"] = "bad-peer";
        }
        internal TestTransport Network { get { return transport; } }
        internal int HostPendingViews { get { return offeredMapViews.Count; } }
        internal int GuestWaitingViews { get { return waitingMapCaches.Count; } }
        internal int GuestFifoEntries { get { return loadingMessages.Count; } }
        internal long HostReservedBytes { get { return retainedMapViewBytes; } }
        internal int DeferredPackets { get { return cacheOutboundRetry.Count; } }
        internal int RecoveryBarriers { get { return loadingResyncs.Count; } }
        internal int HostRecoveryKeys { get { return retriedCacheViews.Count; } }
        internal void HostSend(string player, long mapId = 7)
        {
            string peer = playerPeers[player];
            SendNetworkMapView(peer, player, MakeView(mapId, "ignored"));
        }
        internal void GuestQuery(MapCacheQuery query) { ReceiveMapCacheQuery(query); }
        internal void HostRetry(MapCacheRetry retry, string player)
        { ReceiveMapCacheRetry(player, retry); }
        internal void HostSeedRetry(string player, string view)
        { sentMapViews[player + ":" + view] = new MapCacheQuery { ViewId = view, MapId = 7 }; }
        internal void GuestExpect(MapCacheQuery query)
        {
            waitingMapCaches.Add(query.ViewId, query);
            waitingMapCacheSeconds.Add(query.ViewId, 0);
            EnqueueLoadingMessage(new LoadingMessage { CacheViewId = query.ViewId, WireBytes = 256 });
        }
        internal void GuestStage(MapView view)
        { StageNetworkMapView(new WireMessage { Kind = "view", SessionId = SessionId, Payload = view }); }
        internal bool GuestTerrain(MapView view, BridgeResult result) { return RetryFullTerrain(view, result); }
        internal void AdvanceCache(double seconds) { TickMapCache(seconds); }
        internal void DisposeCache() { ClearMapCacheSession(); }
        internal void BecomeStale(long mapId, long epoch)
        { World.Maps[mapId] = new MapRecord { MapId = mapId, AuthorityEpoch = epoch }; }
        internal static MapCacheQuery Query(long mapId, string viewId, bool ignoreCache = false)
        { return new MapCacheQuery { MapId = mapId, WorldId = "world", ViewId = viewId,
            AuthorityEpoch = 1, Revision = 1, IgnoreCache = ignoreCache }; }
    }
}
