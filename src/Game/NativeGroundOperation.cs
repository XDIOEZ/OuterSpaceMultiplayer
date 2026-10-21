using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private long observedOperationMap = -1;
        private string observedOperationOwner;
        internal bool CanOperateGround(string playerId, long mapId)
        { return Context != null && Context.CanOperateMap != null && Context.CanOperateMap(playerId, mapId); }

        // Presence and combat readiness are different: a downed resident still owns a
        // home to return to, while robots do not prevent the human defenders losing.
        public bool HasGroundPlayerHeroes(long mapId, string playerId)
        {
            return !string.IsNullOrEmpty(playerId) && Players.ContainsKey(playerId)
                && PlayerHeroes(playerId).Any(hero => hero.isInMap(mapId) && !hero.isDead());
        }

        private void UnloadReleasedGround(MapAccess access)
        {
            if (access.MapId != CurrentMapId || access.Access == MapAccessMode.Simulator
                || !string.IsNullOrEmpty(Context.OperatorOfMap(access.MapId))) return;
            UnloadReleasedGround();
        }

        internal void PumpGroundOperationLifecycle()
        {
            long mapId = CurrentMapId;
            string current = Context.OperatorOfMap(mapId);
            if (observedOperationMap == mapId && observedOperationOwner == Context.LocalPlayerId
                && current != Context.LocalPlayerId && IsNativeMapReady(mapId))
                LocalCoreHandle.Plan_CacheClear_LN();
            observedOperationMap = mapId; observedOperationOwner = current;
            if (current == null) UnloadReleasedGround();
        }

        private void UnloadReleasedGround()
        {
            FWStarMapInfo map;
            if (World == null || Seeding || Applying || IsCreatingWorld || GameRoot.isObservingGround()
                || !Maps.TryGetValue(CurrentMapId, out map) || map.starSpread == null || map.starSpread.isAirMap()
                || GameRoot.instance == null || MapManager.instance == null) return;
            // Input ownership and simulation readiness are not scene lifetime. In particular,
            // a scout has no operator and CanSimulateMap stays false until native initEnd is
            // acknowledged. gameType can still be space while MainGame loads asynchronously.
            // Keep the formal local view and its loader alive across that whole transition.
            if (Context.IsLocalGroundViewActive == null || Context.IsLocalGroundViewActive(map.starMapId)
                || !IsNativeMapReady(map.starMapId)) return;
            MapAccess access = Context.GetMapAccess(map.starMapId);
            if (access == null || access.Access == MapAccessMode.Simulator
                || !string.IsNullOrEmpty(Context.OperatorOfMap(map.starMapId))) return;
            // Session has already saved/revoked this map before releasing the slot.
            // Use the native destruction lifecycle; keep the canonical saved beans.
            MethodInfo destroy = AccessTools.DeclaredMethod(typeof(GameRoot), "destroyMapManager");
            if (destroy == null) throw new MissingMethodException(typeof(GameRoot).FullName, "destroyMapManager");
            using (NativeMapProjectionGuard.Enter()) destroy.Invoke(GameRoot.instance, null);
            nativeMapLoad = null;
            replicator.ClearGroundPresentation(map.starMapId, false);
        }
    }

    internal static class NativeGroundOperationHooks
    {
        internal static void Install(Harmony harmony)
        {
            // These personnel editors write schedules/equipment directly, before
            // their legacy CoreHandle notification. Guard the input callback itself.
            string[] editors = { "HeroWorkSettingView", "HeroSchedule_ItemBase", "HeroSchedule_TimeItem",
                "HeroSetWorkItem", "PopList_WorkTime", "HeroEquip_ModEquip", "Hero_MainView_Cmd", "Control_Fight" };
            foreach (string name in editors)
            {
                Type type = typeof(FWHeroBean).Assembly.GetType(name);
                if (type == null) continue; // Optional UI differs between game editions.
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.ReturnType != typeof(void) || method.GetMethodBody() == null
                        || !(method.Name.StartsWith("onEvent_", StringComparison.Ordinal) || method.Name == "onSelectedChange"
                            || method.Name == "showTip" || method.Name == "OnPointerClick" || method.Name == "ChangePriority"
                            || method.Name == "doAttack" || method.Name == "doMove")) continue;
                    harmony.Patch(method, new HarmonyMethod(typeof(NativeGroundOperationHooks), nameof(PersonnelInputPrefix)));
                }
            }
        }

        private static bool PersonnelInputPrefix(MethodBase __originalMethod)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || bridge.Applying || bridge.Executing || bridge.Seeding || !GameRoot.isObservingGround()) return true;
            bool ownSelection = __originalMethod.DeclaringType != typeof(Hero_MainView_Cmd)
                || Handler_RoleSelect.selectedRoles.All(role => role != null
                    && bridge.Context.OwnerOfEntity(role.getUID()) == bridge.Context.LocalPlayerId);
            if (ownSelection && bridge.CanOperateGround(bridge.Context.LocalPlayerId, bridge.CurrentMapId)) return true;
            FairyUIDispatcher.showToast("当前地块仅可观战，不能修改地面人员的设置或装备。");
            return false;
        }
    }
}
