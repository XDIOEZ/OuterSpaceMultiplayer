using System;
using System.Linq;
using System.Reflection;
using SmartAI;
using HarmonyLib;
using SmartAI.Core;
using SmartAI.Control;
using SmartAI.Managers;
using SmartAI.Systems;
using SmartAI.AIC;
using SmartAI.Weapon;
using SmartAI.Gameplay;
using ShipScene.Battle2._0.Scripts.SmartAI.UI;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private readonly System.Collections.Generic.HashSet<long> preparingBattleShips = new System.Collections.Generic.HashSet<long>();
        internal bool CanRunNativePlayerBattle { get { return CanRunPlayerBattle; } }
        internal bool TryStartNativePlayerBattle()
        {
            if (!HasNativePlayerBattle) return true;
            if (IsPlayerBattleReplica || !PlayerBattleActive || nativeBattleStarted) return false;
            nativeBattleStarted = true; return true;
        }
        internal bool CanFinishNativePlayerBattle { get { return finishingPlayerBattle; } }
        internal bool CanAcceptPlayerBattleInput { get { return PlayerBattleActive && !Context.IsPaused(); } }
        internal bool IsLocalPlayerBattleController(PlayerInputController controller)
        { BaseShip ship = controller.GetComponent<BaseShip>(); return ship != null && BattleShipOwner(BattleSourceUid(ship)) == Context.LocalPlayerId; }
        internal void ConfigurePlayerBattleReplica(BaseShip ship)
        {
            if (!IsPlayerBattleReplica || ship == null) return;
            if (ship.Brain != null) ship.Brain.enabled = false;
            if (ship.Pilot != null) ship.Pilot.enabled = false;
            if (ship.RVOController != null) ship.RVOController.enabled = false;
            if (ship.BTOwner != null) { ship.BTOwner.StopBehaviour(); ship.BTOwner.enabled = false; }
            Rigidbody2D body = ship.GetComponent<Rigidbody2D>(); if (body != null) body.simulated = false;
        }
        internal void ConfigureRegisteredPlayerBattleShip(BaseShip ship)
        {
            if (!HasNativePlayerBattle || ship == null) return;
            if (IsPlayerBattleReplica) { ConfigurePlayerBattleReplica(ship); return; }
            if (CurrentNativeBattle.Stage != PlayerFleetBattleStage.Preparing) return;
            preparingBattleShips.Add(ship.GetUid());
            if (ship.Brain != null) ship.Brain.enabled = false;
            if (ship.Pilot != null) ship.Pilot.enabled = false;
            if (ship.RVOController != null) ship.RVOController.enabled = false;
            if (ship.BTOwner != null) { ship.BTOwner.StopBehaviour(); ship.BTOwner.enabled = false; }
            Rigidbody2D body = ship.GetComponent<Rigidbody2D>(); if (body != null) body.simulated = false;
        }
        private void ActivatePreparedPlayerBattleShips()
        {
            if (IsPlayerBattleReplica || !PlayerBattleActive || FleetManager.Instance == null) return;
            foreach (BaseShip ship in FleetManager.Instance.GetAllShips().Values)
            {
                if (ship == null || !preparingBattleShips.Remove(ship.GetUid())) continue;
                Rigidbody2D body = ship.GetComponent<Rigidbody2D>(); if (body != null) body.simulated = true;
                NativeWire.Call(NativeWire.Get(ship, "_controlComponent"), "ToggleAI", ship.ControlMode == ShipControlMode.Autonomous);
            }
        }
        internal void RequestPlayerBattleClock(bool? paused, double? speed)
        {
            if (!HasNativePlayerBattle) return;
            if (!Context.IsHost) { FairyUIDispatcher.showToast("战斗暂停和速度由房主控制。"); return; }
            Context.RequestClock(paused ?? Context.IsPaused(), speed ?? Context.SimulationSpeed());
        }
        internal bool InterceptReplicaWing(WingManager wing, CarrierCommand mode)
        {
            BaseShip ship = wing.GetComponentInParent<BaseShip>();
            if (!IsLocalReplicaShip(ship) || applyingBattleInput) return false;
            SendPlayerBattleInput(ship, "Wing", new FleetBattleControl { Index = (int)mode }); return true;
        }
    }
    internal static class NativePlayerFleetBattleHooks
    {
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(BattleManager), "Update", "ManagerPrefix");
            Patch(harmony, typeof(BattleManager), "FixedUpdate", "SimulationPrefix");
            Patch(harmony, typeof(BattleManager), "StartBattle", "StartPrefix");
            Patch(harmony, typeof(BattleManager), "EndBattle", "EndPrefix");
            Patch(harmony, typeof(BattleManager), "FinishSettlement", "SettlementPrefix");
            Patch(harmony, typeof(BattleManager), "RecordShipDeathData", null, "DeathPostfix");
            Patch(harmony, typeof(BattleManager), "RecordShipRetreatData", null, "RetreatPostfix");
            Patch(harmony, typeof(BattleManager), "Retreat", "RetreatPrefix");
            Patch(harmony, typeof(BaseShip), "Update", "ShipPrefix");
            Patch(harmony, typeof(BaseShip), "UpdateControlLogic", "LocalControlPrefix");
            Patch(harmony, typeof(BaseShip), "HandleToggleAutoPilotInput", "AutoPilotPrefix");
            Patch(harmony, typeof(BaseShip), "SetControlMode", null, "ShipModePostfix");
            Patch(harmony, typeof(BaseShip), "OnSpawn", null, "ShipModePostfix");
            Patch(harmony, typeof(BaseShip), "SetFlightControls", "FlightPrefix");
            Patch(harmony, typeof(BaseShip), "SetVenting", "VentPrefix");
            Patch(harmony, typeof(BaseShip), "ToggleShield", "ShieldPrefix");
            Patch(harmony, typeof(BaseShip), "SetPriorityTarget", "TargetPrefix");
            Patch(harmony, typeof(ShipMotor), "FixedUpdate", "SimulationPrefix");
            Patch(harmony, typeof(ShipPilot), "FixedUpdate", "SimulationPrefix");
            Patch(harmony, typeof(PlayerInputController), "UpdateLogic", "PilotPrefix");
            Patch(harmony, typeof(PlayerInputController), "CanControl", "ControllerPermissionPrefix");
            Patch(harmony, typeof(PlayerInputController), "OnPause", "ControllerPausePrefix");
            Patch(harmony, typeof(WeaponManager), "UpdateLogic", "FirePrefix");
            Patch(harmony, typeof(WeaponManager), "SetSelectedGroup", "GroupPrefix");
            Patch(harmony, typeof(WeaponManager), "ToggleGroupAutofire", "AutoFirePrefix");
            Patch(harmony, typeof(ShipSystemManager), "TryActivateSystem", "SystemPrefix");
            Patch(harmony, typeof(ShieldManager), "UpdatePlayerAim", "ShieldAimPrefix");
            Patch(harmony, typeof(WingManager), "set_currentCommand", "WingPrefix");
            Patch(harmony, typeof(StrategicPoint), "Update", "SimulationPrefix");
            Patch(harmony, typeof(BattleManager), "SetMenuPaused", "MenuPausePrefix");
            Patch(harmony, typeof(BattleManager), "PauseBattle", "NativePausePrefix");
            Patch(harmony, typeof(BattleManager), "ResumeBattle", "NativeResumePrefix");
            Patch(harmony, typeof(BattleManager), "ApplyPause", "NativeApplyPausePrefix");
            Patch(harmony, typeof(BattleEnvironmentProfile), "ResolveEncounterEnvironment", "EnvironmentPrefix");
            Patch(harmony, typeof(BattleGameInfoView), "OnPauseAndPlaySelected", "PauseInputPrefix");
            Patch(harmony, typeof(BattleGameInfoView), "OnNormalSpeedSelected", "NormalSpeedPrefix");
            foreach (MethodInfo method in typeof(FleetManager).GetMethods().Where(m => m.Name == "TrySetPlayerSeat" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(BaseShip)))
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(NativePlayerFleetBattleHooks), "SeatPostfix"));
            foreach (MethodInfo method in typeof(FleetManager).GetMethods().Where(m => m.Name == "RegisterShip"))
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(NativePlayerFleetBattleHooks), "RegisteredPostfix"));
            foreach (string name in new[] { "IssueMoveOrder", "IssueAttackOrder", "IssueDefendOrder", "IssueRetreatOrder", "IssueCaptureOrder", "ClearOrder" })
                Patch(harmony, typeof(AIC_Brain), name, "OrderPrefix");
            Patch(harmony, typeof(AIC_Brain), "AppendMoveWaypoint", "AppendPrefix");
            foreach (Type type in new[] { typeof(BaseShip), typeof(ShipCore) })
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.ReturnType == typeof(float) && new[] { "ApplyDamage", "ApplyDamageWithArmorHitStrength", "ApplyCollisionDamage" }.Contains(m.Name)))
                    harmony.Patch(method, new HarmonyMethod(typeof(NativePlayerFleetBattleHooks), "DamagePrefix"));
        }
        private static void Patch(Harmony h, Type t, string n, string pre = null, string post = null)
        {
            MethodInfo method = AccessTools.Method(t, n); if (method == null) throw new MissingMethodException(t.FullName, n);
            h.Patch(method, pre == null ? null : new HarmonyMethod(typeof(NativePlayerFleetBattleHooks), pre), post == null ? null : new HarmonyMethod(typeof(NativePlayerFleetBattleHooks), post));
        }
        private static bool ManagerPrefix() { return Bridge == null || Bridge.UpdatePlayerBattleManager(); }
        private static bool SimulationPrefix() { return Bridge == null || !Bridge.HasNativePlayerBattle || Bridge.CanRunNativePlayerBattle; }
        private static bool StartPrefix() { return Bridge == null || Bridge.TryStartNativePlayerBattle(); }
        private static bool SettlementPrefix() { return Bridge == null || !Bridge.HasNativePlayerBattle || Bridge.CanFinishNativePlayerBattle; }
        private static bool EndPrefix(BattleResultData.ResultType __0) { return Bridge == null || !Bridge.InterceptPlayerBattleEnd(__0); }
        private static bool ShipPrefix(BaseShip __instance) { return Bridge == null || Bridge.UpdateNetworkShip(__instance); }
        private static bool LocalControlPrefix(BaseShip __instance) { return Bridge == null || Bridge.IsLocalBattleInputShip(__instance); }
        private static bool AutoPilotPrefix(BaseShip __instance) { return Bridge == null || !Bridge.InterceptPlayerBattleAutoPilot(__instance); }
        private static void ShipModePostfix(BaseShip __instance) { if (Bridge != null) Bridge.ConfigureRegisteredPlayerBattleShip(__instance); }
        private static bool PilotPrefix(PlayerInputController __instance) { return Bridge == null || Bridge.UpdateNetworkPilot(__instance); }
        private static bool ControllerPermissionPrefix(PlayerInputController __instance, ref bool __result)
        {
            if (Bridge == null || !Bridge.HasNativePlayerBattle) return true;
            if (Bridge.CanAcceptPlayerBattleInput && Bridge.IsLocalPlayerBattleController(__instance)) return true;
            __result = false; return false;
        }
        private static void RegisteredPostfix(BaseShip __0) { if (Bridge != null) Bridge.ConfigureRegisteredPlayerBattleShip(__0); }
        private static bool FlightPrefix(BaseShip __instance, float __0, float __1, float __2)
        { return Bridge == null || !Bridge.CaptureReplicaFlight(__instance, __0, __1, __2); }
        private static bool FirePrefix(WeaponManager __instance, Vector2 __0, bool __1, ShipCore __2, bool __3)
        {
            if (Bridge == null || !Bridge.HasNativePlayerBattle) return true;
            BaseShip ship = __instance.GetComponentInParent<BaseShip>();
            if (Bridge.CaptureReplicaFire(ship, __0, __1, __2, __3)) return false;
            return Bridge.CanRunNativePlayerBattle;
        }
        private static bool VentPrefix(BaseShip __instance, bool __0)
        { return Bridge == null || !Bridge.InterceptReplicaDiscrete(__instance, "Vent", new GameBridge.FleetBattleControl { Flag = __0 }, false); }
        private static bool ShieldPrefix(BaseShip __instance, bool __0)
        { return Bridge == null || !Bridge.InterceptReplicaDiscrete(__instance, "Shield", new GameBridge.FleetBattleControl { Flag = __0 }, false); }
        private static bool TargetPrefix(BaseShip __instance, BaseShip __0)
        {
            if (Bridge == null || !Bridge.IsLocalReplicaShip(__instance)) return true;
            // The original local target highlight stays usable; the pilot packet carries the
            // corresponding real ship identity to the authority before any weapon evaluation.
            return true;
        }
        private static bool GroupPrefix(WeaponManager __instance, int __0)
        { return Bridge == null || !Bridge.InterceptReplicaDiscrete(__instance.GetComponentInParent<BaseShip>(), "Group", new GameBridge.FleetBattleControl { Index = __0 }, true); }
        private static bool AutoFirePrefix(WeaponManager __instance, int __0)
        { return Bridge == null || !Bridge.InterceptReplicaDiscrete(__instance.GetComponentInParent<BaseShip>(), "AutoFire", new GameBridge.FleetBattleControl { Index = __0 }, true); }
        private static bool SystemPrefix(ShipSystemManager __instance)
        { return Bridge == null || !Bridge.InterceptReplicaDiscrete(__instance.GetComponentInParent<BaseShip>(), "System", new GameBridge.FleetBattleControl(), false); }
        private static bool ShieldAimPrefix(ShieldManager __instance) { return Bridge == null || !Bridge.UpdateRemotePlayerBattleShieldAim(__instance); }
        private static bool WingPrefix(WingManager __instance, CarrierCommand __0) { return Bridge == null || !Bridge.InterceptReplicaWing(__instance, __0); }
        private static bool OrderPrefix(AIC_Brain __instance, MethodBase __originalMethod, object[] __args)
        { return Bridge == null || !Bridge.InterceptReplicaOrder(__instance, __originalMethod.Name, __args); }
        private static bool AppendPrefix(AIC_Brain __instance, object[] __args, ref bool __result)
        { if (Bridge == null || !Bridge.InterceptReplicaOrder(__instance, "AppendMoveWaypoint", __args)) return true; __result = true; return false; }
        private static void SeatPostfix(BaseShip __0, bool __result) { if (__result && Bridge != null) Bridge.SendReplicaSeat(__0); }
        private static void DeathPostfix(BaseShip __0) { if (Bridge != null) Bridge.RecordPlayerBattleShipExit(__0, true); }
        private static void RetreatPostfix(BaseShip __0) { if (Bridge != null) Bridge.RecordPlayerBattleShipExit(__0, false); }
        private static bool RetreatPrefix() { return Bridge == null || !Bridge.InterceptPlayerBattleRetreat(); }
        private static bool DamagePrefix(ref float __result)
        { if (SimulationPrefix()) return true; __result = 0; return false; }
        private static bool PauseInputPrefix()
        {
            if (Bridge == null || !Bridge.HasNativePlayerBattle) return true;
            Bridge.RequestPlayerBattleClock(!Bridge.Context.IsPaused(), null); return false;
        }
        private static bool ControllerPausePrefix(PlayerInputController __instance)
        {
            if (Bridge == null || !Bridge.HasNativePlayerBattle) return true;
            if (Bridge.IsLocalPlayerBattleController(__instance)) Bridge.RequestPlayerBattleClock(!Bridge.Context.IsPaused(), null);
            return false;
        }
        private static bool NormalSpeedPrefix()
        {
            if (Bridge == null || !Bridge.HasNativePlayerBattle) return true;
            Bridge.RequestPlayerBattleClock(false, 1); return false;
        }
        private static bool MenuPausePrefix(object __0, bool __1)
        {
            // The menu still blocks this player's input through native UI focus. It cannot
            // pause the shared simulation or write process-global timeScale independently.
            if (Bridge == null || !Bridge.HasNativePlayerBattle) return true;
            Bridge.SetPlayerBattleMenuFocus(__0, __1); return false;
        }
        private static bool NativePausePrefix()
        { if (Bridge == null || !Bridge.HasNativePlayerBattle || Bridge.IsApplyingPlayerBattleClock) return true; Bridge.RequestPlayerBattleClock(true, null); return false; }
        private static bool NativeResumePrefix()
        { if (Bridge == null || !Bridge.HasNativePlayerBattle || Bridge.IsApplyingPlayerBattleClock) return true; Bridge.RequestPlayerBattleClock(false, null); return false; }
        private static bool NativeApplyPausePrefix()
        { return Bridge == null || !Bridge.HasNativePlayerBattle || Bridge.IsApplyingPlayerBattleClock || Bridge.Context.IsPaused(); }
        private static bool EnvironmentPrefix(SmartAI.Data.BattleEncounterEnvironmentSnapshot __0, ref SmartAI.Data.BattleEncounterEnvironmentSnapshot __result)
        // Native prefab debug settings otherwise replace the real galaxy snapshot before
        // asteroid mesh/profile selection. They must not create a second participant layout.
        { if (Bridge == null || !Bridge.HasNativePlayerBattle) return true; __result = __0; return false; }
    }
}
