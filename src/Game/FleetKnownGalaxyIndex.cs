using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    // Only used during the read-only pose preflight of ONE received fleet
    // frame. A player may own many fleets but its known native galaxies do
    // not need to be scanned again for each received fleet position.
    internal sealed class FleetKnownGalaxyIndex<TGalaxy> where TGalaxy : class
    {
        private const int DensePoseThreshold = 8;
        private const int DenseGalaxyThreshold = 8;

        private sealed class Entry
        {
            internal IEnumerable<TGalaxy> Source;
            internal int Count;
            internal HashSet<string> Ids; // null = malformed source; preserve old Any.
        }

        private readonly Dictionary<string, Entry> players =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Func<TGalaxy, string> galaxyId;

        internal FleetKnownGalaxyIndex(Func<TGalaxy, string> galaxyId)
        { this.galaxyId = galaxyId ?? throw new ArgumentNullException(nameof(galaxyId)); }

        internal static bool ShouldIndex(int poseCount)
        { return poseCount >= DensePoseThreshold; }

        private bool LegacyContains(IEnumerable<TGalaxy> source, string id)
        {
            // A broken/null list or native null galaxy must retain the
            // original LINQ Any's short-circuit and exception behavior.
            return source.Any(galaxy => galaxyId(galaxy) == id);
        }

        private HashSet<string> TryBuild(IEnumerable<TGalaxy> source)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (TGalaxy galaxy in source)
            {
                // If a malformed native list contains null later than a
                // valid match, eager indexing must not reject that match.
                // Fall back to the exact original per-query predicate.
                if (galaxy == null) return null;
                ids.Add(galaxyId(galaxy));
            }
            return ids;
        }

        internal bool Contains(string playerId, IEnumerable<TGalaxy> source, string id)
        {
            if (source == null) return LegacyContains(source, id);
            var collection = source as ICollection<TGalaxy>;
            int count = collection == null ? -1 : collection.Count;
            // The production collection is a native List<FWGalaxy>. For
            // arbitrary one-shot/lazy enumerables, preserve Any's early exit
            // instead of enumerating an unknown provider in advance.
            if (count < DenseGalaxyThreshold)
                return LegacyContains(source, id);

            Entry entry;
            if (!players.TryGetValue(playerId, out entry)
                || !ReferenceEquals(entry.Source, source)
                || entry.Count != count)
            {
                entry = new Entry { Source = source, Count = count, Ids = TryBuild(source) };
                players[playerId] = entry;
            }
            return entry.Ids == null ? LegacyContains(source, id) : entry.Ids.Contains(id);
        }
    }
}
