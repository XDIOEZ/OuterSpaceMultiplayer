using System;
using System.Reflection;
using HarmonyLib;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        // The native alliance remains untouched. Work belongs to a plan or facility,
        // while an employee always belongs to their original PlayerId.
        internal bool CanShareGroundWork(string worker, string owner)
        {
            return string.IsNullOrEmpty(worker) || string.IsNullOrEmpty(owner) || worker == owner;
        }

        internal bool CanUseGroundWorkTarget(BaseRole worker, long target)
        {
            return worker == null || CanShareGroundWork(Context.OwnerOfEntity(worker.getUID()),
                Context.OwnerOfEntity(target) ?? Context.OwnerOfMap(CurrentMapId));
        }

        internal bool CanPerformGroundWork(BaseRole worker, RolePlan plan)
        {
            if (worker == null || plan == null) return true;
            string actor = Context.OwnerOfEntity(worker.getUID());
            if (string.IsNullOrEmpty(actor)) return true;
            ActionStruct_Resuce rescue = plan.actionStruct as ActionStruct_Resuce;
            if (rescue != null && rescue.wounderRole != null)
            {
                string rescued = Context.OwnerOfEntity(rescue.wounderRole.getUID());
                return string.IsNullOrEmpty(rescued) || actor == rescued
                    || (Context.IsFriendlyToward != null && Context.IsFriendlyToward(actor, rescued));
            }
            if (!NativeGroundCommandOwnership.CanWorkPlanZones(this, worker, plan)) return false;
            ActionStruct_Carry carrying = plan.actionStruct as ActionStruct_Carry;
            if (carrying != null || plan.getTaskType() == FWPlanBean.TASK_CARRY)
            {
                string orderOwner = plan.planBean == null ? null : Context.OwnerOfEntity(plan.planBean.getUID());
                if (!CanShareGroundWork(actor, orderOwner)) return false;
                if (carrying != null && carrying.pickAction != null)
                    foreach (PickAction_PutBase put in carrying.pickAction.puts)
                        if (put.toAssistPlan != null && !CanPerformGroundWork(worker, put.toAssistPlan)) return false;
                return true;
            }
            if (plan.planBean == null && plan.getTaskType() == FWPlanBean.TASK_Grow && NativeGroundCommandOwnership.PlantZone(plan) != null)
                return true; // Generated planting work follows its real zone, including an own zone on a visited map.
            BaseBuild assignedBuilding = plan.getPlanBuild();
            if (assignedBuilding != null && !NativeGroundInteraction.IsPublicFieldWork(plan.getTaskType())
                && !CanUseGroundWorkTarget(worker, assignedBuilding.getUID())) return false;
            FWPlanBean bean = plan.planBean;
            if (bean != null)
            {
                string author = NativeGroundCommandOwnership.Owner(this, bean.getUID());
                if (!CanShareGroundWork(actor, author)) return false;
                // A new own demolition/refuel order cannot grant access to a foreign
                // building merely by placing the actor's identity on its plan.
                string targetOwner = bean.objectUID > 0 ? Context.OwnerOfEntity(bean.objectUID) : null;
                if (!NativeGroundInteraction.IsPublicFieldWork(plan.getTaskType()) && plan.getTaskType() != FWPlanBean.TASK_CARRY
                    && !CanShareGroundWork(actor, targetOwner)) return false;
                if (author != null || targetOwner != null) return true;
            }
            if (assignedBuilding != null) return true;
            ActionStruct_ProductTable production = plan.actionStruct as ActionStruct_ProductTable;
            if (production != null && production.produce != null)
                return CanUseGroundWorkTarget(worker, production.produce.getUID());
            ActionStruct_BuildAddMaterials supply = plan.actionStruct as ActionStruct_BuildAddMaterials;
            if (supply != null && supply.build != null) return CanUseGroundWorkTarget(worker, supply.build.getUID());
            ActionStruct_BuildRepair repair = plan.actionStruct as ActionStruct_BuildRepair;
            if (repair != null && repair.build != null) return CanUseGroundWorkTarget(worker, repair.build.getUID());
            ActionStruct_Table table = plan.actionStruct as ActionStruct_Table;
            BaseBuild furniture = table == null ? null : table.furniture as BaseBuild;
            if (furniture != null) return CanUseGroundWorkTarget(worker, furniture.getUID());
            // Existing unregistered map work keeps its original map ownership.
            // New persistent plans are registered at the insertion boundary below.
            return !NativeGroundInteraction.IsWorkPlan(plan)
                || NativeGroundInteraction.IsPublicFieldWork(plan.getTaskType())
                || CanShareGroundWork(actor, Context.OwnerOfMap(CurrentMapId));
        }

        internal void ValidateGroundPlanMutation(string playerId, FWPlanBean plan, bool creating)
        {
            if (plan == null) throw new InvalidOperationException("任务数据不存在。");
            string baseOwner = Context.OwnerOfMap(CurrentMapId);
            if (creating && !string.IsNullOrEmpty(baseOwner) && baseOwner != playerId
                && !NativeGroundInteraction.IsPublicFieldWork(plan.taskType)
                && plan.taskType != FWPlanBean.TASK_CARRY && plan.taskType != FWPlanBean.TASK_RescueTarget)
                throw new InvalidOperationException("不能在其他玩家的基地发布建造、种植或生产管理命令。");
            string author = Context.OwnerOfEntity(plan.getUID());
            if (creating ? author != null && author != playerId : !NativeGroundCommandOwnership.CanEdit(this, playerId, plan.getUID()))
                throw new InvalidOperationException("只能修改或取消自己发布的命令。");
            string target = plan.objectUID > 0 ? Context.OwnerOfEntity(plan.objectUID) : null;
            if (plan.taskType == FWPlanBean.TASK_RescueTarget && target != null
                && (playerId == target || Context.IsFriendlyToward != null && Context.IsFriendlyToward(playerId, target))) return;
            if (!NativeGroundInteraction.IsPublicFieldWork(plan.taskType) && plan.taskType != FWPlanBean.TASK_CARRY
                && !CanShareGroundWork(playerId, target))
                throw new InvalidOperationException("当前关系不允许对该玩家的设施安排工作。");
        }

        internal string GroundMapAssetOwner(FWBaseBean entity, long mapId)
        {
            if (NativeGroundInteraction.ConstructionOwner != null) return NativeGroundInteraction.ConstructionOwner;
            FWPlanBean plan = entity as FWPlanBean;
            if (Executing || NativeCommands.IsPlayerIntent) return ActingPlayer ?? Context.LocalPlayerId;
            // Automatic supply/repair plans follow the target facility; finishing a
            // building is scoped to its original plan, never its friendly helper.
            string target = plan != null && plan.objectUID > 0 ? Context.OwnerOfEntity(plan.objectUID) : null;
            return target ?? Context.OwnerOfMap(mapId) ?? ActingPlayer ?? Context.LocalPlayerId;
        }

        internal void ReleaseIncompatibleGroundWork()
        {
            if (!IsSimulator || !IsMapReady(CurrentMapId)) return;
            foreach (BaseHero hero in MapManager.instance.heroManager.getAllRoles()) ReleaseIncompatibleGroundRole(hero);
            foreach (BaseAnimal animal in MapManager.instance.animalManager.getAnimals()) ReleaseIncompatibleGroundRole(animal);
        }

        private void ReleaseIncompatibleGroundRole(BaseRole role)
        {
            AI_Base ai = role == null ? null : role.getRunAI();
            BaseAction action = ai == null ? null : ai.runAction;
            if (action != null && action.actionType == BaseAction.TYPE_WORK && !CanPerformGroundWork(role, action.plan))
                role.doInterruptAction(); // Native interruption releases plan/material/seat reservations.
            AI_BaseFight fight = ai as AI_BaseFight;
            SpriteObject target = fight == null ? null : fight.getTargetEnemy();
            if (target == null) return;
            string actorOwner = Context.OwnerOfEntity(role.getUID()), targetOwner = Context.OwnerOfEntity(target.getUID());
            if (actorOwner != null && targetOwner != null && actorOwner != targetOwner
                && Context.GetRelation(actorOwner, targetOwner) != Relation.Hostile)
            { fight.setTargetEnemy(null); fight.doStand(); }
        }
    }

    internal static class NativeGroundInteraction
    {
        [ThreadStatic] internal static string ConstructionOwner;
        private static readonly FieldInfo TargetLock = AccessTools.Field(typeof(AI_BaseFight), "isTargetLock");
        private static GameBridge Bridge => GameBridge.Active;

        // Gathering and demolition remain legal on a visited base. Their resulting
        // items still pass the separate pickup/loot checks, never work-sharing.
        internal static bool IsPublicFieldWork(int task)
        {
            switch (task)
            {
                case FWPlanBean.TASK_Felling: case FWPlanBean.TASK_Mining: case FWPlanBean.TASK_Harvesty:
                case FWPlanBean.TASK_Weeding: case FWPlanBean.TASK_Destroy: case FWPlanBean.TASK_RemoveRoof:
                case FWPlanBean.TASK_Uninstall: case FWPlanBean.TASK_Ore_Destroy: case FWPlanBean.TASK_FISHING: return true;
                default: return false;
            }
        }

        internal static bool IsWorkPlan(RolePlan plan)
        {
            if (plan == null) return false;
            switch (plan.taskType)
            {
                case FWPlanBean.TASK_CARRY: case FWPlanBean.TASK_Felling: case FWPlanBean.TASK_Mining:
                case FWPlanBean.TASK_Harvesty: case FWPlanBean.TASK_Weeding: case FWPlanBean.TASK_Destroy:
                case FWPlanBean.TASK_BuildRoof: case FWPlanBean.TASK_RemoveRoof: case FWPlanBean.TASK_Uninstall:
                case FWPlanBean.TASK_MoveBuild: case FWPlanBean.TASK_PackageInstall: case FWPlanBean.TASK_Ore_Destroy:
                case FWPlanBean.TASK_Build: case FWPlanBean.TASK_Grow: case FWPlanBean.TASK_STUDY:
                case FWPlanBean.TASK_MakeButcher: case FWPlanBean.TASK_MakeProduct: case FWPlanBean.TASK_AddMaterial:
                case FWPlanBean.TASK_ProductOperate: case FWPlanBean.TASK_REPAIR: case FWPlanBean.TASK_FISHING:
                case FWPlanBean.TASK_Medical: case FWPlanBean.TASK_AddFood: case FWPlanBean.TASK_RescueTarget: return true;
                default: return false;
            }
        }

        private static void Patch(Harmony harmony, Type type, string name, string prefix = null, string postfix = null)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, name);
            if (target == null) throw new MissingMethodException(type.FullName, name);
            harmony.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(NativeGroundInteraction), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(NativeGroundInteraction), postfix));
        }

        internal static void Install(Harmony harmony)
        {
            ConstructionOwner = null;
            Patch(harmony, typeof(RolePlan), "isCanDo", nameof(PlanCandidatePrefix));
            Patch(harmony, typeof(AI_Base), "doPlan", nameof(AiPlanPrefix));
            Patch(harmony, typeof(BaseAction), "runLogic", nameof(ActionLogicPrefix));
            Patch(harmony, typeof(AI_BaseFight), "targetEnemyAvalible", postfix: nameof(CombatTargetPostfix));
            // Real production candidates must be screened per facility, otherwise a
            // neutral visitor's own workbench would be blocked with the whole map.
            foreach (Type type in typeof(Produce_Base).Assembly.GetTypes())
            {
                if (!typeof(Produce_Base).IsAssignableFrom(type)) continue;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (!method.IsAbstract && (method.Name == "checkProduct" || method.Name == "popPlan_MakeBean" || method.Name == "canDoProduct"))
                        harmony.Patch(method, new HarmonyMethod(typeof(NativeGroundInteraction), nameof(ProductCandidatePrefix)),
                            method.ReturnType == typeof(RolePlan) ? new HarmonyMethod(typeof(NativeGroundInteraction), nameof(ProductCandidatePostfix)) : null);
            }
            foreach (string name in new[] { "Build_AddBuild", "Hero_PackageInstall" })
            {
                MethodInfo inherited = AccessTools.Method(typeof(LocalCoreHandle), name);
                ParameterInfo[] arguments = inherited == null ? new ParameterInfo[0] : inherited.GetParameters();
                Type[] signature = new Type[arguments.Length];
                for (int i = 0; i < signature.Length; i++) signature[i] = arguments[i].ParameterType;
                // Hero_PackageInstall is declared on CoreHandle_Role, inherited by
                // LocalCoreHandle. Harmony needs that actual implementation.
                MethodInfo construction = inherited == null ? null : AccessTools.DeclaredMethod(inherited.DeclaringType, name, signature);
                if (construction == null) throw new MissingMethodException(typeof(LocalCoreHandle).FullName, name);
                harmony.Patch(construction, new HarmonyMethod(typeof(NativeGroundInteraction), nameof(ConstructionEnter)),
                    finalizer: new HarmonyMethod(typeof(NativeGroundInteraction), nameof(ConstructionLeave)));
            }
        }

        private static bool PlanCandidatePrefix(RolePlan __instance, BaseRole role, ref bool __result)
        {
            if (Bridge == null || Bridge.Applying || !IsWorkPlan(__instance) || Bridge.CanPerformGroundWork(role, __instance)) return true;
            __instance.releasePreparedPickAction(role); __result = false; return false;
        }

        private static bool AiPlanPrefix(AI_Base __instance, RolePlan plan)
        {
            if (Bridge == null || Bridge.Applying || !IsWorkPlan(plan)) return true;
            if (Bridge.IsSimulator && Bridge.CanPerformGroundWork(__instance.fromRole, plan)) return true;
            plan.releasePreparedPickAction(__instance.fromRole); return false;
        }

        private static bool ActionLogicPrefix(BaseAction __instance)
        {
            if (Bridge == null || Bridge.Applying || __instance.actionType != BaseAction.TYPE_WORK) return true;
            if (Bridge.IsSimulator && Bridge.CanPerformGroundWork(__instance.role, __instance.plan)) return true;
            __instance.doInterrupt(); return false;
        }

        private static bool ProductCandidatePrefix(Produce_Base __instance, object[] __args)
        {
            if (Bridge == null || Bridge.Applying) return true;
            BaseRole role = null;
            foreach (object value in __args) if (value is BaseRole) { role = (BaseRole)value; break; }
            return role == null || Bridge.CanUseGroundWorkTarget(role, __instance.getUID());
        }

        private static void ProductCandidatePostfix(Produce_Base __instance, RolePlan __result)
        {
            // Hydroponic and other generated jobs have no persistent plan author.
            // Retain their source facility so neutral own work remains own work.
            if (Bridge != null && __result != null) __result.setPlanBuild(__instance);
        }

        private static void ConstructionEnter(RolePlan plan, out string __state)
        {
            __state = ConstructionOwner;
            if (Bridge != null && plan != null && plan.planBean != null)
                ConstructionOwner = Bridge.Context.OwnerOfEntity(plan.planBean.getUID())
                    ?? Bridge.Context.OwnerOfMap(Bridge.CurrentMapId) ?? Bridge.ActingPlayer ?? Bridge.Context.LocalPlayerId;
        }

        private static Exception ConstructionLeave(Exception __exception, string __state)
        { ConstructionOwner = __state; return __exception; }

        private static void CombatTargetPostfix(AI_BaseFight __instance, ref bool __result)
        {
            if (!__result || Bridge == null || __instance.fromRole == null) return;
            SpriteObject target = __instance.getTargetEnemy();
            if (target == null) return;
            string actor = Bridge.Context.OwnerOfEntity(__instance.fromRole.getUID()), other = Bridge.Context.OwnerOfEntity(target.getUID());
            if (actor == null || other == null || actor == other) return;
            Relation relation = Bridge.Context.GetRelation(actor, other);
            // Manual neutral aggression remains possible and becomes hostile only
            // after damage. Stale automatic hatred must not start a neutral fight.
            if (relation == Relation.Friendly || (relation == Relation.Neutral && (TargetLock == null || !(bool)TargetLock.GetValue(__instance)))) __result = false;
        }
    }
}
