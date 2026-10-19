using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string description)
    {
        checks++;
        if (!condition) throw new Exception("FAILED: " + description);
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }
    private static CommandResult Result(string id, bool success = true,
        bool applied = true, bool saved = true)
    {
        return new CommandResult { CommandId = id, PlayerId = "player",
            Success = success, Applied = applied, Saved = saved,
            SaveGeneration = saved ? 27 : 0, PayloadJson = "private-native-payload" };
    }
    private static void Main()
    {
        // C31-01: host transaction already exists when a UI listener runs.
        // Its exception must not change the returned authoritative object,
        // cause a second native execution, or make the host Submit throw.
        var host = new MultiplayerSession();
        var finished = Result("host-command");
        int healthyUi = 0, native = 0;
        host.CommandProjectionRequired += r => { native++; Check(Object.ReferenceEquals(r, finished),
            "native callback sees exact authoritative result"); };
        Action<CommandResult> badUi = _ =>
            throw new InvalidOperationException("private-native-payload SHOULD NOT BE LOGGED");
        host.CommandCompleted += badUi;
        host.CommandCompleted += r =>
        { healthyUi++; Check(r.CommandId == "host-command" && r.Saved, "healthy UI sees saved command"); };
        CommandResult returned = host.HostFinish(finished);
        Check(Object.ReferenceEquals(returned, finished) &&
              Object.ReferenceEquals(host.Stored["host-command"], finished),
              "saved host result returned unchanged despite failed optional UI callback");
        Check(!host.IsFaulted && healthyUi == 1 && native == 1,
            "one optional listener fault cannot block another listener or native projection");
        Check(Logger.Warnings.Count == 1 &&
              Logger.Warnings[0].Contains("command.presentation") &&
              Logger.Warnings[0].Contains("InvalidOperationException") &&
              !Logger.Warnings[0].Contains("private-native-payload"),
              "presentation fault reports only fixed stage/type, never native payload or exception message");

        // C31-02: valid result from host is not a malicious message just
        // because an optional listener throws; healthy observers still run.
        var guest = new MultiplayerSession();
        int guestUi = 0;
        guest.CommandCompleted += badUi;
        guest.CommandCompleted += _ => guestUi++;
        guest.ReceiveHostResult(Result("legitimate-host"));
        Check(guestUi == 1 && !guest.IsFaulted && guest.TraceCount == 1,
            "guest result observer failure never propagates to network message dispatch");

        // C31-03: host has ALREADY removed the remote pending command and
        // recorded its transaction before the local presentation callbacks.
        var receiver = new MultiplayerSession();
        int receiverProjection = 0, receiverUi = 0;
        receiver.CommandProjectionRequired += _ => receiverProjection++;
        receiver.CommandCompleted += badUi;
        receiver.CommandCompleted += _ => receiverUi++;
        var execution = Result("remote-execution", true, true, false);
        Check(Object.ReferenceEquals(receiver.AcceptRemoteExecution(execution), execution) &&
              receiver.Stored["remote-execution"].Applied &&
              !receiver.Stored["remote-execution"].Saved &&
              receiverProjection == 1 && receiverUi == 1 &&
              !receiver.IsFaulted, "remote execution stays applied/unsaved when local UI fails");

        // C31-04: direct Native callback is one-use, even if notification
        // fails and the status event also throws. The session must remain
        // fail-closed, yet the original result is not reclassified as a reject.
        var direct = new MultiplayerSession();
        int directCalls = 0;
        direct.Direct("native-1", _ =>
        {
            directCalls++;
            throw new InvalidOperationException("sensitive-native-exception");
        });
        direct.ThrowStatus = true;
        var directResult = Result("native-1", true, true, true);
        Check(Object.ReferenceEquals(direct.HostFinish(directResult), directResult) &&
            direct.IsFaulted && direct.IsPaused &&
            direct.CallbackCount == 0 && directCalls == 1 &&
            direct.Stored["native-1"].Saved &&
            direct.LastError.Contains("command.native-callback"),
            "real one-use callback fault locks local native state, not authoritative result");
        direct.Emit(directResult);
        Check(directCalls == 1, "subsequent duplicate cannot replay consumed native callback");
        Check(Logger.Errors.Count == 1 &&
              Logger.Errors[0].Contains("command.native-callback") &&
              !Logger.Errors[0].Contains("sensitive-native-exception"),
              "critical native callback's diagnostic never includes untrusted error/payload text");

        // Pending acknowledgements must NOT consume completion callbacks:
        // the exact later terminal result still executes the callback once.
        var phases = new MultiplayerSession();
        int phaseCalls = 0;
        phases.Direct("deferred", _ => phaseCalls++);
        phases.Emit(Result("deferred", true, false, false));
        Check(phaseCalls == 0 && phases.CallbackCount == 1,
            "nonterminal queued response preserves dedicated callback for later commit");
        phases.Emit(Result("deferred", true, true, true));
        phases.Emit(Result("deferred", true, true, true));
        Check(phaseCalls == 1 && phases.CallbackCount == 0,
            "terminal success consumes dedicated callback exactly once");
        var rejected = new MultiplayerSession();
        int rejectionCalls = 0;
        rejected.Direct("rejected", r => { if (!r.Success) rejectionCalls++; });
        rejected.Emit(Result("rejected", false, false, false));
        Check(rejectionCalls == 1 && !rejected.IsFaulted,
            "terminal business rejection still invokes its direct handler once");

        // C31-05/06: native game callback failures, including a broken map
        // projection, are NEVER silently classified as cosmetic UI faults.
        var projection = new MultiplayerSession();
        int projectionUi = 0, nextProjection = 0;
        projection.CommandProjectionRequired += _ =>
            throw new FormatException("private-map-snapshot-invalid");
        projection.CommandProjectionRequired += _ => nextProjection++;
        projection.CommandCompleted += _ => projectionUi++;
        var mapResult = Result("resolved-map");
        Check(Object.ReferenceEquals(projection.HostFinish(mapResult), mapResult) &&
              projection.IsFaulted && projection.IsPaused &&
              projection.Stored["resolved-map"].Applied &&
              nextProjection == 0 && projectionUi == 0,
            "invalid native map projection locks locally and does not display false success");
        Check(Logger.Errors[Logger.Errors.Count - 1].Contains("command.native-projection") &&
              !Logger.Errors[Logger.Errors.Count - 1].Contains("private-map-snapshot-invalid"),
            "native map fault stage is visible without source payload data");

        // An unavailable host clock broadcast and second UI status error do
        // not undo a native fault lock or leak the error to the sender.
        var doubleFault = new MultiplayerSession();
        doubleFault.ThrowClock = true;
        doubleFault.ThrowStatus = true;
        doubleFault.CommandProjectionRequired += _ =>
            throw new InvalidOperationException("native consistency uncertain");
        doubleFault.ReceiveHostResult(Result("double-fault"));
        Check(doubleFault.IsFaulted && doubleFault.LockCount == 1 &&
              doubleFault.LastError.Contains("command.native-projection"),
            "nested native fault/status/broadcast failure retains the fail-closed barrier");

        // C31-07/08: optional observer lists are snapshotted; unsubscribe,
        // reentrant subscription, and mixed good/bad command IDs are deterministic.
        var eventSession = new MultiplayerSession();
        var seen = new List<string>();
        Action<CommandResult> selfRemoving = null;
        Action<CommandResult> late = r => seen.Add("late:" + r.CommandId);
        selfRemoving = r =>
        {
            seen.Add("first:" + r.CommandId);
            eventSession.CommandCompleted -= selfRemoving;
            eventSession.CommandCompleted += late;
        };
        eventSession.CommandCompleted += selfRemoving;
        eventSession.CommandCompleted += r =>
        {
            seen.Add("second:" + r.CommandId);
            if (r.CommandId == "a") throw new IOException("UI panel disposed");
        };
        eventSession.Emit(Result("a"));
        Check(seen.Count == 2 && seen[0] == "first:a" && seen[1] == "second:a",
            "current emission uses original snapshot even after listener unregister/register");
        eventSession.Emit(Result("b"));
        Check(seen.Count == 4 && seen[2] == "second:b" && seen[3] == "late:b" &&
              !eventSession.IsFaulted, "healthy later command survives a previous UI callback fault");

        // C31-08: synchronous reentrancy with the *same* CommandId must not
        // invoke native projection or optional UI a second time before the
        // first notification finishes. A different CommandId may still run.
        var reentered = new MultiplayerSession();
        int reenteredNative = 0, reenteredUi = 0, nestedUi = 0;
        reentered.CommandProjectionRequired += r =>
        {
            reenteredNative++;
            if (r.CommandId == "same") reentered.Emit(r);
        };
        reentered.CommandCompleted += r =>
        {
            if (r.CommandId == "same")
            {
                reenteredUi++;
                reentered.Emit(r);
                reentered.Emit(Result("nested-different-id"));
            }
            else nestedUi++;
        };
        reentered.Emit(Result("same"));
        Check(reenteredNative == 2 && reenteredUi == 1 && nestedUi == 1 &&
              !reentered.IsFaulted, "same-id recursive callbacks run once per active stack, other IDs remain valid");
        reentered.Emit(Result("same"));
        Check(reenteredUi == 2 && reenteredNative == 4,
            "synchronous guard releases after completion without falsely caching a permanent result");

        // Logging is optional even when every logger sink fails.
        Logger.ThrowOnLog = true;
        var noLogs = new MultiplayerSession();
        int noLogsHealthy = 0;
        noLogs.CommandCompleted += badUi;
        noLogs.CommandCompleted += _ => noLogsHealthy++;
        noLogs.ReceiveHostResult(Result("no-log"));
        Logger.ThrowOnLog = false;
        Check(noLogsHealthy == 1 && !noLogs.IsFaulted,
            "observer report failures never block a later healthy listener");

        var noisy = new MultiplayerSession();
        int priorWarnings = Logger.Warnings.Count;
        noisy.CommandCompleted += _ =>
            throw new IOException("secret UI data that must never appear in diagnostics");
        for (int i = 0; i < 1000; i++)
            noisy.ReceiveHostResult(Result("notify-" + i));
        Check(Logger.Warnings.Count == priorWarnings + 1 && !noisy.IsFaulted,
            "repeated same-stage presentation errors are logged at most once per session");
        Check(!Logger.Warnings[Logger.Warnings.Count - 1].Contains("secret UI data"),
            "bounded diagnostics cannot disclose private callback exception details");

        // C31-09: actual business/host validation or tracing is outside the
        // optional presentation catch. A failure there must NOT be swallowed.
        var invalid = new MultiplayerSession();
        invalid.ThrowTrace = true;
        Check(Throws<InvalidOperationException>(() => invalid.Emit(Result("bad-authoritative-state"))),
            "real pre-notification business faults remain fail-closed and observable");
        Check(invalid.TraceCount == 1 && invalid.Stored.Count == 0,
            "observer isolation never fabricates a committed transaction");

        // Many independent results with both native and UI observers. Errors
        // on A must not affect B/C, payload identity or SaveGeneration.
        var random = new Random(31031);
        for (int i = 0; i < 15000; i++)
        {
            var session = new MultiplayerSession();
            int good = 0, goodNative = 0, bad = 0;
            var value = Result("id-" + i, saved: i % 3 == 0);
            int warningBefore = Logger.Warnings.Count;
            session.CommandProjectionRequired += _ => goodNative++;
            session.CommandCompleted += _ =>
            {
                if (random.Next(3) == 0)
                {
                    bad++;
                    throw new IOException("optional UI failure");
                }
            };
            session.CommandCompleted += _ => good++;
            var stored = session.HostFinish(value);
            Check(Object.ReferenceEquals(stored, value) &&
                  Object.ReferenceEquals(session.Stored[value.CommandId], value),
                "randomized host commit remains identical to its stored result");
            Check(good == 1 && goodNative == 1 && !session.IsFaulted &&
                  stored.SaveGeneration == value.SaveGeneration,
                "optional randomized observer errors cannot corrupt native callbacks or SaveGeneration");
            Check(Logger.Warnings.Count == warningBefore + (bad > 0 ? 1 : 0),
                "optional failure yields one stage/type-only diagnostic without spurious healthy logs");
        }

        Console.WriteLine("PASS: " + checks +
            " production CommandResultNotifications observer/native-fault checks");
    }
}
