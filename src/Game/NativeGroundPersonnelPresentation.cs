using System;
using System.Reflection;
using HarmonyLib;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        internal void PresentNativeGroundPersonnel() { replicator.PresentGroundPersonnel(); }
    }

    internal sealed partial class NativeMapReplicator
    {
        private static readonly MethodInfo GroundRoleSorting = AccessTools.DeclaredMethod(typeof(BaseRole), "flushSorting");

        internal void PresentGroundPersonnel()
        {
            // This runs after pose interpolation, including paused frames and initial
            // load with no received pose. Never resume the replica's native logic/AI.
            if (bridge.IsSimulator || !bridge.IsMapReady(bridge.CurrentMapId)) return;
            if (GroundRoleSorting == null) throw new MissingMethodException(typeof(BaseRole).FullName, "flushSorting");
            MapManager manager = MapManager.instance;
            foreach (BaseHero hero in manager.heroManager.getAllRoles()) PresentGroundRole(hero, manager);
            foreach (BaseAnimal animal in manager.animalManager.getAnimals()) PresentGroundRole(animal, manager);
            foreach (BaseMonster monster in manager.monsterManager.monsters) PresentGroundRole(monster, manager);
        }

        private static void PresentGroundRole(BaseRole role, MapManager manager)
        {
            if (role == null || role.roleBean == null || role.roleAnim == null || !role.isVisiable()) return;
            // BaseRole.logic normally assigns MAP_BLOCK/MAP_GROUP render depth. The
            // read-only replica skips logic, otherwise its mesh stays behind terrain.
            if (!role.roleBean.isBeCarryed()) GroundRoleSorting.Invoke(role, null);
            HeroAnim hero = role.roleAnim as HeroAnim;
            MonsterAnim monster = role.roleAnim as MonsterAnim;
            AnimalAnim animal = role.roleAnim as AnimalAnim;
            UnityEngine.Renderer renderer = null;
            if (hero != null && hero.spinePlayer != null) renderer = hero.spinePlayer.getMeshRenderer();
            else if (monster != null && monster.spinePlayer != null) renderer = monster.spinePlayer.getMeshRenderer();
            else if (animal != null && animal.animalPlayer != null)
                renderer = animal.animalPlayer.player.getMeshGroup().getMeshRenderer();
            if (renderer != null)
                renderer.enabled = (animal == null && role.roleBean.isBeCarryed()) || !manager.warFog.isInFog(role.cellX, role.cellY);
        }
    }
}
