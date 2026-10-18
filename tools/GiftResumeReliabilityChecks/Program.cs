using System;
using System.Collections.Generic;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static GiftCheckpointRequest Gift(string id, long mapId, long epoch = 1)
    { return new GiftCheckpointRequest { TransferId = id, MapId = mapId, AuthorityEpoch = epoch }; }

    private static void HostQueueFailure()
    {
        var host = new MultiplayerSession();
        host.Host(); host.AddMap(10, 3, "guest");
        int flush = 0, attempted = 0, sent = 0;
        host.OnTrySend = (peer, kind, payload) =>
        {
            Check(peer == "guest", "host resume is addressed to its original simulator");
            if (kind == "gift.flush") { flush++; return true; }
            if (kind == "gift.resume") { attempted++; return false; }
            throw new InvalidOperationException("unknown gift packet " + kind);
        };
        Check(host.BeginGift("old", 10) != null && host.BarrierCount == 1 && flush == 1,
            "original gift flush is queued and remains registered");
        host.CancelGift("old");
        Check(host.BarrierCount == 0 && host.PendingResumeCount == 1 && attempted == 1,
            "failed reliable enqueue is retained after releasing completed barrier");
        Check(host.BeginGift("new", 10) != null && flush == 1 && host.BarrierCount == 0,
            "cannot start another transfer while previous restore is unqueued");
        for (int n = 0; n < 9; n++) host.RetryResumes(0.1);
        Check(attempted == 1 && host.PendingResumeCount == 1,
            "resume retries are limited to unscaled intervals, not every network tick");
        host.RetryResumes(0.11);
        Check(attempted == 2 && host.PendingResumeCount == 1,
            "one full second retries, failed send still retained");
        host.OnTrySend = (peer, kind, payload) =>
        {
            if (kind == "gift.resume") { sent++; return true; }
            if (kind == "gift.flush") { flush++; return true; }
            throw new InvalidOperationException(kind);
        };
        host.RetryResumes(1.0);
        Check(sent == 1 && host.PendingResumeCount == 0,
            "successful enqueue releases retry queue exactly once");
        host.RetryResumes(2.0);
        Check(sent == 1, "acknowledged enqueue does not send duplicates each tick");
        host.BeginGift("new", 10);
        Check(flush == 2 && host.BarrierCount == 1,
            "next gift is unblocked after old resume enters reliable transport");
    }

    private static void HostEpochAndSimulator()
    {
        var host = new MultiplayerSession();
        host.Host(); host.AddMap(11, 1, "guest");
        int oldResume = 0;
        host.OnTrySend = (peer, kind, payload) =>
        {
            if (kind == "gift.resume") { oldResume++; return false; }
            return true;
        };
        host.BeginGift("old-epoch", 11);
        host.CancelGift("old-epoch");
        Check(host.PendingResumeCount == 1, "pending restore persisted");
        host.ChangeMap(11, 2, "guest");
        Check(host.BeginGift("other", 11) != null && host.BarrierCount == 0,
            "same simulator at later epoch remains blocked pending old resume");
        host.OnTrySend = (peer, kind, payload) =>
        {
            if (kind == "gift.resume")
            {
                Check(((GiftCheckpointRequest)payload).AuthorityEpoch == 1,
                    "retry retains original frozen transfer and epoch");
                oldResume++;
            }
            return true;
        };
        host.RetryResumes(1);
        Check(host.PendingResumeCount == 0 && oldResume == 2,
            "legitimate old resume still sent after canonical epoch increases");
        Check(host.BeginGift("other", 11) != null && host.BarrierCount == 1,
            "new epoch gift can begin after old release was queued");

        var reassigned = new MultiplayerSession();
        reassigned.Host(); reassigned.AddMap(99, 1, "guest");
        reassigned.OnTrySend = (peer, kind, payload) => kind != "gift.resume";
        reassigned.BeginGift("stale", 99); reassigned.CancelGift("stale");
        reassigned.ChangeMap(99, 2, "other");
        reassigned.RetryResumes(1);
        Check(reassigned.PendingResumeCount == 0,
            "changed simulator invalidates old pending resume instead of targeting another player");

        var removed = new MultiplayerSession();
        removed.Host(); removed.AddMap(100, 1, "guest");
        removed.OnTrySend = (peer, kind, payload) => kind != "gift.resume";
        removed.BeginGift("removed", 100); removed.CancelGift("removed");
        removed.RemoveMap(100); removed.RetryResumes(1);
        Check(removed.PendingResumeCount == 0, "removed map drops obsolete outbox entry");
    }

    private static void HostUnexpectedEnqueueException()
    {
        var host = new MultiplayerSession();
        host.Host(); host.AddMap(93, 1, "guest");
        host.OnTrySend = (peer, kind, payload) =>
        {
            if (kind == "gift.resume") throw new InvalidOperationException("unexpected send failure");
            return true;
        };
        host.BeginGift("transport-fault", 93);
        Check(Throws<InvalidOperationException>(() => host.CancelGift("transport-fault")),
            "unexpected send exception propagates instead of reporting false success");
        Check(host.PendingResumeCount == 1 && host.BarrierCount == 0,
            "unexpected exception still retains retry outbox entry");
        host.OnTrySend = (peer, kind, payload) => true;
        host.RetryResumes(1);
        Check(host.PendingResumeCount == 0, "later send retry recovers an unexpected enqueue exception");
    }

    private static void GuestIdentityAndNativeRestoration()
    {
        var guest = new MultiplayerSession();
        guest.AddMap(20, 2);
        var first = Gift("first", 20, 2);
        guest.Receive(first);
        Check(guest.FreezeCount == 1 && guest.IsPaused(20) && guest.FrozenRequestCount == 1,
            "native map and one matching transfer are frozen");

        // The host must never be able to unfreeze a different active gift
        // merely by replaying an older mapId or an unrelated transfer ID.
        guest.Resume(Gift("other", 20, 2));
        Check(guest.IsPaused(20) && guest.RestoreCount == 0,
            "foreign transferId cannot release currently frozen map");
        guest.Resume(Gift("first", 20, 1));
        Check(guest.IsPaused(20) && guest.RestoreCount == 0,
            "wrong request epoch cannot release the frozen transfer");
        Check(Throws<MessageRejectionException>(() => guest.Receive(Gift("second", 20, 2))),
            "different gift flush cannot replace an active frozen transfer");
        Check(guest.FrozenRequestCount == 1, "rejected flush does not replace trusted frozen transfer");
        Check(Throws<MessageRejectionException>(() => guest.Resume(Gift("first", 20, 3))),
            "future authority epoch is rejected");
        Check(guest.IsPaused(20), "future epoch rejection preserves the native freeze");

        guest.RejectNativeRestore(true);
        Check(Throws<InvalidOperationException>(() => guest.Resume(first)),
            "native authority restore failure propagates");
        Check(guest.IsPaused(20) && guest.FrozenRequestCount == 1
            && guest.FailedRestoreCount == 1 && guest.RestoreCount == 0,
            "failed native restore keeps matching transfer frozen for retry");
        guest.RejectNativeRestore(false);
        guest.Resume(Gift("first", 20, 2));
        Check(!guest.IsPaused(20) && guest.FrozenRequestCount == 0
            && guest.RestoreCount == 1,
            "duplicate original request identity succeeds after native restore recovers");
        guest.Resume(first);
        Check(guest.RestoreCount == 1,
            "replayed successful gift.resume has no second native side effect");

        var second = Gift("second", 20, 2);
        guest.Receive(second);
        guest.Resume(first);
        Check(guest.IsPaused(20) && guest.FrozenRequestCount == 1
            && guest.RestoreCount == 1,
            "delayed previous resume cannot unfreeze a newer transfer on same map");
        guest.Resume(second);
        Check(!guest.IsPaused(20) && guest.RestoreCount == 2,
            "current transfer resume succeeds after stale old message was ignored");

        var advanced = new MultiplayerSession();
        advanced.AddMap(21, 1);
        var previousEpoch = Gift("old-epoch", 21);
        advanced.Receive(previousEpoch);
        advanced.ChangeMap(21, 2, "guest");
        advanced.Resume(previousEpoch);
        Check(!advanced.IsPaused(21) && advanced.RestoreCount == 1,
            "old matching gift can restore the same simulator after host map rebase");
    }

    private static void HostGuestIntegration()
    {
        var guest = new MultiplayerSession();
        guest.AddMap(123, 4);
        var host = new MultiplayerSession();
        host.Host(); host.AddMap(123, 4, "guest");
        int enqueueFailures = 2, delivered = 0, flush = 0;
        host.OnTrySend = (player, kind, payload) =>
        {
            var req = (GiftCheckpointRequest)payload;
            Check(player == "guest" && req.MapId == 123 && req.AuthorityEpoch == 4,
                "test transport forwards same authoritative DTO");
            if (kind == "gift.flush") { flush++; guest.Receive(req); return true; }
            if (kind == "gift.resume")
            {
                if (enqueueFailures-- > 0) return false;
                delivered++; guest.Resume(req); return true;
            }
            throw new InvalidOperationException(kind);
        };
        Check(host.BeginGift("delivery", 123) != null && flush == 1 && guest.IsPaused(123),
            "real host flush freezes original guest map before delivery");
        guest.OnSend = (peer, kind, payload) =>
        {
            Check(peer == "host" && kind == "gift.flushed",
                "guest sends real authoritative checkpoint response");
        };
        guest.Tick();
        Check(guest.PendingCount == 0 && guest.IsPaused(123),
            "guest checkpoint ACK must not resume native simulation");
        host.CancelGift("delivery");
        Check(host.PendingResumeCount == 1 && guest.IsPaused(123),
            "failed host enqueue keeps guest frozen and outbox pending");
        host.RetryResumes(1);
        Check(host.PendingResumeCount == 1 && guest.IsPaused(123),
            "second enqueue failure still never claims to have restored guest");
        host.RetryResumes(1);
        Check(host.PendingResumeCount == 0 && delivered == 1
            && !guest.IsPaused(123) && guest.RestoreCount == 1,
            "eventual reliable enqueue applies native guest authority once");
    }

    private static void RandomInterleavings()
    {
        var rng = new Random(20261018);
        for (int i = 0; i < 12000; i++)
        {
            var guest = new MultiplayerSession();
            long epoch = rng.Next(1, 100000);
            long id = (long)rng.Next(1, 100000) * 100 + rng.Next(100);
            guest.AddMap(id, epoch);
            var accepted = Gift("transfer-" + i, id, epoch);
            guest.Receive(accepted);
            int failures = rng.Next(0, 3);
            for (int f = 0; f < failures; f++)
            {
                guest.RejectNativeRestore(true);
                Check(Throws<InvalidOperationException>(() => guest.Resume(accepted)),
                    "random native failure on retry " + i);
                Check(guest.IsPaused(id) && guest.FrozenRequestCount == 1,
                    "random failed native restore preserves freeze " + i);
            }
            guest.RejectNativeRestore(false);
            if (rng.Next(4) == 0) guest.ChangeMap(id, epoch + 1, "guest");
            guest.Resume(Gift("other-" + i, id, epoch));
            Check(guest.IsPaused(id), "random unrelated resume remains inert " + i);
            guest.Resume(accepted);
            Check(!guest.IsPaused(id) && guest.FrozenRequestCount == 0
                && guest.RestoreCount == 1,
                "random same-transfer resume clears freeze once " + i);
            guest.Resume(accepted);
            Check(guest.RestoreCount == 1, "random repeat is idempotent " + i);
        }
    }

    private static int Main()
    {
        try
        {
            HostQueueFailure();
            HostEpochAndSimulator();
            HostUnexpectedEnqueueException();
            GuestIdentityAndNativeRestoration();
            HostGuestIntegration();
            RandomInterleavings();
            Console.WriteLine("PASS: " + checks + " production-linked gift resume reliability checks");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
