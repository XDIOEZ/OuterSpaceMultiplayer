using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Script.FreeWorldAirShip.Galaxy.NPCFleet;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private readonly List<GalaxyNPCFleet> replicaNpcViews = new List<GalaxyNPCFleet>();
        private readonly Dictionary<long, FWFleet> canonicalNpcViews = new Dictionary<long, FWFleet>();
        private string npcPresentationSignature;
        private double nextNpcPresentationRefresh;

        internal void ProjectNpcReplicas(GalaxyNPCFleetManager manager)
        {
            if (Context.IsHost || manager == null || World == null) return;
            NativeWire.Call(manager, "FlushPendingFleetPresentations");
            canonicalNpcViews.Clear();
            foreach (FWFactionFleet record in World.factionFleetRepository)
                if (record.materializedState != null) canonicalNpcViews[record.materializedState.getUID()] = record.materializedState;
            foreach (FWGalaxy galaxy in World.galaxyBlock.knownGalaxies)
                foreach (FWFleet fleet in galaxy.galaxyFleetList.OfType<FWFleet>())
                    if (Context.OwnerOfEntity(fleet.getUID()) == null) canonicalNpcViews[fleet.getUID()] = fleet;
            manager.GetComponentsInChildren(true, replicaNpcViews);
            foreach (GalaxyNPCFleet projection in replicaNpcViews)
            {
                FWFleet canonical;
                if (!canonicalNpcViews.TryGetValue(projection.FleetUId, out canonical) || canonical.pos == null)
                {
                    projection.gameObject.SetActive(false);
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
