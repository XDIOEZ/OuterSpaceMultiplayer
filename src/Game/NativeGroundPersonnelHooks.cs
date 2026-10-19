using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace FreeWorldMultiplayer
{
    // Player employees keep their canonical beans and immutable PlayerId ownership.
    // Native NPC colours/selection are a temporary view, never an alliance conversion.
    internal static class NativeGroundPersonnelHooks
    {
        [ThreadStatic] private static int rosterDepth;
        [ThreadStatic] private static int viewDepth;
        private static readonly MethodInfo RebuildTopRoster = AccessTools.DeclaredMethod(typeof(Top_HeroGrid), "initHeros");

        internal static void Install(Harmony harmony)
        {
            rosterDepth = viewDepth = 0;
            if (RebuildTopRoster == null) throw new MissingMethodException(typeof(Top_HeroGrid).FullName, "initHeros");
            foreach (Type type in new[] { typeof(MainHero_HeroList), typeof(Top_HeroGrid) })
            {
                Patch(harmony, type, "getHeros", prefix: nameof(LocalRosterPrefix));
                Scope(harmony, type, "onEvent_SelectAll", false);
            }
            // The legacy incremental refresh never removes stale foreign entries.
            // Reuse its native rebuild, which restores the selected hero afterwards.
            Patch(harmony, typeof(Top_HeroGrid), "_refresh", prefix: nameof(TopRefreshPrefix));
            Scope(harmony, typeof(MainHero_HeroList), "initList", false);
            Patch(harmony, typeof(HeroAlianceFaction), "getRoles", postfix: nameof(RosterFactionPostfix));
            Scope(harmony, typeof(Info_Role_Info), "refresh", true);
            Scope(harmony, typeof(Handler_ClickSelect), "click_Hero", true);
            Patch(harmony, typeof(BaseRole), "getAlliance", postfix: nameof(ViewAlliancePostfix));
            Patch(harmony, typeof(GameBridge), "ApplyRelationship", postfix: nameof(RelationshipPostfix));
        }

        private static void Patch(Harmony harmony, Type type, string name, string prefix = null, string postfix = null)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, name);
            if (target == null) throw new MissingMethodException(type.FullName, name);
            harmony.Patch(target,
                prefix == null ? null : new HarmonyMethod(typeof(NativeGroundPersonnelHooks), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(NativeGroundPersonnelHooks), postfix));
        }

        private static void Scope(Harmony harmony, Type type, string name, bool view)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, name);
            if (target == null) throw new MissingMethodException(type.FullName, name);
            harmony.Patch(target,
                new HarmonyMethod(typeof(NativeGroundPersonnelHooks), view ? nameof(ViewEnter) : nameof(RosterEnter)),
                finalizer: new HarmonyMethod(typeof(NativeGroundPersonnelHooks), view ? nameof(ViewLeave) : nameof(RosterLeave)));
        }

        private static bool IsLocalEmployee(GameBridge bridge, BaseHero hero)
        {
            return hero != null && hero.heroBean != null
                && bridge.Context.OwnerOfEntity(hero.getUID()) == bridge.Context.LocalPlayerId;
        }

        private static bool LocalRosterPrefix(ref List<BaseHero> __result)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null) return true;
            MapManager manager = MapManager.instance;
            __result = manager == null || manager.heroManager == null ? new List<BaseHero>()
                : manager.heroManager.getAllRoles().Where(h => IsLocalEmployee(bridge, h)
                    && h.heroBean.allianceID == RoleAlliance.Alliance_MINE).ToList();
            __result.Sort((left, right) => HeroScheduleDialog.SortByName(left, right));
            return false;
        }

        private static bool TopRefreshPrefix(Top_HeroGrid __instance)
        {
            if (GameBridge.Active == null) return true;
            RebuildTopRoster.Invoke(__instance, null);
            return false;
        }

        private static void RosterEnter(out int __state) { __state = rosterDepth; rosterDepth++; }
        private static Exception RosterLeave(Exception __exception, int __state)
        { rosterDepth = __state; return __exception; }
        private static void ViewEnter(out int __state) { __state = viewDepth; viewDepth++; }
        private static Exception ViewLeave(Exception __exception, int __state)
        { viewDepth = __state; return __exception; }

        private static void RosterFactionPostfix(HeroAlianceFaction __instance, ref List<BaseHero> __result)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || rosterDepth == 0) return;
            // Never RemoveAll/Sort the native list: it is also the real simulation roster.
            __result = __result.Where(hero =>
            {
                if (hero == null) return false;
                string owner = bridge.Context.OwnerOfEntity(hero.getUID());
                // Native prisoners without a player owner keep the original map UI.
                return string.IsNullOrEmpty(owner) || owner == bridge.Context.LocalPlayerId;
            }).ToList();
        }

        private static void ViewAlliancePostfix(BaseRole __instance, ref sbyte __result)
        {
            GameBridge bridge = GameBridge.Active;
            BaseHero hero = __instance as BaseHero;
            if (bridge == null || viewDepth == 0 || hero == null || bridge.Executing || bridge.ActingPlayer != null) return;
            string owner = bridge.Context.OwnerOfEntity(hero.getUID());
            if (string.IsNullOrEmpty(owner)) return; // Ordinary native NPCs retain their original faction.
            string viewer = bridge.Context.LocalPlayerId;
            __result = owner == viewer ? RoleAlliance.Alliance_MINE
                : bridge.Context.GetRelation(viewer, owner) == Relation.Hostile
                    ? RoleAlliance.Alliance_ENEMY : RoleAlliance.Alliance_NEUTRAL;
        }

        private static void RelationshipPostfix(GameBridge __instance, BridgeResult __result)
        {
            if (__result == null || !__result.Success || !__instance.IsMapReady(__instance.CurrentMapId)) return;
            foreach (BaseHero hero in MapManager.instance.heroManager.getAllRoles())
            {
                if (hero == null || hero.roleStruct == null) continue;
                // A failed optional nameplate refresh cannot undo a committed relation.
                try { hero.roleStruct.refresh(); }
                catch (Exception error) { UnityEngine.Debug.LogException(error); }
            }
        }
    }
}
