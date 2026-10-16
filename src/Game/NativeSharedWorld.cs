using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class SharedValue { public string Scope; public string Id; public string Field; public string NativeValue; }
        private sealed class MapDirectoryItem
        {
            public long MapId; public string GalaxyId; public string StarId;
            public short X, Y; public sbyte Type; public string Name; public string NameId;
            public string WorldPoint;
        }
        private sealed class SharedWorldDelta
        {
            public long Revision;
            public List<SharedValue> Values = new List<SharedValue>();
            public List<MapDirectoryItem> Maps = new List<MapDirectoryItem>();
        }
        // This is a closed business allowlist. Personal currency, technology, missions, heroes,
        // motherships, fleet cargo and active-map starSpread/serStarMap are intentionally absent.
        private static readonly HashSet<string> SharedRootFields = new HashSet<string>(new[] {
            "factionFleetRepository", "strategicFleetBattles", "galaxyEventData", "galaxyEventList",
            "exchangeRawResources", "latestExchangeRawResourceRevision", "spaceportList", "npcFreightTaskList",
            "factionList", "stellarForceList", "forceDiplomacyPairs", "forceWars", "forcePeaceAgreements",
            "BuildForces", "BuildForceMigrates", "ForceTime_mm", "ForceTime_Day", "acrossDayIndex",
            "worldAdvanceCommittedMM", "pendingWorldStepMM", "pendingWorldStepStage", "pendingWorldDay", "pendingWorldDayStage",
            "nextFleetJourneyAdvanceMM", "pendingForceSettlementDay", "completedForceSettlementDay",
            "lastForceMigrationArrivalDay", "lastForcePopulationGrowthDay", "lastForceMigrationDepartureDay",
            "lastForceTaxSettlementDay", "lastForceRegressionDay", "lastForceProjectSettlementDay",
            "lastForceDevelopmentDecisionDay", "lastNomadicFleetSpawnDay", "nomadicFleetSpawnCursorInitialized",
            "nomadicFleetSpawnDefinitionSignature", "pendingForceDailySnapshot" });
        private static readonly HashSet<string> SharedGalaxyFields = new HashSet<string>(new[] {
            "nextNomadicFleetRefreshTime", "circulationModifiers", "taskEffectModifiers", "userId", "thirdMining", "updateTime",
            "galaxyFleetList", "asserts", "ruinsList", "coolDowns", "singularPoints", "stellarAsteroidList",
            "exchangeSpace", "galaxyWeather", "stellarEvents", "eventTags" });
        private static readonly HashSet<string> SharedPlanetFields = new HashSet<string>(new[] {
            "lastActiveTime", "planetFacilityList", "planetRailFacilityList", "homeForceId", "exchangeSpace" });
        // The allowlists do not change during a room. Sort once instead of allocating a
        // new OrderBy result for every planet on every shared-world capture tick.
        private static readonly string[] OrderedRootFields = SharedRootFields.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        private static readonly string[] OrderedGalaxyFields = SharedGalaxyFields.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        private static readonly string[] OrderedPlanetFields = SharedPlanetFields.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        private readonly Dictionary<string, string> sharedFieldBaselines = new Dictionary<string, string>();
        private readonly HashSet<long> introducedMapIds = new HashSet<long>();
        private readonly HashSet<string> sharedBaselineRecipients = new HashSet<string>();
        private SharedWorldDelta cachedSharedDelta;
        private long sharedRevision;
        private long appliedSharedRevision = -1;
        private double nextSharedCapture;

        internal void RebindSharedWorldRoots(FWGameData data)
        {
            if (World == null || data == null) throw new InvalidOperationException("Cannot bind shared business fields before the canonical world exists.");
            foreach (string name in SharedRootFields)
            {
                FieldInfo field = NativeWire.Field(typeof(FWGameData), name);
                field.SetValue(data, field.GetValue(World));
            }
            NativeWire.Set(data, "fleetRepositoryIndex", null);
            var index = NativeWire.Get(data, "uidIndex") as IDictionary; if (index != null) index.Clear();
        }
        private SharedWorldDelta CaptureSharedWorld(string recipient)
        {
            SharedWorldDelta delta = CaptureSharedWorldChanges();
            if (!sharedBaselineRecipients.Add(recipient)) return delta;
            // A new/reconnected participant starts from the current canonical fields, not from
            // whichever partial change set happened to be cached for existing participants.
            var baseline = new SharedWorldDelta { Revision = delta.Revision };
            CaptureSharedFields(baseline, "world", "", World, OrderedRootFields, true);
            foreach (FWGalaxy galaxy in World.galaxyBlock.knownGalaxies.Where(g => g.id != FWGameData.MOTHERSHIP_GALAXY_ID))
            {
                CaptureSharedFields(baseline, "galaxy", galaxy.id, galaxy, OrderedGalaxyFields, true);
                foreach (FWStarInfo star in galaxy.stars)
                {
                    CaptureSharedFields(baseline, "planet", star.starId, star, OrderedPlanetFields, true);
                    foreach (FWStarMapInfo map in star.starMapList) if (map != null) baseline.Maps.Add(CaptureDirectoryItem(map, galaxy.id, star.starId));
                }
            }
            return baseline;
        }
        private static MapDirectoryItem CaptureDirectoryItem(FWStarMapInfo map, string galaxyId, string starId)
        {
            return new MapDirectoryItem { MapId = map.starMapId, GalaxyId = galaxyId, StarId = starId,
                X = map.cellX, Y = map.cellY, Type = map.type, Name = map.name, NameId = map.nameId, WorldPoint = NativeWire.Pack(map.worldPoint) };
        }
        private SharedWorldDelta CaptureSharedWorldChanges()
        {
            if (cachedSharedDelta != null && UnityEngine.Time.unscaledTimeAsDouble < nextSharedCapture) return cachedSharedDelta;
            nextSharedCapture = UnityEngine.Time.unscaledTimeAsDouble + 1;
            var delta = new SharedWorldDelta { Revision = ++sharedRevision };
            CaptureSharedFields(delta, "world", "", World, OrderedRootFields);
            foreach (FWGalaxy galaxy in World.galaxyBlock.knownGalaxies.Where(g => g.id != FWGameData.MOTHERSHIP_GALAXY_ID))
            {
                CaptureSharedFields(delta, "galaxy", galaxy.id, galaxy, OrderedGalaxyFields);
                foreach (FWStarInfo star in galaxy.stars)
                {
                    CaptureSharedFields(delta, "planet", star.starId, star, OrderedPlanetFields);
                    foreach (FWStarMapInfo map in star.starMapList)
                    {
                        if (map == null || !introducedMapIds.Add(map.starMapId)) continue;
                        delta.Maps.Add(CaptureDirectoryItem(map, galaxy.id, star.starId));
                    }
                }
            }
            cachedSharedDelta = delta; return delta;
        }
        private void CaptureSharedFields(SharedWorldDelta delta, string scope, string id, object source, string[] fields, bool force = false)
        {
            foreach (string fieldName in fields)
            {
                FieldInfo field = NativeWire.Field(source.GetType(), fieldName);
                object value = field.GetValue(source);
                if (scope == "galaxy" && fieldName == "galaxyFleetList")
                    value = ((List<FWBaseBean>)value).Where(b => !(b is FWFleet) || Context.OwnerOfEntity(((FWFleet)b).getUID()) == null).ToList();
                string encoded = NativeWire.Pack(value), key = scope + ":" + id + ":" + fieldName, previous;
                if (!force && sharedFieldBaselines.TryGetValue(key, out previous) && previous == encoded) continue;
                if (!force) sharedFieldBaselines[key] = encoded;
                delta.Values.Add(new SharedValue { Scope = scope, Id = id, Field = fieldName, NativeValue = encoded });
            }
        }
        private void ApplySharedWorld(SharedWorldDelta delta)
        {
            if (delta == null || delta.Revision <= appliedSharedRevision) return;
            // Reliable ordered delivery is required; accepting a skipped business revision would lose fields.
            if (appliedSharedRevision >= 0 && delta.Revision != appliedSharedRevision + 1) throw new InvalidOperationException("Shared world business delta is missing a revision.");
            var decoded = new List<Tuple<SharedValue, object, FieldInfo, object>>();
            foreach (SharedValue change in delta.Values)
            {
                object target; HashSet<string> allowed;
                if (change.Scope == "world") { target = World; allowed = SharedRootFields; }
                else if (change.Scope == "galaxy") { target = World.galaxyBlock.knownGalaxies.SingleOrDefault(g => g.id == change.Id); allowed = SharedGalaxyFields; }
                else if (change.Scope == "planet") { target = World.galaxyBlock.knownGalaxies.SelectMany(g => g.stars).SingleOrDefault(s => s.starId == change.Id); allowed = SharedPlanetFields; }
                else throw new InvalidOperationException("Unknown shared world business scope.");
                if (target == null || !allowed.Contains(change.Field)) throw new InvalidOperationException("Shared world delta references a non-whitelisted field.");
                FieldInfo field = NativeWire.Field(target.GetType(), change.Field);
                decoded.Add(Tuple.Create(change, target, field, NativeWire.Unpack(change.NativeValue, field.FieldType)));
            }
            foreach (var item in decoded)
            {
                SharedValue change = item.Item1;
                if (change.Scope == "world")
                {
                    foreach (FWGameData player in Players.Values.Distinct()) item.Item3.SetValue(player, item.Item4);
                }
                else if (change.Scope == "galaxy" && change.Field == "galaxyFleetList")
                {
                    var current = (List<FWBaseBean>)item.Item3.GetValue(item.Item2);
                    var incoming = (List<FWBaseBean>)item.Item4;
                    incoming.AddRange(current.Where(b => b is FWFleet && Context.OwnerOfEntity(((FWFleet)b).getUID()) != null));
                    item.Item3.SetValue(item.Item2, incoming);
                }
                else item.Item3.SetValue(item.Item2, item.Item4);
            }
            foreach (FWGameData player in Players.Values.Distinct())
            {
                NativeWire.Set(player, "fleetRepositoryIndex", null);
                var index = NativeWire.Get(player, "uidIndex") as IDictionary; if (index != null) index.Clear();
            }
            foreach (MapDirectoryItem entry in delta.Maps)
            {
                RegisterMapDirectoryEntry(new FWStarMapInfo { starMapId = entry.MapId, galaxyId = entry.GalaxyId, starId = entry.StarId,
                    cellX = entry.X, cellY = entry.Y, type = entry.Type, name = entry.Name, nameId = entry.NameId,
                    worldPoint = NativeWire.Unpack<FWStarWorldPoint>(entry.WorldPoint) });
            }
            appliedSharedRevision = delta.Revision;
        }
    }
}
