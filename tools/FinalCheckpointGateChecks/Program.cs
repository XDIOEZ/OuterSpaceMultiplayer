using System;
using System.Collections.Generic;
using FreeWorldMultiplayer;

internal static class Program
{
    // Test-only fake native map/personal capture + host transport ACK channel.
    // The callback wiring mirrors the real finalCheckpointRequested branch.
    private sealed class Guest
    {
        internal bool Requested = true;
        internal int MapInFlight, PersonalInFlight, QueuedCommands;
        internal bool NewMap, NewPersonal, NewCommand;
        internal bool FailMap, FailPersonal, FailSend, FailPreflight;
        internal int MapsPublished, PersonalPublished, FinalDoneSent;
        internal readonly List<string> Events = new List<string>();

        internal bool Outstanding()
        {
            Events.Add("check");
            if (FailPreflight) throw new InvalidOperationException("inbox fault");
            return MapInFlight != 0 || PersonalInFlight != 0 || QueuedCommands != 0;
        }
        internal void PublishMaps()
        {
            Events.Add("map");
            MapsPublished++;
            if (NewMap) MapInFlight++;
            if (NewCommand) QueuedCommands++;
            if (FailMap) throw new InvalidOperationException("map capture failed");
        }
        internal void PublishPersonal()
        {
            Events.Add("personal");
            PersonalPublished++;
            if (NewPersonal) PersonalInFlight++;
            if (FailPersonal) throw new InvalidOperationException("personal capture failed");
        }
        internal void FinalDone()
        {
            Events.Add("send");
            if (FailSend) throw new InvalidOperationException("transport enqueue failed");
            FinalDoneSent++;
        }
        internal bool Tick()
        {
            if (!Requested) return false;
            bool confirmed = FinalCheckpointGate.TryPublishAndConfirm(
                Outstanding, PublishMaps, PublishPersonal, FinalDone);
            if (confirmed) Requested = false;
            return confirmed;
        }
    }

