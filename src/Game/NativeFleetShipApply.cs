using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    // Called only while committing one fully validated fleet-content delta.
    // The warehouse owns the canonical asset when both source lists contain
    // the same UID. Copy into the existing native object before replacing
    // the fleet list; do not retain a lookup across received frames.
    internal static class NativeFleetShipApply
    {
        internal static void Apply<TShip>(List<TShip> incoming,
            IEnumerable<TShip> storedShips, List<TShip> fleetShips,
            Func<TShip, long> uid, Action<TShip, TShip> copy)
            where TShip : class
        {
            var canonical = new List<TShip>(incoming.Count);
            // On small deltas preserve the old LINQ path without building an
            // index. Dense deltas use the validation-tested per-call lookup;
            // malformed stored/fleet lists fall back to SingleOrDefault.
            var existing = NativeFleetShipLookup<TShip>.ShouldIndex(incoming.Count)
                ? new NativeFleetShipLookup<TShip>(storedShips, fleetShips, uid)
                : null;
            foreach (TShip ship in incoming)
            {
                // Malformed null entries are rejected by ValidateFleetShips
                // before this method in production. Still preserve the old
                // LINQ behavior if this helper is called independently:
                // an empty source can append null, while a populated source
                // fails when its predicate dereferences the null incoming.
                TShip current = existing == null || ship == null
                    ? storedShips.SingleOrDefault(value => uid(value) == uid(ship))
                        ?? fleetShips.SingleOrDefault(value => uid(value) == uid(ship))
                    : existing.Find(uid(ship));
                if (current == null) current = ship;
                else copy(ship, current);
                canonical.Add(current);
            }
            // Never clear a source list until all source references are
            // resolved and all native field copies have completed.
            fleetShips.Clear();
            fleetShips.AddRange(canonical);
        }
    }
}
