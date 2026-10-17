using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static readonly float[] Normal =
        { 1, 2, 3, 180, 10, -10, 0.25f, 0.7f, 0.3f, 0.9f, 20, 180 };
    private static bool LegacyTelemetry(float[] f)
    {
        // Exactly the former ValidateFightersState 12-field expression.
        return !new[] { f[0], f[1], f[2], f[3], f[4], f[5], f[6],
            f[7], f[8], f[9], f[10], f[11] }.Any(v => float.IsNaN(v) || float.IsInfinity(v));
    }
    private static bool CurrentTelemetry(float[] f)
    {
        return BattleFighterFiniteFields.ValidTelemetry(f[0], f[1], f[2], f[3],
            f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11]);
    }
    private static bool LegacyCells(Dictionary<int, float> cells)
    {
        // This includes the original null and 100000-item short circuit.
        return cells != null && cells.Count <= 100000 &&
            !cells.Values.Any(v => float.IsNaN(v) || v < 0 || v > 1);
    }
    private static bool CurrentCells(Dictionary<int, float> cells)
    {
        return cells != null && cells.Count <= 100000 &&
            BattleFighterFiniteFields.ValidCells(cells);
    }
    private static bool LegacyComplete(float[] f, Dictionary<int, float> cells)
    {
        return cells != null && cells.Count <= 100000
            && !cells.Values.Any(v => float.IsNaN(v) || v < 0 || v > 1)
            && LegacyTelemetry(f);
    }
    private static bool CurrentComplete(float[] f, Dictionary<int, float> cells)
    {
        return cells != null && cells.Count <= 100000
            && BattleFighterFiniteFields.ValidCells(cells)
            && CurrentTelemetry(f);
    }
    private static void Check(bool result, string reason)
    {
        if (!result) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static void Equal(float[] f, Dictionary<int, float> cells, string name)
    {
        Check(LegacyTelemetry(f) == CurrentTelemetry(f), name + " telemetry");
        Check(LegacyCells(cells) == CurrentCells(cells), name + " armor cells");
        Check(LegacyComplete(f, cells) == CurrentComplete(f, cells), name + " short circuit");
    }
    private static bool Throws<T>(Action body) where T : Exception
    {
        try { body(); return false; }
        catch (T) { return true; }
    }
    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 80; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Main()
    {
        try
        {
            float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            float[] edges = { 0, negativeZero, 1, -1, float.Epsilon, -float.Epsilon,
                float.MinValue, float.MaxValue, 1e-34f, -1e-34f, 180, 360,
                float.NaN, float.PositiveInfinity, float.NegativeInfinity };
            var validCells = new Dictionary<int, float>
                { { 0, 0 }, { 5, 0.7f }, { -100, 1 }, { 1000, negativeZero } };
            Equal(Normal, validCells, "ordinary native fighter telemetry and armor");
            Check(CurrentComplete(Normal, validCells), "well-formed fighter values pass");
            Check(Throws<ArgumentNullException>(() => BattleFighterFiniteFields.ValidCells(null)),
                "direct helper reports missing dictionary, before native caller's null guard");
            Equal(Normal, null, "null native armor dictionary");
            Equal(Normal, new Dictionary<int, float>(), "empty armor grid");

            // Exhaustively put IEEE special/boundary values in each of the
            // 12 independent fields, including flux/shield/velocity values.
            var sample = (float[])Normal.Clone();
            for (int field = 0; field < sample.Length; field++)
                for (int e = 0; e < edges.Length; e++)
                {
                    float previous = sample[field];
                    sample[field] = edges[e];
                    Equal(sample, validCells, "telemetry field " + field + " edge " + e);
                    sample[field] = previous;
                }
            // The armor dictionary has an intentionally different contract:
            // signed zero, 0 and 1 pass; NaN and values outside [0,1] fail.
            foreach (float value in edges)
            {
                var cells = new Dictionary<int, float> { { 0, value }, { 1, 0.5f } };
                Equal(Normal, cells, "armor fraction " + value);
            }
            Check(CurrentCells(new Dictionary<int, float> { { -1, negativeZero },
                { 100000, 1 } }), "armor keys are never interpreted as value bounds");
            Check(!CurrentCells(new Dictionary<int, float> { { 0, float.NaN } })
                && !CurrentCells(new Dictionary<int, float> { { 0, -float.Epsilon } })
                && !CurrentCells(new Dictionary<int, float> { { 0, 1.00001f } }),
                "bad armor values still reject before telemetry is examined");

            var maxCells = new Dictionary<int, float>(100001);
            for (int i = 0; i < 100001; i++) maxCells.Add(i, i % 2 == 0 ? 1f : 0f);
            Check(LegacyCells(maxCells) == CurrentCells(maxCells)
                && !CurrentCells(maxCells), "100001 native armor cells are rejected by the size gate");
            maxCells.Remove(100000);
            Check(LegacyCells(maxCells) == CurrentCells(maxCells)
                && CurrentCells(maxCells), "exactly 100000 valid armor cells remain accepted");

            // Corrupt values in multiple places at once to check that the
            // old LINQ short circuit and the linked scalar loops agree.
            for (int i = 0; i < edges.Length; i++)
                for (int j = 0; j < edges.Length; j++)
                {
                    sample[0] = edges[i]; sample[6] = edges[j];
                    sample[11] = edges[(i + j) % edges.Length];
                    var cells = new Dictionary<int, float> {
                        { 0, edges[(i + j) % edges.Length] }, { -1, 0.5f } };
                    Equal(sample, cells, "compound invalid coordinates / armor " + i + "/" + j);
                }

            var random = new Random(20261018);
            var bits = new byte[4];
            for (int iteration = 0; iteration < 6000; iteration++)
            {
                for (int k = 0; k < sample.Length; k++)
                {
                    random.NextBytes(bits);
                    sample[k] = BitConverter.ToSingle(bits, 0);
                }
                var cells = new Dictionary<int, float>();
                int count = random.Next(0, 24);
                for (int k = 0; k < count; k++)
                {
                    random.NextBytes(bits);
                    cells.Add(k - 8, BitConverter.ToSingle(bits, 0));
                }
                Equal(sample, cells, "random 32-bit fighter and armor payload " + iteration);
            }
            // Realistic authorized battle: 256 fighters, each with eight
            // bounded armor cell ratios and unchanged telemetry, repeated
            // across 700 confirmed checkpoints. No game CPU/FPS claims.
            const int fighters = 256, frames = 700;
            var stableCells = new Dictionary<int, float>();
            for (int i = 0; i < 8; i++) stableCells.Add(i, i / 8f);
            int accepted = 0;
            Action oldBatch = () =>
            {
                int passing = 0;
                for (int i = 0; i < fighters; i++)
                    if (LegacyComplete(Normal, stableCells)) passing++;
                accepted += passing;
            };
            Action newBatch = () =>
            {
                int passing = 0;
                for (int i = 0; i < fighters; i++)
                    if (CurrentComplete(Normal, stableCells)) passing++;
                accepted += passing;
            };
            long oldAllocated = Allocated(oldBatch, frames);
            long newAllocated = Allocated(newBatch, frames);
            GC.KeepAlive(accepted);
            Check(oldAllocated > 0 && newAllocated == 0,
                "the linked fighter validation no longer creates per-fighter LINQ/float arrays");
            Console.WriteLine("INFO: " + frames + " synthetic checkpoints × " + fighters +
                " fighters with 8 armor cells: old validation allocated " + oldAllocated +
                " B; linked production validation allocated " + newAllocated +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks + " production fighter numeric validation checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
