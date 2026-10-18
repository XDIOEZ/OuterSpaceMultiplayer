using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Fighter
    {
        internal readonly long Carrier, Runtime;
        internal readonly int Wing, Slot;
        internal Fighter(long carrier, int wing, int slot, long runtime)
        { Carrier = carrier; Wing = wing; Slot = slot; Runtime = runtime; }
    }

    private static int checks;
    private static BattleFighterReplicaIdentity Id(Fighter fighter)
    { return new BattleFighterReplicaIdentity(fighter.Carrier, fighter.Wing, fighter.Slot, fighter.Runtime); }
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static HashSet<BattleFighterReplicaIdentity> Previous(List<Fighter> fighters)
    {
        // Exactly the pre-optimization production block after the
        // authoritative validation/sequence and wing binding guards.
        var present = new HashSet<BattleFighterReplicaIdentity>();
        foreach (Fighter fighter in fighters) present.Add(Id(fighter));
        return present;
    }
    private static void Compare(
        BattleFighterReplicaPresenceBuffer<Fighter> buffer, List<Fighter> fighters,
        Dictionary<BattleFighterReplicaIdentity, object> registry, string label)
    {
        var oldPresence = Previous(fighters);
        var now = buffer.Capture(fighters);
        try
        {
            Check(now.Count == oldPresence.Count && now.SetEquals(oldPresence),
                label + ": exact four-part UID membership and duplicates");
            foreach (Fighter fighter in fighters)
                Check(now.Contains(Id(fighter)) && oldPresence.Contains(Id(fighter)),
                    label + ": all validated fighter identities remain present");

            // Both models must release exactly the same old-generation
            // native shells, and in the same order, before any new fighter
            // can be registered on a reused wing slot.
            var oldGone = registry.Keys.Where(k => !oldPresence.Contains(k)).ToArray();
            var newGone = BattleFighterReplicaPruner.FindMissing(registry, now);
            Check(oldGone.Length == (newGone == null ? 0 : newGone.Count),
                label + ": same number of old native replica shells retired");
            for (int i = 0; i < oldGone.Length; i++)
                Check(oldGone[i].Equals(newGone[i]), label + ": exact removal order " + i);
            if (oldGone.Length == 0)
                Check(newGone == null, label + ": stable frame skips deletion-list allocation");
        }
        finally { buffer.Release(now); }
    }
    private static long Measure(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Main()
    {
        try
        {
            Check(Throws<ArgumentNullException>(() =>
                new BattleFighterReplicaPresenceBuffer<Fighter>(null)),
                "missing identity projection fails at buffer construction");
            var buffer = new BattleFighterReplicaPresenceBuffer<Fighter>(Id);
            Check(Throws<ArgumentNullException>(() => buffer.Capture(null)),
                "null incoming list is rejected by the capture boundary");
            Check(Throws<ArgumentNullException>(() => buffer.Release(null)),
                "null lease is rejected by the release boundary");
            var registry = new Dictionary<BattleFighterReplicaIdentity, object>();
            var empty = new List<Fighter>();
            Compare(buffer, empty, registry, "empty battle checkpoint");

            long[] uids = { long.MinValue, -1, 0, 1, (1L << 32),
                (1L << 32) | 1, long.MaxValue };
            var edge = new List<Fighter>();
            foreach (long carrier in uids)
                foreach (long runtime in uids)
                    edge.Add(new Fighter(carrier, 1, 2, runtime));
            Compare(buffer, edge, registry, "full signed 64-bit mother ship and runtime range");
            edge.Add(edge[0]);
            Compare(buffer, edge, registry,
                "duplicate wire identities are coalesced as in the old set");
            edge.RemoveAt(edge.Count - 1);
            foreach (Fighter fighter in edge)
                registry.Add(Id(fighter), new object());
            Compare(buffer, edge, registry,
                "all previously allocated fighters still present");
            var replaced = new List<Fighter>(edge);
            replaced[0] = new Fighter(edge[0].Carrier, edge[0].Wing,
                edge[0].Slot, 90000);
            Compare(buffer, replaced, registry,
                "same bay/slot launched a new runtime fighter and retires prior identity");
            Compare(buffer, empty, registry,
                "all formerly active fighters have departed");

            // No collector may clobber the IDs used by an outer despawn
            // callback, even when a nested ApplyPlayerBattleFighters arrives.
            var outer = buffer.Capture(edge);
            var inner = buffer.Capture(replaced);
            Check(!ReferenceEquals(outer, inner) &&
                outer.Count == edge.Count && inner.Count == replaced.Count &&
                outer.Contains(Id(edge[0])) && !inner.Contains(Id(edge[0])),
                "nested received checkpoint leases its own independent membership set");
            buffer.Release(inner);
            Check(outer.Contains(Id(edge[0])) && outer.Count == edge.Count,
                "nested release leaves outer removal decisions unchanged");
            buffer.Release(outer);
            var again = buffer.Capture(edge);
            Check(ReferenceEquals(outer, again) && again.Count == edge.Count,
                "successive stable frames reuse the same native backing set");
            buffer.Release(again);

            // A native selector may fail while inspecting a changed scene.
            // It must propagate that exception and clear all partially added
            // identities so the next authoritative frame can be retried.
            var malformed = new List<Fighter> { edge[0], null, edge[1] };
            Check(Throws<NullReferenceException>(() => Previous(malformed)),
                "legacy selector throws for a malformed null fighter");
            Check(Throws<NullReferenceException>(() => buffer.Capture(malformed)),
                "new selector propagates the same null-fighter exception");
            var recovered = buffer.Capture(new List<Fighter> { edge[1] });
            Check(ReferenceEquals(recovered, outer) && recovered.Count == 1 &&
                recovered.Contains(Id(edge[1])) && !recovered.Contains(Id(edge[0])),
                "failed capture releases partial state before the next frame");
            buffer.Release(recovered);

            // Unlike a for-index loop, a strongly typed List enumerator
            // retains the original exception for mutation during traversal.
            var mutable = new List<Fighter> { edge[0], edge[1] };
            var mutating = new BattleFighterReplicaPresenceBuffer<Fighter>(f =>
            {
                if (ReferenceEquals(f, edge[0])) mutable.Add(edge[2]);
                return Id(f);
            });
            Check(Throws<InvalidOperationException>(() => mutating.Capture(mutable)),
                "native list mutation still interrupts enumeration");
            var freshAfterMutation = mutating.Capture(new List<Fighter> { edge[1] });
            Check(freshAfterMutation.Count == 1 && freshAfterMutation.Contains(Id(edge[1])),
                "list mutation failure returns its cleared buffer for retry");
            mutating.Release(freshAfterMutation);

            // Cleanup inside an active despawn callback must preserve that
            // callback's decisions, then free capacity on the last release.
            var burst = new List<Fighter>();
            for (int i = 0; i < 8192; i++)
                burst.Add(new Fighter(17, i / 256, i % 256, 10000 + i));
            var active = buffer.Capture(burst);
            Check(active.Count == 8192, "large native wing population fills the whole presence set");
            buffer.Reset();
            Check(active.Count == 8192 && active.Contains(Id(burst[8000])),
                "room reset defers clearing a currently leased native ID set");
            var nestedDuringReset = buffer.Capture(new List<Fighter> { edge[0] });
            Check(!ReferenceEquals(nestedDuringReset, active) &&
                nestedDuringReset.SetEquals(new[] { Id(edge[0]) }),
                "nested callback during pending reset cannot interfere with outer IDs");
            buffer.Release(nestedDuringReset);
            buffer.Release(active);
            var nextRoom = buffer.Capture(new List<Fighter> { edge[1] });
            Check(ReferenceEquals(nextRoom, outer) &&
                nextRoom.SetEquals(new[] { Id(edge[1]) }),
                "reconnect has no stale fighter IDs from a previous battle");
            buffer.Release(nextRoom);
            buffer.Reset();

            // Deterministic old/new differential checks: multiple carriers,
            // reused slots, high-bit identities and changing live registries.
            var random = new Random(20261018);
            var existing = new Dictionary<BattleFighterReplicaIdentity, object>();
            for (int iteration = 0; iteration < 4200; iteration++)
            {
                var frame = new List<Fighter>();
                var identities = new HashSet<BattleFighterReplicaIdentity>();
                int size = random.Next(0, 130);
                for (int i = 0; i < size; i++)
                {
                    long carrier = random.Next(0, 5) == 0
                        ? ((long)random.Next(-50000, 50000) << 32) | (uint)random.Next()
                        : random.Next(-300, 300);
                    long runtime = random.Next(0, 5) == 0
                        ? ((long)random.Next(-50000, 50000) << 32) | (uint)random.Next()
                        : random.Next(-5000, 5000);
                    var fighter = new Fighter(carrier, random.Next(0, 20),
                        random.Next(0, 256), runtime);
                    if (identities.Add(Id(fighter))) frame.Add(fighter);
                }
                Compare(buffer, frame, existing, "random replica membership " + iteration);
                // Simulate native old-generation cleanup and new registration
                // after the exact source-equivalent FindMissing result.
                var oldGone = existing.Keys.Where(k => !identities.Contains(k)).ToArray();
                foreach (BattleFighterReplicaIdentity key in oldGone) existing.Remove(key);
                foreach (Fighter fighter in frame)
                    if (!existing.ContainsKey(Id(fighter))) existing.Add(Id(fighter), new object());
            }

            const int ships = 320, frames = 1500;
            var stable = new List<Fighter>(ships);
            var live = new Dictionary<BattleFighterReplicaIdentity, object>(ships);
            for (int i = 0; i < ships; i++)
            {
                var fighter = new Fighter(100 + i / 32, i % 4,
                    i % 32, 100000 + i);
                stable.Add(fighter);
                live.Add(Id(fighter), new object());
            }
            Action oldFrame = () =>
            {
                var present = Previous(stable);
                if (BattleFighterReplicaPruner.FindMissing(live, present) != null)
                    throw new InvalidOperationException("Unexpected old native despawn");
            };
            Action newFrame = () =>
            {
                var present = buffer.Capture(stable);
                try
                {
                    if (BattleFighterReplicaPruner.FindMissing(live, present) != null)
                        throw new InvalidOperationException("Unexpected indexed native despawn");
                }
                finally { buffer.Release(present); }
            };
            long oldBytes = Measure(oldFrame, frames);
            long newBytes = Measure(newFrame, frames);
            Check(oldBytes > 0 && newBytes == 0,
                "production leased fighter membership removes all stable-frame set allocations");
            Console.WriteLine("INFO: " + frames + " synthetic stable frames × " +
                ships + " fighter replicas: original per-frame HashSet " + oldBytes +
                " B, linked production reusable set " + newBytes +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " linked fighter replica presence and native expiry checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
