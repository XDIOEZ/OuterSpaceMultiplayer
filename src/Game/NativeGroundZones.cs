using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace FreeWorldMultiplayer
{
    // Explicit player intents run on the map simulator before native cells are changed.
    // Automatic building/life-zone maintenance and task completion keep native behavior.
    internal static class NativeGroundZones
    {
        internal sealed class Mutation
        {
            public string Schema = "GroundZones.V1";
            public string Operation;
            public long Target = -1;
            public string ZoneId;
            public int X0, Y0, X1, Y1, Limit, Selector;
            public long MergeBefore;
            public bool Flag;
            public List<FWPoint> Cells = new List<FWPoint>();
            public List<long> Workers = new List<long>();
        }
        private static GameBridge Bridge => GameBridge.Active;
        private static bool RouteIntent => Bridge != null && !Bridge.Applying && !Bridge.Seeding && !Bridge.Executing && NativeCommands.IsPlayerIntent;
        private static void Patch(Harmony harmony, Type type, string method, string prefix, Type[] args = null)
        {
            MethodInfo target = AccessTools.Method(type, method, args);
            if (target == null) throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target, new HarmonyMethod(typeof(NativeGroundZones), prefix));
        }
        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(MapZoneManager), "addZoneArea", nameof(AddAreaPrefix));
            foreach (string name in new[] { "Zone_AreaExpand_LN", "Zone_AreaReduce_LN", "Zone_AreaReduces_ByLevel_LN", "Zone_AreaReduces_BySubType_LN", "Zone_Remove" })
                Patch(harmony, typeof(LocalCoreHandle), name, nameof(MutationPrefix));
            Patch(harmony, typeof(SchedulingManuals), "addTask", nameof(TidyTaskPrefix));
            Patch(harmony, typeof(SchedulingManuals), "removeTask", nameof(TidyCancelPrefix), new[] { typeof(BaseZoneArea) });
            Patch(harmony, typeof(FWAreaZoneBean), "doTidyup", nameof(TidyFlagPrefix));
        }
        private static bool Submit(Mutation data)
        {
            if (data.Target >= 0 && !NativeGroundCommandOwnership.CanEdit(Bridge, Bridge.Context.LocalPlayerId, data.Target))
                throw new InvalidOperationException("只能修改或取消自己创建的区域。");
            Bridge.Route(CommandKind.ConfigureBuilding, -1, data.Target, data);
            return false;
        }
        private static bool AddAreaPrefix(FairyVector4Int int4, int remainLimitSum, string zoneId, long limieZoneUID)
        {
            if (!RouteIntent) return true;
            return Submit(new Mutation { Operation = "Add", ZoneId = zoneId, X0 = int4.rx0, Y0 = int4.ry0,
                X1 = int4.rx1, Y1 = int4.ry1, Limit = remainLimitSum, MergeBefore = limieZoneUID });
        }
        private static bool MutationPrefix(MethodBase __originalMethod, object[] __args)
        {
            if (!RouteIntent) return true;
            var data = new Mutation();
            switch (__originalMethod.Name)
            {
                case "Zone_Remove": data.Operation = "Remove"; data.Target = ((FWAreaZoneBean)__args[0]).getUID(); break;
                case "Zone_AreaExpand_LN": case "Zone_AreaReduce_LN":
                    data.Operation = __originalMethod.Name == "Zone_AreaExpand_LN" ? "Expand" : "Reduce";
                    data.Target = ((BaseZoneArea)__args[0]).zoneBean.getUID(); data.Cells = ((List<FWPoint>)__args[1]).ToList(); break;
                default:
                    data.Operation = __originalMethod.Name == "Zone_AreaReduces_ByLevel_LN" ? "EraseLevel" : "EraseSubtype";
                    data.Selector = (int)__args[0]; data.Cells = ((List<FWPoint>)__args[1]).ToList(); break;
            }
            return Submit(data);
        }
        private static bool TidyTaskPrefix(ZoneTidyupTask task)
        {
            if (!RouteIntent) return true;
            return Submit(new Mutation { Operation = "Tidy", Target = task.area.zoneBean.getUID(), Flag = true,
                Workers = task.heros.Select(h => h.getUID()).ToList() });
        }
        private static bool TidyCancelPrefix(BaseZoneArea area)
        { return !RouteIntent || Submit(new Mutation { Operation = "Tidy", Target = area.zoneBean.getUID(), Flag = false }); }
        private static bool TidyFlagPrefix(FWAreaZoneBean __instance, bool __0)
        { return !RouteIntent || Submit(new Mutation { Operation = "TidyFlag", Target = __instance.getUID(), Flag = __0 }); }
        internal static bool TryExecute(GameBridge bridge, GameCommand command)
        {
            if (command.Kind != CommandKind.ConfigureBuilding || JObject.Parse(command.PayloadJson ?? "{}").Value<string>("Schema") != "GroundZones.V1") return false;
            Mutation data = NativeWire.Parse<Mutation>(command.PayloadJson);
            if (data == null || data.Target != command.TargetEntityId || data.Cells == null || data.Workers == null || data.Workers.Count > 4096)
                throw new InvalidOperationException("区域操作数据无效。");
            UserFWStarSpread map = UserBean.getInstance().curStarMap;
            if (map.starMapId != command.MapId || !bridge.Context.HasPermission(command.PlayerId, command.MapId, -1, Permission.UseBuilding))
                throw new InvalidOperationException("没有当前地块的区域操作权限。");
            if (data.Cells.Count > (long)map.xLength * map.yLength || data.Cells.Any(p => p == null || p.x < 0 || p.y < 0 || p.x >= map.xLength || p.y >= map.yLength))
                throw new InvalidOperationException("区域坐标超出地图范围。");
            if (data.Operation == "Add") { AddArea(bridge, command.PlayerId, data, map); return true; }
            List<FWAreaZoneBean> targets;
            if (data.Operation == "EraseLevel" || data.Operation == "EraseSubtype")
                targets = map.zones.Where(z => NativeGroundCommandOwnership.CanEdit(bridge, command.PlayerId, z.getUID())
                    && (data.Operation == "EraseLevel" ? z.getDef().Level == data.Selector : z.getDef().SubType == data.Selector)
                    && data.Cells.Any(p => z.isInZone(p.x, p.y))).ToList();
            else
            {
                FWAreaZoneBean zone = map.zones.SingleOrDefault(z => z.getUID() == data.Target);
                if (zone == null || !NativeGroundCommandOwnership.CanEdit(bridge, command.PlayerId, data.Target))
                    throw new InvalidOperationException("区域已不存在，或不属于当前玩家。");
                targets = new List<FWAreaZoneBean> { zone };
            }
            List<BaseHero> workers = new List<BaseHero>();
            if (data.Operation == "Tidy")
                foreach (long uid in data.Workers.Distinct())
                {
                    BaseHero hero = MapManager.instance.heroManager.getHeroByUID(uid);
                    if (hero == null || !bridge.Context.HasPermission(command.PlayerId, command.MapId, uid, Permission.ControlOwnHero)
                        || !NativeGroundCommandOwnership.CanWorkZone(bridge, hero, targets[0]))
                        throw new InvalidOperationException("区域整理只能指派自己的雇员。");
                    workers.Add(hero);
                }
            foreach (FWAreaZoneBean zone in targets)
            {
                BaseZoneArea area = MapManager.instance.zoneManager.getBaseZoneArea_ByUID(zone.getUID());
                if (area == null) throw new InvalidOperationException("区域场景对象不存在。");
                switch (data.Operation)
                {
                    case "Remove": LocalCoreHandle.Zone_Remove(zone); break;
                    case "Expand":
                        if (data.Cells.Any(p => !zone.isInZone(p.x, p.y) && !MapManager.instance.zoneManager.couldCreateZoneArea(zone.allianceID, zone.id, p.x, p.y)))
                            throw new InvalidOperationException("区域扩张位置已被占用。");
                        LocalCoreHandle.Zone_AreaExpand_LN(area, data.Cells.Where(p => !zone.isInZone(p.x, p.y)).ToList()); break;
                    case "Reduce": case "EraseLevel": case "EraseSubtype":
                        LocalCoreHandle.Zone_AreaReduce_LN(area, data.Cells);
                        if (zone.cells.Count == 0) LocalCoreHandle.Zone_Remove(zone); break;
                    case "Tidy":
                        SchedulingManuals.removeTask(area);
                        if (data.Flag && workers.Count != 0) SchedulingManuals.addTask(new ZoneTidyupTask(area, workers));
                        zone.doTidyup(data.Flag); break;
                    case "TidyFlag": zone.doTidyup(data.Flag); break;
                    default: throw new InvalidOperationException("未知区域操作。");
                }
            }
            return true;
        }
        private static void AddArea(GameBridge bridge, string actor, Mutation data, UserFWStarSpread map)
        {
            string[] ids = { ZoneDef.ZONE_ID_PLANT, ZoneDef.ZONE_ID_STORE, ZoneDef.ZONE_ID_RUBBISH, ZoneDef.ZONE_ID_Transport, ZoneDef.ZONE_ID_LIFE, ZoneDef.ZONE_ID_Shepherd };
            if (!ids.Contains(data.ZoneId) || data.Limit < 1 || data.X0 < 0 || data.Y0 < 0 || data.X1 < data.X0 || data.Y1 < data.Y0 || data.X1 >= map.xLength || data.Y1 >= map.yLength)
                throw new InvalidOperationException("新增区域范围或类型无效。");
            var cells = new List<FWPoint>();
            var clearing = new List<FWPlanBean>();
            Control_Base.recyleVec4(new FairyVector4Int(data.X0, data.Y0, data.X1, data.Y1), (cx, cy) =>
            {
                if (cells.Count >= data.Limit || !MapManager.instance.controlManager.buildLimit.isInLimit(cx, cy)) return;
                if (MapManager.instance.zoneManager.couldCreateZoneArea(RoleAlliance.Alliance_MINE, data.ZoneId, cx, cy))
                {
                    cells.Add(new FWPoint(cx, cy));
                    if (PlanCreator.getFelling(cx, cy) != null)
                    {
                        var plan = new FWPlanBean(FWPlanBean.TASK_Felling, -1, "-1");
                        plan.setCell((short)cx, (short)cy); plan.setPlanStep(FWPlanBean.STEP_BLUEPRINT); clearing.Add(plan);
                    }
                }
            });
            if (cells.Count == 0) return;
            // Preserve native adjacency merging within one author. Adjacent foreign
            // zones keep their original UID, configuration, cells and author.
            var merge = new List<FWAreaZoneBean>();
            if (data.ZoneId == ZoneDef.ZONE_ID_PLANT || data.ZoneId == ZoneDef.ZONE_ID_STORE || data.ZoneId == ZoneDef.ZONE_ID_RUBBISH || data.ZoneId == ZoneDef.ZONE_ID_Transport)
                foreach (FWAreaZoneBean zone in map.zones.AsEnumerable().Reverse().ToList())
                    if (zone.id == data.ZoneId && zone.allianceID == RoleAlliance.Alliance_MINE && zone.isNewed()
                        && zone.getUID() < data.MergeBefore && NativeGroundCommandOwnership.CanEdit(bridge, actor, zone.getUID()) && zone.isCloset(cells))
                    { merge.Add(zone); cells.AddRange(zone.cells); }
            FWAreaZoneBean created = data.ZoneId == ZoneDef.ZONE_ID_PLANT ? (FWAreaZoneBean)new FWPlantAreaZoneBean(data.ZoneId, RoleAlliance.Alliance_MINE)
                : data.ZoneId == ZoneDef.ZONE_ID_Shepherd ? new FWAnimalAreaZoneBean(data.ZoneId, RoleAlliance.Alliance_MINE)
                : new FWAreaZoneBean(data.ZoneId, RoleAlliance.Alliance_MINE);
            created.setCells(cells);
            foreach (FWAreaZoneBean zone in merge) LocalCoreHandle.Zone_Remove(zone);
            foreach (FWPlanBean plan in clearing) LocalCoreHandle.Plan_addNewPlan_LN(plan);
            LocalCoreHandle.Zone_Add_LN(created);
        }
    }
}
