using System;
using System.IO;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private readonly HostUidRangeRenewalGate uidHostGrants = new HostUidRangeRenewalGate();
        private readonly ClientUidRangeRenewalGate uidClientRequests = new ClientUidRangeRenewalGate();

        // Only an authenticated player and the exact range token that player
        // was granted can request the next range. The token travels in the
        // already defined WireMessage.RequestId; no new wire kind or save field.
        private void ReceiveUidRangeRequest(string peer, string playerId, string requestKey)
        {
            PlayerRecord player = World.Players[playerId];
            if (player.Uids == null || player.Uids.SessionId != SessionId)
                throw new MessageRejectionException("旧房间 UID 号段不可续领。");
            bool legacy = String.IsNullOrEmpty(requestKey);
            string originalKey = legacy ? UidRangeRenewalToken.For(player.Uids) : requestKey;
            UidRangeRenewalAction action = legacy
                ? uidHostGrants.DecideLegacy(playerId, player.Uids)
                : uidHostGrants.Decide(playerId, player.Uids, requestKey);
            if (action == UidRangeRenewalAction.Invalid)
                throw new MessageRejectionException("UID 续领与本玩家当前会话号段不匹配，请更新联机 MOD。");
            if (action == UidRangeRenewalAction.Cooldown) return;
            if (action == UidRangeRenewalAction.Issue)
            {
                UidRange range = ReserveUidRange(false);
                player.Uids = range;
                player.IssuedUidRanges.Add(range);
                // Reserve the retry entry BEFORE the durable write. Even when
                // Save throws after partially writing, the next identical
                // request reuses this same high water and pending reservation.
                uidHostGrants.RecordIssued(playerId, originalKey, range);
            }
            // Bound both repeated disk Save attempts and repeat responses:
            // a malicious client must not fill transport every room Tick.
            uidHostGrants.ReplyAttempt(playerId, player.Uids);
            if (action != UidRangeRenewalAction.Resend)
            {
                SaveOrThrow();
                uidHostGrants.Saved(playerId, player.Uids);
            }
            // A transport failure after save is safe: the guest will retry
            // with the SAME old RequestId and receive this exact saved range.
            Send(peer, "uids", player.Uids, requestKey);
        }

        private void RequestUidRenewal(PlayerRecord player)
        {
            if (player == null || IsHost || !IsConnected || World == null) return;
            uidClientRequests.TryRequest(player.Uids, key => Send("host", "uids", null, key));
        }

        private void ReceiveUidRenewal(WireMessage message)
        {
            PlayerRecord player = World.Players[LocalPlayerId];
            UidRange range = JsonData.Payload<UidRange>(message.Payload);
            if (uidClientRequests.Accept(player.Uids, range, message.RequestId))
                player.Uids = range;
        }

        private void TickUidRenewal(double seconds)
        {
            uidHostGrants.Advance(seconds);
            uidClientRequests.Advance(seconds);
            if (!IsHost && IsConnected && World != null && LocalPlayerId != null)
            {
                PlayerRecord player;
                if (World.Players.TryGetValue(LocalPlayerId, out player))
                    RequestUidRenewal(player);
            }
        }
    }
}
