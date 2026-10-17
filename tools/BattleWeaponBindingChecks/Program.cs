using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Wire
    {
        internal readonly int Slot, Ammo, Beams;
        internal readonly bool Departed, ResolveError, ApplyError;
        internal Wire(int slot, int ammo, int beams = 0,
            bool departed = false, bool resolveError = false, bool applyError = false)
        {
            Slot = slot; Ammo = ammo; Beams = beams;
            Departed = departed; ResolveError = resolveError; ApplyError = applyError;
        }
    }
    private sealed class Native
    {
        internal readonly int Slot, BeamEmitters;
        internal int Ammo;
        internal Native(int slot, int beams = 0)
        { Slot = slot; BeamEmitters = beams; }
    }
    private sealed class Frame
    {
        internal readonly List<Wire> Incoming = new List<Wire>();
        internal readonly Dictionary<int, Native> Controllers = new Dictionary<int, Native>();
        internal readonly List<int> Applied = new List<int>();
        internal int Resolved;
        internal bool SequenceApplied;
    }

    private static int checks;
    private static void Check(bool valid, string reason)
    {
        if (!valid) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static Native Resolve(Frame frame, Wire wire)
    {
        // Models ResolveEffectController and the same preflight ordering:
        // destroyed/departed ships do not add bindings, while an existing
        // ship without a usable slot/resource is a rejection.
        if (wire.Departed) return null;
        frame.Resolved++;
        Native controller;
        if (wire.ResolveError || !frame.Controllers.TryGetValue(wire.Slot, out controller))
            throw new InvalidOperationException("本机缺少本场舰船武器插槽：" + wire.Slot);
        return controller;
    }
    private static void VerifyEmitters(Wire wire, Native controller)
    {
        if (wire.Beams != controller.BeamEmitters)
            throw new InvalidOperationException("双方原生光束发射器数量不一致。");
    }
    private static void Apply(Frame frame, Wire wire, Native controller)
    {
        if (wire.ApplyError)
            throw new InvalidOperationException("native beam visualization failed");
        controller.Ammo = wire.Ammo;
        frame.Applied.Add(wire.Slot);
    }
    private static string ExceptionText(Exception exception)
    { return exception.GetType().Name + ": " + exception.Message; }

    private static string OldApply(Frame frame)
    {
        try
        {
            // Previous real bridge logic: check every resolved weapon first,
            // allocating both a new List and a Tuple for every present one.
            var bindings = new List<Tuple<Wire, Native>>();
            foreach (Wire wire in frame.Incoming)
            {
                Native native = Resolve(frame, wire);
                if (native == null) continue;
                VerifyEmitters(wire, native);
                bindings.Add(Tuple.Create(wire, native));
            }
            foreach (var pair in bindings) Apply(frame, pair.Item1, pair.Item2);
            frame.SequenceApplied = true;
            return "accepted";
        }
        catch (Exception error) { return ExceptionText(error); }
    }

    private static string NewApply(Frame frame, BattleWeaponBindingBuffer<Wire, Native> buffer)
    {
        var bindings = buffer.Rent();
        try
        {
            foreach (Wire wire in frame.Incoming)
            {
                Native native = Resolve(frame, wire);
                if (native == null) continue;
                VerifyEmitters(wire, native);
                bindings.Add(new KeyValuePair<Wire, Native>(wire, native));
            }
            foreach (var pair in bindings) Apply(frame, pair.Key, pair.Value);
            frame.SequenceApplied = true;
            return "accepted";
        }
        catch (Exception error) { return ExceptionText(error); }
        finally { buffer.Release(bindings); }
    }

    private static Frame Copy(Frame original)
    {
        var frame = new Frame();
        frame.Incoming.AddRange(original.Incoming);
        foreach (var item in original.Controllers)
        {
            var native = new Native(item.Value.Slot, item.Value.BeamEmitters);
            native.Ammo = item.Value.Ammo;
            frame.Controllers.Add(item.Key, native);
        }
        return frame;
    }
    private static void Compare(Frame incoming, BattleWeaponBindingBuffer<Wire, Native> buffer,
        string reason)
    {
        Frame legacy = Copy(incoming), optimized = Copy(incoming);
        string oldResult = OldApply(legacy);
        string newResult = NewApply(optimized, buffer);
        Check(oldResult == newResult,
            reason + " error/acceptance: old " + oldResult + " new " + newResult);
        Check(legacy.Resolved == optimized.Resolved, reason + " native resolve count");
        Check(legacy.Applied.SequenceEqual(optimized.Applied),
            reason + " visual apply order and partial effects");
        Check(legacy.SequenceApplied == optimized.SequenceApplied,
            reason + " only acknowledge successful full application");
        Check(legacy.Controllers.Count == optimized.Controllers.Count,
            reason + " controller identity count");
        foreach (var pair in legacy.Controllers)
        {
            Native target;
            Check(optimized.Controllers.TryGetValue(pair.Key, out target) &&
                pair.Value.Ammo == target.Ammo &&
                pair.Value.BeamEmitters == target.BeamEmitters,
                reason + " native ammo and beam state: " + pair.Key);
        }
        if (oldResult.Contains("光束发射器数量") || oldResult.Contains("缺少本场舰船武器插槽"))
            Check(optimized.Applied.Count == 0,
                reason + " preflight failure must not write native presentation");
    }
    private static long Allocated(Action operation, int iterations)
    {
        for (int i = 0; i < 100; i++) operation();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) operation();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Main()
    {
        try
        {
            var buffer = new BattleWeaponBindingBuffer<Wire, Native>();
            Check(Throws<ArgumentNullException>(() => buffer.Release(null)),
                "native buffer rejects null lease");

            var empty = new Frame();
            Compare(empty, buffer, "empty battle weapons");
            var absent = new Frame();
            absent.Incoming.Add(new Wire(1, 10, departed: true));
            Compare(absent, buffer, "destroyed or departed mother ship is skipped");
            var single = new Frame();
            single.Controllers.Add(3, new Native(3, 2));
            single.Incoming.Add(new Wire(3, 31, 2));
            Compare(single, buffer, "one matching weapon binding");
            single.Incoming.Add(new Wire(3, 32, 2));
            Compare(single, buffer, "same controller repeated by valid preflight");

            var mixed = new Frame();
            for (int i = 0; i < 48; i++)
            {
                mixed.Controllers.Add(i, new Native(i, i % 3));
                mixed.Incoming.Add(new Wire(i, i + 100, i % 3,
                    departed: i % 7 == 0));
            }
            Compare(mixed, buffer, "dense battle frame with departed shells");
            mixed.Incoming.Insert(25, new Wire(1000, 1, resolveError: true));
            Compare(mixed, buffer, "resolution fails after earlier valid bindings");
            mixed.Incoming.RemoveAt(25);
            mixed.Incoming[30] = new Wire(30, 30, 80);
            Compare(mixed, buffer, "emitter count mismatch fails before applying any weapon");

            var applicationFailure = new Frame();
            for (int i = 0; i < 30; i++)
            {
                applicationFailure.Controllers.Add(i, new Native(i));
                applicationFailure.Incoming.Add(new Wire(i, 100 + i,
                    applyError: i == 13));
            }
            Compare(applicationFailure, buffer,
                "native presentation fails during sequential apply after partial writes");
            Check(Throws<InvalidOperationException>(() =>
            {
                var rented = buffer.Rent();
                try
                {
                    rented.Add(new KeyValuePair<Wire, Native>(new Wire(123, 1), new Native(123)));
                    throw new InvalidOperationException("Native Apply callback failed");
                }
                finally { buffer.Release(rented); }
            }), "native failure propagates with guaranteed buffer release");
            var afterError = buffer.Rent();
            Check(afterError.Count == 0,
                "failed native callback does not retain any previous frame binding");
            buffer.Release(afterError);

            // Native events may synchronously cause a second effect apply.
            var outer = buffer.Rent();
            outer.Add(new KeyValuePair<Wire, Native>(single.Incoming[0], single.Controllers[3]));
            var inner = buffer.Rent();
            Check(!ReferenceEquals(outer, inner) && inner.Count == 0,
                "nested received battle frame leases a distinct native binding list");
            inner.Add(new KeyValuePair<Wire, Native>(new Wire(9, 40), new Native(9)));
            buffer.Release(inner);
            Check(outer.Count == 1 && outer[0].Key.Slot == 3,
                "nested release never clears the outer frame's native controller");
            buffer.Release(outer);
            var again = buffer.Rent();
            Check(ReferenceEquals(again, outer) && again.Count == 0,
                "stable frames reuse exactly one empty binding list");
            buffer.Release(again);

            // Teardown may be called while the native callback owns the
            // buffer: do not discard an active frame's references early.
            var large = buffer.Rent();
            for (int i = 0; i < 1024; i++)
                large.Add(new KeyValuePair<Wire, Native>(new Wire(i, i), new Native(i)));
            Check(large.Capacity >= 1024, "one-time burst expands the native binding buffer");
            buffer.Reset();
            Check(large.Count == 1024 && large[0].Key.Slot == 0,
                "reset while in-use defers clearing the current battle frame");
            var duringReset = buffer.Rent();
            Check(!ReferenceEquals(duringReset, large) &&
                duringReset.Count == 0, "reentrant frame during reset remains isolated");
            buffer.Release(duringReset);
            buffer.Release(large);
            Check(large.Count == 0 && large.Capacity == 0,
                "battle cleanup frees bindings and large backing array after lease release");
            var newBattle = buffer.Rent();
            Check(ReferenceEquals(newBattle, outer) && newBattle.Count == 0,
                "next battle begins without prior scene's native controller references");
            buffer.Release(newBattle);

            var random = new Random(20261018);
            for (int trial = 0; trial < 4000; trial++)
            {
                var sample = new Frame();
                int incoming = random.Next(0, 100);
                for (int i = 0; i < 60; i++)
                    sample.Controllers.Add(i, new Native(i, i % 4));
                for (int i = 0; i < incoming; i++)
                {
                    int slot = random.Next(0, 66);
                    int shape = random.Next(0, 24);
                    sample.Incoming.Add(new Wire(slot, random.Next(0, 300),
                        shape == 1 ? random.Next(5, 12) : slot % 4,
                        departed: shape == 2, resolveError: shape == 3,
                        applyError: shape == 4));
                }
                Compare(sample, buffer, "random native battle bindings " + trial);
            }

            // Benchmark only temporary preflight pair retention: native beam
            // state changes, Unity components, JSON and networking are not
            // measured. Old Tuple allocation is the exact previous approach.
            const int weapons = 256, frames = 1500;
            var wire = new Wire[weapons];
            var native = new Native[weapons];
            for (int i = 0; i < weapons; i++)
            {
                wire[i] = new Wire(i, i);
                native[i] = new Native(i);
            }
            Action oldBatch = () =>
            {
                var values = new List<Tuple<Wire, Native>>();
                for (int i = 0; i < weapons; i++) values.Add(Tuple.Create(wire[i], native[i]));
                for (int i = 0; i < values.Count; i++)
                    if (values[i].Item1.Slot != values[i].Item2.Slot)
                        throw new InvalidOperationException("Binding mismatch");
            };
            Action optimizedBatch = () =>
            {
                var values = buffer.Rent();
                try
                {
                    for (int i = 0; i < weapons; i++)
                        values.Add(new KeyValuePair<Wire, Native>(wire[i], native[i]));
                    for (int i = 0; i < values.Count; i++)
                        if (values[i].Key.Slot != values[i].Value.Slot)
                            throw new InvalidOperationException("Binding mismatch");
                }
                finally { buffer.Release(values); }
            };
            long oldAllocated = Allocated(oldBatch, frames);
            long newAllocated = Allocated(optimizedBatch, frames);
            Check(oldAllocated > 0 && newAllocated == 0,
                "steady visual preflight uses no managed Tuple/list allocations");
            Console.WriteLine("INFO: " + frames + " synthetic receive frames × " +
                weapons + " weapon bindings: old Tuple/list path " +
                oldAllocated + " B, linked production buffer " +
                newAllocated + " B (managed pair allocations after warmup)");
            Console.WriteLine("PASS: " + checks + " production-linked battle weapon binding checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
