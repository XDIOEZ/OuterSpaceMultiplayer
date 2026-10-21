using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Script.FairyScripts.ALibrary.Astar;

namespace FreeWorldMultiplayer
{
    // Author identity uses the existing persisted/networked UID ownership table.
    // Friendship never grants production or mutation permission. Pickup uses the
    // independent loot rule so a legal raid is not treated as helping the owner.
    internal static class NativeGroundCommandOwnership
    {
        [ThreadStatic] private static BaseRole candidateWorker;
        private static GameBridge Bridge => GameBridge.Active;
        private static readonly FieldInfo PutGrid = AccessTools.Field(typeof(PickAction_PutBase), "toGridNode");
        private static readonly FieldInfo PutBuild = AccessTools.Field(typeof(PickAction_PutBase), "toBuild");
        internal static string Owner(GameBridge bridge, long uid)
        { return bridge.Context.OwnerOfEntity(uid) ?? bridge.Context.OwnerOfMap(bridge.CurrentMapId); }
        internal static bool CanEdit(GameBridge bridge, string actor, long uid)
        { return bridge.CanOperateGround(actor, bridge.CurrentMapId) && !string.IsNullOrEmpty(actor) && actor == Owner(bridge, uid); }
        internal static bool CanWorkZone(GameBridge bridge, BaseRole worker, FWAreaZoneBean zone)
        {
            if (worker == null || zone == null) return true;
            string owner = bridge.Context.OwnerOfEntity(zone.getUID());
            // Box storage zones are nested records: their enclosing real building owns them.
            if (owner == null && MapManager.isInitFinish)
                foreach (var box in MapManager.instance.buildManager.furnManager.getBoxs(worker.getAlliance()))
                    if (ReferenceEquals(box.boxBean.areaZoneBean, zone)) { owner = Owner(bridge, box.getUID()); break; }
            return bridge.CanShareGroundWork(bridge.Context.OwnerOfEntity(worker.getUID()), owner ?? bridge.Context.OwnerOfMap(bridge.CurrentMapId));
        }
        internal static bool CanWorkPlanZones(GameBridge bridge, BaseRole worker, RolePlan plan)
        {
            if (plan.getTaskType() == FWPlanBean.TASK_Grow)
            {
                FWAreaZoneBean zone = PlantZone(plan);
                if (zone != null && !CanWorkZone(bridge, worker, zone)) return false;
            }
            ActionStruct_Carry carry = plan.actionStruct as ActionStruct_Carry;
            string actor = worker == null ? null : bridge.Context.OwnerOfEntity(worker.getUID());
            if (carry != null && carry.carryThing != null && !bridge.CanTakeGroundThing(actor, carry.carryThing)) return false;
            PickActionGroup picks = plan.pickAction ?? (carry == null ? null : carry.pickAction);
            if (picks != null)
            {
                foreach (PickAction_PickBase pick in picks.picks)
                {
                    BaseThing thing = pick.takeThing == null ? null : pick.takeThing.getThing();
                    if (thing != null && !bridge.CanTakeGroundThing(actor, thing)) return false;
                }
                foreach (PickAction_PutBase put in picks.puts)
                {
                    BaseBuild build = PutBuild.GetValue(put) as BaseBuild;
                    if (build != null && !bridge.CanUseGroundWorkTarget(worker, build.getUID())) return false;
                    GridNode grid = PutGrid.GetValue(put) as GridNode;
                    if (grid != null)
                        foreach (FWAreaZoneBean zone in UserBean.getInstance().curStarMap.zones)
                            if (ZoneDef.isStoreType(zone.id) && zone.isInZone(grid.cx, grid.cy) && !CanWorkZone(bridge, worker, zone)) return false;
                }
            }
            return true;
        }
        internal static FWAreaZoneBean PlantZone(RolePlan plan)
        {
            FWAreaZoneBean zone = plan.zonePoint == null ? null : plan.zonePoint.getFwAreaZoneBean();
            return zone ?? UserBean.getInstance().curStarMap.zones.FirstOrDefault(z => z is FWPlantAreaZoneBean && z.isInZone(plan.getCellX(), plan.getCellY()));
        }
        private static void Patch(Harmony harmony, Type type, string name, string prefix = null, string postfix = null, Type[] args = null)
        {
            MethodInfo method = AccessTools.Method(type, name, args);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            harmony.Patch(method, prefix == null ? null : new HarmonyMethod(typeof(NativeGroundCommandOwnership), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(NativeGroundCommandOwnership), postfix));
        }
        internal static void Install(Harmony harmony)
        {
            candidateWorker = null;
            Patch(harmony, typeof(LocalCoreHandle), "Plan_removePlanFormCache", nameof(CachePlanPrefix));
            Patch(harmony, typeof(LocalCoreHandle), "Plan_updateFormCache", nameof(CachePlanPrefix));
            foreach (MethodInfo method in typeof(LocalCoreHandle).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Plan_removePlan_LN"))
                harmony.Patch(method, new HarmonyMethod(typeof(NativeGroundCommandOwnership), nameof(RemovePlanPrefix)));
            Patch(harmony, typeof(PlanCreator), "getTopPlanGraph", nameof(TopPlanPrefix));
            // Pickup mutates ready/step/grid state after cache registration returns.
            // Stop the whole edit operation before it can mutate a foreign plan.
            foreach (string name in new[] { "doDecor_pickup", "doDecor_putdown", "doDecor_PointerPress", "doDecor_PointerMove", "doDecor_PointerRelease", "changeOri" })
                Patch(harmony, typeof(BasePlanGraph), name, nameof(GraphEditPrefix));
            Patch(harmony, typeof(UserFWStarSpread), "addAreaZone", postfix: nameof(ZoneAdded), args: new[] { typeof(FWAreaZoneBean) });
            Patch(harmony, typeof(MapZoneManager), "canPlant", nameof(PlantPrefix));
            Patch(harmony, typeof(FWAreaZoneBean), "getFreeThingPoint", nameof(FreePointPrefix), args: new[] { typeof(BaseRole) });
            Patch(harmony, typeof(ZoneUtils), "getStore", postfix: nameof(StoreCandidatesPostfix), args: new[] { typeof(sbyte) });
            Patch(harmony, typeof(MapZoneManager), "getAreaZones", postfix: nameof(AreaCandidatesPostfix));
            // Native schedulers call the alliance-only storage cache; scope the real worker
            // while returning a filtered copy, never remove entries from that shared cache.
            foreach (MethodInfo method in typeof(SchedulingCommons).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (method.GetParameters().Any(p => typeof(BaseRole).IsAssignableFrom(p.ParameterType)) && method.GetMethodBody() != null)
                    harmony.Patch(method, new HarmonyMethod(typeof(NativeGroundCommandOwnership), nameof(WorkerEnter)),
                        finalizer: new HarmonyMethod(typeof(NativeGroundCommandOwnership), nameof(WorkerLeave)));
        }
        private static bool CachePlanPrefix(object[] __args)
        {
            if (Bridge == null || Bridge.Applying || !NativeCommands.IsPlayerIntent) return true;
            if (!Bridge.CanOperateGround(Bridge.ActingPlayer ?? Bridge.Context.LocalPlayerId, Bridge.CurrentMapId)) return false;
            RolePlan plan = __args.OfType<RolePlan>().FirstOrDefault();
            if (plan == null || plan.planBean == null || plan.planBean.IsNewPlan) return true;
            return CanEdit(Bridge, Bridge.ActingPlayer ?? Bridge.Context.LocalPlayerId, plan.planBean.getUID());
        }
        private static bool RemovePlanPrefix(object[] __args)
        {
            if (Bridge == null || Bridge.Applying || Bridge.Executing || !NativeCommands.IsPlayerIntent) return true;
            IEnumerable<RolePlan> plans = __args[0] is RolePlan ? new[] { (RolePlan)__args[0] } : (IEnumerable<RolePlan>)__args[0];
            var packet = new NativeCommands.Plans();
            foreach (RolePlan plan in plans)
                if (plan != null && plan.planBean != null && CanEdit(Bridge, Bridge.Context.LocalPlayerId, plan.planBean.getUID())) packet.Remove.Add(plan.planBean.getUID());
            if (packet.Remove.Count != 0) Bridge.Route(CommandKind.Build, -1, -1, packet);
            return false;
        }
        private static bool GraphEditPrefix(BasePlanGraph __instance)
        {
            if (Bridge == null || Bridge.Applying || Bridge.Executing || !NativeCommands.IsPlayerIntent || __instance.rolePlan == null || __instance.rolePlan.planBean == null) return true;
            if (!Bridge.CanOperateGround(Bridge.Context.LocalPlayerId, Bridge.CurrentMapId)) return false;
            return __instance.rolePlan.planBean.IsNewPlan || CanEdit(Bridge, Bridge.Context.LocalPlayerId, __instance.rolePlan.planBean.getUID());
        }
        private static void TopPlanPrefix(ref List<BasePlanGraph> graphs)
        {
            if (Bridge == null || Bridge.Applying || !NativeCommands.IsPlayerIntent || graphs == null) return;
            if (!Bridge.CanOperateGround(Bridge.ActingPlayer ?? Bridge.Context.LocalPlayerId, Bridge.CurrentMapId))
            { graphs = new List<BasePlanGraph>(); return; }
            graphs = graphs.Where(g => g != null && g.rolePlan != null && g.rolePlan.planBean != null
                && (g.rolePlan.planBean.IsNewPlan || CanEdit(Bridge, Bridge.ActingPlayer ?? Bridge.Context.LocalPlayerId, g.rolePlan.planBean.getUID()))).ToList();
        }
        private static void ZoneAdded(UserFWStarSpread __instance, FWAreaZoneBean __0)
        {
            if (Bridge == null || Bridge.Applying || Bridge.Seeding || !Bridge.IsSimulator || __0 == null || !__instance.zones.Contains(__0)) return;
            if (Bridge.Context.OwnerOfEntity(__0.getUID()) == null)
                Bridge.Context.RegisterEntityOwner(__0.getUID(), Bridge.GroundMapAssetOwner(__0, __instance.starMapId));
        }
        private static bool PlantPrefix(BaseRole role, FWPlantAreaZoneBean zoneBean, ref bool __result)
        { if (Bridge == null || Bridge.Applying || CanWorkZone(Bridge, role, zoneBean)) return true; __result = false; return false; }
        private static bool FreePointPrefix(FWAreaZoneBean __instance, BaseRole role, ref FWPoint __result)
        { if (Bridge == null || Bridge.Applying || CanWorkZone(Bridge, role, __instance)) return true; __result = null; return false; }
        private static void StoreCandidatesPostfix(ref List<FWAreaZoneBean> __result)
        {
            if (Bridge != null && !Bridge.Applying && candidateWorker != null && __result != null)
                __result = __result.Where(z => CanWorkZone(Bridge, candidateWorker, z)).ToList();
        }
        private static void AreaCandidatesPostfix(ref List<BaseZoneArea> __result)
        {
            if (Bridge != null && !Bridge.Applying && candidateWorker != null && __result != null)
            {
                string actor = Bridge.Context.OwnerOfEntity(candidateWorker.getUID());
                // Source-zone discovery may see a defeated owner's storage.
                // Destination discovery/free slots still require own storage.
                __result = __result.Where(z => z != null && (CanWorkZone(Bridge, candidateWorker, z.zoneBean)
                    || ZoneDef.isStoreType(z.zoneBean.id) && Bridge.CanTakeGroundZone(actor, z.zoneBean))).ToList();
            }
        }
        private static void WorkerEnter(object[] __args, out BaseRole __state)
        { __state = candidateWorker; BaseRole role = __args.OfType<BaseRole>().FirstOrDefault(); if (role != null) candidateWorker = role; }
        private static Exception WorkerLeave(Exception __exception, BaseRole __state)
        { candidateWorker = __state; return __exception; }
    }
}
