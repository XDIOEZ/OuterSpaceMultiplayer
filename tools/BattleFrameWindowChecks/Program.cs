using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Frame
    {
        internal string NativeJson;
        internal readonly long Revision;
        internal Frame(long revision, string nativeJson)
        { Revision = revision; NativeJson = nativeJson; }
    }
    private sealed class Window
    {
        internal readonly Dictionary<long, Frame> Frames = new Dictionary<long, Frame>();
        internal int Characters;
        internal void Add(Frame frame)
        {
            Frames.Add(frame.Revision, frame);
            if (frame.NativeJson != null) Characters += frame.NativeJson.Length;
        }
        internal Window Clone()
        {
            var clone = new Window();
            foreach (var pair in Frames) clone.Frames.Add(pair.Key, pair.Value);
            clone.Characters = Characters;
            return clone;
        }
    }

    private static readonly Func<Frame, int> NativeLength = f => f.NativeJson.Length;
    private static int checks;
    private static void Check(bool value, string why)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + why);
        checks++;
    }
    private static string Outcome(Action action)
    {
        try { action(); return "accepted"; }
        catch (Exception error) { return error.GetType().FullName + ": " + error.Message; }
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static int OldDiscard(Window window, long revision, Func<Frame, int> length = null)
    {
        // Pre-optimization production source in DiscardBattleFramesThrough.
        List<long> keys = window.Frames.Keys.Where(x => x <= revision).ToList();
        foreach (long key in keys)
        {
            window.Characters -= (length ?? NativeLength)(window.Frames[key]);
            window.Frames.Remove(key);
        }
        return keys.Count;
    }
    private static int NewDiscard(Window window, long revision, Func<Frame, int> length = null)
    {
        return BattleFrameWindowPruner.DiscardThrough(window.Frames, revision,
            length ?? NativeLength, ref window.Characters);
    }
    private static void CheckSame(Window first, Window second, string label)
    {
        Check(first.Characters == second.Characters,
            label + " exact buffered NativeJson character count");
        Check(first.Frames.Count == second.Frames.Count,
            label + " unchanged pending frame count");
        Check(first.Frames.Keys.SequenceEqual(second.Frames.Keys),
            label + " key order, including reused dictionary slots");
        foreach (var pair in first.Frames)
        {
            Frame other;
            Check(second.Frames.TryGetValue(pair.Key, out other) &&
                ReferenceEquals(pair.Value, other),
                label + " retains the same native frame reference at revision " + pair.Key);
        }
    }
    private static void Compare(Window source, long revision, string label)
    {
        Window legacy = source.Clone(), optimized = source.Clone();
        int oldRemoved = -1, newRemoved = -1;
        string oldOutcome = Outcome(() => oldRemoved = OldDiscard(legacy, revision));
        string newOutcome = Outcome(() => newRemoved = NewDiscard(optimized, revision));
        Check(oldOutcome == newOutcome,
            label + " same rejection/acceptance, old=" + oldOutcome + " new=" + newOutcome);
        if (oldOutcome == "accepted")
            Check(oldRemoved == newRemoved, label + " exact retired revisions");
        CheckSame(legacy, optimized, label);
    }
    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 120; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static int Main()
    {
        try
        {
            Check(Throws<ArgumentNullException>(() =>
            {
                int n = 0;
                BattleFrameWindowPruner.DiscardThrough<Frame>(null, 0, NativeLength, ref n);
            }), "null pending ACK dictionary is rejected");
            Check(Throws<ArgumentNullException>(() =>
            {
                int n = 0;
                BattleFrameWindowPruner.DiscardThrough(
                    new Dictionary<long, Frame>(), 0, null, ref n);
            }), "null NativeJson length accessor is rejected");

            var empty = new Window();
            Compare(empty, long.MinValue, "empty window, minimum ACK revision");
            Compare(empty, long.MaxValue, "empty window, maximum ACK revision");

            var exact = new Window();
            exact.Add(new Frame(10, "ABCDE"));
            Compare(exact, 9, "out-of-order ACK retains the one pending revision");
            Compare(exact, 10, "exact ACK retires one native frame");
            Compare(exact, 11, "higher ACK retires the same one native frame");
            Check(NewDiscard(exact, 9) == 0 && exact.Characters == 5 &&
                exact.Frames.Count == 1, "unconfirmed native JSON stays pending");
            Check(NewDiscard(exact, 10) == 1 && exact.Characters == 0 &&
                exact.Frames.Count == 0, "successful ACK releases final buffered frame");
            Check(NewDiscard(exact, 10) == 0,
                "duplicate ACK cannot subtract the same serialized bytes twice");

            long[] extremes = { long.MinValue, -1, 0, 1, 1L << 32,
                (1L << 32) | 1, long.MaxValue };
            var extremeWindow = new Window();
            for (int i = 0; i < extremes.Length; i++)
                extremeWindow.Add(new Frame(extremes[i], new string('x', 4 + i)));
            foreach (long revision in extremes)
                Compare(extremeWindow, revision,
                    "full signed 64-bit ACK and revision ordering");

            var outOfOrder = new Window();
            foreach (long revision in new long[] { 120, 5, 100, -4, 50, 0, 90, 10 })
                outOfOrder.Add(new Frame(revision, "native-frame-" + revision));
            foreach (long revision in new long[] { long.MinValue, -4, 0, 8, 50, 100, 120, long.MaxValue })
                Compare(outOfOrder, revision, "out-of-order dictionary and cumulative ACK " + revision);

            // The original method snapshots all matching keys before any
            // removal. An invalid NativeJson in the SECOND match must leave
            // that frame (and all later ones) pending after the first is gone.
            var corrupted = new Window();
            corrupted.Add(new Frame(1, "valid-1"));
            corrupted.Add(new Frame(2, null));
            corrupted.Add(new Frame(3, "valid-3"));
            Compare(corrupted, 3, "second ACK payload null preserves partial deletion");
            var firstCorrupt = new Window();
            firstCorrupt.Add(new Frame(1, null));
            firstCorrupt.Add(new Frame(2, "still-here"));
            Compare(firstCorrupt, 3, "first corrupt ACK payload prevents any removal");
            Compare(corrupted, 0, "corrupt future ACK frame is never inspected early");

            // A test accessor can fail mid-pruning. Verify the same order
            // and retained frames when prior acknowledgements have committed.
            var mixed = new Window();
            for (int i = 0; i < 15; i++)
                mixed.Add(new Frame(i, new string('A', i + 1)));
            var oldMixed = mixed.Clone();
            var newMixed = mixed.Clone();
            int oldVisits = 0, newVisits = 0;
            Func<Frame, int> failOld = frame =>
            {
                if (++oldVisits == 6) throw new InvalidOperationException("bad native ACK " + frame.Revision);
                return frame.NativeJson.Length;
            };
            Func<Frame, int> failNew = frame =>
            {
                if (++newVisits == 6) throw new InvalidOperationException("bad native ACK " + frame.Revision);
                return frame.NativeJson.Length;
            };
            string oldFailed = Outcome(() => OldDiscard(oldMixed, 8, failOld));
            string newFailed = Outcome(() => NewDiscard(newMixed, 8, failNew));
            Check(oldFailed == newFailed && oldVisits == newVisits &&
                oldVisits == 6, "mid-cleanup callback failure and exception text match exactly");
            CheckSame(oldMixed, newMixed, "mid-cleanup ACK failure keeps prior removal effects");
            Check(oldMixed.Frames.ContainsKey(5) && !oldMixed.Frames.ContainsKey(4),
                "failing revision remains pending and earlier ACKed revisions are removed");

            // Many different battle sessions may be in flight. Pruning a
            // window cannot retire frames belonging to a different battle.
            var battles = new Dictionary<string, Window>();
            battles.Add("battle-A", outOfOrder.Clone());
            battles.Add("battle-B", outOfOrder.Clone());
            NewDiscard(battles["battle-A"], 70);
            Check(battles["battle-B"].Frames.Count == outOfOrder.Frames.Count,
                "acknowledgement for one battle never touches another session window");
            CheckSame(battles["battle-B"], outOfOrder,
                "other player's pending battle revisions stay unchanged");
            NewDiscard(battles["battle-A"], long.MaxValue);
            if (battles["battle-A"].Frames.Count == 0) battles.Remove("battle-A");
            Check(!battles.ContainsKey("battle-A") && battles.ContainsKey("battle-B"),
                "final ACK removes the empty battle window without losing another");

            // Older ACKs and newly inserted revisions can reuse Dictionary
            // slots. Compare exact key iteration order as well as final state.
            var random = new Random(20261018);
            var streaming = new Window();
            for (int frame = 0; frame < 6500; frame++)
            {
                int change = random.Next(0, 6);
                if (change < 3)
                {
                    long uid = random.Next(0, 5) == 0
                        ? extremes[random.Next(extremes.Length)]
                        : random.Next(-160, 350);
                    if (!streaming.Frames.ContainsKey(uid))
                        streaming.Add(new Frame(uid, new string('p', random.Next(0, 65))));
                }
                if (change == 3 && streaming.Frames.Count != 0)
                {
                    long key = streaming.Frames.Keys.ElementAt(random.Next(streaming.Frames.Count));
                    streaming.Frames.Remove(key);
                }
                long ack = random.Next(0, 8) == 0
                    ? extremes[random.Next(extremes.Length)] : random.Next(-150, 345);
                Compare(streaming, ack, "randomized pending ACK queue " + frame);
                if (random.Next(0, 2) == 0)
                {
                    // Apply the same deletion for the persistent test source,
                    // preserving its per-frame partial byte-count invariant.
                    NewDiscard(streaming, ack);
                }
            }

            const int frames = 12000;
            var payload = new Frame(42, "native-json-payload");
            var oldSingle = new Window();
            var newSingle = new Window();
            Action oldAck = () =>
            {
                oldSingle.Frames[42] = payload;
                oldSingle.Characters = payload.NativeJson.Length;
                OldDiscard(oldSingle, 42);
            };
            Action newAck = () =>
            {
                newSingle.Frames[42] = payload;
                newSingle.Characters = payload.NativeJson.Length;
                NewDiscard(newSingle, 42);
            };
            long oldBytes = Allocated(oldAck, frames);
            long newBytes = Allocated(newAck, frames);
            Check(oldBytes > 0 && newBytes == 0,
                "common single-frame ACK removes no LINQ iterator, closure or temporary list");
            Console.WriteLine("INFO: " + frames + " single-frame ACKs with stable payload: old Where/ToList " +
                oldBytes + " B, linked production ACK pruner " + newBytes +
                " B (managed allocations after warmup)");

            var noMatches = new Window();
            noMatches.Add(new Frame(999, "retained"));
            long oldNoHit = Allocated(() => OldDiscard(noMatches, 0), frames);
            long newNoHit = Allocated(() => NewDiscard(noMatches, 0), frames);
            Check(oldNoHit > 0 && newNoHit == 0,
                "out-of-order ACK with no matching revision allocates no temporary list");
            Console.WriteLine("INFO: " + frames + " unmatched ACKs: old " +
                oldNoHit + " B, linked production pruner " + newNoHit + " B");
            Console.WriteLine("PASS: " + checks +
                " production-linked battle frame ACK/window pruning checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
