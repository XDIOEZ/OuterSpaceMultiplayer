using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Basics()
    {
        var gate = new UnauthenticatedPeerDeadline();
        Check(!gate.Begin(null) && !gate.Begin(""), "invalid peers never reserve a transport slot");
        Check(gate.Begin("peerA") && gate.Count == 1, "first connected peer is tracked");
        Check(gate.Begin("peerA") && gate.Count == 1, "duplicate event does not allocate another slot");
        Check(!gate.IsExpired("peerA"), "newly connected peer is within deadline");
        int kicked = 0;
        gate.Tick(15, peer => kicked++);
        Check(!gate.IsExpired("peerA") && kicked == 0, "valid slow handshakes have room before 30 seconds");
        gate.Begin("peerA"); // This must not extend the original lifetime.
        gate.Tick(14.99, peer => kicked++);
        Check(!gate.IsExpired("peerA") && kicked == 0, "29.99s handshake still allowed");
        gate.Tick(0.01, peer =>
        {
            Check(peer == "peerA", "original ID at timeout");
            kicked++;
            gate.Forget(peer); // Simulate the transport's Disconnected event.
        });
        Check(kicked == 1 && gate.Count == 0, "absolute thirty-second timeout closes a silent connection");
        Check(!gate.IsExpired("peerA"), "disconnected peer cannot retain old expired state");
        Check(gate.Begin("peerA"), "new transport generation can use same peer ID");
        gate.Complete("peerA");
        Check(gate.Count == 0, "confirmed authentication frees pending slot");
        gate.Tick(100, peer => kicked++);
        Check(kicked == 1, "authenticated connection never times out under the unauthenticated guard");
        gate.Forget("unknown");
        gate.Clear();
        Check(gate.Count == 0, "clearing a disconnected session is idempotent");
        Check(Throws<ArgumentOutOfRangeException>(() => gate.Tick(-0.1, peer => {})),
            "negative unscaled time rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => gate.Tick(double.NaN, peer => {})),
            "NaN time rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => gate.Tick(double.PositiveInfinity, peer => {})),
            "infinite time rejected");
        Check(Throws<ArgumentNullException>(() => gate.Tick(0, null)),
            "empty disconnect callback rejected");

        var caseSensitive = new UnauthenticatedPeerDeadline();
        Check(caseSensitive.Begin("Guest") && caseSensitive.Begin("guest")
            && caseSensitive.Count == 2, "peer IDs are compared with Ordinal semantics");
        caseSensitive.Forget("Guest");
        Check(caseSensitive.Count == 1, "disconnect isolates only the exact peer ID");
    }

    private static void HostIntegration()
    {
        var host = new MultiplayerSession();
        host.Connect("silent");
        host.Challenge("silent");
        host.Reject("silent");
        host.Tick(29.9);
        Check(host.PendingCount == 1 && host.TestTransport.Disconnected.Count == 0,
            "real linked session wrapper preserves valid slow handshake");
        host.Connect("silent"); // Network duplicate must not extend expiration.
        host.Tick(0.1);
        Check(host.TestTransport.Disconnected.Count == 1
            && host.TestTransport.Disconnected[0] == "silent"
            && !host.HasChallenge("silent"),
            "timeout actually closes transport and clears stale authentication nonce");
        Check(!host.TestTransport.Active.Contains("silent") && host.PendingCount == 0,
            "expired peer no longer consumes either transport or session slot");

        host.Connect("valid");
        host.Tick(25);
        host.Authenticate("valid");
        host.Tick(100);
        Check(host.TestTransport.Active.Contains("valid")
            && host.TestTransport.Disconnected.Count == 1 && host.PendingCount == 0,
            "welcome/authenticated connection is unaffected by future unscaled ticks");

        host.Connect("closed");
        host.Challenge("closed");
        host.PeerLeft("closed");
        host.Tick(31);
        Check(host.TestTransport.Active.Contains("closed") && host.PendingCount == 0,
            "transport Disconnected event forgets timer before next Tick");

        var client = new MultiplayerSession();
        client.SetHost(false);
        client.Connect("host");
        client.Tick(60);
        Check(client.TestTransport.Active.Contains("host")
            && client.TestTransport.Disconnected.Count == 0,
            "guest never kicks its authoritative host from guest-side ticks");

        // TCP/Steam may enqueue Disconnected for the next session poll. Even
        // if the transport requested/started close, host authentication stays
        // expired until that event is actually delivered.
        var delayedHost = new MultiplayerSession();
        delayedHost.TestTransport.AutomaticallyDeliverDisconnected = false;
        delayedHost.Connect("async");
        delayedHost.Challenge("async");
        delayedHost.Tick(30);
        Check(delayedHost.PendingCount == 1 && delayedHost.Expired("async")
            && delayedHost.TestTransport.Disconnected.Count == 1 && !delayedHost.HasChallenge("async"),
            "production host Tick retains expired peer while its transport close event is pending");
        delayedHost.Connect("async");
        Check(delayedHost.Expired("async") && delayedHost.PendingCount == 1,
            "another Connected notification cannot reset the expired handshake");
        delayedHost.Tick(1.99);
        Check(delayedHost.TestTransport.Disconnected.Count == 1,
            "session does not retry a successful close request on every Tick");
        delayedHost.Tick(.01);
        Check(delayedHost.TestTransport.Disconnected.Count == 2
            && delayedHost.PendingCount == 1 && delayedHost.Expired("async"),
            "missing close receipt is retried without admitting an expired peer");
        delayedHost.PeerLeft("async");
        Check(delayedHost.PendingCount == 0 && !delayedHost.Expired("async"),
            "confirmed transport Disconnected event releases the pending admission slot");
    }

    private static void CapacityAndRecovery()
    {
        var room = new MultiplayerSession();
        const int cap = UnauthenticatedPeerDeadline.MaxPendingPeers;
        for (int index = 0; index < cap; index++)
            Check(room.Connect("p" + index) == index + 1,
                "connected-but-unauthed peer count bounded at " + index);
        Check(room.Connect("overflow") == cap
            && room.TestTransport.Disconnected.Contains("overflow")
            && !room.TestTransport.Active.Contains("overflow"),
            "extra unauthenticated transport peer refused at cap");
        room.Authenticate("p4");
        Check(room.Connect("replacement") == cap,
            "validated handshakes free exactly one slot for a new player");
        room.Tick(30);
        Check(room.TestTransport.Disconnected.Count == cap + 1,
            "all remaining unverified connections close after original deadlines");
        Check(room.TestTransport.Active.Contains("p4")
            && !room.TestTransport.Active.Contains("replacement") && room.PendingCount == 0,
            "only explicitly authenticated peer survives a massive idle handshake flood");
    }

    private static void DisconnectionFaults()
    {
        // A successful Disconnect request is not proof that the queued
        // Disconnected event has been processed. Until that event, the peer
        // must stay expired and must not free an admission slot.
        var delayed = new UnauthenticatedPeerDeadline();
        Check(delayed.Begin("delayed"), "delayed disconnect setup");
        int attempts = 0;
        delayed.Tick(30, _ => attempts++);
        Check(attempts == 1 && delayed.Count == 1 && delayed.IsExpired("delayed"),
            "successfully requested disconnect stays expired until actual Disconnected event");
        Check(delayed.Begin("delayed") && delayed.Count == 1 && delayed.IsExpired("delayed"),
            "duplicate Connected cannot revive a still-open expired socket");
        delayed.Tick(1.99, _ => attempts++);
        Check(attempts == 1, "unconfirmed successful disconnect respects retry cooldown");
        delayed.Tick(.01, _ => attempts++);
        Check(attempts == 2 && delayed.IsExpired("delayed"),
            "unconfirmed disconnect is retried after two seconds without permitting auth");
        delayed.Forget("delayed");
        Check(delayed.Count == 0 && !delayed.IsExpired("delayed"),
            "only confirmed Disconnected frees an expired socket's slot");
        Check(delayed.Begin("delayed") && !delayed.IsExpired("delayed"),
            "subsequent new transport generation receives a fresh handshake deadline");

        // A transport implementation may synchronously reenter the session
        // while its Disconnect method is still running. Even a nested Tick
        // advancing more than the retry interval cannot close the same lease
        // twice in one call stack.
        var nested = new UnauthenticatedPeerDeadline();
        nested.Begin("reentrant");
        int nestedCloseCalls = 0;
        nested.Tick(30, peer =>
        {
            nestedCloseCalls++;
            nested.Tick(3, _ => nestedCloseCalls++);
        });
        Check(nestedCloseCalls == 1 && nested.IsExpired("reentrant") && nested.Count == 1,
            "nested transport Tick cannot duplicate an in-progress disconnect");
        nested.Forget("reentrant");

        // Admission capacity cannot be recycled by fake success receipts.
        var capacity = new UnauthenticatedPeerDeadline();
        for (int i = 0; i < UnauthenticatedPeerDeadline.MaxPendingPeers; i++)
            Check(capacity.Begin("held" + i), "setup expired capacity " + i);
        capacity.Tick(30, _ => { });
        Check(!capacity.Begin("new") && capacity.Count == UnauthenticatedPeerDeadline.MaxPendingPeers,
            "unconfirmed disconnects cannot release all 32 unauthenticated admission slots");
        capacity.Forget("held0");
        Check(capacity.Begin("new") && !capacity.IsExpired("new"),
            "actual Disconnected event releases exactly one slot for a new handshake");

        var host = new MultiplayerSession();
        host.Connect("congested");
        host.Challenge("congested");
        host.TestTransport.Failures["congested"] = 1;
        host.Tick(30);
        Check(host.PendingCount == 1 && host.Expired("congested")
            && host.TestTransport.Disconnected.Count == 0,
            "disconnection IO failure is not falsely counted as a released transport");
        Check(!host.HasChallenge("congested") && host.TestErrors.Count == 1,
            "expired challenge is invalidated and the failed eviction is reported");
        host.Tick(1.99);
        Check(host.PendingCount == 1 && host.TestErrors.Count == 1,
            "repeated disconnect errors are throttled instead of logging every Tick");
        host.Tick(.01);
        Check(host.PendingCount == 0 && host.TestTransport.Disconnected.Count == 1,
            "expired peer is retried after two seconds and finally evicted");

        var guard = new UnauthenticatedPeerDeadline();
        guard.Begin("A"); guard.Begin("B");
        int closes = 0;
        Check(Throws<FormatException>(() => guard.Tick(30, id =>
        {
            if (id == "A") throw new FormatException("unexpected transport bug");
            closes++;
        })), "unexpected programming errors are not silently swallowed");
        Check(guard.Count == 2, "unexpected disconnect fault cannot fake successful cleanup");
        guard.Tick(0, id => { closes++; guard.Forget(id); });
        Check(closes == 2 && guard.Count == 0, "subsequent retry can release both peers");

        var reporting = new UnauthenticatedPeerDeadline();
        reporting.Begin("one");
        reporting.Begin("two");
        int attempted = 0;
        reporting.Tick(30, id =>
        {
            attempted++;
            throw new IOException("simulated failing disconnect");
        }, (id, error) => throw new InvalidOperationException("broken optional logger"));
        Check(attempted == 2 && reporting.Count == 2
            && reporting.IsExpired("one") && reporting.IsExpired("two"),
            "faulty diagnostic observer cannot prevent retrying another expired peer");
    }

    private static void ReentrantAndRandom()
    {
        var gate = new UnauthenticatedPeerDeadline();
        gate.Begin("old");
        gate.Begin("other");
        gate.Tick(30, peer =>
        {
            if (peer != "old") return;
            gate.Forget("old");
            gate.Begin("old");
            gate.Forget("other");
        });
        Check(gate.Count == 1 && !gate.IsExpired("old"),
            "synchronous transport callback cannot delete a newer same-ID connection");
        gate.Tick(29, peer => {});
        Check(!gate.IsExpired("old"), "new generation retains a full independent handshake lifetime");
        gate.Tick(1, peer => gate.Forget(peer));
        Check(gate.Count == 0, "new generation eventually expires normally");

        var random = new Random(20261018);
        for (int round = 0; round < 15000; round++)
        {
            var pending = new UnauthenticatedPeerDeadline();
            int count = random.Next(1, 34);
            for (int n = 0; n < count; n++) pending.Begin("peer" + n);
            double elapsed = random.NextDouble() * 29.5;
            pending.Tick(elapsed, peer => throw new InvalidOperationException("early close"));
            int complete = random.Next(0, count);
            pending.Complete("peer" + complete);
            int closed = 0;
            pending.Tick(31 - elapsed, peer => { closed++; pending.Forget(peer); });
            Check(closed == Math.Min(count, UnauthenticatedPeerDeadline.MaxPendingPeers)
                - (complete < UnauthenticatedPeerDeadline.MaxPendingPeers ? 1 : 0),
                "random legitimate completion never falls into expired-peer eviction " + round);
            Check(pending.Count == 0, "random timeout releases all remaining tracked unauthenticated slots " + round);
        }
    }

    private static int Main()
    {
        try
        {
            Basics();
            HostIntegration();
            CapacityAndRecovery();
            DisconnectionFaults();
            ReentrantAndRandom();
            Console.WriteLine("PASS: " + checks + " linked unauthenticated peer deadline checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
