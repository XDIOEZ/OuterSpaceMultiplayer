using System;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        // The first identity/UID grant was durably saved by AuthenticatePeer
        // BEFORE this point. A failed welcome/native admission is not a new
        // player and must never clear those assets or grant them again.
        private void CompleteAuthenticatedJoin(string peer, PlayerRecord player)
        {
            try
            {
                player.Online = true;
                Require(bridge.SetPlayerOnline(player.PlayerId, true), "恢复玩家舰队");
                PlayerRecord introduction = JsonData.Clone(player);
                introduction.CredentialId = null;
                introduction.CredentialHash = null;
                introduction.Uids = null;
                introduction.IssuedUidRanges.Clear();
                // The candidate is not authenticated yet and must not receive
                // a playerJoined frame ahead of its own welcome.
                Broadcast("playerJoined", new PlayerIntroduction { Player = introduction });
                peers[peer] = player.PlayerId;
                playerPeers[player.PlayerId] = peer;
                Send(peer, "welcome", new Welcome {
                    PlayerId = player.PlayerId, World = PublicWorld(player.PlayerId),
                    Uids = JsonData.Clone(player.Uids)
                });
                ITransportPeerAdmission admission = transport as ITransportPeerAdmission;
                if (admission != null) admission.MarkAuthenticated(peer);
                unauthenticatedPeerDeadline.Complete(peer);
                MessageStage("auth.first-map-view", player.CurrentMapId, player.PlayerId);
                if (player.CurrentMapId >= 0) EnterMap(player, player.CurrentMapId);
                BroadcastPolicy();
                SetStatus(player.Nickname + " 已加入");
            }
            catch
            {
                // Keep the first successful save as the source of truth. The
                // partially admitted native/room presence is NOT committed.
                RollbackFailedAuthenticatedJoin(peer, player);
                throw;
            }
        }

        private void RollbackFailedAuthenticatedJoin(string peer, PlayerRecord player)
        {
            // Make room authority fail closed even when subsequent native
            // cleanup throws. Never erase saved initial assets, UID ranges,
            // identity binding or player's prior map location.
            player.Online = false;
            string actor;
            bool registered = peers.TryGetValue(peer, out actor) && actor == player.PlayerId;
            bool nativeFrozen = false;
            try
            {
                if (registered)
                {
                    // Reuse the normal room-exit cleanup: chat, battle, map
                    // visitors, simulator handoff and policy notifications.
                    RemovePeer(peer);
                    nativeFrozen = true;
                }
                else
                {
                    Require(bridge.SetPlayerOnline(player.PlayerId, false),
                        "登录失败后冻结未接入房间的玩家舰队");
                    nativeFrozen = true;
                    // A playerJoined notice may already have reached some
                    // existing peers before a later broadcast/send failed.
                    BroadcastPolicy();
                }
            }
            catch (Exception cleanupError)
            {
                if (!nativeFrozen)
                {
                    try
                    {
                        Require(bridge.SetPlayerOnline(player.PlayerId, false),
                            "登录回滚时补充冻结玩家舰队");
                    }
                    catch (Exception nativeError)
                    {
                        // If the native fleet cannot be frozen, stop the room
                        // rather than allowing an unowned fleet to simulate.
                        try { LockNativeFault("登录回滚时无法冻结玩家舰队：" + nativeError.Message); }
                        catch (Exception) { }
                    }
                }
                ReportJoinCleanupFailure("清理半成功登录时的房间状态失败：" + cleanupError.Message);
            }
            finally
            {
                player.Online = false;
                // Only unlink THIS attempt; never delete another connection's
                // binding if a callback changed the shared peer dictionary.
                if (peers.TryGetValue(peer, out actor) && actor == player.PlayerId)
                    peers.Remove(peer);
                string mappedPeer;
                if (playerPeers.TryGetValue(player.PlayerId, out mappedPeer) && mappedPeer == peer)
                    playerPeers.Remove(player.PlayerId);
            }
            try
            {
                transport.Disconnect(peer, "身份认证未完成，已撤销本次上线状态。");
                unauthenticatedPeerDeadline.Forget(peer);
            }
            catch (Exception disconnectError)
            {
                // A failed Disconnect must not mask the original login error.
                // Keep an unauthenticated timeout to retry evicting the socket
                // if it did not close now.
                unauthenticatedPeerDeadline.Begin(peer);
                ReportJoinCleanupFailure("断开半成功登录连接失败：" + disconnectError.Message);
            }
        }

        private void ReportJoinCleanupFailure(string message)
        {
            try { Error(message); }
            catch (Exception) { } // Preserve the original authentication error.
        }
    }
}
