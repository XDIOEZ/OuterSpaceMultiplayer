using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    // Validation-only index, scoped to ONE incoming authoritative fleet
    // content record. The original lookup checks the player's stored ships
    // first, then the fleet's existing ships. Never merge those sources or
    // cache native references across received frames.
    internal sealed class NativeFleetShipLookup<TShip> where TShip : class
    {
        private const int DenseShipThreshold = 8;
        private readonly IEnumerable<TShip> storedShips, fleetShips;
        private readonly Func<TShip, long> uid;
        private Dictionary<long, TShip> storedIndex, fleetIndex;
        private bool storedReady, fleetReady;

        internal static bool ShouldIndex(int incomingCount)
        { return incomingCount >= DenseShipThreshold; }

        internal NativeFleetShipLookup(IEnumerable<TShip> storedShips,
            IEnumerable<TShip> fleetShips, Func<TShip, long> uid)
        {
            this.storedShips = storedShips;
            this.fleetShips = fleetShips;
            this.uid = uid ?? throw new ArgumentNullException(nameof(uid));
        }

        // An ill-formed native collection is deliberately NOT indexed:
        // SingleOrDefault on such a list has specific missing, duplicate or
        // null-item behavior. Returning null here restores that exact path,
        // including its exception priority for the *requested* UID.
        private Dictionary<long, TShip> TryBuild(IEnumerable<TShip> source)
        {
            if (source == null) return null;
            var index = new Dictionary<long, TShip>();
            foreach (TShip ship in source)
            {
                if (ship == null) return null;
                long id = uid(ship);
                if (index.ContainsKey(id)) return null;
                index.Add(id, ship);
            }
            return index;
        }

        private TShip FindIn(IEnumerable<TShip> source, ref Dictionary<long, TShip> index,
            ref bool ready, long shipUid)
        {
            if (!ready)
            {
                index = TryBuild(source);
                ready = true;
            }
            if (index == null)
                return source.SingleOrDefault(ship => uid(ship) == shipUid);
            TShip shipValue;
            return index.TryGetValue(shipUid, out shipValue) ? shipValue : null;
        }

        internal TShip Find(long shipUid)
        {
            TShip stored = FindIn(storedShips, ref storedIndex, ref storedReady, shipUid);
            return stored ?? FindIn(fleetShips, ref fleetIndex, ref fleetReady, shipUid);
        }
    }
}
