using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class NativeWeapon
    {
        internal string Slot;
        internal int Queries;
        internal NativeWeapon(string slot) { Slot = slot; }
        internal string GetParentSlot()
        {
            Queries++;
            return Slot;
        }
    }
    private static readonly Func<NativeWeapon, string> SlotOf = w => w.GetParentSlot();
    private static int passed;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + description);
        passed++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static NativeWeapon OldLookup(IEnumerable<NativeWeapon> weapons, string slot)
    {
        // The exact pre-optimization SingleOrDefault predicate used by
        // ApplyPlayerBattleFighters (including null-source filtering).
        return weapons.SingleOrDefault(w => w != null && w.GetParentSlot() == slot);
    }
    private static int QueryCount(IEnumerable<NativeWeapon> weapons)
    {
        int count = 0;
        foreach (NativeWeapon item in weapons) if (item != null) count += item.Queries;
        return count;
    }
    private static void ResetQueries(IEnumerable<NativeWeapon> weapons)
    {
        foreach (NativeWeapon item in weapons) if (item != null) item.Queries = 0;
    }

    private static int Main()
    {
        try
        {
            Check(Throws<ArgumentNullException>(() =>
                new BattleWeaponSlotIndex<NativeWeapon>(null, SlotOf)),
                "null native weapon collection is rejected");
            Check(Throws<ArgumentNullException>(() =>
                new BattleWeaponSlotIndex<NativeWeapon>(new NativeWeapon[0], null)),
                "null native slot selector is rejected");

            var empty = new BattleWeaponSlotIndex<NativeWeapon>(new NativeWeapon[0], SlotOf);
            Check(empty.SingleOrDefault("a") == null && empty.SingleOrDefault(null) == null,
                "empty native collection resolves no visual weapon");
            var one = new NativeWeapon("primary");
            var withNull = new NativeWeapon[] { null, one, null };
            var sparse = new BattleWeaponSlotIndex<NativeWeapon>(withNull, SlotOf);
            Check(ReferenceEquals(sparse.SingleOrDefault("primary"), one),
                "null native weapon components are ignored as in the original predicate");
            Check(sparse.SingleOrDefault("missing") == null,
                "absent slot is reported as missing");
            Check(sparse.SingleOrDefault("PRIMARY") == null,
                "weapon slots remain ordinal case-sensitive");
            Check(ReferenceEquals(OldLookup(withNull, "primary"), sparse.SingleOrDefault("primary")),
                "single native weapon identity is preserved");

            var withNullSlot = new NativeWeapon[] { new NativeWeapon(null), new NativeWeapon("alpha") };
            var indexedNull = new BattleWeaponSlotIndex<NativeWeapon>(withNullSlot, SlotOf);
            Check(ReferenceEquals(OldLookup(withNullSlot, null), indexedNull.SingleOrDefault(null)),
                "nullable native slot follows the same lookup semantics");
            var duplicateNull = new NativeWeapon[] { new NativeWeapon(null), new NativeWeapon(null), new NativeWeapon("other") };
            var indexedDupNull = new BattleWeaponSlotIndex<NativeWeapon>(duplicateNull, SlotOf);
            Check(Throws<InvalidOperationException>(() => indexedDupNull.SingleOrDefault(null)),
                "duplicate requested null slot is rejected");
            Check(ReferenceEquals(indexedDupNull.SingleOrDefault("other"), duplicateNull[2]),
                "an unrelated duplicate null slot does not reject another slot");

            var duplicate = new NativeWeapon[]
            {
                new NativeWeapon("primary"), new NativeWeapon("primary"),
                new NativeWeapon("secondary"), new NativeWeapon("tertiary")
            };
            var duplicateIndex = new BattleWeaponSlotIndex<NativeWeapon>(duplicate, SlotOf);
            Check(Throws<InvalidOperationException>(() => OldLookup(duplicate, "primary"))
                && Throws<InvalidOperationException>(() => duplicateIndex.SingleOrDefault("primary")),
                "duplicate requested native weapon slot retains LINQ SingleOrDefault rejection");
            Check(ReferenceEquals(OldLookup(duplicate, "secondary"), duplicateIndex.SingleOrDefault("secondary")),
                "unrelated duplicates are not newly rejected");
            Check(Throws<InvalidOperationException>(() => OldLookup(duplicateNull, null)),
                "null slot duplicate reference check remains equivalent");

            const int weaponCount = 96, frames = 300;
            var weapons = new NativeWeapon[weaponCount];
            for (int i = 0; i < weaponCount; i++) weapons[i] = new NativeWeapon("weapon-" + i);
            // Validate every result object in a dense replicated fighter update.
            // SingleOrDefault probes every weapon for each received slot to
            // detect duplicates, not just until the first match.
            for (int i = 0; i < weaponCount; i++)
            {
                var index = new BattleWeaponSlotIndex<NativeWeapon>(weapons, SlotOf);
                Check(ReferenceEquals(index.SingleOrDefault("weapon-" + i),
                    OldLookup(weapons, "weapon-" + i)),
                    "frame index maps the same native component as the old weapon scan");
            }
            ResetQueries(weapons);
            int oldMatches = 0;
            for (int frame = 0; frame < frames; frame++)
                for (int i = 0; i < weaponCount; i++)
                    if (OldLookup(weapons, "weapon-" + i) == weapons[i]) oldMatches++;
            int oldQueries = QueryCount(weapons);
            ResetQueries(weapons);
            int newMatches = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                var index = new BattleWeaponSlotIndex<NativeWeapon>(weapons, SlotOf);
                for (int i = 0; i < weaponCount; i++)
                    if (index.SingleOrDefault("weapon-" + i) == weapons[i]) newMatches++;
            }
            int indexedQueries = QueryCount(weapons);
            Check(oldMatches == frames * weaponCount && newMatches == oldMatches,
                "dense fighter weapon states still resolve every matching source weapon");
            Check(oldQueries == frames * weaponCount * weaponCount
                && indexedQueries == frames * weaponCount,
                "frame-local index calls parent-slot lookup once per weapon, not per visual");
            Console.WriteLine("INFO: " + frames + " synthetic fighter frames × " + weaponCount +
                " weapon visuals: old parent-slot lookups " + oldQueries +
                ", indexed lookups " + indexedQueries);

            // Native ship templates can be recreated between remote snapshots.
            var old = weapons[0];
            var previous = new BattleWeaponSlotIndex<NativeWeapon>(weapons, SlotOf);
            var replacement = new NativeWeapon("weapon-0");
            weapons[0] = replacement;
            Check(ReferenceEquals(previous.SingleOrDefault("weapon-0"), old),
                "earlier frame retains only its original native instance");
            var next = new BattleWeaponSlotIndex<NativeWeapon>(weapons, SlotOf);
            Check(ReferenceEquals(next.SingleOrDefault("weapon-0"), replacement)
                && !ReferenceEquals(next.SingleOrDefault("weapon-0"), old),
                "new frame observes replacement weapon without caching a stale Unity component");
            // A new slot can be added between frames; never rely on an old index.
            var withNew = weapons.Concat(new[] { new NativeWeapon("added") }).ToArray();
            Check(next.SingleOrDefault("added") == null,
                "previous snapshot cannot invent an unobserved weapon");
            var afterAdd = new BattleWeaponSlotIndex<NativeWeapon>(withNew, SlotOf);
            Check(ReferenceEquals(afterAdd.SingleOrDefault("added"), withNew[weaponCount]),
                "current snapshot sees the newly installed weapon");

            Console.WriteLine("PASS: " + passed + " linked production fighter weapon slot checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
