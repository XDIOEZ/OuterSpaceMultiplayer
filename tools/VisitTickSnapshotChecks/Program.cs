using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Visit
    {
        internal readonly string Id;
        internal double Seconds;
        internal bool Active;
        internal Visit(string id, double seconds, bool active)
        { Id = id; Seconds = seconds; Active = active; }
    }

    private static int checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private static void Same(Dictionary<string, Visit> source,
        VisitTickSnapshot<Visit> buffer, string label)
    {
        // Exact prior Values.ToList() snapshot reference.
        var original = source.Values.ToList();
        var modern = buffer.Capture(source);
        try
        {
            Check(original.Count == modern.Count, label + ": number of native visit records");
            for (int i = 0; i < original.Count; i++)
                Check(ReferenceEquals(original[i], modern[i]),
                    label + ": reference, insertion order and mutable visit identity #" + i);
        }
        finally { buffer.Release(modern); }
    }
    private static void SimulateCallback(Dictionary<string, Visit> source,
        VisitTickSnapshot<Visit> buffer, string label)
    {
        var previous = new Dictionary<string, Visit>(source);
        var current = new Dictionary<string, Visit>(source);
        var oldSeen = new List<string>();
        var newSeen = new List<string>();
        var newVisit = new Visit("created-later", 10, true);
        bool oldCallback = false, newCallback = false;
        foreach (Visit item in previous.Values.ToList())
        {
            oldSeen.Add(item.Id);
            if (!oldCallback)
            {
                oldCallback = true;
                previous.Remove(item.Id);
                previous["created-later"] = newVisit;
            }
        }
        var snapshot = buffer.Capture(current);
        try
        {
            foreach (Visit item in snapshot)
            {
                newSeen.Add(item.Id);
                if (!newCallback)
                {
                    newCallback = true;
                    current.Remove(item.Id);
                    current["created-later"] = newVisit;
                }
            }
        }
        finally { buffer.Release(snapshot); }
        Check(oldSeen.SequenceEqual(newSeen), label + ": callback cannot add visit to the same tick");
        Check(previous.Keys.SequenceEqual(current.Keys),
            label + ": exact same dictionary mutation and original reference order");
        Check(oldCallback == newCallback, label + ": callback triggers only when visit existed");
        Same(current, buffer, label + ": following tick sees callback-created visit");
    }
    private static int Main()
    {
        try
        {
            var snapshot = new VisitTickSnapshot<Visit>();
            Check(Throws<ArgumentNullException>(() => snapshot.Capture(null)),
                "null authoritative dictionary is rejected");
            Check(Throws<ArgumentNullException>(() => snapshot.Release(null)),
                "null frame lease is rejected");
            var source = new Dictionary<string, Visit>(StringComparer.Ordinal);
            Same(source, snapshot, "empty visit history");
            var one = new Visit("visit-1", 300, true);
            source.Add(one.Id, one);
            Same(source, snapshot, "single pending consent");
            source.Add("second", new Visit("second", 60, true));
            source.Add("settled", new Visit("settled", 0, false));
            Same(source, snapshot, "mixed active and settled requests");
            SimulateCallback(source, snapshot, "visit update callback removes an entry");

            var first = snapshot.Capture(source);
            Check(first.Count == 3, "first snapshot captures all native visit records");
            var nested = snapshot.Capture(source);
            Check(!ReferenceEquals(first, nested) && nested.Count == 3,
                "recursive TickVisits receives independent saved visit list");
            nested.Clear(); // Mimic a callback that changed a separate nested list.
            Check(first.Count == 3 && ReferenceEquals(first[0], one),
                "nested frame cannot clear the outer visit callback's retained references");
            snapshot.Release(nested);
            snapshot.Release(first);
            Check(first.Count == 0,
                "releasing a tick clears visit/player references for reuse");
            var reused = snapshot.Capture(source);
            Check(ReferenceEquals(first, reused) &&
                reused.Count == source.Count && ReferenceEquals(reused[0], one),
                "next tick reuses one native visit snapshot allocation");
            snapshot.Release(reused);

            var burst = new Dictionary<string, Visit>();
            for (int i = 0; i < 2048; i++)
                burst.Add("player:" + i, new Visit("player:" + i, 300, true));
            var inUse = snapshot.Capture(burst);
            Check(inUse.Capacity >= 2048, "large consent burst grows snapshot capacity");
            snapshot.Reset();
            Check(inUse.Count == 2048 && inUse[0].Active,
                "disconnect inside callback cannot destroy active frame's consent records");
            var afterResetNested = snapshot.Capture(source);
            Check(!ReferenceEquals(afterResetNested, inUse) && afterResetNested.Count == 3,
                "pending reset does not share the previous room's current frame");
            snapshot.Release(afterResetNested);
            snapshot.Release(inUse);
            Check(inUse.Count == 0 && inUse.Capacity == 0,
                "room cleanup discards high-water array only after the live frame returns");
            Same(source, snapshot, "reconnected room retains only its new request sources");
            snapshot.Reset();

            Check(Throws<InvalidOperationException>(() =>
            {
                List<Visit> active = snapshot.Capture(source);
                try
                {
                    if (active.Count != 0)
                        throw new InvalidOperationException("consent callback failed");
                }
                finally { snapshot.Release(active); }
            }), "visit notification failure propagates without leaking a frame lease");
            var afterFailure = snapshot.Capture(source);
            Check(ReferenceEquals(first, afterFailure) &&
                afterFailure.Count == 3,
                "next frame resumes normal snapshot after UI callback failure");
            snapshot.Release(afterFailure);

            // Compare the exact previously used Values.ToList ordering and
            // reference behavior over thousands of random room generations.
            var random = new Random(20261018);
            for (int epoch = 0; epoch < 5000; epoch++)
            {
                var visits = new Dictionary<string, Visit>(StringComparer.Ordinal);
                int n = random.Next(0, 110);
                for (int i = 0; i < n; i++)
                {
                    string key = random.Next(0, 4) == 0 ?
                        "客人:" + random.Next(0, 100) : "visit:" + i;
                    if (!visits.ContainsKey(key))
                        visits.Add(key, new Visit(key, random.NextDouble() * 300,
                            random.Next(0, 2) == 0));
                }
                Same(visits, snapshot, "random room view " + epoch);
                if (epoch % 211 == 0)
                    SimulateCallback(visits, snapshot,
                        "random callback visit mutation " + epoch);
                if (epoch % 99 == 0) snapshot.Reset();
            }

            const int localCount = 64, hostCount = 128, ticks = 5000;
            var local = new Dictionary<string, Visit>(StringComparer.Ordinal);
            var host = new Dictionary<string, Visit>(StringComparer.Ordinal);
            for (int i = 0; i < localCount; i++)
                local.Add("local:" + i, new Visit("local:" + i, 300, i == 0));
            for (int i = 0; i < hostCount; i++)
                host.Add("host:" + i, new Visit("host:" + i, 300, i == 0));
            var localBuffer = new VisitTickSnapshot<Visit>();
            var hostBuffer = new VisitTickSnapshot<Visit>();
            Action legacyTick = () =>
            {
                foreach (Visit visit in local.Values.ToList())
                    if (visit.Id == null) throw new InvalidOperationException();
                foreach (Visit visit in host.Values.ToList())
                    if (visit.Id == null) throw new InvalidOperationException();
            };
            Action productionTick = () =>
            {
                var localSnapshot = localBuffer.Capture(local);
                try
                {
                    foreach (Visit visit in localSnapshot)
                        if (visit.Id == null) throw new InvalidOperationException();
                }
                finally { localBuffer.Release(localSnapshot); }
                var hostSnapshot = hostBuffer.Capture(host);
                try
                {
                    foreach (Visit visit in hostSnapshot)
                        if (visit.Id == null) throw new InvalidOperationException();
                }
                finally { hostBuffer.Release(hostSnapshot); }
            };
            long before = Allocated(legacyTick, ticks);
            long after = Allocated(productionTick, ticks);
            Check(before > 0 && after == 0,
                "production snapshots eliminate allocations for stable pending authorization timers");
            Console.WriteLine("INFO: " + ticks + " pending visit ticks × " +
                localCount + " local updates and " + hostCount +
                " host visit records: original Values.ToList " + before +
                " B, linked production VisitTickSnapshot " + after +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " production-linked active visit timer snapshot checks");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
