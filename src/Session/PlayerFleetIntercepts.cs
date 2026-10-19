using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        public PlayerFleetBattleRecord GetPendingFleetIntercept()
        {
            if (!IsConnected || World == null || World.FleetBattles == null || closing || shutdown) return null;
            foreach (PlayerFleetBattleRecord record in World.FleetBattles.Values)
                if (record.Stage == PlayerFleetBattleStage.AwaitingResponse && record.DefenderPlayerId == LocalPlayerId)
                    return JsonData.Clone(record);
            return null;
        }

        public void RespondPlayerFleetIntercept(string battleId, bool engage)
        {
            RequireCommunicationRoom();
            PlayerFleetBattleRecord record;
            if (!CommunicationId(battleId) || !World.FleetBattles.TryGetValue(battleId, out record)
                || record.DefenderPlayerId != LocalPlayerId)
                throw new InvalidOperationException("该舰队拦截不属于当前防守玩家。");
            if (record.Stage != PlayerFleetBattleStage.AwaitingResponse) return;
            var response = new PlayerFleetInterceptResponse { BattleId = battleId, AuthorityEpoch = record.AuthorityEpoch, Engage = engage };
            if (IsHost) HostFleetInterceptResponse(LocalPlayerId, response);
            else Send("host", "battle.respond", response);
        }

        private string FleetInterceptInvalidReason(PlayerFleetBattleRecord record)
        {
            PlayerRecord attacker, defender;
            if (!World.Players.TryGetValue(record.AttackerPlayerId, out attacker) || !attacker.Online
                || !World.Players.TryGetValue(record.DefenderPlayerId, out defender) || !defender.Online)
                return "参战玩家已离线，本次拦截已取消。";
            if (GetRelation(record.AttackerPlayerId, record.DefenderPlayerId) != Relation.Hostile)
                return "双方已不再敌对，本次拦截已取消。";
            if (OwnerOfEntity(record.AttackerFleetId) != record.AttackerPlayerId
                || OwnerOfEntity(record.DefenderFleetId) != record.DefenderPlayerId)
                return "参战舰队归属已变化，本次拦截已取消。";
            if (record.InterceptExpiresAtRoomSeconds <= World.RoomRealSeconds)
                return "防守方未在期限内回应，本次拦截已取消，双方可继续航行。";
            return null;
        }

        private void TickFleetIntercept(PlayerFleetBattleRecord record)
        {
            string reason = FleetInterceptInvalidReason(record);
            if (reason != null) FinishFleetIntercept(record, reason);
        }

        private void HostFleetInterceptResponse(string actor, PlayerFleetInterceptResponse response)
        {
            PlayerFleetBattleRecord record;
            if (response == null || !CommunicationId(response.BattleId)
                || !World.FleetBattles.TryGetValue(response.BattleId, out record) || record.DefenderPlayerId != actor)
                throw new MessageRejectionException("舰队拦截回应不属于当前防守玩家。");
            // A reliable retransmission can arrive after consent or cancellation.
            // Return the authoritative state without restarting preparation or its timer.
            if (record.Stage != PlayerFleetBattleStage.AwaitingResponse)
            {
                if (response.AuthorityEpoch <= 0 || response.AuthorityEpoch > record.AuthorityEpoch)
                    throw new MessageRejectionException("舰队拦截回应的任期已失效。");
                SendBattleState(record); return;
            }
            if (response.AuthorityEpoch != record.AuthorityEpoch)
                throw new MessageRejectionException("舰队拦截回应的任期已失效。");
            string reason = FleetInterceptInvalidReason(record);
            if (reason != null) { FinishFleetIntercept(record, reason); return; }
            if (closing || shutdown || nativeFault != null)
            { FinishFleetIntercept(record, "房间正在关闭或无法继续，本次拦截已取消。"); return; }
            // Fleeing cancels this interception without changing the native route,
            // fleet, cargo or crew. Removing the lock resumes the existing journey.
            if (!response.Engage)
            { FinishFleetIntercept(record, "防守方已脱离本次拦截，双方可继续航行。"); return; }
            if (Paused) throw new MessageRejectionException("房间已暂停，请恢复后再选择迎战。");
            BridgeResult contact = bridge.ValidatePlayerFleetContact(record.AttackerPlayerId, record.AttackerFleetId,
                record.DefenderPlayerId, record.DefenderFleetId);
            if (contact == null || !contact.Success)
            { FinishFleetIntercept(record, "双方舰队已不满足真实接触条件，本次拦截已取消。"); return; }

            long oldEpoch = record.AuthorityEpoch;
            double expires = record.InterceptExpiresAtRoomSeconds;
            record.Stage = PlayerFleetBattleStage.Preparing;
            record.AuthorityEpoch = checked(oldEpoch + 1);
            record.InterceptExpiresAtRoomSeconds = 0;
            try { SaveOrThrow(); }
            catch
            {
                record.Stage = PlayerFleetBattleStage.AwaitingResponse; record.AuthorityEpoch = oldEpoch;
                record.InterceptExpiresAtRoomSeconds = expires;
                throw;
            }
            battleReady[record.BattleId] = new HashSet<string>();
            battleQuiescent[record.BattleId] = new HashSet<string>();
            battleDeadlines[record.BattleId] = 45;
            SendBattleState(record);
        }

        private void FinishFleetIntercept(PlayerFleetBattleRecord record, string reason)
        {
            if (record.Stage != PlayerFleetBattleStage.AwaitingResponse) return;
            long oldEpoch = record.AuthorityEpoch;
            string oldError = record.Error;
            bool oldInterrupted = record.Interrupted;
            double expires = record.InterceptExpiresAtRoomSeconds;
            record.Stage = PlayerFleetBattleStage.Finished;
            record.AuthorityEpoch = checked(oldEpoch + 1);
            record.Interrupted = true; record.Error = reason;
            record.InterceptExpiresAtRoomSeconds = 0;
            try { SaveOrThrow(); }
            catch
            {
                record.Stage = PlayerFleetBattleStage.AwaitingResponse; record.AuthorityEpoch = oldEpoch;
                record.Interrupted = oldInterrupted; record.Error = oldError; record.InterceptExpiresAtRoomSeconds = expires;
                throw;
            }
            TrimFinishedBattles();
            SendBattleState(record);
        }
    }
}
