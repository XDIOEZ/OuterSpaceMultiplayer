using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeWorldMultiplayer
{
    // An authoritative update has already been applied when this is called.
    // Never silently drop an observer's map/personal delta: an unsendable
    // observer must leave the room and rejoin from a full canonical state.
    // Sender ACKs are intentionally NOT passed through this helper.
    internal static class ReliableCommitFanout
    {
        internal static bool SendOrDisconnect(Action send, Action disconnect)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            if (disconnect == null) throw new ArgumentNullException(nameof(disconnect));
            try { send(); return true; }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            // Do not hide a failed disconnect: leaving a peer connected after
            // missing a canonical delta would produce an invalid replica.
            disconnect();
            return false;
        }
    }

    public sealed partial class MultiplayerSession
    {
        private static MapUpdate CommittedMapReceipt(MapUpdate update)
        {
            // Echo the exact committed transaction identity and personal
            // revision watermarks, never the large native delta to its author.
            var receipt = new MapUpdate {
                CommitId = update.CommitId, SessionId = update.SessionId,
                MapId = update.MapId, AuthorityEpoch = update.AuthorityEpoch,
                BaseRevision = update.BaseRevision, Revision = update.Revision,
                Players = new List<PlayerUpdate>()
            };
            if (update.Players != null)
                foreach (PlayerUpdate player in update.Players)
                    receipt.Players.Add(new PlayerUpdate {
                        PlayerId = player.PlayerId,
                        BaseRevision = player.BaseRevision, Revision = player.Revision
                    });
            return receipt;
        }

        private void BroadcastCommittedUpdate(string kind, object payload, string exceptPeer)
        {
            if (!IsHost || !transport.IsRunning || peers.Count == 0) return;
            // Preserve Broadcast's shared encoding and peer iteration order;
            // snapshot before Disconnect can mutate the live peer dictionary.
            byte[] encoded = null;
            foreach (string observer in peers.Keys.ToList())
            {
                if (observer == exceptPeer) continue;
                if (encoded == null) encoded = EncodeMessage(kind, payload, null);
                ReliableCommitFanout.SendOrDisconnect(
                    () => transport.Send(observer, encoded),
                    () => transport.Disconnect(observer,
                        "已提交的个人进度同步发送失败；请重新加入以获取房主完整状态。"));
            }
        }

        private void SendCommittedMapToObserver(string observer, MapUpdate update)
        {
            // Serialization errors are not peer congestion and must not be
            // classified as a disconnection; encode outside the catch boundary.
            byte[] encoded = EncodeMessage("map", WireMapUpdate(update), null);
            ReliableCommitFanout.SendOrDisconnect(
                () => transport.Send(observer, encoded),
                () => transport.Disconnect(observer,
                    "已提交的地图差量同步发送失败；请重新加入以获取房主完整检查点。"));
        }

        private void BroadcastCommittedPersonalToRecipient(string observer, PlayerUpdate update)
        {
            byte[] encoded = EncodeMessage("personal", update, null);
            ReliableCommitFanout.SendOrDisconnect(
                () => transport.Send(observer, encoded),
                () => transport.Disconnect(observer,
                    "已提交的个人进度同步发送失败；请重新加入以获取房主完整状态。"));
        }
    }
}
