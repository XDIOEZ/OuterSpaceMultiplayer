using System;
using System.Linq;
using HarmonyLib;

namespace FreeWorldMultiplayer
{
    // Native store types are STORE and RUBBISH (ZoneDef.isStoreType). Quotas
    // are recomputed from persistent zones, so base -> neutral keeps old zones
    // without rewriting their UID/author or relying on a transient counter.
    internal static class NativeGroundStorageQuota
    {
        private const int MaxZones = 3, MaxCells = 270;
        private static bool Applies(GameBridge bridge, long map, string type)
        { return bridge.Context.OwnerOfMap(map) == null && ZoneDef.isStoreType(type); }
        private static FWAreaZoneBean[] Owned(GameBridge bridge, string actor, UserFWStarSpread map, string type)
        {
            return map.zones.Where(z => z.id == type && NativeGroundCommandOwnership.Owner(bridge, z.getUID()) == actor).ToArray();
        }
        internal static void RequireGrowth(GameBridge bridge, string actor, UserFWStarSpread map, string type, int addedZones, int addedCells)
        {
            if (!Applies(bridge, map.starMapId, type) || addedCells == 0 && addedZones <= 0) return;
            FWAreaZoneBean[] owned = Owned(bridge, actor, map, type);
            long cells = owned.Sum(z => (long)z.cells.Select(p => new { p.x, p.y }).Distinct().Count());
            if (owned.Length > MaxZones || cells > MaxCells || owned.Length + addedZones > MaxZones || cells + addedCells > MaxCells)
                throw new InvalidOperationException("每位玩家在每块中立地块的同类型储存区最多 3 个、合计 270 格；超限旧区可保留和缩减，暂不能新增或扩建。");
        }
        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(ZoneDef), "getZoneRemainTiledSum", new[] { typeof(string), typeof(sbyte) });
            if (target == null) throw new MissingMethodException(typeof(ZoneDef).FullName, "getZoneRemainTiledSum");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(NativeGroundStorageQuota), nameof(RemainingCellsPostfix)));
        }
        private static void RemainingCellsPostfix(string id, sbyte alliance, ref int __result)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || bridge.Applying || bridge.Seeding || alliance != RoleAlliance.Alliance_MINE || !ZoneDef.isStoreType(id)) return;
            UserFWStarSpread map = UserBean.getInstance().curStarMap;
            if (map == null || map.starMapId != bridge.CurrentMapId) return;
            // A base has no multiplayer quota. Its finite grid capacity is enough
            // for any drawing operation and avoids a sentinel near int overflow.
            if (bridge.Context.OwnerOfMap(bridge.CurrentMapId) != null)
            { __result = (int)Math.Max(0L, Math.Min(int.MaxValue / 2L, (long)map.xLength * map.yLength)); return; }
            FWAreaZoneBean[] owned = Owned(bridge, bridge.ActingPlayer ?? bridge.Context.LocalPlayerId, map, id);
            long cells = owned.Sum(z => (long)z.cells.Select(p => new { p.x, p.y }).Distinct().Count());
            __result = owned.Length > MaxZones ? 0 : (int)Math.Max(0L, MaxCells - cells);
        }
    }
}
