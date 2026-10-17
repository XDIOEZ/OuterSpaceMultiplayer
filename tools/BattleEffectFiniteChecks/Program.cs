using System;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    // The exact numeric predicates from the pre-optimization production code.
    // Keep these independent from the real helper under test.
    private static bool LegacyPose(float[] f)
    {
        return !new[] { f[0], f[1], f[2], f[3], f[4], f[5], f[6] }
            .Any(v => float.IsNaN(v) || float.IsInfinity(v))
            && !(Math.Abs(f[4]) > 10000 || Math.Abs(f[5]) > 10000 || Math.Abs(f[6]) > 10000);
    }
    private static bool LegacyBeam(float[] f)
    {
        return !new[] { f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7] }
            .Any(v => float.IsNaN(v) || float.IsInfinity(v))
            && !(f[6] < 0 || f[7] < 0);
    }
    private static bool NewPose(float[] f)
    { return BattleEffectFiniteFields.ValidPose(f[0], f[1], f[2], f[3], f[4], f[5], f[6]); }
    private static bool NewBeam(float[] f)
    { return BattleEffectFiniteFields.ValidBeam(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7]); }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static void CheckPair(float[] pose, float[] beam, string label)
    {
        Check(LegacyPose(pose) == NewPose(pose), label + " pose");
        Check(LegacyBeam(beam) == NewBeam(beam), label + " beam");
    }
    private static long Allocations(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action();
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
                10000, -10000, 10000.01f, -10000.01f, 9999.99f, -9999.99f,
                float.MaxValue, float.MinValue, float.NaN, float.PositiveInfinity,
                float.NegativeInfinity, 1.0e-35f, -1.0e-35f };
            float[] pose = { 1, 2, 3, 90, 1, 1, 1 };
            float[] beam = { 1, 2, 3, 4, 5, 6, 0.5f, 0 };
            Check(NewPose(pose) && NewBeam(beam), "ordinary finite effect values accepted");
            for (int k = 0; k < pose.Length; k++)
                foreach (float edge in edges)
                {
                    float original = pose[k];
                    pose[k] = edge;
                    Check(LegacyPose(pose) == NewPose(pose), "pose edge " + k);
                    pose[k] = original;
                }
            for (int k = 0; k < beam.Length; k++)
                foreach (float edge in edges)
                {
                    float original = beam[k];
                    beam[k] = edge;
                    Check(LegacyBeam(beam) == NewBeam(beam), "beam edge " + k);
                    beam[k] = original;
                }
            Check(NewPose(new float[] { 0, 0, 0, 0, 10000, -10000, negativeZero }),
                "inclusive positive/negative scale limits");
            Check(!NewPose(new float[] { 0, 0, 0, 0, 10000.01f, 0, 0 })
                && !NewPose(new float[] { 0, 0, 0, 0, 0, -10000.01f, 0 }),
                "oversized positive and negative scales still fail");
            Check(NewBeam(new float[] { 0, 0, 0, 0, 0, 0, negativeZero, 0 }),
                "negative zero is still a valid beam width");
            Check(!NewBeam(new float[] { 0, 0, 0, 0, 0, 0, -float.Epsilon, 0 })
                && !NewBeam(new float[] { 0, 0, 0, 0, 0, 0, 0, -float.Epsilon }),
                "negative beam width at either endpoint still fails");
            for (int i = 0; i < edges.Length; i++)
                for (int j = 0; j < edges.Length; j++)
                {
                    pose[0] = edges[i]; pose[3] = edges[j]; pose[5] = edges[(i + j) % edges.Length];
                    beam[0] = edges[i]; beam[6] = edges[j]; beam[7] = edges[(i + j) % edges.Length];
                    CheckPair(pose, beam, "compound float edge " + i + "/" + j);
                }
            var random = new Random(20261018);
            var bits = new byte[4];
            // Random raw IEEE-754 patterns exercise subnormals, extreme
            // finite values and varied NaN payloads in every position.
            for (int i = 0; i < 10000; i++)
            {
                for (int j = 0; j < pose.Length; j++)
                {
                    random.NextBytes(bits);
                    pose[j] = BitConverter.ToSingle(bits, 0);
                }
                for (int j = 0; j < beam.Length; j++)
                {
                    random.NextBytes(bits);
                    beam[j] = BitConverter.ToSingle(bits, 0);
                }
                CheckPair(pose, beam, "random float encoding " + i);
            }

            const int posesPerFrame = 256, beamsPerFrame = 32, frames = 1000;
            float[] stablePose = { 1, 2, 3, 90, 1, 1, 1 };
            float[] stableBeam = { 1, 2, 3, 4, 5, 6, 0.5f, 0.4f };
            int sink = 0;
            Action oldBatch = () =>
            {
                int accepted = 0;
                for (int i = 0; i < posesPerFrame; i++) if (LegacyPose(stablePose)) accepted++;
                for (int i = 0; i < beamsPerFrame; i++) if (LegacyBeam(stableBeam)) accepted++;
                sink += accepted;
            };
            Action newBatch = () =>
            {
                int accepted = 0;
                for (int i = 0; i < posesPerFrame; i++) if (NewPose(stablePose)) accepted++;
                for (int i = 0; i < beamsPerFrame; i++) if (NewBeam(stableBeam)) accepted++;
                sink += accepted;
            };
            long legacyAllocated = Allocations(oldBatch, frames);
            long optimizedAllocated = Allocations(newBatch, frames);
            GC.KeepAlive(sink);
            Check(legacyAllocated > 0 && optimizedAllocated == 0,
                "production fixed-argument numeric check avoids arrays after JIT warmup");
            Console.WriteLine("INFO: " + frames + " synthetic frames with " + posesPerFrame +
                " effect poses and " + beamsPerFrame + " beam lines: legacy " +
                legacyAllocated + " B, production fixed-argument check " +
                optimizedAllocated + " B allocated");
            Console.WriteLine("PASS: " + checks + " production finite effect validation checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
