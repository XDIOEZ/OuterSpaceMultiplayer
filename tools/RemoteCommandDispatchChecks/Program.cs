using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }
    private static bool Throws<T>(Action call) where T : Exception
    {
        try { call(); return false; }
        catch (T) { return true; }
    }
    private static GameCommand NewCommand(string id)
    {
        return new GameCommand {
            CommandId = id, SessionId = "session", PlayerId = "guest",
            MapId = 17, AuthorityEpoch = 5, BaseRevision = 40,
            Kind = CommandKind.Work
        };
    }

    private sealed class FakeHost
    {
        internal readonly Dictionary<string, GameCommand> Pending =
            new Dictionary<string, GameCommand>(StringComparer.Ordinal);
        internal readonly List<string> Enqueued = new List<string>();
        internal readonly Dictionary<string, int> NativeExecutions =
            new Dictionary<string, int>(StringComparer.Ordinal);
        internal bool FailSend;
        internal int SendAttempts;

        internal void Dispatch(GameCommand command)
        {
            string key = command.PlayerId + ":" + command.CommandId;
            if (Pending.ContainsKey(key)) return;
            GameCommand snapshot = NewCommand(command.CommandId);
            RemoteCommandDispatch.EnqueueOrRollback(Pending, key, snapshot, () =>
            {
                SendAttempts++;
                if (FailSend) throw new IOException("simulator queue saturated");
                Enqueued.Add(key);
            });
        }

        internal void ExecuteAtSimulator(GameCommand command, bool failMap, bool failReceipt)
        {
            string key = command.PlayerId + ":" + command.CommandId;
            int count;
            if (!NativeExecutions.TryGetValue(key, out count)) NativeExecutions[key] = count = 1;
            RemoteCommandDispatch.PublishBeforeReceipt(
                () =>
                {
                    Enqueued.Add("map:" + key);
                    if (failMap) throw new IOException("map queue saturated");
                },
                () =>
                {
                    if (failReceipt) throw new IOException("execution receipt queue saturated");
                    Enqueued.Add("execution:" + key);
                });
        }
    }

    private static void FailureRollback()
    {
        var host = new FakeHost();
        var command = NewCommand("c-one");
        string key = "guest:c-one";
        host.FailSend = true;
        Check(Throws<IOException>(() => host.Dispatch(command)),
            "initial execute transport rejection is observable");
        Check(host.Pending.Count == 0 && host.SendAttempts == 1 &&
            host.Enqueued.Count == 0, "failed queue never leaves a zombie pending execution");

        host.FailSend = false;
        host.Dispatch(command);
        Check(host.Pending.Count == 1 && host.Pending.ContainsKey(key) &&
            host.Enqueued.Count == 1 && host.Enqueued[0] == key,
            "the same command identity is safely redispatched after queue recovery");
        host.Dispatch(command);
        Check(host.SendAttempts == 2 && host.Enqueued.Count == 1,
            "already pending command is not sent twice");

        host.Pending.Remove(key);
        host.Dispatch(command);
        Check(host.Pending.Count == 1 && host.SendAttempts == 3,
            "a completed command slot can be released without modifying other entries");
    }

    private static void IsolationAndReentrancy()
    {
        var pending = new Dictionary<string, GameCommand>();
        var first = NewCommand("a");
        pending.Add("guest:other", NewCommand("other"));
        int sends = 0;
        Check(Throws<ArgumentNullException>(() =>
            RemoteCommandDispatch.EnqueueOrRollback(null, "id", first, () => { })),
            "null authoritative pending table is rejected");
        Check(Throws<ArgumentException>(() =>
            RemoteCommandDispatch.EnqueueOrRollback(pending, "", first, () => { })),
            "empty transaction key is rejected");
        Check(Throws<ArgumentNullException>(() =>
            RemoteCommandDispatch.EnqueueOrRollback(pending, "guest:a", null, () => { })),
            "missing immutable command snapshot is rejected");
        Check(Throws<ArgumentNullException>(() =>
            RemoteCommandDispatch.EnqueueOrRollback(pending, "guest:a", first, null)),
            "missing reliable send delegate is rejected");
        Check(pending.Count == 1 && pending.ContainsKey("guest:other"),
            "invalid arguments cannot change an existing unrelated pending command");

        Check(Throws<ArgumentException>(() =>
            RemoteCommandDispatch.EnqueueOrRollback(pending, "guest:other", first,
                () => sends++)),
            "Add rejects a second in-flight command with the same key");
        Check(sends == 0 && pending["guest:other"].CommandId == "other",
            "duplicate key never sends or overwrites the first pending transaction");

        Exception original = null;
        try
        {
            RemoteCommandDispatch.EnqueueOrRollback(pending, "guest:a", first,
                () => throw new FormatException("synthetic serialization exception"));
        }
        catch (Exception ex) { original = ex; }
        Check(original is FormatException && original.Message.Contains("serialization") &&
            !pending.ContainsKey("guest:a") && pending.ContainsKey("guest:other"),
            "failure preserves original exception and removes only this failed pending record");

        // Real host callbacks can complete/requeue a command synchronously;
        // unwind of an old send must not erase that newer command.
        var newer = NewCommand("new-a");
        Check(Throws<IOException>(() =>
            RemoteCommandDispatch.EnqueueOrRollback(pending, "guest:a", first,
                () =>
                {
                    pending.Remove("guest:a");
                    pending.Add("guest:a", newer);
                    throw new IOException("late failure for old send");
                })),
            "old sender's thrown error still surfaces after a synchronous requeue");
        Check(ReferenceEquals(pending["guest:a"], newer) &&
            pending["guest:other"].CommandId == "other",
            "rollback uses reference identity and preserves the newly queued command");

        pending.Remove("guest:a");
        RemoteCommandDispatch.EnqueueOrRollback(pending, "guest:a", first,
            () => pending.Remove("guest:a"));
        Check(!pending.ContainsKey("guest:a"),
            "a synchronously confirmed command is not recreated by send's return");
    }

    private static void PublishFirst()
    {
        var steps = new List<string>();
        Check(Throws<ArgumentNullException>(() =>
            RemoteCommandDispatch.PublishBeforeReceipt(null, () => {})),
            "missing actual map capture is rejected");
        Check(Throws<ArgumentNullException>(() =>
            RemoteCommandDispatch.PublishBeforeReceipt(() => {}, null)),
            "missing execution receipt sender is rejected");
        Check(steps.Count == 0, "invalid callbacks did not publish nor send anything");

        RemoteCommandDispatch.PublishBeforeReceipt(
            () => steps.Add("native-map-published"),
            () => steps.Add("execution-claimed"));
        Check(steps.Count == 2 &&
            steps[0] == "native-map-published" &&
            steps[1] == "execution-claimed",
            "map is queued before execution receipt on a successful path");

        steps.Clear();
        Check(Throws<IOException>(() =>
            RemoteCommandDispatch.PublishBeforeReceipt(
                () => { steps.Add("map-attempt"); throw new IOException("map enqueue"); },
                () => steps.Add("false-success"))),
            "failed map publication propagates instead of returning false success");
        Check(steps.Count == 1 && steps[0] == "map-attempt",
            "missing map publication cannot enqueue execution success claim");

        steps.Clear();
        Check(Throws<IOException>(() =>
            RemoteCommandDispatch.PublishBeforeReceipt(
                () => steps.Add("map-queued"),
                () => { steps.Add("receipt-attempt"); throw new IOException("receipt queue"); })),
            "failure of execution receipt propagates after successful map enqueue");
        Check(steps.Count == 2 &&
            steps[0] == "map-queued" && steps[1] == "receipt-attempt",
            "receipt queue failure cannot retroactively discard already published map");
    }

    private static void ProductionShapedRetry()
    {
        var host = new FakeHost();
        var command = NewCommand("stable-transaction");
        host.Dispatch(command);
        Check(Throws<IOException>(() =>
            host.ExecuteAtSimulator(command, true, false)),
            "native execution succeeded but failed map enqueue cannot emit a receipt");
        Check(host.NativeExecutions["guest:stable-transaction"] == 1 &&
            host.Enqueued.Count == 2 &&
            host.Enqueued[1] == "map:guest:stable-transaction",
            "map delivery failure kept one native execution and no positive response");
        host.ExecuteAtSimulator(command, false, false);
        Check(host.NativeExecutions["guest:stable-transaction"] == 1 &&
            host.Enqueued[2] == "map:guest:stable-transaction" &&
            host.Enqueued[3] == "execution:guest:stable-transaction",
            "replay of the same identity publishes map first without rerunning native mutation");
    }

    private static void RandomizedFailures()
    {
        var random = new Random(20261018);
        for (int trial = 0; trial < 22000; trial++)
        {
            var host = new FakeHost();
            var command = NewCommand("stable-" + trial);
            host.FailSend = true;
            int unrelated = random.Next(0, 9);
            for (int i = 0; i < unrelated; i++)
                host.Pending.Add("guest:pending-" + i, NewCommand("pending-" + i));
            int throws = random.Next(1, 6);
            for (int t = 0; t < throws; t++)
            {
                Check(Throws<IOException>(() => host.Dispatch(command)),
                    "random queue failure remains observable " + trial);
                Check(host.Pending.Count == unrelated &&
                    !host.Pending.ContainsKey("guest:" + command.CommandId),
                    "random failed sends never strand a pending command " + trial);
            }
            host.FailSend = false;
            host.Dispatch(command);
            Check(host.Pending.Count == unrelated + 1 &&
                host.Pending.ContainsKey("guest:" + command.CommandId),
                "random retry sends same command only once after recovery " + trial);
            int nativeCalls = random.Next(1, 6);
            for (int i = 0; i < nativeCalls; i++)
            {
                Check(Throws<IOException>(() =>
                    host.ExecuteAtSimulator(command, true, false)),
                    "failed map-send never emits a false success receipt " + trial);
            }
            host.ExecuteAtSimulator(command, false, false);
            Check(host.NativeExecutions["guest:" + command.CommandId] == 1,
                "random transport retries never re-execute native command " + trial);
        }
    }

    private static int Main()
    {
        try
        {
            FailureRollback();
            IsolationAndReentrancy();
            PublishFirst();
            ProductionShapedRetry();
            RandomizedFailures();
            Console.WriteLine("PASS: " + checks +
                " production-linked remote command dispatch checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
