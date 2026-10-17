using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // HostBattleInput validates every native battle command, potentially
    // dozens of times per second. The previous new[] {...}.Contains(...)
    // allocated the same 17-element string array for every incoming input.
    // Membership is fixed for protocol 5; never add operations at runtime.
    internal static class BattleInputAllowedOperations
    {
        private static readonly HashSet<string> Names = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "Pilot", "Seat", "Vent", "Shield", "System", "Group",
            "AutoFire", "Autopilot", "Wing", "Target", "IssueMoveOrder",
            "AppendMoveWaypoint", "IssueRetreatOrder", "IssueAttackOrder",
            "IssueDefendOrder", "IssueCaptureOrder", "ClearOrder"
        };

        internal static bool Contains(string operation)
        {
            return Names.Contains(operation);
        }
    }
}
