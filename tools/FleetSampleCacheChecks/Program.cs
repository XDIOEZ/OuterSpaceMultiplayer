using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class NativeSample
    {
        internal readonly long Identity;
        internal int Revision;
        internal NativeSample(long identity, int revision)
        { Identity = identity; Revision = revision; }
    }

    private static int passed;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + description);
        passed++;
    }
    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
    }
    private static int LegacyPrune<TValue>(Dictionary<long, TValue> cached, HashSet<long> present)
    {
        // Exact previous source path, including its two LINQ operators.
        long[] stale = cached.Keys.Where(uid => !present.Contains(uid)).ToArray();
        foreach (long uid in stale) cached.Remove(uid);
        return stale.Length;
    }
    private static Dictionary<long, TValue> Copy<TValue>(Dictionary<long, TValue> dictionary)
    { return new Dictionary<long, TValue>(dictionary); }
    private static void Same<TValue>(Dictionary<long, TValue> oldCache,
        Dictionary<long, TValue> newCache, string label)
    {
        Check(oldCache.Count == newCache.Count, label + ": number of retained identities");
        Check(oldCache.Keys.SequenceEqual(newCache.Keys), label + ": retained UID order");
        foreach (var pair in oldCache)
        {
            TValue newValue;
            Check(newCache.TryGetValue(pair.Key, out newValue)
                && EqualityComparer<TValue>.Default.Equals(newValue, pair.Value),
                label + ": preserved cache value " + pair.Key);
        }
    }
    private static void Compare<TValue>(Dictionary<long, TValue> cache,
        HashSet<long> present, string label)
    {
        var oldCache = Copy(cache);
        var newCache = Copy(cache);
        int expected = LegacyPrune(oldCache, present);
        int actual = FleetSampleCachePruner.RemoveMissing(newCache, present);
        Check(expected == actual, label + ": exact number of stale UIDs");
        Same(oldCache, newCache, label);
    }
    private static long Allocated(Action action, int samples)
    {
        for (int i = 0; i < 120; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    private static Dictionary<long, NativeSample> MakeFleet(int count)
    {
        var result = new Dictionary<long, NativeSample>(count);
        for (int i = 0; i < count; i++) result.Add(i, new NativeSample(i, 0));
        return result;
    }

    private static int Main()
    {
        try
        {
            var fleet = MakeFleet(4);
            var live = new HashSet<long> { 0, 1, 2, 3 };
            Check(Throws<ArgumentNullException>(() =>
                FleetSampleCachePruner.RemoveMissing<NativeSample>(null, live)),
                "missing fleet cache fails explicitly");
            Check(Throws<ArgumentNullException>(() =>
                FleetSampleCachePruner.RemoveMissing(fleet, null)),
                "missing authoritative presence set fails explicitly");
            Compare(new Dictionary<long, NativeSample>(), new HashSet<long>(),
                "no cached fleets and no live UIDs");
            Compare(new Dictionary<long, NativeSample>(), live,
                "newly visible UIDs are not inserted by pruning");
            Compare(fleet, live, "all fleets still present");
            Compare(fleet, new HashSet<long> { 0, 1, 2 },
                "one retired fleet with old UID 3");
            Compare(fleet, new HashSet<long> { 0, 1 },
                "several retired fleets");
            Compare(fleet, new HashSet<long>(),
                "all fleets departed during disconnection");

            var identity = new Dictionary<long, NativeSample>
            {
                { long.MinValue, new NativeSample(long.MinValue, 1) },
                { 0, new NativeSample(0, 2) },
                { -1, new NativeSample(-1, 3) },
                { long.MaxValue, new NativeSample(long.MaxValue, 4) },
                { 1L << 32, new NativeSample(1L << 32, 5) }
            };
            Compare(identity, new HashSet<long> { long.MinValue, -1, long.MaxValue },
                "full 64-bit UID range including zero, negative and high-bits-only IDs");
            Compare(identity, new HashSet<long> { -1, 0, long.MaxValue, 1L << 32 },
                "single missing UID whose sentinel would collide with long.MinValue");
            Compare(identity, new HashSet<long> { long.MinValue, -1, long.MaxValue, 1L << 32 },
                "single missing zero UID cannot be mistaken for no removal");
            Compare(identity, new HashSet<long> { 5, long.MinValue, 0, -1, long.MaxValue, 1L << 32 },
                "extra incoming UIDs must never alter the old cache");

            // The two cache maps are distinct. Old fleet content must be
            // pruned from live fleet IDs while resident/cargo transit values
            // are pruned only from the expedition/passenger UID set.
            var fleetCache = new Dictionary<long, NativeSample>
            {
                { 10, new NativeSample(10, 1) },
                { 20, new NativeSample(20, 2) },
                { 30, new NativeSample(30, 3) }
            };
            var transitCache = new Dictionary<long, string>
            {
                { 10, "hero-v1" }, { 20, "animal-v3" }, { 40, "cargo-v5" }
            };
            var fleetIds = new HashSet<long> { 10, 30 };
            var transitIds = new HashSet<long> { 20, 40 };
            var oldFleet = Copy(fleetCache); var currentFleet = Copy(fleetCache);
            var oldTransit = Copy(transitCache); var currentTransit = Copy(transitCache);
            Check(LegacyPrune(oldFleet, fleetIds) ==
                FleetSampleCachePruner.RemoveMissing(currentFleet, fleetIds),
                "fleet samples retire only absent authoritative fleet UIDs");
            Check(LegacyPrune(oldTransit, transitIds) ==
                FleetSampleCachePruner.RemoveMissing(currentTransit, transitIds),
                "transit cache retires only absent passenger and cargo UIDs");
            Same(oldFleet, currentFleet, "separate fleet content cache");
            Same(oldTransit, currentTransit, "separate cached transit content");
            Check(currentFleet.ContainsKey(10) && !currentFleet.ContainsKey(20)
                && currentTransit.ContainsKey(20) && !currentTransit.ContainsKey(10),
                "same UID may validly survive in one cache and retire in the other");

            NativeSample retained = currentFleet[10];
            retained.Revision = 99;
            Check(FleetSampleCachePruner.RemoveMissing(currentFleet, fleetIds) == 0
                && ReferenceEquals(currentFleet[10], retained)
                && currentFleet[10].Revision == 99,
                "unchanged cached wire field object and revision are preserved");
            currentFleet.Add(20, new NativeSample(20, 44));
            var reconnected = new HashSet<long> { 10, 20, 30 };
            Check(FleetSampleCachePruner.RemoveMissing(currentFleet, reconnected) == 0
                && currentFleet[20].Revision == 44,
                "reappearing fleet UID keeps newly captured content without stale tombstones");
            Check(FleetSampleCachePruner.RemoveMissing(currentFleet, new HashSet<long>()) == 3
                && currentFleet.Count == 0,
                "all native fleet samples expire when the local player leaves");
            currentFleet.Add(10, new NativeSample(10, 100));
            Check(!ReferenceEquals(retained, currentFleet[10])
                && currentFleet[10].Revision == 100,
                "new room baseline can recreate the same UID with fresh state");

            // Deleting multiple UIDs must not invalidate the dictionary
            // enumerator, even on the older Unity/Mono collection runtime.
            var large = MakeFleet(2048);
            Compare(large, new HashSet<long>(Enumerable.Range(0, 2048)
                .Where(i => i % 3 == 0).Select(i => (long)i)),
                "mass player departure and large cache pruning");
            Compare(large, new HashSet<long> { 0, 2047 },
                "retain first and last entries during full evacuation");
            Compare(large, new HashSet<long>(),
                "large registry fully empty after losing all fleets");

            // Deterministic randomized old/new differential checks preserve
            // UIDs, object references and retained Dictionary key order.
            var random = new Random(20261018);
            for (int run = 0; run < 5000; run++)
            {
                var seed = new Dictionary<long, NativeSample>();
                var active = new HashSet<long>();
                int count = random.Next(0, 180);
                for (int j = 0; j < count; j++)
                {
                    long uid = random.Next(0, 4) == 0
                        ? (long)random.Next(-1_000_000, 1_000_000) << 32 | (uint)random.Next()
                        : random.Next(-200_000, 200_000);
                    if (!seed.ContainsKey(uid))
                        seed.Add(uid, new NativeSample(uid, random.Next(0, 1000)));
                    if (random.Next(0, 5) != 0) active.Add(uid);
                }
                for (int j = 0; j < 15; j++)
                    active.Add(random.Next(-200_000, 200_000));
                Compare(seed, active, "random fleet cache membership " + run);
            }

            const int stableFleets = 96, stableTransit = 64, frames = 5000;
            var contentCache = MakeFleet(stableFleets);
            var liveContent = new HashSet<long>(contentCache.Keys);
            var peopleCache = new Dictionary<long, string>();
            for (int i = 0; i < stableTransit; i++)
                peopleCache.Add(i + 20_000, "native-transit");
            var livePeople = new HashSet<long>(peopleCache.Keys);
            Action oldTick = () =>
            {
                LegacyPrune(contentCache, liveContent);
                LegacyPrune(peopleCache, livePeople);
            };
            Action newTick = () =>
            {
                FleetSampleCachePruner.RemoveMissing(contentCache, liveContent);
                FleetSampleCachePruner.RemoveMissing(peopleCache, livePeople);
            };
            long oldBytes = Allocated(oldTick, frames);
            long newBytes = Allocated(newTick, frames);
            Check(oldBytes > 0 && newBytes == 0,
                "stable native fleet and transit pruning avoids LINQ/ToArray allocations");
            Console.WriteLine("INFO: " + frames + " stable samples of " +
                stableFleets + " fleets and " + stableTransit + " transit values: old pruning " +
                oldBytes + " B, linked production helper " + newBytes +
                " B (managed allocations after warmup)");

            var oneRetired = MakeFleet(16);
            var onePresent = new HashSet<long>(oneRetired.Keys);
            onePresent.Remove(15);
            Check(FleetSampleCachePruner.RemoveMissing(oneRetired, onePresent) == 1
                && !oneRetired.ContainsKey(15) && oneRetired.Count == 15,
                "single stale fleet is removed without a temporary deletion list");
            Console.WriteLine("PASS: " + passed + " linked fleet sample cache pruning checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
