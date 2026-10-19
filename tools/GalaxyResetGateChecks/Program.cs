using System;
using System.Collections.Generic;
using System.Reflection;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;

    private static void Check(bool result, string label)
    {
        if (!result) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }

    private static bool Throws<T>(Action callback) where T : Exception
    {
        try { callback(); return false; }
        catch (T) { return true; }
    }

    private static void BasicRecovery()
    {
        var gate = new GalaxyResetRequestGate();
        var resetOrder = new List<string>();
        Action<string> reset = player => resetOrder.Add(player);
        Check(Throws<ArgumentException>(() => gate.RequestReset("", reset)), "empty player identity rejected");
        Check(Throws<ArgumentNullException>(() => gate.RequestReset("A", null)), "null native reset rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => gate.Advance(-1, reset)), "negative delta rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => gate.Advance(double.NaN, reset)), "NaN delta rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => gate.Advance(double.PositiveInfinity, reset)),
            "infinite delta rejected");
        Check(Throws<ArgumentNullException>(() => gate.Advance(0, null)), "missing native Tick callback rejected");

        Check(gate.RequestReset("A", reset) && resetOrder.Count == 1 && resetOrder[0] == "A",
            "first authenticated reset immediately invalidates only the requester");
        for (int i = 0; i < 100; i++)
            Check(!gate.RequestReset("A", reset) && resetOrder.Count == 1,
                "repeat during in-flight full is coalesced " + i);
        Check(gate.WaitingForFull("A") && !gate.HasDeferredReset("A"),
            "first full baseline awaiting transport enqueue");
        gate.Advance(15, reset);
        Check(resetOrder.Count == 1, "lost enqueue does not discard/reset baseline again on timer");
        gate.FullEnqueued("A");
        Check(!gate.WaitingForFull("A"), "only completed full enqueue clears the in-flight marker");
        Check(gate.RequestReset("A", reset) && resetOrder.Count == 2,
            "client that rejects a later full can retry after cooldown expires");
        gate.FullEnqueued("A");

        // Fresh state: repeated request after first successful full queues
        // just one later attempt, even if the client sends 100 duplicates.
        var retry = new GalaxyResetRequestGate();
        int calls = 0;
        Action<string> one = player => { Check(player == "victim", "only requested player is reset"); calls++; };
        Check(retry.RequestReset("victim", one), "initial request accepted");
        retry.FullEnqueued("victim");
        for (int i = 0; i < 100; i++)
            Check(!retry.RequestReset("victim", one) && calls == 1,
                "cooldown coalesces repeated reset " + i);
        Check(retry.HasDeferredReset("victim"), "first rejected full schedules automatic retry");
        retry.Advance(1.99, one);
        Check(calls == 1, "initial two-second cooldown is respected");
        retry.Advance(0.02, one);
        Check(calls == 2 && retry.WaitingForFull("victim") && !retry.HasDeferredReset("victim"),
            "queued request is sent without another client frame");
        retry.FullEnqueued("victim");
        Check(!retry.RequestReset("victim", one), "second failure queues a longer retry");
        retry.Advance(3.99, one);
        Check(calls == 2, "second backoff lasts four seconds");
        retry.Advance(0.02, one);
        Check(calls == 3, "retry automatically issued after increasing backoff");
        retry.FullEnqueued("victim");
        retry.RequestReset("victim", one);
        retry.Advance(7.99, one);
        Check(calls == 3, "third attempt uses eight-second cooldown");
        retry.Advance(0.02, one);
        Check(calls == 4, "third retry queued, eventually performed");
        retry.FullEnqueued("victim");
        retry.RequestReset("victim", one);
        retry.Advance(15.99, one);
        Check(calls == 4, "burst backoff is bounded by 16 seconds");
        retry.Advance(0.02, one);
        Check(calls == 5, "at most one reset after 16-second cooldown");
        retry.FullEnqueued("victim");
        retry.Advance(31, one);
        Check(retry.RequestReset("victim", one), "single reset after quiet period succeeds");
        retry.FullEnqueued("victim");
        retry.RequestReset("victim", one);
        retry.Advance(1.99, one);
        Check(calls == 6, "quiet interval restarts the short two-second cooldown");
        retry.Advance(0.02, one);
        Check(calls == 7, "quiet period reset uses initial two-second spacing");

        retry.ForgetPlayer("victim");
        Check(!retry.WaitingForFull("victim") && !retry.HasDeferredReset("victim"),
            "disconnect cancels outstanding player-scoped work");
        Check(retry.RequestReset("victim", one) && calls == 8,
            "reconnected identity starts fresh, without inherited cooldown");
    }

    private static void FaultAndIsolation()
    {
        var gate = new GalaxyResetRequestGate();
        int a = 0, b = 0;
        bool fail = true;
        Action<string> reset = id =>
        {
            if (id == "A") { a++; if (fail) throw new InvalidOperationException("native error"); }
            if (id == "B") b++;
        };
        Check(Throws<InvalidOperationException>(() => gate.RequestReset("A", reset)),
            "native bridge reset failure still propagates");
        Check(gate.HasDeferredReset("A") && !gate.WaitingForFull("A"),
            "faulty native reset stays queued, not falsely marked sent");
        Check(gate.RequestReset("B", reset) && b == 1,
            "another client can reset while first client's game call fails");
        fail = false;
        gate.Advance(.5, reset);
        Check(a == 2 && b == 1 && gate.WaitingForFull("A"),
            "next host broadcast safely retries failed native operation for A only");
        gate.FullEnqueued("A");
        gate.FullEnqueued("B");
        for (int i = 0; i < 1000; i++)
            Check(!gate.RequestReset("A", reset) || a <= 2, "early repeat cannot force full flood");
        Check(b == 1, "spam from A does not reset the other client's baseline");
        gate.ForgetPlayer("A");
        gate.Advance(10, reset);
        Check(a == 2 && b == 1, "disconnect discards deferred full re-sync of A");
        Check(!gate.HasDeferredReset("A"), "no stale task after peer eviction");
    }

    private static void TickReentrancy()
    {
        // A native bridge callback can synchronously evict a disconnected
        // player and add a new peer. The host Tick must not enumerate the
        // live dictionary while invoking user/native callbacks.
        var gate = new GalaxyResetRequestGate();
        Action<string> initial = _ => { };
        foreach (string player in new[] { "A", "B" })
        {
            Check(gate.RequestReset(player, initial), "setup first full for " + player);
            gate.FullEnqueued(player);
            Check(!gate.RequestReset(player, initial) && gate.HasDeferredReset(player),
                "setup deferred full for " + player);
        }
        int nativeResets = 0, newPeerResets = 0;
        string evicted = null;
        gate.Advance(2.1, player =>
        {
            nativeResets++;
            evicted = player;
            gate.ForgetPlayer("A");
            gate.ForgetPlayer("B");
            Check(gate.RequestReset("C", _ => newPeerResets++),
                "newly admitted client has its own first baseline");
        });
        Check(nativeResets == 1 && newPeerResets == 1
            && !gate.WaitingForFull("A") && !gate.WaitingForFull("B")
            && !gate.HasDeferredReset("A") && !gate.HasDeferredReset("B")
            && gate.WaitingForFull("C"),
            "disconnect during Tick does not visit evicted stale entries or corrupt new peer");
        Check(evicted == "A" || evicted == "B", "only originally due player reset");

        // The same callback may enter another room-clock Tick. The original
        // grant must be in-progress before invoking native code, or both
        // stacks would issue the same reset.
        var nested = new GalaxyResetRequestGate();
        Check(nested.RequestReset("reentrant", initial), "nested setup first full");
        nested.FullEnqueued("reentrant");
        Check(!nested.RequestReset("reentrant", initial), "nested setup deferred reset");
        int called = 0;
        nested.Advance(2, id =>
        {
            called++;
            nested.Advance(0, _ => called++);
            Check(!nested.RequestReset(id, _ => called++),
                "request during native in-flight reset stays coalesced");
        });
        Check(called == 1 && nested.WaitingForFull("reentrant")
            && !nested.HasDeferredReset("reentrant"),
            "nested Advance does not issue a duplicate native reset");

        // If a synchronous disconnect replaces the SAME player key, an old
        // callback must not mark that abandoned request successfully issued.
        var replacement = new GalaxyResetRequestGate();
        int reconnectResets = 0;
        bool oldAccepted = replacement.RequestReset("same", player =>
        {
            replacement.ForgetPlayer(player);
            Check(replacement.RequestReset(player, _ => reconnectResets++),
                "reconnected player gets a fresh reset state");
        });
        Check(!oldAccepted && reconnectResets == 1 && replacement.WaitingForFull("same"),
            "removed request cannot confirm over its replacement");

        // Idle and deferred (not yet due) host Ticks remain allocation-free.
        // Allocating a due-only snapshot is acceptable only at the actual
        // reset boundary, never on every broadcast frame.
        var idle = new GalaxyResetRequestGate();
        Action<string> noop = _ => { };
        for (int i = 0; i < 32; i++) idle.Advance(0, noop);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) idle.Advance(0, noop);
        Check(GC.GetAllocatedBytesForCurrentThread() == before,
            "empty room clock Ticks do not allocate reset snapshots");
        Check(idle.RequestReset("waiting", noop), "deferred allocation setup");
        idle.FullEnqueued("waiting");
        Check(!idle.RequestReset("waiting", noop), "deferred reset held by cooldown");
        for (int i = 0; i < 32; i++) idle.Advance(0, noop);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) idle.Advance(0, noop);
        Check(GC.GetAllocatedBytesForCurrentThread() == before && idle.HasDeferredReset("waiting"),
            "deferred reset does not allocate before its cooldown expires");
    }

    private static void RealSessionFullEnqueue()
    {
        var room = new MultiplayerSession();
        int deliveries = 0;
        room.AskReset("alice");
        Check(room.TestBridge.ResetCalls == 1 && room.TestGate.WaitingForFull("alice"),
            "real host dispatch gate reserves a full baseline for Alice");
        room.OnSend = (peer, kind, frame) =>
        {
            Check(peer == "alice-peer" && kind == "galaxy" && frame.IsFullBaseline,
                "real production host send method retains peer and full frame");
            throw new System.IO.IOException("queue full");
        };
        Check(Throws<System.IO.IOException>(() => room.SendGalaxy("alice-peer", "alice")),
            "transport enqueue failure bubbles up rather than confirming snapshot");
        Check(room.TestGate.WaitingForFull("alice") && room.TestBridge.ConfirmCalls == 0,
            "failed send cannot satisfy host re-sync completion watermark");
        room.OnSend = (peer, kind, frame) =>
        {
            Check(frame.IsFullBaseline, "retained uncommitted full snapshot is resent");
            deliveries++;
        };
        room.SendGalaxy("alice-peer", "alice");
        Check(deliveries == 1 && room.TestBridge.ConfirmCalls == 1
            && !room.TestGate.WaitingForFull("alice"),
            "full enqueue AND native outgoing-baseline confirm are both required");
        room.OnSend = (peer, kind, frame) =>
        {
            Check(!frame.IsFullBaseline, "successful full switches the next frame back to delta");
            deliveries++;
        };
        room.SendGalaxy("alice-peer", "alice");
        Check(room.TestBridge.ConfirmCalls == 2 && deliveries == 2
            && !room.TestBridge.NeedFull.Contains("alice"),
            "successful full establishes delta baseline for following frames");

        // Battle entry uses the same production method; a native error after
        // transport enqueue must not falsely complete the request gate.
        room.AskReset("bob");
        room.TestBridge.FailConfirmation = true;
        room.OnSend = (peer, kind, frame) =>
            Check(frame.IsFullBaseline, "battle-host send queues full baseline before confirm");
        Check(Throws<InvalidOperationException>(() => room.SendGalaxy("bob-peer", "bob")),
            "bridge confirmation failure propagates for host recovery");
        Check(room.TestGate.WaitingForFull("bob") && room.TestBridge.NeedFull.Contains("bob"),
            "bridge failure retains the exact full outgoing snapshot for retry");
        room.TestBridge.FailConfirmation = false;
        room.SendGalaxy("bob-peer", "bob");
        Check(!room.TestGate.WaitingForFull("bob"),
            "subsequent successful full send clears only Bob's outstanding reset");
        Check(!room.TestGate.WaitingForFull("alice"),
            "completing Bob's frame never modifies Alice's gate state");
    }

    private sealed class SyntheticHost
    {
        internal readonly GalaxyResetRequestGate Gate = new GalaxyResetRequestGate();
        internal int Resets, FullFrames, DeltaFrames, HealthyDeltaFrames;
        internal long Bytes, HealthyBytes;

        internal void IncomingReset(string id)
        {
            Gate.RequestReset(id, who =>
            {
                Check(who == "noisy", "host only resets the requesting player in synthetic test");
                Resets++;
            });
        }

        internal void Step(double elapsed)
        {
            Gate.Advance(elapsed, who => { Resets++; });
            // A full frame is needed after the gate resets only the client's
            // previous reliable outgoing baseline. A delta is otherwise used.
            bool full = Gate.WaitingForFull("noisy");
            if (full) { FullFrames++; Bytes += 131072; Gate.FullEnqueued("noisy"); }
            else { DeltaFrames++; Bytes += 512; }
            // This second client never requested re-sync. Its shared cached
            // sample must not be invalidated by another player's traffic.
            HealthyDeltaFrames++; HealthyBytes += 512;
        }
    }

    private static void QuantitativeBursts()
    {
        // The old switch called ResetGalaxyRecipient on every request. With
        // an attacker issuing 100 resets over 10 seconds and a 0.5s sample
        // period, all 20 frames could be forced to full. Sizes below are
        // synthetic payload assumptions, not measurements from Unity.
        foreach (int messages in new[] { 1, 10, 100 })
        {
            var test = new SyntheticHost();
            int accepted = 0;
            for (int tick = 0; tick < 20; tick++)
            {
                int due = ((tick + 1) * messages / 20) - (tick * messages / 20);
                for (int i = 0; i < due; i++)
                { test.IncomingReset("noisy"); accepted++; }
                test.Step(.5);
            }
            Check(accepted == messages, "all synthetic requests were actually received");
            Check(test.FullFrames <= (messages == 1 ? 1 : 3),
                "10-second full-frame count remains bounded under " + messages + " reset packets");
            Check(test.FullFrames + test.DeltaFrames == 20, "exactly 20 galaxy frames in ten seconds");
            Check(test.HealthyDeltaFrames == 20
                && test.HealthyBytes == 20 * 512,
                "noisy player cannot force full frames for the healthy player");
            if (messages == 100)
                Check(test.Bytes < 20L * 131072 / 4,
                    "100 abusive resets consume less than a quarter of old repeated-full traffic");
            Console.WriteLine("INFO: 10s, " + messages + " reset requests: noisy full=" + test.FullFrames
                + ", delta=" + test.DeltaFrames + ", synthetic bytes=" + test.Bytes
                + "; healthy full=0, delta=20, bytes=" + test.HealthyBytes);
        }
    }

    private static void RandomInterleavings()
    {
        var random = new Random(20261018);
        for (int round = 0; round < 15000; round++)
        {
            var gate = new GalaxyResetRequestGate();
            int invoked = 0;
            Action<string> reset = who =>
            {
                Check(who == "guest", "random guard always targets the correct guest");
                invoked++;
            };
            int requests = random.Next(1, 80);
            for (int i = 0; i < requests; i++)
            {
                gate.RequestReset("guest", reset);
                if (random.Next(3) == 0) gate.FullEnqueued("guest");
                gate.Advance(random.NextDouble() * .35, reset);
            }
            Check(invoked <= 1 + (int)(requests * .35 / 2) + 1,
                "bounded native resets in a random high-rate request stream");
            gate.ForgetPlayer("guest");
            Check(!gate.HasDeferredReset("guest") && !gate.WaitingForFull("guest"),
                "random peer close releases both pending state flags");
        }
    }

    private static int Main()
    {
        try
        {
            FieldInfo field = typeof(MultiplayerSession).GetField("galaxyResetRequests",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Check(field != null && field.FieldType == typeof(GalaxyResetRequestGate),
                "actual production session is wired to the gate instance");
            BasicRecovery();
            FaultAndIsolation();
            TickReentrancy();
            RealSessionFullEnqueue();
            QuantitativeBursts();
            RandomInterleavings();
            Console.WriteLine("PASS: " + checks + " linked galaxy reset gate checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
