using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Script.FreeWorldAirShip.Galaxy.NPCFleet;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    // A scene marker, never an archive entity. FleetUid is the existing real fleet UID;
    // no NPC data, enemy definition, cargo or ship asset is generated for this shell.
    public sealed class RemotePlayerFleetProxyView : MonoBehaviour
    {
        internal GameBridge Bridge;
        internal string PlayerId, VisualKey;
        internal long FleetUid;
        // Updated by the existing visibility refresh, never an owned/archived
        // game entity. Per-frame presentation must not rescan OwnedFleets.
        internal FWFleet BoundFleet;
        internal GalaxyNPCFleet Native;
        internal GalaxyFleetVisual Visual;
        internal IEnumerator<object> Build;
        internal bool ContentDirty = true;
        private void Update() { if (Bridge != null) Bridge.PresentRemoteFleetProxy(this); }
        private void OnDestroy()
        {
            // Scene teardown may destroy the shell without going through the
            // explicit peer-removal path. Do not retain a native fleet then.
            BoundFleet = null;
            Bridge = null;
            if (Build != null) { Build.Dispose(); Build = null; }
            if (Visual != null) Visual.Dispose();
        }
    }

    public sealed partial class GameBridge
    {
        private readonly Dictionary<long, RemotePlayerFleetProxyView> remoteFleetProxies = new Dictionary<long, RemotePlayerFleetProxyView>();
        private readonly Dictionary<long, RemoteFleetPoseCheckpoint<FleetPose, Vector3>> remoteFleetPoses =
            new Dictionary<long, RemoteFleetPoseCheckpoint<FleetPose, Vector3>>();
        private readonly HashSet<long> visibleRemoteFleetIds = new HashSet<long>();
        private GameObject remoteFleetPrefab;

        internal bool IsRemoteFleetProxy(GalaxyNPCFleet view)
        { return view != null && view.GetComponent<RemotePlayerFleetProxyView>() != null; }
        private void MarkRemoteFleetContentChanged(long uid)
        {
            RemotePlayerFleetProxyView view;
            if (remoteFleetProxies.TryGetValue(uid, out view) && view != null) view.ContentDirty = true;
        }
        public bool TryResolvePlayerFleetProxy(long proxyUid, out string playerId, out long realFleetUid)
        {
            RemotePlayerFleetProxyView proxy;
            if (remoteFleetProxies.TryGetValue(proxyUid, out proxy) && proxy != null
                && Online.Contains(proxy.PlayerId) && Context.OwnerOfEntity(proxy.FleetUid) == proxy.PlayerId)
            { playerId = proxy.PlayerId; realFleetUid = proxy.FleetUid; return true; }
            playerId = null; realFleetUid = -1; return false;
        }
        internal void OpenRemoteFleetInteraction(GalaxyNPCFleet view)
        {
            RemotePlayerFleetProxyView proxy = view == null ? null : view.GetComponent<RemotePlayerFleetProxyView>();
            string owner; long fleetUid;
            if (proxy == null || !ReferenceEquals(proxy.Bridge, this) || !TryResolvePlayerFleetProxy(proxy.FleetUid, out owner, out fleetUid))
                throw new InvalidOperationException("玩家舰队代理已失效，请重新选择在线玩家舰队。");
            if (!AirShipUIUtils.isCanTouch()) return;
            if (Context.RequestPlayerFleetInteraction == null) throw new InvalidOperationException("玩家舰队交互未绑定原生面板入口。");
            Context.RequestPlayerFleetInteraction(owner, fleetUid);
        }
        private void RefreshRemoteFleetProxies(GalaxyScenePlayerFleetManager manager, string galaxyId)
        {
            visibleRemoteFleetIds.Clear();
            foreach (var player in Players)
            {
                if (player.Key == Context.LocalPlayerId || !Online.Contains(player.Key)) continue;
                foreach (FWFleet fleet in OwnedFleets(player.Key))
                {
                    RemoteFleetPoseCheckpoint<FleetPose, Vector3> pose;
                    string physicalGalaxy = !Context.IsHost && remoteFleetPoses.TryGetValue(fleet.getUID(), out pose) ? pose.Pose.GalaxyId : fleet.pos == null ? null : fleet.pos.galaxyId;
                    if (physicalGalaxy != galaxyId) continue;
                    long uid = fleet.getUID(); visibleRemoteFleetIds.Add(uid);
                    RemotePlayerFleetProxyView proxy;
                    if (!remoteFleetProxies.TryGetValue(uid, out proxy) || proxy == null)
                    {
                        if (remoteFleetPrefab == null)
                        {
                            remoteFleetPrefab = ResourceLoader.Load<GameObject>(GameAssets.GalaxyNPCFleet);
                            if (remoteFleetPrefab == null) throw new InvalidOperationException("原生玩家舰队代理预制体缺失：" + GameAssets.GalaxyNPCFleet);
                        }
                        GameObject instance = UnityEngine.Object.Instantiate(remoteFleetPrefab, manager.transform, false);
                        try
                        {
                            instance.name = "MultiplayerFleet." + uid;
                            proxy = instance.AddComponent<RemotePlayerFleetProxyView>();
                            proxy.Bridge = this; proxy.PlayerId = player.Key; proxy.FleetUid = uid;
                            proxy.Native = instance.GetComponent<GalaxyNPCFleet>();
                            if (proxy.Native == null) proxy.Native = instance.AddComponent<GalaxyNPCFleet>();
                            // The native world-ray checks isActiveAndEnabled. Keep only its
                            // click identity enabled; proxy-specific hooks suppress all NPC AI.
                            proxy.Native.enabled = true;
                            NativeWire.Set(proxy.Native, "fleetUId", uid);
                            GalaxyPlayerFleet playerController = instance.GetComponent<GalaxyPlayerFleet>();
                            if (playerController != null) { playerController.fleetbean = null; playerController.enabled = false; }
                            proxy.Visual = instance.GetComponent<GalaxyFleetVisual>();
                            if (proxy.Visual == null) proxy.Visual = instance.AddComponent<GalaxyFleetVisual>();
                            proxy.Visual.InitWidget();
                            if (fleet.pos == null) throw new InvalidOperationException("Player fleet proxy has no physical position: " + uid);
                            proxy.transform.localPosition = new Vector3(fleet.pos.gx, fleet.pos.gy, 0);
                            proxy.transform.localRotation = Quaternion.identity;
                            remoteFleetProxies[uid] = proxy;
                        }
                        catch
                        {
                            if (proxy != null) { proxy.Bridge = null; if (proxy.Native != null) proxy.Native.enabled = false; }
                            UnityEngine.Object.Destroy(instance);
                            throw;
                        }
                    }
                    // A full galaxy baseline can replace the native fleet
                    // instance under an unchanged UID. Rebind that exact
                    // object and force visual/content reconciliation.
                    if (!ReferenceEquals(proxy.BoundFleet, fleet) || proxy.PlayerId != player.Key)
                    {
                        proxy.BoundFleet = fleet;
                        proxy.PlayerId = player.Key;
                        proxy.VisualKey = null;
                        proxy.ContentDirty = true;
                    }
                }
            }
            foreach (long uid in remoteFleetProxies.Keys.Where(uid => !visibleRemoteFleetIds.Contains(uid)).ToArray()) RemoveRemoteFleetProxy(uid);
        }
        private void PushRemoteFleetPose(FleetPose pose, long sequence, bool reset)
        {
            RemoteFleetPoseCheckpoint<FleetPose, Vector3> previous;
            double now = Time.unscaledTimeAsDouble;
            bool known = remoteFleetPoses.TryGetValue(pose.FleetId, out previous);
            if (known && !reset && sequence <= previous.Sequence) return;
            bool snap = reset || !known || previous.Pose.GalaxyId != pose.GalaxyId || now - previous.Receipt > 2;
            Vector3 from = new Vector3(pose.X, pose.Y, 0); float fromAngle = pose.Angle;
            if (!snap) TryGetSynchronizedFleetPose(pose.FleetId, out from, out fromAngle);
            // Resolve all snapshot/interpolation inputs before mutating an
            // existing buffer, as in the previous fresh-object assignment.
            double duration = snap ? 0 : Math.Max(0.05, Math.Min(1, now - previous.Receipt));
            double pauseAt = Context.IsPaused() ? now : -1;
            if (known)
                previous.Replace(pose, sequence, now, from, fromAngle, duration, pauseAt);
            else
                remoteFleetPoses.Add(pose.FleetId, new RemoteFleetPoseCheckpoint<FleetPose, Vector3>(
                    pose, sequence, now, from, fromAngle, duration, pauseAt));
        }
        private bool TryGetSynchronizedFleetPose(long uid, out Vector3 position, out float angle)
        {
            RemoteFleetPoseCheckpoint<FleetPose, Vector3> buffer;
            if (!remoteFleetPoses.TryGetValue(uid, out buffer)) { position = default(Vector3); angle = 0; return false; }
            double now = Time.unscaledTimeAsDouble;
            if (Context.IsPaused()) { if (buffer.PauseAt < 0) buffer.PauseAt = now; now = buffer.PauseAt; }
            else if (buffer.PauseAt >= 0) { buffer.StartedAt += now - buffer.PauseAt; buffer.PauseAt = -1; }
            float fraction = buffer.Duration <= 0 ? 1 : Mathf.Clamp01((float)((now - buffer.StartedAt) / buffer.Duration));
            position = Vector3.Lerp(buffer.From, new Vector3(buffer.Pose.X, buffer.Pose.Y, 0), fraction);
            angle = Mathf.LerpAngle(buffer.FromAngle, buffer.Pose.Angle, fraction);
            return true;
        }
        internal void PresentRemoteFleetProxy(RemotePlayerFleetProxyView proxy)
        {
            if (Context == null || disposed || !Online.Contains(proxy.PlayerId)) return;
            FWFleet fleet = proxy.BoundFleet;
            if (fleet == null || fleet.getUID() != proxy.FleetUid || Context.OwnerOfEntity(proxy.FleetUid) != proxy.PlayerId) return;
            if (proxy.ContentDirty)
            {
                string motherType;
                using (EnterPlayer(proxy.PlayerId)) motherType = GalaxyMothershipVisual.ResolveType(FleetView(proxy.PlayerId, fleet));
                string key = fleet.moveSeed + ":" + motherType + ":" + String.Join("|", fleet.ships.Select(ship => ship.getUID() + ":" + ship.id));
                if (key != proxy.VisualKey)
                {
                    if (proxy.Build != null) proxy.Build.Dispose();
                    proxy.Build = proxy.Visual.CreateFleetVisualOverFrames(fleet.ships, fleet.moveSeed, fleet.factionUid,
                        enableShipIdleDrift: true, mothershipType: motherType, fleetUid: fleet.getUID(), fitSelectionCircleToFormation: true).GetEnumerator();
                    proxy.VisualKey = key;
                }
                proxy.Visual.SetNameplate(Context.GetPlayerDisplayName(proxy.PlayerId) + " · " + fleet.getName(), "玩家舰队");
                proxy.ContentDirty = false;
            }
            if (proxy.Build != null && !proxy.Build.MoveNext()) { proxy.Build.Dispose(); proxy.Build = null; }
            float heading = (float)fleet.pos.angle;
            if (Context.IsHost)
            {
                using (EnterPlayer(proxy.PlayerId))
                {
                    AirShipFleetInfoBean view = FleetView(proxy.PlayerId, fleet);
                    proxy.transform.localPosition = nativeFleetPosition(view);
                    heading = NativeFleetHeading(view);
                }
            }
            else
            {
                Vector3 position; float angle;
                if (TryGetSynchronizedFleetPose(proxy.FleetUid, out position, out angle))
                {
                    proxy.transform.localPosition = position;
                    heading = angle;
                }
            }
            // Native SetRotation turns each ship under VisualRoot. Rotating this
            // scene root too would apply the same heading twice and turn the formation.
            proxy.transform.localRotation = Quaternion.identity;
            proxy.Visual.ApplyPresentation(new GalaxyFleetVisual.PresentationState {
                Angle = heading,
                InBattle = Context.IsPlayerFleetBattleLocked != null && Context.IsPlayerFleetBattleLocked(proxy.FleetUid) });
        }
        private void RemoveRemoteFleetProxy(long uid)
        {
            RemotePlayerFleetProxyView proxy;
            if (!remoteFleetProxies.TryGetValue(uid, out proxy)) return;
            remoteFleetProxies.Remove(uid);
            if (proxy == null) return;
            proxy.Bridge = null;
            proxy.BoundFleet = null;
            if (proxy.Native != null) proxy.Native.enabled = false;
            try { if (proxy.Build != null) { proxy.Build.Dispose(); proxy.Build = null; } }
            finally { UnityEngine.Object.Destroy(proxy.gameObject); }
        }
        private void DisposeRemoteFleetProxies()
        {
            var failures = new List<Exception>();
            foreach (long uid in remoteFleetProxies.Keys.ToArray())
                try { RemoveRemoteFleetProxy(uid); } catch (Exception error) { failures.Add(error); }
            remoteFleetPoses.Clear(); visibleRemoteFleetIds.Clear();
            try { if (remoteFleetPrefab != null) ResourceLoader.Release(remoteFleetPrefab); }
            catch (Exception error) { failures.Add(error); }
            finally { remoteFleetPrefab = null; }
            if (failures.Count != 0) throw new AggregateException("Failed to release remote fleet presentation.", failures);
        }
    }

    internal static class NativeRemoteFleetProxyHooks
    {
        private static GameBridge Bridge => GameBridge.Active;
        internal static void Install(Harmony harmony)
        {
            foreach (string method in new[] { "Start", "Update", "UpdateFleet", "OnDestroy" })
                harmony.Patch(AccessTools.Method(typeof(GalaxyNPCFleet), method), new HarmonyMethod(typeof(NativeRemoteFleetProxyHooks), nameof(NativeLogicPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GalaxyNPCFleet), "HandleWorldPointerClick"), new HarmonyMethod(typeof(NativeRemoteFleetProxyHooks), nameof(ClickPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GalaxyNPCFleet), "SetHudHovered"), new HarmonyMethod(typeof(NativeRemoteFleetProxyHooks), nameof(HoverPrefix)));
        }
        private static bool NativeLogicPrefix(GalaxyNPCFleet __instance)
        { return Bridge == null || !Bridge.IsRemoteFleetProxy(__instance); }
        private static bool ClickPrefix(GalaxyNPCFleet __instance)
        {
            if (Bridge == null || !Bridge.IsRemoteFleetProxy(__instance)) return true;
            Bridge.OpenRemoteFleetInteraction(__instance); return false;
        }
        private static bool HoverPrefix(GalaxyNPCFleet __instance)
        { return Bridge == null || !Bridge.IsRemoteFleetProxy(__instance); }
    }
}
