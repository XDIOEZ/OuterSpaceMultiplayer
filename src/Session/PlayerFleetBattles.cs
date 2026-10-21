using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private sealed class LocalBattlePreparation { internal bool LeaveSubmitted, FlushSubmitted, PersonalFlushed, Quiescent, Began, Ready, Failed, FinalCaptured; internal double CaptureElapsed; }
        private readonly Dictionary<string, LocalBattlePreparation> localBattlePreparations = new Dictionary<string, LocalBattlePreparation>();
        private readonly Dictionary<string, HashSet<string>> battleReady = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, HashSet<string>> battleQuiescent = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, double> battleDeadlines = new Dictionary<string, double>();
        private readonly BattleTickRecordSnapshot battleTickRecords = new BattleTickRecordSnapshot();
        private sealed class BattleFrameWindow
        {
            internal readonly Dictionary<long, PlayerFleetBattleFrame> Frames = new Dictionary<long, PlayerFleetBattleFrame>();
            internal int Characters;
        }
        private const int BattleFrameMaxCharacters = 8 * 1024 * 1024;
        private static readonly Func<PlayerFleetBattleFrame, int> PendingBattleFrameLength =
            frame => frame.NativeJson.Length;
        private readonly Dictionary<string, BattleFrameWindow> pendingBattleFrames = new Dictionary<string, BattleFrameWindow>();
        private readonly BattleInputSequenceIndex battleInputSequences = new BattleInputSequenceIndex();
        private readonly HashSet<string> battleResyncPending = new HashSet<string>();
        private long nextBattleInput;
        private bool IsBattleParticipant(PlayerFleetBattleRecord battle, string player) => battle.AttackerPlayerId == player || battle.DefenderPlayerId == player;
        public PlayerFleetBattleRecord GetFleetBattleLock(long fleetUid)
        { return FleetBattleLockProbe.FindFleet(World == null ? null : World.FleetBattles, fleetUid); }
        public bool IsPlayerFleetBattleLocked(long fleetUid) => GetFleetBattleLock(fleetUid) != null;
        private bool PlayerHasFleetBattle(string playerId)
        { return FleetBattleLockProbe.HasParticipant(World == null ? null : World.FleetBattles, playerId); }
        public void RequestPlayerFleetAttack(string playerId, long fleetUid)
        {
            ValidateCommunicationTarget(playerId, fleetUid);
            if (GetRelation(LocalPlayerId, playerId) != Relation.Hostile) throw new InvalidOperationException("只有双方已经敌对时才可攻击玩家舰队。");
            var request = new PlayerFleetBattleRequest { RequestId = Guid.NewGuid().ToString("N"), TargetPlayerId = playerId,
                OwnFleetId = World.Players[LocalPlayerId].FleetId, TargetFleetId = fleetUid };
            if (IsHost) HostBattleRequest(LocalPlayerId, request); else Send("host", "battle.request", request);
        }
        private void HostBattleRequest(string actor, PlayerFleetBattleRequest request)
        {
            if (closing || Paused || nativeFault != null) throw new MessageRejectionException("房间已暂停或正在关闭，不能开始舰战。");
            PlayerRecord target, source;
            if (request == null || !CommunicationId(request.RequestId) || request.TargetPlayerId == actor || request.TargetPlayerId == null
                || !World.Players.TryGetValue(request.TargetPlayerId, out target) || !target.Online
                || !World.Players.TryGetValue(actor, out source) || !source.Online
                || OwnerOfEntity(request.OwnFleetId) != actor || OwnerOfEntity(request.TargetFleetId) != target.PlayerId
                || GetRelation(actor, target.PlayerId) != Relation.Hostile) throw new MessageRejectionException("攻击双方须在线、已敌对且舰队归属有效。");
            PlayerFleetBattleRecord prior;
            if (World.FleetBattles.TryGetValue(request.RequestId, out prior))
            {
                if (prior.AttackerPlayerId != actor || prior.DefenderPlayerId != target.PlayerId
                    || prior.AttackerFleetId != request.OwnFleetId || prior.DefenderFleetId != request.TargetFleetId)
                    throw new MessageRejectionException("拦截请求编号已被其他交战使用。");
                SendBattleState(prior); return;
            }
            if (World.FleetBattles.Values.Any(x => x.Stage != PlayerFleetBattleStage.Finished && (IsBattleParticipant(x, actor) || IsBattleParticipant(x, target.PlayerId)))
                || IsPlayerFleetBattleLocked(request.OwnFleetId) || IsPlayerFleetBattleLocked(request.TargetFleetId))
                throw new MessageRejectionException("参战玩家或舰队已有未结束战场。");
            Require(bridge.ValidatePlayerFleetContact(actor, request.OwnFleetId, target.PlayerId, request.TargetFleetId), "验证玩家舰队真实接触");
            var record = new PlayerFleetBattleRecord { BattleId = request.RequestId, NativeBattleUid = AllocateUid(-1), AuthorityEpoch = 1,
                AttackerPlayerId = actor, DefenderPlayerId = target.PlayerId, SimulatorPlayerId = actor,
                AttackerFleetId = request.OwnFleetId, DefenderFleetId = request.TargetFleetId,
                Stage = PlayerFleetBattleStage.AwaitingResponse, InterceptExpiresAtRoomSeconds = World.RoomRealSeconds + 45 };
            World.FleetBattles.Add(record.BattleId, record);
            try { SaveOrThrow(); }
            catch { World.FleetBattles.Remove(record.BattleId); battleReady.Remove(record.BattleId); battleQuiescent.Remove(record.BattleId); battleDeadlines.Remove(record.BattleId); throw; }
            SendBattleState(record);
        }
        private void SendBattleState(PlayerFleetBattleRecord record)
        {
            // Only participants enter battle. Others receive lock metadata with galaxy policy.
            foreach (string player in new BattleParticipantPair(record.AttackerPlayerId, record.DefenderPlayerId))
                TrySendCommunication(player, "battle.state", JsonData.Clone(record));
            BroadcastPolicy();
        }
        private void ReceiveBattleState(PlayerFleetBattleRecord record)
        {
            if (record == null || !CommunicationId(record.BattleId) || !IsBattleParticipant(record, LocalPlayerId)) throw new MessageRejectionException("舰战状态不属于本玩家。");
            PlayerFleetBattleRecord previous;
            if (World.FleetBattles.TryGetValue(record.BattleId, out previous) && (record.AuthorityEpoch < previous.AuthorityEpoch
                || record.AuthorityEpoch == previous.AuthorityEpoch && record.Revision < previous.Revision)) return;
            World.FleetBattles[record.BattleId] = record;
            if (record.Stage == PlayerFleetBattleStage.Finished)
            {
                Require(bridge.CancelPlayerFleetBattlePreparation(record), "关闭已取消或结束的原版战前配置");
                if (previous == null || previous.Stage != PlayerFleetBattleStage.AwaitingResponse)
                    Require(bridge.StopPlayerFleetBattle(record), "结束本机玩家舰战场景");
                localBattlePreparations.Remove(record.BattleId); pendingBattleFrames.Remove(record.BattleId);
            }
            else if (record.Stage == PlayerFleetBattleStage.Blocked) { Paused = true; Error("舰战结算未完成，保留战场与舰队锁：" + record.Error); }
            else if (record.Stage == PlayerFleetBattleStage.Active && record.Checkpoint != null)
            {
                LocalBattlePreparation local;
                if (localBattlePreparations.TryGetValue(record.BattleId, out local) && local.Began)
                    ApplyReplicaBattleFrame(record, record.Checkpoint, "恢复房主确认的完整舰战基准");
                DiscardBattleFramesThrough(record.BattleId, record.Revision);
            }
            battleResyncPending.Remove(record.BattleId);
            if (record.Stage == PlayerFleetBattleStage.Finished) TrimFinishedBattles();
        }
        private void HostBattleReady(string actor, PlayerFleetBattleReady ready)
        {
            PlayerFleetBattleRecord record;
            if (ready == null || !World.FleetBattles.TryGetValue(ready.BattleId ?? "", out record)
                || ready.AuthorityEpoch != record.AuthorityEpoch || !IsBattleParticipant(record, actor)) throw new MessageRejectionException("舰战准备回执已过期。");
            if (!ready.Success)
            {
                // Failure reports also belong to a live scene. Finished/blocked/committing
                // results are final for this report; duplicates must not restart the deadline.
                if (record.Stage == PlayerFleetBattleStage.Finished || record.Stage == PlayerFleetBattleStage.Blocked
                    || record.Stage == PlayerFleetBattleStage.Committing || record.Interrupted) return;
                if (record.Stage != PlayerFleetBattleStage.Preparing && record.Stage != PlayerFleetBattleStage.Active)
                    throw new MessageRejectionException("舰战失败回执的阶段已失效。");
                InterruptBattle(record, "参战端无法继续战斗。"); return;
            }
            if (record.Stage != PlayerFleetBattleStage.Preparing || !record.SceneAuthorized || !BattleConfigurationsComplete(record) || record.Interrupted)
                throw new MessageRejectionException("舰战准备回执已过期。");
            if (GetRelation(record.AttackerPlayerId, record.DefenderPlayerId) != Relation.Hostile)
            { EndNonHostileBattle(record); throw new MessageRejectionException("双方已不再敌对，舰战准备已取消。"); }
            if (World.Players[actor].CurrentMapId >= 0) throw new MessageRejectionException("参战玩家尚未确认离开地表。");
            if (actor == record.SimulatorPlayerId)
            {
                if (ready.Checkpoint == null) throw new InvalidOperationException("唯一模拟者的舰战初始检查点缺失。");
                AcceptBattleCheckpoint(actor, ready.Checkpoint, true);
            }
            battleReady[record.BattleId].Add(actor);
            if (battleReady[record.BattleId].Count != 2) return;
            record.Stage = PlayerFleetBattleStage.Active;
            try { ApplyReplicaBattleFrame(record, record.Checkpoint, "双方就绪后应用完整舰战初始基准"); }
            catch { return; } // The common failure boundary has stopped control and notified/interrupted the host.
            try { SaveOrThrow(); SendBattleState(record); }
            catch (Exception error) { BlockBattle(record, error); }
        }
        private void HostBattleQuiescent(string actor, PlayerFleetBattleReference reference)
        {
            PlayerFleetBattleRecord record;
            if (reference == null || !World.FleetBattles.TryGetValue(reference.BattleId ?? "", out record) || record.Stage != PlayerFleetBattleStage.Preparing
                || record.AuthorityEpoch != reference.AuthorityEpoch || !IsBattleParticipant(record, actor) || World.Players[actor].CurrentMapId >= 0)
                throw new MessageRejectionException("舰战离图确认已经失效。");
            if (record.PreparationAuthorized || record.SceneAuthorized) return;
            battleQuiescent[record.BattleId].Add(actor);
            if (battleQuiescent[record.BattleId].Count != 2) return;
            if (GetRelation(record.AttackerPlayerId, record.DefenderPlayerId) != Relation.Hostile)
            { EndNonHostileBattle(record); return; }
            try
            {
                // Both final map/personal deltas were ACKed before their reliable barrier.
                // Refresh the exact native fleets before either side constructs a battle scene.
                SaveOrThrow(); BroadcastPolicy();
                foreach (string player in new BattleParticipantPair(record.AttackerPlayerId, record.DefenderPlayerId))
                {
                    if (player == LocalPlayerId) continue;
                    string peer;
                    if (!playerPeers.TryGetValue(player, out peer) || !World.Players[player].Online) throw new InvalidOperationException("参战玩家在入场前已断线。");
                    bridge.ResetGalaxyRecipient(player);
                    SendAndConfirmGalaxy(peer, player);
                }
                record.PreparationAuthorized = true;
                record.PreparationExpiresAtRoomSeconds = World.RoomRealSeconds + FleetConfigurationSeconds;
                battleDeadlines.Remove(record.BattleId);
                SaveOrThrow(); SendBattleState(record);
            }
            catch (Exception error) { BlockBattle(record, error); }
        }
        public void SubmitBattleInput(PlayerFleetBattleInput input)
        {
            RequireCommunicationRoom();
            if (input == null) throw new ArgumentNullException(nameof(input));
            input = JsonData.Clone(input); input.PlayerId = LocalPlayerId; input.Sequence = ++nextBattleInput;
            if (input.BattleId != null && battleResyncPending.Contains(input.BattleId)) throw new InvalidOperationException("正在重新同步战场，请稍后再操作。");
            if (!CommunicationId(input.CommandId)) input.CommandId = Guid.NewGuid().ToString("N");
            if (IsHost) HostBattleInput(LocalPlayerId, input); else Send("host", "battle.input", input);
        }
        private void HostBattleInput(string actor, PlayerFleetBattleInput input)
        {
            PlayerFleetBattleRecord record;
            if (input == null || !CommunicationId(input.CommandId) || !World.FleetBattles.TryGetValue(input.BattleId ?? "", out record)
                || record.Stage != PlayerFleetBattleStage.Active || record.Interrupted || input.AuthorityEpoch != record.AuthorityEpoch
                || !IsBattleParticipant(record, actor) || closing || Paused || !World.Players[actor].Online)
                throw new MessageRejectionException("舰战输入已失效或战斗暂停。");
            if (GetRelation(record.AttackerPlayerId, record.DefenderPlayerId) != Relation.Hostile)
            { EndNonHostileBattle(record); throw new MessageRejectionException("双方已不再敌对，停止继续攻击。"); }
            long fleet = actor == record.AttackerPlayerId ? record.AttackerFleetId : record.DefenderFleetId;
            string shipOwner = OwnerOfEntity(input.ShipId);
            if (input.FleetId != fleet || OwnerOfEntity(fleet) != actor || input.ShipId < 0 || shipOwner != null && shipOwner != actor
                || input.NativeJson == null || input.NativeJson.Length > 16384
                || !BattleInputAllowedOperations.Contains(input.Operation))
                throw new MessageRejectionException("只能控制本战中本人真实舰队的舰船。");
            var key = new BattleInputSequenceKey(record.BattleId, actor, false); long sequence;
            if (input.Sequence <= 0 || battleInputSequences.TryGetValue(key, out sequence) && input.Sequence <= sequence) return;
            if (!ConsumeCommunicationRate(actor, "battle")) throw new MessageRejectionException("舰战输入过于频繁。");
            input.PlayerId = actor;
            Require(bridge.ValidatePlayerFleetBattleInputShip(record, input), "校验本人真实参战舰船");
            battleInputSequences[key] = input.Sequence;
            if (record.SimulatorPlayerId == LocalPlayerId) ExecuteAuthorizedBattleInput(record, input);
            else if (!TrySendCommunication(record.SimulatorPlayerId, "battle.execute", input)) InterruptBattle(record, "唯一模拟者连接已断开。");
        }
        private void ExecuteAuthorizedBattleInput(PlayerFleetBattleRecord record, PlayerFleetBattleInput input)
        {
            try { Require(bridge.ExecutePlayerFleetBattleInput(record, input), "唯一模拟者执行经房主验证的舰战输入"); }
            catch (BridgeOperationException error)
            {
                // A known stale/dead/invalid command is a normal rejection. Unknown native
                // partial writes stop this scene; the host retains its confirmed checkpoint.
                if (!error.NativeUnchanged)
                {
                    LocalBattlePreparation local;
                    if (localBattlePreparations.TryGetValue(record.BattleId, out local)) local.Failed = true;
                    ReportInteractionFault("battle.input-native", error);
                    if (IsHost) InterruptBattle(record, "舰战输入原生应用中断，按最近确认检查点收尾。");
                    else
                    {
                        record.Interrupted = true;
                        Send("host", "battle.ready", new PlayerFleetBattleReady { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch, Success = false, Error = "NativeInputFailure" });
                    }
                }
                throw;
            }
        }
        private void AcceptBattleCheckpoint(string actor, PlayerFleetBattleFrame frame, bool initial = false)
        {
            PlayerFleetBattleRecord record;
            if (frame == null || !World.FleetBattles.TryGetValue(frame.BattleId ?? "", out record) || actor != record.SimulatorPlayerId
                || frame.AuthorityEpoch != record.AuthorityEpoch) return;
            if (frame.Revision <= record.Revision)
            { TrySendCommunication(actor, "battle.ack", new PlayerFleetBattleAck { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch, Revision = frame.Revision }); return; }
            if (GetRelation(record.AttackerPlayerId, record.DefenderPlayerId) != Relation.Hostile)
            { EndNonHostileBattle(record); return; }
            if (!ConsumeCommunicationRate(actor, "battleFrame")) throw new MessageRejectionException("舰战检查点发送过于频繁。");
            if (frame.BaseRevision != record.Revision || frame.Revision != frame.BaseRevision + 1 || frame.NativeJson == null || frame.NativeJson.Length > BattleFrameMaxCharacters
                || record.Stage != PlayerFleetBattleStage.Active && !(initial && record.Stage == PlayerFleetBattleStage.Preparing
                    && record.SceneAuthorized && BattleConfigurationsComplete(record)))
                throw new MessageRejectionException("舰战检查点基准或阶段已经失效。");
            Require(bridge.ApplyPlayerFleetBattleFrame(record, frame), "验证并接收唯一模拟者舰战检查点");
            record.Checkpoint = JsonData.Clone(frame); record.Revision = frame.Revision;
            TrySendCommunication(actor, "battle.ack", new PlayerFleetBattleAck { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch, Revision = record.Revision });
            if (!initial) foreach (string peer in new BattleParticipantPair(record.AttackerPlayerId, record.DefenderPlayerId))
                if (peer != actor && peer != LocalPlayerId) TrySendCommunication(peer, "battle.frame", frame);
            if (!initial && (frame.Ended || record.Interrupted && frame.FinalCheckpoint)) CommitBattle(record);
        }
        private void CommitBattle(PlayerFleetBattleRecord record)
        {
            if (record.Stage == PlayerFleetBattleStage.Finished || record.Stage == PlayerFleetBattleStage.Blocked) return;
            if (record.Stage == PlayerFleetBattleStage.AwaitingResponse)
            { FinishFleetIntercept(record, "本次舰队拦截已取消，双方可继续航行。"); return; }
            try
            {
                if (record.Checkpoint == null && !record.AssetsCommitted) throw new InvalidOperationException("活跃舰战缺少可恢复检查点，禁止无损解锁。");
                record.Stage = PlayerFleetBattleStage.Committing;
                if (!record.AssetsCommitted)
                {
                    ApplyCriticalNative(() => bridge.CommitPlayerFleetBattleResult(record, record.Checkpoint), "提交舰战真实舰船损伤与结果");
                    record.AssetsCommitted = true;
                }
                SaveOrThrow();
                record.Stage = PlayerFleetBattleStage.Finished;
                record.Checkpoint = null; record.Error = null;
                TrimFinishedBattles(); SaveOrThrow();
                Require(bridge.StopPlayerFleetBattle(record), "释放本机已保存的舰战场景");
                battleDeadlines.Remove(record.BattleId); battleReady.Remove(record.BattleId); SendBattleState(record);
            }
            catch (Exception error) { BlockBattle(record, error); }
        }
        private void BlockBattle(PlayerFleetBattleRecord record, Exception error)
        {
            record.Stage = PlayerFleetBattleStage.Blocked; record.Error = error.ToString(); Paused = true;
            ReportInteractionFault("battle.commit", new InvalidOperationException("battle=" + record.BattleId + " epoch=" + record.AuthorityEpoch + " revision=" + record.Revision + " fleets=" + record.AttackerFleetId + "/" + record.DefenderFleetId, error));
            Error("舰战结算或保存失败，保留现场与舰队锁：" + error); SendBattleState(record);
        }
        private void InterruptBattle(PlayerFleetBattleRecord record, string reason)
        {
            if (record.Stage == PlayerFleetBattleStage.Finished || record.Stage == PlayerFleetBattleStage.Blocked) return;
            if (record.Stage == PlayerFleetBattleStage.AwaitingResponse)
            { FinishFleetIntercept(record, reason); return; }
            record.Interrupted = true; record.Error = reason;
            if (record.Stage == PlayerFleetBattleStage.Preparing)
            {
                record.Stage = PlayerFleetBattleStage.Finished;
                record.Checkpoint = null;
                record.PreparationAuthorized = false; record.SceneAuthorized = false; record.PreparationExpiresAtRoomSeconds = 0;
                try { SaveOrThrow(); Require(bridge.CancelPlayerFleetBattlePreparation(record), "取消原版战前配置"); Require(bridge.StopPlayerFleetBattle(record), "取消尚未开始的舰战准备"); TrimFinishedBattles(); SendBattleState(record); }
                catch (Exception error) { BlockBattle(record, error); }
                return;
            }
            if (record.Stage == PlayerFleetBattleStage.Committing) { CommitBattle(record); return; }
            battleDeadlines[record.BattleId] = 2;
            SendBattleState(record);
            if (!TrySendCommunication(record.SimulatorPlayerId, "battle.stopRequest", new PlayerFleetBattleReference { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch })) CommitBattle(record);
        }
        private void StopNonHostileBattles()
        {
            if (!IsHost || World == null || World.FleetBattles == null) return;
            var active = battleTickRecords.Capture(World.FleetBattles, true);
            try
            {
                foreach (PlayerFleetBattleRecord battle in active)
                    if (GetRelation(battle.AttackerPlayerId, battle.DefenderPlayerId) != Relation.Hostile)
                        EndNonHostileBattle(battle);
            }
            finally { battleTickRecords.Release(active); }
        }
        private void EndNonHostileBattle(PlayerFleetBattleRecord battle)
        {
            if (battle.Stage == PlayerFleetBattleStage.AwaitingResponse || battle.Stage == PlayerFleetBattleStage.Preparing)
            { InterruptBattle(battle, "双方已不再敌对，取消本次拦截。"); return; }
            if (battle.Stage != PlayerFleetBattleStage.Active) return;
            battle.Interrupted = true; battle.Error = "双方已不再敌对，按最近确认检查点结束战斗。";
            SendBattleState(battle); CommitBattle(battle);
        }
        private void TrimFinishedBattles()
        {
            foreach (var ended in World.FleetBattles.Values.Where(x => x.Stage == PlayerFleetBattleStage.Finished).ToList())
            {
                ended.Checkpoint = null;
                ended.PreparationAuthorized = false; ended.SceneAuthorized = false; ended.PreparationExpiresAtRoomSeconds = 0;
                ended.AttackerConfiguration = null; ended.DefenderConfiguration = null;
                battleResyncPending.Remove(ended.BattleId);
                localBattlePreparations.Remove(ended.BattleId); pendingBattleFrames.Remove(ended.BattleId);
                battleReady.Remove(ended.BattleId); battleQuiescent.Remove(ended.BattleId); battleDeadlines.Remove(ended.BattleId);
                battleInputSequences.RemoveBattle(ended.BattleId);
            }
            var history = World.FleetBattles.Values.Where(x => x.Stage == PlayerFleetBattleStage.Finished).OrderByDescending(x => x.NativeBattleUid).Skip(64).ToList();
            foreach (var ended in history) World.FleetBattles.Remove(ended.BattleId);
        }
        private void ReceiveBattleFrame(PlayerFleetBattleFrame frame)
        {
            PlayerFleetBattleRecord record;
            if (frame == null || !World.FleetBattles.TryGetValue(frame.BattleId ?? "", out record) || frame.AuthorityEpoch != record.AuthorityEpoch || frame.Revision <= record.Revision
                || record.Stage != PlayerFleetBattleStage.Active && !(record.Stage == PlayerFleetBattleStage.Preparing && record.SceneAuthorized)) return;
            if (battleResyncPending.Contains(record.BattleId)) return;
            if (frame.BaseRevision != record.Revision)
            { battleResyncPending.Add(record.BattleId); Send("host", "battle.resync", new PlayerFleetBattleReference { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch }); return; }
            ApplyReplicaBattleFrame(record, frame, "显示房主确认的舰战帧"); record.Revision = frame.Revision; record.Checkpoint = JsonData.Clone(frame);
        }
        private void ApplyReplicaBattleFrame(PlayerFleetBattleRecord record, PlayerFleetBattleFrame frame, string operation)
        {
            try { Require(bridge.ApplyPlayerFleetBattleFrame(record, frame), operation); }
            catch (Exception error)
            {
                battleResyncPending.Add(record.BattleId);
                LocalBattlePreparation local;
                if (localBattlePreparations.TryGetValue(record.BattleId, out local)) local.Failed = true;
                ReportInteractionFault("battle.projection-native", error);
                Error("本机战场显示失败，正在按最近确认检查点结束：" + error);
                try
                {
                    if (IsHost) InterruptBattle(record, "本机战场无法完整显示，按最近确认检查点中断。");
                    else
                    {
                        record.Interrupted = true;
                        Send("host", "battle.ready", new PlayerFleetBattleReady { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch, Success = false, Error = "NativeProjectionFailure" });
                    }
                }
                catch (Exception notification) { throw new AggregateException("战场显示失败且中断通知未完整送出。", error, notification); }
                throw;
            }
        }
        private void ReceiveBattleAck(PlayerFleetBattleAck ack)
        {
            PlayerFleetBattleRecord record; BattleFrameWindow window; PlayerFleetBattleFrame pending;
            if (ack == null || !World.FleetBattles.TryGetValue(ack.BattleId ?? "", out record) || ack.AuthorityEpoch != record.AuthorityEpoch
                || !pendingBattleFrames.TryGetValue(record.BattleId, out window) || !window.Frames.TryGetValue(ack.Revision, out pending)) return;
            if (ack.Revision > record.Revision) { record.Revision = ack.Revision; record.Checkpoint = pending; }
            DiscardBattleFramesThrough(record.BattleId, ack.Revision);
        }
        private void DiscardBattleFramesThrough(string battleId, long revision)
        {
            BattleFrameWindow window;
            if (!pendingBattleFrames.TryGetValue(battleId, out window)) return;
            BattleFrameWindowPruner.DiscardThrough(window.Frames, revision,
                PendingBattleFrameLength, ref window.Characters);
            if (window.Frames.Count == 0) pendingBattleFrames.Remove(battleId);
        }
        private void ReceiveBattleStop(PlayerFleetBattleReference reference)
        {
            PlayerFleetBattleRecord record;
            if (reference != null && World.FleetBattles.TryGetValue(reference.BattleId ?? "", out record)
                && record.AuthorityEpoch == reference.AuthorityEpoch && record.SimulatorPlayerId == LocalPlayerId) record.Interrupted = true;
        }
        private bool HandleHostBattle(string peer, WireMessage message)
        {
            if (!message.Kind.StartsWith("battle.", StringComparison.Ordinal)) return false;
            string actor;
            if (!peers.TryGetValue(peer, out actor) || message.SessionId != SessionId) throw new MessageRejectionException("舰战所属连接或会话已过期。");
            switch (message.Kind)
            {
                case "battle.request": HostBattleRequest(actor, ReadCommunication<PlayerFleetBattleRequest>(message)); break;
                case "battle.respond": HostFleetInterceptResponse(actor, ReadCommunication<PlayerFleetInterceptResponse>(message)); break;
                case "battle.left": HostBattleQuiescent(actor, ReadCommunication<PlayerFleetBattleReference>(message)); break;
                case "battle.configuration": HostBattleConfiguration(actor, ReadCommunication<PlayerFleetBattleConfiguration>(message)); break;
                case "battle.cancel": HostCancelBattlePreparation(actor, ReadCommunication<PlayerFleetBattleReference>(message)); break;
                case "battle.leave":
                    var leave = ReadCommunication<PlayerFleetBattleReference>(message); PlayerFleetBattleRecord leaving;
                    if (leave == null || !World.FleetBattles.TryGetValue(leave.BattleId ?? "", out leaving) || leaving.AuthorityEpoch != leave.AuthorityEpoch || !IsBattleParticipant(leaving, actor))
                        throw new MessageRejectionException("舰战退出请求已失效。");
                    InterruptBattle(leaving, "参战玩家请求离开房间，收齐最后检查点后结束。"); break;
                case "battle.ready": HostBattleReady(actor, ReadCommunication<PlayerFleetBattleReady>(message)); break;
                case "battle.input": HostBattleInput(actor, ReadCommunication<PlayerFleetBattleInput>(message)); break;
                case "battle.frame":
                    try { AcceptBattleCheckpoint(actor, ReadCommunication<PlayerFleetBattleFrame>(message)); }
                    catch
                    {
                        foreach (var failed in World.FleetBattles.Values.Where(x => x.Stage == PlayerFleetBattleStage.Active && x.SimulatorPlayerId == actor).ToList())
                            InterruptBattle(failed, "唯一模拟者的舰战检查点未被接受，按最近确认检查点中断。");
                        throw;
                    }
                    break;
                case "battle.resync":
                    var reference = ReadCommunication<PlayerFleetBattleReference>(message); PlayerFleetBattleRecord current;
                    if (reference == null || !World.FleetBattles.TryGetValue(reference.BattleId ?? "", out current) || current.AuthorityEpoch != reference.AuthorityEpoch || !IsBattleParticipant(current, actor))
                        throw new MessageRejectionException("舰战重同步请求已经失效。");
                    if (!ConsumeCommunicationRate(actor, "battle")) throw new MessageRejectionException("舰战重同步请求过于频繁。");
                    TrySendCommunication(actor, "battle.state", JsonData.Clone(current)); break;
                default: throw new MessageRejectionException("不支持的舰战消息。");
            }
            return true;
        }
        private bool HandleClientBattle(WireMessage message)
        {
            switch (message.Kind)
            {
                case "battle.state": ReceiveBattleState(ReadCommunication<PlayerFleetBattleRecord>(message)); return true;
                case "battle.frame": ReceiveBattleFrame(ReadCommunication<PlayerFleetBattleFrame>(message)); return true;
                case "battle.ack": ReceiveBattleAck(ReadCommunication<PlayerFleetBattleAck>(message)); return true;
                case "battle.stopRequest": ReceiveBattleStop(ReadCommunication<PlayerFleetBattleReference>(message)); return true;
                case "battle.execute":
                    PlayerFleetBattleInput input = ReadCommunication<PlayerFleetBattleInput>(message);
                    PlayerFleetBattleRecord record;
                    if (input == null || !World.FleetBattles.TryGetValue(input.BattleId ?? "", out record) || record.SimulatorPlayerId != LocalPlayerId
                        || record.Stage != PlayerFleetBattleStage.Active || record.AuthorityEpoch != input.AuthorityEpoch) throw new MessageRejectionException("舰战控制输入任期已失效。");
                    var key = new BattleInputSequenceKey(input.BattleId, input.PlayerId, true); long prior;
                    if (battleInputSequences.TryGetValue(key, out prior) && input.Sequence <= prior) return true;
                    battleInputSequences[key] = input.Sequence;
                    ExecuteAuthorizedBattleInput(record, input); return true;
                default: return false;
            }
        }
        private void TickPlayerFleetBattles(double seconds)
        {
            if (World == null || World.FleetBattles == null || !IsConnected) return;
            // Finished battles remain in bounded room history, but have no
            // per-frame work unless the host still has an outstanding deadline.
            // Avoid allocating StopNonHostileBattles / main-loop snapshots on
            // every frame of a room with no active battle.
            if (!FleetBattleTickGate.HasWork(World.FleetBattles, IsHost, battleDeadlines.Count)) return;
            StopNonHostileBattles();
            var snapshot = battleTickRecords.Capture(World.FleetBattles, false);
            try
            {
                foreach (PlayerFleetBattleRecord record in snapshot)
                {
                // A pending interception is only a persisted lock and a notification.
                // It must never leave a surface, construct a scene, or settle assets.
                if (record.Stage == PlayerFleetBattleStage.AwaitingResponse)
                {
                    if (IsHost) TickFleetIntercept(record);
                    continue;
                }
                if (IsHost && battleDeadlines.ContainsKey(record.BattleId))
                {
                    if (!Paused) battleDeadlines[record.BattleId] -= seconds;
                    if (battleDeadlines[record.BattleId] <= 0)
                    { battleDeadlines.Remove(record.BattleId); if (record.Stage == PlayerFleetBattleStage.Preparing) InterruptBattle(record, "舰战准备超时。"); else if (record.Interrupted) CommitBattle(record); }
                }
                if (IsHost && record.Stage == PlayerFleetBattleStage.Preparing && record.PreparationAuthorized && !record.SceneAuthorized)
                    TickFleetConfiguration(record);
                if (!IsBattleParticipant(record, LocalPlayerId)
                    || record.Stage != PlayerFleetBattleStage.Preparing && record.Stage != PlayerFleetBattleStage.Active) continue;
                LocalBattlePreparation local;
                if (!localBattlePreparations.TryGetValue(record.BattleId, out local)) localBattlePreparations[record.BattleId] = local = new LocalBattlePreparation();
                if (local.Failed) continue;
                try
                {
                    if (!local.Began)
                    {
                        if (World.Players[LocalPlayerId].CurrentMapId >= 0)
                        {
                            if (!local.LeaveSubmitted) { local.LeaveSubmitted = true; CommandResult left = Submit(new GameCommand { Kind = CommandKind.LeaveMap }); if (!left.Success) throw new InvalidOperationException(left.Error); }
                            continue;
                        }
                        if (inFlightUpdates.Count != 0 || personalInFlight.Count != 0 || checkpointCommands.Count != 0) continue;
                        if (!local.FlushSubmitted) { PublishLocalMaps(true); local.FlushSubmitted = true; }
                        if (inFlightUpdates.Count != 0) continue;
                        if (!local.PersonalFlushed) { PublishPersonalUpdates(true); local.PersonalFlushed = true; }
                        if (personalInFlight.Count != 0) continue;
                        if (!local.Quiescent)
                        {
                            local.Quiescent = true;
                            var reference = new PlayerFleetBattleReference { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch };
                            if (IsHost) HostBattleQuiescent(LocalPlayerId, reference); else Send("host", "battle.left", reference);
                        }
                        if (!record.PreparationAuthorized || record.Interrupted
                            || record.Stage != PlayerFleetBattleStage.Preparing && record.Stage != PlayerFleetBattleStage.Active) continue;
                        // Native preparation is idempotent: keep it open until the
                        // host confirms our configuration, including rejected-send retries.
                        Require(bridge.BeginPlayerFleetBattlePreparation(record), "打开或等待本方原版舰队战前配置");
                        if (!record.SceneAuthorized || record.Interrupted
                            || record.Stage != PlayerFleetBattleStage.Preparing && record.Stage != PlayerFleetBattleStage.Active) continue;
                        if (!BattleConfigurationsComplete(record)) throw new InvalidOperationException("双方尚未确认原版舰队配置，禁止创建战场。");
                        Require(bridge.BeginPlayerFleetBattle(record), "打开本人真实舰队战场"); local.Began = true;
                    }
                    if (!local.Ready && bridge.IsPlayerFleetBattleReady(record))
                    {
                        local.Ready = true;
                        var ready = new PlayerFleetBattleReady { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch, Success = true,
                            Checkpoint = record.SimulatorPlayerId == LocalPlayerId ? bridge.CapturePlayerFleetBattle(record) : null };
                        if (IsHost) HostBattleReady(LocalPlayerId, ready); else Send("host", "battle.ready", ready);
                    }
                    if (!local.Ready || record.Stage != PlayerFleetBattleStage.Active || record.SimulatorPlayerId != LocalPlayerId) continue;
                    local.CaptureElapsed += seconds;
                    if (record.Interrupted && local.FinalCaptured) continue;
                    if (!record.Interrupted && local.CaptureElapsed < 0.1) continue;
                    BattleFrameWindow window;
                    pendingBattleFrames.TryGetValue(record.BattleId, out window);
                    // Full checkpoints are reliably ordered, so several can be in flight.
                    // Reserve one maximum-size frame before capture (which drains FX events).
                    if (window != null && (window.Frames.Count >= 12 || window.Characters > BattleFrameMaxCharacters)) continue;
                    local.CaptureElapsed = 0;
                    PlayerFleetBattleFrame frame = bridge.CapturePlayerFleetBattle(record);
                    if (frame == null) continue;
                    if (frame.NativeJson == null || frame.NativeJson.Length > BattleFrameMaxCharacters) throw new InvalidOperationException("舰战完整检查点超过有界传输容量。");
                    frame.FinalCheckpoint = record.Interrupted;
                    if (record.Interrupted) local.FinalCaptured = true;
                    if (IsHost) AcceptBattleCheckpoint(LocalPlayerId, frame);
                    else
                    {
                        if (window == null) pendingBattleFrames[record.BattleId] = window = new BattleFrameWindow();
                        frame.BaseRevision = window.Frames.Count == 0
                            ? record.Revision : BattleFrameRevisionMax.Highest(window.Frames);
                        frame.Revision = checked(frame.BaseRevision + 1);
                        window.Frames.Add(frame.Revision, frame); window.Characters += frame.NativeJson.Length;
                        Send("host", "battle.frame", frame);
                    }
                }
                catch (Exception error)
                {
                    local.Failed = true;
                    ReportInteractionFault("battle.native", error); Error("舰战无法继续：" + error);
                    if (IsHost) InterruptBattle(record, "舰战运行失败：" + error);
                    else Send("host", "battle.ready", new PlayerFleetBattleReady { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch, Success = false, Error = error.GetType().Name });
                }
                }
            }
            finally { battleTickRecords.Release(snapshot); }
        }
        private void DisconnectPlayerFleetBattles(string player, bool all)
        {
            if (all) battleTickRecords.Reset();
            if (World == null || World.FleetBattles == null) return;
            foreach (var record in World.FleetBattles.Values.Where(x => x.Stage != PlayerFleetBattleStage.Finished && (all || IsBattleParticipant(x, player))).ToList())
            {
                if (IsHost) InterruptBattle(record, "参战玩家离开或房间关闭，按最新检查点中断。");
                else if (IsBattleParticipant(record, LocalPlayerId))
                {
                    record.Interrupted = true;
                    if (transport.IsRunning) Send("host", "battle.leave", new PlayerFleetBattleReference { BattleId = record.BattleId, AuthorityEpoch = record.AuthorityEpoch });
                }
            }
        }
        private bool HasPendingLocalBattleExit()
        {
            if (pendingBattleFrames.Count != 0) return true;
            return World != null && World.FleetBattles != null && World.FleetBattles.Values.Any(x => x.Stage == PlayerFleetBattleStage.Active
                && x.SimulatorPlayerId == LocalPlayerId && localBattlePreparations.ContainsKey(x.BattleId) && !localBattlePreparations[x.BattleId].Failed && !localBattlePreparations[x.BattleId].FinalCaptured);
        }
        private void StopLocalPlayerFleetBattles()
        {
            if (World == null || World.FleetBattles == null) return;
            foreach (var record in World.FleetBattles.Values.Where(x => IsBattleParticipant(x, LocalPlayerId)
                && x.Stage != PlayerFleetBattleStage.AwaitingResponse).ToList())
            {
                Require(bridge.CancelPlayerFleetBattlePreparation(record), "离开房间时关闭原版战前配置");
                Require(bridge.StopPlayerFleetBattle(record), "离开房间时释放本机玩家舰战场景");
            }
        }
        private void CommitInterruptedPlayerBattles()
        {
            if (!IsHost || World == null || World.FleetBattles == null) return;
            foreach (var record in World.FleetBattles.Values.Where(x => x.Stage == PlayerFleetBattleStage.Active || x.Stage == PlayerFleetBattleStage.Committing).ToList())
            { record.Interrupted = true; CommitBattle(record); }
        }
        private void RecoverPlayerFleetBattles()
        {
            if (World.FleetBattles == null) World.FleetBattles = new Dictionary<string, PlayerFleetBattleRecord>();
            foreach (var record in World.FleetBattles.Values.Where(x => x.Stage != PlayerFleetBattleStage.Finished).ToList())
            {
                record.Interrupted = true;
                if (record.Stage == PlayerFleetBattleStage.AwaitingResponse)
                {
                    record.Error = "房间已重新载入，本次拦截已取消，双方可继续航行。";
                    record.Checkpoint = null; record.SceneAuthorized = false; record.InterceptExpiresAtRoomSeconds = 0;
                    record.Stage = PlayerFleetBattleStage.Finished;
                }
                // Even an initial scene checkpoint is only a deployment baseline
                // until both participants are ready. Recovery must not settle it
                // as combat damage or trigger native crew-survival processing.
                else if (record.AssetsCommitted || record.Stage == PlayerFleetBattleStage.Preparing
                    || record.Stage == PlayerFleetBattleStage.Blocked && record.Checkpoint == null && !record.SceneAuthorized)
                {
                    record.Checkpoint = null;
                    record.Stage = PlayerFleetBattleStage.Finished;
                }
                else
                {
                    if (record.Checkpoint == null) throw new InvalidOperationException("旧舰战没有检查点，不能无损恢复并解锁。");
                    Require(bridge.CommitPlayerFleetBattleResult(record, record.Checkpoint), "恢复旧任期舰战真实损伤"); record.AssetsCommitted = true; record.Stage = PlayerFleetBattleStage.Finished;
                }
                record.AuthorityEpoch++;
                record.PreparationAuthorized = false; record.SceneAuthorized = false; record.PreparationExpiresAtRoomSeconds = 0;
                record.AttackerConfiguration = null; record.DefenderConfiguration = null;
            }
            TrimFinishedBattles();
        }
    }
}
