using System;
using System.Linq;

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
            foreach (var pair in offeredMapViews.ToList())
            {
                OfferedMapView current;
                if (!offeredMapViews.TryGetValue(pair.Key, out current) || !ReferenceEquals(current, pair.Value)) continue;
                pair.Value.Elapsed += seconds;
                if (pair.Value.Elapsed < 60)
                {
                    // Cache discovery is optional. Before the client's 60-second
                    // deadline, fill the SAME ViewId/FIFO slot with the retained
                    // complete checkpoint, without assuming any cached chunks.
                    if (pair.Value.Elapsed >= 30) TryFallbackMapCacheOffer(pair.Key, pair.Value);
                    continue;
                }
                retainedMapViewBytes -= pair.Value.RetainedBytes;
                offeredMapViews.Remove(pair.Key);
                string peer;
                if (!playerPeers.TryGetValue(pair.Value.Player, out peer)) continue;
                OptionalPeerNoticeOutcome outcome = OptionalPeerNotice.SendOrDisconnect(
                    () => Send(peer, "error", "等待地图缓存清单超时，请重新进入该地图。"),
                    () => transport.Disconnect(peer, "地图缓存协商超时，无法发送恢复提示。"));
                if (outcome != OptionalPeerNoticeOutcome.Sent)
                {
                    string reason = outcome == OptionalPeerNoticeOutcome.Disconnected
                        ? "[map-cache] 地图缓存超时通知发送失败，已断开拥塞连接。"
                        : "[map-cache] 地图缓存超时通知与断开连接均失败；其它玩家仍可继续。";
                    try { MessageFaulted?.Invoke(reason); }
                    catch (Exception) { } // A best-effort warning is not an authority failure.
                }
            }
            foreach (var pair in waitingMapCaches.ToList())
            {
                waitingMapCacheSeconds[pair.Key] += seconds;
                if (waitingMapCacheSeconds[pair.Key] >= 60)
                    FailMapLoading(new TimeoutException("等待房主地图内容超时：map=" + pair.Value.MapId));
            }
        }
    }
}
