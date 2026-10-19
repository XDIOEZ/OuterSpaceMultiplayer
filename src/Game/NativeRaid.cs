using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private readonly Dictionary<long, string> raidPreparations = new Dictionary<long, string>();
        private sealed class RaidNativeState { public long LandingExpeditionId; public long EvacuationExpeditionId = -1; }
        public BridgeResult ApplyRelationship(RelationshipRecord relationship)
        {
            return Run(() =>
            {
                if (MapManager.instance == null) return;
                ReleaseIncompatibleGroundWork();
                foreach (BaseHero hero in MapManager.instance.heroManager.getAllRoles())
                {
                    string owner = Context.OwnerOfEntity(hero.getUID());
                    if (owner == relationship.PlayerA || owner == relationship.PlayerB)
                        hero.roleBean.fightHatreds.clearHatred();
                }
            });
        }
        public void OpenRaidPreparation(long mapId)
        {
            New_UIExpeditionDialog dialog = FairyUIDispatcher.showNewExpeditionDialog();
            dialog.doFairyExpedition(Maps[mapId]);
        }
        public RaidRequest BuildRaidRequest(long targetMapId)
        {
            New_UIExpeditionDialog dialog = UnityEngine.Object.FindObjectOfType<New_UIExpeditionDialog>();
            if (dialog == null || dialog.pakTeam == null || dialog.pakTeam.vehicleBean == null)
                throw new InvalidOperationException("请先在原版远征面板选择出发舰队、空投仓及参战雇员。");
            New_ExpeditionPakTeams team = dialog.pakTeam;
            AirShipFleetInfoBean fleet = team.getStartFleetBean();
            if (fleet == null) throw new InvalidOperationException("离线袭击必须选择实际出发舰队。");
            Manifest manifest = new Manifest { VehicleId = team.vehicleBean.getId(), DurationSeconds = team.needTime_second,
                Heroes = team.heroBeans.Select(h => h.getUID()).ToList() };
            if (manifest.Heroes.Count == 0) throw new InvalidOperationException("请选择实际参战雇员。");
            foreach (FWThingBean item in team.things) manifest.Items.Add(new CargoLine { Uid = item.getUID(), Count = item.getSum() });
            long reservation = NextUid(); raidPreparations[reservation] = NativeWire.Json(manifest);
            return new RaidRequest { MapId = targetMapId, DropPodId = reservation, SourceFleetId = fleet.bean.getUID(),
                AttackerHeroIds = manifest.Heroes, NativeTransportJson = raidPreparations[reservation] };
        }
        public BridgeResult InspectRaid(RaidRequest request, string attackerPlayerId, out RaidStartData data)
        {
            data = null;
            try
            {
                Manifest manifest = NativeWire.Parse<Manifest>(request.NativeTransportJson);
                if (manifest.Heroes.Count == 0 || manifest.Heroes.Distinct().Count() != manifest.Heroes.Count
                    || !manifest.Heroes.OrderBy(u => u).SequenceEqual(request.AttackerHeroIds.OrderBy(u => u)))
                    throw new InvalidOperationException("出征清单与本场雇员名单不一致或重复。");
                if (manifest.Items.Any(i => i.Count <= 0) || manifest.Items.Select(i => i.Uid).Distinct().Count() != manifest.Items.Count)
                    throw new InvalidOperationException("出征物资数量无效或同一来源重复选择。");
                B_VehicleDef vehicle = B_VehicleDef.getDataID(manifest.VehicleId);
                if (vehicle == null || vehicle.Carrying <= 0) throw new InvalidOperationException("出征载具不存在或没有原版运输容量。");
                FWFleet fleet = OwnedFleets(attackerPlayerId).SingleOrDefault(f => f.getUID() == request.SourceFleetId);
                if (fleet == null) throw new InvalidOperationException("Source fleet identity mismatch.");
                foreach (long uid in request.AttackerHeroIds)
                    if (Context.OwnerOfEntity(uid) != attackerPlayerId || !fleet.getHeros().Any(h => h.getUID() == uid)) throw new InvalidOperationException("出征雇员不在该玩家出发舰队。");
                raidPreparations[request.DropPodId] = request.NativeTransportJson;
                data = new RaidStartData { Capacity = vehicle.Carrying, ProtectedDefenderHeroIds = Players.Values.SelectMany(p => p.heros)
                    .Where(h => h.isInMap(request.MapId) && Context.OwnerOfEntity(h.getUID()) != attackerPlayerId && !h.isDead())
                    .Select(h => h.getUID()).ToList() };
                return BridgeResult.Ok();
            }
            catch (Exception error) { return BridgeResult.Fail(error.Message); }
        }
        public BridgeResult BeginRaid(AttackRecord attack)
        {
            return Run(() =>
            {
                string manifest;
                if (!raidPreparations.TryGetValue(attack.DropPodId, out manifest)) manifest = attack.NativeTransportJson;
                if (string.IsNullOrEmpty(manifest)) throw new InvalidOperationException("Missing registered native sortie manifest.");
                var transfer = new TransferRecord { TransferId = attack.AttackId + ".landing", PlayerId = attack.AttackerPlayerId,
                    Request = new TransferRequest { SourceFleetId = attack.SourceFleetId, DestinationMapId = attack.MapId,
                        HeroIds = attack.AttackerHeroIds.ToList(), NativeManifestJson = manifest } };
                FWExpedition expedition = BuildNativeExpedition(transfer);
                Attacks[attack.AttackId] = attack;
                attack.LandingExpeditionId = expedition.getUID();
                attack.NativeBattleState = NativeWire.Json(new RaidNativeState { LandingExpeditionId = expedition.getUID() });
                QueueNativeLanding(expedition, attack.MapId, attack.AttackerPlayerId);
            });
        }
        public RaidEvacuation BuildRaidEvacuation(AttackRecord attack)
        {
            if (attack.AttackerPlayerId != Context.LocalPlayerId || CurrentMapId != attack.MapId) throw new InvalidOperationException("当前玩家不在本场进攻地图。");
            New_UIExpeditionDialog dialog = UnityEngine.Object.FindObjectOfType<New_UIExpeditionDialog>();
            if (dialog == null || dialog.pakTeam == null) throw new InvalidOperationException("请在原版运输面板选择本次撤离物资。");
            string departure;
            if (!raidPreparations.TryGetValue(attack.DropPodId, out departure)) departure = attack.NativeTransportJson;
            if (string.IsNullOrEmpty(departure)) throw new InvalidOperationException("本场袭击缺少已保存的载具登记。");
            Manifest manifest = new Manifest { VehicleId = NativeWire.Parse<Manifest>(departure).VehicleId };
            double capacity = 0;
            foreach (FWThingBean thing in dialog.pakTeam.things)
            { manifest.Items.Add(new CargoLine { Uid = thing.getUID(), Count = thing.getSum() }); capacity += thing.getWeight(); }
            manifest.Heroes = dialog.pakTeam.heroBeans.Select(h => h.getUID()).ToList();
            return new RaidEvacuation { AttackId = attack.AttackId, TransferId = attack.AttackId + ".evacuation", CapacityUsed = capacity, NativeManifestJson = NativeWire.Json(manifest) };
        }
        public BridgeResult EvacuateRaid(AttackRecord attack, RaidEvacuation evacuation)
        {
            return Run(() =>
            {
                Manifest manifest = NativeWire.Parse<Manifest>(evacuation.NativeManifestJson);
                Manifest departure = NativeWire.Parse<Manifest>(attack.NativeTransportJson);
                if (manifest.VehicleId != departure.VehicleId) throw new NativeRejectionException("撤离必须沿用开战登记的唯一载具。");
                if (manifest.Items.Select(i => i.Uid).Distinct().Count() != manifest.Items.Count || manifest.Heroes.Distinct().Count() != manifest.Heroes.Count)
                    throw new NativeRejectionException("撤离清单重复选择同一实体。");
                if (manifest.Animals.Count != 0 || manifest.Heroes.Any(uid => !attack.AttackerHeroIds.Contains(uid))) throw new NativeRejectionException("撤离不能夹带本场以外的角色。");
                var materials = new Dictionary<long, int>();
                double weight = 0;
                foreach (CargoLine item in manifest.Items)
                {
                    if (item.Count <= 0) throw new NativeRejectionException("撤离物资数量无效。");
                    LootReservation loan = LootReservations.SingleOrDefault(r => r.CarryUid == item.Uid);
                    long sourceUid = item.Uid;
                    if (loan != null)
                    {
                        if (loan.AttackId != attack.AttackId || loan.Count < item.Count || !attack.AttackerHeroIds.Contains(loan.HeroUid)) throw new NativeRejectionException("战利品预占记录不属于本场或数量不足。");
                        FWHeroBean carrier = Players[attack.AttackerPlayerId].heros.Single(h => h.getUID() == loan.HeroUid && h.isInMap(attack.MapId));
                        if (carrier.isDead() || !carrier.carry_Daily.Concat(carrier.carry_Task).Any(t => t.getUID() == loan.CarryUid && t.getSum() >= item.Count)) throw new NativeRejectionException("战利品携带者已死亡或预占物品不再随身。");
                        sourceUid = loan.SourceUid;
                    }
                    int quantity; materials.TryGetValue(sourceUid, out quantity); materials[sourceUid] = checked(quantity + item.Count);
                }
                if (materials.Count != 0 && !AreDefendersDefeated(attack)) throw new NativeRejectionException("守军尚未失去作战能力，当前只能空仓撤离。");
                foreach (var item in materials)
                {
                    FWThingBean source = Maps[attack.MapId].starSpread.things.SingleOrDefault(t => t.getUID() == item.Key);
                    if (source == null || source.getSum() < item.Value) throw new NativeRejectionException("预占战利品来源已变化或不足。");
                    FWThingBean portion = NativeWire.Unpack<FWThingBean>(NativeWire.Pack(source)); portion.setSum(item.Value); weight += portion.getWeight();
                }
                foreach (long uid in manifest.Heroes)
                    if (!Players[attack.AttackerPlayerId].heros.Any(h => h.getUID() == uid && h.isInMap(attack.MapId) && !h.isDead() && !h.prisoner)) throw new NativeRejectionException("撤离雇员已不在本场地块或不可运输。");
                if (weight > attack.Capacity) throw new NativeRejectionException("战利品超过开战锁定的单个原版空投仓容量。");
                manifest.Items = materials.Select(p => new CargoLine { Uid = p.Key, Count = p.Value }).ToList();
                evacuation.CapacityUsed = weight;
                if (materials.Count == 0 && manifest.Heroes.Count == 0) return;
                var transfer = new TransferRecord { TransferId = evacuation.TransferId, PlayerId = attack.AttackerPlayerId,
                    Request = new TransferRequest { SourceMapId = attack.MapId, DestinationFleetId = attack.SourceFleetId,
                        HeroIds = manifest.Heroes.ToList(), NativeManifestJson = NativeWire.Json(manifest) } };
                var borrowed = LootReservations.Where(r => r.AttackId == attack.AttackId).Select(r =>
                {
                    FWHeroBean hero = Players[attack.AttackerPlayerId].heros.Single(h => h.getUID() == r.HeroUid);
                    FWThingBean copy = (r.CarryType == FWRoleBean.carryType.type_Daily ? hero.carry_Daily : hero.carry_Task).Single(t => t.getUID() == r.CarryUid);
                    return Tuple.Create(NativeWire.Parse<LootReservation>(NativeWire.Json(r)), NativeWire.Pack(copy));
                }).ToList();
                CommittingRaidTransfer = true;
                try
                {
                    // The carrier gets real source stock once. Loan copies never enter the outbound
                    // expedition or the saved fleet inventory, including copies held by unselected roles.
                    foreach (long hero in attack.AttackerHeroIds) ReleaseAllLoot(hero);
                    FWExpedition expedition = BuildNativeExpedition(transfer);
                    RaidNativeState state = NativeWire.Parse<RaidNativeState>(attack.NativeBattleState);
                    state.EvacuationExpeditionId = expedition.getUID(); attack.NativeBattleState = NativeWire.Json(state);
                    evacuation.Transfer = transfer;
                }
                catch (NativeRejectionException)
                {
                    // BuildNativeExpedition uses this exception only before its first UID/carrier or
                    // inventory mutation. Restore exactly the loans removed for its capacity precheck.
                    foreach (var backup in borrowed)
                    {
                        LootReservation loan = backup.Item1;
                        FWHeroBean hero = Players[attack.AttackerPlayerId].heros.Single(h => h.getUID() == loan.HeroUid);
                        List<FWThingBean> bag = loan.CarryType == FWRoleBean.carryType.type_Daily ? hero.carry_Daily : hero.carry_Task;
                        if (bag.Any(t => t.getUID() == loan.CarryUid) || LootReservations.Any(r => r.CarryUid == loan.CarryUid))
                            throw new InvalidOperationException("Cannot restore a rejected evacuation over a duplicate loan.");
                        bag.Add(NativeWire.Unpack<FWThingBean>(backup.Item2)); LootReservations.Add(loan);
                    }
                    throw;
                }
                finally { CommittingRaidTransfer = false; }
            });
        }
        public BridgeResult FinishRaid(AttackRecord attack, RaidFinishReason reason)
        {
            return Run(() =>
            {
                using (EnterPlayer(attack.AttackerPlayerId))
                {
                    foreach (long uid in attack.AttackerHeroIds)
                    {
                        ReleaseAllLoot(uid);
                        FWHeroBean hero = PlayerHeroes(attack.AttackerPlayerId).Single(h => h.getUID() == uid);
                        if (hero.isDead()) continue;
                        if (hero.isInMap(attack.MapId))
                        {
                            BaseHero projection = CurrentMapId == attack.MapId && MapManager.instance != null ? MapManager.instance.heroManager.getHeroByUID(uid) : null;
                            if (projection != null) projection.clearBag(true);
                            else
                            {
                                foreach (FWThingBean item in hero.carry_Task.ToArray()) Maps[attack.MapId].starSpread.things.Add(item);
                                hero.carry_Task.Clear();
                                foreach (FWThingBean item in hero.carry_Daily.ToArray()) Maps[attack.MapId].starSpread.things.Add(item);
                                hero.carry_Daily.Clear();
                            }
                            if (projection != null) MapManager.instance.heroManager.removeRole(hero);
                            hero.starMapId = -1; hero.fleetUid = attack.SourceFleetId;
                            OwnedFleets(attack.AttackerPlayerId).Single(f => f.getUID() == attack.SourceFleetId).AddHero(hero);
                        }
                    }
                }
                Attacks.Remove(attack.AttackId);
                NativeLandings.Remove(attack.LandingExpeditionId);
                raidPreparations.Remove(attack.DropPodId);
                GameHooks.EndProtection(attack.ProtectedDefenderHeroIds);
            });
        }
        public bool AreDefendersDefeated(AttackRecord attack)
        {
            return attack.ProtectedDefenderHeroIds.All(uid =>
            {
                FWHeroBean hero = Players.Values.SelectMany(p => p.heros).FirstOrDefault(h => h.getUID() == uid);
                return hero != null && (hero.isFallDown() || GameHooks.IsKnockedOut(uid));
            });
        }
        public bool IsMapReady(long mapId) { return CurrentMapId == mapId && MapManager.instance != null && MapManager.isInitFinish; }
        public bool IsRaidReady(AttackRecord attack)
        {
            if (attack.LandingExpeditionId <= 0) throw new InvalidOperationException("袭击缺少持久化原生落仓记录。");
            NativeLanding landing;
            if (!NativeLandings.TryGetValue(attack.LandingExpeditionId, out landing)) throw new InvalidOperationException("袭击落仓记录未随战场检查点恢复。");
            return IsNativeLandingComplete(attack.LandingExpeditionId, attack.MapId, attack.AttackerHeroIds);
        }
    }
}
