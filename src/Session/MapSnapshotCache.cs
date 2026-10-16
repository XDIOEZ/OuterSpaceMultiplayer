using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private sealed class OfferedMapView
        {
            internal MapView View;
            internal MapCacheQuery Query;
            internal double Elapsed;
            internal long RetainedBytes;
            internal string Player;
        }
        private MapCacheStore mapCache;
        private string mapCacheWarning;
        private bool mapCacheWarningReported;
        private readonly Dictionary<string, OfferedMapView> offeredMapViews = new Dictionary<string, OfferedMapView>();
        private readonly Dictionary<string, MapCacheQuery> sentMapViews = new Dictionary<string, MapCacheQuery>();
        private readonly HashSet<string> retriedCacheViews = new HashSet<string>();
        private readonly HashSet<string> fullTerrainRetries = new HashSet<string>();
        private readonly HashSet<long> requiresFullTerrain = new HashSet<long>();
        private readonly Dictionary<string, MapCacheQuery> waitingMapCaches = new Dictionary<string, MapCacheQuery>();
        private readonly Dictionary<string, double> waitingMapCacheSeconds = new Dictionary<string, double>();
        private long retainedMapViewBytes;
        private bool HasMapCacheBarrier => waitingMapCaches.Count != 0;

        private void InitializeMapCache(string runtimeScope)
        {
            if (string.IsNullOrWhiteSpace(runtimeScope)) return;
            try { mapCache = new MapCacheStore(storageRoot, runtimeScope); }
            catch (Exception error) { DisableMapCache(error); }
        }
        private void DisableMapCache(Exception error)
        {
            mapCache = null;
            if (mapCacheWarning == null) mapCacheWarning = error.ToString();
        }
        private byte[] ReadMapCacheContent(string hash)
        {
            if (mapCache == null) return null;
            try { return mapCache.TryReadContent(hash); }
            catch (Exception error) { DisableMapCache(error); return null; }
        }
        private void WriteMapCacheContent(string hash, byte[] content)
        {
            if (mapCache == null) return;
            try { mapCache.WriteContent(hash, content); }
            catch (Exception error) { DisableMapCache(error); }
        }
        private void TickMapCache(double seconds)
        {
            if (mapCacheWarning != null && !mapCacheWarningReported)
            {
                mapCacheWarningReported = true;
                MessageFaulted?.Invoke("[map-cache] 本地派生缓存已停用，本场继续从房主获取权威数据。" + Environment.NewLine + mapCacheWarning);
            }
            foreach (var pair in offeredMapViews.ToList())
            {
                pair.Value.Elapsed += seconds;
                if (pair.Value.Elapsed < 60) continue;
                retainedMapViewBytes -= pair.Value.RetainedBytes; offeredMapViews.Remove(pair.Key);
                string peer;
                if (playerPeers.TryGetValue(pair.Value.Player, out peer)) Send(peer, "error", "等待地图缓存清单超时，请重新进入该地图。");
            }
            foreach (var pair in waitingMapCaches.ToList())
            {
                waitingMapCacheSeconds[pair.Key] += seconds;
                if (waitingMapCacheSeconds[pair.Key] >= 60)
                    FailMapLoading(new TimeoutException("等待房主地图内容超时：map=" + pair.Value.MapId));
            }
        }
        private void SendNetworkMapView(string peer, string playerId, MapView view, bool fullTerrain = false, bool ignoreCache = false)
        {
            RequireFullCheckpoint(view.Snapshot, view.Access.MapId);
            long bytes = 2L * view.Snapshot.NativeJson.Length + view.Players.Sum(x => 2L * (x.NativeFieldsJson == null ? 0 : x.NativeFieldsJson.Length));
            if (offeredMapViews.Values.Count(x => x.Player == playerId) >= 8 || bytes > 128L * 1024 * 1024 || retainedMapViewBytes + bytes > 256L * 1024 * 1024)
                throw new InvalidOperationException("待发送的完整地图超过本房间有界准备容量，请稍后重试。");
            var query = new MapCacheQuery { ViewId = Guid.NewGuid().ToString("N"), WorldId = World.WorldId, MapId = view.Access.MapId,
                AuthorityEpoch = view.Access.AuthorityEpoch, Revision = view.Access.Revision, FullTerrain = fullTerrain, IgnoreCache = ignoreCache };
            offeredMapViews[query.ViewId] = new OfferedMapView { Player = playerId, View = view, Query = query, RetainedBytes = bytes };
            retainedMapViewBytes += bytes;
            Send(peer, "map.cache-query", query);
        }
        private bool HandleMapCacheMessage(string peer, WireMessage message)
        {
            if (message.Kind != "map.cache-query" && message.Kind != "map.cache-offer" && message.Kind != "map.cache-retry") return false;
            if (!IsConnected || World == null || message.SessionId != SessionId) throw new MessageRejectionException("旧会话的地图缓存消息已失效。");
            if (!IsHost)
            {
                if (message.Kind != "map.cache-query") throw new InvalidDataException("Only the host may announce a map view.");
                ReceiveMapCacheQuery(JsonData.Payload<MapCacheQuery>(message.Payload)); return true;
            }
            string player;
            if (!peers.TryGetValue(peer, out player)) throw new MessageRejectionException("地图缓存请求尚未认证。");
            if (nativeFault != null) throw new MessageRejectionException("房主原生事务已暂停，不能继续准备地图。");
            if (message.Kind == "map.cache-offer") ReceiveMapCacheOffer(peer, player, JsonData.Payload<MapCacheOffer>(message.Payload));
            else if (message.Kind == "map.cache-retry") ReceiveMapCacheRetry(player, JsonData.Payload<MapCacheRetry>(message.Payload));
            else throw new InvalidDataException("A client cannot announce a host map view.");
            return true;
        }
        private void ReceiveMapCacheQuery(MapCacheQuery query)
        {
            if (query == null || query.WorldId != World.WorldId || query.MapId < 0 || !IsViewId(query.ViewId)) throw new InvalidDataException("Invalid map cache query.");
            if (waitingMapCaches.ContainsKey(query.ViewId)) return;
            if (waitingMapCaches.Count >= 8) throw new InvalidDataException("Too many pending map views.");
            waitingMapCaches.Add(query.ViewId, query); waitingMapCacheSeconds.Add(query.ViewId, 0);
            EnqueueLoadingMessage(new LoadingMessage { CacheViewId = query.ViewId, WireBytes = 256 });
            var offer = new MapCacheOffer { WorldId = World.WorldId, ViewId = query.ViewId, MapId = query.MapId };
            offer.FullTerrain = query.FullTerrain || requiresFullTerrain.Contains(query.MapId);
            if (!query.IgnoreCache && mapCache != null)
            {
                try
                {
                    MapCacheManifest manifest = mapCache.TryReadManifest(World.WorldId, query.MapId);
                    if (manifest != null) offer.AvailableChunks = manifest.Chunks.Select(x => x.Hash).Distinct().Where(mapCache.ContainsContent).ToList();
                }
                catch (Exception error) { DisableMapCache(error); }
            }
            Send("host", "map.cache-offer", offer);
        }
        private void ReceiveMapCacheOffer(string peer, string player, MapCacheOffer offer)
        {
            OfferedMapView pending;
            MapRecord map;
            if (offer == null || offer.WorldId != World.WorldId || !offeredMapViews.TryGetValue(offer.ViewId ?? "", out pending) || pending.Player != player
                || offer.ViewId != pending.Query.ViewId || offer.MapId != pending.Query.MapId
                || !World.Maps.TryGetValue(offer.MapId, out map) || !map.Visitors.Contains(player))
                throw new MessageRejectionException("地图缓存清单对应的访问请求已过期。");
            if (offer.AvailableChunks == null || offer.AvailableChunks.Count > MapSnapshotDeltaCodec.MaxChunks
                || offer.AvailableChunks.Any(x => !MapSnapshotDeltaCodec.IsHash(x))) throw new InvalidDataException("Invalid cache chunk offer.");
            pending.Query.FullTerrain |= offer.FullTerrain;
            MapSnapshot wire = pending.Query.FullTerrain ? pending.View.Snapshot : bridge.PrepareNetworkMap(pending.View.Snapshot);
            RequireFullCheckpoint(wire, pending.View.Access.MapId);
            if (wire.Revision != pending.View.Access.Revision) throw new InvalidOperationException("Prepared network map changed its canonical revision.");
            long encodeStarted = Stopwatch.GetTimestamp();
            MapCacheTransfer transfer;
            try
            {
                MapSnapshotDeltaCodec.Encoded encoded = MapSnapshotDeltaCodec.Encode(World.WorldId, wire);
                transfer = MapSnapshotDeltaCodec.MakeTransfer(encoded, pending.Query.IgnoreCache ? null : offer.AvailableChunks);
            }
            finally { ReportSlowOperation("map.cache.encode", encodeStarted, mapId: pending.Query.MapId); }
            RecordMapCacheTransfer(transfer);
            var outgoing = new MapView { Access = pending.View.Access, Players = pending.View.Players, ViewId = pending.Query.ViewId,
                FullTerrain = pending.Query.FullTerrain, Cache = transfer };
            Send(peer, "view", outgoing);
            offeredMapViews.Remove(pending.Query.ViewId); retainedMapViewBytes -= pending.RetainedBytes;
            sentMapViews[player + ":" + pending.Query.ViewId] = pending.Query;
            if (sentMapViews.Count > 512) sentMapViews.Remove(sentMapViews.Keys.First());
        }
        private void ReceiveMapCacheRetry(string player, MapCacheRetry retry)
        {
            MapCacheQuery sent;
            MapRecord map;
            if (retry == null || !sentMapViews.TryGetValue(player + ":" + retry.ViewId, out sent) || retry.ViewId != sent.ViewId || retry.MapId != sent.MapId
                || !World.Maps.TryGetValue(retry.MapId, out map) || !map.Visitors.Contains(player)) throw new MessageRejectionException("地图恢复请求已过期。");
            string key = player + ":" + retry.ViewId + ":" + retry.FullTerrain;
            if (!retriedCacheViews.Add(key)) throw new MessageRejectionException("同一地图视图已请求过该恢复方式。");
            if (retriedCacheViews.Count > 256) retriedCacheViews.Remove(retriedCacheViews.First());
            SendMapView(map, player, true, retry.FullTerrain, true);
        }
        private void StageNetworkMapView(WireMessage message)
        {
            // Cached views take a typed FIFO route instead of the normal message dispatcher.
            // Enforce that dispatcher's session boundary here before inspecting cache content.
            if (!IsConnected || World == null || message.SessionId != SessionId)
                throw new MessageRejectionException("旧房间会话的地图视图已失效。");
            MapView view = JsonData.Payload<MapView>(message.Payload);
            MapCacheQuery expected;
            if (view == null || view.Access == null || view.Cache == null || view.Snapshot != null
                || !waitingMapCaches.TryGetValue(view.ViewId ?? "", out expected)
                || view.Access.MapId != expected.MapId || view.Access.AuthorityEpoch != expected.AuthorityEpoch || view.Access.Revision != expected.Revision
                || view.FullTerrain != (expected.FullTerrain || requiresFullTerrain.Contains(expected.MapId)))
                throw new MessageRejectionException("旧地图内容已被新的访问请求替代。");
            try
            {
                long reassembleStarted = Stopwatch.GetTimestamp();
                try { view.Snapshot = MapSnapshotDeltaCodec.Reassemble(view.Cache, World.WorldId, view.Access.MapId, ReadMapCacheContent); }
                finally { ReportSlowOperation("map.cache.reassemble", reassembleStarted, receivingWireBytes, view.Access.MapId); }
                ValidateLoadingView(view);
                if (mapCache != null)
                {
                    try
                    {
                        foreach (MapCacheChunk chunk in view.Cache.ChangedChunks) mapCache.WriteContent(chunk.Hash, chunk.Data);
                        mapCache.WriteManifest(World.WorldId, view.Access.MapId, view.Cache.Manifest);
                    }
                    catch (Exception error) { DisableMapCache(error); }
                }
                int position = loadingMessages.FindIndex(x => x.CacheViewId == view.ViewId);
                if (position < 0) throw new InvalidDataException("The ordered map view placeholder is missing.");
                var queued = loadingMessages[position];
                long expandedBytes = Math.Max(receivingWireBytes, 2L * view.Snapshot.NativeJson.Length);
                if (loadingWireBytes - queued.WireBytes + expandedBytes > 512L * 1024 * 1024) throw new InvalidDataException("Expanded pending map views exceed the local application budget.");
                loadingWireBytes -= queued.WireBytes;
                view.Cache = null;
                // This exact FIFO placeholder is still the reliable-stream boundary. The
                // validated complete view is already a strongly typed object; a second JSON
                // tree would double memory/CPU for a large (often 20 MiB+) map transfer.
                // TickMapLoading replays it through the same guest ReceiveMapView validation.
                queued.NetworkView = view;
                queued.CacheViewId = null;
                queued.WireBytes = expandedBytes;
                loadingWireBytes += queued.WireBytes;
                waitingMapCaches.Remove(view.ViewId); waitingMapCacheSeconds.Remove(view.ViewId);
                CoalesceLoadingViews(view, position);
            }
            catch (Exception error)
            {
                string key = "local:" + view.ViewId + ":cache";
                if (!expected.IgnoreCache && retriedCacheViews.Add(key))
                {
                    if (retriedCacheViews.Count > 256) retriedCacheViews.Remove(retriedCacheViews.First());
                    if (mapCacheWarning == null) mapCacheWarning = error.ToString();
                    Send("host", "map.cache-retry", new MapCacheRetry { ViewId = view.ViewId, MapId = view.Access.MapId });
                    RemoveMapCachePlaceholder(view.ViewId);
                    AddSeedRecoveryBarrier(view);
                    return;
                }
                throw new InvalidDataException("无缓存完整地图仍未通过完整性校验，未写入原生地图。", error);
            }
        }
        private bool RetryFullTerrain(MapView view, BridgeResult result)
        {
            if (result == null || !result.SeedMismatch) return false;
            if (!result.NativeUnchanged || view.FullTerrain || !fullTerrainRetries.Add(view.ViewId))
                throw new InvalidOperationException("完整地形恢复后仍发生地图生成校验错误：" + view.Access.MapId);
            nativeMapLoad = null; awaitingLocalReady.Remove(view.Access.MapId);
            requiresFullTerrain.Add(view.Access.MapId);
            if (fullTerrainRetries.Count > 256) fullTerrainRetries.Remove(fullTerrainRetries.First());
            if (requiresFullTerrain.Count > 128) requiresFullTerrain.Remove(requiresFullTerrain.First());
            AddSeedRecoveryBarrier(view);
            Send("host", "map.cache-retry", new MapCacheRetry { ViewId = view.ViewId, MapId = view.Access.MapId, FullTerrain = true });
            return true;
        }
        private MapSnapshot ResolvedMapSnapshot(MapView view, BridgeResult result)
        {
            MapSnapshot resolved = string.IsNullOrEmpty(result.PayloadJson) ? view.Snapshot : JsonData.Read<MapSnapshot>(result.PayloadJson);
            RequireFullCheckpoint(resolved, view.Access.MapId);
            if (resolved.Revision != view.Access.Revision) throw new InvalidDataException("Resolved map revision differs from its host view.");
            return resolved;
        }
        private static bool IsViewId(string id) { return id != null && id.Length == 32 && id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
        private void ForgetMapCachePeer(string player)
        {
            foreach (var pair in offeredMapViews.Where(x => x.Value.Player == player).ToList())
            { retainedMapViewBytes -= pair.Value.RetainedBytes; offeredMapViews.Remove(pair.Key); }
            foreach (string id in sentMapViews.Keys.Where(x => x.StartsWith(player + ":", StringComparison.Ordinal)).ToList()) sentMapViews.Remove(id);
        }
        private void RemoveMapCachePlaceholder(string viewId)
        {
            int index = loadingMessages.FindIndex(x => x.CacheViewId == viewId);
            if (index >= 0) { loadingWireBytes -= loadingMessages[index].WireBytes; loadingMessages.RemoveAt(index); }
            waitingMapCaches.Remove(viewId); waitingMapCacheSeconds.Remove(viewId);
        }
        private void AddSeedRecoveryBarrier(MapView view)
        {
            LoadingResync required;
            if (!loadingResyncs.TryGetValue(view.Access.MapId, out required))
                loadingResyncs.Add(view.Access.MapId, required = new LoadingResync { Epoch = view.Access.AuthorityEpoch });
            required.Revision = Math.Max(required.Revision, view.Access.Revision);
            foreach (PlayerFieldState fields in view.Players)
            { long old; required.Players.TryGetValue(fields.PlayerId, out old); required.Players[fields.PlayerId] = Math.Max(old, fields.PersonalRevision); }
        }
        private void ClearMapCacheSession()
        { offeredMapViews.Clear(); sentMapViews.Clear(); waitingMapCaches.Clear(); waitingMapCacheSeconds.Clear(); retainedMapViewBytes = 0; retriedCacheViews.Clear(); fullTerrainRetries.Clear(); requiresFullTerrain.Clear(); }
    }
}
