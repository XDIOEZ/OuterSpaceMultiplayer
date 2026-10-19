using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string description)
    {
        checks++;
        if (!value) throw new Exception("FAIL " + description);
    }
    private static bool Throws<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return true; }
        return false;
    }
    private static PlayerUpdate Update(long revision, string player = "local")
    {
        return new PlayerUpdate { PlayerId = player, BaseRevision = revision - 1, Revision = revision,
            NativeDeltaJson = "{\"Field\":\"synthetic-value\"}" };
    }
    private static void Main()
    {
        // C32-01: the bridge has already advanced its capture baseline. A
        // failed queue admission must keep the exact frame and the gate.
        var queued = new MultiplayerSession();
        queued.Network.Failures = 2;
        queued.Network.IOExceptionFailures = 1;
        queued.SendDelta(Update(1));
        string firstFrame = queued.PendingFrameText;
        Check(queued.IsPending && !queued.Queued && queued.FlightCount == 1 &&
            queued.Network.Attempted.Count == 1 && queued.Network.Sent.Count == 0,
            "first transient TCP queue failure preserves exactly one in-flight lease");
        queued.Clock(1.999);
        Check(queued.Network.Attempted.Count == 1, "two-second unscaled retry backoff");
        queued.Clock(0.001);
        Check(queued.Network.Attempted.Count == 2 && queued.Network.Sent.Count == 0,
            "closed Steam queue preserves the same original retry without re-capture");
        queued.Clock(2);
        Check(queued.Network.Attempted.Count == 3 && queued.Network.Sent.Count == 1 &&
            queued.Network.Sent[0] == queued.Network.Attempted[0] &&
            queued.EncodeCalls == 1 && queued.Queued && queued.FlightCount == 1 &&
            firstFrame == queued.PendingFrameText,
            "later transport acceptance sends the same encoded delta only once");
        queued.Clock(600);
        Check(queued.Network.Attempted.Count == 3, "successful enqueue is not blindly retransmitted");
        queued.Ack(Update(1));
        Check(!queued.IsPending && queued.FlightCount == 0 && queued.Revision == 1,
            "matching authoritative ACK releases the gate and advances confirmed revision");

        // C32-02: stale, duplicated and forged acknowledgements cannot
        // consume a later authoritative revision or clear its active gate.
        var receipts = new MultiplayerSession();
        receipts.SendDelta(Update(1));
        receipts.Ack(Update(1));
        receipts.SendDelta(Update(2));
        receipts.Ack(Update(1));
        receipts.Ack(new PlayerUpdate { PlayerId = "local", BaseRevision = 999, Revision = 2 });
        Check(receipts.IsPending && receipts.FlightCount == 1 && receipts.Revision == 1,
            "obsolete or mismatching ACK retains a newer outstanding update");
        Check(Throws<MessageRejectionException>(() => receipts.Ack(Update(100, "other"))),
            "foreign player ACK cannot alter another player's progress");
        receipts.Ack(Update(2));
        Check(receipts.Revision == 2 && receipts.FlightCount == 0,
            "matching second ACK finishes the correct newer update");
        receipts.Ack(Update(1));
        Check(receipts.Revision == 2, "duplicate stale ACK does not regress local revision");
        receipts.NewRevision(10);
        receipts.SendDelta(Update(3));
        receipts.Ack(Update(3));
        Check(receipts.Revision == 10, "matching older ACK cannot roll back an independently newer revision");

        // C32-03: an unusual synchronous transport callback may deliver an
        // acknowledgement before Send() returns. It cannot resurrect a
        // cleared lease or make the retry timer send twice.
        var synchronous = new MultiplayerSession();
        synchronous.Network.OnSend = () => synchronous.Ack(Update(1));
        synchronous.SendDelta(Update(1));
        Check(!synchronous.IsPending && synchronous.FlightCount == 0 &&
            synchronous.Revision == 1 && synchronous.Network.Sent.Count == 1,
            "inline ACK clears exact transaction without reentrant resurrection");
        synchronous.Clock(5);
        Check(synchronous.Network.Sent.Count == 1, "inline ACK does not create a delayed retry");

        // C32-04: a real white-list baseline reset applies native fields
        // before abandoning the captured delta.
        var reset = new MultiplayerSession();
        reset.Network.Failures = 4;
        reset.SendDelta(Update(1));
        reset.Native.ShouldFail = true;
        Check(Throws<InvalidOperationException>(() => reset.Reset(new PlayerFieldState {
            PlayerId = "local", PersonalRevision = 8 })),
            "failed native reset still fails closed");
        Check(reset.IsPending && reset.FlightCount == 1 && reset.Revision == 0,
            "partial reset cannot release outstanding personal delta");
        reset.Native.ShouldFail = false;
        Check(Throws<MessageRejectionException>(() => reset.Reset(new PlayerFieldState {
            PlayerId = "foreign", PersonalRevision = 9 })),
            "a foreign reset may not overwrite the local native player");
        reset.Reset(new PlayerFieldState { PlayerId = "local", PersonalRevision = 8 });
        Check(!reset.IsPending && reset.FlightCount == 0 && reset.Revision == 8 &&
            reset.Native.Applied == 2,
            "valid native reset atomically releases the previous flight on success");

        // C32-05: encoding errors after native capture retain the pending
        // transaction and cause a local fail-closed lock, not a recapture.
        var malformed = new MultiplayerSession();
        malformed.FailEncoding();
        malformed.FailStatus();
        Check(Throws<FormatException>(() => malformed.SendDelta(Update(1))),
            "real encoding failure is not hidden as a transient full queue");
        Check(malformed.IsFaulted && malformed.IsPending && malformed.FlightCount == 1 &&
            malformed.Network.Attempted.Count == 0 && malformed.Locks == 1,
            "serialization failure locks native state before losing delta or faking ACK");
        malformed.Clock(2);
        Check(malformed.Network.Sent.Count == 0, "failed serialization never sends a missing frame");

        // C32-06: old session / stopped transport cannot receive old pending
        // work; a new session may create an entirely new lease.
        var old = new MultiplayerSession();
        old.Network.Failures = 999;
        old.SendDelta(Update(1));
        old.ChangeSession("next-session");
        old.Clock(2);
        Check(old.Network.Attempted.Count == 1 && old.FlightCount == 1,
            "old encoded delta never enters another host session");
        old.Leave();
        Check(!old.IsPending && old.FlightCount == 0, "disconnect clears pending encoded personal frame");
        old.Rejoin();
        old.Network.Failures = 0;
        old.SendDelta(Update(1));
        Check(old.PendingFrameText.Contains("next-session") && old.Queued,
            "new session uses newly encoded frame and clean lease");
        old.Close();
        old.Clock(2);
        Check(old.Network.Attempted.Count == 2, "closing does not send stale retries");

        // C32-07: exact-match validation, nulls, and duplicates fail closed
        // without changing the local player's independent state.
        var validation = new MultiplayerSession();
        Check(Throws<InvalidOperationException>(() => validation.SendDelta(Update(1, "other"))),
            "foreign delta never leaves this client");
        Check(Throws<InvalidOperationException>(() => validation.SendDelta(null)),
            "null delta cannot reserve a slot");
        validation.SendDelta(Update(1));
        Check(Throws<InvalidOperationException>(() => validation.SendDelta(Update(2))),
            "a second delta cannot jump ahead of the outstanding native baseline");
        Check(validation.FlightCount == 1 && validation.Network.Sent.Count == 1,
            "second blocked send does not enqueue or consume new data");
        Check(Throws<MessageRejectionException>(() => validation.Ack(null)),
            "null ACK is rejected");

        // Permanent backpressure must become a visible fail-closed fault,
        // rather than silently keeping the native delta and room frozen forever.
        var exhausted = new MultiplayerSession();
        exhausted.Network.Failures = 10000;
        exhausted.FailStatus();
        exhausted.SendDelta(Update(1));
        exhausted.Clock(59.9);
        Check(!exhausted.IsFaulted && exhausted.FlightCount == 1 &&
            exhausted.Network.Sent.Count == 0,
            "before the 60-second limit the same captured frame is still retryable");
        exhausted.Clock(0.1);
        Check(exhausted.IsFaulted && exhausted.Locks == 1 &&
            exhausted.IsPending && exhausted.FlightCount == 1,
            "60 seconds of congestion preserves the transaction and locks unsafe progress");
        int attemptsAfterFault = exhausted.Network.Attempted.Count;
        exhausted.Clock(120);
        Check(exhausted.Network.Attempted.Count == attemptsAfterFault,
            "permanent failure does not trigger an unbounded retry storm after fail-closed");

        // C32-08: 12k deterministic trials of failed first admission,
        // independent base/revision markers and matching ACKs. No sleeps.
        var random = new Random(32032);
        for (int i = 0; i < 12000; i++)
        {
            var trial = new MultiplayerSession();
            int failures = random.Next(1, 5);
            trial.Network.Failures = failures;
            trial.Network.IOExceptionFailures = random.Next(0, failures + 1);
            int revision = random.Next(1, 1_000_000);
            var update = Update(revision);
            trial.SendDelta(update);
            Check(trial.IsPending && trial.FlightCount == 1 && trial.EncodeCalls == 1,
                "trial records one unique captured personal revision");
            for (int j = 0; j < failures; j++)
            {
                trial.Clock(2);
                Check(trial.FlightCount == 1 && trial.EncodeCalls == 1,
                    "trial retry never re-captures or frees the authoritative pending update");
            }
            Check(trial.Queued && trial.Network.Sent.Count == 1 &&
                trial.Network.Attempted.Count == failures + 1,
                "trial admits exactly one copy after its known failures");
            trial.Ack(Update(revision - 1));
            Check(trial.IsPending && trial.Revision == 0,
                "trial stale ACK does not clear the current revision");
            trial.Ack(update);
            Check(!trial.IsPending && trial.FlightCount == 0 &&
                trial.Revision == revision,
                "trial matching ACK confirms only its own captured progress");
        }

        Console.WriteLine("PASS: " + checks +
            " production personal delta outbound retry, ACK ownership and native reset checks");
    }
}
