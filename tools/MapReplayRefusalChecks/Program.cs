using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool result, string reason)
    {
        if (!result) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static MultiplayerSession HostWithReplay(string first = "reject", string second = "valid")
    {
        var session = new MultiplayerSession();
        session.Bind("peer-a", "player-a");
        session.Bind("peer-b", "player-b");
        session.NativeBarrier(false);
        Check(session.Queue("peer-a", first), "first authenticated message deferred under native barrier");
        Check(session.Queue("peer-b", second), "other player stays in original FIFO slot");
        Check(session.Pending == 2 && session.PendingBytes == 40, "deferred frame reservation stays bounded");
        session.Ready();
        return session;
    }

    private static void Main()
    {
        // C30-01: ordinary expired command refuses, but does not poison
        // the map application barrier or fabricate an ACK for the command.
        var plain = HostWithReplay();
        plain.Tick(0);
        Check(!plain.Failed && plain.Pending == 0 && plain.PendingBytes == 0,
            "rejected request and subsequent valid request are consumed exactly once");
        Check(plain.Received.Count == 1 && plain.Received[0] == "peer-b:valid",
            "only the healthy player's valid request is applied");
        Check(plain.Network.Sent.Count == 1 && plain.Network.Sent[0] == "peer-a:error" &&
            !plain.Quarantined("peer-a") && plain.Network.Disconnected.Count == 0,
            "successful optional refusal notice is not a reason to disconnect a healthy peer");
        Check(!plain.ReplayFlag && plain.HandledCount == 2, "actual replay flag and work budget are reset");

        // C30-02: an advisory refusal cannot escalate backpressure into
        // FailMapLoading, nor block the valid message of player B.
        var congested = HostWithReplay();
        congested.Network.SendFailure = (peer, kind) =>
            peer == "peer-a" && kind == "error" ? new IOException("TCP queue full") : null;
        congested.Tick(0);
        Check(!congested.Failed && congested.Received.Count == 1 &&
            congested.Received[0] == "peer-b:valid",
            "map replay continues after rejected player's error reply failed to enqueue");
        Check(congested.Network.Sent.Count == 0 &&
            congested.Network.Disconnected.Count == 1 &&
            congested.Quarantined("peer-a") && !congested.Quarantined("peer-b"),
            "failed TCP refusal attempts only offending peer's disconnect and quarantines it");
        Check(congested.Pending == 0 && congested.PendingBytes == 0,
            "failed advisory send doesn't requeue the rejected business message");

        // The main network receive path already ignores quarantined peers,
        // but replay has its OWN FIFO that may contain later packets from A.
        // None may acquire authority after the failed refusal/eviction.
        var queuedAfterReject = new MultiplayerSession();
        queuedAfterReject.Bind("peer-a", "player-a");
        queuedAfterReject.Bind("peer-b", "player-b");
        queuedAfterReject.NativeBarrier(false);
        Check(queuedAfterReject.Queue("peer-a", "reject"), "queued rejected command");
        Check(queuedAfterReject.Queue("peer-a", "would-change-assets"),
            "later command from offending peer still waiting behind refusal");
        Check(queuedAfterReject.Queue("peer-b", "valid"), "healthy B stays behind offending A in FIFO");
        queuedAfterReject.Network.SendFailure = (peer, kind) => peer == "peer-a"
            ? new IOException("outbound refusal queue full") : null;
        queuedAfterReject.Ready();
        queuedAfterReject.Tick(0);
        Check(!queuedAfterReject.Failed && queuedAfterReject.Pending == 0 &&
            queuedAfterReject.Received.Count == 1 && queuedAfterReject.Received[0] == "peer-b:valid",
            "already-deferred messages from quarantined A cannot bypass rejected peer isolation");
        Check(queuedAfterReject.Quarantined("peer-a") &&
            queuedAfterReject.HandledCount == 3, "skipped A still consumes exactly its FIFO work slot");

        var ordinaryAfterReject = new MultiplayerSession();
        ordinaryAfterReject.Bind("peer-a", "player-a");
        ordinaryAfterReject.NativeBarrier(false);
        Check(ordinaryAfterReject.Queue("peer-a", "reject"), "healthy refusal test queued");
        Check(ordinaryAfterReject.Queue("peer-a", "valid"), "healthy follow-up queued");
        ordinaryAfterReject.Ready();
        ordinaryAfterReject.Tick(0);
        Check(!ordinaryAfterReject.Quarantined("peer-a") &&
            ordinaryAfterReject.Received.Count == 1 &&
            ordinaryAfterReject.Received[0] == "peer-a:valid",
            "a successfully notified rejection does not quarantine otherwise healthy player A");

        // C30-03: the other supported transport enqueue exception has the
        // same behavior; unknown encoding failures quarantine separately.
        var missing = HostWithReplay();
        missing.Network.SendFailure = (peer, kind) => new InvalidOperationException("peer not connected");
        missing.Tick(0);
        Check(!missing.Failed && missing.Received.Count == 1 &&
            missing.Quarantined("peer-a"), "closed/unknown peer's refusal cannot lock the host");

        var unexpected = HostWithReplay();
        unexpected.Network.SendFailure = (peer, kind) => new FormatException("invalid optional error envelope");
        unexpected.Tick(0);
        Check(!unexpected.Failed && unexpected.Quarantined("peer-a") &&
            unexpected.Received.Count == 1, "unexpected failure in optional notice quarantines only that peer");

        // C30-04: an advisory send and several disconnections fail.
        // Quarantine retains the peer until a real terminal callback arrives.
        var repeat = HostWithReplay();
        repeat.Network.SendFailure = (peer, kind) => new IOException("queue permanently full");
        repeat.Network.DisconnectFailures = 3;
        repeat.Network.OnDisconnect = (peer, reason) => repeat.ForgetPeer(peer);
        repeat.Tick(0);
        Check(!repeat.Failed && repeat.Quarantined("peer-a") && repeat.Received.Count == 1,
            "disconnect failures do not stop a good player's queued message");
        Check(repeat.Network.Disconnected.Count == 1,
            "failed advisory send caused one immediate disconnection attempt");
        repeat.EvictionTick(1.999);
        Check(repeat.Network.Disconnected.Count == 1, "no unbounded per-frame retry storm");
        repeat.EvictionTick(0.001);
        Check(repeat.Network.Disconnected.Count == 2 && repeat.Quarantined("peer-a"),
            "eviction retries at the expected two-second deadline");
        repeat.EvictionTick(2);
        repeat.EvictionTick(2);
        Check(repeat.Network.Disconnected.Count == 4 &&
            !repeat.Quarantined("peer-a") && repeat.Peers.ContainsKey("peer-b"),
            "real disconnect callback ends quarantine after repeated transport errors");

        // C30-05: status UI failures are optional observers too; business
        // failures remain recorded and never turn into committed messages.
        var uiFailure = HostWithReplay();
        uiFailure.StatusFails();
        uiFailure.Network.SendFailure = (peer, kind) => new IOException("queue full");
        uiFailure.Tick(0);
        Check(!uiFailure.Failed && uiFailure.Received.Count == 1,
            "UI StatusChanged failure during normal rejection can't poison replay");

        // C30-06: native/map/personal failures do NOT get swallowed by the
        // optional error reporter. The real FailMapLoading is exercised.
        var fatal = HostWithReplay("native-fault");
        fatal.Tick(0);
        Check(fatal.Failed && fatal.Pending == 1 &&
            fatal.Received.Count == 0 && fatal.Network.Sent.Count == 0,
            "actual native/map failure locks loading and preserves later unapplied transactions");
        fatal.Tick(0);
        Check(fatal.Pending == 1 && fatal.HandledCount == 1,
            "once failed, actual map replay cannot silently resume or ACK later work");

        // C30-07: a guest doesn't claim host authority or send a refusal.
        var guest = new MultiplayerSession();
        guest.Guest();
        guest.NativeBarrier(false);
        Check(guest.Queue(null, "reject"), "guest network message queued");
        guest.Ready();
        guest.Tick(0);
        Check(!guest.Failed && guest.Network.Sent.Count == 0 && guest.Pending == 0,
            "a local guest rejection never creates host-origin error messages");

        var overflow = new MultiplayerSession();
        overflow.Bind("peer-a", "player-a");
        overflow.NativeBarrier(false);
        for (int i = 0; i < 128; i++)
            Check(overflow.Queue("peer-a", "valid"), "valid queue item within 128 item budget");
        bool capacity = false;
        try { overflow.Queue("peer-a", "valid"); }
        catch (MessageRejectionException) { capacity = true; }
        Check(capacity && overflow.Failed && overflow.Pending == 128,
            "real map queue capacity violation still triggers fail-closed");

        var timeout = new MultiplayerSession();
        timeout.NativeBarrier(false);
        timeout.Tick(60);
        Check(timeout.Failed, "native map initialization's 60-second deadline stays fail-closed");

        // C30-08: synchronous transport disconnect and a reused temporary
        // peer identifier must not re-add the OLD quarantine after the event.
        var reused = HostWithReplay();
        reused.Network.SendFailure = (peer, kind) => new IOException("queue full");
        reused.Network.OnDisconnect = (peer, reason) =>
        {
            reused.ForgetPeer(peer);
            reused.Bind(peer, "new-player");
        };
        reused.Tick(0);
        Check(!reused.Quarantined("peer-a") &&
            reused.Peers["peer-a"] == "new-player" && reused.Received.Count == 1,
            "synchronous disconnect removes stale quarantine before same peer id reconnect");
        // Verify that identity changed BEFORE replay rather than during send.
        var remapped = HostWithReplay();
        remapped.Bind("peer-a", "new-player");
        remapped.Tick(0);
        Check(remapped.Network.Sent.Count == 0 &&
            remapped.Network.Disconnected.Count == 0 &&
            remapped.Received.Count == 1, "stale queued peer-player mapping never sends notice to a new player");

        // Stress the exact production replay method in deterministic order:
        // no repeated refusal, no duplicated good message, and no load lock.
        var random = new Random(30030);
        for (int i = 0; i < 12000; i++)
        {
            var session = HostWithReplay();
            int failure = random.Next(3);
            if (failure != 0)
                session.Network.SendFailure = (peer, kind) => failure == 1
                    ? (Exception)new IOException("synthetic congestion")
                    : new InvalidOperationException("synthetic disconnect");
            session.Tick(0);
            Check(!session.Failed && session.Received.Count == 1 &&
                session.Received[0] == "peer-b:valid" && session.Pending == 0,
                "randomized replay never loses a healthy ordered message to advisory failure");
            Check(session.Network.Sent.Count + session.Network.Disconnected.Count == 1,
                "randomized replay performs exactly one advisory send or disconnect decision");
        }

        Console.WriteLine("PASS: " + checks +
            " production MapViewLoading replay / OptionalPeerNotice / RejectedPeerEvictionGate checks");
    }
}
