using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Ack
    {
        internal readonly string Id;
        internal double Seconds;
        internal Ack(string id, double seconds) { Id = id; Seconds = seconds; }
    }

    private static int checks;
    private static void Check(bool correct, string description)
    {
        if (!correct) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static void SameSnapshot(List<Ack> oldItems, List<Ack> current, string reason)
    {
        Check(oldItems.Count == current.Count, reason + ": chat snapshot length");
        for (int i = 0; i < oldItems.Count; i++)
            Check(ReferenceEquals(oldItems[i], current[i]), reason +
                ": original dictionary value enumeration order and identity");
    }
    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 90; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Main()
    {
        try
        {
            var pendingSnapshots = new VisitTickSnapshot<Ack>();
            var deliverySnapshots = new VisitTickSnapshot<Ack>();
            Check(Throws<ArgumentNullException>(() =>
                    pendingSnapshots.Capture(null)),
                "invalid null pending ACK dictionary is rejected before changing lease");
            Check(Throws<ArgumentNullException>(() =>
                    pendingSnapshots.Release(null)),
                "null snapshot release is rejected");

            var pending = new Dictionary<string, Ack>();
            var deliveries = new Dictionary<string, Ack>();
            for (int i = 0; i < 8; i++) pending.Add("pending:" + i, new Ack("pending:" + i, i + 1));
            for (int i = 0; i < 64; i++) deliveries.Add("relay:" + i, new Ack("relay:" + i, i + 1));
            var oldPending = pending.Values.ToList();
            var oldDeliveries = deliveries.Values.ToList();
            var pendingFrame = pendingSnapshots.Capture(pending);
            var deliveryFrame = deliverySnapshots.Capture(deliveries);
            try
            {
                SameSnapshot(oldPending, pendingFrame, "active pending message ACKs");
                SameSnapshot(oldDeliveries, deliveryFrame, "active host delivery ACKs");
                pending.Remove("pending:0");
                deliveries.Remove("relay:0");
                pending.Add("pending:born", new Ack("pending:born", 20));
                deliveries.Add("relay:born", new Ack("relay:born", 20));
                SameSnapshot(oldPending, pendingFrame,
                    "a chat callback mutates pending dictionary after snapshot capture");
                SameSnapshot(oldDeliveries, deliveryFrame,
                    "a delivery callback mutates room ACK dictionary after snapshot capture");
                Check(!pendingFrame.Exists(x => x.Id == "pending:born") &&
                    !deliveryFrame.Exists(x => x.Id == "relay:born"),
                    "new ACKs are not double-ticked in the existing frame");

                var inner = pendingSnapshots.Capture(pending);
                try
                {
                    Check(!ReferenceEquals(inner, pendingFrame) &&
                        inner.Exists(x => x.Id == "pending:born") &&
                        !inner.Exists(x => x.Id == "pending:0"),
                        "reentrant Tick gets independent snapshot with updated records");
                    SameSnapshot(oldPending, pendingFrame,
                        "inner Tick does not rewrite outer chat ACK values");
                }
                finally { pendingSnapshots.Release(inner); }
                Check(pendingFrame.Count == oldPending.Count,
                    "nested release does not clear outer chat Tick record references");
            }
            finally
            {
                pendingSnapshots.Release(pendingFrame);
                deliverySnapshots.Release(deliveryFrame);
            }
            Check(pendingFrame.Count == 0 && deliveryFrame.Count == 0,
                "pending and delivery snapshots release retained message references");
            Check(ReferenceEquals(pendingFrame, pendingSnapshots.Capture(pending)),
                "next ordinary Tick reuses the original pending snapshot backing list");
            // That lease was intentionally kept alive for a deferred reset.
            pendingSnapshots.Reset();
            Check(pendingFrame.Count == pending.Count,
                "room exit during a callback cannot invalidate an active chat iteration");
            pendingSnapshots.Release(pendingFrame);
            Check(pendingFrame.Count == 0 && pendingFrame.Capacity == 0,
                "deferred room cleanup clears references and releases high-water capacity");
            deliverySnapshots.Reset();
            Check(deliveryFrame.Capacity == 0,
                "room shutdown also releases the host delivery snapshot array");

            // Verify finally-release behavior when a live ChatChanged handler
            // throws from the middle of a pending timeout walk.
            var failing = new Dictionary<string, Ack>
            {
                { "one", new Ack("one", 0.1) },
                { "two", new Ack("two", 10) }
            };
            var failedLease = pendingSnapshots.Capture(failing);
            Check(Throws<InvalidOperationException>(() =>
                {
                    try
                    {
                        foreach (Ack ack in failedLease)
                            if ((ack.Seconds -= 0.2) <= 0)
                                throw new InvalidOperationException("chat callback failure");
                    }
                    finally { pendingSnapshots.Release(failedLease); }
                }), "failing chat callback propagates unchanged");
            var recovered = pendingSnapshots.Capture(failing);
            Check(ReferenceEquals(recovered, failedLease) && recovered.Count == 2,
                "snapshot can be safely reused after a notification exception");
            pendingSnapshots.Release(recovered);

            // Deterministic differential comparison against the old
            // Dictionary.Values.ToList() semantics, including null elements,
            // random changes and hot/cold ACK cardinalities.
            var random = new Random(20261018);
            for (int iteration = 0; iteration < 5500; iteration++)
            {
                var incoming = new Dictionary<string, Ack>();
                int count = random.Next(0, 140);
                for (int i = 0; i < count; i++)
                {
                    string id = "ack:" + iteration + ":" + i;
                    incoming.Add(id, random.Next(0, 41) == 0 ? null :
                        new Ack(id, random.NextDouble() * 500));
                }
                var previous = incoming.Values.ToList();
                var current = pendingSnapshots.Capture(incoming);
                try
                {
                    SameSnapshot(previous, current, "random active ACK set " + iteration);
                    string added = "new-after-capture";
                    incoming.Add(added, new Ack(added, 50));
                    if (previous.Count != 0)
                        incoming.Remove("ack:" + iteration + ":" + random.Next(previous.Count));
                    SameSnapshot(previous, current,
                        "dictionary mutated during active chat callback " + iteration);
                    Check(!current.Exists(x => x != null && x.Id == added),
                        "callback-added ACK becomes visible on next tick only");
                }
                finally { pendingSnapshots.Release(current); }
            }

            // Allocation test includes both active paths for one host Tick.
            // It excludes actual JSON cloning, network send, game update
            // and the creation of expired ChatOutcome errors.
            const int frames = 7000;
            var manyPending = new Dictionary<string, Ack>();
            var manyDeliveries = new Dictionary<string, Ack>();
            for (int i = 0; i < 8; i++) manyPending.Add("p" + i, new Ack("p" + i, 3000));
            for (int i = 0; i < 64; i++) manyDeliveries.Add("d" + i, new Ack("d" + i, 3000));
            Action oldHost = () =>
            {
                foreach (Ack ack in manyPending.Values.ToList()) ack.Seconds -= .0001;
                foreach (Ack ack in manyDeliveries.Values.ToList()) ack.Seconds -= .0001;
            };
            Action newHost = () =>
            {
                var local = pendingSnapshots.Capture(manyPending);
                try { foreach (Ack ack in local) ack.Seconds -= .0001; }
                finally { pendingSnapshots.Release(local); }
                var relay = deliverySnapshots.Capture(manyDeliveries);
                try { foreach (Ack ack in relay) ack.Seconds -= .0001; }
                finally { deliverySnapshots.Release(relay); }
            };
            long oldBytes = Allocated(oldHost, frames);
            long newBytes = Allocated(newHost, frames);
            Check(oldBytes > newBytes && newBytes == 0,
                "production reusable ACK snapshots avoid managed allocation on stable active host ticks");
            Console.WriteLine("INFO: " + frames + " synthetic active chat ticks, 8 pending + 64 host delivery ACKs: " +
                "old LINQ Value.ToList " + oldBytes + " B vs linked production snapshot " +
                newBytes + " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks + " production-linked active chat snapshot checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
