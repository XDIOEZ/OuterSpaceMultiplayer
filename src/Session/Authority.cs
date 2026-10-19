using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private const long UidBlockSize = 1000000;
        private static PlayerRecord NewPlayer(string id, string credentialId, string verifier, string nickname)
        {
            return new PlayerRecord { PlayerId = id, CredentialId = credentialId, CredentialHash = verifier, Nickname = string.IsNullOrWhiteSpace(nickname) ? "玩家" : nickname.Substring(0, Math.Min(64, nickname.Length)) };
        }
        private void ApplyPlayerRecord(PlayerRecord record, PlayerSnapshot snapshot)
        {
            if (snapshot == null || snapshot.PlayerId != record.PlayerId) throw new InvalidDataException("Native player snapshot has the wrong owner.");
            record.Snapshot = snapshot; record.MotherShipMapId = snapshot.MotherShipMapId; record.FleetId = snapshot.FleetId;
            if (record.FleetId >= 0) RegisterOwner(record.FleetId, record.PlayerId);
            foreach (long uid in snapshot.HeroIds) RegisterOwner(uid, record.PlayerId);
            foreach (long uid in snapshot.OwnedEntityIds) RegisterOwner(uid, record.PlayerId);
        }
        private UidRange ReserveUidRange(bool persist)
        {
            long first = checked(World.UidHighWater + 1);
            World.UidHighWater = checked(World.UidHighWater + UidBlockSize);
            var range = new UidRange { First = first, Last = World.UidHighWater, Next = first, SessionId = SessionId };
            if (persist) SaveOrThrow();
            return range;
        }
        public long AllocateUid(long mapId)
        {
            PlayerRecord player;
            if (World == null || LocalPlayerId == null || !World.Players.TryGetValue(LocalPlayerId, out player)) throw new InvalidOperationException("No multiplayer player context for UID allocation.");
            if (player.Uids == null || player.Uids.SessionId != SessionId) throw new InvalidOperationException("旧会话 UID 号段不可继续使用。");
            if (player.Uids.Next > player.Uids.Last)
            {
                if (!IsHost) { RequestUidRenewal(player); throw new InvalidOperationException("房主 UID 号段已耗尽，等待新号段后重试该操作。"); }
                player.Uids = ReserveUidRange(false); player.IssuedUidRanges.Add(player.Uids);
                // If serialization itself requested a UID, the current generation will persist
                // this high-water mark. A nested save here would recursively capture the same world.
                if (!saveInProgress) SaveOrThrow();
            }
            long uid = player.Uids.Next++;
            if (!IsHost && player.Uids.Last - player.Uids.Next <= 1000) RequestUidRenewal(player);
            return uid;
        }
        private void RegisterOwner(long uid, string playerId)
        {
            if (uid < 0 || !World.Players.ContainsKey(playerId)) throw new InvalidOperationException("Invalid owned entity.");
            string existing;
            if (World.EntityOwners.TryGetValue(uid, out existing) && existing != playerId) throw new InvalidOperationException("实体 UID 已属于另一玩家：" + uid);
            World.EntityOwners[uid] = playerId;
        }
        public void RegisterEntityOwner(long uid, string playerId)
        {
            if (applyingOwners != null)
            {
                string staged;
                if (applyingOwners.TryGetValue(uid, out staged) && staged == playerId) return;
                if (World.EntityOwners.TryGetValue(uid, out staged) && staged == playerId) return;
                throw new InvalidOperationException("地图事务试图登记未经验证的新实体归属。");
            }
            RegisterOwner(uid, playerId);
            if (!IsHost && IsConnected)
            {
                long mapId = World.Players[LocalPlayerId].CurrentMapId;
                MapRecord map;
                if (!World.Maps.TryGetValue(mapId, out map)) throw new InvalidOperationException("新实体登记缺少当前地图任期。");
                if (map.SimulatorPlayerId != LocalPlayerId || map.AwaitingAuthorityReady) return; // Replica registration is local application of host-owned data.
                Send("host", "owner", new OwnerRegistration { EntityId = uid, PlayerId = playerId, MapId = mapId, AuthorityEpoch = map.AuthorityEpoch });
            }
        }
        public string OwnerOfEntity(long uid)
        {
            string owner;
            if (applyingOwners != null && applyingOwners.TryGetValue(uid, out owner)) return owner;
            return World != null && World.EntityOwners.TryGetValue(uid, out owner) ? owner : null;
        }
        public string OwnerOfMap(long mapId)
        {
            MapRecord map;
            return World != null && World.Maps.TryGetValue(mapId, out map) ? map.OwnerPlayerId : null;
        }
        public Relation GetRelation(string a, string b)
        {
            if (a == b && a != null) return Relation.Friendly;
            if (World == null || a == null || b == null) return Relation.Neutral;
            RelationshipRecord pair = FindRelationship(a, b, false);
            return pair == null ? Relation.Neutral : pair.Relation;
        }
        public bool HasPermission(string playerId, long mapId, long entityId, Permission permission)
        {
            if (World == null || playerId == null || !World.Players.ContainsKey(playerId)) return false;
            string entityOwner = OwnerOfEntity(entityId), mapOwner = OwnerOfMap(mapId);
            if (permission == Permission.View) return true;
            if (permission == Permission.ControlOwnHero) return entityOwner == playerId;
            if (permission == Permission.Attack)
            {
                if (!IsAttackAllowed(mapId)) return false;
                string targetOwner = entityOwner;
                return targetOwner != playerId && (targetOwner == null || GetRelation(playerId, targetOwner) != Relation.Friendly);
            }
            MapRecord map;
            if (permission == Permission.Loot)
            {
                AttackRecord attack;
                return World.Maps.TryGetValue(mapId, out map) && map.ActiveAttackId != null && World.Attacks.TryGetValue(map.ActiveAttackId, out attack) && attack.Stage == AttackStage.Active && attack.AttackerPlayerId == playerId && attack.DefendersDefeated && !attack.EvacuationUsed && attack.ElapsedSeconds < 600 && !Paused;
            }
            if (mapOwner == playerId) return true;
            if (permission == Permission.Transfer) return entityOwner == playerId; // Personal heroes/carry gifts only; never direct foreign stock-to-ship.
            return mapOwner != null && GetRelation(playerId, mapOwner) == Relation.Friendly && (permission == Permission.Work || permission == Permission.UseBuilding || permission == Permission.TakeInventory);
        }
        public bool CanSimulateMap(long mapId)
        {
            MapRecord map;
            return IsConnected && !closing && !Paused && World != null && World.Maps.TryGetValue(mapId, out map) && map.SimulatorPlayerId == LocalPlayerId && !map.AwaitingAuthorityReady && (map.Mode == MapMode.Realtime || (map.Mode == MapMode.Battle && map.ActiveAttackId != null && World.Attacks.ContainsKey(map.ActiveAttackId) && World.Attacks[map.ActiveAttackId].Stage != AttackStage.Finished && !World.Attacks[map.ActiveAttackId].Blocked));
        }
        public MapAccess GetMapAccess(long mapId)
        {
            MapRecord map;
            if (World == null || !World.Maps.TryGetValue(mapId, out map)) return new MapAccess { MapId = mapId, Access = MapAccessMode.Frozen };
            return AccessFor(map, LocalPlayerId);
        }
        private MapAccess AccessFor(MapRecord map, string playerId)
        {
            return new MapAccess { MapId = map.MapId, AuthorityEpoch = map.AuthorityEpoch, Revision = map.Revision, SimulatorPlayerId = map.SimulatorPlayerId, Access = map.SimulatorPlayerId == playerId ? MapAccessMode.Simulator : MapAccessMode.Replica };
        }
        private MapRecord EnsureMap(long mapId)
        {
            if (mapId < 0) throw new InvalidOperationException("Invalid map identifier.");
            MapRecord map;
            if (World.Maps.TryGetValue(mapId, out map)) return map;
            MapSnapshot snapshot = bridge.CaptureMap(mapId);
            if (snapshot == null || snapshot.MapId != mapId) throw new InvalidOperationException("地块不存在或不能读取：" + mapId);
            map = new MapRecord { MapId = mapId, Checkpoint = snapshot, Revision = snapshot.Revision, Mode = MapMode.Frozen, LastSimulatedWorldMM = World.WorldTimeMM };
            World.Maps.Add(mapId, map); return map;
        }
        private void RegisterMotherShip(PlayerRecord player)
        {
            if (player.MotherShipMapId < 0) throw new InvalidDataException("Player mother ship map was not created.");
            MapRecord map = EnsureMap(player.MotherShipMapId);
            if (map.OwnerPlayerId != null && map.OwnerPlayerId != player.PlayerId) throw new InvalidOperationException("New mother ship map is already owned.");
            map.OwnerPlayerId = player.PlayerId;
        }
        private bool IsFormalBase(MapRecord map)
        {
            PlayerRecord owner;
            return map.OwnerPlayerId != null && World.Players.TryGetValue(map.OwnerPlayerId, out owner) && owner.FormalBaseMapId == map.MapId;
        }
        private void SettleOffline(MapRecord map)
        {
            if (map.Mode != MapMode.Offline || map.ActiveAttackId != null || !IsFormalBase(map)) return;
            if (World.WorldTimeMM <= map.LastSimulatedWorldMM) return;
            try
            {
                ApplyCriticalNative(() => bridge.SimulateOffline(map.MapId, map.LastSimulatedWorldMM, World.WorldTimeMM), "结算正式基地托管");
                map.LastSimulatedWorldMM = World.WorldTimeMM;
                RefreshCheckpoint(map);
            }
            catch (Exception ex) { if (nativeFault == null) LockNativeFault(ex.Message); throw; }
        }
        private void RefreshCheckpoint(MapRecord map)
        {
            MapSnapshot snapshot = bridge.CaptureMap(map.MapId);
            if (snapshot == null) throw new InvalidOperationException("Cannot capture map after transaction: " + map.MapId);
            map.Revision++; snapshot.Revision = map.Revision; map.Checkpoint = snapshot;
            foreach (var owner in snapshot.EntityOwners) RegisterOwner(owner.Key, owner.Value);
        }
        private void EnterMap(PlayerRecord player, long mapId)
        {
            MapRecord target = EnsureMap(mapId);
            if (player.CurrentMapId >= 0 && player.CurrentMapId != mapId) LeaveMap(player);
            if (target.Mode == MapMode.Offline) SettleOffline(target);
            player.CurrentMapId = mapId;
            if (!target.Visitors.Contains(player.PlayerId)) target.Visitors.Add(player.PlayerId);
            if (target.SimulatorPlayerId == null) ReassignMap(target);
            else SendMapView(target, player.PlayerId);
            BroadcastPolicy();
        }
        private void LeaveMap(PlayerRecord player)
        {
            MapRecord map;
            if (World.Maps.TryGetValue(player.CurrentMapId, out map))
            {
                if (map.SimulatorPlayerId == LocalPlayerId && player.PlayerId == LocalPlayerId) RefreshCheckpoint(map);
                map.Visitors.Remove(player.PlayerId);
                if (map.SimulatorPlayerId == player.PlayerId) ReassignMap(map);
            }
            player.CurrentMapId = -1; BroadcastPolicy();
        }
        private void ReassignMap(MapRecord map, string preferred = null)
        {
            if (map.Mode == MapMode.Offline) SettleOffline(map);
            map.AuthorityEpoch++; map.AwaitingAuthorityReady = false; map.SimulatorPlayerId = null;
            var candidates = map.Visitors.Where(x => World.Players.ContainsKey(x) && World.Players[x].Online).ToList();
            string next = preferred != null && candidates.Contains(preferred) ? preferred : (candidates.Contains(map.OwnerPlayerId) ? map.OwnerPlayerId : candidates.FirstOrDefault());
            map.LastSimulatedWorldMM = World.WorldTimeMM;
            if (next == null)
            {
                if (map.ActiveAttackId != null) map.Mode = MapMode.Battle;
                else map.Mode = IsFormalBase(map) ? MapMode.Offline : MapMode.Frozen;
                Require(bridge.SetMapAuthority(new MapAccess { MapId = map.MapId, AuthorityEpoch = map.AuthorityEpoch, Revision = map.Revision, Access = MapAccessMode.Frozen }), "撤销地图模拟权");
                BroadcastPolicy(); return;
            }
            map.SimulatorPlayerId = next; map.AwaitingAuthorityReady = true;
            map.Mode = map.ActiveAttackId != null ? MapMode.Battle : MapMode.Realtime;
            Require(bridge.SetMapAuthority(AccessFor(map, LocalPlayerId)), "同步房主离屏地图任期与版本");
            foreach (string visitor in map.Visitors.ToList()) SendMapView(map, visitor);
            BroadcastPolicy();
        }
        private void SendMapView(MapRecord map, string playerId)
        {
            if (map.Checkpoint == null) throw new InvalidOperationException("Authority assignment has no map checkpoint.");
            var view = new MapView { Snapshot = map.Checkpoint, Access = AccessFor(map, playerId) };
            foreach (string id in map.Visitors.Concat(new[] { map.OwnerPlayerId }).Where(x => x != null).Distinct())
            {
                PlayerFieldState fields = bridge.CapturePlayerFields(id);
                if (fields != null) { fields.PersonalRevision = World.Players[id].PersonalRevision; view.Players.Add(fields); }
            }
            if (playerId == LocalPlayerId)
            {
                Require(bridge.ApplyMap(view.Snapshot, view.Access), "载入地图权威状态");
                localBaselines[map.MapId] = JsonData.Clone(map.Checkpoint);
                inFlightUpdates.Remove(map.MapId);
                if (map.SimulatorPlayerId == LocalPlayerId) awaitingLocalReady[map.MapId] = view.Access;
            }
            else
            {
                string peer;
                if (playerPeers.TryGetValue(playerId, out peer)) Send(peer, "view", view);
            }
        }
        private void ConfirmAuthority(string playerId, AuthorityReady ready)
        {
            MapRecord map;
            if (!World.Maps.TryGetValue(ready.MapId, out map) || map.SimulatorPlayerId != playerId || map.AuthorityEpoch != ready.AuthorityEpoch || map.Revision != ready.Revision) throw new InvalidOperationException("模拟权确认版本已经失效。");
            map.AwaitingAuthorityReady = false;
            if (playerId == LocalPlayerId) Require(bridge.SetMapAuthority(AccessFor(map, playerId)), "启用唯一地图模拟");
            BroadcastPolicy();
        }
        private void ReceiveMapView(MapView view)
        {
            if (view == null || view.Snapshot == null || view.Access == null || view.Snapshot.MapId != view.Access.MapId) throw new InvalidDataException("Incomplete map view.");
            MapRecord map;
            if (!World.Maps.TryGetValue(view.Snapshot.MapId, out map)) { map = new MapRecord { MapId = view.Snapshot.MapId }; World.Maps.Add(map.MapId, map); }
            map.AuthorityEpoch = view.Access.AuthorityEpoch; map.Revision = view.Access.Revision;
            pausedGiftRequests.Remove(map.MapId);
            map.SimulatorPlayerId = view.Access.SimulatorPlayerId; map.Checkpoint = view.Snapshot;
            map.AwaitingAuthorityReady = true;
            foreach (PlayerFieldState fields in view.Players)
            {
                Require(bridge.ApplyPlayerFields(fields), "按白名单字段恢复个人进度基准");
                World.Players[fields.PlayerId].PersonalRevision = fields.PersonalRevision;
            }
            foreach (var owner in view.Snapshot.EntityOwners)
            {
                string existing;
                if (!World.Players.ContainsKey(owner.Value) || (World.EntityOwners.TryGetValue(owner.Key, out existing) && existing != owner.Value)) throw new InvalidOperationException("地图检查点的实体归属冲突。");
            }
            applyingOwners = view.Snapshot.EntityOwners;
            try { Require(bridge.ApplyMap(view.Snapshot, view.Access), "接收地图检查点"); }
            catch (Exception ex) { LockNativeFault(ex.Message); throw; }
            finally { applyingOwners = null; }
            foreach (var owner in view.Snapshot.EntityOwners) RegisterOwner(owner.Key, owner.Value);
            localBaselines[map.MapId] = JsonData.Clone(view.Snapshot); inFlightUpdates.Remove(map.MapId);
            if (view.Access.Access == MapAccessMode.Simulator) awaitingLocalReady[map.MapId] = view.Access;
        }
        private void PublishLocalMaps()
        {
            if (World == null || LocalPlayerId == null || personalInFlight.Count != 0 || nativeFault != null) return;
            foreach (MapRecord map in World.Maps.Values.Where(x => x.SimulatorPlayerId == LocalPlayerId && !x.AwaitingAuthorityReady).ToList())
            {
                if (inFlightUpdates.ContainsKey(map.MapId)) continue;
                MapSnapshot baseline;
                localBaselines.TryGetValue(map.MapId, out baseline);
                MapUpdate update = bridge.CaptureMapUpdate(map.MapId, baseline);
                if (update == null) continue;
                if (update.Checkpoint == null || update.Checkpoint.MapId != map.MapId) throw new InvalidDataException("Map delta omitted its complete recovery checkpoint.");
                update.CommitId = Guid.NewGuid().ToString("N"); update.SessionId = SessionId;
                update.MapId = map.MapId; update.AuthorityEpoch = map.AuthorityEpoch;
                update.BaseRevision = map.Revision; update.Revision = checked(map.Revision + 1);
                update.Checkpoint.Revision = update.Revision;
                update.EntityOwners = update.Checkpoint.EntityOwners;
                if (update.Players == null) update.Players = new List<PlayerUpdate>();
                if (IsHost) AcceptMapUpdate(LocalPlayerId, null, update);
                else { inFlightUpdates[map.MapId] = update; Send("host", "map", WireMapUpdate(update)); }
            }
        }
        private static MapUpdate WireMapUpdate(MapUpdate update)
        {
            return new MapUpdate { CommitId = update.CommitId, SessionId = update.SessionId, MapId = update.MapId,
                AuthorityEpoch = update.AuthorityEpoch, BaseRevision = update.BaseRevision, Revision = update.Revision,
                NativeDeltaJson = update.NativeDeltaJson, EntityOwners = update.EntityOwners, Players = update.Players };
        }
        private void AcceptMapUpdate(string playerId, string peer, MapUpdate update)
        {
            if (update == null || string.IsNullOrWhiteSpace(update.CommitId) || update.CommitId.Length > 128) throw new InvalidDataException("Map transaction needs a stable commit identifier.");
            MapRecord map;
            if (World.Maps.TryGetValue(update.MapId, out map) && map.LastCommitId == update.CommitId && update.AuthorityEpoch == map.AuthorityEpoch && update.SessionId == SessionId) { if (peer != null) Send(peer, "map", WireMapUpdate(update)); return; }
            if (!World.Maps.TryGetValue(update.MapId, out map) || update.SessionId != SessionId || map.SimulatorPlayerId != playerId || map.AwaitingAuthorityReady || map.AuthorityEpoch != update.AuthorityEpoch || map.Revision != update.BaseRevision || update.Revision != update.BaseRevision + 1)
            {
                if (peer != null)
                {
                    Send(peer, "mapRejected", new AuthorityReady { MapId = update.MapId });
                    if (map != null) SendMapView(map, playerId);
                }
                else throw new InvalidOperationException("本机模拟地图提交与房主任期/版本不一致。");
                return;
            }
            try
            {
                // A malformed remote envelope must fail before applying any native map change.
                // Local capture and wire serialization always supply a list (possibly empty).
                if (update.EntityOwners == null || update.Players == null || (update.Checkpoint != null && update.Checkpoint.MapId != map.MapId)) throw new InvalidDataException("Incomplete map transaction ownership or player update registry.");
                // Validate the entire set before any native map or personal write.
                MapTransactionPlayerValidation.RequireUnique(update.Players);
                foreach (var owner in update.EntityOwners)
                {
                    string existing;
                    if (!World.Players.ContainsKey(owner.Value) || (World.EntityOwners.TryGetValue(owner.Key, out existing) && existing != owner.Value)) throw new InvalidOperationException("地图提交不能转移既有雇员归属。");
                }
                // Verify EVERY newly introduced UID before a remote native
                // delta can register its claimed owners. Existing hosted or
                // transported entities keep the source recorded by the host.
                if (peer != null)
                    MapEntityUidValidation.RequireRemoteProvenance(
                        update.EntityOwners, World.EntityOwners,
                        map.Checkpoint == null ? null : map.Checkpoint.EntityOwners,
                        World.Players, playerId, SessionId);
                applyingOwners = update.EntityOwners;
                foreach (PlayerUpdate personal in update.Players ?? new List<PlayerUpdate>())
                {
                    if (personal == null || !World.Players.ContainsKey(personal.PlayerId)) throw new InvalidOperationException("地图提交包含未知个人状态。");
                    if (playerId != World.HostPlayerId && personal.PlayerId != playerId && personal.PlayerId != map.OwnerPlayerId && !map.Visitors.Contains(personal.PlayerId)) throw new InvalidOperationException("地图提交包含无关玩家状态。");
                    if (personal.BaseRevision != World.Players[personal.PlayerId].PersonalRevision || personal.Revision != personal.BaseRevision + 1) throw new InvalidOperationException("个人进度提交基准版本已过期，整组地图事务未应用。");
                }
                if (playerId != LocalPlayerId) Require(bridge.ValidateMapUpdate(update), "预先验证整组地图与个人字段差量");
            }
            catch
            {
                applyingOwners = null;
                if (peer != null) { Send(peer, "mapRejected", new AuthorityReady { MapId = map.MapId }); SendMapView(map, playerId); }
                throw;
            }
            MapSnapshot committed;
            try
            {
                if (playerId != LocalPlayerId) Require(bridge.ApplyMapUpdate(update), "合并权威地图实时变动");
                committed = playerId == LocalPlayerId ? update.Checkpoint : bridge.CaptureMap(map.MapId);
                if (committed == null || committed.MapId != map.MapId) throw new InvalidOperationException("地图差量已应用，但无法构造当前版本恢复检查点。");
                committed.Revision = update.Revision;
            }
            catch (Exception ex) { LockNativeFault(ex.Message); throw; }
            finally { applyingOwners = null; }
            foreach (var owner in update.EntityOwners) RegisterOwner(owner.Key, owner.Value);
            foreach (PlayerUpdate personal in update.Players ?? new List<PlayerUpdate>())
            {
                World.Players[personal.PlayerId].PersonalRevision = personal.Revision;
            }
            map.Revision = update.Revision; map.Checkpoint = committed; map.LastCommitId = update.CommitId; map.LastSimulatedWorldMM = World.WorldTimeMM;
            if (playerId == LocalPlayerId) localBaselines[map.MapId] = committed;
            // Map revisions already deduplicate updates; no per-frame transaction ledger is retained.
            // ACK the canonical simulator first. Failure to enqueue a delta for
            // another visitor cannot conceal this successfully applied commit.
            if (peer != null) Send(peer, "map", CommittedMapReceipt(update));
            foreach (string visitor in map.Visitors.ToList())
            {
                string targetPeer;
                if (playerPeers.TryGetValue(visitor, out targetPeer))
                {
                    if (targetPeer == peer) continue;
                    SendCommittedMapToObserver(targetPeer, update);
                }
            }
            foreach (PlayerUpdate personal in update.Players)
            {
                string targetPeer;
                if (!map.Visitors.Contains(personal.PlayerId) && playerPeers.TryGetValue(personal.PlayerId, out targetPeer)
                    && targetPeer != peer)
                    BroadcastCommittedPersonalToRecipient(targetPeer, personal);
            }
        }
        private void ReceiveMapUpdate(MapUpdate update)
        {
            MapRecord map;
            if (!World.Maps.TryGetValue(update.MapId, out map) || update.AuthorityEpoch != map.AuthorityEpoch || update.SessionId != SessionId) throw new InvalidOperationException("收到旧任期地图变动。");
            if (update.Revision <= map.Revision)
            {
                MapUpdate pending;
                if (inFlightUpdates.TryGetValue(map.MapId, out pending) && pending.CommitId == update.CommitId) { localBaselines[map.MapId] = pending.Checkpoint; inFlightUpdates.Remove(map.MapId); }
                return;
            }
            if (update.BaseRevision != map.Revision) { Send("host", "resync", new MapReference { MapId = map.MapId }); throw new InvalidOperationException("地图变动缺包，已请求完整检查点。"); }
            if (map.SimulatorPlayerId != LocalPlayerId)
            {
                if (update.EntityOwners == null) throw new InvalidDataException("地图事务缺少实体归属登记。");
                foreach (var owner in update.EntityOwners)
                {
                    string existing;
                    if (!World.Players.ContainsKey(owner.Value) || (World.EntityOwners.TryGetValue(owner.Key, out existing) && existing != owner.Value)) throw new InvalidOperationException("地图事务不能转移已有雇员归属。");
                }
                applyingOwners = update.EntityOwners;
                try { Require(bridge.ValidateMapUpdate(update), "验证完整地图与个人差量"); Require(bridge.ApplyMapUpdate(update), "实时应用其他玩家地图变动"); }
                catch (Exception ex) { LockNativeFault(ex.Message); throw; }
                finally { applyingOwners = null; }
                foreach (var owner in update.EntityOwners) RegisterOwner(owner.Key, owner.Value);
            }
            map.Revision = update.Revision;
            foreach (PlayerUpdate player in update.Players ?? new List<PlayerUpdate>())
            {
                if (player == null) continue;
                if (!World.Players.ContainsKey(player.PlayerId)) throw new InvalidOperationException("个人差量玩家不存在。");
                World.Players[player.PlayerId].PersonalRevision = player.Revision;
            }
            if (map.SimulatorPlayerId == LocalPlayerId)
            {
                MapUpdate pending;
                if (!inFlightUpdates.TryGetValue(map.MapId, out pending) || pending.CommitId != update.CommitId)
                    throw new InvalidOperationException("模拟地图提交回执没有对应的本地检查点。");
                map.Checkpoint = pending.Checkpoint; localBaselines[map.MapId] = pending.Checkpoint; inFlightUpdates.Remove(map.MapId);
            }
        }
        private void RebaseMap(long mapId)
        {
            if (mapId < 0) return;
            MapRecord map = EnsureMap(mapId);
            RefreshCheckpoint(map); ReassignMap(map, map.SimulatorPlayerId);
        }
        private void PublishPersonalUpdates()
        {
            if (World == null || inFlightUpdates.Count != 0 || personalInFlight.Count != 0 || nativeFault != null) return;
            foreach (PlayerUpdate update in bridge.CapturePlayerUpdates())
            {
                if (IsHost) AcceptPersonalUpdate(LocalPlayerId, null, update);
                else
                {
                    if (update.PlayerId != LocalPlayerId) throw new InvalidOperationException("客户端不能在地图事务外提交其他玩家个人进度。");
                    SendCapturedPersonalUpdate(update);
                }
            }
        }
        private void AcceptPersonalUpdate(string sender, string peer, PlayerUpdate update)
        {
            PlayerRecord player;
            if (update == null || !World.Players.TryGetValue(update.PlayerId, out player) || (sender != World.HostPlayerId && sender != update.PlayerId)) throw new InvalidOperationException("个人进度提交归属不匹配。");
            if (update.BaseRevision != player.PersonalRevision || update.Revision != update.BaseRevision + 1)
            {
                if (peer != null)
                {
                    PlayerFieldState reset = bridge.CapturePlayerFields(player.PlayerId); reset.PersonalRevision = player.PersonalRevision;
                    Send(peer, "personalReset", reset);
                }
                throw new InvalidOperationException("个人进度基准已更新，已发送房主白名单字段重新同步。");
            }
            if (sender != LocalPlayerId)
            {
                try { Require(bridge.ApplyPlayerUpdate(update), "原地应用个人进度差量"); }
                catch
                {
                    if (peer != null)
                    {
                        PlayerFieldState reset = bridge.CapturePlayerFields(player.PlayerId); reset.PersonalRevision = player.PersonalRevision;
                        Send(peer, "personalReset", reset);
                    }
                    throw;
                }
            }
            player.PersonalRevision = update.Revision;
            // This ACK confirms host application, not save completion. Deliver
            // it before unreliable observers can exhaust their send queues.
            if (peer != null) Send(peer, "personalAck", update);
            BroadcastCommittedUpdate("personal", update, peer);
        }
    }
}
