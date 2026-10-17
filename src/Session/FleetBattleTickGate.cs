using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // A room can retain 64 finished battles for inspection. Avoid producing
    // two LINQ list snapshots on every idle Tick just to skip those records.
    // Examine the dictionary's concrete ValueCollection to avoid boxing its
    // struct enumerator on the hot path.
    internal static class FleetBattleTickGate
    {
        internal static bool HasWork(Dictionary<string, PlayerFleetBattleRecord> battles, bool isHost, int hostDeadlines)
        {
            if (battles == null || battles.Count == 0) return false;
            // Even a finished or blocked record can have a retained deadline:
            // leave the original deadline processing unchanged on the host.
            if (isHost && hostDeadlines != 0) return true;
            foreach (PlayerFleetBattleRecord battle in battles.Values)
                if (battle.Stage != PlayerFleetBattleStage.Finished && battle.Stage != PlayerFleetBattleStage.Blocked)
                    return true;
            return false;
        }
    }
}
