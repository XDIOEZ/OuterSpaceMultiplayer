using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool valid, string label)
    {
        if (!valid) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void InitialRefusals()
    {
        var gate = new RejectedPeerEvictionGate();
        gate.Track(null, "null id"); gate.Track("", "empty id");
        Check(gate.Count == 0, "invalid peer IDs consume no eviction slots");
        Check(Throws<ArgumentNullException>(() => gate.TryDisconnect("a", null, null)),
            "null close delegate is rejected");
        Check(Throws<ArgumentNullException>(() => gate.Tick(0, null, null)),
            "null scheduled close delegate is rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => gate.Tick(-1, (id, why) => {}, null)),
            "negative unscaled delta rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => gate.Tick(double.NaN, (id, why) => {}, null)),
            "NaN unscaled delta rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => gate.Tick(double.PositiveInfinity, (id, why) => {}, null)),
            "infinite unscaled delta rejected");
        Check(!gate.TryDisconnect("unknown", (id, why) => {}, null),
            "unknown peer has no phantom disconnect action");
        gate.Track("a", "first fault");
        gate.Track("a", "second fault");
        gate.Track("A", "separate peer");
        Check(gate.Count == 2 && gate.Contains("a") && gate.Contains("A"),
            "repeated faults coalesce, peer IDs remain case-sensitive");
        string expectedReason = null;
        Check(gate.TryDisconnect("a", (id, reason) => expectedReason = reason, null) &&
            expectedReason == "first fault",
            "first rejection reason is retained during repeated malicious input");
        Check(gate.Contains("a"), "transport close request does not release peer before Disconnected");
        gate.Forget("a");
        Check(gate.Count == 1 && !gate.Contains("a") && gate.Contains("A"),
            "Disconnected event releases only exact peer identity");
        gate.Forget("a"); gate.Forget(null);
        Check(gate.Count == 1, "repeated close events are harmless");
    }

    private static void FailedTransportAndLogging()
    {
        var room = new MultiplayerSession();
        room.Fake.CloseFailures.Add("bad", 3);
        room.ThrowFromError = true;
        room.Rejected("bad", "invalid-map command");
        Check(room.QuarantinedCount == 1 && room.IsQuarantined("bad") &&
            room.Fake.Attempts.Count == 1 && room.Errors.Count == 1,
            "initial failed Disconnect and failed diagnostics cannot stop host Tick");
        for (int index = 0; index < 1000; index++) room.Advance(.001);
        Check(room.Fake.Attempts.Count == 1 && room.IsQuarantined("bad"),
            "failed close cannot cause per-frame flood of transport retries");
        room.Advance(1.0);
        Check(room.Fake.Attempts.Count == 2 && room.QuarantinedCount == 1,
            "one full two-second interval produces exactly one retry");
        room.Advance(2.0);
        Check(room.Fake.Attempts.Count == 3 && room.IsQuarantined("bad"),
            "transient second disconnect error stays quarantined");
        room.Advance(2.0);
        Check(room.Fake.Attempts.Count == 4 && room.Fake.Closed.Contains("bad") &&
            room.IsQuarantined("bad"),
            "eventual close succeeds but still waits for actual transport close event");
        room.Left("bad");
        Check(room.QuarantinedCount == 0,
            "real Disconnected event finally releases room's quarantine state");
        room.Advance(20);
        Check(room.Fake.Attempts.Count == 4,
            "no subsequent eviction of already-closed peer");
    }

    private static void OriginalErrorAndOtherPlayers()
    {
        var host = new MultiplayerSession();
        host.ReportRejectedMessage("healthy", false);
        Check(!host.IsQuarantined("healthy") &&
            host.Fake.Attempts.Count == 0,
            "successfully sent advisory refusal does not disconnect normal player");
        host.Fake.CloseFailures["malicious"] = 2;
        host.ReportRejectedMessage("malicious", true);
        Check(host.IsQuarantined("malicious") &&
            !host.IsQuarantined("healthy") &&
            host.Fake.Attempts.Count == 1,
            "original rejected message remains isolated to malformed peer");
        host.Advance(2);
        Check(host.Fake.Attempts.Count == 2 &&
            !host.Fake.Closed.Contains("malicious"),
            "failed initial rejection cleanup is retried, not silently acknowledged");
        host.Advance(2);
        Check(host.Fake.Closed.Contains("malicious") &&
            !host.IsQuarantined("healthy"),
            "another user's authority processing is unaffected by bad connection");
        host.Left("malicious");
        Check(host.QuarantinedCount == 0,
            "no quarantine remains after normal peer disconnected callback");
    }

    private static void SynchronousCallbackReentrancy()
    {
        var gate = new RejectedPeerEvictionGate();
        gate.Track("first", "bad");
        gate.Track("second", "bad");
        gate.Track("third", "bad");
        int called = 0;
        gate.Tick(2, (peer, reason) =>
        {
            called++;
            if (peer == "first")
            {
                gate.Forget("second");
                gate.Track("fourth", "new callback");
            }
            if (peer == "third")
                gate.Forget("third");
        }, null);
        Check(called == 2, "same-tick callback removal skips stale work safely");
        Check(gate.Contains("fourth") && !gate.Contains("second") &&
            !gate.Contains("third") && gate.Contains("first"),
            "new callback-created eviction waits until a later tick");
        gate.Tick(1.999, (peer, reason) => called++, null);
        Check(called == 2, "synchronous new peer is not retried too early");
        gate.Tick(.002, (peer, reason) => called++, null);
        Check(called == 4, "surviving entries retry exactly once after cooldown");

        var generation = new RejectedPeerEvictionGate();
        generation.Track("old", "old generation");
        generation.Track("reuse", "old generation");
        int reusedCalls = 0;
        generation.Tick(2, (peer, reason) =>
        {
            if (peer == "old")
            {
                generation.Forget("reuse");
                generation.Track("reuse", "new generation");
            }
            if (peer == "reuse") reusedCalls++;
        }, null);
        Check(reusedCalls == 0 && generation.Contains("reuse"),
            "reused peer ID cannot inherit already-expired old generation deadline");
        generation.Tick(1.999, (peer, reason) =>
        { if (peer == "reuse") reusedCalls++; }, null);
        Check(reusedCalls == 0, "new generation retains almost two seconds of quarantine");
        generation.Tick(.002, (peer, reason) =>
        { if (peer == "reuse") reusedCalls++; }, null);
        Check(reusedCalls == 1, "new generation may retry only after its own deadline");

        var synchronous = new MultiplayerSession();
        synchronous.Fake.OnDisconnect = id => synchronous.Left(id);
        synchronous.Rejected("sync", "unauthenticated stale packet");
        Check(!synchronous.IsQuarantined("sync") &&
            synchronous.Fake.Attempts.Count == 1 &&
            synchronous.Fake.Closed.Contains("sync"),
            "synchronous Disconnected callback safely releases current quarantine");
        synchronous.Advance(20);
        Check(synchronous.Fake.Attempts.Count == 1,
            "completed synchronous disconnect is not retried by the next host tick");
    }

    private static void GuestNeverEvictsHost()
    {
        var client = new MultiplayerSession();
        client.Host(false);
        client.Rejected("host", "failed local diagnostic");
        int before = client.Fake.Attempts.Count;
        client.Advance(30);
        Check(client.Fake.Attempts.Count == before && client.IsQuarantined("host"),
            "guest Tick never performs host-side peer eviction retries");
        client.Left("host");
        Check(client.QuarantinedCount == 0,
            "guest can clean state after its host disconnect callback");
    }

    private static void RandomFaultBatches()
    {
        var rng = new Random(20261019);
        for (int trial = 0; trial < 18000; trial++)
        {
            var room = new MultiplayerSession();
            int failures = rng.Next(0, 5);
            room.Fake.CloseFailures["bad"] = failures;
            room.ThrowFromError = rng.Next(4) == 0;
            room.Rejected("bad", "malformed map");
            room.Rejected("bad", "malformed map again");
            int initial = room.Fake.Attempts.Count;
            Check(initial == 1 && room.QuarantinedCount == 1,
                "duplicate rejection shares a single quarantine record " + trial);
            for (int step = 0; step < 6; step++)
            {
                room.Advance(2.0);
                Check(room.IsQuarantined("bad"),
                    "random failed or successful close never opens rejected peer prematurely " + trial);
            }
            Check(room.Fake.Closed.Contains("bad"),
                "bounded close failures eventually evict the bad peer " + trial);
            room.Left("bad");
            Check(room.QuarantinedCount == 0,
                "random disconnect event frees exact quarantined peer " + trial);
            Check(!room.IsQuarantined("other"),
                "random rejection leaves other player untouched " + trial);
        }
    }

    private static int Main()
    {
        try
        {
            InitialRefusals();
            FailedTransportAndLogging();
            OriginalErrorAndOtherPlayers();
            SynchronousCallbackReentrancy();
            GuestNeverEvictsHost();
            RandomFaultBatches();
            Console.WriteLine("PASS: " + checks +
                " production-linked rejected-peer eviction checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
