using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Sample
    {
        internal long Uid;
        internal readonly int Revision;
        internal Sample(long uid, int revision) { Uid = uid; Revision = revision; }
    }
    private static int passed;
    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + description);
        passed++;
    }
    private static bool Throws<T>(Action call) where T : Exception
    {
        try { call(); return false; }
        catch (T) { return true; }
    }
    private static string Outcome(Action call)
    {
        try { call(); return "accepted"; }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }

    // Source-equivalent old SampleFleetSynchronization presence-set scope;
    // native owner validation is deliberately left outside this pure check.
    private static void OldSample(Dictionary<long, Sample> sampleCache,
        Dictionary<long, string> transitCache, IEnumerable<long> fleetUids,
        HashSet<long> transitIds)
    {
        var present = new HashSet<long>();
        foreach (long uid in fleetUids)
            if (!present.Add(uid))
                throw new InvalidOperationException("Owned fleet identity is missing or duplicated: " + uid);
        FleetSampleCachePruner.RemoveMissing(sampleCache, present);
        FleetSampleCachePruner.RemoveMissing(transitCache, transitIds);
    }

    private static void NewSample(FleetSamplePresenceBuffer buffer,
        Dictionary<long, Sample> sampleCache, Dictionary<long, string> transitCache,
        IEnumerable<long> fleetUids, HashSet<long> transitIds)
    {
        HashSet<long> present = buffer.Rent();
        try
        {
            foreach (long uid in fleetUids)
                if (!present.Add(uid))
                    throw new InvalidOperationException("Owned fleet identity is missing or duplicated: " + uid);
            FleetSampleCachePruner.RemoveMissing(sampleCache, present);
            FleetSampleCachePruner.RemoveMissing(transitCache, transitIds);
        }
        finally { buffer.Return(present); }
    }

    private static Dictionary<long, T> Clone<T>(Dictionary<long, T> original)
    { return new Dictionary<long, T>(original); }
    private static void Same<T>(Dictionary<long, T> oldCache,
        Dictionary<long, T> newCache, string description)
    {
        Check(oldCache.Count == newCache.Count, description + ": count unchanged");
        Check(oldCache.Keys.SequenceEqual(newCache.Keys), description + ": UID order unchanged");
        foreach (var pair in oldCache)
        {
            T actual;
            Check(newCache.TryGetValue(pair.Key, out actual) &&
                EqualityComparer<T>.Default.Equals(actual, pair.Value),
                description + ": identical cached payload reference " + pair.Key);
        }
    }
    private static long Allocated(Action action, int times)
    {
        for (int i = 0; i < 80; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < times; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public static int Main()
    {
        try
        {
            var buffer = new FleetSamplePresenceBuffer();
            var first = buffer.Rent();
            Check(first.Count == 0, "first native presence set starts empty");
            Check(first.Add(long.MinValue) && first.Add(long.MaxValue) &&
                first.Add(0) && first.Add(1L << 32) && first.Add(-1),
                "full signed 64-bit fleet UIDs can be registered");
            Check(!first.Add(long.MinValue), "duplicate owned fleet is detected in the current frame");

            var nested = buffer.Rent();
            Check(!ReferenceEquals(first, nested), "reentrant native sampling gets a separate set");
            Check(nested.Count == 0 && nested.Add(long.MinValue),
                "same UID may be registered independently in the nested frame");
            Check(first.Count == 5 && first.Contains(long.MinValue),
                "nested sampling cannot clear the outer frame's fleet identities");
            buffer.Return(nested);
            Check(first.Count == 5 && first.Contains(0), "nested return does not mutate outer frame");
            buffer.Return(first);
            Check(first.Count == 0, "normal release clears all previous-frame IDs");
            var second = buffer.Rent();
            Check(ReferenceEquals(first, second), "stable frames reuse the same production hash set");
            Check(second.Add(long.MinValue) && second.Add(0) && second.Count == 2,
                "previously occupied fleet UIDs are not falsely duplicated next frame");
            buffer.Return(second);

            Check(Throws<ArgumentNullException>(() => buffer.Return(null)),
                "missing lease is rejected explicitly");
            Check(Throws<InvalidOperationException>(() =>
            {
                var active = buffer.Rent();
                try
                {
                    active.Add(100);
                    throw new InvalidOperationException("simulated native position failure");
                }
                finally { buffer.Return(active); }
            }), "native capture exception propagates unchanged");
            var afterError = buffer.Rent();
            Check(ReferenceEquals(afterError, first) && afterError.Count == 0 &&
                afterError.Add(100), "failed capture returns clean reusable set for retry");
            buffer.Return(afterError);

            var resetDuringUse = buffer.Rent();
            for (int i = 0; i < 4096; i++) resetDuringUse.Add(i);
            Check(resetDuringUse.Count == 4096, "large authoritative fleet population can be tracked");
            buffer.Reset();
            Check(resetDuringUse.Count == 4096 && resetDuringUse.Contains(4000),
                "scene cleanup defers reset while native sample is leased");
            var duringResetNested = buffer.Rent();
            Check(!ReferenceEquals(duringResetNested, resetDuringUse) &&
                duringResetNested.Add(4000), "nested use during deferred reset is isolated");
            buffer.Return(duringResetNested);
            buffer.Return(resetDuringUse);
            var afterReset = buffer.Rent();
            Check(ReferenceEquals(afterReset, first) && afterReset.Count == 0,
                "closing a room reuses empty storage after deferred reset");
            buffer.Return(afterReset);
            buffer.Reset();
            var fresh = buffer.Rent();
            Check(fresh.Count == 0 && fresh.Add(4000),
                "next room starts without any stale native owned fleet UID");
            buffer.Return(fresh);

            var sampleA = new Sample(10, 3);
            var sampleB = new Sample(20, 7);
            var sampleC = new Sample(30, 11);
            var registry = new Dictionary<long, Sample>
            { { 10, sampleA }, { 20, sampleB }, { 30, sampleC } };
            var transit = new Dictionary<long, string>
            { { 10, "hero" }, { 20, "animal" }, { 40, "cargo" } };
            var oldSamples = Clone(registry);
            var newSamples = Clone(registry);
            var oldTransit = Clone(transit);
            var newTransit = Clone(transit);
            long[] fleetIds = { 10, 30 };
            var transitIds = new HashSet<long> { 20, 40 };
            OldSample(oldSamples, oldTransit, fleetIds, transitIds);
            NewSample(buffer, newSamples, newTransit, fleetIds, transitIds);
            Same(oldSamples, newSamples, "separate fleet samples cache");
            Same(oldTransit, newTransit, "separate passenger/cargo transit cache");
            Check(!newSamples.ContainsKey(20) && newTransit.ContainsKey(20) &&
                newSamples.ContainsKey(10) && !newTransit.ContainsKey(10),
                "independent per-type UID expiration preserves the correct cargo/ship state");
            var beforeReconnect = newSamples[10];
            NewSample(buffer, newSamples, newTransit, fleetIds, transitIds);
            Check(ReferenceEquals(beforeReconnect, newSamples[10]) &&
                newSamples[10].Revision == 3,
                "unchanged native sample stays attached to its cached content instance");

            // Failed/duplicate sample must not erroneously prune cached
            // fleet or transit state, and retry must be able to proceed.
            var duplicated = new long[] { 10, 30, 10 };
            oldSamples = Clone(registry); newSamples = Clone(registry);
            oldTransit = Clone(transit); newTransit = Clone(transit);
            var oldResult = Outcome(() => OldSample(oldSamples, oldTransit,
                duplicated, transitIds));
            var newResult = Outcome(() => NewSample(buffer, newSamples, newTransit,
                duplicated, transitIds));
            Check(oldResult == newResult && oldResult.Contains("10"),
                "duplicate UID error text matches the original authoritative path");
            Same(oldSamples, newSamples, "duplicate sample must not alter fleet cache");
            Same(oldTransit, newTransit, "duplicate sample must not alter passenger cache");
            Check(Outcome(() => NewSample(buffer, newSamples, newTransit,
                fleetIds, transitIds)) == "accepted",
                "retry after a duplicate sample no longer sees the previous aborted UID set");
            Check(Outcome(() => OldSample(oldSamples, oldTransit,
                fleetIds, transitIds)) == "accepted",
                "old reference accepts the same retry after duplicate input");
            Same(oldSamples, newSamples, "aborted native sample preserves fleet cache until retry");

            // Randomized differential sampling tests the exact same real
            // pruning helper with both old/new presence-set lifecycles.
            var random = new Random(20261018);
            var ownedOld = new Dictionary<long, Sample>();
            var ownedNew = new Dictionary<long, Sample>();
            var peopleOld = new Dictionary<long, string>();
            var peopleNew = new Dictionary<long, string>();
            for (int run = 0; run < 3000; run++)
            {
                var active = new List<long>();
                var people = new HashSet<long>();
                int count = random.Next(0, 150);
                var unique = new HashSet<long>();
                for (int i = 0; i < count; i++)
                {
                    long uid = random.Next(0, 4) == 0
                        ? ((long)random.Next(-200000, 200000) << 32) | (uint)random.Next()
                        : random.Next(-100000, 100000);
                    if (!unique.Add(uid)) continue;
                    active.Add(uid);
                    if (!ownedOld.ContainsKey(uid))
                    {
                        Sample sample = new Sample(uid, run);
                        ownedOld.Add(uid, sample);
                        ownedNew.Add(uid, sample);
                    }
                    if (random.Next(0, 2) == 0)
                    {
                        people.Add(uid);
                        if (!peopleOld.ContainsKey(uid))
                        {
                            string value = "passenger-frame-" + run;
                            peopleOld.Add(uid, value);
                            peopleNew.Add(uid, value);
                        }
                    }
                }
                OldSample(ownedOld, peopleOld, active, people);
                NewSample(buffer, ownedNew, peopleNew, active, people);
                Same(ownedOld, ownedNew, "random authoritative sample " + run);
                Same(peopleOld, peopleNew, "random transit sample " + run);
            }

            // Only measure presence-set creation/clear, not player fleet
            // enumeration, reflection, wire serialization or map capture.
            const int fleetCount = 96, stableFrames = 5000;
            var uids = Enumerable.Range(0, fleetCount).Select(x => (long)x).ToArray();
            Action oldPresence = () =>
            {
                var present = new HashSet<long>();
                foreach (long uid in uids)
                    if (!present.Add(uid)) throw new Exception("duplicate");
                GC.KeepAlive(present);
            };
            Action newPresence = () =>
            {
                var present = buffer.Rent();
                try
                {
                    foreach (long uid in uids)
                        if (!present.Add(uid)) throw new Exception("duplicate");
                }
                finally { buffer.Return(present); }
            };
            long oldBytes = Allocated(oldPresence, stableFrames);
            long newBytes = Allocated(newPresence, stableFrames);
            Check(oldBytes > newBytes && newBytes == 0,
                "production reusable presence set avoids stable-frame HashSet allocations");
            Console.WriteLine("INFO: " + stableFrames + " synthetic samples × " +
                fleetCount + " owned fleet UIDs: old per-frame presence " + oldBytes +
                " bytes, linked production buffer " + newBytes +
                " bytes (managed allocations after warmup)");
            Console.WriteLine("PASS: " + passed +
                " linked fleet sample presence and pruning checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
