using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Pose
    {
        internal readonly long Id;
        internal Pose(long id) { Id = id; }
    }
    private sealed class Replica
    {
        internal readonly long Id;
        internal bool Expired;
        internal Replica(long id) { Id = id; }
    }
    private static readonly Func<Pose, long> IdOf = p => p.Id;
    private static int checks;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static HashSet<long> Previous(IEnumerable<Pose> projectiles, IEnumerable<Pose> vfx)
    {
        // Precisely the original ApplyPlayerBattleEffects presence expression.
        return new HashSet<long>(projectiles.Concat(vfx).Select(p => p.Id));
    }
    private static void Equal(List<Pose> projectiles, List<Pose> vfx,
        BattleEffectPresenceBuffer buffer, string description)
    {
        HashSet<long> old = Previous(projectiles, vfx);
        HashSet<long> present = buffer.Capture(projectiles, vfx, IdOf);
        try
        {
            Check(present.SetEquals(old) && present.Count == old.Count,
                description + " full authoritative UID membership");
            foreach (Pose pose in projectiles)
                Check(present.Contains(pose.Id), description + " existing projectile ID");
            foreach (Pose pose in vfx)
                Check(present.Contains(pose.Id), description + " existing VFX ID");
            // The only production consumer checks which old replicas are no
            // longer present before scheduling their short visual fade.
            var oldReplicas = new List<Replica>();
            var newReplicas = new List<Replica>();
            for (int id = -4; id <= 100; id++)
            {
                oldReplicas.Add(new Replica(id));
                newReplicas.Add(new Replica(id));
            }
            foreach (Replica replica in oldReplicas)
                if (!old.Contains(replica.Id)) replica.Expired = true;
            foreach (Replica replica in newReplicas)
                if (!present.Contains(replica.Id)) replica.Expired = true;
            for (int i = 0; i < oldReplicas.Count; i++)
                Check(oldReplicas[i].Expired == newReplicas[i].Expired,
                    description + " absent replica expires identically");
        }
        finally { buffer.Release(present); }
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
            var buffer = new BattleEffectPresenceBuffer();
            Check(Throws<ArgumentNullException>(() =>
                    buffer.Capture<Pose>(null, new Pose[0], IdOf)),
                "null projectile collection is rejected");
            Check(Throws<ArgumentNullException>(() =>
                    buffer.Capture(new Pose[0], null, IdOf)),
                "null VFX collection is rejected");
            Check(Throws<ArgumentNullException>(() =>
                    buffer.Capture(new Pose[0], new Pose[0], null)),
                "null UID selector is rejected");
            Check(Throws<ArgumentNullException>(() => buffer.Release(null)),
                "null frame lease is rejected");

            var empty = new List<Pose>();
            Equal(empty, empty, buffer, "empty visual frame");
            Equal(new List<Pose> { new Pose(0) }, empty, buffer,
                "one projectile with zero UID");
            Equal(empty, new List<Pose> { new Pose(long.MinValue) }, buffer,
                "one native VFX with minimum signed UID");
            Equal(new List<Pose> { new Pose(long.MaxValue), new Pose(-1) },
                new List<Pose> { new Pose(1L << 32), new Pose(0) }, buffer,
                "64-bit and negative signed visual IDs remain distinct");
            Equal(new List<Pose> { new Pose(77), new Pose(77), new Pose(-1) },
                new List<Pose> { new Pose(77), new Pose(77), new Pose(-1) }, buffer,
                "duplicates within and across projectile/VFX lists collapse");

            // Both the old LINQ selector and the production helper encounter
            // a corrupt null pose at the same stage. Decode validation rejects
            // this first, but this helper must also release on failure.
            var malformed = new List<Pose> { new Pose(10), null, new Pose(11) };
            Check(Throws<NullReferenceException>(() => Previous(malformed, empty)),
                "old pose UID selector fails on malformed null projectile");
            Check(Throws<NullReferenceException>(() => buffer.Capture(malformed, empty, IdOf)),
                "new helper propagates malformed null projectile failure");
            var afterFailure = buffer.Capture(new[] { new Pose(11) }, empty, IdOf);
            Check(afterFailure.Count == 1 && afterFailure.Contains(11) &&
                !afterFailure.Contains(10),
                "partially failed capture does not leave stale native UID in a retry");
            buffer.Release(afterFailure);

            var projectile = new List<Pose> { new Pose(101), new Pose(102) };
            var vfx = new List<Pose> { new Pose(201) };
            var outer = buffer.Capture(projectile, vfx, IdOf);
            var nested = buffer.Capture(new[] { new Pose(300) },
                new[] { new Pose(301) }, IdOf);
            Check(!ReferenceEquals(outer, nested) &&
                outer.SetEquals(new long[] { 101, 102, 201 }) &&
                nested.SetEquals(new long[] { 300, 301 }),
                "reentrant visual frame leases cannot clear or mix each other's native IDs");
            buffer.Release(nested);
            Check(outer.Count == 3 && outer.Contains(102) && !outer.Contains(300),
                "nested release leaves outer frame expiration guard intact");
            buffer.Release(outer);
            var reused = buffer.Capture(projectile, vfx, IdOf);
            Check(ReferenceEquals(outer, reused) && reused.Count == 3,
                "steady received frames reuse their same UID backing buckets");
            buffer.Release(reused);

            // A custom game pose getter may throw or trigger another frame.
            int calls = 0;
            Check(Throws<InvalidOperationException>(() =>
                buffer.Capture(new[] { new Pose(1), new Pose(2), new Pose(3) },
                    empty, p =>
                    {
                        if (++calls == 2)
                            throw new InvalidOperationException("native transform unavailable");
                        return p.Id;
                    })),
                "native pose getter error propagates through the capture helper");
            var recovered = buffer.Capture(new[] { new Pose(444) }, empty, IdOf);
            Check(ReferenceEquals(outer, recovered) &&
                recovered.SetEquals(new[] { 444L }),
                "failed native callback releases reusable UID set before next checkpoint");
            buffer.Release(recovered);

            calls = 0;
            var callbackOuter = buffer.Capture(new[] { new Pose(66), new Pose(67) },
                empty, p =>
                {
                    if (++calls == 1)
                    {
                        var inner = buffer.Capture(new[] { new Pose(666) }, empty, IdOf);
                        Check(inner.SetEquals(new[] { 666L }),
                            "nested native callback receives its own IDs");
                        buffer.Release(inner);
                    }
                    return p.Id;
                });
            Check(callbackOuter.SetEquals(new long[] { 66, 67 }),
                "native callback reentrancy preserves original capture");
            buffer.Release(callbackOuter);

            // List<T>.Enumerator must still detect an unexpected native
            // mutation during traversal; avoid switching to index loops,
            // which would silently accept the new entry mid-checkpoint.
            var mutating = new List<Pose> { new Pose(51), new Pose(52) };
            Check(Throws<InvalidOperationException>(() =>
                buffer.Capture(mutating, empty, p =>
                {
                    if (p.Id == 51) mutating.Add(new Pose(53));
                    return p.Id;
                })),
                "typed List iterator retains original mid-capture mutation rejection");
            var afterMutation = buffer.Capture(new[] { new Pose(52) }, empty, IdOf);
            Check(afterMutation.SetEquals(new long[] { 52 }),
                "list mutation failure does not leak partial UID membership");
            buffer.Release(afterMutation);

            var highWaterProjectiles = new List<Pose>();
            for (long uid = 0; uid < 8192; uid++) highWaterProjectiles.Add(new Pose(uid));
            var active = buffer.Capture(highWaterProjectiles, empty, IdOf);
            Check(active.Count == 8192, "large visual burst can reserve all 8192 active UIDs");
            buffer.Reset();
            Check(active.Count == 8192 && active.Contains(8191),
                "scene cleanup during a callback defers clearing an active visual frame");
            var nestedAfterReset = buffer.Capture(new[] { new Pose(90000) }, empty, IdOf);
            Check(!ReferenceEquals(nestedAfterReset, active) &&
                nestedAfterReset.SetEquals(new[] { 90000L }),
                "reset-pending nested frame remains independent");
            buffer.Release(nestedAfterReset);
            buffer.Release(active);
            var nextRoom = buffer.Capture(new[] { new Pose(2) }, empty, IdOf);
            Check(ReferenceEquals(active, nextRoom) && nextRoom.SetEquals(new[] { 2L }),
                "new room starts with no previous battle's expired or active UIDs");
            buffer.Release(nextRoom);
            buffer.Reset();

            // Deterministic randomized presence comparisons across changing
            // projectile and VFX counts, IDs, collisions and cleared frames.
            var random = new Random(20261018);
            for (int frame = 0; frame < 5000; frame++)
            {
                var projectilesRandom = new List<Pose>();
                var vfxRandom = new List<Pose>();
                int n = random.Next(0, 90), m = random.Next(0, 45);
                for (int i = 0; i < n; i++)
                    projectilesRandom.Add(new Pose(random.Next(0, 4) == 0
                        ? (long)random.Next(-20000, 20000) << 32 | (uint)random.Next()
                        : random.Next(-20, 120)));
                for (int i = 0; i < m; i++)
                    vfxRandom.Add(new Pose(random.Next(0, 4) == 0
                        ? (long)random.Next(-20000, 20000) << 32 | (uint)random.Next()
                        : random.Next(-20, 120)));
                Equal(projectilesRandom, vfxRandom, buffer, "random battle frame " + frame);
            }

            // Restrict the allocation benchmark to this UID-set path, not
            // JSON parsing, Unity object creation or NativeWire reflection.
            const int projectileCount = 256, vfxCount = 64, frames = 1200;
            var stableProjectiles = new Pose[projectileCount];
            var stableVfx = new Pose[vfxCount];
            for (int i = 0; i < projectileCount; i++)
                stableProjectiles[i] = new Pose(i + 10);
            for (int i = 0; i < vfxCount; i++)
                stableVfx[i] = new Pose(i + 10000);
            Action oldFrame = () =>
            {
                HashSet<long> present = Previous(stableProjectiles, stableVfx);
                if (present.Count != projectileCount + vfxCount)
                    throw new InvalidOperationException("Old frame lost a visual ID");
            };
            Action newFrame = () =>
            {
                HashSet<long> present = buffer.Capture(stableProjectiles, stableVfx, IdOf);
                try
                {
                    if (present.Count != projectileCount + vfxCount)
                        throw new InvalidOperationException("New frame lost a visual ID");
                }
                finally { buffer.Release(present); }
            };
            long oldAllocated = Allocated(oldFrame, frames);
            long newAllocated = Allocated(newFrame, frames);
            Check(oldAllocated > newAllocated && newAllocated == 0,
                "steady battle effect presence checks allocate no managed UID buffers");
            Console.WriteLine("INFO: " + frames + " synthetic receive frames × " +
                projectileCount + " projectiles and " + vfxCount + " VFX: old LINQ/HashSet " +
                oldAllocated + " B; linked production presence buffer " + newAllocated +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks + " production battle effect presence checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
