using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    internal static class GameHooks
    {
        private static readonly HashSet<long> KnockedOut = new HashSet<long>();
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        private static void Patch(Harmony harmony, Type owner, string method, string prefix = null, string postfix = null, Type[] parameters = null)
        {
            MethodInfo target = parameters == null ? AccessTools.Method(owner, method) : AccessTools.Method(owner, method, parameters);
            if (target == null) throw new MissingMethodException(owner.FullName, method);
            harmony.Patch(target,
                prefix == null ? null : new HarmonyMethod(typeof(GameHooks), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(GameHooks), postfix));
        }
        internal static void Install(Harmony harmony)
        {
            KnockedOut.Clear();
            NativeGroundPersonnelHooks.Install(harmony);
            NativeGroundInteraction.Install(harmony);
            Patch(harmony, typeof(FWGameData), "createUID", "UidPrefix");
            Patch(harmony, typeof(FWUidCounter), "nextUid", "UidPrefix");
            Patch(harmony, typeof(FWUidItem), "createUID", "UidItemPrefix");
            foreach (string method in new[] { "beginLogic", "logic", "lateLogic" })
                harmony.Patch(AccessTools.Method(typeof(MapManager), method), new HarmonyMethod(typeof(GameHooks), "MapLogicPrefix"),
                    finalizer: new HarmonyMethod(typeof(GameHooks), "LandingOpenFinalizer"));
            Patch(harmony, typeof(MapManager), "updateGame", "MapUpdatePrefix");
            Patch(harmony, typeof(MapManager), "initEnd", postfix: "MapLoadedPostfix");
            Patch(harmony, typeof(UserFWStarSpread), "AdvanceWorldTime", "WorldTimePrefix");
            Patch(harmony, typeof(UserFWStarSpread), "ContinuePendingWorldAdvance", "WorldTimePrefix");
            Patch(harmony, typeof(MapManager), "setPlay", "PlayPrefix");
            Patch(harmony, typeof(MapManager), "setLogicSpeed", "ScalePrefix");
            Patch(harmony, typeof(MapManager), "getNowSpeed", "SpeedQueryPrefix");
            Patch(harmony, typeof(UserBean), "saveArchive", "SavePrefix");
            Patch(harmony, typeof(UserBean), "saveArchiveWithSynchronousSnapshot", "SavePrefix");
            Patch(harmony, typeof(NetSender), "backCover", "BackCoverPrefix", parameters: new[] { typeof(callbackInt) });
            NativeGuestExitHooks.Install(harmony);
            Patch(harmony, typeof(UserBean), "isMineStarMap", "OwnMapPrefix");
            Patch(harmony, typeof(FWStarMapAutoLiveService), "simulateOnActivate", "DisableNativeOfflinePrefix");
            Patch(harmony, typeof(FWStarMapAutoLiveService), "simulateBeforeGroundLoad", "DisableNativeOfflinePrefix");
            Patch(harmony, typeof(LocalCoreHandle), "AcrossDay_Ground", "GroundDayPrefix");
            Patch(harmony, typeof(BaseHero), "canCommand", "CommandPermissionPrefix");
            Patch(harmony, typeof(RoleAlliance), "isHostileAlliance", "HostilityPrefix", parameters: new[] { typeof(SpriteObject), typeof(SpriteObject) });
            Patch(harmony, typeof(Struct_Door), "canPass", "DoorPermissionPrefix", parameters: new[] { typeof(SpriteObject) });
            Patch(harmony, typeof(RoleCommand), "order_Fight_Move", "MovePrefix");
            Patch(harmony, typeof(RoleCommand), "order_Fight_Conscript", "ConscriptPrefix");
            Patch(harmony, typeof(RoleCommand), "order_Fight_Attack", "AttackPrefix");
            Patch(harmony, typeof(BaseRole), "beHit", "HitPrefix", "HitPostfix");
            Patch(harmony, typeof(BaseBuild), "beHit", "BuildHitPrefix", "BuildHitPostfix");
            Patch(harmony, typeof(BaseRole), "clearBag", "ClearBagPrefix");
            Patch(harmony, typeof(UserBean), "local_useThing", "UseItemPrefix");
            Patch(harmony, typeof(FWRoleBean), "addEquip", "EquipItemPrefix");
            Patch(harmony, typeof(FWThingBean), "getCouldUseSum", postfix: "AvailableLootPostfix");
            Patch(harmony, typeof(FWThingBean), "reduceSum", "ConsumeLootPrefix");
            Patch(harmony, typeof(FWThingBean), "subSum", "ConsumeLootPrefix");
            foreach (MethodInfo putdown in typeof(UserBean).GetMethods().Where(m => m.Name == "local_putdown_toMap"))
                harmony.Patch(putdown, new HarmonyMethod(typeof(GameHooks), "PutDownLootPrefix"));
            foreach (MethodInfo pickup in typeof(LocalCoreHandle).GetMethods().Where(m => m.Name == "Hero_Thing_Pickup_LN"))
                harmony.Patch(pickup, new HarmonyMethod(typeof(GameHooks), "PickupPrefix"));
            Patch(harmony, typeof(LocalCoreHandle), "Hero_Thing_Throw_LN", "ThrowItemPrefix");
            Patch(harmony, typeof(BaseRole), "doDead", "DeathPrefix", parameters: new[] { typeof(bool) });
            Patch(harmony, typeof(BaseHero), "doDead", "DeathPrefix", parameters: new[] { typeof(bool) });
            Patch(harmony, typeof(FWRoleBean), "doDead", "BeanDeathPrefix");
            Patch(harmony, typeof(AttrDriven_HeroBase), "checkLive", "LivePrefix");
            Patch(harmony, typeof(HeroAnim), "event_Listener", "AnimationEventPrefix");
            Patch(harmony, typeof(AnimalAnim), "event_Listener", "AnimationEventPrefix");
            Patch(harmony, typeof(MonsterAnim), "event_Listener", "AnimationEventPrefix");
            foreach (MethodInfo method in typeof(HurtUtils).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => new[] { "hitTarget", "hitBuild", "hitRole", "attackRange", "triggerAddtionEff" }.Contains(m.Name)))
                harmony.Patch(method, new HarmonyMethod(typeof(GameHooks), "DamageAuthorityPrefix"));
            Patch(harmony, typeof(FWStarMapAutoLiveService), "getOfflineRoleWorkers", postfix: "OfflineWorkersPostfix");
            Patch(harmony, typeof(ActionDecide), "buildAddFule_cando", "WorkPermissionPrefix");
            foreach (MethodInfo method in typeof(SchedulingCommons).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("pop", StringComparison.Ordinal) && m.GetParameters().Any(p => typeof(BaseRole).IsAssignableFrom(p.ParameterType))))
                harmony.Patch(method, new HarmonyMethod(typeof(GameHooks), "WorkPermissionPrefix"));
            foreach (MethodInfo method in typeof(ActionDecide).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name != "buildAddFule_cando" && (m.Name.StartsWith("build", StringComparison.Ordinal) || m.Name.StartsWith("plant_", StringComparison.Ordinal)
                    || new[] { "building_cando", "mining_cando", "carry_cando", "research_cando", "uninstall_cando", "moveBuild_cando", "opening_cando" }.Contains(m.Name))))
                harmony.Patch(method, new HarmonyMethod(typeof(GameHooks), "WorkPermissionPrefix"));
            Patch(harmony, typeof(Defense_BaseTurret), "checkTarget", postfix: "TurretTargetsPostfix");
            Patch(harmony, typeof(AirShipMoveBeanBase), "logic", "FleetMotionPrefix");
            Patch(harmony, typeof(GalaxyPlayerFleet), "MoveBeanLogic", "FleetPresentationPrefix");
            NativeCommands.Install(harmony);
            NativeGalaxyHooks.Install(harmony);
            NativeTravelHooks.Install(harmony);
            NativeTransferHooks.Install(harmony);
            NativeBuildingCommandHooks.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(ExpeditionEnter), "open"),
                new HarmonyMethod(typeof(GameHooks), "LandingOpenPrefix"),
                new HarmonyMethod(typeof(GameHooks), "LandingOpenPostfix"),
                finalizer: new HarmonyMethod(typeof(GameHooks), "LandingOpenFinalizer"));
        }
        private static bool BackCoverPrefix(callbackInt __0)
        {
            MultiplayerRuntime runtime = MultiplayerRuntime.Instance;
            return runtime == null || !runtime.TryQueueNativeReturn(__0);
        }
        private static bool UidPrefix(ref long __result)
        {
            if (Bridge == null || Bridge.Applying) return true;
            __result = Bridge.NextUid(); return false;
        }
        private static bool UidItemPrefix(FWUidItem __instance, ref long __result)
        {
            if (Bridge == null || Bridge.Applying) return true;
            long uid = Bridge.NextUid(); // Outside the native catch-all/fake-UID fallback.
            __instance.uid = uid; __result = uid; return false;
        }
        private static bool MapLogicPrefix(out IDisposable __state)
        {
            __state = null;
            if (Bridge == null) return true;
            if (!Bridge.IsSimulator) return false;
            string owner = Bridge.Context.OwnerOfMap(Bridge.CurrentMapId);
            if (!string.IsNullOrEmpty(owner)) __state = Bridge.EnterPlayer(owner);
            return true;
        }
        private static bool MapUpdatePrefix(MapManager __instance)
        {
            if (Bridge != null) { Bridge.UpdateNativeLandingClock(); Bridge.PumpNativeLandings(); }
            if (Bridge == null || Bridge.IsSimulator) return true;
            if (!MapManager.isInitFinish) return false;
            // Only presentation. Do not call beginLogic/logic/lateLogic or the world clock.
            TimeInfo.UpdateLoop(MapManager.getNowSpeed());
            CatPlayerr.setUpdateTime(TimeInfo.elapsedTime_MM);
            SpinePlayer.setGameScaleGlobal(MapManager.getNowSpeed());
            Bridge.PresentNativeGroundPersonnel();
            __instance.casePaint();
            return false;
        }
        private static bool WorldTimePrefix() { return Bridge == null || Bridge.Advancing; }
        private static bool FleetMotionPrefix() { return Bridge == null || Bridge.Advancing; }
        private static bool FleetPresentationPrefix() { return Bridge == null; }
        private static bool DisableNativeOfflinePrefix() { return Bridge == null; }
        private static bool GroundDayPrefix()
        {
            if (Bridge == null) return true;
            return Bridge.IsSimulator && Bridge.Context.OwnerOfMap(Bridge.CurrentMapId) == (Bridge.ActingPlayer ?? Bridge.Context.LocalPlayerId);
        }
        private static bool OwnMapPrefix(long starMapID, ref bool __result)
        {
            if (Bridge == null) return true;
            __result = Bridge.Context.OwnerOfMap(starMapID) == (Bridge.ActingPlayer ?? Bridge.Context.LocalPlayerId); return false;
        }
        private static void MapLoadedPostfix()
        {
            if (Bridge != null) { Bridge.EnsureMapCharacters(); Bridge.PumpNativeLandings(); }
        }
        private static void LandingOpenPrefix(ExpeditionEnter __instance, out IDisposable __state)
        { __state = Bridge == null ? null : Bridge.EnterNativeLanding(__instance); }
        private static void LandingOpenPostfix(ExpeditionEnter __instance, bool __result)
        { if (Bridge != null) Bridge.CompleteNativeLanding(__instance, __result); }
        private static Exception LandingOpenFinalizer(Exception __exception, IDisposable __state)
        { __state?.Dispose(); return __exception; }
        private static void TurretTargetsPostfix(Defense_BaseTurret __instance)
        {
            if (Bridge == null || !Bridge.IsSimulator) return;
            if (__instance.targetRole != null && !RoleAlliance.isHostileAlliance(__instance, __instance.targetRole))
                NativeWire.Call(__instance, "doStand");
            if (__instance.targetRole != null) return;
            foreach (BaseHero hero in MapManager.instance.heroManager.getAllRoles())
                if (RoleAlliance.isHostileAlliance(__instance, hero) && (bool)NativeWire.Call(__instance, "canAim", (SpriteObject)hero)) break;
        }
        private static bool PlayPrefix(bool _flag)
        {
            if (Bridge == null || Bridge.Seeding) return true;
            if (Bridge.Applying || !MapManager.isInitFinish || !NativeCommands.IsPlayerIntent) return false;
            if (Bridge.Context.IsHost) Bridge.Context.RequestClock(!_flag, Bridge.Context.SimulationSpeed());
            return false;
        }
        private static bool ScalePrefix(float _speed)
        {
            if (Bridge == null || Bridge.Seeding) return true;
            if (Bridge.Applying || !MapManager.isInitFinish || !NativeCommands.IsPlayerIntent) return false;
            if (Bridge.Context.IsHost) Bridge.Context.RequestClock(false, _speed);
            return false;
        }
        private static bool SpeedQueryPrefix(ref float __result)
        {
            if (Bridge == null || Bridge.Seeding) return true;
            __result = Bridge.Context.IsPaused() ? 0 : (float)Bridge.Context.SimulationSpeed(); return false;
        }
        private static bool SavePrefix(object[] __args)
        {
            if (Bridge == null) return true;
            // Native Archive paths belong to single-player. Session owns the only multiplayer save.
            bool saved = Bridge.Context.IsHost && Bridge.Context.RequestSave();
            foreach (object argument in __args) if (argument is Action<bool>) ((Action<bool>)argument)(saved);
            return false;
        }
        private static bool CommandPermissionPrefix(BaseHero __instance, ref bool __result)
        {
            if (Bridge == null || Bridge.Applying) return true;
            if (!Bridge.Executing && !NativeCommands.IsPlayerIntent && Bridge.IsSimulator) return true;
            string actor = Bridge.Executing ? (Bridge.ActingPlayer ?? Bridge.Context.LocalPlayerId) : Bridge.Context.LocalPlayerId;
            if (!Bridge.Context.HasPermission(actor, Bridge.CurrentMapId, __instance.getUID(), Permission.ControlOwnHero))
            { __result = false; return false; }
            return true;
        }
        private static bool DoorPermissionPrefix(Struct_Door __instance, SpriteObject role, ref bool __result)
        {
            if (Bridge == null || role == null || __instance.doorBean.damage) return true;
            string actor = Bridge.Context.OwnerOfEntity(role.getUID());
            string owner = Bridge.Context.OwnerOfEntity(__instance.getUID()) ?? Bridge.Context.OwnerOfMap(Bridge.CurrentMapId);
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(owner)) return true;
            __result = actor == owner || Bridge.Context.GetRelation(actor, owner) == Relation.Friendly; return false;
        }
        private static bool HostilityPrefix(SpriteObject from, SpriteObject target, ref bool __result)
        {
            if (Bridge == null || from == null || target == null) return true;
            string a = Bridge.Context.OwnerOfEntity(from.getUID()), b = Bridge.Context.OwnerOfEntity(target.getUID());
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;
            __result = a != b && Bridge.Context.GetRelation(a, b) == Relation.Hostile;
            return false;
        }
        private static bool MovePrefix(BaseRole hero, short cellx, short celly)
        { return Bridge == null || Bridge.Route(CommandKind.Move, hero.getUID(), -1, new { X = cellx, Y = celly }); }
        private static bool ConscriptPrefix(BaseRole hero, bool isMual)
        { return Bridge == null || Bridge.Route(CommandKind.Conscript, hero.getUID(), -1, new { Manual = isMual }); }
        private static bool AttackPrefix(BaseRole hero, SpriteObject _target)
        {
            if (Bridge == null) return true;
            string actor = Bridge.ActingPlayer ?? Bridge.Context.LocalPlayerId;
            if (!Bridge.Context.HasPermission(actor, Bridge.CurrentMapId, _target.getUID(), Permission.Attack)) return false;
            return Bridge.Route(CommandKind.Attack, hero.getUID(), _target.getUID(), new { });
        }
        private static bool AnimationEventPrefix()
        { return Bridge == null || (Bridge.IsSimulator && Bridge.Context.IsAttackAllowed(Bridge.CurrentMapId)); }
        private static bool DamageAuthorityPrefix()
        { return Bridge == null || (Bridge.IsSimulator && Bridge.Context.IsAttackAllowed(Bridge.CurrentMapId)); }
        private sealed class HitState
        {
            internal double Hp;
            internal string Before;
        }
        private static bool HitPrefix(BaseRole __instance, HurtStruct hurtStruct, out HitState __state)
        {
            __state = null;
            if (Bridge == null) return true;
            if (!Bridge.IsSimulator || !Bridge.Context.IsAttackAllowed(Bridge.CurrentMapId)) return false;
            if (Bridge.Context.IsProtectedDefender(__instance.getUID()) && KnockedOut.Contains(__instance.getUID())) return false;
            string from = hurtStruct.from == null ? null : Bridge.Context.OwnerOfEntity(hurtStruct.from.getUID());
            string target = Bridge.Context.OwnerOfEntity(__instance.getUID());
            if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(target) && from != target && Bridge.Context.GetRelation(from, target) == Relation.Friendly) return false;
            __state = new HitState { Hp = __instance.roleBean.attr_Hp.hp,
                Before = Bridge.Context.IsProtectedDefender(__instance.getUID()) ? NativeWire.Pack(__instance.roleBean) : null };
            return true;
        }
        private static bool BuildHitPrefix(BaseBuild __instance, HurtStruct hurtStruct, out float __state)
        {
            __state = __instance.getHp();
            if (Bridge == null) return true;
            if (!Bridge.IsSimulator || !Bridge.Context.IsAttackAllowed(Bridge.CurrentMapId)) return false;
            string from = hurtStruct.from == null ? null : Bridge.Context.OwnerOfEntity(hurtStruct.from.getUID());
            string owner = Bridge.Context.OwnerOfEntity(__instance.getUID()) ?? Bridge.Context.OwnerOfMap(Bridge.CurrentMapId);
            return string.IsNullOrEmpty(from) || string.IsNullOrEmpty(owner) || from == owner || Bridge.Context.GetRelation(from, owner) != Relation.Friendly;
        }
        private static void BuildHitPostfix(BaseBuild __instance, HurtStruct hurtStruct, float __state)
        {
            if (Bridge != null && hurtStruct.from != null && __instance.getHp() < __state)
                Bridge.Context.ReportDamage(new GameCommand { Kind = CommandKind.DamageReported, MapId = Bridge.CurrentMapId,
                    PlayerId = Bridge.Context.OwnerOfEntity(hurtStruct.from.getUID()), EntityId = hurtStruct.from.getUID(), TargetEntityId = __instance.getUID() });
        }
        private static bool PickupPrefix(object[] __args)
        {
            if (Bridge == null || Bridge.Applying) return true;
            BaseRole role = __args.OfType<BaseRole>().FirstOrDefault();
            FWRoleBean bean = __args.OfType<FWRoleBean>().FirstOrDefault() ?? (role == null ? null : role.roleBean);
            FWThingBean thing = __args.OfType<FWThingBean>().FirstOrDefault();
            if (bean == null || thing == null) throw new InvalidOperationException("Native pickup signature changed.");
            int count = __args.OfType<int>().First();
            FWRoleBean.carryType type = __args.OfType<FWRoleBean.carryType>().First();
            callbackInt callback = __args.OfType<callbackInt>().FirstOrDefault();
            return Bridge.HandlePickup(role, bean, thing, count, type, callback);
        }
        private static void ClearBagPrefix(BaseRole __instance)
        { if (Bridge != null && !Bridge.Applying) Bridge.ReleaseAllLoot(__instance.getUID()); }
        private static bool ThrowItemPrefix(BaseRole hero, FWThingBean thingBean, int sum, callbackInt callInt)
        {
            if (Bridge == null || Bridge.Applying || !Bridge.ReleaseLoot(hero.getUID(), thingBean.getUID(), sum)) return true;
            callInt?.Invoke(Callback.SUCCESS); return false;
        }
        private static bool UseItemPrefix(FWThingBean useThing)
        { return Bridge == null || !Bridge.IsReservedLoot(useThing.getUID()); }
        private static void EquipItemPrefix(FWThingBean takeEquipThing)
        {
            if (Bridge != null && Bridge.IsReservedLoot(takeEquipThing.getUID()))
                throw new InvalidOperationException("本场预占战利品只能在唯一一次撤离提交后使用或装备。");
        }
        private static void AvailableLootPostfix(FWThingBean __instance, ref int __result)
        { if (Bridge != null) __result = Math.Max(0, __result - Bridge.ReservedSourceCount(__instance.getUID())); }
        private static void ConsumeLootPrefix(FWThingBean __instance, object[] __args)
        {
            if (Bridge == null || Bridge.Applying || Bridge.CommittingRaidTransfer) return;
            int count = __args.OfType<int>().First();
            if (__instance.getSum() - count < Bridge.ReservedSourceCount(__instance.getUID()))
                throw new InvalidOperationException("物资已被本场袭击预占，不能重复消耗或搬运。UID=" + __instance.getUID());
        }
        private static bool PutDownLootPrefix(BaseRole role, object[] __args, ref List<FWThingBean> __result)
        {
            if (Bridge == null || Bridge.Applying) return true;
            FWThingBean single = __args.OfType<FWThingBean>().FirstOrDefault();
            if (single != null)
            {
                if (!Bridge.ReleaseLoot(role.getUID(), single.getUID(), __args.OfType<int>().First())) return true;
                role.carryContainer.refreshAnim(); __result = new List<FWThingBean>(); return false;
            }
            int index = Array.FindIndex(__args, a => a is List<FWThingBean>);
            List<FWThingBean> selected = (List<FWThingBean>)__args[index];
            var normal = new List<FWThingBean>();
            foreach (FWThingBean item in selected.ToArray())
                if (!Bridge.ReleaseLoot(role.getUID(), item.getUID(), item.getSum())) normal.Add(item);
            if (normal.Count == selected.Count) return true;
            role.carryContainer.refreshAnim();
            if (normal.Count > 0) { __args[index] = normal; return true; }
            __result = new List<FWThingBean>(); return false;
        }
        private static void HitPostfix(BaseRole __instance, HurtStruct hurtStruct, HitState __state)
        {
            if (Bridge == null || __state == null) return;
            if (!string.IsNullOrEmpty(__state.Before) && KnockedOut.Contains(__instance.getUID()))
            {
                // Restore body parts and equipment from before the fatal hit, retain the real hit as a
                // nonfatal incapacitating HP result. This happens inside the same damage transaction.
                FWRoleBean before = NativeWire.Unpack(__state.Before, __instance.roleBean.GetType()) as FWRoleBean;
                NativeWire.Copy(before, __instance.roleBean);
                Incapacitate(__instance);
            }
            if (hurtStruct.from != null && __instance.roleBean.attr_Hp.hp < __state.Hp)
                Bridge.Context.ReportDamage(new GameCommand { Kind = CommandKind.DamageReported,
                    PlayerId = Bridge.Context.OwnerOfEntity(hurtStruct.from.getUID()), EntityId = hurtStruct.from.getUID(),
                    TargetEntityId = __instance.getUID(), MapId = Bridge.CurrentMapId });
        }
        internal static void Incapacitate(BaseRole role)
        {
            bool first = KnockedOut.Add(role.getUID());
            role.roleBean.dead = false;
            role.roleBean.attr_Hp.hp = -Math.Max(1, role.roleBean.getMaxHp() * 0.01);
            role.roleBean.attr_Hp.tempHP = 0;
            if (first) role.doFallDown();
        }
        private static bool DeathPrefix(BaseRole __instance)
        {
            if (Bridge == null || !Bridge.Context.IsProtectedDefender(__instance.getUID())) return true;
            Incapacitate(__instance); return false;
        }
        private static bool BeanDeathPrefix(FWRoleBean __instance)
        {
            if (Bridge == null || !Bridge.Context.IsProtectedDefender(__instance.getUID())) return true;
            __instance.dead = false;
            __instance.attr_Hp.hp = -Math.Max(1, __instance.getMaxHp() * 0.01);
            __instance.attr_Hp.tempHP = 0;
            KnockedOut.Add(__instance.getUID()); return false;
        }
        private static bool LivePrefix(AttrDriven_HeroBase __instance)
        {
            if (Bridge == null) return true;
            BaseHero hero = (BaseHero)NativeWire.Get(__instance, "hero");
            if (!Bridge.Context.IsProtectedDefender(hero.getUID())) return true;
            if (KnockedOut.Contains(hero.getUID()) || hero.heroBean.isFallDown()) { Incapacitate(hero); return false; }
            if (hero.heroBean.isHpDead()) { Incapacitate(hero); return false; }
            return true;
        }
        private static void OfflineWorkersPostfix(FWStarMapInfo starMapInfo, object __result)
        {
            if (Bridge == null || __result == null) return;
            var list = (System.Collections.IList)__result;
            string owner = Bridge.Context.OwnerOfMap(starMapInfo.starMapId);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                FWHeroBean hero = (FWHeroBean)NativeWire.Get(list[i], "hero");
                string workerOwner = Bridge.Context.OwnerOfEntity(hero.getUID());
                if (workerOwner != owner && Bridge.Context.GetRelation(workerOwner, owner) != Relation.Friendly) list.RemoveAt(i);
            }
        }
        private static bool WorkPermissionPrefix(MethodBase __originalMethod, object[] __args)
        {
            if (Bridge == null) return true;
            BaseRole role = __args.OfType<BaseRole>().FirstOrDefault();
            if (role == null) return true;
            string owner = Bridge.Context.OwnerOfEntity(role.getUID());
            if (string.IsNullOrEmpty(owner)) return true;
            RolePlan plan = __args.OfType<RolePlan>().FirstOrDefault();
            if (plan == null)
            {
                BaseBuild build = __args.OfType<BaseBuild>().FirstOrDefault();
                // Outer schedulers must inspect all candidates, including this
                // player's own workbench on another player's map.
                return build == null || Bridge.CanUseGroundWorkTarget(role, build.getUID());
            }
            if (Bridge.CanPerformGroundWork(role, plan)) return true;
            return (NativeCommands.IsPlayerIntent || Bridge.Executing) && __originalMethod.Name.IndexOf("carry", StringComparison.OrdinalIgnoreCase) >= 0
                && Bridge.Context.HasPermission(owner, Bridge.CurrentMapId, -1, Permission.Loot);
        }
        internal static bool IsKnockedOut(long uid) { return KnockedOut.Contains(uid); }
        internal static void EndProtection(IEnumerable<long> heroes) { foreach (long uid in heroes) KnockedOut.Remove(uid); }
    }
}
