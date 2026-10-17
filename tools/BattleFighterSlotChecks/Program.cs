using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Fighter
    {
        internal long Carrier, Runtime;
        internal int Wing, Slot;
    }
    private sealed class ActiveSlot
    {
        internal long Carrier, Runtime;
        internal int Wing, Slot;
    }

    private static int checks;
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + name);
        checks++;
    }

    // The exact source-equivalent LINQ predicate and string occupied key
    // from the pre-optimization ValidateFightersState. This reference is
    // intentionally independent of the production slot membership class.
    private static bool LegacyValid(List<Fighter> fighters, List<ActiveSlot> slots)
    {
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (Fighter f in fighters)
            if (!occupied.Add(f.Carrier + "/" + f.Wing + "/" + f.Slot)) return false;
        foreach (ActiveSlot slot in slots)
            if (slot.Runtime >= 0 && !fighters.Any(f => f.Carrier == slot.Carrier
                && f.Wing == slot.Wing && f.Slot == slot.Slot && f.Runtime == slot.Runtime))
                return false;
        return true;
    }

    private static bool IndexedValid(List<Fighter> fighters, List<ActiveSlot> slots)
    {
        var membership = fighters.Count >= 8 ? new BattleFighterSlotMembership() : null;
        var occupied = membership == null ? new HashSet<string>(StringComparer.Ordinal) : null;
        foreach (Fighter f in fighters)
            if (!(membership == null
                ? occupied.Add(f.Carrier + "/" + f.Wing + "/" + f.Slot)
                : membership.TryRegister(f.Carrier, f.Wing, f.Slot, f.Runtime)))
                return false;
        foreach (ActiveSlot slot in slots)
            if (slot.Runtime >= 0 && !(membership == null
                ? fighters.Any(f => f.Carrier == slot.Carrier && f.Wing == slot.Wing
                    && f.Slot == slot.Slot && f.Runtime == slot.Runtime)
                : membership.Matches(slot.Carrier, slot.Wing, slot.Slot, slot.Runtime)))
                return false;
        return true;
    }

    private static void Same(List<Fighter> fighters, List<ActiveSlot> slots, string name)
    {
        Check(LegacyValid(fighters, slots) == IndexedValid(fighters, slots), name);
    }
    private static Fighter NewFighter(long carrier, int wing, int slot, long runtime)
    { return new Fighter { Carrier = carrier, Wing = wing, Slot = slot, Runtime = runtime }; }
    private static ActiveSlot NewSlot(long carrier, int wing, int slot, long runtime)
    { return new ActiveSlot { Carrier = carrier, Wing = wing, Slot = slot, Runtime = runtime }; }

    private static int Main()
    {
        try
        {
            Same(new List<Fighter>(), new List<ActiveSlot>(), "empty fighter frame");
            for (int count = 0; count <= 10; count++)
            {
                var fighters = new List<Fighter>();
                var slots = new List<ActiveSlot>();
                for (int i = 0; i < count; i++)
                {
                    fighters.Add(NewFighter(1, 0, i, 100 + i));
                    slots.Add(NewSlot(1, 0, i, 100 + i));
                }
                Same(fighters, slots, "frame-size threshold " + count);
                if (count != 0)
                {
                    slots[0].Runtime = 999;
                    Same(fighters, slots, "wrong runtime on frame-size " + count);
                }
            }

            var baseFighters = new List<Fighter>();
            var baseSlots = new List<ActiveSlot>();
            for (int i = 0; i < 32; i++)
            {
                baseFighters.Add(NewFighter(1, 1, i, 2000 + i));
                baseSlots.Add(NewSlot(1, 1, i, 2000 + i));
            }
            Check(IndexedValid(baseFighters, baseSlots), "valid multi-fighter baseline");
            var membership = new BattleFighterSlotMembership();
            foreach (Fighter f in baseFighters)
                Check(membership.TryRegister(f.Carrier, f.Wing, f.Slot, f.Runtime),
                    "registered unique fighter slot");
            Check(membership.Matches(1, 1, 0, 2000), "valid source runtime resolves");
            Check(!membership.Matches(1, 1, 0, 2001), "same slot with different runtime is rejected");
            Check(!membership.Matches(2, 1, 0, 2000), "different owner carrier cannot match");
            Check(!membership.Matches(1, 2, 0, 2000), "different wing cannot match");
            Check(!membership.Matches(1, 1, 1, 2000), "different slot cannot match");
            Check(!membership.Matches(1, 1, 999, 2000), "unknown slot is absent");
            Check(!membership.TryRegister(1, 1, 0, 2000), "duplicate same-runtime slot is rejected");
            Check(!membership.TryRegister(1, 1, 0, 9999), "duplicate different-runtime slot is rejected");
            Check(membership.Matches(1, 1, 0, 2000),
                "failed duplicate cannot overwrite existing native runtime");

            var missing = new List<ActiveSlot>(baseSlots) { NewSlot(1, 1, 40, 888) };
            Same(baseFighters, missing, "active slot has no matching fighter");
            var idle = new List<ActiveSlot>(baseSlots) { NewSlot(1, 1, 40, -1) };
            Same(baseFighters, idle, "inactive slot does not require a fighter");
            var duplicate = new List<Fighter>(baseFighters) { NewFighter(1, 1, 0, 2000) };
            Same(duplicate, baseSlots, "duplicate fighter identity is rejected");
            duplicate[duplicate.Count - 1].Runtime = 9000;
            Same(duplicate, baseSlots, "two runtime IDs cannot occupy one slot");

            var colliding = new BattleFighterSlotMembership();
            Check(colliding.TryRegister(0L, 1, 4, 17)
                && colliding.TryRegister(1L << 32, 1, 4, 18)
                && colliding.TryRegister(-1, 1, 4, 19),
                "different 64-bit carriers stay distinct even if hash components collide");
            Check(colliding.Matches(0L, 1, 4, 17)
                && colliding.Matches(1L << 32, 1, 4, 18)
                && colliding.Matches(-1, 1, 4, 19),
                "hash collisions do not change authoritative carrier identity");

            // 512 active fighters across several carriers and wings. Old
            // reverse validation scans the entire fighter list for each slot.
            const int carrierCount = 4, wingsPerCarrier = 4, slotsPerWing = 32;
            var denselyOccupied = new List<ActiveSlot>();
            var denseFighters = new List<Fighter>();
            long nextUid = 40000;
            for (int c = 0; c < carrierCount; c++)
                for (int wing = 0; wing < wingsPerCarrier; wing++)
                    for (int slot = 0; slot < slotsPerWing; slot++)
                    {
                        long id = ++nextUid;
                        denselyOccupied.Add(NewSlot(1000 + c, wing, slot, id));
                        denseFighters.Add(NewFighter(1000 + c, wing, slot, id));
                    }
            denseFighters.Reverse();
            Same(denseFighters, denselyOccupied, "dense multi-carrier validation");
            long legacyProbes = 0;
            foreach (ActiveSlot slot in denselyOccupied)
            {
                foreach (Fighter fighter in denseFighters)
                {
                    legacyProbes++;
                    if (fighter.Carrier == slot.Carrier && fighter.Wing == slot.Wing
                        && fighter.Slot == slot.Slot && fighter.Runtime == slot.Runtime) break;
                }
            }
            membership = new BattleFighterSlotMembership();
            foreach (Fighter fighter in denseFighters)
                if (!membership.TryRegister(fighter.Carrier, fighter.Wing, fighter.Slot, fighter.Runtime))
                    throw new InvalidOperationException("Unexpected duplicate");
            foreach (ActiveSlot slot in denselyOccupied)
                Check(membership.Matches(slot.Carrier, slot.Wing, slot.Slot, slot.Runtime),
                    "dense reverse match retains exact runtime");
            Check(legacyProbes == 512L * 513 / 2,
                "legacy reverse verification does a triangular number of fighter probes");
            Console.WriteLine("INFO: 512 active slots and 512 fighters: legacy reverse verification "
                + legacyProbes + " fighter comparisons, indexed path 512 registrations + 512 lookups");

            // Randomized input checks both fast and fallback branches, inactive
            // slots, absent IDs and conflicting owner/wing/slot combinations.
            var random = new Random(20261018);
            for (int iteration = 0; iteration < 4000; iteration++)
            {
                var randomFighters = new List<Fighter>();
                var randomSlots = new List<ActiveSlot>();
                int count = random.Next(0, 50);
                for (int j = 0; j < count; j++)
                {
                    randomFighters.Add(NewFighter(random.Next(1, 8), random.Next(0, 6),
                        random.Next(0, 40), random.Next(1, 500)));
                    randomSlots.Add(NewSlot(random.Next(1, 8), random.Next(0, 6),
                        random.Next(0, 40), random.Next(-1, 500)));
                }
                Same(randomFighters, randomSlots, "randomized membership " + iteration);
            }

            var fresh = new BattleFighterSlotMembership();
            Check(!fresh.Matches(1, 1, 0, 2000),
                "next incoming frame does not inherit prior fighter identities");
            Check(fresh.TryRegister(1, 1, 0, 3000) && fresh.Matches(1, 1, 0, 3000)
                && !fresh.Matches(1, 1, 0, 2000),
                "recreated fighter with the same slot uses its new runtime UID");
            Console.WriteLine("PASS: " + checks + " production fighter-slot membership checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
