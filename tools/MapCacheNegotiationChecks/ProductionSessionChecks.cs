using System;
using System.IO;
using FreeWorldMultiplayer;

// Link the actual production MapSnapshotCache + MapCacheExpiry partials to a
// deterministic host/client transport. This is not Unity or Steam Play Mode.
internal static class ProductionSessionChecks
{
    private static string Id(char c) { return new string(c, 32); }

    internal static void Run(Action<bool, string> check)
    {
        // C27-01/C27-07: failed first host query releases exactly its large
        // prepared lease, and healthy players may still receive a query.
        var host = new MultiplayerSession();
        host.ConfigureHost();
        host.Network.Failure = (peer, data) => peer == "bad-peer" && System.Text.Encoding.UTF8.GetString(data)
            .StartsWith("map.cache-query#") ? new IOException("full TCP queue") : null;
        bool hostFailed = false;
        try { host.HostSend("bad"); } catch (IOException) { hostFailed = true; }
        check(hostFailed && host.HostPendingViews == 0 && host.HostReservedBytes == 0,
            "production host query enqueue error releases one reserved map view");
        host.HostSend("good");
        check(host.HostPendingViews == 1 && host.HostReservedBytes > 0
            && host.Network.Sent.Count == 1 && host.Network.Sent[0] == "good-peer:map.cache-query",
            "healthy peer receives a real query despite other queue congestion");

        // C27-02: outgoing offer is retried on the session clock, without
        // another FIFO placeholder or repeated same-view query.
        var guest = new MultiplayerSession();
        int offerAttempts = 0;
        guest.Network.Failure = (peer, data) =>
        {
            if (System.Text.Encoding.UTF8.GetString(data).StartsWith("map.cache-offer#")
                && ++offerAttempts == 1) return new IOException("full outgoing queue");
            return null;
        };
        var query = MultiplayerSession.Query(9, Id('a'));
        guest.GuestQuery(query);
        check(guest.GuestWaitingViews == 1 && guest.GuestFifoEntries == 1
            && guest.DeferredPackets == 1 && offerAttempts == 1,
            "production client retains only one waiting FIFO slot after offer failure");
        guest.GuestQuery(query);
        check(guest.GuestFifoEntries == 1 && offerAttempts == 1,
            "duplicate query does not create a new slot or bypass the retry gate");
        guest.AdvanceCache(1.999);
        check(offerAttempts == 1, "production offer retry respects real-time backoff");
        guest.AdvanceCache(0.001);
        check(offerAttempts == 2 && guest.DeferredPackets == 0
            && guest.GuestWaitingViews == 1 && guest.GuestFifoEntries == 1,
            "transport retry enqueues offer and retains the single original FIFO slot");

        // C27-03: a valid host recovery request does not lose its single-use
        // permission when its outbound query failed to enter transport.
        var recoveryHost = new MultiplayerSession();
        recoveryHost.ConfigureHost();
        recoveryHost.HostSeedRetry("bad", Id('b'));
        int hostRetryAttempts = 0;
        recoveryHost.Network.Failure = (_, data) =>
        {
            if (System.Text.Encoding.UTF8.GetString(data).StartsWith("map.cache-query#")
                && ++hostRetryAttempts == 1) return new IOException("Steam full");
            return null;
        };
        var retry = new MapCacheRetry { ViewId = Id('b'), MapId = 7 };
        bool failedRecoveryQuery = false;
        try { recoveryHost.HostRetry(retry, "bad"); }
        catch (IOException) { failedRecoveryQuery = true; }
        check(failedRecoveryQuery && recoveryHost.HostRecoveryKeys == 0
            && recoveryHost.HostPendingViews == 0 && recoveryHost.HostReservedBytes == 0,
            "failed recovery query neither burns allowance nor leaks reserved snapshot");
        recoveryHost.HostRetry(retry, "bad");
        check(hostRetryAttempts == 2 && recoveryHost.HostRecoveryKeys == 1
            && recoveryHost.HostPendingViews == 1,
            "same validated recovery may be retried once and recorded only after successful enqueue");

        var brokenHost = new MultiplayerSession();
        brokenHost.ConfigureHost();
        brokenHost.HostSeedRetry("bad", Id('f'));
        brokenHost.Network.Failure = (_, __) => new FormatException("not an enqueue failure");
        bool unexpectedRecovery = false;
        try { brokenHost.HostRetry(new MapCacheRetry { ViewId = Id('f'), MapId = 7 }, "bad"); }
        catch (FormatException) { unexpectedRecovery = true; }
        check(unexpectedRecovery && brokenHost.HostRecoveryKeys == 1
            && brokenHost.HostPendingViews == 0, "unknown faults retain once-only recovery allowance");
        bool cannotReplayFaultedRecovery = false;
        try { brokenHost.HostRetry(new MapCacheRetry { ViewId = Id('f'), MapId = 7 }, "bad"); }
        catch (MessageRejectionException) { cannotReplayFaultedRecovery = true; }
        check(cannotReplayFaultedRecovery,
            "unexpected native/contract fault must not make asset recovery replayable");

        // C27-04: a corrupt chunk takes the existing guest retry mode and
        // moves to a resync barrier even when that retry's enqueue fails.
        var broken = new MultiplayerSession();
        var failedViewId = Id('c');
        broken.GuestExpect(MultiplayerSession.Query(11, failedViewId));
        broken.Network.Failure = (_, data) => System.Text.Encoding.UTF8.GetString(data)
            .StartsWith("map.cache-retry#") && broken.Network.Sent.Count == 0
                ? new IOException("synthetic congested peer") : null;
        MapSnapshotDeltaCodec.FailReassembly = true;
        MapView corruptedWireView = MultiplayerSession.MakeView(11, failedViewId);
        corruptedWireView.Snapshot = null; // Real encoded wire views carry Cache, not a full Snapshot.
        broken.GuestStage(corruptedWireView);
        MapSnapshotDeltaCodec.FailReassembly = false;
        check(broken.GuestWaitingViews == 0 && broken.GuestFifoEntries == 0
            && broken.DeferredPackets == 1 && broken.RecoveryBarriers == 1,
            "corrupt view retains a fail-closed revision barrier and defers failed retry");
        // Remove the injected queue error, then deliver exactly once.
        broken.Network.Failure = null;
        broken.AdvanceCache(2);
        check(broken.Network.Sent.Count == 1 && broken.Network.Sent[0] == "host:map.cache-retry"
            && broken.DeferredPackets == 0 && broken.RecoveryBarriers == 1,
            "cached recovery retry reaches host without releasing native revision barrier");

        // C27-05: full terrain recovery remains unique and bounded if the
        // first outbound FullTerrain=true request fails.
        var seed = new MultiplayerSession();
        var seedView = MultiplayerSession.MakeView(15, Id('d'));
        int seedSends = 0;
        seed.Network.Failure = (_, data) =>
        {
            if (System.Text.Encoding.UTF8.GetString(data).StartsWith("map.cache-retry#")
                && ++seedSends == 1) return new IOException("first SEED retry congested");
            return null;
        };
        check(seed.GuestTerrain(seedView, new BridgeResult { SeedMismatch = true, NativeUnchanged = true })
            && seed.DeferredPackets == 1 && seed.RecoveryBarriers == 1,
            "production seed mismatch defers full terrain request without resuming simulation");
        seed.AdvanceCache(2);
        check(seedSends == 2 && seed.DeferredPackets == 0
            && seed.Network.Sent.Count == 1, "full terrain retry eventually reaches host once");
        bool repeatsDenied = false;
        try { seed.GuestTerrain(seedView, new BridgeResult { SeedMismatch = true, NativeUnchanged = true }); }
        catch (InvalidOperationException) { repeatsDenied = true; }
        check(repeatsDenied, "same ViewId cannot loop forever through SEED retries");

        // C27-06: old session and authority changes discard deferred packets.
        var logout = new MultiplayerSession();
        logout.Network.Failure = (_, __) => new IOException("no connection");
        logout.GuestQuery(MultiplayerSession.Query(42, Id('e')));
        check(logout.DeferredPackets == 1, "old offer set up for logout");
        logout.DisposeCache();
        logout.Network.Failure = null;
        logout.AdvanceCache(3);
        check(logout.DeferredPackets == 0 && logout.Network.Sent.Count == 0,
            "leaving a session discards unsent old-world offer");

        var moved = new MultiplayerSession();
        moved.Network.Failure = (_, __) => new IOException("before ownership change");
        moved.GuestQuery(MultiplayerSession.Query(42, Id('e')));
        moved.BecomeStale(42, 3);
        moved.Network.Failure = null;
        moved.AdvanceCache(2);
        check(moved.DeferredPackets == 0 && moved.Network.Sent.Count == 0,
            "authority rollover invalidates a delayed cache offer before it can be sent");

        // C27-08: host per-player preparation capacity is bounded and a
        // non-transport fault must not be reported as a successful enqueue.
        var capped = new MultiplayerSession();
        capped.ConfigureHost();
        for (int i = 0; i < 8; i++) capped.HostSend("good");
        bool cappedOffer = false;
        try { capped.HostSend("good"); } catch (InvalidOperationException) { cappedOffer = true; }
        check(cappedOffer && capped.HostPendingViews == 8,
            "host still enforces eight pending full views per recipient");

        var persistent = new MultiplayerSession();
        int failedSends = 0;
        persistent.Network.Failure = (_, __) =>
        {
            failedSends++;
            return new IOException("persistent congestion");
        };
        persistent.GuestQuery(MultiplayerSession.Query(77, Id('7')));
        bool timedOut = false;
        try
        {
            for (int second = 0; second < 60; second++) persistent.AdvanceCache(1);
        }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut && failedSends <= 31,
            "60-second recovery limit fails closed without an unbounded transport retry storm");

