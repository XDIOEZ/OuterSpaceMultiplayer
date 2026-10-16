using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Script.FreeWorldAirShip.Galaxy.NPCFleet;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        // The native manager flushes this private, parameterless method each frame.
        // Resolve it once instead of searching all native methods for every projection tick.
        private static readonly MethodInfo flushNpcPresentations = typeof(GalaxyNPCFleetManager).GetMethod(
            "FlushPendingFleetPresentations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        private readonly List<GalaxyNPCFleet> replicaNpcViews = new List<GalaxyNPCFleet>();
        private readonly Dictionary<long, FWFleet> canonicalNpcViews = new Dictionary<long, FWFleet>();
        private string npcPresentationSignature;
        private double nextNpcPresentationRefresh;

        internal void ProjectNpcReplicas(GalaxyNPCFleetManager manager)
        {
            if (Context.IsHost || manager == null || World == null) return;
            if (flushNpcPresentations == null) NativeWire.Call(manager, "FlushPendingFleetPresentations");
            else
            {
                try { flushNpcPresentations.Invoke(manager, null); }
                catch (TargetInvocationException error) { throw error.InnerException ?? error; }
            }
            canonicalNpcViews.Clear();
            foreach (FWFactionFleet record in World.factionFleetRepository)
                if (record.materializedState != null) canonicalNpcViews[record.materializedState.getUID()] = record.materializedState;
            foreach (FWGalaxy galaxy in World.galaxyBlock.knownGalaxies)
                foreach (FWBaseBean candidate in galaxy.galaxyFleetList)
                {
                    // This runs each frame; avoid allocating an OfType iterator per galaxy.
                    FWFleet fleet = candidate as FWFleet;
                    if (fleet != null && Context.OwnerOfEntity(fleet.getUID()) == null)
                        canonicalNpcViews[fleet.getUID()] = fleet;
                }
            manager.GetComponentsInChildren(true, replicaNpcViews);
            foreach (GalaxyNPCFleet projection in replicaNpcViews)
            {
                FWFleet canonical;
                if (!canonicalNpcViews.TryGetValue(projection.FleetUId, out canonical) || canonical.pos == null)
                {
                    // Already hidden projections need no repeated Unity activation call.
                    if (projection.gameObject.activeSelf) projection.gameObject.SetActive(false);
                    continue;
                }
                AirShipFleetInfoBean view = projection.NpcSetBean == null ? null : projection.NpcSetBean.getFleetBean();
                if (view != null && view.bean != canonical) view.bean.update(canonical);
                projection.transform.localPosition = new Vector3(canonical.pos.gx, canonical.pos.gy, 0);
                projection.transform.localRotation = Quaternion.Euler(0, 0, (float)canonical.pos.angle);
                if (!projection.gameObject.activeSelf) projection.gameObject.SetActive(true);
            }
            if (Time.unscaledTimeAsDouble < nextNpcPresentationRefresh) return;
            nextNpcPresentationRefresh = Time.unscaledTimeAsDouble + 1;
            var galaxyView = NativeWire.Get(manager, "galaxy") as GalaxyStar;
            if (galaxyView == null) return;
            string galaxyId = galaxyView.getGalaxyId();
            string signature = galaxyId + ":" + String.Join(",", canonicalNpcViews.Values.Where(f => f.pos != null && f.pos.galaxyId == galaxyId).Select(f => f.getUID()).OrderBy(uid => uid));
            if (signature != npcPresentationSignature)
            {
                npcPresentationSignature = signature;
                manager.SetData(galaxyView); // Native presentation queue; no client NPC simulation step.
            }
        }
    }

    internal static class NativeNpcProjectionHooks
    {
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(GalaxyNPCFleetManager), "Update"), new HarmonyMethod(typeof(NativeNpcProjectionHooks), nameof(ManagerPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GalaxyNPCFleet), "Update"), new HarmonyMethod(typeof(NativeNpcProjectionHooks), nameof(SimulationPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GalaxyNPCFleet), "UpdateFleet"), new HarmonyMethod(typeof(NativeNpcProjectionHooks), nameof(SimulationPrefix)));
            harmony.Patch(AccessTools.Method(typeof(FWFactionFleet), "getFleet"), new HarmonyMethod(typeof(NativeNpcProjectionHooks), nameof(MaterializationPrefix)));
        }
        private static bool ManagerPrefix(GalaxyNPCFleetManager __instance)
        {
            if (Bridge == null || Bridge.Context.IsHost) return true;
            Bridge.ProjectNpcReplicas(__instance); return false;
        }
        private static bool SimulationPrefix() { return Bridge == null || Bridge.Context.IsHost; }
        private static bool MaterializationPrefix(FWFactionFleet __instance, ref FWFleet __result)
        {
            if (Bridge == null || Bridge.Context.IsHost) return true;
            // Replicas never allocate private copies of NPC ships/IDs or claim a native simulation lease.
            __result = __instance.HasExternalPresence ? __instance.materializedState : null;
            return false;
        }
    }
}
