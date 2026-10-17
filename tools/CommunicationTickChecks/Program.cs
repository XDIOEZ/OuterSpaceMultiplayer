using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }

    private static long AllocatedByTicks(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action(); // Warm JIT and collection dispatch.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // Previous production empty-path behavior: four independent ToList() calls
    // per host tick. This is a reference for allocation comparison, not a copy
    // of the game/session timer logic.
    private static long LegacyEmptySnapshotAllocations(int iterations)
    {
        var empty = new Dictionary<int, object>();
        return AllocatedByTicks(() =>
        {
            GC.KeepAlive(empty.Values.ToList()); // pendingChats
            GC.KeepAlive(empty.Values.ToList()); // chatDeliveries
            GC.KeepAlive(empty.Values.ToList()); // localVisits
            GC.KeepAlive(empty.Values.ToList()); // hostVisits
        }, iterations);
    }

    private static int Main()
    {
        try
        {
            const int iterations = 5000;
            var host = new MultiplayerSession { IsHost = true };
            long hostAllocated = AllocatedByTicks(() => host.TestTick(0.016), iterations);
            long oldAllocated = LegacyEmptySnapshotAllocations(iterations);
            Check(oldAllocated > 0 && hostAllocated < oldAllocated,
                "idle host no longer allocates four empty timer snapshots per frame");
            Check(hostAllocated == 0, "genuinely idle host tick has zero managed allocations");
            Check(host.TestClock > 1, "idle fast path still advances the real-time communication clock");

            var guest = new MultiplayerSession { IsHost = false };
            long guestAllocated = AllocatedByTicks(() => guest.TestTick(0.016), iterations);
            Check(guestAllocated == 0, "idle guest tick also avoids empty collection allocations");
            Console.WriteLine("INFO: " + iterations + " idle host ticks allocated " +
                oldAllocated + " bytes for old empty-list snapshots versus " + hostAllocated +
                " bytes via the actual optimized TickCommunication/ TickVisits methods");

            var settled = new MultiplayerSession();
            settled.AddSettledHistory(64, 128);
            settled.TestTick(0.5);
            Check(settled.LocalVisit("local-history-0").RemainingSeconds == 49.5,
                "settled visit history retains the legacy countdown reduction semantics");
            long settledAllocated = AllocatedByTicks(() => settled.TestTick(0.016), iterations);
            Check(settledAllocated == 0,
                "bounded completed visit history does not allocate idle snapshots every tick");
            Console.WriteLine("INFO: " + iterations +
                " idle ticks with 64 local and 128 host settled visits allocated " +
                settledAllocated + " bytes");

            var pending = new MultiplayerSession();
            pending.AddPending("first", 1);
            pending.TestTick(0.4);
            Check(pending.PendingCount == 1 && Math.Abs(pending.PendingSeconds("first") - 0.6) < 0.00001,
                "pending chat timeout continues to count down");
            pending.TestTick(0.7);
            Check(pending.PendingCount == 0 && pending.ChatById("first").Delivery == RoomChatDelivery.Failed,
                "expired unacknowledged message is still failed and removed exactly once");

            pending.AddPending("original", 0.1);
            bool newChatAdded = false;
            pending.ChatChanged += msg =>
            {
                if (msg.ClientMessageId == "original" && !newChatAdded)
                {
                    newChatAdded = true;
                    pending.AddPending("reentrant", 5);
                }
            };
            pending.TestTick(0.2);
            Check(newChatAdded && pending.PendingCount == 1 && pending.PendingSeconds("reentrant") == 5,
                "chat result callback can add a pending message without changing the current tick snapshot");
            pending.TestTick(0.5);
            Check(Math.Abs(pending.PendingSeconds("reentrant") - 4.5) < 0.00001,
                "callback-created pending chat begins counting down on the next tick");

            var delivery = new MultiplayerSession();
            delivery.AddDelivery("relay-1", 1);
            delivery.TestTick(0.4);
            Check(delivery.DeliveryCount == 1, "host still retains unexpired delivery acknowledgements");
            delivery.TestTick(0.7);
            Check(delivery.DeliveryCount == 0 && delivery.SentKinds.Count(x => x == "chat.result") == 1,
                "host delivery timeout still returns exactly one failure result");

            var visits = new MultiplayerSession();
            visits.AddLocalVisit("first-visit", 0.4);
            bool newVisitAdded = false;
            visits.VisitChanged += update =>
            {
                if (update.Request.RequestId == "first-visit" && !newVisitAdded)
                {
                    newVisitAdded = true;
                    visits.AddLocalVisit("reentrant-visit", 10);
                }
            };
            visits.TestTick(0.5);
            Check(visits.LocalVisit("first-visit").State == PlayerVisitState.Expired
                && visits.LocalVisit("first-visit").RemainingSeconds == 0,
                "local visit timeout still clears the expired grant and countdown");
            Check(newVisitAdded && visits.LocalVisit("reentrant-visit").RemainingSeconds == 10,
                "visit callback may add a request during snapshot traversal");
            visits.TestTick(0.5);
            Check(visits.LocalVisit("reentrant-visit").RemainingSeconds == 9.5,
                "visit created inside callback is first advanced on the following tick");

            var hostVisits = new MultiplayerSession();
            hostVisits.AddHostVisit("host-visit", 0.5);
            hostVisits.TestTick(0.6);
            Check(hostVisits.HostVisitStatus("host-visit") == PlayerVisitState.Expired
                && hostVisits.HostVisitSeconds("host-visit") == 0,
                "host still expires a pending visit when its consent timer reaches zero");
            Check(hostVisits.SentKinds.Count(x => x == "visit.update") == 1,
                "host visit expiration retains its remote-party notification");

            var nonHost = new MultiplayerSession { IsHost = false };
            nonHost.AddHostVisit("host-should-not-tick", 2);
            nonHost.TestTick(1);
            Check(nonHost.HostVisitSeconds("host-should-not-tick") == 2,
                "guest cannot advance host-only visit deadlines");

            Console.WriteLine("PASS: " + checks + " production communication and visit tick checks");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
