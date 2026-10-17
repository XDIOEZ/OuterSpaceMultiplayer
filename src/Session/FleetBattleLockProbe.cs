using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The galaxy proxy's presentation calls IsPlayerFleetBattleLocked for
    // every visible remote fleet, every frame. Enumerable.FirstOrDefault with
    // a captured fleet UID allocates even when all history is finished.
    // Iterate the concrete Dictionary.ValueCollection instead: it keeps a
    // struct enumerator, preserves its original order and short circuits.
    internal static class FleetBattleLockProbe
    {
        internal static PlayerFleetBattleRecord FindFleet(
            Dictionary<string, PlayerFleetBattleRecord> battles, long fleetUid)
        {
            if (battles == null) return null;
            foreach (PlayerFleetBattleRecord battle in battles.Values)
                if (battle.Stage != PlayerFleetBattleStage.Finished &&
                    (battle.AttackerFleetId == fleetUid || battle.DefenderFleetId == fleetUid))
                    return battle;
            return null;
        }

        internal static bool HasParticipant(
            Dictionary<string, PlayerFleetBattleRecord> battles, string playerId)
        {
            if (battles == null) return false;
            foreach (PlayerFleetBattleRecord battle in battles.Values)
                if (battle.Stage != PlayerFleetBattleStage.Finished &&
                    (battle.AttackerPlayerId == playerId || battle.DefenderPlayerId == playerId))
                    return true;
            return false;
        }
    }
}
