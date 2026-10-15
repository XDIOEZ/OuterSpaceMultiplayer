using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private readonly Dictionary<string, callbackObj> pendingMapResolutions = new Dictionary<string, callbackObj>();
        private bool resolvingCanonicalMap;
        internal bool ResolvingCanonicalMap { get { return resolvingCanonicalMap; } }

        public BridgeResult ResolveMapEntry(MapEntryRequest request, out long canonicalMapId)
        {
            long resolved = -1;
            BridgeResult result = Run(() =>
            {
                if (!Context.IsHost || World == null || request == null) throw new InvalidOperationException("Only host may resolve a world map entry.");
                FWStarMapInfo map;
                bool hasSurface = !String.IsNullOrEmpty(request.StarId) && request.StarId != "-1" && request.StarId != FWGameData.MOTHERSHIP_STAR_ID;
                if (!hasSurface)
                {
                    if (request.KnownMapId < 0 || !Maps.TryGetValue(request.KnownMapId, out map)) throw new InvalidOperationException("Interior/mothership entry requires an existing host map identity.");
                }
                else
                {
                    if (request.WorldX < Int16.MinValue || request.WorldX > Int16.MaxValue || request.WorldY < Int16.MinValue || request.WorldY > Int16.MaxValue) throw new InvalidOperationException("Invalid planet tile coordinates.");
                    FWGalaxy galaxy = World.galaxyBlock.knownGalaxies.SingleOrDefault(g => g.id == request.GalaxyId);
                    if (galaxy == null) throw new InvalidOperationException("Target galaxy does not exist in the host world.");
                    FWStarInfo star = galaxy.stars.SingleOrDefault(s => s.starId == request.StarId);
                    if (star == null) throw new InvalidOperationException("Target planet does not exist in the requested galaxy.");
                    map = star.starMapList.SingleOrDefault(m => m != null && m.worldPoint != null && m.worldPoint.worldX == request.WorldX && m.worldPoint.worldY == request.WorldY);
                    if (map == null)
                    {
                        resolvingCanonicalMap = true;
                        try
                        {
                            using (EnterPlayer(hostId))
                            {
                                FWStarWorldPoint point = star.getStarWorldPoint((short)request.WorldX, (short)request.WorldY);
                                if (point == null || !point.canLand()) throw new InvalidOperationException("The selected host planet tile is not landable.");
                                map = World.createStarMapInfo(star, point, null);
                                star.addStarMapInfo(map);
                            }
                        }
                        finally { resolvingCanonicalMap = false; }
                    }
                    // The host's coordinate identity wins; a client-provided newly allocated UID is ignored.
                    Maps[map.starMapId] = map;
                }
                using (EnterPlayer(hostId)) map.requestMap();
                Maps[map.starMapId] = map;
                resolved = map.starMapId;
            });
            canonicalMapId = resolved; return result;
        }
        internal void RouteMapEntry(FWStarMapInfo map)
        {
            if (map == null) throw new ArgumentNullException("map");
            var request = new MapEntryRequest { KnownMapId = map.starMapId, GalaxyId = map.galaxyId, StarId = map.starId,
                WorldX = map.worldPoint == null ? map.cellX : map.worldPoint.worldX,
                WorldY = map.worldPoint == null ? map.cellY : map.worldPoint.worldY };
            CommandResult result = Context.SubmitCommand(new GameCommand { Kind = CommandKind.EnterMap, MapId = map.starMapId, PayloadJson = NativeWire.Json(request) });
            if (!result.Success) throw new InvalidOperationException("进入地图未获房主确认：" + result.Error);
        }
        internal void ResolveMapForNativeUi(string starId, short x, short y, callbackObj callback)
        {
            FWStarInfo star = Players[Context.LocalPlayerId].getStarInfo(starId);
            if (star == null) throw new InvalidOperationException("Planet not found in shared world.");
            var request = new MapEntryRequest { GalaxyId = star.galaxyId, StarId = star.starId, WorldX = x, WorldY = y, ResolveOnly = true };
            var command = new GameCommand { Kind = CommandKind.EnterMap, PayloadJson = NativeWire.Json(request) };
            if (callback != null) pendingMapResolutions.Add(command.CommandId, callback);
            CommandResult result = Context.SubmitCommand(command);
            // Host Submit raises CommandCompleted synchronously; processing the result again is idempotent.
            if (Context.IsHost || !result.Success) ObserveTravelCommandResult(result);
        }
        internal void ObserveTravelCommandResult(CommandResult result)
        {
            callbackObj callback;
            if (result == null || !pendingMapResolutions.TryGetValue(result.CommandId, out callback)) return;
            if (!result.Success) { pendingMapResolutions.Remove(result.CommandId); throw new InvalidOperationException("房主未能确认目标地块：" + result.Error); }
            if (String.IsNullOrEmpty(result.PayloadJson)) return; // An asynchronous queued acknowledgement is not a map resolution.
            MapSnapshot snapshot = NativeWire.Parse<MapSnapshot>(result.PayloadJson);
            if (snapshot == null || snapshot.MapId < 0) throw new InvalidOperationException("Host map resolution omitted its canonical checkpoint.");
            string encoded = JObject.Parse(snapshot.NativeJson).Value<string>("Map");
            FWStarMapInfo map = NativeWire.Unpack<FWStarMapInfo>(encoded);
            if (map.starMapId != snapshot.MapId) throw new InvalidOperationException("Canonical map resolution identity mismatch.");
            RegisterMapDirectoryEntry(map);
            pendingMapResolutions.Remove(result.CommandId);
            callback(map);
        }
        internal void RegisterMapDirectoryEntry(FWStarMapInfo incoming)
        {
            FWStarMapInfo current;
            bool known = Maps.TryGetValue(incoming.starMapId, out current);
            if (known && (incoming.galaxyId == FWGameData.MOTHERSHIP_GALAXY_ID || incoming.starId == FWGameData.MOTHERSHIP_STAR_ID || String.IsNullOrEmpty(incoming.starId) || incoming.starId == "-1")) return;
            // A checkpoint may precede its catalogue introduction. Preserve that full object and
            // still publish it to the native planet directory; metadata must not replace entities.
            FWStarMapInfo canonical = known ? current : incoming;
            if (canonical.galaxyId != incoming.galaxyId || canonical.starId != incoming.starId) throw new InvalidOperationException("Canonical map directory identity conflicts with an existing map.");
            FWGalaxy galaxy = World.galaxyBlock.knownGalaxies.SingleOrDefault(g => g.id == canonical.galaxyId);
            FWStarInfo star = galaxy == null ? null : galaxy.stars.SingleOrDefault(s => s.starId == canonical.starId);
            if (star == null) throw new InvalidOperationException("Canonical map directory entry references an unknown planet.");
            FWStarMapInfo conflict = star.starMapList.FirstOrDefault(m => m.starMapId != canonical.starMapId && m.worldPoint != null && canonical.worldPoint != null && m.worldPoint.ComparePosition(canonical.worldPoint));
            if (conflict != null) throw new InvalidOperationException("A different canonical map already occupies this shared tile.");
            FWStarMapInfo listed = star.starMapList.FirstOrDefault(m => m.starMapId == canonical.starMapId);
            if (listed == null) star.addStarMapInfo(canonical);
            else if (!ReferenceEquals(listed, canonical)) throw new InvalidOperationException("Native map catalogue and checkpoint use different canonical objects.");
            Maps[canonical.starMapId] = canonical;
        }
        internal FWStarMapInfo PendingMapDescriptor(FWStarInfo star, FWStarWorldPoint point)
        {
            if (star == null || point == null) throw new InvalidOperationException("Missing planet map intent.");
            FWStarMapInfo existing = star.starMapList.FirstOrDefault(m => m.worldPoint != null && m.worldPoint.ComparePosition(point));
            return existing ?? new FWStarMapInfo { starMapId = -1, galaxyId = star.galaxyId, starId = star.starId,
                worldPoint = point, cellX = point.worldX, cellY = point.worldY, type = FWStarMapInfo.TYPE_NORMAL };
        }
    }

    internal static class NativeTravelHooks
    {
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(UserBean), "enterMap", new[] { typeof(FWStarMapInfo) }), new HarmonyMethod(typeof(NativeTravelHooks), nameof(EnterInfoPrefix)));
            harmony.Patch(AccessTools.Method(typeof(UserBean), "enterMap", new[] { typeof(long) }), new HarmonyMethod(typeof(NativeTravelHooks), nameof(EnterIdPrefix)));
            harmony.Patch(AccessTools.Method(typeof(UserBean), "getStarMapInfo_Sync"), new HarmonyMethod(typeof(NativeTravelHooks), nameof(ResolvePrefix)));
            foreach (MethodInfo method in typeof(FWGameData).GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.Name == "createStarMapInfo"))
                harmony.Patch(method, new HarmonyMethod(typeof(NativeTravelHooks), nameof(CreatePrefix)));
            harmony.Patch(AccessTools.Method(typeof(FWGameDataUtils), "createStarMapInfo"), new HarmonyMethod(typeof(NativeTravelHooks), nameof(CreatePrefix)));
            harmony.Patch(AccessTools.Method(typeof(FWStarInfo), "addStarMapInfo"), new HarmonyMethod(typeof(NativeTravelHooks), nameof(RegisterPrefix)));
            harmony.Patch(AccessTools.Method(typeof(FWStarMapInfo), "requestMap"), new HarmonyMethod(typeof(NativeTravelHooks), nameof(RequestMapPrefix)));
        }
        private static bool Bypass { get { return Bridge == null || Bridge.Applying || Bridge.Seeding || Bridge.ResolvingCanonicalMap; } }
        private static bool EnterInfoPrefix(FWStarMapInfo starMapInfo)
        { if (Bypass) return true; Bridge.RouteMapEntry(starMapInfo); return false; }
        private static bool EnterIdPrefix(long starMapID)
        {
            if (Bypass) return true;
            FWStarMapInfo map;
            if (!Bridge.Maps.TryGetValue(starMapID, out map)) throw new InvalidOperationException("Unknown host map identity; select a planet tile again.");
            Bridge.RouteMapEntry(map); return false;
        }
        private static bool ResolvePrefix(string starID, short cx, short cy, callbackObj callback)
        { if (Bypass) return true; Bridge.ResolveMapForNativeUi(starID, cx, cy, callback); return false; }
        private static bool CreatePrefix(object[] __args, ref FWStarMapInfo __result)
        {
            if (Bypass || Bridge.Context.IsHost) return true;
            FWStarInfo star = __args.OfType<FWStarInfo>().FirstOrDefault();
            if (star == null) throw new InvalidOperationException("Client map factory has no planet identity.");
            FWStarWorldPoint point = __args.OfType<FWStarWorldPoint>().FirstOrDefault();
            if (point == null)
            {
                short[] coordinates = __args.OfType<short>().ToArray();
                if (coordinates.Length < 2) throw new InvalidOperationException("Client map factory has no tile coordinates.");
                point = new FWStarWorldPoint(coordinates[0], coordinates[1]);
            }
            __result = Bridge.PendingMapDescriptor(star, point); return false;
        }
        private static bool RegisterPrefix(FWStarMapInfo _starMapInfo)
        { return Bypass || Bridge.Context.IsHost || (_starMapInfo != null && _starMapInfo.starMapId >= 0); }
        private static bool RequestMapPrefix(FWStarMapInfo __instance)
        {
            if (!Bypass && !Bridge.Context.IsHost && (__instance.starMapId < 0 || __instance.starSpread == null))
                throw new InvalidOperationException("地图尚未由房主解析，不能在客户端生成地表；请等待原生远征界面的房主确认。");
            return true;
        }
    }
}
