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
            internal bool FullSnapshotFallbackAttempted;
        }
        private sealed class RecoveryQueryAttempt { internal bool TransportEnqueueFailed; }
        private RecoveryQueryAttempt activeRecoveryQueryAttempt;
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
        private readonly MapCacheOutboundRetry cacheOutboundRetry = new MapCacheOutboundRetry();
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
        private void SendNetworkMapView(string peer, string playerId, MapView view, bool fullTerrain = false, bool ignoreCache = false)
        {
            RequireFullCheckpoint(view.Snapshot, view.Access.MapId);
            long bytes = 2L * view.Snapshot.NativeJson.Length + view.Players.Sum(x => 2L * (x.NativeFieldsJson == null ? 0 : x.NativeFieldsJson.Length));
            if (offeredMapViews.Values.Count(x => x.Player == playerId) >= 8 || bytes > 128L * 1024 * 1024 || retainedMapViewBytes + bytes > 256L * 1024 * 1024)
                throw new InvalidOperationException("待发送的完整地图超过本房间有界准备容量，请稍后重试。");
            var query = new MapCacheQuery { ViewId = Guid.NewGuid().ToString("N"), WorldId = World.WorldId, MapId = view.Access.MapId,
                AuthorityEpoch = view.Access.AuthorityEpoch, Revision = view.Access.Revision, FullTerrain = fullTerrain, IgnoreCache = ignoreCache };
            byte[] encoded = EncodeMessage("map.cache-query", query, null);
            var reserved = new OfferedMapView { Player = playerId, View = view, Query = query, RetainedBytes = bytes };
            offeredMapViews[query.ViewId] = reserved;
            retainedMapViewBytes += bytes;
            try { transport.Send(peer, encoded); }
            catch (Exception error)
            {
                // An enqueue failure is not a pending offer. Only roll back
                // our exact reservation; a synchronous callback may replace it.
                if (MapCacheOutboundRetry.RemoveIfSame(offeredMapViews, query.ViewId, reserved))
                    retainedMapViewBytes -= reserved.RetainedBytes;
                // Only a verified transport enqueue rejection can reopen the
                // same one-use recovery intent. Native/contract faults cannot.
                if (activeRecoveryQueryAttempt != null &&
                    (error is IOException || error is InvalidOperationException))
                    activeRecoveryQueryAttempt.TransportEnqueueFailed = true;
                throw;
            }
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
            // Protocol/encoding failures must surface before inserting a FIFO
            // placeholder. Only transport enqueue failures are retried.
            byte[] encoded = EncodeMessage("map.cache-offer", offer, null);
            waitingMapCaches.Add(query.ViewId, query); waitingMapCacheSeconds.Add(query.ViewId, 0);
            try { EnqueueLoadingMessage(new LoadingMessage { CacheViewId = query.ViewId, WireBytes = 256 }); }
            catch { waitingMapCaches.Remove(query.ViewId); waitingMapCacheSeconds.Remove(query.ViewId); throw; }
            cacheOutboundRetry.TrySend(MapCacheOutboundKind.Offer, query.ViewId,
                query.MapId, query.AuthorityEpoch, query.Revision, encoded, bytes => transport.Send("host", bytes));
        }
        private void ReceiveMapCacheOffer(string peer, string player, MapCacheOffer offer, bool fullSnapshotFallback = false)
        {
            if (offer == null || offer.WorldId != World.WorldId || !IsViewId(offer.ViewId))
                throw new MessageRejectionException("地图缓存清单对应的访问请求已过期（世界或请求标识不匹配）。");
            if (offer.AvailableChunks == null || offer.AvailableChunks.Count > MapSnapshotDeltaCodec.MaxChunks
                || offer.AvailableChunks.Any(x => !MapSnapshotDeltaCodec.IsHash(x))) throw new InvalidDataException("Invalid cache chunk offer.");
            OfferedMapView pending;
            if (!offeredMapViews.TryGetValue(offer.ViewId, out pending))
            {
                MapCacheQuery sent;
                // A late offer can follow our full-snapshot fallback. Its exact
                // already-sent view fills the client's original FIFO slot once.
                if (sentMapViews.TryGetValue(player + ":" + offer.ViewId, out sent)
                    && sent.WorldId == offer.WorldId && sent.ViewId == offer.ViewId && sent.MapId == offer.MapId) return;
                throw new MessageRejectionException("地图缓存清单对应的访问请求已过期（请求不存在或已回收）。");
            }
            if (pending.Player != player)
                throw new MessageRejectionException("地图缓存清单对应的访问请求已过期（请求归属不匹配）。");
            if (offer.ViewId != pending.Query.ViewId || offer.MapId != pending.Query.MapId || !World.Maps.ContainsKey(offer.MapId))
                throw new MessageRejectionException("地图缓存清单对应的访问请求已过期（地块不匹配或不存在）。");
            // This exact checkpoint was authorized when its query was issued.
            // A later LeaveMap must not strand that earlier reliable-stream slot;
            // following policies/transactions still replay after this full view.
            pending.Query.FullTerrain |= offer.FullTerrain || fullSnapshotFallback;
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
            byte[] frame = EncodeMessage("view", outgoing, null);
            // The fallback is attempted once. Only a transport enqueue rejection
            // may wait for a late offer/the original bounded timeout; preparation
            // and encoding faults retain their normal failure semantics.
            try { transport.Send(peer, frame); }
            catch (IOException error) when (fullSnapshotFallback) { ReportMapCacheFallbackFailure(error); return; }
            catch (InvalidOperationException error) when (fullSnapshotFallback) { ReportMapCacheFallbackFailure(error); return; }
            if (!MapCacheOutboundRetry.RemoveIfSame(offeredMapViews, pending.Query.ViewId, pending)) return;
            retainedMapViewBytes -= pending.RetainedBytes;
            sentMapViews[player + ":" + pending.Query.ViewId] = pending.Query;
            if (sentMapViews.Count > 512) sentMapViews.Remove(sentMapViews.Keys.First());
        }
        private void TryFallbackMapCacheOffer(string viewId, OfferedMapView pending)
        {
            string peer, boundPlayer;
            if (pending.FullSnapshotFallbackAttempted || !IsHost || !IsConnected || nativeFault != null
                || !playerPeers.TryGetValue(pending.Player, out peer)
                || !peers.TryGetValue(peer, out boundPlayer) || boundPlayer != pending.Player) return;
            pending.FullSnapshotFallbackAttempted = true;
            try
            {
                ReceiveMapCacheOffer(peer, boundPlayer, new MapCacheOffer
                {
                    WorldId = pending.Query.WorldId, ViewId = viewId,
                    MapId = pending.Query.MapId, FullTerrain = true
                }, true);
            }
            catch (Exception error)
            {
                // This optional fallback uses only the retained full snapshot:
                // it never invokes native preparation, Apply, Save, or an ACK.
                // A codec/encoding fault must not abort every player's Tick.
                // Preserve the original request and its final timeout instead.
                ReportMapCacheFallbackFailure(error);
            }
        }
        private void ReportMapCacheFallbackFailure(Exception error)
        {
            try { MessageFaulted?.Invoke("[map-cache] 完整地图回退未完成，保留原请求等待清单或超时；异常类型=" + error.GetType().Name); }
            catch (Exception) { } // Optional diagnostics cannot stop the room.
        }
        private void ReceiveMapCacheRetry(string player, MapCacheRetry retry)
        {
            MapCacheQuery sent;
            MapRecord map;
            if (retry == null || !sentMapViews.TryGetValue(player + ":" + retry.ViewId, out sent) || retry.ViewId != sent.ViewId || retry.MapId != sent.MapId
                || !World.Maps.TryGetValue(retry.MapId, out map) || !map.Visitors.Contains(player)) throw new MessageRejectionException("地图恢复请求已过期。");
            string key = player + ":" + retry.ViewId + ":" + retry.FullTerrain;
            if (!retriedCacheViews.Add(key)) throw new MessageRejectionException("同一地图视图已请求过该恢复方式。");
            RecoveryQueryAttempt previous = activeRecoveryQueryAttempt;
            var attempt = new RecoveryQueryAttempt();
            activeRecoveryQueryAttempt = attempt;
            try { SendMapView(map, player, true, retry.FullTerrain, true); }
            catch
            {
                // Other failures may have changed native state; never make
                // those operations replayable just because they threw.
                if (attempt.TransportEnqueueFailed) retriedCacheViews.Remove(key);
                throw;
            }
            finally { activeRecoveryQueryAttempt = previous; }
            if (retriedCacheViews.Count > 256) retriedCacheViews.Remove(retriedCacheViews.First());
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
                // The host may upgrade an unanswered query to its full native
                // checkpoint. Never accept a downgrade of required full terrain.
                || !view.FullTerrain && (expected.FullTerrain || requiresFullTerrain.Contains(expected.MapId)))
                throw new MessageRejectionException("旧地图内容已被新的访问请求替代。");
            try
            {
                long reassembleStarted = Stopwatch.GetTimestamp();
                // ReadMapCacheContent delegates to MapCacheStore.TryReadContent,
                // which checks each disk chunk's SHA-256 before returning.
                try { view.Snapshot = MapSnapshotDeltaCodec.ReassembleFromVerifiedCache(view.Cache, World.WorldId, view.Access.MapId, ReadMapCacheContent); }
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
                cacheOutboundRetry.Remove(MapCacheOutboundKind.Offer, view.ViewId);
                CoalesceLoadingViews(view, position);
            }
            catch (Exception error)
            {
                string key = "local:" + view.ViewId + ":cache";
                if (!expected.IgnoreCache && retriedCacheViews.Add(key))
                {
                    if (retriedCacheViews.Count > 256) retriedCacheViews.Remove(retriedCacheViews.First());
                    if (mapCacheWarning == null) mapCacheWarning = error.ToString();
                    // A failed queue insertion must not leave the original FIFO
                    // placeholder ahead of a retry that has not reached the host.
                    // The recovery barrier keeps later map/personal changes blocked.
                    AddSeedRecoveryBarrier(view);
                    RemoveMapCachePlaceholder(view.ViewId);
                    SendCacheRetry(view, false);
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
            SendCacheRetry(view, true);
            return true;
        }
        private void SendCacheRetry(MapView view, bool fullTerrain)
        {
            // Encode errors are programming/contract failures and are not
            // disguised as transient queue congestion.
            byte[] encoded = EncodeMessage("map.cache-retry",
                new MapCacheRetry { ViewId = view.ViewId, MapId = view.Access.MapId, FullTerrain = fullTerrain }, null);
            cacheOutboundRetry.TrySend(fullTerrain ? MapCacheOutboundKind.FullTerrainRetry : MapCacheOutboundKind.CacheRetry,
                view.ViewId, view.Access.MapId, view.Access.AuthorityEpoch, view.Access.Revision,
                encoded, bytes => transport.Send("host", bytes));
        }
        private void TickCacheNegotiationOutbound(double seconds)
        {
            if (cacheOutboundRetry.Count == 0) return;
            cacheOutboundRetry.Advance(seconds,
                (kind, viewId, mapId, epoch, revision) =>
                {
                    if (!IsConnected || IsHost || World == null) return false;
                    if (kind == MapCacheOutboundKind.Offer)
                    {
                        MapCacheQuery expected;
                        MapRecord currentOfferMap;
                        return waitingMapCaches.TryGetValue(viewId, out expected)
                            && expected.WorldId == World.WorldId && expected.MapId == mapId
                            && expected.AuthorityEpoch == epoch && expected.Revision == revision
                            && (!World.Maps.TryGetValue(mapId, out currentOfferMap) || currentOfferMap.AuthorityEpoch <= epoch);
                    }
                    LoadingResync required;
                    MapRecord current;
                    return loadingResyncs.TryGetValue(mapId, out required) && required.Epoch <= epoch
                        && required.Revision >= revision
                        && (!World.Maps.TryGetValue(mapId, out current) || current.AuthorityEpoch <= epoch);
                }, bytes => transport.Send("host", bytes));
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
            cacheOutboundRetry.Remove(MapCacheOutboundKind.Offer, viewId);
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
        { offeredMapViews.Clear(); sentMapViews.Clear(); waitingMapCaches.Clear(); waitingMapCacheSeconds.Clear(); retainedMapViewBytes = 0; retriedCacheViews.Clear(); fullTerrainRetries.Clear(); requiresFullTerrain.Clear(); cacheOutboundRetry.Clear(); }
    }
}
