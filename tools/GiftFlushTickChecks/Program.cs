using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool result, string reason)
    {
        if (!result) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static GiftCheckpointRequest Gift(long map, long epoch = 1)
    {
        return new GiftCheckpointRequest { MapId = map, AuthorityEpoch = epoch, TransferId = "transfer-" + map };
    }
    private static bool Throws<TException>(Action operation) where TException : Exception
    {
        try { operation(); return false; }
        catch (TException) { return true; }
    }
    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    private static void CheckSnapshot(Dictionary<long, GiftCheckpointRequest> source,
        VisitTickSnapshot<GiftCheckpointRequest> buffer, string label)
    {
        List<GiftCheckpointRequest> original = source.Values.ToList();
        List<GiftCheckpointRequest> actual = buffer.CaptureDictionary(source);
        try
        {
            Check(original.Count == actual.Count, label + " count");
            for (int i = 0; i < original.Count; i++)
                Check(ReferenceEquals(original[i], actual[i]), label + " order/reference " + i);
        }
        finally { buffer.Release(actual); }
    }
    private static void MainChecks()
    {
        var buffer = new VisitTickSnapshot<GiftCheckpointRequest>();
        Check(Throws<ArgumentNullException>(() => buffer.CaptureDictionary<long>(null)),
            "numeric-key dictionary null guard");
        CheckSnapshot(new Dictionary<long, GiftCheckpointRequest>(), buffer, "empty numeric dictionary");
        var random = new Random(20261018);
        for (int round = 0; round < 2400; round++)
        {
            var source = new Dictionary<long, GiftCheckpointRequest>();
            int n = random.Next(0, 65);
            for (int i = 0; i < n; i++)
            {
                long id = random.Next(0, 2) == 0 ? random.Next(1, 999) :
                    (long)random.Next(1, 100000) << 32 | (uint)random.Next();
                if (!source.ContainsKey(id)) source.Add(id, Gift(id));
            }
            CheckSnapshot(source, buffer, "random gift dictionary " + round);
            if (round % 117 == 0)
            {
                var outer = buffer.CaptureDictionary(source);
                var inner = buffer.CaptureDictionary(source);
                Check(!ReferenceEquals(outer, inner), "reentrant capture owns a different list");
                inner.Clear();
                Check(outer.Count == source.Count, "nested release cannot clear outer tick");
                buffer.Release(inner);
                buffer.Reset();
                Check(outer.Count == source.Count, "room reset defers while snapshot is in use");
                buffer.Release(outer);
                Check(outer.Capacity == 0, "deferred reset frees high-water array");
            }
        }
        var session = new MultiplayerSession();
        Check(Allocated(session.Tick, 200000) == 0, "idle guest gift tick allocates no snapshots");

        session.AddMap(1);
        var gift = Gift(1);
        session.Receive(gift);
        Check(session.FreezeCount == 1 && session.PendingCount == 1 && session.IsPaused(1),
            "real ReceiveGiftFlush freezes and queues exactly once");
        session.HoldFlight();
        session.Tick();
        Check(session.PendingCount == 1 && session.MapPublishes == 0, "in-flight map blocks checkpoint");
        long activeAllocation = Allocated(session.Tick, 60000);
        Check(activeAllocation == 0, "pending gift ticks reuse value snapshot without allocation");
        session.ClearFlights();
        session.OnPublishMaps = session.HoldPersonal;
        session.Tick();
        Check(session.PendingCount == 1 && session.PersonalPublishes == 1,
            "in-flight personal checkpoint after publish blocks ACK");
        session.OnPublishMaps = null;
        session.ClearFlights();
        int sent = 0;
        session.OnSend = (peer, kind, payload) =>
        {
            Check(peer == "host" && kind == "gift.flushed" && ReferenceEquals(payload, gift),
                "original gift ACK recipient, kind and DTO identity");
            sent++;
        };
        session.Tick();
        Check(sent == 1 && session.PendingCount == 0 && session.IsPaused(1),
            "sent gift flush is removed but map remains paused until resume");
        session.Resume(gift);
        Check(!session.IsPaused(1), "gift resume releases map pause after acknowledged flush");
        session.Tick();
        Check(sent == 1, "idle after ACK never repeats prior gift");

        var stale = new MultiplayerSession();
        stale.AddMap(2, 2);
        stale.Enqueue(Gift(2, 1));
        stale.Tick();
        Check(stale.PendingCount == 0 && !stale.IsPaused(2), "stale authority epoch discarded");
        stale.AddMap(3, 1, "another-guest");
        stale.Enqueue(Gift(3));
        stale.Tick();
        Check(stale.PendingCount == 0 && !stale.IsPaused(3), "changed simulator discarded");
        stale.Enqueue(Gift(4));
        stale.Tick();
        Check(stale.PendingCount == 0 && !stale.IsPaused(4), "missing map discarded");

        var callbacks = new MultiplayerSession();
        callbacks.AddMap(10); callbacks.AddMap(11); callbacks.AddMap(12);
        var first = Gift(10); var second = Gift(11); var later = Gift(12);
        callbacks.Enqueue(first); callbacks.Enqueue(second);
        var seen = new List<long>();
        callbacks.OnSend = (peer, kind, payload) =>
        {
            long id = ((GiftCheckpointRequest)payload).MapId;
            seen.Add(id);
            if (id == 10) callbacks.Enqueue(later);
        };
        callbacks.Tick();
        Check(seen.SequenceEqual(new long[] { 10, 11 }) && callbacks.PendingCount == 1,
            "callback insertion never enters same tick snapshot, original order preserved");
        callbacks.Tick();
        Check(seen.SequenceEqual(new long[] { 10, 11, 12 }) && callbacks.PendingCount == 0,
            "subsequent tick picks up callback-created gift");

        var failure = new MultiplayerSession();
        failure.AddMap(20); failure.Enqueue(Gift(20));
        failure.OnSend = (peer, kind, payload) => { throw new InvalidOperationException("network offline"); };
        Check(Throws<InvalidOperationException>(failure.Tick), "send error propagates");
        Check(failure.PendingCount == 1 && failure.IsPaused(20),
            "failed ACK remains queued with original map freeze");
        failure.OnSend = null;
        failure.Tick();
        Check(failure.PendingCount == 0, "retry succeeds after failed callback releases snapshot");

        var reentrant = new MultiplayerSession();
        reentrant.AddMap(30); reentrant.AddMap(31);
        reentrant.Enqueue(Gift(30)); reentrant.Enqueue(Gift(31));
        var nestedOrder = new List<long>();
        bool inside = false;
        reentrant.OnSend = (peer, kind, payload) =>
        {
            long id = ((GiftCheckpointRequest)payload).MapId;
            nestedOrder.Add(id);
            if (id == 30 && !inside)
            {
                inside = true;
                reentrant.Tick();
            }
        };
        reentrant.Tick();
        Check(nestedOrder.SequenceEqual(new long[] { 30, 30, 31, 31 }),
            "reentrant gift tick preserves both original outer and inner value snapshots");
        Check(reentrant.PendingCount == 0, "reentrant tick eventually drains original queue");

        var map = new Dictionary<long, GiftCheckpointRequest>();
        for (long id = 0; id < 64; id++) map.Add(id, Gift(id));
        var cached = new VisitTickSnapshot<GiftCheckpointRequest>();
        Action original = () =>
        {
            foreach (GiftCheckpointRequest item in map.Values.ToList())
                if (item == null) throw new InvalidOperationException();
        };
        Action production = () =>
        {
            var snapshot = cached.CaptureDictionary(map);
            try
            {
                foreach (GiftCheckpointRequest item in snapshot)
                    if (item == null) throw new InvalidOperationException();
            }
            finally { cached.Release(snapshot); }
        };
        long oldBytes = Allocated(original, 12000);
        long newBytes = Allocated(production, 12000);
        Check(oldBytes > 0 && newBytes == 0, "production long-key snapshot avoids active-list allocations");
        Console.WriteLine("INFO: 200000 idle ticks 0 B, 60000 pending-blocked ticks " +
            activeAllocation + " B, 12000 synthetic × 64 gift records old Values.ToList " +
            oldBytes + " B vs production snapshot " + newBytes + " B");
    }
    private static int Main()
    {
        try
        {
            MainChecks();
            Console.WriteLine("PASS: " + checks + " production-linked guest gift tick checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
