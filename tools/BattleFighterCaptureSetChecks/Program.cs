using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    // UnityEngine.Object overrides equality; the cache must retain the
    // default HashSet comparer, not replace it with reference identity.
    private sealed class NativeRef : IEquatable<NativeRef>
    {
        internal readonly int Id;
        internal NativeRef(int id) { Id = id; }
        public bool Equals(NativeRef other) { return other != null && Id == other.Id; }
        public override bool Equals(object other) { return Equals(other as NativeRef); }
        public override int GetHashCode() { return Id; }
    }

    private static int checks;
    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static bool Throws<T>(Action operation) where T : Exception
    {
        try { operation(); return false; } catch (T) { return true; }
    }
    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private static void SameSnapshot(
        BattleFighterCaptureSet<NativeRef> pool, List<NativeRef> native,
        string reason)
    {
        var original = new HashSet<NativeRef>(native);
        using (var lease = pool.Rent())
        {
            foreach (NativeRef value in native) lease.Items.Add(value);
            Check(original.Count == lease.Items.Count, reason + ": default equality count");
            var previous = original.ToArray();
            var current = lease.Items.ToArray();
            Check(previous.Length == current.Length, reason + ": cardinality");
            for (int i = 0; i < previous.Length; i++)
                Check(ReferenceEquals(previous[i], current[i]),
                    reason + ": original first-occurrence object & order " + i);
        }
    }

    private static void MainChecks()
    {
        var observed = new BattleFighterCaptureSet<NativeRef>();
        var carriers = new BattleFighterCaptureSet<NativeRef>();
        var first = observed.Rent();
        HashSet<NativeRef> firstSet = first.Items;
        first.Items.Add(new NativeRef(7));
        first.Items.Add(new NativeRef(7));
        first.Items.Add(null);
        Check(first.Items.Count == 2 && first.Items.Contains(new NativeRef(7))
            && first.Items.Contains(null), "native value equality, duplicate and null are unchanged");

        using (var nested = observed.Rent())
        {
            Check(!ReferenceEquals(first.Items, nested.Items), "native callback owns an isolated capture");
            nested.Items.Add(new NativeRef(8));
            Check(first.Items.Count == 2 && nested.Items.Count == 1,
                "nested inserts cannot corrupt the outer capture");
            observed.Reset();
            Check(first.Items.Count == 2, "battle cleanup defers reset for a leased capture");
        }
        Check(first.Items.Count == 2, "nested lease disposal leaves the outer snapshot untouched");
        first.Dispose();
        Check(firstSet.Count == 0, "outer lease removes all Unity object references");
        Check(firstSet.EnsureCapacity(0) <= 3, "deferred reset releases all but minimum set capacity");
        using (var reused = observed.Rent())
            Check(ReferenceEquals(firstSet, reused.Items) && reused.Items.Count == 0,
                "post-reset native capture reuses clean original set");

        using (var left = observed.Rent())
        using (var right = carriers.Rent())
        {
            left.Items.Add(new NativeRef(12));
            right.Items.Add(new NativeRef(12));
            Check(!ReferenceEquals(left.Items, right.Items), "observed fighters and carriers never share storage");
        }
        Check(Throws<InvalidOperationException>(() =>
        {
            using (var lease = observed.Rent())
            {
                lease.Items.Add(new NativeRef(20));
                throw new InvalidOperationException("native fighter manager callback failed");
            }
        }), "native callback failure propagates to its owner");
        using (var lease = observed.Rent())
            Check(lease.Items.Count == 0, "failure releases dirty observed identities for retry");

        var random = new Random(20261018);
        for (int round = 0; round < 3200; round++)
        {
            int count = random.Next(0, 110);
            var source = new List<NativeRef>(count);
            for (int i = 0; i < count; i++)
                source.Add(random.Next(17) == 0 ? null : new NativeRef(random.Next(0, 320)));
            SameSnapshot(observed, source, "random native fighters " + round);
            if ((round % 137) == 0)
            {
                var captured = observed.Rent();
                foreach (NativeRef value in source) captured.Items.Add(value);
                int savedCount = captured.Items.Count;
                source.Add(new NativeRef(999));
                source.Clear();
                Check(captured.Items.Count == savedCount,
                    "native spawn/despawn after capture cannot change frozen membership");
                using (var nested = observed.Rent())
                    Check(nested.Items.Count == 0,
                        "reentrant capture starts empty even while first snapshot is leased");
                captured.Dispose();
                observed.Reset();
            }
        }

        // Keep a one-off dense battle's capacity until explicit cleanup;
        // Reset must discard it even if an outer native callback is active.
        var heavy = new List<NativeRef>();
        for (int i = 0; i < 4096; i++) heavy.Add(new NativeRef(i));
        var heavyCapture = observed.Rent();
        foreach (NativeRef item in heavy) heavyCapture.Items.Add(item);
        Check(heavyCapture.Items.Count == 4096, "dense fighter burst retained while capture executes");
        observed.Reset();
        Check(heavyCapture.Items.Count == 4096, "battle exit cannot clear an unfinished capture");
        heavyCapture.Dispose();
        Check(heavyCapture.Items.EnsureCapacity(0) <= 3,
            "battle exit releases one-time dense fighter capacity");

        var fighterFrame = new List<NativeRef>();
        var carrierFrame = new List<NativeRef>();
        for (int i = 0; i < 128; i++) fighterFrame.Add(new NativeRef(i));
        for (int i = 0; i < 48; i++) carrierFrame.Add(new NativeRef(i));
        Action legacy = () =>
        {
            var fighters = new HashSet<NativeRef>();
            var masters = new HashSet<NativeRef>(carrierFrame);
            foreach (NativeRef f in fighterFrame) fighters.Add(f);
            if (fighters.Count != 128 || masters.Count != 48) throw new InvalidOperationException();
        };
        Action production = () =>
        {
            using (var fighters = observed.Rent())
            using (var masters = carriers.Rent())
            {
                foreach (NativeRef f in fighterFrame) fighters.Items.Add(f);
                foreach (NativeRef c in carrierFrame) masters.Items.Add(c);
                if (fighters.Items.Count != 128 || masters.Items.Count != 48)
                    throw new InvalidOperationException();
            }
        };
        const int frames = 18000;
        long oldBytes = Allocated(legacy, frames);
        long newBytes = Allocated(production, frames);
        Check(oldBytes > 0 && newBytes == 0, "capturing both native identity sets avoids steady allocations");
        Console.WriteLine("INFO: " + frames + " synthetic checkpoint frames (128 fighters, 48 carriers): " +
            "previous hash sets " + oldBytes + " B, linked production leases " + newBytes + " B (after warmup)");
    }

    private static int Main()
    {
        try
        {
            MainChecks();
            Console.WriteLine("PASS: " + checks + " production-linked fighter capture set checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