    private static int checks;
    private static void Check(bool good, string reason)
    {
        if (!good) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static bool SameEvents(List<string> events, params string[] expected)
    {
        if (events.Count != expected.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (events[i] != expected[i]) return false;
        return true;
    }

    private static void MainChecks()
    {
        Func<bool> empty = () => false;
        Action noop = () => { };
        Check(Throws<ArgumentNullException>(() =>
            FinalCheckpointGate.TryPublishAndConfirm(null, noop, noop, noop)),
            "missing pending predicate fails closed");
        Check(Throws<ArgumentNullException>(() =>
            FinalCheckpointGate.TryPublishAndConfirm(empty, null, noop, noop)),
            "missing native map publish rejected");
        Check(Throws<ArgumentNullException>(() =>
            FinalCheckpointGate.TryPublishAndConfirm(empty, noop, null, noop)),
            "missing personal publish rejected");
        Check(Throws<ArgumentNullException>(() =>
            FinalCheckpointGate.TryPublishAndConfirm(empty, noop, noop, null)),
            "missing host finalDone sender rejected");

        var clean = new Guest();
        Check(clean.Tick() && !clean.Requested && clean.FinalDoneSent == 1,
            "clean guest emits exactly one real finalDone");
        Check(SameEvents(clean.Events, "check", "map", "personal", "check", "send"),
            "native/ACK checks bracket both publish callbacks");
        Check(!clean.Tick() && clean.FinalDoneSent == 1,
            "successful finalDone clears the request exactly once");

        foreach (string source in new[] { "map", "personal", "command" })
        {
            var pending = new Guest();
            if (source == "map") pending.MapInFlight = 1;
            if (source == "personal") pending.PersonalInFlight = 1;
            if (source == "command") pending.QueuedCommands = 1;
            Check(!pending.Tick() && pending.Requested && pending.FinalDoneSent == 0,
                "preexisting pending " + source + " blocks finalDone");
            Check(SameEvents(pending.Events, "check"),
                "pending " + source + " prevents both new publishes");
            pending.MapInFlight = pending.PersonalInFlight = pending.QueuedCommands = 0;
            pending.Events.Clear();
            Check(pending.Tick() && pending.FinalDoneSent == 1 && !pending.Requested,
                "ACK or command drain allows the next final tick: " + source);
        }

        // Reproduction for BUG-10: the old code checked in-flight only
        // BEFORE publish, then emitted finalDone even for a freshly queued map.
        foreach (string newWork in new[] { "map", "personal", "command" })
        {
            var closing = new Guest
            {
                NewMap = newWork == "map",
                NewPersonal = newWork == "personal",
                NewCommand = newWork == "command"
            };
            Check(!closing.Tick() && closing.Requested && closing.FinalDoneSent == 0,
                "newly published " + newWork + " blocks premature finalDone");
            Check(SameEvents(closing.Events, "check", "map", "personal", "check"),
                "newly published " + newWork + " is checked before confirmation");
            Check(closing.MapsPublished == 1 && closing.PersonalPublished == 1,
                "both capture methods ran before waiting for " + newWork);

            closing.NewMap = closing.NewPersonal = closing.NewCommand = false;
            closing.MapInFlight = closing.PersonalInFlight = closing.QueuedCommands = 0;
            closing.Events.Clear();
            Check(closing.Tick() && closing.FinalDoneSent == 1 && !closing.Requested,
                "post-publish host ACK or command completion unblocks " + newWork);
        }

        var both = new Guest { NewMap = true, NewPersonal = true, NewCommand = true };
        Check(!both.Tick() && both.MapInFlight == 1 && both.PersonalInFlight == 1
            && both.QueuedCommands == 1 && both.FinalDoneSent == 0,
            "simultaneous new map/personal/command transactions remain outstanding");
        both.NewMap = both.NewPersonal = both.NewCommand = false;
        both.MapInFlight = 0;
        Check(!both.Tick() && both.FinalDoneSent == 0,
            "map ACK alone never overrides outstanding personal or command state");
        both.PersonalInFlight = 0;
        Check(!both.Tick() && both.FinalDoneSent == 0,
            "personal ACK still waits for deferred checkpoint command");
        both.QueuedCommands = 0;
        Check(both.Tick() && both.FinalDoneSent == 1,
            "all three categories must drain before completing finalDone");

        var mapFailure = new Guest { FailMap = true };
        Check(Throws<InvalidOperationException>(() => mapFailure.Tick())
            && mapFailure.Requested && mapFailure.FinalDoneSent == 0
            && mapFailure.PersonalPublished == 0,
            "map capture exception propagates without clearing final checkpoint request");
        mapFailure.FailMap = false;
        Check(mapFailure.Tick() && mapFailure.FinalDoneSent == 1,
            "map capture retry can finish after fault is gone");

        var personalFailure = new Guest { FailPersonal = true };
        Check(Throws<InvalidOperationException>(() => personalFailure.Tick())
            && personalFailure.Requested && personalFailure.FinalDoneSent == 0,
            "personal capture fault never confirms incomplete checkpoint");
        personalFailure.FailPersonal = false;
        Check(personalFailure.Tick() && personalFailure.FinalDoneSent == 1,
            "personal capture retries safely after failure");

        var sendFailure = new Guest { FailSend = true };
        Check(Throws<InvalidOperationException>(() => sendFailure.Tick())
            && sendFailure.Requested && sendFailure.FinalDoneSent == 0,
            "failed host queue enqueue preserves finalDone request");
        Check(SameEvents(sendFailure.Events, "check", "map", "personal", "check", "send"),
            "real Send fault only occurs after all barriers succeeded");
        sendFailure.FailSend = false;
        sendFailure.Events.Clear();
        Check(sendFailure.Tick() && sendFailure.FinalDoneSent == 1
            && !sendFailure.Requested,
            "failed send can be retried exactly once after recovery");

        var preflightFailure = new Guest { FailPreflight = true };
        Check(Throws<InvalidOperationException>(() => preflightFailure.Tick())
            && preflightFailure.Requested && preflightFailure.MapsPublished == 0,
            "outstanding transaction inspection fault does not bypass barrier");

        // Randomized interleavings of host ACK, user changes, and the final
        // send. No run may claim completion with outstanding work.
        var random = new Random(20261018);
        for (int round = 0; round < 18000; round++)
        {
            var state = new Guest
            {
                MapInFlight = random.Next(0, 3),
                PersonalInFlight = random.Next(0, 3),
                QueuedCommands = random.Next(0, 3),
                NewMap = random.Next(2) == 0,
                NewPersonal = random.Next(2) == 0,
                NewCommand = random.Next(2) == 0
            };
            bool confirmed = state.Tick();
            Check(!confirmed || state.MapInFlight == 0
                    && state.PersonalInFlight == 0 && state.QueuedCommands == 0,
                "random finalDone never races a newly queued update " + round);
            Check(confirmed == (state.FinalDoneSent == 1)
                    && state.Requested == !confirmed,
                "random completion and pending request state are consistent " + round);
            if (!confirmed)
            {
                state.NewMap = state.NewPersonal = state.NewCommand = false;
                state.MapInFlight = state.PersonalInFlight = state.QueuedCommands = 0;
                Check(state.Tick() && state.FinalDoneSent == 1 && !state.Requested,
                    "random ACK drainage permits completion on a later tick " + round);
            }
        }
    }

    private static int Main()
    {
        try
        {
            MainChecks();
            Console.WriteLine("PASS: " + checks + " production-linked final checkpoint gate checks");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
