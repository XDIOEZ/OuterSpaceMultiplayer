using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private FWFleet TransferFleet(string owner, long uid)
        {
            FWFleet fleet = OwnedFleets(owner).SingleOrDefault(f => f.getUID() == uid);
            if (fleet == null) throw new InvalidOperationException("运输舰队不属于当前玩家：" + uid);
            return fleet;
        }
        private IList<FWThingBean> SourceItems(TransferRequest request, string owner, bool destination)
        {
            long mapId = destination ? request.DestinationMapId : request.SourceMapId;
            if (mapId >= 0)
            {
                FWStarMapInfo map;
                if (!Maps.TryGetValue(mapId, out map)) throw new InvalidOperationException("运输地图尚未由房主生成：" + mapId);
                return map.starSpread.things;
            }
            return TransferFleet(owner, destination ? request.DestinationFleetId : request.SourceFleetId).getThings();
        }
        private FWExpeditionGoalPos TransportGoal(string playerId, long mapId, long fleetId)
        {
            if (mapId < 0)
            {
                FWFleet fleet = TransferFleet(playerId, fleetId);
                using (EnterPlayer(playerId)) return ExpeditionTransportRouteUtils.CreateRouteGoal(FleetView(playerId, fleet));
            }
            FWStarMapInfo map;
            if (!Maps.TryGetValue(mapId, out map)) throw new InvalidOperationException("运输目标地图不存在：" + mapId);
            string mapOwner = Context.OwnerOfMap(mapId) ?? playerId;
            FWExpeditionGoalPos route;
            using (EnterPlayer(mapOwner)) route = ExpeditionTransportRouteUtils.CreateRouteGoal(map);
            var goal = new FWExpeditionGoalPos(FWBagGoalPos.BagType._无, FWGoalPos.GoalType._STAR_MAP_INFO, mapId);
            goal.galaxyId = route.galaxyId; goal.galaxyX = route.galaxyX; goal.galaxyY = route.galaxyY;
            goal.fixedGalaxyPos = route.fixedGalaxyPos; goal.starId = map.starId; goal.starWorldPoint = map.worldPoint;
            return goal;
        }
        private FWExpedition FindCarrier(TransferRecord transfer)
        {
            return Players[transfer.PlayerId].expeditionSeq.SingleOrDefault(e => e.getUID() == transfer.NativeExpeditionId);
        }
        private void DebitTransportItem(IList<FWThingBean> inventory, FWThingBean item, int count)
        {
            if (count < 1 || item.getSum() < count) throw new InvalidOperationException("运输提交时库存发生变化：" + item.getUID());
            item.setSum(item.getSum() - count);
            if (item.getSum() != 0) return;
            FWStarMapInfo map = Maps.Values.FirstOrDefault(m => ReferenceEquals(m.starSpread.things, inventory));
            if (map != null && CurrentMapId == map.starMapId && MapManager.instance != null) map.starSpread.removeThing(item);
            else inventory.Remove(item);
        }
        // The expedition is the only cargo carrier. NativeCargoJson contains references and completion,
        // never another serialized copy of its inventory. The original record survives a partial failure.
        internal FWExpedition BuildNativeExpedition(TransferRecord transfer)
        {
            bool nativeMutation = false;
            try
            {
                if (transfer == null || transfer.Request == null || !Players.ContainsKey(transfer.PlayerId)) throw new InvalidOperationException("运输原单或玩家不存在。");
                if (transfer.NativeExpeditionId >= 0)
                {
                    FWExpedition previous = FindCarrier(transfer);
                    if (previous == null) throw new InvalidOperationException("已登记运输丢失唯一原生载体：" + transfer.NativeExpeditionId);
                    return previous;
                }
                TransferRequest request = transfer.Request;
                if ((request.SourceMapId >= 0) == (request.SourceFleetId >= 0) || (request.DestinationMapId >= 0) == (request.DestinationFleetId >= 0))
                    throw new InvalidOperationException("运输始发/目标必须各自唯一指定地图或舰队。");
                if (request.SourceMapId >= 0 && request.SourceMapId == request.DestinationMapId || request.SourceFleetId >= 0 && request.SourceFleetId == request.DestinationFleetId)
                    throw new InvalidOperationException("运输始发与目的相同。");
                Manifest manifest = NativeWire.Parse<Manifest>(request.NativeManifestJson);
                if (manifest == null || manifest.Items == null || manifest.Heroes == null || manifest.Animals == null) throw new InvalidOperationException("运输清单无效。");
                if (manifest.Items.Select(x => x.Uid).Distinct().Count() != manifest.Items.Count || manifest.Heroes.Distinct().Count() != manifest.Heroes.Count || manifest.Animals.Distinct().Count() != manifest.Animals.Count)
                    throw new InvalidOperationException("运输清单重复选择同一实体。");
                if (request.HeroIds.Count != 0 && !request.HeroIds.OrderBy(x => x).SequenceEqual(manifest.Heroes.OrderBy(x => x))) throw new InvalidOperationException("运输雇员名单与原单不一致。");
                string sourceOwner = request.SourceMapId >= 0 ? Context.OwnerOfMap(request.SourceMapId) : transfer.PlayerId;
                if (!CommittingRaidTransfer && sourceOwner != transfer.PlayerId && manifest.Items.Count != 0) throw new InvalidOperationException("不能直接运输他人地块库存。");
                FWGameData owner = Players[transfer.PlayerId];
                FWFleet sourceFleet = request.SourceFleetId >= 0 ? TransferFleet(transfer.PlayerId, request.SourceFleetId) : null;
                IList<FWThingBean> source = SourceItems(request, transfer.PlayerId, false);
                var portions = new List<Tuple<FWThingBean, int, FWThingBean>>();
                foreach (CargoLine line in manifest.Items)
                {
                    FWThingBean bean = source.SingleOrDefault(t => t.getUID() == line.Uid);
                    if (bean == null || line.Count <= 0 || bean.getSum() < line.Count) throw new InvalidOperationException("运输来源库存不足：" + line.Uid);
                    FWThingBean portion = NativeWire.Unpack<FWThingBean>(NativeWire.Pack(bean)); portion.setSum(line.Count);
                    portions.Add(Tuple.Create(bean, line.Count, portion));
                }
                var heroes = new List<FWHeroBean>();
                foreach (long uid in manifest.Heroes)
                {
                    FWHeroBean hero = sourceFleet != null ? sourceFleet.getHeros().SingleOrDefault(h => h.getUID() == uid) : owner.heros.SingleOrDefault(h => h.getUID() == uid && h.isInMap(request.SourceMapId));
                    if (hero == null || Context.OwnerOfEntity(uid) != transfer.PlayerId || hero.isDead() || hero.prisoner) throw new InvalidOperationException("运输雇员不在真实始发地或不可出征：" + uid);
                    heroes.Add(hero);
                }
                var animals = new List<FWAnimalBean>();
                foreach (long uid in manifest.Animals)
                {
                    FWAnimalBean animal = sourceFleet != null ? sourceFleet.getAnimals().SingleOrDefault(a => a.getUID() == uid) : owner.animals.SingleOrDefault(a => a.getUID() == uid && a.isInMap(request.SourceMapId));
                    if (animal == null || Context.OwnerOfEntity(uid) != transfer.PlayerId || animal.isDead()) throw new InvalidOperationException("运输动物不在真实始发地：" + uid);
                    animals.Add(animal);
                }
                if (portions.Count == 0 && heroes.Count == 0 && animals.Count == 0 && !CommittingRaidTransfer) throw new InvalidOperationException("运输清单为空。");
                B_VehicleDef vehicle = B_VehicleDef.getDataID(manifest.VehicleId);
                if (vehicle == null || vehicle.Carrying <= 0) throw new InvalidOperationException("运输载具不存在或无原生容量。");
                double weight = portions.Sum(x => (double)x.Item3.getWeight()) + heroes.Sum(h => (double)h.getWeight()) + animals.Sum(a => (double)a.getWeight());
                if (double.IsNaN(weight) || double.IsInfinity(weight) || weight > vehicle.Carrying) throw new InvalidOperationException("运输超出所选原生载具容量。");
                FWExpeditionGoalPos start = TransportGoal(transfer.PlayerId, request.SourceMapId, request.SourceFleetId);
                FWExpeditionGoalPos end = TransportGoal(transfer.PlayerId, request.DestinationMapId, request.DestinationFleetId);
                if (start.galaxyId != end.galaxyId) throw new InvalidOperationException("原生运输路线必须位于同一星系。");
                var route = ExpeditionTransportRouteUtils.Calculate(vehicle, start, end);
                if (route.NeedSecond <= 0 || float.IsNaN(route.NeedSecond) || float.IsInfinity(route.NeedSecond)) throw new InvalidOperationException("原生运输路线无效。");
                if (manifest.UseSpaceElevator) throw new InvalidOperationException("联机运输尚不允许未由房主核实的太空电梯加速，请取消电梯选项。");
                FWThingBean probe = null; IList<FWThingBean> probeInventory = null;
                if (!CommittingRaidTransfer)
                {
                    bool mothership = request.SourceMapId == owner.getMothershipStarMapInfo().starMapId || request.DestinationMapId == owner.getMothershipStarMapInfo().starMapId;
                    bool hasProvider = mothership && vehicle.ID == B_VehicleDef.舰队;
                    if (sourceFleet != null)
                    {
                        B_VehicleDef provided = (B_VehicleDef)NativeWire.Call(FleetView(transfer.PlayerId, sourceFleet), "getVechicle");
                        hasProvider |= provided != null && provided.ID == vehicle.ID;
                    }
                    if (request.SourceMapId >= 0) hasProvider |= Maps[request.SourceMapId].vehicleStationList.Any(s => s.getVechicleDef().getProvideVehicles().Any(v => v.ID == vehicle.ID));
                    if (!hasProvider && request.DestinationFleetId >= 0)
                    {
                        B_VehicleDef provided = (B_VehicleDef)NativeWire.Call(FleetView(transfer.PlayerId, TransferFleet(transfer.PlayerId, request.DestinationFleetId)), "getVechicle");
                        hasProvider = provided != null && provided.ID == vehicle.ID;
                    }
                    if (!hasProvider) throw new InvalidOperationException("始发地图/本船没有提供所选运输载具。");
                    if (vehicle.FuelType == (int)B_VehicleDef.VehicleFuelType.探针)
                    {
                        var inventories = new List<IList<FWThingBean>>();
                        if (sourceFleet != null) inventories.Add(sourceFleet.getThings());
                        if (request.DestinationFleetId >= 0) inventories.Add(TransferFleet(transfer.PlayerId, request.DestinationFleetId).getThings());
                        if (request.SourceMapId >= 0 && sourceOwner == transfer.PlayerId) inventories.Add(source);
                        if (request.DestinationMapId >= 0 && Context.OwnerOfMap(request.DestinationMapId) == transfer.PlayerId) inventories.Add(Maps[request.DestinationMapId].starSpread.things);
                        foreach (IList<FWThingBean> inventory in inventories)
                        {
                            probe = inventory.FirstOrDefault(t => t.id == T_MaterialDef.ID_探针 && t.getSum() > portions.Where(p => ReferenceEquals(p.Item1, t)).Sum(p => p.Item2));
                            if (probe != null) { probeInventory = inventory; break; }
                        }
                        if (probe == null) throw new InvalidOperationException("运输路线没有可实际扣除的探针（装载物资不算剩余燃料）。");
                    }
                }
                // All validation/serialization above precedes the only source debit.
                nativeMutation = true;
                var expedition = new FWExpedition { uid = NextUid(), startGoalPos = start, endGoalPos = end,
                    missionUid = manifest.MissionUid, vehicleId = vehicle.ID, status = FWExpedition.STATUS_MARCH,
                    roleUidList = heroes.Select(h => h.getUID()).ToList(), animalUidList = animals.Select(a => a.getUID()).ToList() };
                using (EnterPlayer(transfer.PlayerId)) expedition.StartTravel(WorldTimeMM, route.NeedSecond, false);
                foreach (var portion in portions) { portion.Item3.uid = NextUid(); expedition.things.Add(portion.Item3); }
                transfer.NativeExpeditionId = expedition.getUID(); transfer.ArrivalWorldMM = expedition.endTime;
                transfer.NativeCargoJson = NativeWire.Json(new Cargo { ExpeditionUid = expedition.getUID(), Heroes = expedition.roleUidList.ToList(), Animals = expedition.animalUidList.ToList() });
                owner.expeditionSeq.Add(expedition);
                foreach (var portion in portions) DebitTransportItem(source, portion.Item1, portion.Item2);
                if (probe != null) DebitTransportItem(probeInventory, probe, 1);
                foreach (FWHeroBean hero in heroes)
                {
                    if (CurrentMapId == request.SourceMapId && MapManager.instance != null) MapManager.instance.heroManager.removeRole(hero);
                    if (sourceFleet != null) sourceFleet.removeHeroByUid(hero.getUID());
                    if (!owner.heros.Any(h => h.getUID() == hero.getUID())) owner.heros.Add(hero);
                    hero.expedtion_start(-1); hero.fleetUid = -1; hero.airShipUid = -1;
                }
                foreach (FWAnimalBean animal in animals)
                {
                    if (CurrentMapId == request.SourceMapId && MapManager.instance != null) MapManager.instance.animalManager.removeAnimal(animal);
                    if (sourceFleet != null) sourceFleet.removeAnimalByUid(animal.getUID());
                    if (!owner.animals.Any(a => a.getUID() == animal.getUID())) owner.animals.Add(animal);
                    animal.expedtion_start(-1); animal.fleetUid = -1; animal.airShipUid = -1;
                }
                return expedition;
            }
            catch (NativeRejectionException) { throw; }
            catch (Exception error)
            {
                if (!nativeMutation) throw new NativeRejectionException(error.Message, error);
                throw;
            }
        }
        public BridgeResult DepartTransfer(TransferRecord transfer) { return Run(() => BuildNativeExpedition(transfer)); }
        public BridgeResult ArriveTransfer(TransferRecord transfer)
        {
            return Run(() =>
            {
                if (WorldTimeMM < transfer.ArrivalWorldMM) throw new InvalidOperationException("原生运输航程尚未到期。");
                Cargo cargo = NativeWire.Parse<Cargo>(transfer.NativeCargoJson);
                if (cargo.Delivered) return;
                FWExpedition expedition = FindCarrier(transfer);
                if (expedition == null) throw new InvalidOperationException("运输缺失唯一原生载体。");
                if (expedition.endStarMapId >= 0) { QueueNativeLanding(expedition, expedition.endStarMapId, transfer.PlayerId); return; }
                FWFleet destination = TransferFleet(transfer.PlayerId, expedition.endFleetUid);
                if (expedition.things.Any(t => destination.getThings().Any(d => d.getUID() == t.getUID()))) throw new InvalidOperationException("同一运输货物已存在目标舰队。");
                foreach (FWThingBean item in expedition.things) destination.getThings().Add(item);
                foreach (long uid in expedition.roleUidList)
                {
                    FWHeroBean hero = Players[transfer.PlayerId].heros.Single(h => h.getUID() == uid);
                    hero.starMapId = -1; hero.fleetUid = destination.getUID(); hero.airShipUid = -1;
                    if (!destination.getHeros().Any(h => h.getUID() == uid)) destination.AddHero(hero);
                    Players[transfer.PlayerId].heros.Remove(hero);
                }
                foreach (long uid in expedition.animalUidList)
                {
                    FWAnimalBean animal = Players[transfer.PlayerId].animals.Single(a => a.getUID() == uid);
                    animal.starMapId = -1; animal.fleetUid = destination.getUID(); animal.airShipUid = -1;
                    if (!destination.getAnimals().Any(a => a.getUID() == uid)) destination.AddAnimal(animal);
                    Players[transfer.PlayerId].animals.Remove(animal);
                }
                expedition.things.Clear(); Players[transfer.PlayerId].expeditionSeq.Remove(expedition);
                cargo.Delivered = true; transfer.NativeCargoJson = NativeWire.Json(cargo);
            });
        }
        public bool IsTransferComplete(TransferRecord transfer)
        {
            Cargo cargo = NativeWire.Parse<Cargo>(transfer.NativeCargoJson);
            if (cargo.Delivered) return true;
            long mapId = transfer.Returning ? transfer.Request.SourceMapId : transfer.Request.DestinationMapId;
            if (mapId < 0) return false;
            if (!IsNativeLandingComplete(transfer.NativeExpeditionId, mapId, cargo.Heroes)) return false;
            cargo.Delivered = true; transfer.NativeCargoJson = NativeWire.Json(cargo); return true;
        }
        public BridgeResult CancelTransfer(TransferRecord transfer)
        {
            return Run(() =>
            {
                if (transfer.Returning) return;
                FWExpedition expedition = FindCarrier(transfer);
                if (expedition == null || NativeLandings.ContainsKey(expedition.getUID())) throw new InvalidOperationException("运输已开始落仓，不能再次退回资产。");
                FWExpeditionGoalPos originalStart = expedition.startGoalPos;
                expedition.startGoalPos = expedition.endGoalPos; expedition.endGoalPos = originalStart;
                var route = ExpeditionTransportRouteUtils.Calculate(B_VehicleDef.getDataID(expedition.vehicleId), expedition.startGoalPos, expedition.endGoalPos);
                if (route.NeedSecond <= 0 || float.IsNaN(route.NeedSecond) || float.IsInfinity(route.NeedSecond)) throw new InvalidOperationException("原运输单无法生成返航路线。");
                using (EnterPlayer(transfer.PlayerId)) expedition.StartTravel(WorldTimeMM, route.NeedSecond, false);
                transfer.Returning = true; transfer.ArrivalWorldMM = expedition.endTime;
            });
        }
        internal void SubmitNativeTransfer(FWExpeditionGoalPos start, FWExpeditionGoalPos end, long missionUid,
            List<FWHeroBean> heroes, List<FWAnimalBean> animals, List<FWThingBean> things, string vehicleId,
            bool spaceElevator, callback completed)
        {
            if (start == null || end == null) throw new InvalidOperationException("运输缺少真实始发地/目的地。");
            if (!((start.goalType == (int)FWGoalPos.GoalType._STAR_MAP_INFO) || (start.goalType == (int)FWGoalPos.GoalType._FLEET)) || !((end.goalType == (int)FWGoalPos.GoalType._STAR_MAP_INFO) || (end.goalType == (int)FWGoalPos.GoalType._FLEET)))
                throw new InvalidOperationException("联机运输需要选择玩家地块或舰队。");
            var manifest = new Manifest { VehicleId = vehicleId, MissionUid = missionUid, UseSpaceElevator = spaceElevator,
                Heroes = (heroes ?? new List<FWHeroBean>()).Select(h => h.getUID()).ToList(),
                Animals = (animals ?? new List<FWAnimalBean>()).Select(a => a.getUID()).ToList(),
                Items = (things ?? new List<FWThingBean>()).Select(t => new CargoLine { Uid = t.getUID(), Count = t.getSum() }).ToList() };
            string id = Guid.NewGuid().ToString("N");
            var request = new TransferRequest { TransferId = id, SourceMapId = (start.goalType == (int)FWGoalPos.GoalType._STAR_MAP_INFO) ? start.getGoalUid() : -1,
                SourceFleetId = (start.goalType == (int)FWGoalPos.GoalType._FLEET) ? start.getGoalUid() : -1,
                DestinationMapId = (end.goalType == (int)FWGoalPos.GoalType._STAR_MAP_INFO) ? end.getGoalUid() : -1,
                DestinationFleetId = (end.goalType == (int)FWGoalPos.GoalType._FLEET) ? end.getGoalUid() : -1,
                HeroIds = manifest.Heroes.ToList(), NativeManifestJson = NativeWire.Json(manifest) };
            Context.RegisterCommandCallback(id, result =>
            {
                if (!result.Success || !result.Saved) { FairyUIDispatcher.showToast(result.Error ?? "运输尚未成功保存。"); return; }
                // The legacy success callback debits the source. Only the host is allowed to do that.
                // completedCallback refreshes presentation after the saved authoritative transaction.
                if (completed != null) completed();
                if (GalaxySceneExpeditions.instance != null) GalaxySceneExpeditions.instance.RefreshExpeditions();
            });
            CommandResult submitted = Context.SubmitCommand(new GameCommand { CommandId = id, Kind = CommandKind.DepartTransfer,
                MapId = request.SourceMapId, PayloadJson = JsonData.Write(request) });
            if (!submitted.Success && !submitted.Applied) FairyUIDispatcher.showToast(submitted.Error);
        }
    }

    internal static class NativeTransferHooks
    {
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(EarthSender), "createExpedition", "CreatePrefix");
            Patch(harmony, typeof(FWExpedition), "isTransformType_Space", "SpacePrefix");
            Patch(harmony, typeof(AirShipLocalData), "ExpeditionAutoOver", "AutoOverPrefix");
            Patch(harmony, typeof(Expedition_Drive), "check_Comback", "ReturnAutoPrefix");
            Patch(harmony, typeof(EarthSender), "send_Expedition_ChangeTargetToFleet", "CancelPrefix");
            Patch(harmony, typeof(EarthSender), "send_Expedition_ChangeTargetToStarMap", "TargetMapPrefix");
            foreach (MethodInfo method in typeof(LocalCoreHandle).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Expedition_Go"))
                harmony.Patch(method, new HarmonyMethod(typeof(NativeTransferHooks), "SurfacePrefix"));
        }
        private static void Patch(Harmony harmony, Type owner, string name, string prefix)
        {
            MethodInfo method = AccessTools.Method(owner, name);
            if (method == null) throw new MissingMethodException(owner.FullName, name);
            harmony.Patch(method, new HarmonyMethod(typeof(NativeTransferHooks), prefix));
        }
        private static bool SpacePrefix(FWExpedition __instance, ref bool __result)
        { if (Bridge == null || !Bridge.Context.IsManagedExpedition(__instance.getUID())) return true; __result = false; return false; }
        private static bool AutoOverPrefix(object[] __args)
        { FWExpedition e = __args.OfType<FWExpedition>().FirstOrDefault(); return Bridge == null || e == null || !Bridge.Context.IsManagedExpedition(e.getUID()); }
        private static bool ReturnAutoPrefix(object[] __args) { return AutoOverPrefix(__args); }
        private static bool CreatePrefix(object[] __args)
        {
            if (Bridge == null || Bridge.Seeding) return true;
            Bridge.SubmitNativeTransfer((FWExpeditionGoalPos)__args[0], (FWExpeditionGoalPos)__args[1], (long)__args[2],
                (List<FWHeroBean>)__args[3], (List<FWAnimalBean>)__args[4], (List<FWThingBean>)__args[5], (string)__args[6],
                (bool)__args[8], __args.Length > 10 ? __args[10] as callback : null);
            return false;
        }
        private static bool SurfacePrefix(object[] __args)
        {
            if (Bridge == null || Bridge.Seeding) return true;
            FWExpeditionGoalPos start = __args[6] as FWExpeditionGoalPos, end = __args[7] as FWExpeditionGoalPos;
            FWStarMapInfo startMap = __args[6] as FWStarMapInfo, endMap = __args[7] as FWStarMapInfo;
            if (startMap != null) start = new FWExpeditionGoalPos(FWBagGoalPos.BagType._无, FWGoalPos.GoalType._STAR_MAP_INFO, startMap.starMapId);
            if (endMap != null) end = new FWExpeditionGoalPos(FWBagGoalPos.BagType._无, FWGoalPos.GoalType._STAR_MAP_INFO, endMap.starMapId);
            FWMission mission = __args.Length > 8 ? __args[8] as FWMission : null;
            Bridge.SubmitNativeTransfer(start, end, mission == null ? -1 : mission.uid,
                (List<FWHeroBean>)__args[0], (List<FWAnimalBean>)__args[1], (List<FWThingBean>)__args[2],
                (string)__args[3], (bool)__args[5], __args.Length > 9 ? __args[9] as callback : null);
            return false;
        }
        private static bool CancelPrefix(object[] __args)
        {
            if (Bridge == null) return true;
            FWExpedition expedition = (FWExpedition)__args[0];
            TransferRecord transfer = Bridge.Context.GetTransfer(expedition.getUID());
            if (transfer == null) { if (Bridge.Context.IsManagedExpedition(expedition.getUID())) throw new InvalidOperationException("袭击须使用唯一撤离入口。"); return true; }
            SubmitCarrierCommand(transfer, CommandKind.CancelTransfer, __args.Length > 1 ? __args[1] as callbackInt : null);
            return false;
        }
        private static bool TargetMapPrefix(object[] __args)
        {
            if (Bridge == null) return true;
            FWExpedition expedition = (FWExpedition)__args[0];
            TransferRecord transfer = Bridge.Context.GetTransfer(expedition.getUID());
            if (transfer == null)
            {
                if (Bridge.Context.IsManagedExpedition(expedition.getUID())) throw new InvalidOperationException("本场袭击空投使用已登记的唯一战场落仓。");
                return true;
            }
            long expected = transfer.Returning ? transfer.Request.SourceMapId : transfer.Request.DestinationMapId;
            if ((long)__args[1] != expected) throw new InvalidOperationException("联机运输须沿用已保存的目的地；需要返航时请取消原运输单。");
            SubmitCarrierCommand(transfer, CommandKind.ArriveTransfer, __args.Length > 3 ? __args[3] as callbackInt : null);
            return false;
        }
        private static void SubmitCarrierCommand(TransferRecord transfer, CommandKind kind, callbackInt completed)
        {
            string id = Guid.NewGuid().ToString("N");
            Bridge.Context.RegisterCommandCallback(id, result =>
            {
                if (!result.Success || !result.Saved) { FairyUIDispatcher.showToast(result.Error ?? "运输变更尚未保存。"); return; }
                if (completed != null) completed(0);
                if (GalaxySceneExpeditions.instance != null) GalaxySceneExpeditions.instance.RefreshExpeditions();
            });
            Bridge.Context.SubmitCommand(new GameCommand { CommandId = id, Kind = kind, PayloadJson = JsonData.Write(new TransferReference { TransferId = transfer.TransferId }) });
        }
    }
}


