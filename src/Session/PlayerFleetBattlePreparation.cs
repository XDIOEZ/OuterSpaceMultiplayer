using System;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private const double FleetConfigurationSeconds = 600;
        private const int FleetConfigurationMaxCharacters = 256 * 1024;

        private static bool BattleConfigurationsComplete(PlayerFleetBattleRecord record)
        {
            return record.AttackerConfiguration != null && record.DefenderConfiguration != null;
        }

        public void SubmitBattleConfiguration(PlayerFleetBattleConfiguration configuration)
        {
            RequireCommunicationRoom();
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            configuration = JsonData.Clone(configuration);
            configuration.PlayerId = LocalPlayerId;
            if (IsHost) HostBattleConfiguration(LocalPlayerId, configuration);
            else Send("host", "battle.configuration", configuration);
        }

        public void CancelBattlePreparation(PlayerFleetBattleReference reference)
        {
            RequireCommunicationRoom();
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            if (IsHost) HostCancelBattlePreparation(LocalPlayerId, reference);
            else Send("host", "battle.cancel", reference);
        }

        private string FleetConfigurationInvalidReason(PlayerFleetBattleRecord record)
        {
            PlayerRecord attacker, defender;
            if (closing || shutdown || nativeFault != null)
                return "房间无法继续，已取消本次战前配置。";
            if (!World.Players.TryGetValue(record.AttackerPlayerId, out attacker) || !attacker.Online
                || !World.Players.TryGetValue(record.DefenderPlayerId, out defender) || !defender.Online)
                return "参战玩家已离线，已取消本次战前配置。";
            if (GetRelation(record.AttackerPlayerId, record.DefenderPlayerId) != Relation.Hostile
                || OwnerOfEntity(record.AttackerFleetId) != record.AttackerPlayerId
                || OwnerOfEntity(record.DefenderFleetId) != record.DefenderPlayerId)
                return "双方关系或舰队归属已变化，已取消本次战前配置。";
            if (record.PreparationExpiresAtRoomSeconds <= World.RoomRealSeconds)
                return "战前配置超过 10 分钟，已取消本次战斗，双方可继续航行。";
            return null;
        }

        private void TickFleetConfiguration(PlayerFleetBattleRecord record)
        {
            string reason = FleetConfigurationInvalidReason(record);
            if (reason != null)
            {
                PlayerFleetBattleTrace.Emit("preparation.host.expired-or-invalid", record,
                    "expired=" + (record.PreparationExpiresAtRoomSeconds <= World.RoomRealSeconds)
                    + " closing=" + (closing || shutdown) + " nativeFault=" + (nativeFault != null), true);
                InterruptBattle(record, reason);
            }
        }

        private void HostBattleConfiguration(string actor, PlayerFleetBattleConfiguration configuration)
        {
            PlayerFleetBattleRecord traceRecord = null;
            if (configuration != null && configuration.BattleId != null && World != null && World.FleetBattles != null)
                World.FleetBattles.TryGetValue(configuration.BattleId, out traceRecord);
            PlayerFleetBattleTrace.Emit("preparation.host.configuration-received", traceRecord,
                "actor=" + actor + " requestEpoch=" + (configuration == null ? 0 : configuration.AuthorityEpoch)
                + " jsonChars=" + (configuration == null || configuration.NativeJson == null ? 0 : configuration.NativeJson.Length));
            try { HostBattleConfigurationCore(actor, configuration); }
            catch (Exception error)
            {
                PlayerFleetBattleTrace.Emit("preparation.host.configuration-rejected", traceRecord,
                    "actor=" + actor + " paused=" + Paused + " error=" + error.GetType().Name, true);
                throw;
            }
        }

        private void HostBattleConfigurationCore(string actor, PlayerFleetBattleConfiguration configuration)
        {
            PlayerFleetBattleRecord record;
            if (configuration == null || !CommunicationId(configuration.BattleId)
                || !World.FleetBattles.TryGetValue(configuration.BattleId, out record)
                || record.AuthorityEpoch != configuration.AuthorityEpoch || !IsBattleParticipant(record, actor)
                || configuration.PlayerId != actor
                || configuration.FleetId != (actor == record.AttackerPlayerId ? record.AttackerFleetId : record.DefenderFleetId)
                || configuration.NativeJson == null || configuration.NativeJson.Length > FleetConfigurationMaxCharacters)
                throw new MessageRejectionException("战前配置的玩家、舰队或战斗任期无效。");

            PlayerFleetBattleConfiguration previous = actor == record.AttackerPlayerId
                ? record.AttackerConfiguration : record.DefenderConfiguration;
            // A confirmation is immutable. Reliable retries can return its state,
            // but cannot replace a deployment after the other player has confirmed.
            if (previous != null)
            {
                if (!string.Equals(previous.NativeJson, configuration.NativeJson, StringComparison.Ordinal))
                {
                    PlayerFleetBattleTrace.Emit("preparation.host.configuration-replacement-rejected", record, "actor=" + actor, true);
                    throw new MessageRejectionException("本方已确认战前配置，不能在本次战斗中替换。");
                }
                if (record.Stage == PlayerFleetBattleStage.Preparing || record.Stage == PlayerFleetBattleStage.Active)
                {
                    PlayerFleetBattleTrace.Emit("preparation.host.configuration-duplicate", record, "actor=" + actor);
                    SendBattleState(record); return;
                }
            }
            if (record.Stage != PlayerFleetBattleStage.Preparing || !record.PreparationAuthorized
                || record.SceneAuthorized || record.Interrupted)
                throw new MessageRejectionException("本次原版战前配置已失效。");
            string reason = FleetConfigurationInvalidReason(record);
            if (reason != null)
            {
                PlayerFleetBattleTrace.Emit("preparation.host.configuration-invalidated", record,
                    "actor=" + actor + " expired=" + (record.PreparationExpiresAtRoomSeconds <= World.RoomRealSeconds), true);
                InterruptBattle(record, reason); return;
            }
            if (Paused) throw new MessageRejectionException("房间已暂停，请恢复后再确认战前配置。");
            if (World.Players[record.AttackerPlayerId].CurrentMapId >= 0 || World.Players[record.DefenderPlayerId].CurrentMapId >= 0)
                throw new MessageRejectionException("双方尚未完成地表离图，不能确认战前配置。");
            if (!ConsumeCommunicationRate(actor, "battle")) throw new MessageRejectionException("战前配置提交过于频繁。");
            // The bridge parses a fixed deployment DTO and checks every ship against
            // this fleet's actual native roster; ownership tags alone are insufficient.
            Require(bridge.ValidatePlayerFleetBattleConfiguration(record, configuration), "校验本方原版舰队战前配置");
            PlayerFleetBattleTrace.Emit("preparation.host.configuration-validated", record, "actor=" + actor);
            if (actor == record.AttackerPlayerId) record.AttackerConfiguration = JsonData.Clone(configuration);
            else record.DefenderConfiguration = JsonData.Clone(configuration);
            try
            {
                if (BattleConfigurationsComplete(record))
                {
                    Require(bridge.ValidatePlayerFleetBattleConfiguration(record, record.AttackerConfiguration), "复核进攻方最终战前配置");
                    Require(bridge.ValidatePlayerFleetBattleConfiguration(record, record.DefenderConfiguration), "复核防守方最终战前配置");
                    record.SceneAuthorized = true;
                    record.PreparationExpiresAtRoomSeconds = 0;
                    battleDeadlines[record.BattleId] = 45;
                }
                SaveOrThrow();
                PlayerFleetBattleTrace.Emit(record.SceneAuthorized ? "preparation.host.scene-authorized" : "preparation.host.waiting-opponent",
                    record, "actor=" + actor);
                SendBattleState(record);
            }
            catch (Exception error)
            {
                PlayerFleetBattleTrace.Emit("preparation.host.configuration-failed", record,
                    "actor=" + actor + " error=" + error.GetType().Name, true);
                BlockBattle(record, error);
            }
        }

        private void HostCancelBattlePreparation(string actor, PlayerFleetBattleReference reference)
        {
            PlayerFleetBattleRecord record;
            if (reference == null || !CommunicationId(reference.BattleId)
                || !World.FleetBattles.TryGetValue(reference.BattleId, out record)
                || record.AuthorityEpoch != reference.AuthorityEpoch || !IsBattleParticipant(record, actor))
                throw new MessageRejectionException("取消战前配置的身份或任期无效。");
            if (record.Stage == PlayerFleetBattleStage.Finished) { SendBattleState(record); return; }
            if (record.Stage != PlayerFleetBattleStage.Preparing)
                throw new MessageRejectionException("本场已开战，不能再取消战前配置。");
            PlayerFleetBattleTrace.Emit("preparation.host.cancel-accepted", record, "actor=" + actor);
            InterruptBattle(record, "参战玩家已取消战前配置，双方可继续航行。");
        }
    }
}
