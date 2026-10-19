using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string description)
    {
        checks++;
        if (!condition) throw new Exception("FAIL: " + description);
    }
    private static bool Throws<T>(Action call) where T : Exception
    {
        try { call(); }
        catch (T) { return true; }
        return false;
    }

    private static void Main()
    {
        // C27-01: failed host send only undoes the exact pending lease,
        // leaving a reentrant replacement and the other player's bytes.
        var offered = new Dictionary<string, object>();
        var old = new object();
        var replacement = new object();
        offered["a"] = old; offered["b"] = new object();
        Check(MapCacheOutboundRetry.RemoveIfSame(offered, "a", old) && !offered.ContainsKey("a")
            && offered.ContainsKey("b"), "host failed query rolls back only its matching lease");
        offered["a"] = replacement;
        Check(!MapCacheOutboundRetry.RemoveIfSame(offered, "a", old)
            && ReferenceEquals(offered["a"], replacement), "inline replacement lease is not removed");

        // C27-02 / C27-04: retries are triggered by unscaled time after a
        // synthetic TCP/Steam queue failure, not by another incoming query.
        var gate = new MapCacheOutboundRetry();
        byte[] offer = { 1, 2, 3 };
        int calls = 0, successes = 0;
        Action<byte[]> failFirst = bytes =>
        {
            calls++;
            if (calls == 1) throw new IOException("queue full");
            Check(ReferenceEquals(bytes, offer), "deferred send retains exact encoded bytes");
            successes++;
        };
        Check(!gate.TrySend(MapCacheOutboundKind.Offer, "view", 12, 3, 5, offer, failFirst)
            && gate.Count == 1 && calls == 1, "failed offer remains queued once");
        Check(!gate.TrySend(MapCacheOutboundKind.Offer, "view", 12, 3, 5, offer, failFirst)
            && calls == 1, "duplicate offer cannot bypass backoff");
        gate.Advance(1.999, (_, __, ___, ____, _____) => true, failFirst);
        Check(calls == 1, "retry waits full real-time backoff");
        gate.Advance(0.001, (_, __, ___, ____, _____) => true, failFirst);
        Check(calls == 2 && successes == 1 && gate.Count == 0, "offer succeeds once after retry");

        var failed = new MapCacheOutboundRetry();
        byte[] retryCache = { 10 }, retryTerrain = { 11 };
        Check(!failed.TrySend(MapCacheOutboundKind.CacheRetry, "same-view", 1, 4, 5, retryCache,
            _ => throw new InvalidOperationException("connection not available")), "closed peer caches pending recovery");
        Check(!failed.TrySend(MapCacheOutboundKind.FullTerrainRetry, "same-view", 1, 4, 5, retryTerrain,
            _ => throw new IOException("Steam queue full")), "terrain and cache recovery use independent keys");
        var delivered = new List<byte>();
        failed.Advance(2, (_, __, ___, ____, _____) => true,
            data => delivered.Add(data[0]));
        Check(delivered.Count == 2 && delivered.Contains(10) && delivered.Contains(11) && failed.Count == 0,
            "both approved recovery modes retry without duplicate FIFO entries");

        // C27-05/06: stale lease epochs and disconnect clear pending bytes.
        Check(!failed.TrySend(MapCacheOutboundKind.CacheRetry, "stale", 1, 1, 8, offer,
            _ => throw new IOException("queue full")), "stale test setup");
        failed.Advance(2, (_, __, ___, ____, _____) => false, _ => throw new Exception("stale send"));
        Check(failed.Count == 0, "stale view never sends across authority changes");
        Check(!failed.TrySend(MapCacheOutboundKind.Offer, "disconnect", 5, 1, 2, offer,
            _ => throw new IOException("disconnected")), "disconnect test setup");
        failed.Clear();
        Check(failed.Count == 0 && !failed.HasPending(MapCacheOutboundKind.Offer, "disconnect"),
            "session teardown drops every queued recovery");

        // C27-08: bounded retry queue, programming faults escape, clock
        // errors are not silently treated as a successful request.
        var bounded = new MapCacheOutboundRetry();
        for (int i = 0; i < MapCacheOutboundRetry.MaxPending; i++)
            Check(!bounded.TrySend(MapCacheOutboundKind.Offer, "id" + i, i, 1, 1,
                offer, _ => throw new IOException("full")), "bounded pending setup");
        Check(bounded.Count == MapCacheOutboundRetry.MaxPending, "pending capacity is fixed");
        Check(Throws<InvalidDataException>(() => bounded.TrySend(MapCacheOutboundKind.Offer,
                "overflow", 0, 1, 1, offer, _ => throw new IOException("full"))),
            "cannot retain an unbounded number of outgoing frames");
        Check(Throws<FormatException>(() => new MapCacheOutboundRetry().TrySend(MapCacheOutboundKind.Offer,
                "fault", 1, 1, 1, offer, _ => throw new FormatException("bad frame"))),
            "serialization/programming errors are not hidden as backpressure");
        Check(Throws<ArgumentOutOfRangeException>(() => bounded.Advance(-1,
                (_, __, ___, ____, _____) => true, _ => { })),
            "negative unscaled seconds are rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => bounded.Advance(double.NaN,
                (_, __, ___, ____, _____) => true, _ => { })),
            "nonfinite clock is rejected");
        Check(Throws<ArgumentException>(() => bounded.TrySend((MapCacheOutboundKind)99,
                "bad", 1, 1, 1, offer, _ => { })), "unknown kind is rejected");

        // Reentrancy: sending a pending message may dispose and create a new
        // session. Completion of the old send must not erase the new entry.
        var reentered = new MapCacheOutboundRetry();
        reentered.TrySend(MapCacheOutboundKind.Offer, "same", 7, 1, 1, offer,
            _ => throw new IOException("full"));
        byte[] newMessage = { 15 };
        reentered.Advance(2, (_, __, ___, ____, _____) => true, _ =>
        {
            reentered.Clear();
            reentered.TrySend(MapCacheOutboundKind.Offer, "same", 7, 2, 2, newMessage,
                __ => throw new IOException("new session full"));
        });
        Check(reentered.Count == 1 && reentered.HasPending(MapCacheOutboundKind.Offer, "same"),
            "old success cannot erase a new session's queued lease");
        reentered.Clear();

        // The still-current callback may synchronously replace the same view.
        // Neither a stale result nor a formerly valid result can delete or send
        // the newer lease during the old snapshot's iteration.
        foreach (bool stillValid in new[] { false, true })
        {
            var replacedDuringCheck = new MapCacheOutboundRetry();
            byte[] replacementPacket = { 23 };
            Check(!replacedDuringCheck.TrySend(MapCacheOutboundKind.Offer, "replaced", 8, 1, 1,
                offer, _ => throw new IOException("old queue full")), "validation replacement setup");
            int staleSends = 0;
            replacedDuringCheck.Advance(2, (kind, viewId, _, __, ___) =>
            {
                replacedDuringCheck.Remove(kind, viewId);
                Check(!replacedDuringCheck.TrySend(kind, viewId, 8, 2, 2, replacementPacket,
                    _ => throw new IOException("new queue full")), "callback can insert new lease");
                return stillValid;
            }, _ => staleSends++);
            Check(replacedDuringCheck.Count == 1 && staleSends == 0,
                "stale callback cannot remove or transmit a replacement lease");
            replacedDuringCheck.Advance(2, (_, __, ___, ____, _____) => true,
                bytes => Check(ReferenceEquals(bytes, replacementPacket), "only replacement bytes may send"));
            Check(replacedDuringCheck.Count == 0, "new lease remains independently retryable");
        }

        // In a congested map load this runs every frame. A pending non-due
        // retry must not allocate a list/array on every Tick after warmup.
        var noAllocation = new MapCacheOutboundRetry();
        Check(!noAllocation.TrySend(MapCacheOutboundKind.Offer, "allocation", 1, 1, 1,
            offer, _ => throw new IOException("full")), "allocation setup");
        Func<MapCacheOutboundKind, string, long, long, long, bool> current =
            (_, __, ___, ____, _____) => true;
        Action<byte[]> neverDue = _ => throw new Exception("unexpected retry");
        for (int i = 0; i < 64; i++) noAllocation.Advance(0, current, neverDue);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) noAllocation.Advance(0, current, neverDue);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "warm pending Tick creates no managed snapshot garbage (" + allocated + " bytes)");

        // Stable randomized backoff: each first send fails, exactly one retry
        // succeeds after a positive interval, with no early extra sends.
        var random = new Random(27027);
        for (int i = 0; i < 12000; i++)
        {
            gate.Clear();
            int attempted = 0;
            byte[] payload = { (byte)(i % 251) };
            Action<byte[]> send = bytes =>
            {
                attempted++;
                Check(ReferenceEquals(payload, bytes), "random retry retains original payload");
                if (attempted == 1) throw new IOException("synthetic transient failure");
            };
            var kind = (MapCacheOutboundKind)(i % 3);
            Check(!gate.TrySend(kind, "rnd" + i, i, 9, 2, payload, send),
                "random first failure deferred");
            double elapsed = random.NextDouble() * 1.9999;
            gate.Advance(elapsed, (_, __, ___, ____, _____) => true, send);
            Check(attempted == 1 && gate.Count == 1, "no premature random retry");
            gate.Advance(2 - elapsed, (_, __, ___, ____, _____) => true, send);
            Check(attempted == 2 && gate.Count == 0, "one random retry when due");
        }
        ProductionSessionChecks.Run(Check);
        Console.WriteLine("PASS: " + checks + " linked production map cache negotiation and " +
            "outbound retry/failure-injection checks");
    }
}
