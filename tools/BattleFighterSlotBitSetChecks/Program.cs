using System;
using System.Collections.Generic;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Slot
    {
        internal int Index;
        internal int Status;
        internal float Progress, Remaining;
        internal long Fighter;

        internal Slot(int index, int status = 0, float progress = 0,
            float remaining = 0, long fighter = -1)
        {
            Index = index;
            Status = status;
            Progress = progress;
            Remaining = remaining;
            Fighter = fighter;
        }
    }
    private static int checks;
    private const int Active = 2;
    private const string Error = "玩家舰载机槽位状态无效。";

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }

    // Exact short-circuit order of the previous production wing validator.
    private static string OldValidate(IList<Slot> slots)
    {
        var identities = new HashSet<int>();
        foreach (Slot slot in slots)
            if (slot == null || !identities.Add(slot.Index)
                || slot.Index < 0 || slot.Index > 255
                || slot.Status < 0 || slot.Status > 3
                || float.IsNaN(slot.Progress) || float.IsInfinity(slot.Progress)
                || slot.Progress < 0 || slot.Progress > 1
                || float.IsNaN(slot.Remaining) || float.IsInfinity(slot.Remaining)
                || slot.Remaining < 0 || slot.Fighter < -1
                || slot.Fighter >= 0 && slot.Status != Active)
                return Error;
        return "accepted";
    }

    // Source-equivalent new wing loop using the REAL production helper,
    // including the range/status/progress/active guards left in GameBridge.
    private static string NewValidate(IList<Slot> slots)
    {
        var identities = new BattleFighterSlotBitSet();
        foreach (Slot slot in slots)
            if (slot == null || !identities.TryAdd(slot.Index)
                || slot.Index < 0 || slot.Index > 255
                || slot.Status < 0 || slot.Status > 3
                || float.IsNaN(slot.Progress) || float.IsInfinity(slot.Progress)
                || slot.Progress < 0 || slot.Progress > 1
                || float.IsNaN(slot.Remaining) || float.IsInfinity(slot.Remaining)
                || slot.Remaining < 0 || slot.Fighter < -1
                || slot.Fighter >= 0 && slot.Status != Active)
                return Error;
        return "accepted";
    }

    private static void Same(IList<Slot> slots, string reason)
    {
        string original = OldValidate(slots);
        string current = NewValidate(slots);
        Check(original == current,
            reason + ", legacy=" + original + ", production=" + current);
    }

    private static long Measure(Action action, int count)
    {
        for (int i = 0; i < 100; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static void TestIndividualBitPositions()
    {
        int[] boundaries = { 0, 1, 62, 63, 64, 65, 126, 127,
            128, 129, 190, 191, 192, 193, 254, 255 };
        var ids = new BattleFighterSlotBitSet();
        foreach (int id in boundaries)
        {
            Check(ids.TryAdd(id), "first use of boundary slot " + id);
            Check(!ids.TryAdd(id), "duplicate boundary slot " + id);
        }
        // A range violation is rejected by the caller's following
        // slot.Index < 0 || slot.Index > 255 check, not by the bitset itself.
        Check(ids.TryAdd(-1) && ids.TryAdd(256) &&
            ids.TryAdd(int.MinValue) && ids.TryAdd(int.MaxValue),
            "out-of-range candidates fall through to the unchanged range guard");
        Check(!ids.TryAdd(0) && !ids.TryAdd(255),
            "invalid candidates never corrupt valid slot occupancy bits");
        var all = new BattleFighterSlotBitSet();
        for (int i = 0; i < 256; i++)
            Check(all.TryAdd(i), "all 256 legal native fighter slots can coexist: " + i);
        for (int i = 0; i < 256; i++)
            Check(!all.TryAdd(i), "all 256 legal native fighter slots reject duplicates: " + i);
        var fresh = new BattleFighterSlotBitSet();
        Check(fresh.TryAdd(0) && fresh.TryAdd(255),
            "new wing has independent slot occupancy with no prior-frame state");
        Check(!all.TryAdd(0), "resetting another wing cannot modify the previous wing");
    }

    public static int Main()
    {
        try
        {
            TestIndividualBitPositions();
            Same(new Slot[0], "empty native wing");
            Same(new Slot[] { new Slot(0) }, "single slot 0");
            Same(new Slot[] { new Slot(255) }, "single slot 255");
            Same(new Slot[] { new Slot(255), new Slot(0) },
                "opposite bitset ends in one wing");
            Same(new Slot[] { new Slot(63), new Slot(64),
                new Slot(127), new Slot(128), new Slot(191), new Slot(192) },
                "crossing all four 64-bit boundaries");
            Same(new Slot[] { new Slot(63), new Slot(64), new Slot(63) },
                "duplicate after crossing mask boundary");
            Same(new Slot[] { null }, "missing wing slot object");
            Same(new Slot[] { new Slot(2), null, new Slot(3) },
                "missing slot after valid earlier slot");
            foreach (int id in new[] { int.MinValue, -256, -1, 256, 257, int.MaxValue })
                Same(new Slot[] { new Slot(4), new Slot(id), new Slot(4) },
                    "invalid slot range has priority before following duplicate: " + id);

            for (int count = 0; count <= 256; count++)
            {
                var slots = new List<Slot>(count);
                for (int i = 0; i < count; i++)
                    slots.Add(new Slot(i, Active, 0.5f, 1, i));
                Same(slots, "dense valid wing with " + count + " slots");
                if (count > 1)
                {
                    slots[count - 1] = new Slot(count / 2, Active, 0.5f, 1, count - 1);
                    Same(slots, "dense wing with late duplicate at " + count);
                }
            }

            var metadata = new Slot[] {
                new Slot(7, -1), new Slot(7, 4), new Slot(7, 0, float.NaN),
                new Slot(7, 0, float.PositiveInfinity),
                new Slot(7, 0, float.NegativeInfinity),
                new Slot(7, 0, -0.01f), new Slot(7, 0, 1.01f),
                new Slot(7, 0, 0, float.NaN),
                new Slot(7, 0, 0, float.PositiveInfinity),
                new Slot(7, 0, 0, float.NegativeInfinity),
                new Slot(7, 0, 0, -0.01f),
                new Slot(7, 0, 0, 0, -2),
                new Slot(7, 0, 0, 0, 42),
                new Slot(7, Active, 0, 0, 42),
                new Slot(7, 0, -0.0f, -0.0f, -1)
            };
            foreach (Slot slot in metadata)
                Same(new Slot[] { slot }, "old error priority for wing slot metadata");
            foreach (Slot slot in metadata)
                Same(new Slot[] { new Slot(6), slot },
                    "metadata error is checked after an unrelated legal slot");

            // Every bit position and every possible conflicting second
            // position: also tests different masks do not alias.
            for (int first = 0; first < 256; first++)
                for (int second = 0; second < 256; second++)
                {
                    var old = new HashSet<int>();
                    var modern = new BattleFighterSlotBitSet();
                    Check(old.Add(first) == modern.TryAdd(first) &&
                        old.Add(second) == modern.TryAdd(second),
                        "pairwise 256-slot occupancy parity " + first + "/" + second);
                }

            var random = new Random(20261018);
            for (int frame = 0; frame < 20000; frame++)
            {
                int n = random.Next(0, 257);
                var slots = new Slot[n];
                for (int i = 0; i < n; i++)
                {
                    int index = random.Next(-16, 280);
                    if (random.Next(0, 18) == 0) index = random.Next(0, 2) == 0
                        ? int.MinValue : int.MaxValue;
                    int status = random.Next(-1, 5);
                    long fighter = random.Next(0, 5) == 0 ? random.Next(0, 3000) : -1;
                    float progress = random.Next(0, 70) == 0
                        ? float.NaN : (float)random.NextDouble();
                    float remaining = random.Next(0, 70) == 0
                        ? float.PositiveInfinity : (float)random.NextDouble();
                    slots[i] = random.Next(0, 120) == 0 ? null :
                        new Slot(index, status, progress, remaining, fighter);
                }
                Same(slots, "random live wing snapshot " + frame);
            }

            // Benchmark only per-wing duplicate detection and temporary
            // collections, not game DTO parsing, Unity hooks or simulation.
            const int wings = 128, slotsPerWing = 12, checkpoints = 4500;
            var allWingIds = new int[wings][];
            for (int w = 0; w < wings; w++)
            {
                allWingIds[w] = new int[slotsPerWing];
                for (int i = 0; i < slotsPerWing; i++)
                    allWingIds[w][i] = i + (w % 4) * 64;
            }
            Action oldValidation = () =>
            {
                foreach (int[] wingSlots in allWingIds)
                {
                    var seen = new HashSet<int>();
                    foreach (int index in wingSlots)
                        if (!seen.Add(index)) throw new InvalidOperationException("old duplicate slot");
                }
            };
            Action newValidation = () =>
            {
                foreach (int[] wingSlots in allWingIds)
                {
                    var seen = new BattleFighterSlotBitSet();
                    foreach (int index in wingSlots)
                        if (!seen.TryAdd(index)) throw new InvalidOperationException("new duplicate slot");
                }
            };
            long oldBytes = Measure(oldValidation, checkpoints);
            long newBytes = Measure(newValidation, checkpoints);
            Check(oldBytes > newBytes && newBytes == 0,
                "linked production four-mask slot validator removes per-wing HashSet allocations");
            Console.WriteLine("INFO: " + checkpoints + " synthetic battle checkpoints × " +
                wings + " native wings × " + slotsPerWing + " valid slots: old HashSet<int> " +
                oldBytes + " B; linked production bitset " + newBytes +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks + " linked native fighter wing slot bitset checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
