using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession : IDisposable
    {
        private readonly ITransport transport;
        private readonly IGameBridge bridge;
        private readonly SaveStore saves;
        private readonly LocalIdentity identity;
        private readonly Dictionary<string, string> peers = new Dictionary<string, string>();
        private readonly Dictionary<string, string> playerPeers = new Dictionary<string, string>();
        private readonly Dictionary<string, PeerAuthentication> challenges = new Dictionary<string, PeerAuthentication>();
        private readonly Dictionary<long, MapSnapshot> localBaselines = new Dictionary<long, MapSnapshot>();
        private readonly Dictionary<long, MapUpdate> inFlightUpdates = new Dictionary<long, MapUpdate>();
        private readonly HashSet<string> personalInFlight = new HashSet<string>();
        private readonly Dictionary<string, Action<CommandResult>> commandCallbacks = new Dictionary<string, Action<CommandResult>>();
        private readonly Queue<GameCommand> checkpointCommands = new Queue<GameCommand>();
        private Dictionary<long, string> applyingOwners;
        private readonly Dictionary<string, GameCommand> pendingExecutions = new Dictionary<string, GameCommand>();
        private readonly Dictionary<string, CommandResult> executedCommands = new Dictionary<string, CommandResult>();
        private readonly Dictionary<string, string> commandReplyPeers = new Dictionary<string, string>();
        private double autosaveElapsed, syncElapsed, galaxyElapsed, heartbeatElapsed, offlineElapsed;
        private bool closing, disposed;
        private bool shutdown;
        private bool closeNoticeSent, leaveSent, leaveAcknowledged;
        private string nativeFault;
        private bool criticalNativeStarted;
        private bool saveInProgress;
        private double closeRemaining;
        private bool finalCheckpointRequested;
        private readonly HashSet<string> finalCheckpointPeers = new HashSet<string>();
        private readonly Dictionary<long, MapAccess> awaitingLocalReady = new Dictionary<long, MapAccess>();
        public WorldState World { get; private set; }
        public string LocalPlayerId { get; private set; }
        public string SessionId { get; private set; }
        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsStarting { get; private set; }
        public bool IsClosing => closing;
        public bool HostLeft { get; private set; }
        public bool Paused { get; private set; }
        public double Speed { get; private set; } = 1;
        public string LastError { get; private set; }
        public string Status { get; private set; } = "未连接";
        public event Action<string> StatusChanged;
        public event Action<CommandResult> CommandCompleted;
        public MultiplayerSession(ITransport transport, IGameBridge bridge, string storageRoot, LocalIdentity identity)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            saves = new SaveStore(storageRoot ?? throw new ArgumentNullException(nameof(storageRoot)));
        }
        private void SetStatus(string value) { Status = value; StatusChanged?.Invoke(value); }
        private void Error(string value) { LastError = value; SetStatus(value); }
        private void LockNativeFault(string value)
        {
            nativeFault = value; Paused = true;
            if (IsHost && World != null) Broadcast("clock", new ClockState { Paused = true, Speed = Speed, RoomRealSeconds = World.RoomRealSeconds, WorldTimeMM = World.WorldTimeMM });
            Error("原生事务中断，保留现场并禁止保存或重试副作用：" + value);
        }
        private static void Require(BridgeResult result, string operation)
        {
            if (result == null || !result.Success) throw new InvalidOperationException(operation + ": " + (result == null ? "bridge returned null" : result.Error));
        }
        private void AttachBridge()
        {
            bridge.Attach(new BridgeContext
            {
                LocalPlayerId = LocalPlayerId, IsHost = IsHost, SubmitCommand = Submit,
                SubmitBattleConfiguration = SubmitBattleConfiguration, CancelBattlePreparation = CancelBattlePreparation,
                AllocateUid = AllocateUid, CanSimulateMap = CanSimulateMap, GetMapAccess = GetMapAccess,
                IsPaused = () => IsStarting || Paused || closing || nativeFault != null, SimulationSpeed = () => Speed,
                RequestClock = SetClock, RequestSave = Save,
                RequestCancelWorldCreation = () => { if (IsStarting) CancelHostCreation(); },
                GetPlayerRevision = id => World != null && World.Players.ContainsKey(id) ? World.Players[id].PersonalRevision : 0,
                OwnerOfEntity = OwnerOfEntity, OwnerOfMap = OwnerOfMap, GetRelation = GetRelation,
                HasPermission = HasPermission, RegisterEntityOwner = RegisterEntityOwner,
                IsLocalGroundViewActive = IsLocalGroundViewActive,
                ReportDamage = ReportDamage, IsProtectedDefender = IsProtectedDefender,
                IsAttackAllowed = IsAttackAllowed,
                GetAttack = mapId => World != null && World.Maps.ContainsKey(mapId) && World.Maps[mapId].ActiveAttackId != null && World.Attacks.ContainsKey(World.Maps[mapId].ActiveAttackId) ? World.Attacks[World.Maps[mapId].ActiveAttackId] : null,
                IsManagedExpedition = uid => World != null && (World.Transfers.Values.Any(x => x.NativeExpeditionId == uid) || World.Attacks.Values.Any(x => x.LandingExpeditionId == uid)),
                GetTransfer = uid => World == null ? null : World.Transfers.Values.FirstOrDefault(x => x.NativeExpeditionId == uid),
                RegisterCommandCallback = (id, callback) => commandCallbacks[id] = callback
            });
        }
        public void HostNew(WorldOptions options, TransportOptions transportOptions)
        {
            EnsureIdle();
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (transportOptions == null) throw new ArgumentNullException(nameof(transportOptions));
            if (options.MaxPlayers < 1 || options.MaxPlayers > 64) throw new ArgumentOutOfRangeException(nameof(options.MaxPlayers));
            string steamAccount = RequireLocalSteamAccount(transportOptions);
            IsHost = true;
            LocalPlayerId = Guid.NewGuid().ToString("N");
            SessionId = Guid.NewGuid().ToString("N");
            World = new WorldState { WorldId = Guid.NewGuid().ToString("N"), Name = options.Name, HostPlayerId = LocalPlayerId, Options = options };
            var host = NewPlayer(LocalPlayerId, identity.CredentialId, CryptoData.Hash(identity.Secret), identity.Nickname);
            host.SteamId = steamAccount;
            host.Online = true;
            World.Players.Add(host.PlayerId, host);
            host.Uids = ReserveUidRange(false);
            host.IssuedUidRanges.Add(host.Uids);
            AttachBridge();
            pendingHostOptions = JsonData.Clone(transportOptions);
            IsStarting = true;
            SetStatus("正在创建联机世界，请等待……");
            try { Require(bridge.BeginCreateWorld(options, LocalPlayerId, host.Uids), "启动联机世界生成"); }
            catch (Exception error)
            {
                if (bridge.IsCreatingWorld) { DeferStartupFailure(error); return; }
                IsStarting = false; pendingHostOptions = null;
                throw;
            }
        }
        public void HostLoad(string worldId, TransportOptions transportOptions)
        {
            EnsureIdle();
            World = saves.Load(worldId);
            PlayerRecord host;
            if (!World.Players.TryGetValue(World.HostPlayerId, out host)) throw new InvalidOperationException("联机存档缺少明确的房主记录。");
            string steamAccount = ResolveLocalHostSteamAccount(host, transportOptions, null);
            IsHost = true; LocalPlayerId = host.PlayerId; SessionId = Guid.NewGuid().ToString("N");
            foreach (PlayerRecord player in World.Players.Values) { player.Online = false; player.Uids = null; player.IssuedUidRanges.Clear(); }
            foreach (MapRecord map in World.Maps.Values)
            {
                map.SimulatorPlayerId = null; map.Visitors.Clear(); map.AuthorityEpoch++;
                map.AwaitingAuthorityReady = false;
                RecoverGroundOperationSlot(map);
                if (map.Mode != MapMode.Battle) map.Mode = IsFormalBase(map) ? MapMode.Offline : MapMode.Frozen;
            }
            host.Online = true; host.Uids = ReserveUidRange(false);
            host.IssuedUidRanges.Add(host.Uids);
            AttachBridge();
            Require(bridge.LoadWorld(World.Snapshot, host.Snapshot), "恢复联机世界");
            foreach (PlayerRecord player in World.Players.Values)
            {
                Require(bridge.ApplyPlayer(player.Snapshot, false), "恢复玩家");
                Require(bridge.SetPlayerOnline(player.PlayerId, player.Online), "恢复舰队在线状态");
            }
            foreach (RelationshipRecord relationship in World.Relationships) Require(bridge.ApplyRelationship(relationship), "恢复玩家关系");
            SaveWithSteamAccountBinding(host, steamAccount);
            transportOptions.MaxPlayers = World.Options.MaxPlayers;
            transport.StartHost(transportOptions); IsConnected = true;
            host.CurrentMapId = ReconnectMap(host);
            if (host.CurrentMapId >= 0) EnterMap(host, host.CurrentMapId);
            SetStatus((saves.LastLoadWarning == null ? "已恢复联机世界：" : saves.LastLoadWarning + "；") + World.Name);
        }
        public void Join(TransportOptions options)
        {
            EnsureIdle(); IsHost = false; SessionId = null; LocalPlayerId = null;
            transport.Connect(options); SetStatus("正在连接房主");
        }
        private void EnsureIdle()
        {
            if (disposed) throw new ObjectDisposedException(nameof(MultiplayerSession));
            if (IsStarting || closing || transport.IsRunning || IsConnected || World != null) throw new InvalidOperationException("请先退出当前联机会话或等待创建完成。");
            pendingGroundSettlements.Clear(); groundSlotDeniedOperators.Clear();
        }
        public void Tick(double unscaledSeconds)
        {
            if (disposed || shutdown) return;
            if (double.IsNaN(unscaledSeconds) || double.IsInfinity(unscaledSeconds) || unscaledSeconds < 0) throw new ArgumentOutOfRangeException(nameof(unscaledSeconds));
            // Poll even though transport/World are not ready. No network, clock, auto-save or map
            // simulation may observe the native factory's half-created global data.
            if (IsStarting) { TickHostCreation(); return; }
            // Unity's unscaled delta is the authority; no client wall-clock or game speed drives raid timers.
            double seconds = unscaledSeconds;
            TickMapOutbound(seconds); // Retry a rejected first map enqueue, never a committed host ACK.
            TickPersonalOutbound(seconds); // A rejected enqueue retains its original already-captured personal delta.
            TickUidRenewal(seconds); // Unscaled, including paused rooms and lost UID replies.
            TickUnauthenticatedPeers(seconds); // Host idle-handshake lease is independent of pause and game speed.
            TickRejectedPeerEvictions(seconds); // Rejected network peers must not stop the room if Disconnect fails.
            if (IsConnected && IsHost && World != null)
            {
                // Retry only the advisory resume send; a transient transport
                // fault must not skip the rest of the authoritative room tick.
                try { TickGiftResumes(seconds); }
                catch (Exception error) { Error("赠送地表恢复通知重试失败，保留待发状态：" + error.Message); }
            }
            TransportEvent item; int budget = 256;
            while (!shutdown && budget-- > 0 && transport.TryDequeue(out item))
            {
                try { HandleTransport(item); }
                catch (Exception ex)
                {
                    if (shutdown)
                    {
                        // Preserve the close/transport reason instead of relabelling cleanup as a failed handshake.
                        string reason = Status;
                        if (LastError != null && LastError != reason) reason += Environment.NewLine + LastError;
                        Error(reason + Environment.NewLine + "退出联机会话时清理失败：" + ex);
                        return;
                    }
                    Error("联机消息拒绝：" + ex.Message);
                    if (IsHost && item != null && item.PeerId != null && peers.ContainsKey(item.PeerId))
                    {
                        string rejectedPeer = item.PeerId;
                        // An advisory refusal reply can fail on a full or closed
                        // transport queue. Never let that secondary error stop host ticks.
                        const string isolationReason = "拒绝消息的错误提示发送失败；隔离拥塞连接。";
                        try
                        {
                            OptionalPeerNoticeOutcome notice = OptionalPeerNotice.SendOrDisconnect(
                                () => Send(rejectedPeer, "error", ex.Message),
                                () => transport.Disconnect(rejectedPeer, isolationReason));
                            if (notice != OptionalPeerNoticeOutcome.Sent)
                                rejectedPeerEvictions.Track(rejectedPeer, isolationReason);
                        }
                        catch (Exception)
                        {
                            QuarantineRejectedPeer(rejectedPeer, isolationReason);
                        }
                    }
                    if (IsHost && item != null && item.PeerId != null && !peers.ContainsKey(item.PeerId))
                        QuarantineRejectedPeer(item.PeerId, "握手失败");
                }
            }
            if (IsConnected && !transport.IsRunning)
            {
                if (!IsHost && !closing) HostLeft = true;
                Paused = true;
                string cause = LastError ?? "传输已停止";
                if (IsHost && World != null)
                {
                    try { SaveOrThrow(); }
                    catch (Exception ex) { cause += "；退出保存失败：" + ex.Message; }
                }
                Error(cause + "；已关闭联机会话。"); CompleteDisconnect(); return;
            }
            if (closing) { TickClosing(seconds); return; }
            if (!IsConnected || World == null || nativeFault != null) return;
            string tickStage = "session.tick";
            try
            {
                if (!IsHost && checkpointCommands.Count != 0 && inFlightUpdates.Count == 0 && personalInFlight.Count == 0)
                {
                    // Capture the state at the command boundary. Reliable transport keeps this
                    // final delta ahead of the structural command at the host.
                    GameCommand queued = checkpointCommands.Dequeue();
                    Submit(queued);
                }
                foreach (var entry in awaitingLocalReady.ToList())
                {
                    if (!bridge.IsMapReady(entry.Key)) continue;
                    awaitingLocalReady.Remove(entry.Key);
                    var ready = new AuthorityReady { MapId = entry.Key, AuthorityEpoch = entry.Value.AuthorityEpoch, Revision = entry.Value.Revision };
                    if (IsHost) ConfirmAuthority(LocalPlayerId, ready); else Send("host", "ready", ready);
                }
                heartbeatElapsed += seconds;
                if (heartbeatElapsed >= 10) { heartbeatElapsed = 0; if (IsHost) Broadcast("ping", null); else Send("host", "ping", null); }
                // A local map load blocks native asset transactions, not shared-world time
                // or galaxy replication. Capture/save/arrival side effects remain below its gate.
                if (IsHost)
                {
                    if (!Paused)
                    {
                        tickStage = "world.advance";
                        Require(bridge.AdvanceWorld(seconds, Speed, World.Players.Values.Where(x => x.Online).Select(x => x.PlayerId).ToList()), "房主世界推进");
                        World.WorldTimeMM = bridge.WorldTimeMM;
                        World.RoomRealSeconds += seconds;
                        if (!HasMapLoadingBarrier)
                        {
                            TickGroundOperationSlots();
                            tickStage = "transfers.advance";
                            AdvanceTransfers();
                        }
                        foreach (MapRecord map in World.Maps.Values) map.RaidCooldownSeconds = Math.Max(0, map.RaidCooldownSeconds - seconds);
                        tickStage = "attacks.advance";
                        if (HasMapLoadingBarrier) AdvanceLoadingAttackClocks(seconds);
                        else AdvanceAttackTimers(seconds);
                        offlineElapsed += seconds;
                        if (!HasMapLoadingBarrier && offlineElapsed >= 5)
                        {
                            offlineElapsed = 0;
                            tickStage = "maps.offline";
                            foreach (MapRecord map in World.Maps.Values.ToList()) if (map.Mode == MapMode.Offline) SettleOffline(map);
                        }
                    }
                    autosaveElapsed += seconds;
                    if (!HasMapLoadingBarrier && autosaveElapsed >= Math.Max(10, World.Options.AutoSaveSeconds)) { autosaveElapsed = 0; tickStage = "save.autosave"; Save(); }
                    tickStage = "galaxy.broadcast";
                    galaxyElapsed += seconds;
                    if (galaxyElapsed >= 0.5)
                    {
                        galaxyResetRequests.Advance(galaxyElapsed, bridge.ResetGalaxyRecipient);
                        galaxyElapsed = 0;
                        Broadcast("clock", new ClockState { Paused = Paused, Speed = Speed, RoomRealSeconds = World.RoomRealSeconds, WorldTimeMM = World.WorldTimeMM });
                        Broadcast("raidClock", new RaidClockState { Active = World.Attacks.Values.Where(x => x.Stage != AttackStage.Finished).ToList(), Cooldowns = World.Maps.Values.Where(x => x.RaidCooldownSeconds > 0).ToDictionary(x => x.MapId, x => x.RaidCooldownSeconds) });
                        foreach (var peer in peers.ToList()) SendAndConfirmGalaxy(peer.Key, peer.Value);
                    }
                }
                if (HasMapLoadingBarrier) return;
                syncElapsed += seconds;
                if (syncElapsed >= Math.Max(0.1, World.Options.MapSyncSeconds))
                {
                    syncElapsed = 0; tickStage = "maps.publish"; PublishLocalMaps();
                    tickStage = "personal.publish"; PublishPersonalUpdates();
                }
                tickStage = "checkpoint.final";
                if (finalCheckpointRequested && FinalCheckpointGate.TryPublishAndConfirm(
                    () => inFlightUpdates.Count != 0 || personalInFlight.Count != 0 || checkpointCommands.Count != 0,
                    () => PublishLocalMaps(),
                    () => PublishPersonalUpdates(),
                    () => Send("host", "finalDone", null)))
                {
                    // Do not clear this request while newly published deltas
                    // are waiting for host ACK, or when Send has thrown.
                    finalCheckpointRequested = false;
                }
            }
            catch (Exception ex) { Paused = true; ReportSessionFault(tickStage, ex); Error("联机推进已暂停，保留现场：" + ex.Message); }
        }
        public void SetClock(bool paused, double speed)
        {
            if (!IsHost || !IsConnected) throw new InvalidOperationException("只有房主可以暂停或更改倍速。");
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed < 0.1 || speed > 10) throw new ArgumentOutOfRangeException(nameof(speed));
            Paused = paused; Speed = speed;
            Broadcast("clock", new ClockState { Paused = paused, Speed = speed, RoomRealSeconds = World.RoomRealSeconds, WorldTimeMM = World.WorldTimeMM });
        }
        public bool Save()
        {
            if (!IsHost || World == null) { Error("只有房主可以保存联机世界。"); return false; }
            try { SaveOrThrow(); SetStatus("联机世界已保存（代次 " + World.SaveGeneration + "）"); return true; }
            catch (Exception ex) { Error("联机保存失败，已应用事务保留待保存，未确认落盘：" + ex.Message); return false; }
        }
        private void SaveOrThrow(bool captureNativeState = true)
        {
            if (!IsHost || World == null) throw new InvalidOperationException("Not an authoritative host world.");
            if (IsStarting) throw new InvalidOperationException("联机世界正在生成，不能保存半成品世界。");
            if (nativeFault != null) throw new InvalidOperationException("原生应用事务未完成，禁止保存半事务：" + nativeFault);
            if (saveInProgress) throw new InvalidOperationException("联机存档捕获/提交正在进行，不能从原生序列化回调再次进入保存。");
            saveInProgress = true;
            try
            {
                // Every collection is serialized in ONE generation file before the pointer is switched.
                if (captureNativeState)
                {
                    World.Snapshot = bridge.CaptureWorld() ?? throw new InvalidOperationException("Native world capture returned null.");
                    World.WorldTimeMM = World.Snapshot.WorldTimeMM;
                    foreach (PlayerRecord player in World.Players.Values)
                    {
                        PlayerSnapshot snapshot = bridge.CapturePlayer(player.PlayerId);
                        if (snapshot != null) ApplyPlayerRecord(player, snapshot);
                    }
                    foreach (MapRecord map in World.Maps.Values)
                    {
                        if (map.SimulatorPlayerId == LocalPlayerId && !map.AwaitingAuthorityReady)
                        {
                            MapSnapshot snapshot = bridge.CaptureMap(map.MapId);
                            if (snapshot != null) { snapshot.Revision = map.Revision; map.Checkpoint = snapshot; }
                        }
                    }
                }
                if (World.Snapshot == null || World.Players.Values.Any(player => player.Snapshot == null))
                    throw new InvalidOperationException("联机世界或玩家快照不完整，禁止提交存档代。");
                saves.Save(World);
                try { Broadcast("saved", World.SaveGeneration); }
                catch (Exception ex) { Error("存档代已成功提交，但通知部分玩家失败：" + ex.Message); }
            }
            finally { saveInProgress = false; }
        }
        public void Disconnect()
        {
            if (disposed || closing || shutdown) return;
            if (IsStarting) { CancelHostCreation(); return; }
            if (IsConnected && IsHost && World != null)
            {
                Paused = true;
                PublishLocalMaps();
                closing = true; closeRemaining = 2;
                foreach (string peer in peers.Keys) finalCheckpointPeers.Add(peer);
                Broadcast("checkpointRequest", "房主正在收集最终检查点并保存");
                if (finalCheckpointPeers.Count == 0) TickClosing(0);
                return;
            }
            if (IsConnected)
            {
                Paused = true; PublishLocalMaps(); closing = true; closeRemaining = 2;
                TickClosing(0); return;
            }
            CompleteDisconnect();
        }
        private void TickClosing(double seconds)
        {
            if (!closing) return;
            closeRemaining -= seconds;
            var drain = transport as ITransportDrain;
            if (!IsHost)
            {
                if (!leaveSent && ((inFlightUpdates.Count == 0 && personalInFlight.Count == 0) || closeRemaining <= 0))
                {
                    PublishLocalMaps(); PublishPersonalUpdates(); Send("host", "leave", null); leaveSent = true;
                    drain?.BeginDrain();
                }
                if (leaveAcknowledged && (drain == null || drain.IsDrained)) { CompleteDisconnect(); return; }
                if (closeRemaining <= 0) { Error("退出时未收到房主保存确认；重连以房主最后完整保存为准。"); CompleteDisconnect(); }
                return;
            }
            if (!closeNoticeSent)
            {
                if (finalCheckpointPeers.Count > 0 && closeRemaining > 0) return;
                try { SaveOrThrow(); Broadcast("closed", "房主已保存并结束房间"); }
                catch (Exception ex) { Error("房主退出保存失败；下次恢复最后完整存档代：" + ex.Message); Broadcast("closed", "房主保存失败，将恢复上一完整代"); }
                closeNoticeSent = true; closeRemaining = 2; drain?.BeginDrain();
            }
            if ((drain == null || drain.IsDrained) || closeRemaining <= 0) CompleteDisconnect();
        }
        private void CompleteDisconnect()
        {
            if (shutdown) return;
            if (IsStarting || bridge.IsCreatingWorld) { CancelHostCreation(); return; }
            shutdown = true;
            ClearMapOutbound();
            ClearPersonalOutbound();
            Exception transportError = null, bridgeError = null;
            try { transport.Dispose(); }
            catch (Exception error) { transportError = error; }
            try { bridge.Dispose(); }
            catch (Exception error) { bridgeError = error; }
            finally
            {
                IsConnected = false;
                closing = false;
                if (LastError == null) SetStatus("已退出联机");
            }
            if (transportError != null && bridgeError != null) throw new AggregateException("传输及原生上下文清理均失败。", transportError, bridgeError);
            if (transportError != null) throw new InvalidOperationException("传输清理失败。", transportError);
            if (bridgeError != null) throw new InvalidOperationException("原生上下文清理失败。", bridgeError);
        }
        public void Dispose()
        {
            if (disposed) return;
            if (IsStarting || bridge.IsCreatingWorld)
            {
                disposeRequested = true; CancelHostCreation(); return;
            }
            Disconnect();
            if (closing) { closeRemaining = 0; TickClosing(0); if (closing) CompleteDisconnect(); }
            disposed = true;
        }
    }
}
