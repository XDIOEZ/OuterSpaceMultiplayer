using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Owned by one ApplyFleetFrame preflight only. A large removed-UID batch
    // previously walked every player's primary fleet for every removed UID.
    // Keep the FIRST query's original left-to-right, short-circuit traversal:
    // it must reject the first matching primary without evaluating later,
    // possibly malformed, player records. Only a fully successful first
    // scan can establish a safe read-only index for the remaining UID checks.
    internal sealed class FleetPrimaryRemovalIndex<TPlayer>
    {
        private readonly IEnumerable<TPlayer> players;
        private readonly Func<TPlayer, bool> hasPrimary;
        private readonly Func<TPlayer, long> primaryUid;
        private HashSet<long> primaries;

        internal FleetPrimaryRemovalIndex(IEnumerable<TPlayer> players,
            Func<TPlayer, bool> hasPrimary, Func<TPlayer, long> primaryUid)
        {
            this.players = players ?? throw new ArgumentNullException(nameof(players));
            this.hasPrimary = hasPrimary ?? throw new ArgumentNullException(nameof(hasPrimary));
            this.primaryUid = primaryUid ?? throw new ArgumentNullException(nameof(primaryUid));
        }

        internal bool Contains(long removedUid)
        {
            if (primaries != null) return primaries.Contains(removedUid);

            // If the first query rejects or any native getter fails, do not
            // publish a partially built index. This preserves the original
            // Any(...) exception timing and short-circuit behavior.
            var complete = new HashSet<long>();
            foreach (TPlayer player in players)
            {
                if (!hasPrimary(player)) continue;
                long uid = primaryUid(player);
                if (uid == removedUid) return true;
                complete.Add(uid);
            }
            primaries = complete;
            return false;
        }
    }
}
