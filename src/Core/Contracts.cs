using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    public enum TransportEventKind { Connected, Disconnected, Message, Error }
    public enum Relation { Neutral, Friendly, Hostile }
    public enum MapMode { Frozen, Offline, Realtime, Battle }
    public enum MapAccessMode { Frozen, Replica, Simulator }
    public enum TransferStage { InTransit, Arrived, Cancelled, Preparing, Blocked, Landing }
    public enum AttackStage { Loading, Active, Finished }
    public enum RaidFinishReason { Evacuated, Timeout, Interrupted }
    public enum Permission { View, ControlOwnHero, Work, UseBuilding, TakeInventory, Attack, Transfer, Loot }
    public enum CommandKind { Move, Conscript, Attack, Work, Build, Deconstruct, ConfigureBuilding, TakeItem, ClaimMap, SetBase, EnterMap, LeaveMap, Relation, DepartTransfer, ArriveTransfer, CancelTransfer, StartRaid, RaidReady, EvacuateRaid, DamageReported, Native, FleetCommand }

    public sealed class TransportOptions
    {
        public string Address = "127.0.0.1";
        public int Port = 27515;
        public int MaxPlayers = 4;
        public string RoomId = "";
        public bool UseSteam;
    }
    public sealed class TransportEvent
    {
        public TransportEventKind Kind;
        public string PeerId;
        public byte[] Data;
        public string Error;
    }
    // Implementations enqueue callbacks; only MultiplayerSession.Tick consumes them.
    public interface ITransport : IDisposable
    {
        string Name { get; }
        string LocalPeerId { get; }
        bool IsRunning { get; }
        void StartHost(TransportOptions options);
        void Connect(TransportOptions options);
        void Send(string peerId, byte[] data);
        void Disconnect(string peerId, string reason);
        bool TryDequeue(out TransportEvent item);
    }
    public interface ITransportDrain
    {
        bool IsDrained { get; }
        void BeginDrain();
    }
    public sealed class WorldOptions
    {
        public string Name = "联机世界";
        public string StartPresetJson = "";
        public int MaxPlayers = 4;
        public double AutoSaveSeconds = 300;
        public double MapSyncSeconds = 0.25;
    }
    public sealed class LocalIdentity
    {
        public string CredentialId;
        public string Secret;
        public string Nickname;
    }
    public sealed class UidRange
    {
        public long First;
        public long Last;
        public long Next;
        public string SessionId;
    }
    public sealed class PlayerRecord
    {
        public string PlayerId;
        public string Nickname;
        public string CredentialId;
        public string CredentialHash;
        public string SteamId;
        public bool InitialAssetsGranted;
        public bool Online;
        public long PersonalRevision;
        public long MotherShipMapId = -1;
        public long FleetId = -1;
        public long FormalBaseMapId = -1;
        public long CurrentMapId = -1;
        public string GalaxyId = "";
        public PlayerSnapshot Snapshot;
        public UidRange Uids;
        public List<UidRange> IssuedUidRanges = new List<UidRange>();
    }
    public sealed class PlayerSnapshot
    {
        public string PlayerId;
        public string NativeJson;
        public long MotherShipMapId = -1;
        public long FleetId = -1;
        public List<long> HeroIds = new List<long>();
        public List<long> OwnedEntityIds = new List<long>();
    }
    public sealed class PlayerUpdate
    {
        public string PlayerId;
        public long BaseRevision;
        public long Revision;
        public string NativeDeltaJson;
    }
    public sealed class PlayerFieldState
    {
        public string PlayerId;
        public long PersonalRevision;
        public string NativeFieldsJson;
    }
    public sealed class GameSnapshot
    {
        public string NativeJson;
        public long WorldTimeMM;
    }
    public sealed class MapSnapshot
    {
        public long MapId;
        public long Revision;
        public string NativeJson;
        public Dictionary<long, string> EntityOwners = new Dictionary<long, string>();
    }
    public sealed class MapEntryRequest
    {
        public string GalaxyId;
        public string StarId;
        public int WorldX;
        public int WorldY;
        public long KnownMapId = -1;
        public bool ResolveOnly;
    }
    public sealed class MapUpdate
    {
        public string CommitId;
        public string SessionId;
        public long MapId;
        public long AuthorityEpoch;
        public long BaseRevision;
        public long Revision;
        public string NativeDeltaJson;
        public MapSnapshot Checkpoint;
        public List<PlayerUpdate> Players = new List<PlayerUpdate>();
        public Dictionary<long, string> EntityOwners = new Dictionary<long, string>();
    }
    public sealed class MapAccess
    {
        public long MapId;
        public long AuthorityEpoch;
        public long Revision;
        public string SimulatorPlayerId;
        public string OperatorPlayerId;
        public MapAccessMode Access;
    }
    public sealed class MapRecord
    {
        public long MapId;
        public string OwnerPlayerId;
        public string SimulatorPlayerId;
        // Player input authority is exclusive; the simulator may run only AI for observers.
        public string OperatorPlayerId;
        public double OperatorReleaseAtRoomSeconds;
        public long AuthorityEpoch;
        public long Revision;
        public long LastSimulatedWorldMM;
        public MapMode Mode;
        public MapSnapshot Checkpoint;
        public List<string> Visitors = new List<string>();
        public double RaidCooldownSeconds;
        public string ActiveAttackId;
        public bool AwaitingAuthorityReady;
        public string LastCommitId;
    }
    public sealed class RelationshipRecord
    {
        public string PlayerA;
        public string PlayerB;
        public Relation Relation;
        public bool FriendlyConsentA;
        public bool FriendlyConsentB;
    }
    public sealed class GameCommand
    {
        public string CommandId = Guid.NewGuid().ToString("N");
        public string SessionId;
        public string PlayerId;
        public CommandKind Kind;
        public long MapId = -1;
        public long EntityId = -1;
        public long TargetEntityId = -1;
        public string TargetPlayerId;
        public long AuthorityEpoch;
        public long BaseRevision;
        public string PayloadJson = "{}";
    }
    public sealed class CommandResult
    {
        public string CommandId;
        public string PlayerId;
        public bool Success;
        public bool Applied;
        public bool Saved;
        public long SaveGeneration;
        public string Error;
        public string PayloadJson;
        public static CommandResult Ok(string id, string payload = null) { return new CommandResult { CommandId = id, Success = true, Applied = true, PayloadJson = payload }; }
        public static CommandResult Fail(string id, string reason) { return new CommandResult { CommandId = id, Error = reason }; }
    }
    public sealed class BridgeResult
    {
        public bool Success;
        public bool NativeUnchanged;
        public string Error;
        public string PayloadJson;
        public static BridgeResult Ok(string payload = null) { return new BridgeResult { Success = true, PayloadJson = payload }; }
        public static BridgeResult Fail(string error) { return new BridgeResult { Error = error }; }
        public static BridgeResult Reject(string error) { return new BridgeResult { Error = error, NativeUnchanged = true }; }
    }
    internal sealed class NativeRejectionException : InvalidOperationException
    {
        internal NativeRejectionException(string message) : base(message) { }
        internal NativeRejectionException(string message, Exception inner) : base(message, inner) { }
    }
    public sealed class TransferRequest
    {
        public string TransferId;
        public long SourceMapId = -1;
        public long DestinationMapId = -1;
        public long SourceFleetId = -1;
        public long DestinationFleetId = -1;
        public string NativeManifestJson;
        public List<long> HeroIds = new List<long>();
    }
    public sealed class TransferRecord
    {
        public string TransferId;
        public string PlayerId;
        public TransferStage Stage;
        public TransferRequest Request;
        public string NativeCargoJson;
        public long ArrivalWorldMM = -1;
        public double GroundLandedAtRoomSeconds = -1;
        public long NativeExpeditionId = -1;
        public bool Returning;
        public string Error;
    }
    public sealed class RaidRequest
    {
        public long MapId;
        public long DropPodId;
        public long SourceFleetId;
        public string NativeTransportJson;
        public List<long> AttackerHeroIds = new List<long>();
    }
    public sealed class RaidStartData
    {
        public double Capacity;
        public List<long> ProtectedDefenderHeroIds = new List<long>();
    }
    public sealed class RaidEvacuation
    {
        public string AttackId;
        public string TransferId;
        public string NativeManifestJson;
        public double CapacityUsed;
        public TransferRecord Transfer;
    }
    public sealed class AttackRecord
    {
        public string AttackId;
        public long MapId;
        public string AttackerPlayerId;
        public string DefenderPlayerId;
        public long RegisteredMapRevision;
        public long DropPodId;
        public long SourceFleetId;
        public string NativeTransportJson;
        public string NativeBattleState;
        public long LandingExpeditionId = -1;
        public double Capacity;
        public double ElapsedSeconds;
        public double GroundLandedAtRoomSeconds = -1;
        public AttackStage Stage;
        public bool EvacuationUsed;
        public string EvacuationTransferId;
        public bool Blocked;
        public string Error;
        public bool DefendersDefeated;
        public RaidFinishReason FinishReason;
        public List<long> AttackerHeroIds = new List<long>();
        public List<long> ProtectedDefenderHeroIds = new List<long>();
    }
    public sealed class GalaxySnapshot
    {
        public string NativeJson;
        public long Revision;
    }
    public sealed class BridgeContext
    {
        public string LocalPlayerId;
        public bool IsHost;
        public Func<GameCommand, CommandResult> SubmitCommand;
        public Action<PlayerFleetBattleConfiguration> SubmitBattleConfiguration;
        public Action<PlayerFleetBattleReference> CancelBattlePreparation;
        public Func<long, long> AllocateUid;
        public Func<long, bool> CanSimulateMap;
        public Func<bool> IsPaused;
        public Func<double> SimulationSpeed;
        public Action<bool, double> RequestClock;
        public Func<bool> RequestSave;
        public Action RequestCancelWorldCreation;
        public Func<long, MapAccess> GetMapAccess;
        public Func<long, bool> IsLocalGroundViewActive;
        public Func<string, long> GetPlayerRevision;
        public Func<long, string> OwnerOfEntity;
        public Func<long, string> OwnerOfMap;
        public Func<string, string, Relation> GetRelation;
        public Func<string, long, long, Permission, bool> HasPermission;
        public Func<string, long, bool> CanOperateMap;
        public Func<long, string> OperatorOfMap;
        public Func<long, int> GroundObserverCount;
        public Action<long, string> RegisterEntityOwner;
        public Action<GameCommand> ReportDamage;
        public Func<long, bool> IsProtectedDefender;
        public Func<long, bool> IsAttackAllowed;
        public Func<long, AttackRecord> GetAttack;
        public Func<long, bool> IsManagedExpedition;
        public Func<long, TransferRecord> GetTransfer;
        public Action<string, Action<CommandResult>> RegisterCommandCallback;
    }
    // Known native types must be serialized explicitly inside this implementation.
    // NativeJson is opaque data, never a client-selected CLR type name.
    public interface IGameBridge : IDisposable
    {
        BridgeResult BeginPlayerFleetBattlePreparation(PlayerFleetBattleRecord battle);
        BridgeResult ValidatePlayerFleetBattleConfiguration(PlayerFleetBattleRecord battle, PlayerFleetBattleConfiguration configuration);
        BridgeResult CancelPlayerFleetBattlePreparation(PlayerFleetBattleRecord battle);
        BridgeResult ValidatePlayerFleetBattleInputShip(PlayerFleetBattleRecord battle, PlayerFleetBattleInput input);
        void Attach(BridgeContext context);
        bool IsCreatingWorld { get; }
        BridgeResult BeginCreateWorld(WorldOptions options, string hostPlayerId, UidRange uids);
        // Pending is Success with completed=false and null snapshots. A failure is completed=true;
        // IsCreatingWorld remains true until the worker has ended and this poll consumes its result.
        BridgeResult PollCreateWorld(out bool completed, out GameSnapshot world, out PlayerSnapshot player);
        BridgeResult CreatePlayer(string playerId, UidRange uids, out PlayerSnapshot player);
        BridgeResult LoadWorld(GameSnapshot world, PlayerSnapshot localPlayer);
        GameSnapshot CaptureWorld();
        PlayerSnapshot CapturePlayer(string playerId);
        BridgeResult ApplyPlayer(PlayerSnapshot player, bool localPlayer);
        BridgeResult ApplyPlayerUpdate(PlayerUpdate update);
        PlayerFieldState CapturePlayerFields(string playerId);
        List<PlayerUpdate> CapturePlayerUpdates();
        BridgeResult ApplyPlayerFields(PlayerFieldState state);
        MapSnapshot CaptureMap(long mapId);
        MapUpdate CaptureMapUpdate(long mapId, MapSnapshot previous);
        BridgeResult ApplyMap(MapSnapshot map, MapAccess access);
        BridgeResult ApplyMapUpdate(MapUpdate update);
        BridgeResult ValidateMapUpdate(MapUpdate update);
        BridgeResult SetMapAuthority(MapAccess access);
        bool IsMapReady(long mapId);
        bool HasGroundPlayerHeroes(long mapId, string playerId);
        BridgeResult ExecuteCommand(GameCommand command);
        BridgeResult ExecuteFleetCommand(GameCommand command);
        BridgeResult ClaimMap(long mapId, string ownerPlayerId);
        BridgeResult SetFormalBase(long mapId, string ownerPlayerId);
        BridgeResult ResolveMapEntry(MapEntryRequest request, out long canonicalMapId);
        BridgeResult AdvanceWorld(double realSeconds, double speed, IList<string> onlinePlayerIds);
        long WorldTimeMM { get; }
        BridgeResult SimulateOffline(long mapId, long fromWorldMM, long toWorldMM);
        GalaxySnapshot CaptureGalaxy(string playerId);
        BridgeResult ApplyGalaxy(GalaxySnapshot galaxy);
        BridgeResult SetPlayerOnline(string playerId, bool online);
        BridgeResult ApplyRelationship(RelationshipRecord relationship);
        BridgeResult DepartTransfer(TransferRecord transfer);
        BridgeResult ArriveTransfer(TransferRecord transfer);
        BridgeResult CancelTransfer(TransferRecord transfer);
        bool IsTransferComplete(TransferRecord transfer);
        BridgeResult InspectRaid(RaidRequest request, string attackerPlayerId, out RaidStartData data);
        BridgeResult BeginRaid(AttackRecord attack);
        BridgeResult EvacuateRaid(AttackRecord attack, RaidEvacuation evacuation);
        BridgeResult FinishRaid(AttackRecord attack, RaidFinishReason reason);
        bool AreDefendersDefeated(AttackRecord attack);
        bool IsRaidReady(AttackRecord attack);
    }
    public sealed class WorldState
    {
        public int SchemaVersion = 1;
        public string WorldId;
        public string Name;
        public string HostPlayerId;
        public long SaveGeneration;
        public long UidHighWater = 1000000000000L;
        public long WorldTimeMM;
        public double RoomRealSeconds;
        public WorldOptions Options;
        public GameSnapshot Snapshot;
        public Dictionary<string, PlayerRecord> Players = new Dictionary<string, PlayerRecord>();
        public Dictionary<long, MapRecord> Maps = new Dictionary<long, MapRecord>();
        public Dictionary<long, string> EntityOwners = new Dictionary<long, string>();
        public List<RelationshipRecord> Relationships = new List<RelationshipRecord>();
        public Dictionary<string, TransferRecord> Transfers = new Dictionary<string, TransferRecord>();
        public Dictionary<string, AttackRecord> Attacks = new Dictionary<string, AttackRecord>();
        public Dictionary<string, CommandResult> Transactions = new Dictionary<string, CommandResult>();
        public List<string> RevokedCredentialIds = new List<string>();
    }
}
