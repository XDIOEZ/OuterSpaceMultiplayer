using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Frame
    {
        internal readonly long BaseRevision, Revision;
        internal readonly string NativeJson;
        internal Frame(long origin, long revision, int length = 16)
        {
            BaseRevision = origin;
            Revision = revision;
            NativeJson = new string('x', length);
        }
    }
    private sealed class Window
    {
        internal readonly Dictionary<long, Frame> Frames = new Dictionary<long, Frame>();
        internal long RecordRevision;
        internal int Characters;
    }

    private static int checks;
    private static long benchSink;
    private static readonly Func<Frame, int> Length = frame => frame.NativeJson.Length;
    private static void Check(bool valid, string label)
    {
        if (!valid) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static string Result(Func<long> call, out long revision)
    {
        revision = 0;
        try { revision = call(); return "accepted"; }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static string OldBase(Window window, out long latest)
    {
        return Result(() => window.Frames.Count == 0
            ? window.RecordRevision : window.Frames.Keys.Max(), out latest);
    }
    private static string NewBase(Window window, out long latest)
    {
        return Result(() => window.Frames.Count == 0
            ? window.RecordRevision : BattleFrameRevisionMax.Highest(window.Frames), out latest);
    }

    private static void CompareMax(Dictionary<long, Frame> pending, string label)
    {
        long expected = 0, actual = 0;
        string old = Result(() => pending.Keys.Max(), out expected);
        string modern = Result(() => BattleFrameRevisionMax.Highest(pending), out actual);
        Check(old == modern && expected == actual,
            label + " old=" + old + " new=" + modern +
            " old revision=" + expected + " new revision=" + actual);
    }
    private static void Same(Window left, Window right, string label)
    {
        Check(left.RecordRevision == right.RecordRevision, label + " current confirmed revision");
        Check(left.Characters == right.Characters, label + " bounded frame characters");
        Check(left.Frames.Count == right.Frames.Count, label + " pending frame count");
        Check(left.Frames.Keys.SequenceEqual(right.Frames.Keys), label + " native send order");
        foreach (var pair in left.Frames)
        {
            Frame other;
            Check(right.Frames.TryGetValue(pair.Key, out other) &&
                pair.Value.BaseRevision == other.BaseRevision &&
                pair.Value.Revision == other.Revision &&
                pair.Value.NativeJson == other.NativeJson,
                label + " native checkpoint fields " + pair.Key);
        }
        long expected = 0, actual = 0;
        string old = OldBase(left, out expected);
        string modern = NewBase(right, out actual);
        Check(old == modern && expected == actual,
            label + " next simulator BaseRevision");
    }
    private static void Send(Window w, bool modern, int size)
    {
        // Same production backpressure: a failed Send can keep an outstanding
        // window, while no more than twelve frames may be queued.
        if (w.Frames.Count >= 12 || w.Characters > 8 * 1024 * 1024) return;
        long highest = w.Frames.Count == 0 ? w.RecordRevision :
            modern ? BattleFrameRevisionMax.Highest(w.Frames) : w.Frames.Keys.Max();
        long next = checked(highest + 1);
        var frame = new Frame(highest, next, size);
        w.Frames.Add(next, frame);
        w.Characters += size;
    }
    private static void Ack(Window w, long revision)
    {
        // Production removes the ACKed and older frames without introducing
        // a cached highest revision that could become stale.
        BattleFrameWindowPruner.DiscardThrough(w.Frames, revision, Length, ref w.Characters);
        if (revision > w.RecordRevision) w.RecordRevision = revision;
    }
    private static void CompareSend(Window old, Window current, int size, string label)
    {
        string oldError = null, newError = null;
        try { Send(old, false, size); }
        catch (Exception error) { oldError = error.GetType().Name + ": " + error.Message; }
        try { Send(current, true, size); }
        catch (Exception error) { newError = error.GetType().Name + ": " + error.Message; }
        Check(oldError == newError, label + " send failure/checked overflow match");
        Same(old, current, label);
    }
    private static long Measure(Action work, int count)
    {
        for (int i = 0; i < 100; i++) work();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Main()
    {
        try
        {
            var empty = new Dictionary<long, Frame>();
            CompareMax(empty, "empty collection retains Enumerable.Max error");
            Check(BattleFrameRevisionMax.Highest(new Dictionary<long, Frame> {
                { long.MinValue, null } }) == long.MinValue,
                "minimum long revision is not mistaken for an empty dictionary");
            Check(BattleFrameRevisionMax.Highest(new Dictionary<long, Frame> {
                { long.MinValue, null }, { long.MaxValue, null }, { 0, null } }) == long.MaxValue,
                "highest revision considers the complete signed 64-bit range");
            var extremes = new long[] { long.MinValue, long.MinValue + 1, -42,
                -1, 0, 1, 42, (1L << 32), long.MaxValue - 1, long.MaxValue };
            var random = new Random(20261018);
            for (int trial = 0; trial < 16000; trial++)
            {
                var test = new Dictionary<long, Frame>();
                int count = random.Next(0, 14);
                for (int i = 0; i < count; i++)
                {
                    long revision = random.Next(0, 3) == 0 ?
                        extremes[random.Next(extremes.Length)] :
                        ((long)random.Next(-100000, 100000) << 32) | (uint)random.Next();
                    if (!test.ContainsKey(revision)) test.Add(revision, new Frame(0, revision));
                }
                CompareMax(test, "random revision set " + trial);
            }

            // The real simulator allocates a new window only for a battle and
            // keeps it across up to twelve unacknowledged checkpoints. ACK,
            // out-of-order delivery and resumption must preserve BaseRevision.
            var prior = new Window { RecordRevision = 9 };
            var updated = new Window { RecordRevision = 9 };
            CompareSend(prior, updated, 100, "first checkpoint uses confirmed baseline");
            CompareSend(prior, updated, 100, "second in-flight checkpoint chains from the first");
            CompareSend(prior, updated, 100, "third in-flight checkpoint");
            Ack(prior, 11); Ack(updated, 11);
            Same(prior, updated, "ACK removes the first two frames");
            Check(prior.Frames.Count == 1 &&
                BattleFrameRevisionMax.Highest(updated.Frames) == 12,
                "later unconfirmed checkpoint remains the next parent");
            Ack(prior, 12); Ack(updated, 12);
            CompareSend(prior, updated, 100, "empty pending window returns confirmed host revision");
            for (int i = 0; i < 20; i++)
                CompareSend(prior, updated, 100, "bounded 12-frame window " + i);
            Check(prior.Frames.Count == 12 && updated.Frames.Count == 12,
                "saturated sender cannot schedule an extra native checkpoint");

            for (int loop = 0; loop < 12000; loop++)
            {
                if (loop % 503 == 0)
                {
                    prior = new Window { RecordRevision = random.Next(0, 1000) };
                    updated = new Window { RecordRevision = prior.RecordRevision };
                }
                int mode = random.Next(0, 6);
                if (mode <= 3) CompareSend(prior, updated, random.Next(1, 250),
                    "random bounded battle send " + loop);
                else
                {
                    long ack = prior.RecordRevision + random.Next(-2, 15);
                    Ack(prior, ack); Ack(updated, ack);
                    Same(prior, updated, "random ACK and pending-frame retirement " + loop);
                }
            }
            var overflow = new Window { RecordRevision = long.MaxValue };
            var overflowNew = new Window { RecordRevision = long.MaxValue };
            CompareSend(overflow, overflowNew, 10,
                "checked new checkpoint revision cannot overflow the 64-bit wire sequence");
            overflow.Frames.Add(long.MaxValue, new Frame(0, long.MaxValue));
            overflowNew.Frames.Add(long.MaxValue, new Frame(0, long.MaxValue));
            CompareSend(overflow, overflowNew, 10,
                "in-flight highest long UID/revision overflows identically");

            const int frames = 200000, pending = 12;
            var dense = new Dictionary<long, Frame>();
            for (long revision = 100; revision < 100 + pending; revision++)
                dense.Add(revision, new Frame(revision - 1, revision));
            Action oldWork = () =>
            {
                benchSink = dense.Keys.Max();
                if (benchSink != 111) throw new Exception("old max lost revision");
            };
            Action newWork = () =>
            {
                benchSink = BattleFrameRevisionMax.Highest(dense);
                if (benchSink != 111) throw new Exception("new max lost revision");
            };
            long oldBytes = Measure(oldWork, frames);
            long newBytes = Measure(newWork, frames);
            Check(oldBytes > newBytes,
                "live pending revision lookup avoids LINQ/boxed-enumerator allocations");
            Console.WriteLine("INFO: " + frames + " highest-revision queries on " +
                pending + " in-flight checkpoints: Enumerable.Max " + oldBytes +
                " B vs linked production direct scan " + newBytes +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " production-linked pending battle frame revision checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
