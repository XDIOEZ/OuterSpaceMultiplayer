using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Proxy
    {
        internal readonly long Uid;
        internal readonly string Owner;
        internal Proxy(long uid, string owner) { Uid = uid; Owner = owner; }
    }

    private static int checks;
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static long[] LegacyMissing<T>(Dictionary<long, T> proxies, HashSet<long> visible)
    {
        // Exact old RefreshRemoteFleetProxies expression before the
        // native RemoveRemoteFleetProxy loop.
        return proxies.Keys.Where(uid => !visible.Contains(uid)).ToArray();
    }
    private static Dictionary<long, T> Clone<T>(Dictionary<long, T> source)
    { return new Dictionary<long, T>(source); }
    private static bool SameReferences<T>(Dictionary<long, T> a, Dictionary<long, T> b)
        where T : class
    {
        return a.Count == b.Count && a.Keys.SequenceEqual(b.Keys)
            && a.All(pair => b.TryGetValue(pair.Key, out var value)
                && ReferenceEquals(pair.Value, value));
    }

    private static void Compare(Dictionary<long, Proxy> proxies,
        HashSet<long> visible, string context)
    {
        var previous = Clone(proxies);
        var updated = Clone(proxies);
        var oldReleased = new List<long>();
        var newReleased = new List<long>();
        long[] removedBefore = LegacyMissing(previous, visible);
        List<long> removedAfter = RemoteFleetVisibilityPruner.FindMissing(updated, visible);
        Check(removedBefore.Length == (removedAfter == null ? 0 : removedAfter.Count),
            context + " exact departed count");
        if (removedBefore.Length == 0)
            Check(removedAfter == null,
                context + " stable frame returns no allocated removal list");
        else
            for (int i = 0; i < removedBefore.Length; i++)
                Check(removedBefore[i] == removedAfter[i],
                    context + " preserve snapshot enumeration order and full 64-bit UID " + i);

        foreach (long uid in removedBefore)
        {
            oldReleased.Add(uid);
            previous.Remove(uid);
        }
        if (removedAfter != null)
            foreach (long uid in removedAfter)
            {
                newReleased.Add(uid);
                updated.Remove(uid);
            }
        Check(SameReferences(previous, updated),
            context + " same remaining player proxies and canonical references");
        Check(oldReleased.SequenceEqual(newReleased) &&
            oldReleased.All(uid => !visible.Contains(uid)),
            context + " only non-visible fleets are destroyed in original order");
        foreach (long uid in visible)
            if (proxies.ContainsKey(uid))
                Check(updated.ContainsKey(uid),
                    context + " still-visible fleet UID is never accidentally pruned");
    }
    private static long Allocated(Action operation, int iterations)
    {
        for (int i = 0; i < 100; i++) operation();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) operation();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private static Dictionary<long, Proxy> Create(int count, int offset = 0)
    {
        var proxies = new Dictionary<long, Proxy>();
        for (int i = 0; i < count; i++)
        {
            long uid = i + offset;
            proxies.Add(uid, new Proxy(uid, i % 2 == 0 ? "guest-A" : "guest-B"));
        }
        return proxies;
    }

    private static int Main()
    {
        try
        {
            Compare(new Dictionary<long, Proxy>(), new HashSet<long>(),
                "empty room and no remote fleet proxies");
            Compare(new Dictionary<long, Proxy>(),
                new HashSet<long> { 0, 1, long.MaxValue },
                "visible IDs do not create replica objects in prune-only phase");
            for (int count = 0; count <= 16; count++)
            {
                var proxies = Create(count);
                Compare(proxies, new HashSet<long>(proxies.Keys),
                    "stable fleet count " + count);
                Compare(proxies, new HashSet<long>(),
                    "all remote fleet players disconnect " + count);
                if (count != 0)
                {
                    Compare(proxies, new HashSet<long>(proxies.Keys.Skip(1)),
                        "first UID leaves galaxy " + count);
                    Compare(proxies, new HashSet<long>(proxies.Keys.Take(count - 1)),
                        "last UID leaves galaxy " + count);
                }
            }

            var unusual = new Dictionary<long, Proxy>();
            long[] extremes = { long.MinValue, long.MaxValue, 0, -1,
                1L << 32, (1L << 32) | 1, -(1L << 32), 1 };
            foreach (long id in extremes)
                unusual.Add(id, new Proxy(id, "one-player"));
            Compare(unusual, new HashSet<long> {
                long.MinValue, long.MaxValue, -1, (1L << 32) | 1 },
                "full signed UID domain with colliding upper/lower 32-bit halves");
            Compare(unusual, new HashSet<long>(unusual.Keys),
                "all extreme native UIDs still visible");
            Compare(unusual, new HashSet<long>(),
                "cross-galaxy teleport removes all original identities");
            Compare(unusual, new HashSet<long> { 0, -1, 1L << 32 },
                "negative, zero and high-only UIDs remain distinct");

            // Removing proxies must happen AFTER both implementations
            // construct their snapshot; destroying a native GameObject can
            // reenter scene code which adds/rebinds another UID.
            var scene = Create(8, 2000);
            var oldScene = Clone(scene);
            var newScene = Clone(scene);
            var seen = new HashSet<long> { 2000, 2002, 2004, 2006 };
            long[] before = LegacyMissing(oldScene, seen);
            List<long> after = RemoteFleetVisibilityPruner.FindMissing(newScene, seen);
            var visitsBefore = new List<long>();
            var visitsAfter = new List<long>();
            foreach (long id in before)
            {
                visitsBefore.Add(id);
                if (visitsBefore.Count == 1)
                    oldScene.Add(9999, new Proxy(9999, "new-online-guest"));
                oldScene.Remove(id);
            }
            foreach (long id in after)
            {
                visitsAfter.Add(id);
                if (visitsAfter.Count == 1)
                    newScene.Add(9999, oldScene[9999]);
                newScene.Remove(id);
            }
            Check(visitsBefore.SequenceEqual(visitsAfter)
                && SameReferences(oldScene, newScene)
                && newScene.ContainsKey(9999),
                "native callbacks adding new player fleets cannot invalidate precomputed prune snapshots");

            // If native Destroy throws, the loop still stops after exactly
            // the same previously destroyed proxies (no accidental rollback).
            for (int failIndex = 0; failIndex <= 3; failIndex++)
            {
                var original = Create(8, 9000);
                var replacement = Clone(original);
                var live = new HashSet<long> { 9001, 9003, 9005, 9007 };
                long[] oldMissing = LegacyMissing(original, live);
                var newMissing = RemoteFleetVisibilityPruner.FindMissing(replacement, live);
                var previousRemoved = new List<long>();
                var currentRemoved = new List<long>();
                for (int i = 0; i < oldMissing.Length; i++)
                {
                    if (i == failIndex) break;
                    original.Remove(oldMissing[i]);
                    previousRemoved.Add(oldMissing[i]);
                }
                for (int i = 0; i < newMissing.Count; i++)
                {
                    if (i == failIndex) break;
                    replacement.Remove(newMissing[i]);
                    currentRemoved.Add(newMissing[i]);
                }
                Check(previousRemoved.SequenceEqual(currentRemoved)
                    && SameReferences(original, replacement),
                    "native cleanup failure preserves exact partial removal boundary " + failIndex);
            }

            // Two independent players' fleets may share slot/visual names
            // but only the actual owner-approved numeric IDs are compared.
            var ownership = Create(96);
            var liveA = new HashSet<long>(ownership.Keys.Where(uid => uid % 2 == 0));
            Compare(ownership, liveA, "guest-B disconnect does not remove guest-A");
            var liveB = new HashSet<long>(ownership.Keys.Where(uid => uid % 2 != 0));
            Compare(ownership, liveB, "guest-A disconnect does not remove guest-B");

            var random = new Random(20261018);
            for (int frame = 0; frame < 6000; frame++)
            {
                var proxies = new Dictionary<long, Proxy>();
                var visible = new HashSet<long>();
                int count = random.Next(0, 150);
                for (int i = 0; i < count; i++)
                {
                    long uid = random.Next(0, 5) == 0
                        ? ((long)random.Next(-50000, 50000) << 32) | (uint)random.Next()
                        : random.Next(-500, 500);
                    if (!proxies.ContainsKey(uid))
                        proxies.Add(uid, new Proxy(uid, "guest-" + random.Next(3)));
                    if (random.Next(0, 8) != 0) visible.Add(uid);
                }
                for (int i = 0; i < 7; i++)
                    visible.Add(random.Next(-1000, 1000));
                Compare(proxies, visible, "random remote galaxy visibility frame " + frame);
            }

            const int stableFleets = 128, stableFrames = 5000;
            var dense = Create(stableFleets);
            var unchanged = new HashSet<long>(dense.Keys);
            Action previousRefresh = () =>
            {
                long[] stale = LegacyMissing(dense, unchanged);
                if (stale.Length != 0)
                    throw new InvalidOperationException("old visibility unexpectedly lost fleets");
            };
            Action newRefresh = () =>
            {
                var stale = RemoteFleetVisibilityPruner.FindMissing(dense, unchanged);
                if (stale != null)
                    throw new InvalidOperationException("new visibility unexpectedly lost fleets");
            };
            long oldBytes = Allocated(previousRefresh, stableFrames);
            long newBytes = Allocated(newRefresh, stableFrames);
            Check(oldBytes > 0 && newBytes == 0,
                "production helper avoids stable refresh LINQ/ToArray managed allocations");
            Console.WriteLine("INFO: " + stableFrames + " stable visibility refreshes × " +
                stableFleets + " remote player fleets: old LINQ/ToArray " +
                oldBytes + " B, linked production lazy-pruner " + newBytes +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " production-linked remote fleet visibility and cleanup checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
