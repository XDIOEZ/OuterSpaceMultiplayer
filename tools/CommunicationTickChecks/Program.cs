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
            // The original private entrypoint is shared by live chat, visit,
            // and battle commands; this compiles that production method,
            // rather than calling only the extracted utility in isolation.
            Check(MultiplayerSession.TestCommunicationId("0123456789abcdefABCDEF0123456789") &&
                MultiplayerSession.TestCommunicationId(Guid.Empty.ToString("N")),
                "shared production communication ID accepts exact ASCII hex, in either case");
            Check(!MultiplayerSession.TestCommunicationId(null) &&
                !MultiplayerSession.TestCommunicationId("") &&
                !MultiplayerSession.TestCommunicationId("0123456789abcdefABCDEF012345678Z") &&
                !MultiplayerSession.TestCommunicationId("0123456789abcdefABCDEF0123456789 "),
                "shared production communication ID rejects null, length, bad hex and whitespace");
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

            // Unlike the idle case above, an active 5-minute consent used to
            // allocate both full Values.ToList snapshots on EVERY tick.
            const int activeFrames = 5000;
            var activeHost = new MultiplayerSession { IsHost = true };
            activeHost.AddLocalVisit("active-local", 3000);
            activeHost.AddStableHostVisit("active-host", 3000);
            long activeAllocated = AllocatedByTicks(() => activeHost.TestTick(0.016), activeFrames);
            Check(activeAllocated == 0,
                "active host visit/consent timers reuse both snapshots without managed allocations");
            Check(Math.Abs(activeHost.LocalVisit("active-local").RemainingSeconds -
                (3000 - (activeFrames + 100) * .016)) < 0.0001 &&
                Math.Abs(activeHost.HostVisitSeconds("active-host") -
                (3000 - (activeFrames + 100) * .016)) < 0.0001,
                "actual production tick still advances both active clocks once per frame");
            Check(activeHost.LocalVisit("active-local").State == PlayerVisitState.Waiting &&
                activeHost.HostVisitStatus("active-host") == PlayerVisitState.Waiting,
                "long-lived active consent is not expired or cancelled prematurely");
            Console.WriteLine("INFO: " + activeFrames +
                " production host ticks with active local and remote visits: allocated " +
                activeAllocated + " B for visit snapshots after warmup");

            // Real pending and delivery ACKs can remain active for many game
            // frames (including long latency or a briefly unreachable peer).
            // Measure BOTH production TickCommunication snapshot paths while
            // using stable, non-expiring timeouts, not empty dictionaries.
            const int pendingCount = 8, deliveryCount = 64, activeChatFrames = 5000;
            var activeChat = new MultiplayerSession { IsHost = true };
            for (int i = 0; i < pendingCount; i++)
                activeChat.AddPending("stable-chat-" + i, 3000);
            for (int i = 0; i < deliveryCount; i++)
                activeChat.AddDelivery("stable-relay-" + i, 3000);
            long activeChatAllocated =
                AllocatedByTicks(() => activeChat.TestTick(0.016), activeChatFrames);
            Check(activeChatAllocated == 0,
                "real active host chat/relay tick reuses both ACK timer value snapshots");
            Check(activeChat.PendingCount == pendingCount &&
                activeChat.DeliveryCount == deliveryCount,
                "no active room ACK is prematurely expired or lost");
            Check(Math.Abs(activeChat.PendingSeconds("stable-chat-0") -
                (3000 - (activeChatFrames + 100) * .016)) < .0001 &&
                Math.Abs(activeChat.DeliverySeconds("stable-relay-0") -
                (3000 - (activeChatFrames + 100) * .016)) < .0001,
                "both real ACK countdown clocks advance exactly once per frame");
            Console.WriteLine("INFO: " + activeChatFrames +
                " production ticks with " + pendingCount + " pending messages and " +
                deliveryCount + " delivery ACKs: managed timer-snapshot allocations " +
                activeChatAllocated + " B after warmup");
            activeChat.TestDisconnectAllCommunication();
            Check(activeChat.PendingCount == 0 && activeChat.DeliveryCount == 0,
                "room exit clears both ACK windows and resets their reusable buffers");

            var nestedPending = new MultiplayerSession { IsHost = false };
            nestedPending.AddPending("outer-chat", 0.1);
            nestedPending.AddPending("other-chat", 10);
            bool nestedChatTick = false;
            nestedPending.ChatChanged += msg =>
            {
                if (msg.ClientMessageId != "outer-chat" || nestedChatTick) return;
                nestedChatTick = true;
                nestedPending.AddPending("born-chat", 20);
                nestedPending.TestTick(0.25);
            };
            nestedPending.TestTick(0.1);
            Check(nestedChatTick && nestedPending.PendingCount == 2 &&
                Math.Abs(nestedPending.PendingSeconds("other-chat") - 9.65) < .00001 &&
                Math.Abs(nestedPending.PendingSeconds("born-chat") - 19.75) < .00001,
                "reentrant real ChatChanged listener snapshots the newly added message only in its own tick");

            var nestedDelivery = new MultiplayerSession { IsHost = true };
            nestedDelivery.AddPending("client-first-delivery", 50);
            nestedDelivery.AddDelivery("first-delivery", 0.1, senderLocal: true);
            bool addedDuringDelivery = false;
            nestedDelivery.ChatChanged += msg =>
            {
                if (msg.ClientMessageId != "client-first-delivery" || addedDuringDelivery) return;
                addedDuringDelivery = true;
                nestedDelivery.AddDelivery("born-delivery", 10);
            };
            nestedDelivery.TestTick(0.2);
            Check(addedDuringDelivery && nestedDelivery.DeliveryCount == 1 &&
                nestedDelivery.DeliverySeconds("born-delivery") == 10,
                "delivery callback may add an ACK without advancing it in the current snapshot");
            nestedDelivery.TestTick(0.3);
            Check(Math.Abs(nestedDelivery.DeliverySeconds("born-delivery") - 9.7) < .00001,
                "callback-added delivery is first advanced by the next real host tick");

            var failingChat = new MultiplayerSession { IsHost = false };
            failingChat.AddPending("fail-chat", 0.1);
            failingChat.AddPending("after-fail", 10);
            Action<RoomChatMessage> failListener = msg =>
            { throw new InvalidOperationException("test chat UI callback failure"); };
            failingChat.ChatChanged += failListener;
            bool chatErrorPropagated = false;
            try { failingChat.TestTick(0.2); }
            catch (InvalidOperationException error)
            { chatErrorPropagated = error.Message == "test chat UI callback failure"; }
            finally { failingChat.ChatChanged -= failListener; }
            Check(chatErrorPropagated &&
                Math.Abs(failingChat.PendingSeconds("after-fail") - 10) < .00001,
                "chat UI callback failure propagates without advancing later pending records");
            failingChat.TestTick(0.3);
            Check(Math.Abs(failingChat.PendingSeconds("after-fail") - 9.7) < .00001,
                "a failed chat callback releases the snapshot for normal retry");

            // Real VisitChanged listener can synchronously reenter Tick and
            // must not overwrite the outer visit timer snapshot.
            var reentrantVisits = new MultiplayerSession { IsHost = false };
            reentrantVisits.AddLocalVisit("outer-expiring", 0.1);
            reentrantVisits.AddLocalVisit("other", 10);
            bool nestedTick = false;
            reentrantVisits.VisitChanged += update =>
            {
                if (update.Request.RequestId != "outer-expiring" || nestedTick) return;
                nestedTick = true;
                reentrantVisits.TestTick(0.25);
            };
            reentrantVisits.TestTick(0.1);
            Check(nestedTick && reentrantVisits.LocalVisit("outer-expiring").State == PlayerVisitState.Expired,
                "native visit notification can safely reenter the real production Tick");
            Check(Math.Abs(reentrantVisits.LocalVisit("other").RemainingSeconds - 9.65) < 0.00001,
                "nested tick preserves outer snapshot order and exact elapsed time");

            var failingVisit = new MultiplayerSession { IsHost = false };
            failingVisit.AddLocalVisit("fault", 0.1);
            Action<PlayerVisitUpdate> brokenListener =
                update => { throw new InvalidOperationException("test visit callback failure"); };
            failingVisit.VisitChanged += brokenListener;
            bool propagated = false;
            try { failingVisit.TestTick(0.2); }
            catch (InvalidOperationException error)
            { propagated = error.Message == "test visit callback failure"; }
            finally { failingVisit.VisitChanged -= brokenListener; }
            Check(propagated, "visit notification exception propagates without losing the frame lease");
            failingVisit.AddLocalVisit("after-fault", 10);
            failingVisit.TestTick(0.4);
            Check(Math.Abs(failingVisit.LocalVisit("after-fault").RemainingSeconds - 9.6) < 0.00001,
                "next actual tick can reuse the snapshot after a failing UI notification");

            var rateHost = new MultiplayerSession { IsHost = true };
            for (int i = 0; i < 20; i++)
                Check(rateHost.TestConsumeRate("guest", "battleFrame"),
                    "host battle-frame input consumes exact original 20 tokens");
            Check(!rateHost.TestConsumeRate("guest", "battleFrame"),
                "host rejects battle-frame input after original category budget");
            Check(rateHost.TestConsumeRate("guest", "chat"),
                "same player retains independent chat token bucket");
            Check(rateHost.TestConsumeRate("host", "battleFrame"),
                "another player retains independent frame token bucket");
            rateHost.TestTick(0.05);
            Check(rateHost.TestConsumeRate("guest", "battleFrame"),
                "production idle Tick advances rate-limit refill time");
            rateHost.TestDisconnectRate("guest");
            for (int i = 0; i < 20; i++)
                Check(rateHost.TestConsumeRate("guest", "battleFrame"),
                    "real disconnect clears all categories for the former guest");
            Check(!rateHost.TestConsumeRate("guest", "battleFrame"),
                "same player is again rate-limited after its fresh reconnect budget");
            Check(rateHost.TestConsumeRate("host", "battleFrame"),
                "disconnect cannot replenish or invalidate the host's own limiter");

            Console.WriteLine("PASS: " + checks + " production communication/visit tick and rate checks");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
