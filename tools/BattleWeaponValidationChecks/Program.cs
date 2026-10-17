using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;
using Weapon = FreeWorldMultiplayer.GameBridge.BattleWeaponEffect;
using Beam = FreeWorldMultiplayer.GameBridge.BattleBeamLine;

internal static class Program
{
    private sealed class LegacyEffectsState
    {
        internal int Schema = 1;
        internal long Sequence = 1;
        internal List<object> Projectiles = new List<object>();
        internal List<object> Vfx = new List<object>();
        internal List<object> Events = new List<object>();
        internal List<Weapon> Weapons = new List<Weapon>();
    }
    private static int checks;
    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }
    private static string Outcome(Action action)
    {
        try { action(); return "accepted"; }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }

    // Copy of the exact previous production validation path. This is a
    // deliberately independent reference: a fighter previously had to create
    // a BattleEffectsState and validate empty pose/event lists to validate
    // its Weapons collection.
    private static void LegacyValidateFighter(List<Weapon> weapons)
    {
        var state = new LegacyEffectsState { Weapons = weapons };
        if (state.Schema != 1 || state.Sequence <= 0 || state.Projectiles == null || state.Vfx == null
            || state.Events == null || state.Weapons == null
            || state.Projectiles.Count > 16000 || state.Vfx.Count > 16000
            || state.Events.Count > 32000 || state.Weapons.Count > 8192)
            throw new InvalidOperationException("玩家舰战表现快照结构或数量无效。");
        var ids = new HashSet<long>();
        foreach (object pose in state.Projectiles.Concat(state.Vfx))
            throw new InvalidOperationException("Fixture unexpectedly contains a pose: " + pose);
        long previous = 0;
        foreach (object item in state.Events)
            throw new InvalidOperationException("Fixture unexpectedly contains an event: " + item);
        GC.KeepAlive(previous);
        GC.KeepAlive(ids);
        var weaponSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (Weapon value in state.Weapons)
        {
            if (value == null || value.Beams == null || value.Beams.Count > 128
                || String.IsNullOrEmpty(value.Slot) || value.Slot.Length > 128
                || String.IsNullOrEmpty(value.Resource) || value.Resource.Length > 128
                || float.IsNaN(value.Angle) || float.IsInfinity(value.Angle)
                || !weaponSlots.Add(value.Ship + ":" + value.Slot))
                throw new InvalidOperationException("舰战武器表现字段或插槽身份无效。");
            foreach (Beam line in value.Beams)
                if (line == null || !BattleEffectFiniteFields.ValidBeam(line.X, line.Y, line.Z,
                    line.EndX, line.EndY, line.EndZ, line.Width, line.EndWidth))
                    throw new InvalidOperationException("舰战光束端点或宽度无效。");
        }
    }

    private static void Equal(List<Weapon> weapons, string reason)
    {
        string old = Outcome(() => LegacyValidateFighter(weapons));
        string current = Outcome(() => GameBridge.CheckWeaponWire(weapons));
        Check(old == current, reason + " old=" + old + " new=" + current);
    }
    private static Weapon Valid(long ship = 1, string slot = "main", string resource = "laser")
    {
        return new Weapon { Ship = ship, Slot = slot, Resource = resource, Angle = 90, Ammo = 12 };
    }
    private static Beam ValidBeam()
    {
        return new Beam { X = 2, Y = 3, Z = 4, EndX = 8, EndY = 9, EndZ = 10,
            Width = 0.5f, EndWidth = 0.7f };
    }
    private static long Measure(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    public static int Main()
    {
        try
        {
            Equal(null, "null weapon list retains full effects frame structural rejection");
            var none = new List<Weapon>();
            Equal(none, "empty fighter weapon list");
            var single = new List<Weapon> { Valid() };
            Equal(single, "one valid weapon");
            Equal(new List<Weapon> { Valid(1, "left"), Valid(1, "right") },
                "two separate weapon slots on the same ship");
            Equal(new List<Weapon> { Valid(1, "left"), Valid(2, "left") },
                "two separate ships with identical slot names");
            Equal(new List<Weapon> { Valid(), Valid() }, "duplicate ship and slot");
            Equal(new List<Weapon> { null }, "null weapon element");
            var overLimit = new List<Weapon>();
            for (int i = 0; i < 8193; i++) overLimit.Add(Valid(1, "slot-" + i));
            Equal(overLimit.GetRange(0, 8192), "exactly 8192 distinct weapons remains permitted");
            Equal(overLimit, "weapon list above exact 8192-item cap");

            var field = Valid();
            field.Beams = null;
            Equal(new List<Weapon> { field }, "null beam list");
            field.Beams = new List<Beam>();
            for (int i = 0; i < 129; i++) field.Beams.Add(ValidBeam());
            Equal(new List<Weapon> { field }, "beam list above exact 128-item cap");

            var mutation = Valid();
            mutation.Slot = null; Equal(new List<Weapon> { mutation }, "missing slot");
            mutation.Slot = ""; Equal(new List<Weapon> { mutation }, "empty slot");
            mutation.Slot = new string('x', 129); Equal(new List<Weapon> { mutation }, "oversized slot");
            mutation.Slot = "s"; mutation.Resource = null;
            Equal(new List<Weapon> { mutation }, "missing resource");
            mutation.Resource = ""; Equal(new List<Weapon> { mutation }, "empty resource");
            mutation.Resource = new string('y', 129);
            Equal(new List<Weapon> { mutation }, "oversized resource");
            mutation.Resource = "beam"; mutation.Angle = float.NaN;
            Equal(new List<Weapon> { mutation }, "nan weapon angle");
            mutation.Angle = float.PositiveInfinity;
            Equal(new List<Weapon> { mutation }, "infinite weapon angle");
            mutation.Angle = float.NegativeInfinity;
            Equal(new List<Weapon> { mutation }, "negative infinite weapon angle");

            var goodBeam = Valid();
            goodBeam.Beams.Add(ValidBeam());
            Equal(new List<Weapon> { goodBeam }, "one valid beam");
            goodBeam.Beams.Add(null);
            Equal(new List<Weapon> { goodBeam }, "null beam element");
            goodBeam.Beams.RemoveAt(goodBeam.Beams.Count - 1);
            float[] anomalies = { -1, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
                0, -0.0f, float.Epsilon, float.MaxValue };
            foreach (float n in anomalies)
            {
                var value = Valid();
                value.Beams.Add(ValidBeam());
                value.Beams[0].Width = n;
                Equal(new List<Weapon> { value }, "beam start width " + n);
                value.Beams[0].Width = 0.2f;
                value.Beams[0].EndWidth = n;
                Equal(new List<Weapon> { value }, "beam end width " + n);
                value.Beams[0].EndWidth = 0.2f;
                value.Beams[0].X = n;
                Equal(new List<Weapon> { value }, "beam coordinate " + n);
            }

            // Compare exact exception category and message on random but
            // bounded DTOs, including duplicate slot, invalid angle, invalid
            // component, missing fields, and good multiweapon snapshots.
            var random = new Random(20261018);
            for (int i = 0; i < 4000; i++)
            {
                var frame = new List<Weapon>();
                int count = random.Next(0, 8);
                for (int j = 0; j < count; j++)
                {
                    int shape = random.Next(0, 11);
                    if (shape == 0) { frame.Add(null); continue; }
                    var item = Valid(random.Next(1, 5), "slot-" + random.Next(0, 7),
                        "resource-" + random.Next(0, 5));
                    if (shape == 1) item.Slot = null;
                    if (shape == 2) item.Resource = "";
                    if (shape == 3) item.Angle = float.NaN;
                    if (shape == 4) item.Angle = float.PositiveInfinity;
                    if (shape == 5) item.Beams = null;
                    if (shape >= 6)
                    {
                        item.Beams.Add(ValidBeam());
                        if (shape == 6) item.Beams[0].Width = -1;
                        if (shape == 7) item.Beams[0].EndX = float.PositiveInfinity;
                        if (shape == 8) item.Beams.Add(null);
                    }
                    frame.Add(item);
                }
                Equal(frame, "random wire fighter frame " + i);
            }

            // Previous check created four empty lists, one wrapper object,
            // a HashSet for poses and one for weapon slots, even when a
            // fighter carried no weapons. New helper touches the shared
            // empty list without creating a separate effects frame.
            const int fightersPerFrame = 128, frames = 1000;
            Action legacyBatch = () =>
            {
                for (int i = 0; i < fightersPerFrame; i++) LegacyValidateFighter(none);
            };
            Action optimizedBatch = () =>
            {
                for (int i = 0; i < fightersPerFrame; i++) GameBridge.CheckWeaponWire(none);
            };
            long before = Measure(legacyBatch, frames);
            long after = Measure(optimizedBatch, frames);
            Check(before > 0 && after == 0,
                "empty fighter weapon validation avoids unrelated DTO and LINQ allocations");
            Console.WriteLine("INFO: " + frames + " stable checkpoints × " + fightersPerFrame
                + " unarmed fighters: previous validator " + before
                + " B vs linked production weapon validator " + after + " B managed allocations");
            Console.WriteLine("PASS: " + checks + " production shared weapon/beam validation checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
