using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Only lives for one incoming galaxy fleet frame. The source fleet index can
    // be rebuilt after a native create/rebind, so never retain this across frames.
    // Lookup by (player, UID) rather than UID alone: authority and player-owned
    // asset boundaries must remain unchanged.
    internal sealed class FleetFrameIndex<TFleet> where TFleet : class
    {
        private const int IndexedBatchThreshold = 8;
        private sealed class PlayerIndex
        {
            internal readonly List<TFleet> All = new List<TFleet>();
            internal readonly Dictionary<long, TFleet> First = new Dictionary<long, TFleet>();
            internal HashSet<long> Duplicates;
        }

        private readonly Func<string, IEnumerable<TFleet>> owned;
        private readonly Func<TFleet, long> uid;
        private readonly Dictionary<string, PlayerIndex> players =
            new Dictionary<string, PlayerIndex>(StringComparer.Ordinal);

        internal static bool ShouldIndex(int contentCount, int poseCount)
        {
            // A handful of poses is cheaper to scan in the existing per-frame
            // OwnedFleets cache. Dense frames amortize indexing over many probes.
            return (long)contentCount + poseCount >= IndexedBatchThreshold;
        }

        internal FleetFrameIndex(Func<string, IEnumerable<TFleet>> owned, Func<TFleet, long> uid)
        {
            this.owned = owned ?? throw new ArgumentNullException(nameof(owned));
            this.uid = uid ?? throw new ArgumentNullException(nameof(uid));
        }

        private static void Append(PlayerIndex index, long id, TFleet value)
        {
            index.All.Add(value);
            if (!index.First.ContainsKey(id)) index.First.Add(id, value);
            else
            {
                if (index.Duplicates == null) index.Duplicates = new HashSet<long>();
                index.Duplicates.Add(id);
            }
        }

        private PlayerIndex ForPlayer(string playerId)
        {
            PlayerIndex index;
            if (players.TryGetValue(playerId, out index)) return index;
            index = new PlayerIndex();
            foreach (TFleet fleet in owned(playerId)) Append(index, uid(fleet), fleet);
            players.Add(playerId, index);
            return index;
        }

        internal IEnumerable<TFleet> All(string playerId) { return ForPlayer(playerId).All; }

        internal bool Contains(string playerId, long fleetUid)
        { return ForPlayer(playerId).First.ContainsKey(fleetUid); }

        internal TFleet SingleOrDefault(string playerId, long fleetUid)
        {
            PlayerIndex index = ForPlayer(playerId);
            if (index.Duplicates != null && index.Duplicates.Contains(fleetUid))
                throw new InvalidOperationException("Sequence contains more than one matching fleet.");
            TFleet fleet;
            return index.First.TryGetValue(fleetUid, out fleet) ? fleet : null;
        }

        internal TFleet Single(string playerId, long fleetUid)
        {
            TFleet fleet = SingleOrDefault(playerId, fleetUid);
            if (fleet == null) throw new InvalidOperationException("Sequence contains no matching fleet.");
            return fleet;
        }

        internal void RegisterCreated(string playerId, long fleetUid, TFleet fleet)
        {
            if (fleet == null || uid(fleet) != fleetUid)
                throw new InvalidOperationException("Registered fleet no longer matches its authoritative UID.");
            // Reuse the already-built index without rescanning all other fleets
            // after each newly committed native instance.
            Append(ForPlayer(playerId), fleetUid, fleet);
        }
    }
}