        var malformed = new MultiplayerSession();
        malformed.ConfigureHost();
        malformed.Network.Failure = (_, __) => new FormatException("malformed envelope");
        bool surfaced = false;
        try { malformed.HostSend("bad"); } catch (FormatException) { surfaced = true; }
        check(surfaced && malformed.HostPendingViews == 0 && malformed.HostReservedBytes == 0,
            "unexpected encode/transport error is surfaced and reservation is not leaked");

        // An idle room must not allocate two LINQ list snapshots every Tick.
        var idleRoom = new MultiplayerSession();
        for (int i = 0; i < 64; i++) idleRoom.AdvanceCache(0);
        long idleBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 3000; i++) idleRoom.AdvanceCache(0);
        long idleBytes = GC.GetAllocatedBytesForCurrentThread() - idleBefore;
        check(idleBytes == 0, "idle map-cache Tick allocates no snapshot garbage: " + idleBytes);

        // The same should hold while views are pending for many frames.
        var activeRoom = new MultiplayerSession();
        activeRoom.GuestExpect(MultiplayerSession.Query(31, Id('8')));
        activeRoom.GuestExpect(MultiplayerSession.Query(32, Id('9')));
        var activeHost = new MultiplayerSession();
        activeHost.ConfigureHost();
        activeHost.HostSend("good");
        for (int i = 0; i < 64; i++)
        { activeRoom.AdvanceCache(0); activeHost.AdvanceCache(0); }
        long activeBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 3000; i++)
        { activeRoom.AdvanceCache(0); activeHost.AdvanceCache(0); }
        long activeBytes = GC.GetAllocatedBytesForCurrentThread() - activeBefore;
        check(activeBytes == 0 && activeRoom.GuestWaitingViews == 2 && activeHost.HostPendingViews == 1,
            "pending map-cache Tick keeps leases without allocation: " + activeBytes);

        // The first timeout can synchronously close a room. Its previous
        // snapshot still includes the second view: that stale key must be
        // skipped rather than looked up in a cleared stopwatch dictionary.
        var closing = new MultiplayerSession();
        closing.GuestExpect(MultiplayerSession.Query(40, Id('a')));
        closing.GuestExpect(MultiplayerSession.Query(41, Id('b')));
        int timedOutViews = 0;
        closing.OnFailMapLoading = error =>
        {
            check(error is TimeoutException, "timeout retains original exception kind");
            timedOutViews++;
            closing.DisposeCache();
        };
        closing.AdvanceCache(60);
        check(timedOutViews == 1 && closing.GuestWaitingViews == 0 && closing.DeferredPackets == 0,
            "room teardown during timeout skips subsequent stale snapshot entries");
    }
}
