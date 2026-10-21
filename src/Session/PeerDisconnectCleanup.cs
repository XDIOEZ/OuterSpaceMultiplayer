using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    // Removing a peer is an authority change, not a sequence of best-effort
    // UI callbacks. An unrelated chat/battle/native failure must never skip
    // revoking old map leases or removing deferred commands for the leaver.
    internal static class PeerDisconnectCleanup
    {
        internal static Exception Attempt(Action action, Exception firstFailure)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            try { action(); }
            catch (Exception error) { if (firstFailure == null) return error; }
            return firstFailure;
        }

        internal static void RevokeUnsafeLease(MapRecord map, Action<MapRecord> freezeNative)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (freezeNative == null) throw new ArgumentNullException(nameof(freezeNative));
            // Remove the old lease in host authority BEFORE trying native
            // freeze. If the bridge itself is broken the host must lock.
            map.SimulatorPlayerId = null;
            map.AwaitingAuthorityReady = false;
            map.Mode = MapMode.Frozen;
            map.AuthorityEpoch = checked(map.AuthorityEpoch + 1);
            freezeNative(map);
        }
    }

    public sealed partial class MultiplayerSession
    {
        private void RemovePeerAuthorityFirst(string peer)
        {
            challenges.Remove(peer);
            DeferredCommandReplyMap.RemoveForPeer(commandReplyPeers, peer);

            string playerId;
            if (!peers.TryGetValue(peer, out playerId)) return;
            peers.Remove(peer);
            string mappedPeer;
            if (playerPeers.TryGetValue(playerId, out mappedPeer) && mappedPeer == peer)
                playerPeers.Remove(playerId);
            galaxyResetRequests.ForgetPlayer(playerId);

            PlayerRecord player;
            Exception criticalFailure = null, advisoryFailure = null;
            if (!World.Players.TryGetValue(playerId, out player) || player == null)
                criticalFailure = new InvalidOperationException("断开连接时找不到房主的玩家身份记录。");
            else player.Online = false;

            advisoryFailure = PeerDisconnectCleanup.Attempt(
                () => ForgetMapCachePeer(playerId), advisoryFailure);
            advisoryFailure = PeerDisconnectCleanup.Attempt(
                () => DisconnectCommunication(playerId, false), advisoryFailure);
            criticalFailure = PeerDisconnectCleanup.Attempt(
                () => DisconnectPlayerFleetBattles(playerId, false), criticalFailure);
            criticalFailure = PeerDisconnectCleanup.Attempt(
                () => Require(bridge.SetPlayerOnline(playerId, false), "冻结离线舰队"),
                criticalFailure);

            // Always process every map. A failing first handoff cannot strand
            // a second map under an offline simulator's old authority epoch.
            foreach (MapRecord map in World.Maps.Values.ToList())
            {
                if (map.Visitors != null) map.Visitors.Remove(playerId);
                if (player != null) LeaveGroundOperation(player, map, false);
                if (map.SimulatorPlayerId != playerId) continue;
                try { ReassignMap(map); }
                catch (Exception handoffFailure)
                {
                    if (criticalFailure == null) criticalFailure = handoffFailure;
                    // ReassignMap can fail BEFORE or AFTER changing the map
                    // revision/lease. In both cases revoke its pending lease.
                    criticalFailure = PeerDisconnectCleanup.Attempt(
                        () => PeerDisconnectCleanup.RevokeUnsafeLease(map,
                            revoked => Require(bridge.SetMapAuthority(new MapAccess {
                                MapId = revoked.MapId,
                                AuthorityEpoch = revoked.AuthorityEpoch,
                                Revision = revoked.Revision,
                                OperatorPlayerId = revoked.OperatorPlayerId,
                                Access = MapAccessMode.Frozen
                            }), "断线清理失败后强制冻结地图模拟权")),
                        criticalFailure);
                }
            }

            // Deferred executions could otherwise be delivered against an
            // offline simulator on a later Tick after a partial disconnect.
            foreach (string id in pendingExecutions
                .Where(entry => entry.Value != null && entry.Value.PlayerId == playerId)
                .Select(entry => entry.Key).ToList())
                pendingExecutions.Remove(id);

            // Policy is authoritative, not advisory: a peer that misses a
            // simulator reassignment cannot safely continue native play.
            criticalFailure = PeerDisconnectCleanup.Attempt(
                () => BroadcastPolicy(), criticalFailure);
            if (criticalFailure != null)
            {
                string reason = "断线权威收口未全部成功，已停用旧地图任期并暂停原生提交：" +
                    criticalFailure.Message;
                // LockNativeFault sets the fail-closed flag before notifying
                // peers, so even a failed status/broadcast cannot reopen it.
                try { LockNativeFault(reason); }
                catch (Exception)
                {
                    nativeFault = reason;
                    Paused = true;
                }
                return;
            }
            if (advisoryFailure != null)
            {
                // Chat/cache/status notification failure must not undo the
                // committed peer eviction and map authority handoffs.
                try { Error("玩家断线清理提示失败，但地图权威已收口：" + advisoryFailure.Message); }
                catch (Exception) { }
                return;
            }
            if (player != null)
            {
                try { SetStatus(player.Nickname + " 已离线，舰队冻结"); }
                catch (Exception error)
                {
                    try { Error("玩家断线已收口，但状态回调失败：" + error.Message); }
                    catch (Exception) { }
                }
            }
        }
    }
}
