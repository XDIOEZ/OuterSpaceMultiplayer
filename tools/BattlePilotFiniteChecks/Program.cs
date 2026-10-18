using System;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Control
    {
        internal float X, Y, Z, Throttle, Turn, Strafe;
        internal int Index;
    }

    private static int checks;

    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }

    // Exact pre-optimization predicate from ExecutePlayerFleetBattleInput.
    // Keep the new float[]/Enumerable.Any on purpose as a reference.
    private static bool PreviousFinite(Control c)
    {
        return !new[] { c.X, c.Y, c.Z, c.Throttle, c.Turn, c.Strafe }
            .Any(value => float.IsNaN(value) || float.IsInfinity(value));
    }

    private static bool CurrentFinite(Control c)
    {
        return BattlePilotFiniteFields.Valid(c.X, c.Y, c.Z, c.Throttle, c.Turn, c.Strafe);
    }

    private static string Result(Action action)
    {
        try { action(); return "accepted"; }
        catch (Exception error)
        { return error.GetType().Name + ": " + error.Message; }
    }

    // Only the six-field finite guard and the unchanged native bounds/action
    // checks are modeled here. Authority, battle epoch and ship ownership
    // are deliberately untouched and remain in the original GameBridge.
    private static void ValidatePrevious(Control c, string operation)
    {
        if (c == null || !PreviousFinite(c)
            || Math.Abs(c.Throttle) > 1 || Math.Abs(c.Turn) > 1 ||
               Math.Abs(c.Strafe) > 1 || c.Index < 0 || c.Index > 64 ||
               operation == "Wing" && c.Index > 2)
            throw new InvalidOperationException("舰战控制参数无效。");
    }

    private static void ValidateCurrent(Control c, string operation)
    {
        if (c == null || !CurrentFinite(c)
            || Math.Abs(c.Throttle) > 1 || Math.Abs(c.Turn) > 1 ||
               Math.Abs(c.Strafe) > 1 || c.Index < 0 || c.Index > 64 ||
               operation == "Wing" && c.Index > 2)
            throw new InvalidOperationException("舰战控制参数无效。");
    }

    private static void Compare(Control c, string operation, string description)
    {
        if (c != null)
            Check(PreviousFinite(c) == CurrentFinite(c),
                description + ": exact IEEE-754 finite result");
        string old = Result(() => ValidatePrevious(c, operation));
        string current = Result(() => ValidateCurrent(c, operation));
        Check(old == current,
            description + ": accepted/exception text old=" + old + ", new=" + current);
    }

    private static Control Clone(Control c)
    {
        return new Control {
            X = c.X, Y = c.Y, Z = c.Z,
            Throttle = c.Throttle, Turn = c.Turn, Strafe = c.Strafe,
            Index = c.Index
        };
    }

    private static long Measure(Action action, int count)
    {
        for (int i = 0; i < 150; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    public static int Main()
    {
        try
        {
            Compare(null, "Pilot", "null parsed native pilot control");
            Compare(new Control(), "Pilot", "all fields zero");
            Compare(new Control { X = 22, Y = -30, Z = 550,
                Throttle = -1, Turn = 1, Strafe = 0, Index = 64 },
                "Pilot", "position unbounded, signed control inputs at limits");
            Compare(new Control { X = float.MaxValue, Y = float.MinValue,
                Z = float.Epsilon, Throttle = 1, Turn = -1, Strafe = 1 },
                "Pilot", "extreme finite telemetry stays accepted");
            Compare(new Control { Throttle = 1.00001f },
                "Pilot", "existing throttle magnitude guard remains after finite check");
            Compare(new Control { Turn = -1.00001f },
                "Pilot", "turn magnitude guard still rejects");
            Compare(new Control { Strafe = 1.00001f },
                "Pilot", "strafe magnitude guard still rejects");
            Compare(new Control { Index = -1 }, "Pilot",
                "negative action index unchanged");
            Compare(new Control { Index = 65 }, "Pilot",
                "oversized action index unchanged");
            Compare(new Control { Index = 2 }, "Wing",
                "maximum wing index accepted");
            Compare(new Control { Index = 3 }, "Wing",
                "wing index greater than two rejected");
            Compare(new Control { Index = 64 }, "Group",
                "high non-wing group index remains allowed");

            // Every original field rejects every payload NaN or +/-infinity
            // and accepts signed zero, subnormals and maximal finite values.
            int[] bits = {
                0, unchecked((int)0x80000000u), 1, unchecked((int)0x80000001u),
                unchecked((int)0x007fffffu), unchecked((int)0x807fffffu),
                unchecked((int)0x7f7fffffu), unchecked((int)0xff7fffffu),
                unchecked((int)0x7f800000u), unchecked((int)0xff800000u),
                unchecked((int)0x7fc00000u), unchecked((int)0xffc00000u),
                unchecked((int)0x7f800001u), unchecked((int)0xff800001u),
                unchecked((int)0x7fffffffu), unchecked((int)0xffffffffu)
            };
            foreach (int raw in bits)
                for (int index = 0; index < 6; index++)
                {
                    float value = BitConverter.Int32BitsToSingle(raw);
                    var sample = new Control();
                    if (index == 0) sample.X = value;
                    else if (index == 1) sample.Y = value;
                    else if (index == 2) sample.Z = value;
                    else if (index == 3) sample.Throttle = value;
                    else if (index == 4) sample.Turn = value;
                    else sample.Strafe = value;
                    Compare(sample, "Pilot", "bit-level field=" + index + " raw=" + raw);
                }

            // The six fields are independent. Other input validation must
            // reject valid-but-out-of-range controls at its original point.
            var random = new Random(20261018);
            byte[] rawValues = new byte[24];
            for (int i = 0; i < 120000; i++)
            {
                random.NextBytes(rawValues);
                var c = new Control {
                    X = BitConverter.ToSingle(rawValues, 0),
                    Y = BitConverter.ToSingle(rawValues, 4),
                    Z = BitConverter.ToSingle(rawValues, 8),
                    Throttle = BitConverter.ToSingle(rawValues, 12),
                    Turn = BitConverter.ToSingle(rawValues, 16),
                    Strafe = BitConverter.ToSingle(rawValues, 20),
                    Index = random.Next(-2, 68)
                };
                string operation = i % 7 == 0 ? "Wing" :
                    i % 3 == 0 ? "Group" : "Pilot";
                Compare(c, operation, "random native pilot input " + i);
            }

            // Also exercise typical (already bounded) controls with random
            // authoritative positions and all three axis controls.
            for (int i = 0; i < 16000; i++)
            {
                var c = new Control {
                    X = (float)(random.NextDouble() * 1e7 - 5e6),
                    Y = (float)(random.NextDouble() * 1e7 - 5e6),
                    Z = (float)(random.NextDouble() * 1e7 - 5e6),
                    Throttle = (float)(random.NextDouble() * 2 - 1),
                    Turn = (float)(random.NextDouble() * 2 - 1),
                    Strafe = (float)(random.NextDouble() * 2 - 1),
                    Index = random.Next(0, 65)
                };
                Compare(c, i % 4 == 0 ? "Wing" : "Pilot",
                    "typical valid finite pilot packet " + i);
                var copy = Clone(c);
                Check(CurrentFinite(copy) == CurrentFinite(c),
                    "same numeric telemetry content accepted across cloned packets");
            }

            // Measure only the old/new production finite guard: the old
            // float[] and Any per command versus the linked direct predicate.
            // Not a measurement of JSON, network traffic or Unity input.
            var stable = new Control {
                X = 104, Y = -301, Z = 9, Throttle = 0.7f, Turn = -0.2f,
                Strafe = 0.01f, Index = 2
            };
            const int packets = 400000;
            Action old = () =>
            {
                if (!PreviousFinite(stable))
                    throw new InvalidOperationException("old unexpected non-finite");
            };
            Action modern = () =>
            {
                if (!CurrentFinite(stable))
                    throw new InvalidOperationException("new unexpected non-finite");
            };
            long before = Measure(old, packets), after = Measure(modern, packets);
            Check(before > 0 && after == 0,
                "linked direct finite checks create no per-packet managed arrays/iterators");
            Console.WriteLine("INFO: " + packets + " valid pilot control packets: old six-float LINQ guard " +
                before + " B, production direct six-field guard " + after +
                " B (managed allocation after warmup)");
            Console.WriteLine("PASS: " + checks + " production-linked pilot finite validation checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
