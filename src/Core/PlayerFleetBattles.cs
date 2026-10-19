using System;

namespace FreeWorldMultiplayer
{
    public enum PlayerFleetBattleStage { Preparing, Active, Committing, Finished, Blocked, AwaitingResponse }
    public sealed class PlayerFleetBattleRecord
    {
        public string BattleId, AttackerPlayerId, DefenderPlayerId, SimulatorPlayerId, Error;
        public long NativeBattleUid, AuthorityEpoch, AttackerFleetId, DefenderFleetId, Revision;
        public PlayerFleetBattleStage Stage;
        public PlayerFleetBattleFrame Checkpoint;
        public bool Interrupted, AssetsCommitted, SceneAuthorized;
        public double InterceptExpiresAtRoomSeconds;
    }
    public sealed class PlayerFleetBattleFrame
    {
        public string BattleId, NativeJson, Outcome;
        public long AuthorityEpoch, BaseRevision, Revision;
        public bool Ended, FinalCheckpoint;
    }
    public sealed class PlayerFleetBattleInput
    {
        public string BattleId, CommandId, PlayerId, Operation, NativeJson;
        public long AuthorityEpoch, FleetId, ShipId, Sequence;
    }
    internal sealed class PlayerFleetBattleRequest
    {
        public string RequestId, TargetPlayerId;
        public long OwnFleetId, TargetFleetId;
    }
    internal sealed class PlayerFleetBattleReady { public string BattleId; public long AuthorityEpoch; public bool Success; public string Error; public PlayerFleetBattleFrame Checkpoint; }
    internal sealed class PlayerFleetInterceptResponse { public string BattleId; public long AuthorityEpoch; public bool Engage; }
    internal sealed class PlayerFleetBattleReference { public string BattleId; public long AuthorityEpoch; }
    internal sealed class PlayerFleetBattleAck { public string BattleId; public long AuthorityEpoch, Revision; }
}
