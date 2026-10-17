using System;
using System.IO;
using System.Linq;

namespace FreeWorldMultiplayer
{
    internal enum MapCacheNoticeOutcome { Sent, Disconnected, DisconnectFailed }

    internal static class MapCacheTimeoutNotice
    {
        // A cache-offer timeout is advisory, never a map commit or native mutation.
        // A full reliable send queue means the peer can no longer receive the
        // authoritative view: disconnect it rather than blocking all host ticks.
        internal static MapCacheNoticeOutcome SendOrDisconnect(Action send, Action disconnect)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            if (disconnect == null) throw new ArgumentNullException(nameof(disconnect));
            try { send(); return MapCacheNoticeOutcome.Sent; }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            // Even failure to close a broken transport is an isolated peer fault,
            // not a reason to halt another player's galaxy or committed maps.
            try { disconnect(); return MapCacheNoticeOutcome.Disconnected; }
            catch (Exception) { return MapCacheNoticeOutcome.DisconnectFailed; }
        }
    }

    public sealed partial class MultiplayerSession
    {
        private void TickMapCache(double seconds)
        {
            if (mapCacheWarning != null && !mapCacheWarningReported)
            {
                mapCacheWarningReported = true;
                // Optional diagnostic callbacks must not stop the game clock.
                try { MessageFaulted?.Invoke("[map-cache] 本地派生缓存已停用，本场继续从房主获取权威数据。" + Environment.NewLine + mapCacheWarning); }
                catch (Exception) { }
            }
            foreach (var pair in offeredMapViews.ToList())
            {
                pair.Value.Elapsed += seconds;
                if (pair.Value.Elapsed < 60) continue;
                retainedMapViewBytes -= pair.Value.RetainedBytes;
                offeredMapViews.Remove(pair.Key);
                string peer;
                if (!playerPeers.TryGetValue(pair.Value.Player, out peer)) continue;
                MapCacheNoticeOutcome outcome = MapCacheTimeoutNotice.SendOrDisconnect(
                    () => Send(peer, "error", "等待地图缓存清单超时，请重新进入该地图。"),
                    () => transport.Disconnect(peer, "地图缓存协商超时，无法发送恢复提示。"));
                if (outcome != MapCacheNoticeOutcome.Sent)
                {
                    string reason = outcome == MapCacheNoticeOutcome.Disconnected
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
