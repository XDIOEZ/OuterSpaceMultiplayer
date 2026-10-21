using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private readonly HashSet<long> pendingGroundSettlements = new HashSet<long>();
        private readonly Dictionary<long, string> groundSlotDeniedOperators = new Dictionary<long, string>();

        private void TraceGroundSlot(MapRecord map, string actor, string reason)
        {
            try
            {
                Logger.LogEngine("[FreeWorldMultiplayer][ground-slot] map=" + map.MapId
                    + " actor=" + (actor ?? "none") + " operator=" + (map.OperatorPlayerId ?? "none")
                    + " reason=" + reason + " releaseIn=" + (map.OperatorReleaseAtRoomSeconds <= 0 ? 0
                        : Math.Max(0, map.OperatorReleaseAtRoomSeconds - World.RoomRealSeconds)));
            }
            catch { }
        }
        public string OperatorOfMap(long mapId)
        {
            MapRecord map;
            return World != null && World.Maps.TryGetValue(mapId, out map) ? map.OperatorPlayerId : null;
        }

        public int GroundObserverCount(long mapId)
        {
            MapRecord map;
            return World != null && World.Maps.TryGetValue(mapId, out map) && map.Visitors != null
                ? map.Visitors.Distinct().Count(id => id != map.OperatorPlayerId && IsPlayerOnline(id)) : 0;
        }

        private bool IsMotherShipMap(long mapId)
        {
            return World.Players.Values.Any(player => player.MotherShipMapId == mapId);
        }

        public bool CanOperateMap(string playerId, long mapId)
        {
            MapRecord map;
            if (World == null || playerId == null || !World.Maps.TryGetValue(mapId, out map)) return false;
            if (pendingGroundSettlements.Contains(mapId)) return false;
            // Personal ship/fleet control is independent of a surface operation slot.
            if (IsMotherShipMap(mapId)) return map.OwnerPlayerId == playerId;
            return map.OperatorPlayerId == playerId;
        }

        private void RequireGroundOperator(string playerId, long mapId)
        {
            if (!CanOperateMap(playerId, mapId))
                throw new InvalidOperationException("当前地块仅可观战，操作权由另一玩家持有或尚未通过空投取得。");
        }

        private void RequireLandingSlot(string playerId, long mapId)
        {
            MapRecord map = EnsureMap(mapId);
            if (pendingGroundSettlements.Contains(mapId))
                throw new NativeRejectionException("地面行动已结算，正在等待房主可靠保存，暂不能开启新的空投。");
            if (IsMotherShipMap(mapId)) return;
            if (map.OperatorPlayerId != null && map.OperatorPlayerId != playerId)
            {
                string rejected, signature = playerId + ":" + map.OperatorPlayerId;
                if (!groundSlotDeniedOperators.TryGetValue(mapId, out rejected) || rejected != signature)
                {
                    groundSlotDeniedOperators[mapId] = signature;
                    TraceGroundSlot(map, playerId, "landing-denied-busy");
                }
                throw new NativeRejectionException("当前地块的操作权已被占用，不能空投人员；仍可观察或赠送物资。");
            }
            AttackRecord attack = ActiveGroundAttack(mapId);
            if (attack != null && attack.AttackerPlayerId != playerId)
                throw new NativeRejectionException("当前地块的地面行动尚未结算，不能空投第二位玩家的人员。");
        }

        private void AcquireLandingSlot(string playerId, MapRecord map)
        {
            RequireLandingSlot(playerId, map.MapId);
            if (IsMotherShipMap(map.MapId)) return;
            map.OperatorPlayerId = playerId;
            map.OperatorReleaseAtRoomSeconds = 0;
            groundSlotDeniedOperators.Remove(map.MapId);
            TraceGroundSlot(map, playerId, "landing-acquired");
        }

        private void TryAcquireHomeSlot(PlayerRecord player, MapRecord map)
        {
            if (pendingGroundSettlements.Contains(map.MapId)) return;
            if (map.OperatorPlayerId == player.PlayerId)
            {
                bool returning = map.OperatorReleaseAtRoomSeconds > 0;
                map.OperatorReleaseAtRoomSeconds = 0;
                if (returning) TraceGroundSlot(map, player.PlayerId, "return-cancel-grace");
                return;
            }
            if (map.OperatorPlayerId != null && map.OperatorPlayerId != player.PlayerId) return;
            if (ActiveGroundAttack(map.MapId) != null && map.OperatorPlayerId != player.PlayerId) return;
            if (IsMotherShipMap(map.MapId) || bridge.HasGroundPlayerHeroes(map.MapId, player.PlayerId))
            {
                map.OperatorPlayerId = player.PlayerId;
                map.OperatorReleaseAtRoomSeconds = 0;
                groundSlotDeniedOperators.Remove(map.MapId);
                TraceGroundSlot(map, player.PlayerId, "resident-acquired");
            }
        }

        private void LeaveGroundOperation(PlayerRecord player, MapRecord map, bool switchingMap)
        {
            if (map.OperatorPlayerId != player.PlayerId) return;
            AttackRecord attack = ActiveGroundAttack(map.MapId);
            if (attack != null && attack.AttackerPlayerId == player.PlayerId) return;
            if (switchingMap)
            {
                map.OperatorPlayerId = null;
                map.OperatorReleaseAtRoomSeconds = 0;
                TraceGroundSlot(map, player.PlayerId, "switched-map-release");
            }
            else if (map.OperatorReleaseAtRoomSeconds <= 0)
            {
                map.OperatorReleaseAtRoomSeconds = World.RoomRealSeconds + 180;
                TraceGroundSlot(map, player.PlayerId, "space-or-disconnect-grace");
            }
        }

        private void ReleaseOtherGroundSlots(PlayerRecord player, long enteringMapId)
        {
            bool enteringSurface = !IsMotherShipMap(enteringMapId);
            foreach (MapRecord map in World.Maps.Values.ToList())
                if (map.MapId != enteringMapId && map.OperatorPlayerId == player.PlayerId)
                {
                    string previous = map.OperatorPlayerId;
                    LeaveGroundOperation(player, map, enteringSurface);
                    if (previous != map.OperatorPlayerId) ReassignMap(map);
                }
        }

        private void TickGroundOperationSlots()
        {
            bool changed = false;
            foreach (MapRecord map in World.Maps.Values.ToList())
            {
                if (map.OperatorPlayerId == null || ActiveGroundAttack(map.MapId) != null) continue;
                if (map.OperatorReleaseAtRoomSeconds <= 0 || map.OperatorReleaseAtRoomSeconds > World.RoomRealSeconds) continue;
                string previous = map.OperatorPlayerId;
                map.OperatorPlayerId = null; map.OperatorReleaseAtRoomSeconds = 0;
                TraceGroundSlot(map, previous, "grace-expired-release");
                ReassignMap(map); changed = true;
            }
            if (changed) BroadcastPolicy();
        }

        private void ConfirmGroundSettlementsSaved()
        {
            if (pendingGroundSettlements.Count == 0) return;
            long[] settled = pendingGroundSettlements.ToArray();
            pendingGroundSettlements.Clear();
            foreach (long mapId in settled)
            {
                MapRecord map;
                if (!World.Maps.TryGetValue(mapId, out map)) continue;
                foreach (string visitor in map.Visitors.OrderBy(id => id == map.OwnerPlayerId ? 0 : 1))
                {
                    PlayerRecord present;
                    if (World.Players.TryGetValue(visitor, out present) && present.Online) TryAcquireHomeSlot(present, map);
                }
                TraceGroundSlot(map, map.OperatorPlayerId, "settlement-saved");
                Require(bridge.SetMapAuthority(AccessFor(map, LocalPlayerId)), "保存结算后同步地块操作槽");
            }
            BroadcastPolicy();
        }

        private void RecoverGroundOperationSlot(MapRecord map)
        {
            AttackRecord attack = ActiveGroundAttack(map.MapId);
            if (attack != null)
            {
                map.OperatorPlayerId = attack.AttackerPlayerId;
                map.OperatorReleaseAtRoomSeconds = 0;
            }
            else if (map.OperatorPlayerId != null)
            {
                if (!World.Players.ContainsKey(map.OperatorPlayerId)) map.OperatorPlayerId = null;
                else if (map.OperatorReleaseAtRoomSeconds <= 0)
                    map.OperatorReleaseAtRoomSeconds = World.RoomRealSeconds + 180;
            }
        }
    }
}
