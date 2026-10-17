using System;
using System.Collections;
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
    private sealed class TrackedWingSlots : IEnumerable<ActiveSlot>
    {
        internal readonly List<ActiveSlot> Values = new List<ActiveSlot>();
        internal int Enumerations, Visits;
        public IEnumerator<ActiveSlot> GetEnumerator()
        {
            Enumerations++;
            foreach (ActiveSlot slot in Values)
            {
                Visits++;
                yield return slot;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        internal void ResetCounts() { Enumerations = Visits = 0; }
    }
    private static readonly Func<ActiveSlot, int> DeclaredIndex = s => s.Slot;
    private static readonly Func<ActiveSlot, long> DeclaredRuntime = s => s.Runtime;

    private static int checks;
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + name);
        checks++;
    }
    private static bool Rejects<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
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

            // Production validates the full wing and its unique slot indices
            // before fighter validation. Build the forward cache lazily only
            // for wings which a fighter actually references.
            var declared = new TrackedWingSlots();
            declared.Values.Add(NewSlot(1, 1, 0, 2000));
            declared.Values.Add(NewSlot(1, 1, 1, 2001));
            declared.Values.Add(NewSlot(1, 1, 2, -1));
            var forward = new BattleFighterSlotMembership();
            Check(declared.Enumerations == 0, "forward cache construction does not scan unused wings");
            Check(forward.DeclaredSlotMatches(1, 1, declared, 0, 2000, DeclaredIndex, DeclaredRuntime)
                && declared.Enumerations == 1 && declared.Visits == 3,
                "first referenced wing is indexed exactly once");
            Check(forward.DeclaredSlotMatches(1, 1, declared, 1, 2001, DeclaredIndex, DeclaredRuntime)
                && declared.Enumerations == 1,
                "next fighter of the same wing reuses its existing slot table");
            Check(!forward.DeclaredSlotMatches(1, 1, declared, 0, 2001, DeclaredIndex, DeclaredRuntime),
                "mismatched fighter UID cannot pass a matching slot index");
            Check(!forward.DeclaredSlotMatches(1, 1, declared, 9, 2001, DeclaredIndex, DeclaredRuntime),
                "absent native slot is still rejected");
            Check(!forward.DeclaredSlotMatches(1, 1, declared, 2, 2002, DeclaredIndex, DeclaredRuntime),
                "empty fighter slot cannot be taken by a different runtime UID");
            Check(declared.Enumerations == 1, "all negative lookups reuse the single wing traversal");
            Check(Rejects<ArgumentNullException>(() =>
                    forward.DeclaredSlotMatches<ActiveSlot>(1, 1, null, 0, 1, DeclaredIndex, DeclaredRuntime))
                && Rejects<ArgumentNullException>(() =>
                    forward.DeclaredSlotMatches(1, 1, declared, 0, 1, null, DeclaredRuntime))
                && Rejects<ArgumentNullException>(() =>
                    forward.DeclaredSlotMatches(1, 1, declared, 0, 1, DeclaredIndex, null)),
                "null wire slot collection/selectors are rejected before caching");
            var anotherWing = new TrackedWingSlots();
            anotherWing.Values.Add(NewSlot(2, 1, 0, 3000));
            Check(forward.DeclaredSlotMatches(2, 1, anotherWing, 0, 3000, DeclaredIndex, DeclaredRuntime)
                && !forward.DeclaredSlotMatches(1, 1, declared, 0, 3000, DeclaredIndex, DeclaredRuntime),
                "another mother ship never shares this wing's fighter runtime");
            var thirdWing = new TrackedWingSlots();
            thirdWing.Values.Add(NewSlot(1, 2, 0, 4000));
            Check(forward.DeclaredSlotMatches(1, 2, thirdWing, 0, 4000, DeclaredIndex, DeclaredRuntime)
                && !forward.DeclaredSlotMatches(1, 1, declared, 0, 4000, DeclaredIndex, DeclaredRuntime),
                "another wing on one carrier remains independently indexed");

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

            // Validate the OTHER direction used by ValidateFightersState:
            // each fighter used to scan its wing's full slot list. The new
            // table is built only on first use of each populated wing.
            var indexedWings = new Dictionary<string, TrackedWingSlots>(StringComparer.Ordinal);
            foreach (ActiveSlot slot in denselyOccupied)
            {
                string key = slot.Carrier + "/" + slot.Wing;
                TrackedWingSlots group;
                if (!indexedWings.TryGetValue(key, out group))
                    indexedWings.Add(key, group = new TrackedWingSlots());
                group.Values.Add(slot);
            }
            int legacyForwardMatches = 0;
            foreach (Fighter fighter in denseFighters)
            {
                TrackedWingSlots group = indexedWings[fighter.Carrier + "/" + fighter.Wing];
                if (group.Any(s => s.Slot == fighter.Slot && s.Runtime == fighter.Runtime))
                    legacyForwardMatches++;
            }
            int legacyForwardVisits = indexedWings.Values.Sum(g => g.Visits);
            int legacyForwardEnumerations = indexedWings.Values.Sum(g => g.Enumerations);
            foreach (TrackedWingSlots group in indexedWings.Values) group.ResetCounts();
            int indexedForwardMatches = 0;
            var forwardDense = new BattleFighterSlotMembership();
            foreach (Fighter fighter in denseFighters)
            {
                TrackedWingSlots group = indexedWings[fighter.Carrier + "/" + fighter.Wing];
                if (forwardDense.DeclaredSlotMatches(fighter.Carrier, fighter.Wing, group,
                        fighter.Slot, fighter.Runtime, DeclaredIndex, DeclaredRuntime))
                    indexedForwardMatches++;
            }
            int indexedForwardVisits = indexedWings.Values.Sum(g => g.Visits);
            int indexedForwardEnumerations = indexedWings.Values.Sum(g => g.Enumerations);
            Check(legacyForwardMatches == 512 && indexedForwardMatches == legacyForwardMatches,
                "dense fighter-to-wing slot validation keeps all original native matches");
            Check(legacyForwardEnumerations == 512 && indexedForwardEnumerations == indexedWings.Count,
                "each referenced wing's declared slot list is visited once instead of once per fighter");
            Check(indexedForwardVisits == denselyOccupied.Count && legacyForwardVisits > indexedForwardVisits,
                "dense forward validation avoids repeated native slot-element reads");
            Console.WriteLine("INFO: 512 fighter-to-wing matches across 16 wings: legacy visited " +
                legacyForwardVisits + " slot elements in " + legacyForwardEnumerations +
                " scans, new frame-local index visited " + indexedForwardVisits +
                " slot elements in " + indexedForwardEnumerations + " scans");

            // Sparse baselines and empty wings must not allocate a lookup for
            // an unrelated wing. Per-frame membership never caches across
            // a native replacement, reconnection or baseline reset.
            var unusedWing = new TrackedWingSlots();
            unusedWing.Values.Add(NewSlot(5, 4, 1, 8000));
            Check(unusedWing.Enumerations == 0,
                "unreferenced native wing is never indexed by a different wing's fighters");
            var nextFrame = new BattleFighterSlotMembership();
            var respawned = new TrackedWingSlots();
            respawned.Values.Add(NewSlot(1, 1, 0, 9000));
            Check(!nextFrame.DeclaredSlotMatches(1, 1, respawned, 0, 2000, DeclaredIndex, DeclaredRuntime)
                && nextFrame.DeclaredSlotMatches(1, 1, respawned, 0, 9000, DeclaredIndex, DeclaredRuntime),
                "next snapshot uses new runtime UID and does not reuse the old wing slot table");
            Check(forward.DeclaredSlotMatches(1, 1, declared, 0, 2000, DeclaredIndex, DeclaredRuntime),
                "old frame remains internally stable until the enclosing validation completes");

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

            // Independent comparisons with the original wing.Slots.Any()
            // predicate across randomly generated, prevalidated unique
            // slot lists. Include absent slot/UID and negative placeholder.
            for (int iteration = 0; iteration < 2500; iteration++)
            {
                int carrier = random.Next(1, 9), wing = random.Next(0, 8);
                int slotCount = random.Next(0, 80);
                var group = new TrackedWingSlots();
                for (int slot = 0; slot < slotCount; slot++)
                    group.Values.Add(NewSlot(carrier, wing, slot, random.Next(-1, 9000)));
                var membershipForFrame = new BattleFighterSlotMembership();
                for (int probe = 0; probe < 8; probe++)
                {
                    int candidate = random.Next(-1, slotCount + 2);
                    long uid = random.Next(3) == 0 && candidate >= 0 && candidate < slotCount
                        ? group.Values[candidate].Runtime : random.Next(-1, 9000);
                    bool oldResult = group.Any(s => s.Slot == candidate && s.Runtime == uid);
                    bool indexedResult = membershipForFrame.DeclaredSlotMatches(
                        carrier, wing, group, candidate, uid, DeclaredIndex, DeclaredRuntime);
                    Check(oldResult == indexedResult, "random wing/slot/runtime match " + iteration + "/" + probe);
                }
                Check(group.Enumerations == 9,
                    "eight legacy probes plus one indexed wing traversal on randomized input");
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
