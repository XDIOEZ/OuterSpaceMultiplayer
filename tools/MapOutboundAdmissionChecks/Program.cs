using System;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string context)
    {
        checks++;
        if (!condition) throw new Exception("FAILED: " + context);
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }
    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void Main()
    {
        // C33-01/02: the production SendCapturedMapUpdate method holds the
        // captured original frame on TCP/Steam enqueue rejection; no second
        // native capture can silently erase changes already in its baseline.
        var slow = new MultiplayerSession();
        slow.Network.Failures = 2; slow.Network.InvalidFailures = 1;
        var original = slow.PublishOnce("stable-commit");
        byte[] first = slow.PendingFrame;
        Check(original != null && slow.Pending && slow.FlightCount == 1 &&
            slow.CaptureCalls == 1 && slow.EncodeCalls == 1,
            "first failed enqueue preserves one original map transaction and encoding");
        Check(slow.PublishOnce("never-recapture") == null && slow.CaptureCalls == 1,
            "waiting admission gate blocks another native capture of the same map");
        slow.Clock(1.999);
        Check(slow.Network.Attempted.Count == 1, "first retry is not a frame-by-frame busy-loop");
        slow.Clock(0.001);
        Check(slow.Network.Attempted.Count == 2 && slow.Pending &&
            Object.ReferenceEquals(slow.Network.Attempted[0], slow.Network.Attempted[1]),
            "second attempt reuses same serialized bytes across queue error types");
        slow.Clock(2);
        Check(slow.Network.Attempted.Count == 3 && slow.Network.Accepted.Count == 1
            && SameBytes(first, slow.Network.Accepted[0]) &&
            slow.FlightCount == 1 && !slow.Pending && slow.EncodeCalls == 1,
            "successful transport enqueue releases retry frame, not unacknowledged map");
        slow.Clock(30);
        Check(slow.Network.Attempted.Count == 3 && slow.FlightCount == 1,
            "an accepted frame is not resent merely because the ACK is absent");
        slow.Ack(7);
        Check(slow.FlightCount == 0 && slow.Revision == 6 &&
            slow.PublishOnce("next-commit") != null,
            "acknowledged map advances host revision, permitting the next capture");
        slow.Ack(7);
        Check(slow.Revision == 7, "stale duplicate does not advance a finished transaction twice");

        // C33-03: a formatter or unknown transport exception has an uncertain
        // status; the only safe recovery is native fault lock and human rebase.
        var malformed = new MultiplayerSession();
        malformed.FailEncode = true;
        malformed.FailNativeDiagnostic = true;
        Check(Throws<FormatException>(() => malformed.PublishOnce("encode-error"))
            && malformed.Faulted && malformed.Locks == 1
            && malformed.Network.Attempted.Count == 0 && malformed.FlightCount == 1,
            "failed encoding locks the native baseline even when the UI fault notification throws");
        malformed.Clock(10);
        Check(malformed.Network.Attempted.Count == 0 &&
            !malformed.Fault.Contains("private-native-payload"),
            "no retry and no raw map contents in the retained error state");
        var unknown = new MultiplayerSession();
        unknown.Network.UnexpectedFailure = new FormatException("secret-unknown-transport");
        Check(Throws<FormatException>(() => unknown.PublishOnce("unknown"))
            && unknown.Faulted && unknown.PendingFrame == null,
            "uncertain post-enqueue exception cannot auto-replay an unidentified frame");
        unknown.Clock(100);
        Check(unknown.Network.Attempted.Count == 1
            && !unknown.Fault.Contains("secret-unknown-transport"),
            "native lock prevents retry and redacts diagnostic message data");

        // C33-04/08: until a real matching ACK exists the in-flight lease is
        // NOT an authoritative revision or final checkpoint completion.
        var queued = new MultiplayerSession();
        var noAck = queued.PublishOnce("ack-delayed");
        queued.Clock(60);
        Check(!queued.Pending && queued.FlightCount == 1
            && queued.Revision == noAck.BaseRevision
            && queued.Network.Accepted.Count == 1,
            "transport success means queued, not applied/saved/acknowledged");
        Check(queued.PublishOnce("must-wait") == null,
            "an accepted but unacknowledged map remains a publish barrier");
        queued.Ack(7);
        Check(queued.Revision == noAck.Revision &&
            queued.FlightCount == 0, "exact ACK advances authoritative local map once");

        // C33-05: a synchronous response can consume the original lease before
        // the transport call returns; the old sender cannot resurrect it.
        var reentrant = new MultiplayerSession();
        reentrant.Network.AfterSend = () => { reentrant.Network.AfterSend = null; reentrant.Ack(7); };
        reentrant.PublishOnce("synchronous-ack");
        Check(!reentrant.Pending && reentrant.FlightCount == 0 && reentrant.Revision == 6,
            "synchronous ACK clears the current lease before Send returns");

        var replace = new MultiplayerSession();
        replace.Network.AfterSend = () =>
        {
            replace.Network.AfterSend = null;
            replace.ReplaceView(7, 6);
            replace.Network.Failures = 1;
            replace.SendUpdate(replace.CaptureMap("new-after-rebase"));
            // Mimics old transport Send later throwing after an event reentered
            // and installed a different map transaction.
            throw new IOException("stale outer sender");
        };
        replace.PublishOnce("old-before-rebase");
        Check(replace.Pending && replace.PendingCommit == "new-after-rebase"
            && replace.FlightCount == 1,
            "stale sender failure does not delete/modify reentrant replacement lease; pending="
                + replace.Pending + ", commit=" + replace.PendingCommit + ", count=" + replace.FlightCount);
        replace.Clock(2);
        Check(!replace.Pending && replace.Network.Accepted.Count == 2,
            "replacement retry uses its own bytes after outer send failure");
        replace.Ack(7);
        Check(replace.Revision == 7 && replace.FlightCount == 0,
            "new authority transaction can be acknowledged independently");

        // C33-06: a covering full host view removes the old in-flight object,
        // so its retry must be discarded before trying to transmit old delta.
        var view = new MultiplayerSession();
        view.Network.Failures = 5;
        view.PublishOnce("stale-before-view");
        view.ReplaceView(7, 20);
        view.Clock(2);
        Check(!view.Pending && view.FlightCount == 0 &&
              view.Network.Attempted.Count == 1 && view.Revision == 20,
            "authoritative host view removes the stale retry frame without sending it");
        Check(view.PublishOnce("post-rebase") != null,
            "a newer host view unblocks native capture without reviving old commit");

        var oldAuthority = new MultiplayerSession();
        oldAuthority.Network.Failures = 2;
        oldAuthority.PublishOnce("obsolete-epoch");
        oldAuthority.ChangeAuthority(4);
        oldAuthority.Clock(2);
        Check(oldAuthority.Faulted && oldAuthority.Network.Attempted.Count == 1,
            "changed authority epoch fails closed instead of emitting an old simulator update");

        var oldSession = new MultiplayerSession();
        oldSession.Network.Failures = 2;
        oldSession.PublishOnce("obsolete-room");
        oldSession.ChangeSession("new-room");
        oldSession.Clock(2);
        Check(oldSession.Faulted && oldSession.Network.Attempted.Count == 1,
            "old session's delta is never retried into a newer room");
        var oldWorld = new MultiplayerSession();
        oldWorld.Network.Failures = 2;
        oldWorld.PublishOnce("obsolete-world");
        oldWorld.ChangeWorld("new-world");
        oldWorld.Clock(2);
        Check(oldWorld.Faulted && oldWorld.Network.Attempted.Count == 1,
            "old world delta is never retried into a new world scope");

        var changedSimulator = new MultiplayerSession();
        changedSimulator.Network.Failures = 2;
        changedSimulator.PublishOnce("obsolete-simulator");
        changedSimulator.ChangeSimulator("another-player");
        changedSimulator.Clock(2);
        Check(changedSimulator.Faulted && changedSimulator.Network.Attempted.Count == 1,
            "old simulator cannot publish after map authority owner changes");

        // C33-08: after prolonged, definite queue rejection, no new capture or
        // false save/ACK occurs. The map data stays protected by a native lock.
        var forever = new MultiplayerSession();
        forever.Network.Failures = 10000;
        forever.PublishOnce("never-enqueued");
        for (int t = 0; t < 60; t++) forever.Clock(1);
        Check(forever.Faulted && forever.FlightCount == 1 &&
              forever.Network.Attempted.Count == 31 &&
              forever.CaptureCalls == 1,
            "60 seconds of refused admission locks the same buffered map delta");
        forever.Clock(400);
        Check(forever.Network.Attempted.Count == 31 &&
            forever.PublishOnce("blocked-by-pending") == null,
            "native lock prevents an unbounded retry storm and duplicate capture");
        forever.Leave();
        Check(!forever.Pending, "session teardown frees a not-yet-queued frame");

        // C33-07 / C33-09 partial: invalid cross-session/null map payload
        // cannot install an outbound lease or masquerade as a recoverable IO.
        var invalid = new MultiplayerSession();
        Check(Throws<InvalidOperationException>(() => invalid.SendUpdate(null))
            && invalid.FlightCount == 0, "null map transaction is rejected before registration");
        var wrongSession = invalid.CaptureMap("invalid-transaction");
        wrongSession.SessionId = "another-room";
        Check(Throws<InvalidOperationException>(() => invalid.SendUpdate(wrongSession))
            && invalid.FlightCount == 0, "foreign room does not register a map delta");
        var host = new MultiplayerSession();
        host.HostMode();
        Check(Throws<InvalidOperationException>(() => host.SendUpdate(host.CaptureMap("host")))
            && host.FlightCount == 0, "host does not use guest outbound retry path");

        // A large batch of deterministic independent room lifetimes exercises
        // strict two-second retry steps and same-identity ACK consumption.
        var random = new Random(33033);
        for (int i = 0; i < 12000; i++)
        {
            var session = new MultiplayerSession();
            int rejects = random.Next(0, 6);
            session.Network.Failures = rejects;
            session.Network.InvalidFailures = random.Next(0, rejects + 1);
            string commit = "commit-" + i;
            var update = session.PublishOnce(commit);
            Check(update != null && update.CommitId == commit &&
                session.CaptureCalls == 1, "random map capture ID is stable before retry");
            for (int step = 0; step < rejects; step++) session.Clock(2);
            Check(session.Network.Accepted.Count == 1 && !session.Pending
                && session.FlightCount == 1 && session.EncodeCalls == 1,
                "random rejected admissions reuse the same original encoded frame");
            for (int j = 1; j < session.Network.Attempted.Count; j++)
                Check(Object.ReferenceEquals(session.Network.Attempted[0],
                    session.Network.Attempted[j]), "random retry never re-encodes a map");
            session.Ack(7);
            Check(session.FlightCount == 0 && session.Revision == 6 && !session.Faulted,
                "random exact ACK releases matching map lease only");
        }
        Console.WriteLine("PASS: " + checks +
            " production MapOutbound first-send/retry/reentrancy/authority checks");
    }
}
