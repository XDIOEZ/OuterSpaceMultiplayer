using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;
using Weapon = FreeWorldMultiplayer.GameBridge.BattleWeaponEffect;
using Beam = FreeWorldMultiplayer.GameBridge.BattleBeamLine;

internal static class Program
{
    private sealed class Fighter
    {
        internal long RuntimeId;
        internal List<Weapon> Weapons;
        internal Fighter(long runtimeId, List<Weapon> weapons)
        { RuntimeId = runtimeId; Weapons = weapons; }
    }

    private sealed class WatchedWeapon
    {
        internal long Ship;
        internal int Reads;
        internal WatchedWeapon(long ship) { Ship = ship; }
    }
    private static readonly Func<Weapon, long> ShipId = value => value.Ship;
    private static readonly Func<WatchedWeapon, long> WatchedShipId =
        value => { value.Reads++; return value.Ship; };
    private static int checks;

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static string Error(Exception ex)
    { return ex.GetType().Name + ": " + ex.Message; }

    private static string Previous(Fighter fighter)
    {
        try
        {
            // Exactly the old ValidateFightersState post-structure ordering:
            // complete production weapon/beam preflight, then captured LINQ.
            GameBridge.CheckWeaponWire(fighter.Weapons);
            if (fighter.Weapons.Any(w => w.Ship != fighter.RuntimeId))
                throw new InvalidOperationException("舰载机武器引用另一架战机。");
            return "accepted";
        }
        catch (Exception ex) { return Error(ex); }
    }
    private static string Current(Fighter fighter)
    {
        try
        {
            GameBridge.CheckWeaponWire(fighter.Weapons);
            if (BattleFighterWeaponOwnership.ContainsForeignShip(
                fighter.Weapons, fighter.RuntimeId, ShipId))
                throw new InvalidOperationException("舰载机武器引用另一架战机。");
            return "accepted";
        }
        catch (Exception ex) { return Error(ex); }
    }
    private static void Compare(Fighter fighter, string label)
    {
        string old = Previous(fighter), current = Current(fighter);
        Check(old == current, label + " old=" + old + " new=" + current);
    }
    private static Weapon Valid(long id, string slot = "main")
    { return new Weapon { Ship = id, Slot = slot, Resource = "laser", Angle = 0, Ammo = 12 }; }
    private static Beam ValidBeam()
    {
        return new Beam { X = 0, Y = 1, Z = 2, EndX = 3, EndY = 4, EndZ = 5,
            Width = 0.5f, EndWidth = 0.25f };
    }
    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
    }

    private static int Main()
    {
        try
        {
            long[] ids = { long.MinValue, -1, 0, 1, (1L << 32),
                (1L << 32) | 1, long.MaxValue };
            foreach (long uid in ids)
            {
                Compare(new Fighter(uid, new List<Weapon>()),
                    "unarmed native fighter: " + uid);
                Compare(new Fighter(uid, new List<Weapon> { Valid(uid) }),
                    "single matching weapon: " + uid);
                Compare(new Fighter(uid, new List<Weapon> { Valid(uid), Valid(uid, "other") }),
                    "two valid slots for one fighter: " + uid);
                foreach (long otherUid in ids)
                {
                    Compare(new Fighter(uid, new List<Weapon> {
                        Valid(otherUid, "foreign"), Valid(uid, "own") }),
                        "first weapon is foreign: " + uid + "/" + otherUid);
                    Compare(new Fighter(uid, new List<Weapon> {
                        Valid(uid, "own"), Valid(otherUid, "foreign") }),
                        "second weapon is foreign: " + uid + "/" + otherUid);
                }
            }

            Compare(new Fighter(12, null), "null list rejected by shared validator first");
            Compare(new Fighter(12, new List<Weapon> { null }),
                "null weapon element rejected by shared validator first");
            Compare(new Fighter(12, new List<Weapon> { Valid(12), null }),
                "late null weapon rejects before checking ownership");
            Compare(new Fighter(12, new List<Weapon> { Valid(13), null }),
                "invalid wire shape takes priority over foreign ship");
            Compare(new Fighter(12, new List<Weapon> { Valid(12), Valid(12) }),
                "duplicate ship/slot rejects before ownership");
            var missingResource = Valid(13);
            missingResource.Resource = null;
            Compare(new Fighter(12, new List<Weapon> { missingResource }),
                "resource rejection retains its original priority over ship mismatch");
            var missingSlot = Valid(13);
            missingSlot.Slot = "";
            Compare(new Fighter(12, new List<Weapon> { missingSlot }),
                "slot rejection retains original priority over ship mismatch");
            var wrongAngle = Valid(13);
            wrongAngle.Angle = float.PositiveInfinity;
            Compare(new Fighter(12, new List<Weapon> { wrongAngle }),
                "nonfinite angle rejects before ownership");
            var badBeam = Valid(13);
            badBeam.Beams.Add(ValidBeam());
            badBeam.Beams[0].Width = -1;
            Compare(new Fighter(12, new List<Weapon> { badBeam }),
                "invalid beam endpoint or width rejects before ownership");
            var nullBeam = Valid(13);
            nullBeam.Beams.Add(null);
            Compare(new Fighter(12, new List<Weapon> { nullBeam }),
                "null beam takes priority over cross-fighter reference");

            var oversized = new List<Weapon>(8193);
            for (int i = 0; i < 8193; i++) oversized.Add(Valid(1, "slot-" + i));
            Compare(new Fighter(1, oversized),
                "oversized wire weapons rejected before ownership iteration");
            Check(Throws<ArgumentNullException>(() =>
                BattleFighterWeaponOwnership.ContainsForeignShip<Weapon>(
                    null, 1, ShipId)),
                "null list direct helper has the old LINQ argument exception class");
            Check(Throws<ArgumentNullException>(() =>
                BattleFighterWeaponOwnership.ContainsForeignShip(
                    new List<Weapon>(), 1, null)),
                "missing UID selector fails explicitly");

            // Verify the original Any(predicate) short-circuits at the same
            // element as the linked production helper. On malformed null
            // elements, the earlier complete wire validation protects both.
            for (int n = 0; n <= 64; n++)
                for (int foreign = -1; foreign < n; foreign++)
                {
                    var original = new List<WatchedWeapon>();
                    var revised = new List<WatchedWeapon>();
                    for (int i = 0; i < n; i++)
                    {
                        long id = i == foreign ? 99 : 1;
                        original.Add(new WatchedWeapon(id));
                        revised.Add(new WatchedWeapon(id));
                    }
                    bool old = original.Any(w => { w.Reads++; return w.Ship != 1; });
                    bool current = BattleFighterWeaponOwnership.ContainsForeignShip(
                        revised, 1, WatchedShipId);
                    Check(old == current, "first foreign-ship short-circuit n=" + n);
                    for (int i = 0; i < n; i++)
                        Check(original[i].Reads == revised[i].Reads,
                            "same per-weapon visitation and early-exit n=" + n);
                }

            // The production shared validator is compiled/linked, so these
            // differential checks also protect its error priority. Fuzz
            // empty, single and multiweapon native frames with malformed
            // Resource/Slot/Angle/Beam contents and foreign owners.
            var random = new Random(20261018);
            for (int frame = 0; frame < 11000; frame++)
            {
                long fighterId = random.Next(0, 7) == 0
                    ? ids[random.Next(ids.Length)]
                    : random.Next(-20000, 20000);
                int count = random.Next(0, 24);
                var weapons = new List<Weapon>(count);
                for (int i = 0; i < count; i++)
                {
                    long owner = random.Next(0, 6) == 0
                        ? ids[random.Next(ids.Length)] : fighterId;
                    var value = Valid(owner, "slot-" + (random.Next(0, 11) == 0
                        ? random.Next(0, 4) : i));
                    int malformed = random.Next(0, 36);
                    if (malformed == 0) value = null;
                    else if (malformed == 1) value.Resource = "";
                    else if (malformed == 2) value.Slot = null;
                    else if (malformed == 3) value.Angle = float.NaN;
                    else if (malformed == 4)
                    {
                        value.Beams.Add(ValidBeam());
                        value.Beams[0].EndWidth = float.NaN;
                    }
                    else if (malformed == 5)
                        value.Beams = null;
                    weapons.Add(value);
                }
                Compare(new Fighter(fighterId, weapons),
                    "random native fighter weapon frame " + frame);
            }

            // Synthetic benchmark is strictly the post-validation ownership
            // predicate, excluding unrelated slot/beam/native processing.
            // It includes the common unarmed craft and an armed-craft sample.
            const int fighterCount = 192, frames = 1600;
            var unarmed = new List<Fighter>(fighterCount);
            var armed = new List<Fighter>(fighterCount);
            for (int i = 0; i < fighterCount; i++)
            {
                unarmed.Add(new Fighter(i + 1, new List<Weapon>()));
                armed.Add(new Fighter(i + 1, new List<Weapon> {
                    Valid(i + 1, "primary"), Valid(i + 1, "secondary") }));
            }
            Action oldUnarmed = () =>
            {
                foreach (Fighter fighter in unarmed)
                    if (fighter.Weapons.Any(w => w.Ship != fighter.RuntimeId))
                        throw new InvalidOperationException("Old invalid fighter");
            };
            Action newUnarmed = () =>
            {
                foreach (Fighter fighter in unarmed)
                    if (BattleFighterWeaponOwnership.ContainsForeignShip(
                        fighter.Weapons, fighter.RuntimeId, ShipId))
                        throw new InvalidOperationException("New invalid fighter");
            };
            Action oldArmed = () =>
            {
                foreach (Fighter fighter in armed)
                    if (fighter.Weapons.Any(w => w.Ship != fighter.RuntimeId))
                        throw new InvalidOperationException("Old invalid fighter");
            };
            Action newArmed = () =>
            {
                foreach (Fighter fighter in armed)
                    if (BattleFighterWeaponOwnership.ContainsForeignShip(
                        fighter.Weapons, fighter.RuntimeId, ShipId))
                        throw new InvalidOperationException("New invalid fighter");
            };
            long beforeEmpty = Allocated(oldUnarmed, frames);
            long afterEmpty = Allocated(newUnarmed, frames);
            long beforeArmed = Allocated(oldArmed, frames);
            long afterArmed = Allocated(newArmed, frames);
            Check(beforeEmpty > afterEmpty && beforeArmed > afterArmed &&
                afterEmpty == 0 && afterArmed == 0,
                "production ownership predicate removes per-fighter allocation");
            Console.WriteLine("INFO: " + frames + " synthetic checkpoints × " +
                fighterCount + " fighters: unarmed old " + beforeEmpty +
                " B, new " + afterEmpty + " B; two weapons per fighter old " +
                beforeArmed + " B, new " + afterArmed +
                " B (managed allocations after warmup, ownership check only)");
            Console.WriteLine("PASS: " + checks +
                " production-linked fighter weapon ownership and validation checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
