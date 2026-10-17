using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Wing
    {
        internal readonly long Carrier;
        internal readonly int Index;
        internal readonly string Bay;
        internal Wing(long carrier, int index, string bay)
        { Carrier = carrier; Index = index; Bay = bay; }
    }
    private sealed class Fighter
    {
        internal readonly long Carrier;
        internal readonly int WingIndex, Slot;
        internal readonly long RuntimeId;
        internal readonly string Bay;
        internal Fighter(long carrier, int wingIndex, int slot, long runtimeId, string bay)
        { Carrier = carrier; WingIndex = wingIndex; Slot = slot; RuntimeId = runtimeId; Bay = bay; }
    }
    private sealed class State
    {
        internal readonly List<Wing> Wings = new List<Wing>();
        internal readonly List<Fighter> Fighters = new List<Fighter>();
    }

    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + message);
        checks++;
    }
    private static string OldKey(long carrier, int wing)
    { return carrier + "/" + wing; }
    private static BattleFighterWingIdentity NewKey(long carrier, int wing)
    { return new BattleFighterWingIdentity(carrier, wing); }
    private static string ExceptionText(Exception error)
    { return error.GetType().Name + ": " + error.Message; }
    private static string OldOccupancy(Fighter f)
    { return OldKey(f.Carrier, f.WingIndex) + "/" + f.Slot; }
    private static string NewOccupancy(Fighter f)
    { return NewKey(f.Carrier, f.WingIndex).ToString() + "/" + f.Slot; }

    private static string ValidateOld(State frame)
    {
        try
        {
            var wings = new Dictionary<string, Wing>(StringComparer.Ordinal);
            foreach (Wing wing in frame.Wings) wings.Add(OldKey(wing.Carrier, wing.Index), wing);
            // Sparse active-fighter frames still construct the exact same
            // slash-delimited slot occupancy key, independent of wing index.
            var slots = frame.Fighters.Count < 8
                ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (Fighter fighter in frame.Fighters)
            {
                Wing wing;
                if (slots != null && !slots.Add(OldOccupancy(fighter)))
                    throw new InvalidOperationException("玩家舰载机身份、母舰槽位或伤损状态不一致。");
                if (!wings.TryGetValue(OldKey(fighter.Carrier, fighter.WingIndex), out wing) ||
                    wing.Bay != fighter.Bay)
                    throw new InvalidOperationException("玩家舰载机身份、母舰槽位或伤损状态不一致。");
            }
            return "accepted";
        }
        catch (Exception error) { return ExceptionText(error); }
    }
    private static string ValidateNew(State frame)
    {
        try
        {
            var wings = new Dictionary<BattleFighterWingIdentity, Wing>();
            foreach (Wing wing in frame.Wings) wings.Add(NewKey(wing.Carrier, wing.Index), wing);
            var slots = frame.Fighters.Count < 8
                ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (Fighter fighter in frame.Fighters)
            {
                Wing wing;
                if (slots != null && !slots.Add(NewOccupancy(fighter)))
                    throw new InvalidOperationException("玩家舰载机身份、母舰槽位或伤损状态不一致。");
                if (!wings.TryGetValue(NewKey(fighter.Carrier, fighter.WingIndex), out wing) ||
                    wing.Bay != fighter.Bay)
                    throw new InvalidOperationException("玩家舰载机身份、母舰槽位或伤损状态不一致。");
            }
            return "accepted";
        }
        catch (Exception error) { return ExceptionText(error); }
    }
    private static string ApplyOld(State frame, List<Wing> applied)
    {
        try
        {
            var wings = new Dictionary<string, Wing>(StringComparer.Ordinal);
            var nativeSlots = new Dictionary<string, Wing>(StringComparer.Ordinal);
            foreach (Wing wing in frame.Wings)
            {
                string key = OldKey(wing.Carrier, wing.Index);
                wings.Add(key, wing);
                if (wing.Index % 2 == 0) nativeSlots.Add(key, wing);
            }
            foreach (Wing wing in frame.Wings)
            {
                string key = OldKey(wing.Carrier, wing.Index);
                applied.Add(wings[key]);
                Wing source;
                if (wing.Index % 2 == 0 &&
                    (!nativeSlots.TryGetValue(key, out source) || !ReferenceEquals(source, wing)))
                    throw new InvalidOperationException("Native wing slot source was lost.");
            }
            foreach (Fighter fighter in frame.Fighters)
                applied.Add(wings[OldKey(fighter.Carrier, fighter.WingIndex)]);
            return "accepted";
        }
        catch (Exception error) { return ExceptionText(error); }
    }
    private static string ApplyNew(State frame, List<Wing> applied)
    {
        try
        {
            var wings = new Dictionary<BattleFighterWingIdentity, Wing>();
            var nativeSlots = new Dictionary<BattleFighterWingIdentity, Wing>();
            foreach (Wing wing in frame.Wings)
            {
                BattleFighterWingIdentity key = NewKey(wing.Carrier, wing.Index);
                wings.Add(key, wing);
                if (wing.Index % 2 == 0) nativeSlots.Add(key, wing);
            }
            foreach (Wing wing in frame.Wings)
            {
                BattleFighterWingIdentity key = NewKey(wing.Carrier, wing.Index);
                applied.Add(wings[key]);
                Wing source;
                if (wing.Index % 2 == 0 &&
                    (!nativeSlots.TryGetValue(key, out source) || !ReferenceEquals(source, wing)))
                    throw new InvalidOperationException("Native wing slot source was lost.");
            }
            foreach (Fighter fighter in frame.Fighters)
                applied.Add(wings[NewKey(fighter.Carrier, fighter.WingIndex)]);
            return "accepted";
        }
        catch (Exception error) { return ExceptionText(error); }
    }
    private static void Compare(State frame, string label)
    {
        string before = ValidateOld(frame);
        string after = ValidateNew(frame);
        Check(before == after, label + " validation error/acceptance old=" + before + " new=" + after);
        if (before != "accepted") return; // Native apply only follows complete validation.

        var oldApplied = new List<Wing>();
        var newApplied = new List<Wing>();
        before = ApplyOld(frame, oldApplied);
        after = ApplyNew(frame, newApplied);
        Check(before == after, label + " native apply error/acceptance old=" + before + " new=" + after);
        Check(oldApplied.Count == newApplied.Count, label + " number of applied native wings");
        for (int i = 0; i < oldApplied.Count; i++)
            Check(ReferenceEquals(oldApplied[i], newApplied[i]),
                label + " exact same native wing reference in original order " + i);
    }
    private static long Allocated(Action action, int frames)
    {
        for (int i = 0; i < 80; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Main()
    {
        try
        {
            long[] ids = { long.MinValue, -1, 0, 1, 1L << 32,
                (1L << 32) | 1, long.MaxValue };
            int[] indices = { int.MinValue, -1, 0, 1, 255, 256, int.MaxValue };
            foreach (long carrier in ids)
                foreach (int index in indices)
                {
                    var key = NewKey(carrier, index);
                    Check(OldKey(carrier, index) == key.ToString(),
                        "original wing key text is preserved on demand");
                    Check(key.Equals(NewKey(carrier, index)) &&
                        key.GetHashCode() == NewKey(carrier, index).GetHashCode(),
                        "equal native wing identity produces equal hash codes");
                }
            var collisionA = NewKey(0, 5);
            var collisionB = NewKey((1L << 32) | 1, 5);
            Check(collisionA.GetHashCode() == collisionB.GetHashCode() &&
                !collisionA.Equals(collisionB) &&
                new HashSet<BattleFighterWingIdentity> { collisionA, collisionB }.Count == 2,
                "full 64-bit carrier UID is compared even during an intentional hash collision");
            Check(!NewKey(1, 0).Equals(NewKey(1, 1)) &&
                !NewKey(1, 0).Equals(NewKey(2, 0)),
                "wing number and carrier UID independently identify a native wing");

            Compare(new State(), "empty snapshot");
            for (int size = 0; size <= 12; size++)
            {
                var frame = new State();
                frame.Wings.Add(new Wing(1, 0, "bay-0"));
                for (int i = 0; i < size; i++)
                    frame.Fighters.Add(new Fighter(1, 0, i, 100 + i, "bay-0"));
                Compare(frame, "sparse vs dense active fighter threshold " + size);
                if (size > 0)
                {
                    frame.Fighters[0] = new Fighter(1, 0, 0, 100, "wrong-bay");
                    Compare(frame, "incorrect bay rejected at threshold " + size);
                    frame.Fighters[0] = new Fighter(1, 1, 0, 100, "bay-0");
                    Compare(frame, "unregistered native wing rejected at threshold " + size);
                }
            }
            var repeated = new State();
            repeated.Wings.Add(new Wing(long.MaxValue, 100, "A"));
            repeated.Wings.Add(new Wing(long.MaxValue, 100, "A"));
            Compare(repeated, "duplicate wing identity preserves Dictionary.Add exception text");
            var differentCarriers = new State();
            differentCarriers.Wings.Add(new Wing(long.MinValue, 0, "main"));
            differentCarriers.Wings.Add(new Wing(long.MaxValue, 0, "main"));
            differentCarriers.Wings.Add(new Wing(0, 0, "main"));
            differentCarriers.Wings.Add(new Wing(1L << 32, 0, "main"));
            differentCarriers.Wings.Add(new Wing((1L << 32) | 1, 0, "main"));
            foreach (Wing wing in differentCarriers.Wings)
                differentCarriers.Fighters.Add(new Fighter(wing.Carrier, wing.Index,
                    1, 5, wing.Bay));
            Compare(differentCarriers,
                "different 64-bit carrier IDs with identical wing numbers remain distinct");

            var random = new Random(20261018);
            for (int i = 0; i < 6500; i++)
            {
                var frame = new State();
                int wingCount = random.Next(0, 55);
                for (int j = 0; j < wingCount; j++)
                {
                    long carrier = random.Next(0, 5) == 0 ? ids[random.Next(ids.Length)]
                        : random.Next(-15, 160);
                    int wing = random.Next(0, 16);
                    string bay = "bay:" + random.Next(0, 5);
                    frame.Wings.Add(new Wing(carrier, wing, bay));
                    if (random.Next(0, 18) == 0 && frame.Wings.Count != 0)
                        frame.Wings.Add(frame.Wings[random.Next(frame.Wings.Count)]);
                }
                int fighterCount = random.Next(0, 120);
                for (int j = 0; j < fighterCount; j++)
                {
                    Wing source = frame.Wings.Count > 0 && random.Next(0, 8) != 0
                        ? frame.Wings[random.Next(frame.Wings.Count)] : null;
                    long carrier = source == null ? random.Next(3000, 5000) : source.Carrier;
                    int wing = source == null ? random.Next(0, 16) : source.Index;
                    string bay = source == null ? "unknown" : source.Bay;
                    if (random.Next(0, 23) == 0) bay = "wrong";
                    frame.Fighters.Add(new Fighter(carrier, wing,
                        random.Next(0, 32), 10000 + j, bay));
                }
                Compare(frame, "random original wing dictionary + native apply " + i);
            }

            // Compare repeated per-fighter allocations during a busy checkpoint
            // while keeping the same number of dictionaries and lookups.
            const int wingCountDense = 64, fighterCountDense = 2048, frames = 400;
            var dense = new State();
            for (int i = 0; i < wingCountDense; i++)
                dense.Wings.Add(new Wing(10000 + i / 8, i % 8, "bay-" + i));
            for (int i = 0; i < fighterCountDense; i++)
            {
                Wing wing = dense.Wings[i % wingCountDense];
                dense.Fighters.Add(new Fighter(wing.Carrier, wing.Index,
                    i / 64, 100000 + i, wing.Bay));
            }
            Compare(dense, "high density complete fighter snapshot");

            Action legacy = () =>
            {
                var index = new Dictionary<string, Wing>(StringComparer.Ordinal);
                foreach (Wing wing in dense.Wings) index.Add(OldKey(wing.Carrier, wing.Index), wing);
                foreach (Fighter fighter in dense.Fighters)
                {
                    Wing wing;
                    if (!index.TryGetValue(OldKey(fighter.Carrier, fighter.WingIndex), out wing))
                        throw new InvalidOperationException("missing old wing");
                }
            };
            Action modern = () =>
            {
                var index = new Dictionary<BattleFighterWingIdentity, Wing>();
                foreach (Wing wing in dense.Wings) index.Add(NewKey(wing.Carrier, wing.Index), wing);
                foreach (Fighter fighter in dense.Fighters)
                {
                    Wing wing;
                    if (!index.TryGetValue(NewKey(fighter.Carrier, fighter.WingIndex), out wing))
                        throw new InvalidOperationException("missing numeric wing");
                }
            };
            long oldBytes = Allocated(legacy, frames);
            long newBytes = Allocated(modern, frames);
            Check(oldBytes > newBytes && oldBytes - newBytes > 1000000,
                "production numeric wing identities reduce repeated frame allocation");
            Console.WriteLine("INFO: " + frames + " synthetic checkpoints × " +
                fighterCountDense + " fighters across " + wingCountDense +
                " wings: legacy string-key lookup " + oldBytes +
                " B, linked production numeric wing key " + newBytes +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " linked native fighter wing identity and apply checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
