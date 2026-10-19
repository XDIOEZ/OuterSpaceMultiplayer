using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private void TickMapCache(double seconds)
        {
            // Only transport enqueue failures are retried; each view keeps its
            // original session/epoch/revision and the existing 60-second load timeout.
            TickCacheNegotiationOutbound(seconds);
            if (mapCacheWarning != null && !mapCacheWarningReported)
            {
                mapCacheWarningReported = true;
                // Optional diagnostic callbacks must not stop the game clock.
                try { MessageFaulted?.Invoke("[map-cache] 本地派生缓存已停用，本场继续从房主获取权威数据。" + Environment.NewLine + mapCacheWarning); }
                catch (Exception) { }
            }
            if (offeredMapViews.Count != 0)
            {
                // Keep the original dictionary enumeration order and an
                // independent snapshot if callbacks enter Tick recursively.
                List<OfferedMapView> offers = offeredMapTickSnapshot.Capture(offeredMapViews);
                try
                {
                    foreach (OfferedMapView pending in offers)
                    {
                        string viewId = pending.Query.ViewId;
                        OfferedMapView current;
                        if (!offeredMapViews.TryGetValue(viewId, out current) || !ReferenceEquals(current, pending))
                            continue;
                        pending.Elapsed += seconds;
                        if (pending.Elapsed < 60)
                        {
                            // Cache discovery is optional. Before the client's
                            // deadline, fill this same FIFO slot with the
                            // retained complete checkpoint.
                            if (pending.Elapsed >= 30) TryFallbackMapCacheOffer(viewId, pending);
                            continue;
                        }
                        retainedMapViewBytes -= pending.RetainedBytes;
                        offeredMapViews.Remove(viewId);
                        NotifyMapCacheTimeout(pending.Player);
                    }
                }
                finally { offeredMapTickSnapshot.Release(offers); }
            }
            if (waitingMapCaches.Count == 0) return;
            List<MapCacheQuery> waiting = waitingMapTickSnapshot.Capture(waitingMapCaches);
            try
            {
                foreach (MapCacheQuery query in waiting)
                {
                    string viewId = query.ViewId;
                    MapCacheQuery current;
                    double elapsed;
                    // FailMapLoading can synchronously clear every placeholder.
                    // A stale snapshot must neither revive its stopwatch nor
                    // accidentally time out a new lease with the same ViewId.
                    if (!waitingMapCaches.TryGetValue(viewId, out current) ||
                        !ReferenceEquals(current, query) ||
                        !waitingMapCacheSeconds.TryGetValue(viewId, out elapsed)) continue;
                    elapsed += seconds;
                    waitingMapCacheSeconds[viewId] = elapsed;
                    if (elapsed >= 60)
                        FailMapLoading(new TimeoutException("等待房主地图内容超时：map=" + query.MapId));
                }
            }
            finally { waitingMapTickSnapshot.Release(waiting); }
        }

        private void NotifyMapCacheTimeout(string player)
        {
            // Keep the callback capture outside the pending-view hot loop.
            // Most offers wait for many frames but never reach the deadline.
            string peer;
            if (!playerPeers.TryGetValue(player, out peer)) return;
            OptionalPeerNoticeOutcome outcome = OptionalPeerNotice.SendOrDisconnect(
                () => Send(peer, "error", "等待地图缓存清单超时，请重新进入该地图。"),
                () => transport.Disconnect(peer, "地图缓存协商超时，无法发送恢复提示。"));
            if (outcome != OptionalPeerNoticeOutcome.Sent)
            {
                string reason = outcome == OptionalPeerNoticeOutcome.Disconnected
                    ? "[map-cache] 地图缓存超时通知发送失败，已断开拥塞连接。"
                    : "[map-cache] 地图缓存超时通知与断开连接均失败；其它玩家仍可继续。";
                try { MessageFaulted?.Invoke(reason); }
                catch (Exception) { } // Optional warnings do not stop the room.
            }
        }
    }
}
