using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Fighter
    {
        internal long Carrier, RuntimeId;
        internal int Wing, Slot;
        internal Fighter(long carrier, int wing, int slot, long runtimeId)
        { Carrier = carrier; Wing = wing; Slot = slot; RuntimeId = runtimeId; }
    }

    private static int checks;
    private static void Check(bool pass, string label)
    {
        if (!pass) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static bool Throws<T>(Action call) where T : Exception
    {
        try { call(); return false; }
        catch (T) { return true; }
    }
    private static string OldKey(Fighter fighter)
    {
        return fighter.Carrier + "/" + fighter.Wing + "/" +
            fighter.Slot + "/" + fighter.RuntimeId;
    }
    private static BattleFighterReplicaIdentity NewKey(Fighter fighter)
    {
        return new BattleFighterReplicaIdentity(fighter.Carrier, fighter.Wing,
            fighter.Slot, fighter.RuntimeId);
    }
    private static string OldName(Fighter fighter) { return "MultiplayerFighter." + OldKey(fighter); }
    private static string NewName(Fighter fighter) { return "MultiplayerFighter." + NewKey(fighter); }

    private sealed class Comparison
    {
        internal readonly Dictionary<string, object> Original =
            new Dictionary<string, object>(StringComparer.Ordinal);
        internal readonly Dictionary<BattleFighterReplicaIdentity, object> Updated =
            new Dictionary<BattleFighterReplicaIdentity, object>();
        internal int Created, Removed;

        internal void Apply(List<Fighter> incoming, string label)
        {
            // Original ValidateFightersState checks duplicate identities
            // before any native objects are updated. The new one uses exact
            // production identity and has identical membership semantics.
            var oldIds = new HashSet<string>(StringComparer.Ordinal);
            var newIds = new HashSet<BattleFighterReplicaIdentity>();
            foreach (Fighter fighter in incoming)
            {
                bool addedBefore = oldIds.Add(OldKey(fighter));
                bool addedAfter = newIds.Add(NewKey(fighter));
                Check(addedBefore == addedAfter, label + " identity collision/replay");
                if (!addedBefore) return; // Caller rejects this frame before any writes.
            }

            var oldPresent = new HashSet<string>(incoming.Select(OldKey), StringComparer.Ordinal);
            var newPresent = new HashSet<BattleFighterReplicaIdentity>();
            foreach (Fighter fighter in incoming) newPresent.Add(NewKey(fighter));

            string[] oldStale = Original.Keys.Where(key => !oldPresent.Contains(key)).ToArray();
            List<BattleFighterReplicaIdentity> newStale =
                BattleFighterReplicaPruner.FindMissing(Updated, newPresent);
            Check(oldStale.Length == (newStale == null ? 0 : newStale.Count),
                label + " missing generation count");
            for (int i = 0; i < oldStale.Length; i++)
                Check(oldStale[i] == newStale[i].ToString(),
                    label + " old generation removal order");
            foreach (string key in oldStale) Original.Remove(key);
            if (newStale != null)
                foreach (BattleFighterReplicaIdentity key in newStale) Updated.Remove(key);
            Removed += oldStale.Length;

            foreach (Fighter fighter in incoming)
            {
                string old = OldKey(fighter);
                BattleFighterReplicaIdentity modern = NewKey(fighter);
                object oldValue, newValue;
                if (!Original.TryGetValue(old, out oldValue))
                {
                    oldValue = new object();
                    // Both dictionaries must share this exact native shell,
                    // not silently create a replacement on a stable frame.
                    Original.Add(old, oldValue);
                    Updated.Add(modern, oldValue);
                    Created++;
                }
                else
                {
                    Check(Updated.TryGetValue(modern, out newValue) &&
                        ReferenceEquals(oldValue, newValue),
                        label + " existing native replica identity retained");
                }
                Check(NewName(fighter) == OldName(fighter),
                    label + " original GameObject name remains unchanged");
            }
            Check(Original.Count == Updated.Count,
                label + " replica count after pruning and registration");
            string[] beforeOrder = Original.Keys.ToArray();
            string[] nowOrder = Updated.Keys.Select(k => k.ToString()).ToArray();
            Check(beforeOrder.SequenceEqual(nowOrder),
                label + " dictionary insertion/retention order");
            foreach (var item in Updated)
            {
                object originalValue;
                Check(Original.TryGetValue(item.Key.ToString(), out originalValue) &&
                    ReferenceEquals(originalValue, item.Value),
                    label + " native reference resolves from every four-field key");
            }
        }
    }

    private static void TestId(long carrier, int wing, int slot, long runtime)
    {
        var item = new Fighter(carrier, wing, slot, runtime);
        Check(OldKey(item) == NewKey(item).ToString(),
            "old and new four-field identity format");
        Check(OldName(item) == NewName(item),
            "new native GameObject naming preserves old human-readable identity");
        var again = NewKey(new Fighter(carrier, wing, slot, runtime));
        Check(NewKey(item).Equals(again) &&
            NewKey(item).GetHashCode() == again.GetHashCode(),
            "equal native fighter identities have equal hash codes");
    }
    private static long Allocated(Action work, int frames)
    {
        for (int i = 0; i < 60; i++) work();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) work();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    private static void Benchmark()
    {
        const int fighters = 320, frames = 600;
        Fighter[] sample = Enumerable.Range(0, fighters)
            .Select(i => new Fighter((i % 7) + 1L, i % 4, i / 28, 10000 + i))
            .ToArray();
        var oldReplicas = new Dictionary<string, object>(StringComparer.Ordinal);
        var newReplicas = new Dictionary<BattleFighterReplicaIdentity, object>();
        foreach (Fighter fighter in sample)
        {
            object token = new object();
            oldReplicas.Add(OldKey(fighter), token);
            newReplicas.Add(NewKey(fighter), token);
        }
        Action previousFrame = () =>
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Fighter fighter in sample)
                if (!ids.Add(OldKey(fighter))) throw new Exception("Duplicate UID");
            var present = new HashSet<string>(sample.Select(OldKey), StringComparer.Ordinal);
            foreach (string key in oldReplicas.Keys.Where(key => !present.Contains(key)).ToArray())
                oldReplicas.Remove(key);
            foreach (Fighter fighter in sample)
                if (!oldReplicas.ContainsKey(OldKey(fighter))) throw new Exception("Missing fighter");
        };
        Action indexedFrame = () =>
        {
            var ids = new HashSet<BattleFighterReplicaIdentity>();
            foreach (Fighter fighter in sample)
                if (!ids.Add(NewKey(fighter))) throw new Exception("Duplicate UID");
            var present = new HashSet<BattleFighterReplicaIdentity>();
            foreach (Fighter fighter in sample) present.Add(NewKey(fighter));
            if (BattleFighterReplicaPruner.FindMissing(newReplicas, present) != null)
                throw new Exception("Stable frame falsely pruned a fighter");
            foreach (Fighter fighter in sample)
                if (!newReplicas.ContainsKey(NewKey(fighter)))
                    throw new Exception("Missing indexed fighter");
        };
        long oldBytes = Allocated(previousFrame, frames);
        long newBytes = Allocated(indexedFrame, frames);
        Check(oldBytes > newBytes && oldBytes - newBytes > 2000000,
            "composite UID key and no-stale pruning measurably reduce per-frame allocations");
        Console.WriteLine("INFO: " + frames + " stable fighter snapshots × " + fighters +
            " native fighters: old slash-delimited identity and LINQ pruning " +
            oldBytes + " B, linked production numeric identity and lazy pruning " +
            newBytes + " B managed allocations after warmup");
    }

    private static int Main()
    {
        try
        {
            var extremes = new long[] {
                long.MinValue, -1, 0, 1, 1L << 32, (1L << 32) | 1, long.MaxValue
            };
            foreach (long carrier in extremes)
                foreach (long runtime in extremes)
                    foreach (int wing in new int[] { int.MinValue, -1, 0, 1, 255, int.MaxValue })
                        TestId(carrier, wing, 0, runtime);
            var first = new BattleFighterReplicaIdentity(0, 1, 2, 3);
            var clash = new BattleFighterReplicaIdentity((1L << 32) | 1, 1, 2, 3);
            Check(first.GetHashCode() == clash.GetHashCode() && !first.Equals(clash) &&
                new HashSet<BattleFighterReplicaIdentity> { first, clash }.Count == 2,
                "equal low/high UID hash collision cannot merge different carrier IDs");
            Check(!first.Equals(new BattleFighterReplicaIdentity(0, 1, 2, 4)) &&
                !first.Equals(new BattleFighterReplicaIdentity(0, 1, 3, 3)) &&
                !first.Equals(new BattleFighterReplicaIdentity(0, 2, 2, 3)) &&
                !first.Equals(new BattleFighterReplicaIdentity(1, 1, 2, 3)),
                "runtime, slot, wing and carrier remain independently significant");
            Check(Throws<ArgumentNullException>(() =>
                BattleFighterReplicaPruner.FindMissing<object>(null,
                    new HashSet<BattleFighterReplicaIdentity>())),
                "null live native replica dictionary is rejected");
            Check(Throws<ArgumentNullException>(() =>
                BattleFighterReplicaPruner.FindMissing(
                    new Dictionary<BattleFighterReplicaIdentity, object>(), null)),
                "null current fighter UID set is rejected");

            var comparison = new Comparison();
            var stable = new List<Fighter>();
            for (int i = 0; i < 50; i++)
                stable.Add(new Fighter(123, i / 10, i % 10, i + 500));
            comparison.Apply(stable, "initial burst of native carrier fighters");
            int created = comparison.Created;
            comparison.Apply(stable, "same fighter state checkpoint");
            Check(comparison.Created == created && comparison.Removed == 0,
                "unchanged fighter checkpoint retains all native shells");
            Check(BattleFighterReplicaPruner.FindMissing(comparison.Updated,
                new HashSet<BattleFighterReplicaIdentity>(stable.Select(NewKey))) == null,
                "no despawn allocates no temporary native key-removal list");
            var relaunched = stable.ToList();
            relaunched[0] = new Fighter(123, 0, 0, 100500);
            comparison.Apply(relaunched, "fighter respawns with new runtime UID in same slot");
            Check(comparison.Created == created + 1 && comparison.Removed == 1,
                "old fighter shell removed before new slot registration");
            comparison.Apply(relaunched.Skip(10).ToList(),
                "wing detaches and native fighters leave");
            Check(comparison.Removed == 11, "all departing native fighter replicas released");
            comparison.Apply(new List<Fighter>(), "battle ends with no native fighters");
            Check(comparison.Original.Count == 0 && comparison.Updated.Count == 0,
                "cleared battle frame leaves no stale fighter replicas");
            comparison.Apply(stable, "reconnection recreates original runtime shell identities");

            // Validate duplicate UID rejection (both exact duplicates and
            // independent carrier/wing/slot/runtime identity components).
            var duplicated = stable.ToList();
            duplicated.Add(new Fighter(stable[7].Carrier, stable[7].Wing,
                stable[7].Slot, stable[7].RuntimeId));
            int oldCount = comparison.Original.Count, newCount = comparison.Updated.Count;
            comparison.Apply(duplicated, "duplicate incoming four-field identity");
            Check(oldCount == comparison.Original.Count && newCount == comparison.Updated.Count,
                "duplicate frame is rejected before any native replica mutation");

            var random = new Random(20261018);
            for (int iteration = 0; iteration < 4200; iteration++)
            {
                var snapshot = new List<Fighter>();
                var unique = new HashSet<string>(StringComparer.Ordinal);
                int count = random.Next(0, 130);
                for (int i = 0; i < count; i++)
                {
                    long carrier = random.Next(0, 7) == 0
                        ? (long)random.Next(-4000, 4000) << 32 | (uint)random.Next()
                        : random.Next(-1000, 1000);
                    int wing = random.Next(-1, 15), slot = random.Next(-2, 34);
                    long runtime = random.Next(0, 4) == 0
                        ? (long)random.Next(-4000, 4000) << 32 | (uint)random.Next()
                        : random.Next(-10000, 10000);
                    var fighter = new Fighter(carrier, wing, slot, runtime);
                    if (unique.Add(OldKey(fighter))) snapshot.Add(fighter);
                }
                if (random.Next(0, 15) == 0 && snapshot.Count != 0)
                    snapshot.Add(snapshot[0]);
                comparison.Apply(snapshot, "random native battle frame " + iteration);
            }
            Benchmark();
            Console.WriteLine("PASS: " + checks +
                " production-linked native fighter replica identity checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
