using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // ApplyFleetFrame must revisit the CURRENT player/galaxy lists after each
    // removed UID: native proxy callbacks may modify those lists. The old
    // Players.Values.SelectMany(...).Distinct() also allocated a new LINQ
    // chain and distinct set for every UID in a large full-baseline removal.
    //
    // This walker is local to one ApplyFleetFrame. Only its duplicate filter
    // is reused; it never caches player or galaxy membership across removals.
    internal sealed class FleetGalaxyRemovalWalker<TPlayer, TGalaxy>
    {
        private readonly HashSet<TGalaxy> reusableSeen = new HashSet<TGalaxy>();
        private bool inUse;

        internal void VisitDistinct(IEnumerable<TPlayer> players,
            Func<TPlayer, IEnumerable<TGalaxy>> getGalaxies, long fleetUid,
            Action<TGalaxy, long> visitor)
        {
            if (players == null) throw new ArgumentNullException(nameof(players));
            if (getGalaxies == null) throw new ArgumentNullException(nameof(getGalaxies));
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));

            // Callbacks from RemoveAll/native proxy disposal may reenter a
            // galaxy frame; do not let the nested call clear the outer scan.
            bool ownsReusable = !inUse;
            var seen = ownsReusable ? reusableSeen : new HashSet<TGalaxy>();
            if (ownsReusable) inUse = true;
            try
            {
                foreach (TPlayer player in players)
                {
                    IEnumerable<TGalaxy> galaxies = getGalaxies(player);
                    // Native knownGalaxies is a List<T>. A typed enumerator
                    // avoids boxing on each player but preserves the usual
                    // List<T> mutation-during-enumeration exception.
                    var list = galaxies as List<TGalaxy>;
                    if (list != null)
                    {
                        foreach (TGalaxy galaxy in list)
                            if (seen.Add(galaxy)) visitor(galaxy, fleetUid);
                        continue;
                    }
                    var array = galaxies as TGalaxy[];
                    if (array != null)
                    {
                        foreach (TGalaxy galaxy in array)
                            if (seen.Add(galaxy)) visitor(galaxy, fleetUid);
                        continue;
                    }
                    foreach (TGalaxy galaxy in galaxies)
                        if (seen.Add(galaxy)) visitor(galaxy, fleetUid);
                }
            }
            finally
            {
                // No retained game-object references and no stale 'seen'
                // entries after success, exceptions or nested callbacks.
                seen.Clear();
                if (ownsReusable) inUse = false;
            }
        }
    }
}
