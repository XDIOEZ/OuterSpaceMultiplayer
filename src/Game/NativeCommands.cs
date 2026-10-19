using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace FreeWorldMultiplayer
{
    // Network operations map to this compiled allowlist, never an arbitrary reflected method name.
    internal static class NativeCommands
    {
        internal sealed class Argument { public string Kind; public long Uid; public string Value; }
        internal sealed class Invocation { public string Operation; public List<Argument> Arguments = new List<Argument>(); }
        internal sealed class Plans { public List<string> Add = new List<string>(); public List<long> Remove = new List<long>(); public List<string> Update = new List<string>(); }
        internal sealed class ProductionEdit { public long Building; public long Original; public string Draft; }
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();
        [ThreadStatic] private static int uiIntentDepth;
        internal static bool IsPlayerIntent { get { return uiIntentDepth > 0; } }
        internal static void Install(Harmony harmony)
        {
            Methods.Clear();
            string[] names = { "Product_SubmitMakeBean_LN", "Product_DelMakePlan_LN", "Product_MakeList_PlayPause", "Product_MakeList_TakeUp", "Product_WaterPlant_Submit_LN",
                "order_Hero_EatThing", "order_Hero_UseMedicial", "order_Hero_ElectricSwitch", "order_Hero_OpenMedic", "order_Hero_GoBed", "order_Fight_Stand", "order_Fight_Patrol" };
            foreach (Type owner in new[] { typeof(LocalCoreHandle), typeof(RoleCommand) })
                foreach (MethodInfo method in owner.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => names.Contains(m.Name)))
                {
                    if (method.GetParameters().Any(p => p.ParameterType.IsByRef || typeof(Delegate).IsAssignableFrom(p.ParameterType))) continue;
                    string key = owner.Name + "." + method.Name + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";
                    Methods.Add(key, method);
                    harmony.Patch(method, new HarmonyMethod(typeof(NativeCommands), "NativePrefix"));
                }
            harmony.Patch(AccessTools.Method(typeof(LocalCoreHandle), "Plan_CacheUpdate_LN"), new HarmonyMethod(typeof(NativeCommands), "PlanPrefix"));
            harmony.Patch(AccessTools.Method(typeof(BaseRole), "doAction", new[] { typeof(RolePlan) }), new HarmonyMethod(typeof(NativeCommands), "WorkPrefix"));
            harmony.Patch(AccessTools.Method(typeof(LocalCoreHandle), "Product_EditMakeBean_LN"), new HarmonyMethod(typeof(NativeCommands), "ProductionEditPrefix"));
            Type[] roots = { typeof(GraphUI_Base), typeof(MainUI_Mod_Base), typeof(Hero_MainView_Cmd), typeof(ProductSettingV4_MakeItem), typeof(GameUI_SpeedControl), typeof(RightClickHandler), typeof(Control_Base), typeof(MainUI), typeof(ViewBuild_Fctions), typeof(ViewZone_Fctions), typeof(Dialog_Animal) };
            foreach (Type type in typeof(GraphUI_Base).Assembly.GetTypes().Where(t => roots.Any(r => r.IsAssignableFrom(t)))) InstallUiScope(harmony, type);
        }
        private static void InstallUiScope(Harmony harmony, Type type)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (!method.IsAbstract && !method.IsGenericMethodDefinition && method.ReturnType == typeof(void) && method.GetMethodBody() != null)
                    harmony.Patch(method, new HarmonyMethod(typeof(NativeCommands), "UiEnter"), finalizer: new HarmonyMethod(typeof(NativeCommands), "UiLeave"));
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) InstallUiScope(harmony, nested);
        }
        private static void UiEnter() { uiIntentDepth++; }
        private static Exception UiLeave(Exception __exception) { uiIntentDepth--; return __exception; }
        private static bool WorkPrefix(BaseRole __instance, RolePlan plan)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || bridge.Applying || bridge.Executing) return true;
            if (!IsPlayerIntent)
            {
                if (!bridge.IsSimulator) return false;
                return !NativeGroundInteraction.IsWorkPlan(plan) || bridge.CanPerformGroundWork(__instance, plan);
            }
            if (plan.planBean != null && plan.planBean.taskType == FWPlanBean.TASK_CARRY
                && bridge.FindPersistentBean(plan.planBean.objectUID) is FWThingBean)
                return bridge.Route(CommandKind.TakeItem, __instance.getUID(), plan.planBean.objectUID,
                    new { Count = ((FWThingBean)bridge.FindPersistentBean(plan.planBean.objectUID)).getSum() });
            SchedulingResult.Append("操作已提交，由地块模拟者执行。");
            long planUid = plan.planBean == null ? -1 : plan.planBean.getUID();
            return bridge.Route(CommandKind.Work, __instance.getUID(), planUid >= 0 && bridge.Context.OwnerOfEntity(planUid) != null ? planUid : -1,
                new { PlanId = plan.planBean == null ? -1 : plan.planBean.getUID(), NativePlan = plan.planBean == null ? null : NativeWire.Pack(plan.planBean) });
        }
        private static bool PlanPrefix()
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || bridge.Applying || bridge.Executing) return true;
            if (!IsPlayerIntent) return bridge.IsSimulator;
            var packet = new Plans();
            var add = (IEnumerable)AccessTools.Field(typeof(LocalCoreHandle), "newPlans").GetValue(null);
            foreach (RolePlan plan in add) packet.Add.Add(NativeWire.Pack(plan.planBean));
            var remove = (IEnumerable)AccessTools.Field(typeof(LocalCoreHandle), "removeOldPlans").GetValue(null);
            foreach (RolePlan plan in remove) packet.Remove.Add(plan.planBean.getUID());
            foreach (var update in LocalCoreHandle.updateOldPlans)
                if (!packet.Remove.Contains(update.Item1.planBean.getUID())) packet.Update.Add(NativeWire.Pack(update.Item1.planBean));
            if (packet.Add.Count == 0 && packet.Remove.Count == 0 && packet.Update.Count == 0) return false;
            // Restore the actual baseline before a host-local command can execute synchronously.
            // Clearing after Submit would remove the newly authoritative plan as if it were a preview.
            LocalCoreHandle.Plan_CacheClear_LN();
            return bridge.Route(CommandKind.Build, -1, -1, packet);
        }
        private static bool NativePrefix(MethodBase __originalMethod, object[] __args)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || bridge.Applying || bridge.Executing) return true;
            string operation = Methods.Single(p => p.Value == __originalMethod).Key;
            Invocation packet = new Invocation { Operation = operation };
            long actor = -1, target = -1;
            foreach (object value in __args)
            {
                if (value is SpriteObject)
                {
                    long uid = ((SpriteObject)value).getUID();
                    if (value is BaseRole) actor = uid; else target = uid;
                    packet.Arguments.Add(new Argument { Kind = "scene", Uid = uid });
                }
                else if (value is BaseThing)
                {
                    long uid = ((BaseThing)value).getUID(); target = uid;
                    packet.Arguments.Add(new Argument { Kind = "scene", Uid = uid });
                }
                else if (value is FWUidItem)
                {
                    long uid = NativeWire.Uid(value); target = uid;
                    packet.Arguments.Add(new Argument { Kind = "bean", Uid = uid, Value = NativeWire.Pack(value) });
                }
                else packet.Arguments.Add(new Argument { Kind = "value", Value = NativeWire.Pack(value) });
            }
            return bridge.Route(CommandKind.Native, actor, target, packet);
        }
        private static bool ProductionEditPrefix(Produce_Base produce, FWMakeBean original, FWMakeBean draft, ref string reason, ref bool __result)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || bridge.Applying || bridge.Executing) return true;
            bridge.Route(CommandKind.ConfigureBuilding, -1, produce.getUID(), new ProductionEdit {
                Building = produce.getUID(), Original = original.getUID(), Draft = NativeWire.Pack(draft) });
            reason = "生产修改已提交，等待地块模拟者确认。"; __result = false; return false;
        }
        internal static void Execute(GameBridge bridge, GameCommand command)
        {
            if (command.Kind == CommandKind.ConfigureBuilding)
            {
                if (!bridge.Context.HasPermission(command.PlayerId, command.MapId, command.TargetEntityId, Permission.UseBuilding)) throw new InvalidOperationException("Production configuration denied.");
                ProductionEdit edit = NativeWire.Parse<ProductionEdit>(command.PayloadJson);
                if (edit.Building != command.TargetEntityId) throw new InvalidOperationException("Production target mismatch.");
                Produce_Base produce = bridge.FindSceneObject(edit.Building) as Produce_Base;
                if (produce == null) throw new InvalidOperationException("生产设施已不存在。");
                FWMakeBean original = produce.productTableBean.getMakeList().makeList.SingleOrDefault(m => m.getUID() == edit.Original);
                if (original == null) throw new InvalidOperationException("生产计划已结束或被删除。");
                string reason;
                if (!LocalCoreHandle.Product_EditMakeBean_LN(produce, original, NativeWire.Unpack<FWMakeBean>(edit.Draft), out reason)) throw new InvalidOperationException(reason);
                return;
            }
            if (command.Kind == CommandKind.Build || command.Kind == CommandKind.Deconstruct)
            {
                if (!bridge.Context.HasPermission(command.PlayerId, command.MapId, -1, Permission.UseBuilding)) throw new InvalidOperationException("Construction permission denied.");
                Plans plans = NativeWire.Parse<Plans>(command.PayloadJson);
                List<FWPlanBean> added = plans.Add.Select(NativeWire.Unpack<FWPlanBean>).ToList();
                List<FWPlanBean> updated = plans.Update.Select(NativeWire.Unpack<FWPlanBean>).ToList();
                if (plans.Remove.Distinct().Count() != plans.Remove.Count || added.Concat(updated).Select(p => p.getUID()).Distinct().Count() != added.Count + updated.Count)
                    throw new InvalidOperationException("Duplicate construction plan mutation.");
                foreach (long uid in plans.Remove.Concat(updated.Select(p => p.getUID())))
                    if (MapManager.instance.planManager.getPlanGraphByUID(uid) == null) throw new InvalidOperationException("Plan to change does not exist.");
                foreach (FWPlanBean plan in added)
                    if (MapManager.instance.planManager.getPlanGraphByUID(plan.getUID()) != null) throw new InvalidOperationException("New plan already exists.");
                foreach (long uid in plans.Remove)
                    bridge.ValidateGroundPlanMutation(command.PlayerId, MapManager.instance.planManager.getPlanGraphByUID(uid).rolePlan.planBean, false);
                foreach (FWPlanBean plan in updated) bridge.ValidateGroundPlanMutation(command.PlayerId, plan, false);
                foreach (FWPlanBean plan in added) bridge.ValidateGroundPlanMutation(command.PlayerId, plan, true);
                foreach (long uid in plans.Remove)
                {
                    BasePlanGraph graph = MapManager.instance.planManager.getPlanGraphByUID(uid);
                    if (graph == null) throw new InvalidOperationException("Plan to remove does not exist.");
                    LocalCoreHandle.Plan_removePlan_LN(graph.rolePlan);
                }
                foreach (FWPlanBean plan in updated)
                {
                    BasePlanGraph current = MapManager.instance.planManager.getPlanGraphByUID(plan.getUID());
                    UserBean.getInstance().curStarMap.removePlan(current.rolePlan); // Move/update preserves held materials; no refund.
                    plan.setNewPlan(false); plan.setPlanStep(FWPlanBean.STEP_BLUEPRINT);
                    LocalCoreHandle.Plan_addNewPlan_LN(plan);
                }
                foreach (FWPlanBean plan in added)
                {
                    plan.setNewPlan(false); plan.setPlanStep(FWPlanBean.STEP_BLUEPRINT);
                    LocalCoreHandle.Plan_addNewPlan_LN(plan);
                }
                return;
            }
            if (command.Kind == CommandKind.Work)
            {
                if (!bridge.Context.HasPermission(command.PlayerId, command.MapId, command.EntityId, Permission.ControlOwnHero)) throw new InvalidOperationException("Cannot assign another player's employee.");
                BaseHero hero = MapManager.instance.heroManager.getHeroByUID(command.EntityId);
                BasePlanGraph graph = MapManager.instance.planManager.getPlanGraphByUID(command.TargetEntityId);
                if (graph == null)
                {
                    JObject payload = JObject.Parse(command.PayloadJson);
                    string encoded = payload.Value<string>("NativePlan");
                    if (!string.IsNullOrEmpty(encoded))
                    {
                        FWPlanBean plan = NativeWire.Unpack<FWPlanBean>(encoded);
                        if (command.TargetEntityId >= 0 && plan.getUID() != command.TargetEntityId)
                            throw new InvalidOperationException("Work plan identity mismatch.");
                        graph = MapManager.instance.planManager.getPlanGraphByUID(plan.getUID());
                        bridge.ValidateGroundPlanMutation(command.PlayerId, graph == null ? plan : graph.rolePlan.planBean, graph == null);
                        if (graph == null) graph = LocalCoreHandle.Plan_addNewPlan_LN(plan);
                    }
                }
                if (hero == null || graph == null) throw new InvalidOperationException("Worker or plan is no longer present.");
                bridge.ValidateGroundPlanMutation(command.PlayerId, graph.rolePlan.planBean, false);
                if (!bridge.CanPerformGroundWork(hero, graph.rolePlan)) throw new InvalidOperationException("当前关系不允许该雇员执行这项工作。");
                hero.doAction(graph.rolePlan); return;
            }
            Invocation invocation = NativeWire.Parse<Invocation>(command.PayloadJson);
            MethodInfo method;
            if (!Methods.TryGetValue(invocation.Operation, out method)) throw new InvalidOperationException("Unrecognized native operation.");
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != invocation.Arguments.Count) throw new InvalidOperationException("Native operation argument count changed.");
            if (!bridge.Context.HasPermission(command.PlayerId, command.MapId, command.TargetEntityId, Permission.UseBuilding)) throw new InvalidOperationException("Native operation permission denied.");
            object[] args = new object[parameters.Length];
            for (int i = 0; i < args.Length; i++)
            {
                Argument arg = invocation.Arguments[i]; Type type = parameters[i].ParameterType;
                if (arg.Kind == "scene") args[i] = bridge.FindSceneObject(arg.Uid);
                else if (arg.Kind == "bean") args[i] = bridge.FindPersistentBean(arg.Uid) ?? NativeWire.Unpack(arg.Value, type);
                else if (arg.Kind == "value") args[i] = NativeWire.Unpack(arg.Value, type);
                else throw new InvalidOperationException("Unrecognized argument token.");
                if (args[i] != null && !type.IsInstanceOfType(args[i])) throw new InvalidOperationException("Native operation argument type mismatch.");
                if (args[i] is BaseRole && !bridge.Context.HasPermission(command.PlayerId, command.MapId, ((BaseRole)args[i]).getUID(), Permission.ControlOwnHero)) throw new InvalidOperationException("Cannot command another player's employee.");
            }
            method.Invoke(null, args);
        }
    }
}
