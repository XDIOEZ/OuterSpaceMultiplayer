using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class FleetState { public string PlayerId; public long FleetId; public string NativeFleet; }
        private sealed class FleetCompletion { public string CommandId; public bool Success; }
        private sealed class FleetFrame
        {
            public List<FleetState> Fleets = new List<FleetState>();
            public List<string> Online = new List<string>();
            public List<FleetCompletion> Completed = new List<FleetCompletion>();
            public List<TransitState> Transit = new List<TransitState>();
            public long WorldTimeMM;
            public SharedWorldDelta Shared;
        }
        internal sealed class FleetOrder
        {
            public string Operation;
            public string GalaxyId;
            public float X, Y;
            public sbyte VoyageType;
            public bool LocalLogic, SuppressToast, AllowRestartDirectionChange, PreserveFollowingTarget;
            public string GoalJson;
            public long TargetNpcUid = -1;
            public long TargetPlatformUid = -1;
        }
        private readonly Dictionary<FWGameData, AirShipLocalData> nativeGalaxyCaches = new Dictionary<FWGameData, AirShipLocalData>();
        private readonly Dictionary<long, AirShipFleetInfoBean> extraFleetViews = new Dictionary<long, AirShipFleetInfoBean>();
        private readonly Dictionary<string, callbackbool> fleetCallbacks = new Dictionary<string, callbackbool>();
        private readonly Dictionary<string, FleetCompletion> completedFleetCommands = new Dictionary<string, FleetCompletion>();
        private readonly HashSet<long> nativeMotionHandled = new HashSet<long>();
        private bool steppingPlayerFleets;
        private double nextFleetPresentation;

        // AirShipLocalData has per-process cached fleetInfoBean/listShips, while getGameData reads UserBean.
        // The patched singleton getter chooses the cache by the actual current game-data object, including
        // temporary starter construction. This does not initialize/reset the global NPC or galaxy managers.
        internal AirShipLocalData NativeGalaxyCache(FWGameData data)
        {
            AirShipLocalData cache;
            if (!nativeGalaxyCaches.TryGetValue(data, out cache))
            {
                cache = new AirShipLocalData(); nativeGalaxyCaches.Add(data, cache);
                if (data.galaxyBlock != null)
                {
                    foreach (FWAirShip ship in data.galaxyBlock.airShipList)
                    {
                        var wrapper = new AirShipDataBean_Variant(); wrapper.setData(ship); cache.listShips.Add(wrapper);
                    }
                }
            }
            if (data.galaxyBlock != null && data.galaxyBlock.playerFleet != null && data.galaxyBlock.playerFleet.pos != null)
            {
                var view = NativeWire.Get(cache, "fleetInfoBean") as AirShipFleetInfoBean;
                if (view == null || !ReferenceEquals(view.bean, data.galaxyBlock.playerFleet))
                {
                    view = new AirShipFleetInfoBean();
                    NativeWire.Set(cache, "fleetInfoBean", view); // Install before native callbacks can query the singleton.
                    view.setFleetGalaxy(data.galaxyBlock.playerFleet);
                }
            }
            return cache;
        }
        internal bool TryGetNativeGalaxyCache(out AirShipLocalData cache)
        {
            FWGameData current = UserBean.getInstance().gameData;
            if (current == null || ReferenceEquals(current, singlePlayerData)) { cache = null; return false; }
            cache = NativeGalaxyCache(current); return true;
        }
        private AirShipFleetInfoBean FleetView(string playerId)
        {
            using (EnterPlayer(playerId)) return (AirShipFleetInfoBean)NativeWire.Get(NativeGalaxyCache(Players[playerId]), "fleetInfoBean");
        }
        private IEnumerable<FWFleet> OwnedFleets(string playerId)
        {
            FWGameData data = Players[playerId];
            var seen = new HashSet<long>();
            if (data.galaxyBlock.playerFleet != null && seen.Add(data.galaxyBlock.playerFleet.getUID())) yield return data.galaxyBlock.playerFleet;
            // Native FWGBGalaxy/getCurFleet explicitly exposes one current player fleet. Legacy world
            // fleet lists are NPC/task fleets unless separately registered to a multiplayer PlayerId.
            foreach (FWGalaxy galaxy in data.galaxyBlock.knownGalaxies)
                foreach (FWFleet fleet in galaxy.galaxyFleetList.OfType<FWFleet>())
                    if (Context.OwnerOfEntity(fleet.getUID()) == playerId && seen.Add(fleet.getUID())) yield return fleet;
        }
        private AirShipFleetInfoBean FleetView(string playerId, FWFleet fleet)
        {
            if (ReferenceEquals(Players[playerId].galaxyBlock.playerFleet, fleet)) return FleetView(playerId);
            AirShipFleetInfoBean view;
            if (!extraFleetViews.TryGetValue(fleet.getUID(), out view))
            {
                view = new AirShipFleetInfoBean(); extraFleetViews.Add(fleet.getUID(), view);
                using (EnterPlayer(playerId)) view.setFleetGalaxy(fleet);
            }
            else if (!ReferenceEquals(view.bean, fleet)) using (EnterPlayer(playerId)) view.setFleetGalaxy(fleet);
            return view;
        }
        internal void TickPlayerFleets(long deltaMM)
        {
            steppingPlayerFleets = true;
            try
            {
                foreach (var pair in Players)
                    foreach (FWFleet fleet in OwnedFleets(pair.Key).ToList())
                    {
                        if (!Online.Contains(pair.Key))
                        {
                            if (fleet.voyage != null) fleet.voyage.pauseTimeLength = checked(fleet.voyage.pauseTimeLength + deltaMM);
                            continue;
                        }
                        using (EnterPlayer(pair.Key))
                        {
                            AirShipFleetInfoBean view = FleetView(pair.Key, fleet);
                            if (view == null) throw new InvalidOperationException("Player fleet has no native wrapper.");
                            var scene = GalaxySceneInterface.getInstance();
                            string visibleGalaxy = scene == null ? null : scene.getGalaxyId();
                            bool inVisibleGalaxy = visibleGalaxy != null && visibleGalaxy == view.getCurGalaxyId();
                            bool handled = (bool)NativeWire.Call(view, "TickLocalMotion", inVisibleGalaxy, visibleGalaxy,
                                deltaMM / 1000f, WorldTimeMM, Time.frameCount);
                            if (handled) nativeMotionHandled.Add(fleet.getUID()); else nativeMotionHandled.Remove(fleet.getUID());
                            AirShipMoveBeanBase motion = view.getMoveBean();
                            if (motion != null) motion.logic();
                        }
                    }
                RefreshFleetVisibility();
            }
            finally { steppingPlayerFleets = false; nativeMotionHandled.Clear(); }
        }
        internal bool TickNativeFleetPrefix(AirShipFleetInfoBean view, int argumentCount, ref bool result)
        {
            if (Applying) { result = false; return false; }
            if (!Advancing || !steppingPlayerFleets) { result = false; return false; }
            if (argumentCount == 0) { result = view.bean != null && nativeMotionHandled.Contains(view.bean.getUID()); return false; }
            return true;
        }
        public GalaxySnapshot CaptureGalaxy(string playerId)
        {
            FleetFrame frame = new FleetFrame { WorldTimeMM = WorldTimeMM, Online = Online.ToList(), Completed = completedFleetCommands.Values.ToList(), Shared = CaptureSharedWorld(playerId) };
            foreach (var pair in Players)
            {
                foreach (FWFleet fleet in OwnedFleets(pair.Key)) frame.Fleets.Add(new FleetState { PlayerId = pair.Key, FleetId = fleet.getUID(), NativeFleet = NativeWire.Pack(fleet) });
                frame.Transit.Add(CaptureTransit(pair.Key));
            }
            return new GalaxySnapshot { NativeJson = NativeWire.Json(frame), Revision = WorldTimeMM };
        }
        public BridgeResult ApplyGalaxy(GalaxySnapshot galaxy)
        {
            return Run(() =>
            {
                FleetFrame frame = NativeWire.Parse<FleetFrame>(galaxy.NativeJson);
                Applying = true;
                try
                {
                    Online.Clear(); foreach (string id in frame.Online) Online.Add(id);
                    ApplySharedWorld(frame.Shared);
                    foreach (TransitState state in frame.Transit) ApplyTransit(state);
                    foreach (FleetState state in frame.Fleets)
                    {
                        FWGameData player;
                        if (!Players.TryGetValue(state.PlayerId, out player)) throw new InvalidOperationException("Galaxy frame references unknown player.");
                        FWFleet incoming = NativeWire.Unpack<FWFleet>(state.NativeFleet);
                        if (incoming.getUID() != state.FleetId || Context.OwnerOfEntity(state.FleetId) != state.PlayerId) throw new InvalidOperationException("Fleet frame ownership mismatch.");
                        FWFleet existing = OwnedFleets(state.PlayerId).SingleOrDefault(f => f.getUID() == state.FleetId);
                        if (existing == null) throw new InvalidOperationException("Fleet frame references an unregistered fleet.");
                        existing.update(incoming); player.elapsedMM = frame.WorldTimeMM;
                        using (EnterPlayer(state.PlayerId)) FleetView(state.PlayerId, existing).setFleetGalaxy(existing);
                    }
                    foreach (string id in Players.Keys) RebindTransitArrivalsToFleet(id);
                    RefreshFleetVisibility();
                    foreach (FleetCompletion completion in frame.Completed) CompleteLocalFleetCallback(completion.CommandId, completion.Success);
                }
                finally { Applying = false; }
            });
        }
        internal void RefreshFleetVisibility()
        {
            var manager = GalaxyScenePlayerFleetManager.instance;
            if (manager == null) return;
            var galaxy = NativeWire.Get(manager, "galaxy") as GalaxyStar;
            if (galaxy == null) return;
            string galaxyId = galaxy.getGalaxyId();
            foreach (var pair in Players)
                foreach (FWFleet fleet in OwnedFleets(pair.Key))
                    if (Online.Contains(pair.Key) && fleet.pos != null && fleet.pos.galaxyId == galaxyId)
                        using (EnterPlayer(pair.Key)) manager.addFleetBean_Update(FleetView(pair.Key, fleet));
            foreach (GalaxyPlayerFleet projection in manager.fleetItems.ToList()) ProjectFleet(projection);
        }
        internal void ProjectFleet(GalaxyPlayerFleet projection)
        {
            if (projection == null || projection.fleetbean == null || projection.fleetbean.bean == null) return;
            FWFleet fleet = projection.fleetbean.bean;
            string owner = Context.OwnerOfEntity(fleet.getUID());
            if (owner == null) return;
            var scene = GalaxySceneInterface.getInstance();
            bool visible = Online.Contains(owner) && fleet.pos != null && scene != null && fleet.pos.galaxyId == scene.getGalaxyId();
            projection.setVisible(visible);
            if (!visible) return;
            Vector3 position = (Vector3)NativeWire.Call(projection.fleetbean, "getCurPos");
            projection.transform.localPosition = position; // Presentation only; never advance or write the voyage.
            projection.setFleetRotation((float)fleet.pos.angle);
            object label = NativeWire.Get(projection, "fleetUI");
            if (label != null) NativeWire.Call(label, "UpdateSelfPosition", position, false);
        }
        internal void GalaxyPresentationTick()
        {
            if (Time.unscaledTimeAsDouble < nextFleetPresentation) return;
            nextFleetPresentation = Time.unscaledTimeAsDouble + 0.5;
            RefreshFleetVisibility();
        }
        public BridgeResult SetPlayerOnline(string playerId, bool online)
        {
            return Run(() =>
            {
                if (!Players.ContainsKey(playerId)) throw new InvalidOperationException("Unknown player online state.");
                if (online) Online.Add(playerId); else Online.Remove(playerId);
                if (Context.IsHost) sharedBaselineRecipients.Remove(playerId);
                RefreshFleetVisibility();
            });
        }
        internal CommandResult RouteFleetCommand(AirShipFleetInfoBean view, FleetOrder order, callbackbool callback = null)
        {
            if (view == null || view.bean == null) throw new InvalidOperationException("No player fleet selected.");
            if (Context.OwnerOfEntity(view.bean.getUID()) != Context.LocalPlayerId) throw new InvalidOperationException("Cannot navigate another player's fleet.");
            var command = new GameCommand { Kind = CommandKind.FleetCommand, EntityId = view.bean.getUID(), MapId = -1, PayloadJson = NativeWire.Json(order) };
            if (callback != null) fleetCallbacks.Add(command.CommandId, callback);
            CommandResult result = Context.SubmitCommand(command);
            if (!result.Success) { CompleteLocalFleetCallback(command.CommandId, false); throw new InvalidOperationException(result.Error); }
            return result;
        }
        public BridgeResult ExecuteFleetCommand(GameCommand command)
        {
            return Run(() =>
            {
                if (!Context.IsHost || !Online.Contains(command.PlayerId) || Context.OwnerOfEntity(command.EntityId) != command.PlayerId) throw new InvalidOperationException("Fleet navigation requires the online owning player and host authority.");
                FleetOrder order = NativeWire.Parse<FleetOrder>(command.PayloadJson);
                if (order == null || float.IsNaN(order.X) || float.IsInfinity(order.X) || float.IsNaN(order.Y) || float.IsInfinity(order.Y)) throw new InvalidOperationException("Invalid navigation destination.");
                using (EnterPlayer(command.PlayerId))
                {
                    FWFleet fleet = OwnedFleets(command.PlayerId).SingleOrDefault(f => f.getUID() == command.EntityId);
                    if (fleet == null) throw new InvalidOperationException("Unknown owned fleet.");
                    AirShipFleetInfoBean view = FleetView(command.PlayerId, fleet);
                    bool previous = Executing; Executing = true;
                    try
                    {
                        callbackbool finish = success =>
                        {
                            completedFleetCommands[command.CommandId] = new FleetCompletion { CommandId = command.CommandId, Success = success };
                            if (completedFleetCommands.Count > 256) completedFleetCommands.Remove(completedFleetCommands.Keys.First());
                            CompleteLocalFleetCallback(command.CommandId, success);
                        };
                        if (order.Operation == "Move" || order.Operation == "MoveUi")
                        {
                            GalaxyStar galaxy = GalaxyMapData.getInstance().getGalaxyStar(order.GalaxyId);
                            if (galaxy == null) throw new InvalidOperationException("Navigation target galaxy does not exist.");
                            FWGoalPos goal = String.IsNullOrEmpty(order.GoalJson) ? null : NativeWire.Unpack<FWGoalPos>(order.GoalJson);
                            bool accepted = order.Operation == "MoveUi"
                                ? view.MoveTo_UIPos(galaxy, new Vector3(order.X, order.Y, 0), finish, order.LocalLogic)
                                : view.MoveTo_Pos(galaxy, new Vector2(order.X, order.Y), finish, order.VoyageType, order.LocalLogic, goal,
                                    order.SuppressToast, order.AllowRestartDirectionChange, order.PreserveFollowingTarget);
                            if (!accepted) throw new InvalidOperationException("Original fleet rules rejected this voyage; no local client voyage was created.");
                        }
                        else if (order.Operation == "Stop") { view.FinishMove(); finish(true); }
                        else if (order.Operation == "CancelFollow") { view.cancelFollowingTargetMovement(); finish(true); }
                        else if (order.Operation == "StopFollow") { view.stopFollowNpc(); finish(true); }
                        else if (order.Operation == "Pause") { if (view.getMoveBean() != null) view.getMoveBean().PauseVoyage(); finish(true); }
                        else if (order.Operation == "Resume") { if (view.getMoveBean() != null) view.getMoveBean().ResumeVoyage(); finish(true); }
                        else if (order.Operation == "Follow")
                        {
                            var target = NativeWire.Call(typeof(TK_NPCSetBeanDatas), "getNpcByUid", order.TargetNpcUid) as TK_NPCSetBean;
                            if (target == null)
                            {
                                FWNpc npc = Players[command.PlayerId].getNpc(order.TargetNpcUid);
                                if (npc == null) throw new InvalidOperationException("Follow target no longer exists in the host world.");
                                target = TK_NPCSetBeanDatas.addNpcBean(order.GalaxyId, npc);
                            }
                            if (target == null || !view.beginFollowingTarget(target)) throw new InvalidOperationException("Original follow command rejected.");
                        }
                        else if (order.Operation == "Stargate")
                        {
                            GalaxyStar galaxy = GalaxyMapData.getInstance().getGalaxyStar(order.GalaxyId);
                            var target = galaxy == null ? null : NativeWire.Call(galaxy.fwGalaxyBean, "getGalaxyPlatformByUid", order.TargetPlatformUid) as G_StellarPlatformBean;
                            if (target == null) throw new InvalidOperationException("Stargate target no longer exists.");
                            GalaxyServicesLogic.Instance.servicesLogic_Fleet.FleetThroughStargate(view, target, null); finish(true);
                        }
                        else throw new InvalidOperationException("Unknown fleet navigation operation.");
                    }
                    finally { Executing = previous; }
                }
                RefreshFleetVisibility();
            });
        }
        internal void ObserveFleetCommandResult(CommandResult result) { if (!result.Success) CompleteLocalFleetCallback(result.CommandId, false); }
        private void CompleteLocalFleetCallback(string id, bool success)
        {
            callbackbool callback;
            if (id != null && fleetCallbacks.TryGetValue(id, out callback)) { fleetCallbacks.Remove(id); callback(success); }
        }
        internal void DisposeGalaxy()
        {
            foreach (AirShipLocalData cache in nativeGalaxyCaches.Values) cache.LogOut();
            nativeGalaxyCaches.Clear(); extraFleetViews.Clear(); fleetCallbacks.Clear(); completedFleetCommands.Clear();
        }
    }

    internal static class NativeGalaxyHooks
    {
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        internal static void Install(Harmony harmony)
        {
            NativeNpcProjectionHooks.Install(harmony);
            harmony.Patch(AccessTools.PropertyGetter(typeof(AirShipLocalData), "Instance"), new HarmonyMethod(typeof(NativeGalaxyHooks), nameof(CachePrefix)));
            foreach (MethodInfo method in typeof(AirShipFleetInfoBean).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == "TickLocalMotion"))
                harmony.Patch(method, new HarmonyMethod(typeof(NativeGalaxyHooks), nameof(MotionPrefix)));
            foreach (string name in new[] { "MoveTo_UIPos", "MoveTo_Pos", "beginFollowingTarget" })
                harmony.Patch(Require(typeof(AirShipFleetInfoBean), name), new HarmonyMethod(typeof(NativeGalaxyHooks), nameof(NavigationPrefix)));
            foreach (string name in new[] { "FinishMove", "cancelFollowingTargetMovement", "stopFollowNpc" })
                harmony.Patch(Require(typeof(AirShipFleetInfoBean), name), new HarmonyMethod(typeof(NativeGalaxyHooks), nameof(StopPrefix)));
            foreach (string name in new[] { "PauseVoyage", "ResumeVoyage" })
                harmony.Patch(Require(typeof(AirShipMoveBeanBase), name), new HarmonyMethod(typeof(NativeGalaxyHooks), nameof(VoyagePausePrefix)));
            harmony.Patch(Require(typeof(ServicesLogic_Fleet_Local), "FleetThroughStargate"), new HarmonyMethod(typeof(NativeGalaxyHooks), nameof(StargatePrefix)));
            harmony.Patch(Require(typeof(GalaxyPlayerFleet), "Update"), postfix: new HarmonyMethod(typeof(NativeGalaxyHooks), nameof(ProjectionPostfix)));
            foreach (string name in new[] { "setData", "refreData" })
                harmony.Patch(Require(typeof(GalaxyScenePlayerFleetManager), name), postfix: new HarmonyMethod(typeof(NativeGalaxyHooks), nameof(ManagerPostfix)));
        }
        private static MethodInfo Require(Type type, string name) { return AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name); }
        private static bool Bypass { get { return Bridge == null || Bridge.Applying || Bridge.Executing || Bridge.Advancing || Bridge.Seeding; } }
        private static bool CachePrefix(ref AirShipLocalData __result)
        {
            if (Bridge == null) return true;
            AirShipLocalData cache;
            if (!Bridge.TryGetNativeGalaxyCache(out cache)) return true;
            __result = cache; return false;
        }
        private static bool MotionPrefix(AirShipFleetInfoBean __instance, object[] __args, ref bool __result)
        { return Bridge == null || Bridge.TickNativeFleetPrefix(__instance, __args.Length, ref __result); }
        private static bool NavigationPrefix(AirShipFleetInfoBean __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            if (Bypass) return true;
            var order = new GameBridge.FleetOrder(); callbackbool callback = null;
            if (__originalMethod.Name == "beginFollowingTarget")
            {
                var target = (TK_NPCSetBean)__args[0];
                if (target == null) throw new InvalidOperationException("No follow target.");
                order.Operation = "Follow"; order.TargetNpcUid = target.getNpcUid(); order.GalaxyId = target.getGalaxyId();
            }
            else
            {
                order.Operation = __originalMethod.Name == "MoveTo_UIPos" ? "MoveUi" : "Move";
                order.GalaxyId = ((GalaxyStar)__args[0]).getGalaxyId();
                Vector2 position = __args[1] is Vector3 ? (Vector2)(Vector3)__args[1] : (Vector2)__args[1];
                order.X = position.x; order.Y = position.y; callback = __args[2] as callbackbool;
                if (order.Operation == "MoveUi") order.LocalLogic = (bool)__args[3];
                else
                {
                    order.VoyageType = (sbyte)__args[3]; order.LocalLogic = (bool)__args[4];
                    order.GoalJson = __args[5] == null ? null : NativeWire.Pack(__args[5]);
                    order.SuppressToast = (bool)__args[6]; order.AllowRestartDirectionChange = (bool)__args[7]; order.PreserveFollowingTarget = (bool)__args[8];
                }
            }
            __result = Bridge.RouteFleetCommand(__instance, order, callback).Success; return false;
        }
        private static bool StopPrefix(AirShipFleetInfoBean __instance, MethodBase __originalMethod)
        {
            if (Bypass) return true;
            Bridge.RouteFleetCommand(__instance, new GameBridge.FleetOrder { Operation = __originalMethod.Name == "FinishMove" ? "Stop" : __originalMethod.Name == "stopFollowNpc" ? "StopFollow" : "CancelFollow" }); return false;
        }
        private static bool VoyagePausePrefix(AirShipMoveBeanBase __instance, MethodBase __originalMethod)
        {
            if (Bypass) return true;
            AirShipFleetInfoBean view = __instance.getFleetBean();
            if (view == null || view.bean == null || Bridge.Context.OwnerOfEntity(view.bean.getUID()) == null) return true;
            Bridge.RouteFleetCommand(view, new GameBridge.FleetOrder { Operation = __originalMethod.Name == "PauseVoyage" ? "Pause" : "Resume" }); return false;
        }
        private static bool StargatePrefix(AirShipFleetInfoBean curFleetBean, G_StellarPlatformBean targetPlatbean, callback closeCallback)
        {
            if (Bypass) return true;
            Bridge.RouteFleetCommand(curFleetBean, new GameBridge.FleetOrder { Operation = "Stargate", GalaxyId = targetPlatbean.galaxyId, TargetPlatformUid = (long)NativeWire.Call(targetPlatbean, "getUid") }, success => { if (success && closeCallback != null) closeCallback(); }); return false;
        }
        private static void ProjectionPostfix(GalaxyPlayerFleet __instance) { if (Bridge != null) Bridge.ProjectFleet(__instance); }
        private static void ManagerPostfix() { if (Bridge != null) Bridge.RefreshFleetVisibility(); }
    }
}
