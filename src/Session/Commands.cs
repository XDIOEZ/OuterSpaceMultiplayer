using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeWorldMultiplayer
{
    internal sealed class RelationRequest { public Relation Relation; }
    internal sealed class AttackReference { public string AttackId; }
    internal sealed class TransferReference { public string TransferId; }
    public sealed partial class MultiplayerSession
    {
        public CommandResult Submit(GameCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!IsConnected || World == null || closing) return CommandResult.Fail(command.CommandId, "房间未连接或正在关闭。");
            if (string.IsNullOrWhiteSpace(command.CommandId)) command.CommandId = Guid.NewGuid().ToString("N");
            command.PlayerId = LocalPlayerId; command.SessionId = SessionId;
            if (!IsHost && (IsCritical(command.Kind) || command.Kind == CommandKind.EnterMap || command.Kind == CommandKind.LeaveMap))
            {
                if (inFlightUpdates.Count != 0 || personalInFlight.Count != 0)
                {
                    if (!checkpointCommands.Any(c => c.CommandId == command.CommandId)) checkpointCommands.Enqueue(command);
                    return new CommandResult { CommandId = command.CommandId, Success = true, Error = "正在提交当前地图进度，然后执行该操作" };
                }
                PublishLocalMaps(); PublishPersonalUpdates();
            }
            MapRecord map;
            if (World.Maps.TryGetValue(command.MapId, out map)) { command.AuthorityEpoch = map.AuthorityEpoch; command.BaseRevision = map.Revision; }
            if (!IsHost) { Send("host", "command", command); return new CommandResult { CommandId = command.CommandId, Success = true, Error = "等待房主处理" }; }
            CommandResult result = ExecuteHostCommand(command);
            NotifyCommandResult(result); return result;
        }
        private static string TransactionKey(GameCommand command) { return command.PlayerId + ":" + command.CommandId; }
        private CommandResult ExecuteHostCommand(GameCommand command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > 128) return CommandResult.Fail(command?.CommandId, "无效命令编号。");
            if (nativeFault != null) return CommandResult.Fail(command.CommandId, "原生事务状态未完整确认，已暂停并禁止重试副作用；请重开最后完整存档：" + nativeFault);
            string key = TransactionKey(command);
            CommandResult previous;
            if (World.Transactions.TryGetValue(key, out previous))
            {
                if (previous.Applied && !previous.Saved && IsCritical(command.Kind))
                {
                    try { SaveOrThrow(); } catch (Exception ex) { return PendingSave(previous, ex); }
                }
                return JsonData.Clone(previous);
            }
            if (pendingExecutions.ContainsKey(key)) return new CommandResult { CommandId = command.CommandId, Success = true, Error = "命令已经排队，等待地图模拟者" };
            PlayerRecord actor;
            if (command.SessionId != SessionId || !World.Players.TryGetValue(command.PlayerId, out actor) || !actor.Online) return CommandResult.Fail(command.CommandId, "玩家或房间会话已经失效。");
            try
            {
                criticalNativeStarted = false;
                CommandResult result;
                switch (command.Kind)
                {
                    case CommandKind.FleetCommand:
                        if (Paused || closing || OwnerOfEntity(command.EntityId) != actor.PlayerId) throw new InvalidOperationException("只有在线玩家可在未暂停时控制自己的舰队。");
                        BridgeResult fleet = bridge.ExecuteFleetCommand(command);
                        Require(fleet, "房主执行玩家舰队航行意图");
                        result = CommandResult.Ok(command.CommandId, fleet.PayloadJson); break;
                    case CommandKind.EnterMap:
                        MapEntryRequest entry = JsonData.Read<MapEntryRequest>(command.PayloadJson ?? "{}");
                        if (entry.KnownMapId < 0 && command.MapId >= 0) entry.KnownMapId = command.MapId;
                        long canonicalMapId;
                        Require(bridge.ResolveMapEntry(entry, out canonicalMapId), "由房主解析唯一地块身份");
                        command.MapId = canonicalMapId;
                        if (entry.ResolveOnly) { EnsureMap(canonicalMapId); result = CommandResult.Ok(command.CommandId, JsonData.Write(bridge.CaptureMap(canonicalMapId))); }
                        else { EnterMap(actor, canonicalMapId); result = CommandResult.Ok(command.CommandId, JsonData.Write(new MapReference { MapId = canonicalMapId })); }
                        break;
                    case CommandKind.LeaveMap:
                        LeaveMap(actor); result = CommandResult.Ok(command.CommandId); break;
                    case CommandKind.ClaimMap:
                        MapRecord claim = EnsureMap(command.MapId);
                        if (claim.OwnerPlayerId != null && claim.OwnerPlayerId != actor.PlayerId) throw new InvalidOperationException("该地块已被其他玩家占领。");
                        ApplyCriticalNative(() => bridge.ClaimMap(command.MapId, actor.PlayerId), "通过原版离屏数据入口占领地块");
                        claim.OwnerPlayerId = actor.PlayerId;
                        RefreshCheckpoint(claim); BroadcastPolicy(); result = CommandResult.Ok(command.CommandId); break;
                    case CommandKind.SetBase:
                        MapRecord newBase = EnsureMap(command.MapId);
                        if (newBase.OwnerPlayerId != actor.PlayerId || newBase.MapId == actor.MotherShipMapId) throw new InvalidOperationException("只能将自己占领的地表设为正式基地。");
                        MapRecord oldBase;
                        if (World.Maps.TryGetValue(actor.FormalBaseMapId, out oldBase) && oldBase.Mode == MapMode.Offline) SettleOffline(oldBase);
                        ApplyCriticalNative(() => bridge.SetFormalBase(command.MapId, actor.PlayerId), "通过原版离屏数据入口设置正式基地");
                        actor.FormalBaseMapId = newBase.MapId;
                        if (oldBase != null && oldBase.MapId != newBase.MapId && oldBase.Mode == MapMode.Offline) oldBase.Mode = MapMode.Frozen;
                        if (newBase.SimulatorPlayerId == null && newBase.ActiveAttackId == null) { newBase.Mode = MapMode.Offline; newBase.LastSimulatedWorldMM = World.WorldTimeMM; }
                        BroadcastPolicy(); result = CommandResult.Ok(command.CommandId); break;
                    case CommandKind.Relation:
                        ChangeRelation(actor.PlayerId, command.TargetPlayerId, JsonData.Read<RelationRequest>(command.PayloadJson).Relation);
                        result = CommandResult.Ok(command.CommandId); break;
                    case CommandKind.DepartTransfer: result = Depart(command); break;
                    case CommandKind.ArriveTransfer: result = CompleteTransfer(command, false); break;
                    case CommandKind.CancelTransfer: result = CompleteTransfer(command, true); break;
                    case CommandKind.StartRaid: result = StartAttack(command); break;
                    case CommandKind.RaidReady: result = ReadyAttack(command); break;
                    case CommandKind.EvacuateRaid: result = EvacuateAttack(command); break;
                    case CommandKind.DamageReported:
                        MapRecord battlefield = EnsureMap(command.MapId);
                        if (battlefield.SimulatorPlayerId != actor.PlayerId || battlefield.AuthorityEpoch != command.AuthorityEpoch) throw new InvalidOperationException("只有当前权威模拟者可以报告实际伤害。");
                        string a = OwnerOfEntity(command.EntityId), b = OwnerOfEntity(command.TargetEntityId) ?? command.TargetPlayerId;
                        if (a != null && b != null && a != b && GetRelation(a, b) == Relation.Neutral) ChangeRelation(a, b, Relation.Hostile);
                        result = CommandResult.Ok(command.CommandId); break;
                    default: return RouteGameplayCommand(command);
                }
                result.PlayerId = command.PlayerId;
                World.Transactions[key] = result;
                if (result.Applied && IsCritical(command.Kind))
                {
                    try { SaveOrThrow(); }
                    catch (Exception ex) { return PendingSave(result, ex); }
                }
                return result;
            }
            catch (Exception ex)
            {
                if (criticalNativeStarted && IsCritical(command.Kind) && nativeFault == null) LockNativeFault(ex.Message);
                return CommandResult.Fail(command.CommandId, ex.Message);
            }
        }
        private static bool IsCritical(CommandKind kind)
        {
            return kind == CommandKind.ClaimMap || kind == CommandKind.SetBase || kind == CommandKind.DepartTransfer || kind == CommandKind.ArriveTransfer || kind == CommandKind.CancelTransfer || kind == CommandKind.StartRaid || kind == CommandKind.EvacuateRaid;
        }
        private CommandResult PendingSave(CommandResult applied, Exception error)
        {
            Error("关键事务已应用但保存失败，保留同一事务等待重试：" + error.Message);
            return new CommandResult { CommandId = applied.CommandId, Applied = true, Success = false, Saved = false, Error = "已应用、未保存；请重试同一请求或保存，禁止重复发放：" + error.Message };
        }
        private void ApplyCriticalNative(Func<BridgeResult> mutation, string operation)
        {
            criticalNativeStarted = true;
            try
            {
                BridgeResult result = mutation();
                if (result != null && !result.Success && result.NativeUnchanged)
                {
                    criticalNativeStarted = false;
                    throw new NativeRejectionException(operation + ": " + result.Error);
                }
                Require(result, operation);
            }
            catch (NativeRejectionException) { criticalNativeStarted = false; throw; }
            catch (Exception ex)
            {
                // Native adapters cannot promise rollback after a mid-operation exception. Never
                // retry a possibly applied debit, or serialize a partial transaction as valid state.
                LockNativeFault(operation + ": " + ex.Message);
                throw;
            }
        }
        private CommandResult RouteGameplayCommand(GameCommand command)
        {
            MapRecord map = EnsureMap(command.MapId);
            if (!map.Visitors.Contains(command.PlayerId)) throw new InvalidOperationException("玩家尚未进入该地图。");
            if (Paused || map.AwaitingAuthorityReady || map.SimulatorPlayerId == null) throw new InvalidOperationException("地图暂停或正在交接模拟权。");
            if (command.AuthorityEpoch != map.AuthorityEpoch || command.BaseRevision != map.Revision) throw new InvalidOperationException("命令的地图版本已过期，需使用房主最新状态。");
            if (command.EntityId >= 0 && OwnerOfEntity(command.EntityId) != command.PlayerId) throw new InvalidOperationException("不能征召或控制其他玩家的雇员。");
            Permission permission;
            switch (command.Kind)
            {
                case CommandKind.Move: case CommandKind.Conscript: permission = Permission.ControlOwnHero; break;
                case CommandKind.Attack: permission = Permission.Attack; break;
                case CommandKind.Work: permission = Permission.Work; break;
                case CommandKind.TakeItem:
                    permission = HasPermission(command.PlayerId, command.MapId, command.TargetEntityId, Permission.Loot) ? Permission.Loot : Permission.TakeInventory; break;
                default: permission = Permission.UseBuilding; break;
            }
            long permissionEntity = permission == Permission.ControlOwnHero ? command.EntityId : command.TargetEntityId;
            if (!HasPermission(command.PlayerId, command.MapId, permissionEntity, permission)) throw new InvalidOperationException("当前玩家关系或实体归属不允许该操作。");
            if (map.SimulatorPlayerId == LocalPlayerId)
            {
                BridgeResult native = bridge.ExecuteCommand(command);
                CommandResult result = native != null && native.Success ? CommandResult.Ok(command.CommandId, native.PayloadJson) : CommandResult.Fail(command.CommandId, native?.Error ?? "原生命令没有返回结果。");
                World.Transactions[TransactionKey(command)] = result;
                result.PlayerId = command.PlayerId;
                return result;
            }
            string peer;
            if (!playerPeers.TryGetValue(map.SimulatorPlayerId, out peer)) throw new InvalidOperationException("地图模拟者连接已经失效。");
            pendingExecutions[TransactionKey(command)] = JsonData.Clone(command);
            Send(peer, "execute", command);
            return new CommandResult { CommandId = command.CommandId, Success = true, Error = "命令已排队，尚未执行" };
        }
        private void ExecuteOnSimulator(GameCommand command)
        {
            MapRecord map;
            if (!World.Maps.TryGetValue(command.MapId, out map) || map.SimulatorPlayerId != LocalPlayerId || map.AuthorityEpoch != command.AuthorityEpoch || map.AwaitingAuthorityReady || Paused) throw new InvalidOperationException("当前客户端不是有效地图模拟者。");
            string key = TransactionKey(command);
            CommandResult result;
            if (!executedCommands.TryGetValue(key, out result))
            {
                BridgeResult native = bridge.ExecuteCommand(command);
                result = native != null && native.Success ? CommandResult.Ok(command.CommandId, native.PayloadJson) : CommandResult.Fail(command.CommandId, native?.Error ?? "原生命令没有返回结果。");
                result.PlayerId = command.PlayerId;
                executedCommands[key] = result;
            }
            Send("host", "execution", result);
            PublishLocalMaps();
        }
        private void AcceptExecution(string simulator, CommandResult result)
        {
            string key = result.PlayerId + ":" + result.CommandId;
            GameCommand pending;
            if (!pendingExecutions.TryGetValue(key, out pending) || !World.Maps.ContainsKey(pending.MapId) || World.Maps[pending.MapId].SimulatorPlayerId != simulator) throw new InvalidOperationException("命令执行回执不属于当前地图模拟者。");
            MapRecord map = World.Maps[pending.MapId];
            if (map.AuthorityEpoch != pending.AuthorityEpoch) throw new InvalidOperationException("旧任期的命令回执已失效。");
            pendingExecutions.Remove(key); World.Transactions[key] = result;
            string reply;
            if (commandReplyPeers.TryGetValue(key, out reply)) { Send(reply, "result", result); commandReplyPeers.Remove(key); }
            else NotifyCommandResult(result);
        }
        private RelationshipRecord FindRelationship(string a, string b, bool create)
        {
            if (string.CompareOrdinal(a, b) > 0) { string t = a; a = b; b = t; }
            RelationshipRecord pair = World.Relationships.FirstOrDefault(x => x.PlayerA == a && x.PlayerB == b);
            if (pair == null && create) { pair = new RelationshipRecord { PlayerA = a, PlayerB = b, Relation = Relation.Neutral }; World.Relationships.Add(pair); }
            return pair;
        }
        private void ChangeRelation(string actor, string target, Relation desired)
        {
            if (actor == target || !World.Players.ContainsKey(target)) throw new InvalidOperationException("无效的关系目标。");
            RelationshipRecord pair = FindRelationship(actor, target, true);
            if (desired == Relation.Friendly)
            {
                if (pair.PlayerA == actor) pair.FriendlyConsentA = true; else pair.FriendlyConsentB = true;
                if (pair.FriendlyConsentA && pair.FriendlyConsentB) pair.Relation = Relation.Friendly;
            }
            else
            {
                pair.Relation = desired; pair.FriendlyConsentA = false; pair.FriendlyConsentB = false;
            }
            Require(bridge.ApplyRelationship(pair), "变更玩家关系并释放失效任务占用");
            BroadcastPolicy();
        }
        public void ReportDamage(GameCommand command)
        {
            if (command == null) return;
            command.Kind = CommandKind.DamageReported;
            CommandResult result = Submit(command);
            if (!result.Success && !result.Applied) Error("实际伤害关系变更提交失败：" + result.Error);
        }
        private CommandResult Depart(GameCommand command)
        {
            TransferRequest request = JsonData.Read<TransferRequest>(command.PayloadJson);
            if (request == null) throw new InvalidDataException("Missing transfer manifest.");
            request.TransferId = string.IsNullOrWhiteSpace(request.TransferId) ? command.CommandId : request.TransferId;
            TransferRecord existing;
            if (World.Transfers.TryGetValue(request.TransferId, out existing))
            {
                if (existing.PlayerId != command.PlayerId) throw new InvalidOperationException("运输单属于其他玩家。");
                if (existing.Stage == TransferStage.Preparing || existing.Stage == TransferStage.Blocked) throw new InvalidOperationException("该运输原单处于未完成/中断状态，不能再次扣源：" + existing.Error);
                return CommandResult.Ok(command.CommandId, JsonData.Write(existing));
            }
            PlayerRecord player = World.Players[command.PlayerId];
            if (request.SourceFleetId >= 0 && request.SourceFleetId != player.FleetId) throw new InvalidOperationException("只能从自己的舰队运输。");
            foreach (long heroId in request.HeroIds) if (OwnerOfEntity(heroId) != command.PlayerId) throw new InvalidOperationException("不能带走别人的雇员。");
            if (request.SourceMapId >= 0 && OwnerOfMap(request.SourceMapId) != command.PlayerId && request.HeroIds.Count == 0) throw new InvalidOperationException("不能直接将他人库存装船运输。");
            if (request.SourceMapId >= 0 && OwnerOfMap(request.SourceMapId) != command.PlayerId)
            {
                Newtonsoft.Json.Linq.JObject manifest = Newtonsoft.Json.Linq.JObject.Parse(request.NativeManifestJson ?? "{}");
                foreach (var property in manifest.Properties())
                    if (string.Equals(property.Name, "Items", StringComparison.OrdinalIgnoreCase) && property.Value.HasValues) throw new InvalidOperationException("友好撤离只允许自己的雇员及其实际随身物品，不能混入基地库存清单。");
            }
            var record = new TransferRecord { TransferId = request.TransferId, PlayerId = command.PlayerId, Request = request, Stage = TransferStage.Preparing };
            World.Transfers.Add(record.TransferId, record);
            try
            {
                ApplyCriticalNative(() => bridge.DepartTransfer(record), "从来源移入唯一运输单");
                if (record.ArrivalWorldMM < 0) throw new InvalidOperationException("原生运输缺少房主权威到期时间。");
                record.Stage = TransferStage.InTransit;
            }
            catch (NativeRejectionException) { World.Transfers.Remove(record.TransferId); throw; }
            catch (Exception ex) { record.Stage = TransferStage.Blocked; record.Error = ex.Message; throw; }
            RebaseMap(request.SourceMapId); RebaseMap(request.DestinationMapId);
            BroadcastPolicy();
            return CommandResult.Ok(command.CommandId, JsonData.Write(record));
        }
        private CommandResult CompleteTransfer(GameCommand command, bool cancel)
        {
            string id = JsonData.Read<TransferReference>(command.PayloadJson).TransferId;
            TransferRecord record;
            if (!World.Transfers.TryGetValue(id, out record) || record.PlayerId != command.PlayerId) throw new InvalidOperationException("运输单不存在或不属于当前玩家。");
            if (record.Stage == TransferStage.Preparing || record.Stage == TransferStage.Blocked) throw new InvalidOperationException("运输原单中断，不能再次放置/扣除资产：" + record.Error);
            if (cancel && record.Stage == TransferStage.Landing) throw new InvalidOperationException("运输已经开始原生落仓，不能另发返航重复交付。");
            if (record.Stage != TransferStage.InTransit) return CommandResult.Ok(command.CommandId, JsonData.Write(record));
            if (cancel && record.Returning) return CommandResult.Ok(command.CommandId, JsonData.Write(record));
            if (!cancel && (record.ArrivalWorldMM < 0 || World.WorldTimeMM < record.ArrivalWorldMM)) throw new InvalidOperationException("原生运输航程尚未到期，不能提前抵达。");
            try
            {
                if (cancel) ApplyCriticalNative(() => bridge.CancelTransfer(record), "按原运输单退回资产");
                else ApplyCriticalNative(() => bridge.ArriveTransfer(record), "运输资产抵达目标");
            }
            catch (NativeRejectionException) { throw; }
            catch (Exception ex) { record.Stage = TransferStage.Blocked; record.Error = ex.Message; throw; }
            record.Stage = cancel ? TransferStage.InTransit : (bridge.IsTransferComplete(record) ? (record.Returning ? TransferStage.Cancelled : TransferStage.Arrived) : TransferStage.Landing);
            RebaseMap(record.Request.SourceMapId); RebaseMap(record.Request.DestinationMapId);
            BroadcastPolicy();
            return CommandResult.Ok(command.CommandId, JsonData.Write(record));
        }
        private CommandResult StartAttack(GameCommand command)
        {
            RaidRequest request = JsonData.Read<RaidRequest>(command.PayloadJson);
            if (request.DropPodId <= 0)
            {
                // This is a host reservation identity, not a fictional native vehicle-instance UID.
                request.DropPodId = checked(++World.UidHighWater);
            }
            MapRecord map = EnsureMap(request.MapId);
            PlayerRecord defender;
            if (map.OwnerPlayerId == null || !World.Players.TryGetValue(map.OwnerPlayerId, out defender) || defender.PlayerId == command.PlayerId) throw new InvalidOperationException("目标不是其他玩家占领地块。");
            if (map.MapId == defender.MotherShipMapId) throw new InvalidOperationException("V1 只袭击地表占领地块，不支持母舰袭击。");
            if (defender.Online) throw new InvalidOperationException("此入口只用于离线地块袭击；在线战斗使用原版地表攻击。");
            if (GetRelation(command.PlayerId, defender.PlayerId) != Relation.Hostile) throw new InvalidOperationException("只有敌对玩家可开启离线袭击。");
            if (map.ActiveAttackId != null || map.RaidCooldownSeconds > 0) throw new InvalidOperationException("该地块已有袭击或仍在所有进攻方共用的冷却中。");
            foreach (long hero in request.AttackerHeroIds) if (OwnerOfEntity(hero) != command.PlayerId) throw new InvalidOperationException("出征雇员不属于进攻玩家。");
            if (map.Mode == MapMode.Offline) SettleOffline(map);
            RaidStartData data;
            Require(bridge.InspectRaid(request, command.PlayerId, out data), "核对实际出征空投仓与守军");
            if (data == null || data.Capacity <= 0 || double.IsNaN(data.Capacity) || double.IsInfinity(data.Capacity)) throw new InvalidOperationException("实际出征空投仓容量无效。");
            var attack = new AttackRecord { AttackId = command.CommandId, MapId = map.MapId, AttackerPlayerId = command.PlayerId, DefenderPlayerId = defender.PlayerId, RegisteredMapRevision = map.Revision, DropPodId = request.DropPodId, SourceFleetId = request.SourceFleetId, NativeTransportJson = request.NativeTransportJson, Capacity = data.Capacity, Stage = AttackStage.Loading, AttackerHeroIds = request.AttackerHeroIds, ProtectedDefenderHeroIds = data.ProtectedDefenderHeroIds };
            MapMode previousMode = map.Mode;
            World.Attacks.Add(attack.AttackId, attack); map.ActiveAttackId = attack.AttackId; map.Mode = MapMode.Battle;
            try { ApplyCriticalNative(() => bridge.BeginRaid(attack), "进入同一原版真实战场"); }
            catch (NativeRejectionException) { World.Attacks.Remove(attack.AttackId); map.ActiveAttackId = null; map.Mode = previousMode; throw; }
            catch (Exception ex) { attack.Blocked = true; attack.Error = ex.Message; throw; }
            EnterMap(World.Players[command.PlayerId], map.MapId);
            RefreshCheckpoint(map); ReassignMap(map, command.PlayerId);
            BroadcastPolicy();
            return CommandResult.Ok(command.CommandId, JsonData.Write(attack));
        }
        private CommandResult ReadyAttack(GameCommand command)
        {
            string id = JsonData.Read<AttackReference>(command.PayloadJson).AttackId;
            AttackRecord attack;
            if (!World.Attacks.TryGetValue(id, out attack) || attack.AttackerPlayerId != command.PlayerId || attack.Stage == AttackStage.Finished) throw new InvalidOperationException("袭击登记无效。");
            MapRecord map = World.Maps[attack.MapId];
            if (map.AwaitingAuthorityReady || map.SimulatorPlayerId == null) throw new InvalidOperationException("战场尚未完成地图同步。");
            if (!bridge.IsRaidReady(attack)) throw new InvalidOperationException("原生空投尚未真实落仓完成。");
            if (attack.Stage == AttackStage.Loading) attack.Stage = AttackStage.Active;
            BroadcastPolicy(); return CommandResult.Ok(command.CommandId, JsonData.Write(attack));
        }
        private CommandResult EvacuateAttack(GameCommand command)
        {
            RaidEvacuation request = JsonData.Read<RaidEvacuation>(command.PayloadJson);
            AttackRecord attack;
            if (!World.Attacks.TryGetValue(request.AttackId, out attack) || attack.AttackerPlayerId != command.PlayerId) throw new InvalidOperationException("袭击不属于该进攻玩家。");
            if (attack.Stage != AttackStage.Active || attack.EvacuationUsed || attack.ElapsedSeconds >= 600 || Paused) throw new InvalidOperationException("袭击已结束、暂停或唯一撤离已经用过。");
            attack.DefendersDefeated = bridge.AreDefendersDefeated(attack);
            // Empty early evacuation is legal. The native adapter checks the actual manifest:
            // carrying defender-owned loot still requires the defenders to have been defeated.
            if (request.CapacityUsed < 0 || double.IsNaN(request.CapacityUsed) || double.IsInfinity(request.CapacityUsed) || request.CapacityUsed > attack.Capacity) throw new InvalidOperationException("战利品超过开战锁定的一个空投仓容量。");
            request.TransferId = string.IsNullOrWhiteSpace(request.TransferId) ? command.CommandId : request.TransferId;
            if (World.Transfers.ContainsKey(request.TransferId)) throw new InvalidOperationException("本场撤离运输编号已经使用。");
            request.Transfer = null; // The client cannot supply the authoritative carrier returned by the bridge.
            attack.EvacuationUsed = true;
            attack.EvacuationTransferId = request.TransferId;
            try { ApplyCriticalNative(() => bridge.EvacuateRaid(attack, request), "按唯一原版空投仓确认期限内撤离"); }
            catch (NativeRejectionException) { attack.EvacuationUsed = false; attack.EvacuationTransferId = null; throw; }
            catch (Exception ex) { attack.Blocked = true; attack.Error = ex.Message; throw; }
            if (request.Transfer != null)
            {
                if (request.Transfer.TransferId != request.TransferId || request.Transfer.PlayerId != attack.AttackerPlayerId
                    || request.Transfer.NativeExpeditionId < 0 || request.Transfer.ArrivalWorldMM < 0)
                    throw new InvalidOperationException("原生撤离未返回可持久化的唯一返航载体。");
                request.Transfer.Stage = TransferStage.InTransit;
                World.Transfers.Add(request.Transfer.TransferId, request.Transfer);
            }
            FinishAttack(attack, RaidFinishReason.Evacuated);
            return CommandResult.Ok(command.CommandId, JsonData.Write(attack));
        }
        private void AdvanceAttackTimers(double seconds)
        {
            bool activated = false;
            foreach (AttackRecord loading in World.Attacks.Values.Where(x => x.Stage == AttackStage.Loading && !x.Blocked).ToList())
            {
                MapRecord map = World.Maps[loading.MapId];
                if (!map.AwaitingAuthorityReady && map.SimulatorPlayerId != null && bridge.IsRaidReady(loading)) { loading.Stage = AttackStage.Active; activated = true; }
            }
            if (activated) BroadcastPolicy();
            foreach (AttackRecord attack in World.Attacks.Values.Where(x => x.Stage == AttackStage.Active).ToList())
            {
                attack.ElapsedSeconds = Math.Min(600, attack.ElapsedSeconds + seconds);
                attack.DefendersDefeated = bridge.AreDefendersDefeated(attack);
                if (attack.ElapsedSeconds >= 600)
                {
                    FinishAttack(attack, RaidFinishReason.Timeout);
                    string key = "finish:" + attack.AttackId;
                    World.Transactions[key] = CommandResult.Ok(key);
                    try { SaveOrThrow(); }
                    catch (Exception ex) { PendingSave(World.Transactions[key], ex); }
                }
            }
        }
        private void AdvanceTransfers()
        {
            foreach (TransferRecord transfer in World.Transfers.Values.Where(x => (x.Stage == TransferStage.InTransit && x.ArrivalWorldMM >= 0 && World.WorldTimeMM >= x.ArrivalWorldMM) || x.Stage == TransferStage.Landing).ToList())
            {
                try
                {
                    if (transfer.Stage == TransferStage.Landing && !bridge.IsTransferComplete(transfer)) continue;
                    if (transfer.Stage == TransferStage.InTransit) ApplyCriticalNative(() => bridge.ArriveTransfer(transfer), "房主按原生航程到期交付唯一运输单");
                    transfer.Stage = bridge.IsTransferComplete(transfer) ? (transfer.Returning ? TransferStage.Cancelled : TransferStage.Arrived) : TransferStage.Landing;
                    RebaseMap(transfer.Request.SourceMapId); RebaseMap(transfer.Request.DestinationMapId);
                    BroadcastPolicy();
                    string id = "arrival:" + transfer.TransferId;
                    World.Transactions[id] = CommandResult.Ok(id);
                    try { SaveOrThrow(); } catch (Exception ex) { PendingSave(World.Transactions[id], ex); }
                }
                catch (Exception ex) { transfer.Stage = TransferStage.Blocked; transfer.Error = ex.Message; throw; }
            }
        }
        private void FinishAttack(AttackRecord attack, RaidFinishReason reason)
        {
            if (attack.Stage == AttackStage.Finished) return;
            try { ApplyCriticalNative(() => bridge.FinishRaid(attack, reason), "结束袭击、留还战利品并空手撤回存活进攻者"); }
            catch (Exception ex) { attack.Blocked = true; attack.Error = ex.Message; throw; }
            attack.Stage = AttackStage.Finished; attack.FinishReason = reason;
            MapRecord map = World.Maps[attack.MapId];
            map.ActiveAttackId = null; map.RaidCooldownSeconds = 3600;
            RefreshCheckpoint(map); ReassignMap(map);
            BroadcastPolicy();
        }
        public bool IsProtectedDefender(long heroId)
        {
            return World != null && World.Attacks.Values.Any(x => x.Stage != AttackStage.Finished && x.ProtectedDefenderHeroIds.Contains(heroId));
        }
        public bool IsAttackAllowed(long mapId)
        {
            if (Paused || closing || World == null) return false;
            MapRecord map;
            if (!World.Maps.TryGetValue(mapId, out map)) return false;
            if (map.ActiveAttackId == null)
            {
                PlayerRecord owner;
                // Visiting or landing remains legal. Hostile activity against an offline occupied
                // base must be registered first, so its defenders and persisted deadline are protected.
                if (map.OwnerPlayerId != null && World.Players.TryGetValue(map.OwnerPlayerId, out owner) && !owner.Online) return false;
                return map.Mode == MapMode.Realtime;
            }
            AttackRecord attack;
            return World.Attacks.TryGetValue(map.ActiveAttackId, out attack) && attack.Stage == AttackStage.Active && attack.ElapsedSeconds < 600;
        }
    }
}
