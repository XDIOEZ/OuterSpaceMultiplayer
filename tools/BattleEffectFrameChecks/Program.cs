using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Effect
    {
        internal readonly int Identity;
        internal bool Expired;
        internal int Updates;
        internal Effect(int id) { Identity = id; }
    }
    private static int checks;
    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static List<int> OldTick(Dictionary<int, Effect> live, Action<int> released = null)
    {
        var visited = new List<int>();
        foreach (var pair in live.ToArray()) // Exactly the previous update snapshot.
        {
            visited.Add(pair.Key);
            if (pair.Value.Expired)
            {
                if (released != null) released(pair.Key);
                live.Remove(pair.Key);
            }
            else pair.Value.Updates++;
        }
        return visited;
    }
    private static List<int> NewTick(Dictionary<int, Effect> live,
        BattleEffectFrameSnapshot<int, Effect> frame, Action<int> released = null)
    {
        var visited = new List<int>();
        var captured = frame.Capture(live);
        try
        {
            for (int i = 0; i < captured.Count; i++)
            {
                var pair = captured[i];
                visited.Add(pair.Key);
                if (pair.Value.Expired)
                {
                    if (released != null) released(pair.Key);
                    live.Remove(pair.Key);
                }
                else pair.Value.Updates++;
            }
        }
        finally { frame.Release(captured); }
        return visited;
    }
    // Source-equivalent of the pre-optimization projectile/VFX capture. The
    // same loop runs once for projectiles and then once for VFX; inactive
    // native instances are removed from their dictionary during the walk.
    private static List<int> OldCapture(Dictionary<int, Effect> live, Action<int> onVisit = null)
    {
        var present = new List<int>();
        foreach (var pair in live.ToArray())
        {
            if (onVisit != null) onVisit(pair.Key);
            if (pair.Value.Expired) { live.Remove(pair.Key); continue; }
            present.Add(pair.Key);
        }
        return present;
    }
    // Run the exact production snapshot helper from CapturePlayerBattleEffects
    // with the same iteration/removal order. Only Unity poses are stubbed.
    private static List<int> NewCapture(Dictionary<int, Effect> live,
        BattleEffectFrameSnapshot<int, Effect> frame, Action<int> onVisit = null)
    {
        var present = new List<int>();
        var snapshot = frame.Capture(live);
        try
        {
            foreach (var pair in snapshot)
            {
                if (onVisit != null) onVisit(pair.Key);
                if (pair.Value.Expired) { live.Remove(pair.Key); continue; }
                present.Add(pair.Key);
            }
        }
        finally { frame.Release(snapshot); }
        return present;
    }
    private static Dictionary<int, Effect> Make(int n)
    {
        var result = new Dictionary<int, Effect>();
        for (int i = 0; i < n; i++) result.Add(i, new Effect(i) { Expired = i % 7 == 0 });
        return result;
    }
    private static void SameState(Dictionary<int, Effect> oldValues,
        Dictionary<int, Effect> newValues, string name)
    {
        Check(oldValues.Keys.SequenceEqual(newValues.Keys), name + " key order and removal");
        Check(oldValues.Values.Select(e => e.Updates).SequenceEqual(newValues.Values.Select(e => e.Updates)),
            name + " update counts");
    }
    private static long Allocated(Action tick, int rounds)
    {
        for (int i = 0; i < 50; i++) tick(); // JIT warm-up and reusable buffer growth.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < rounds; i++) tick();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public static int Main()
    {
        try
        {
            var buffer = new BattleEffectFrameSnapshot<int, Effect>();
            Check(Throws<ArgumentNullException>(() => buffer.Capture(null)), "null source is rejected");
            Check(Throws<ArgumentNullException>(() => buffer.Release(null)), "null release is rejected");
            var empty = new Dictionary<int, Effect>();
            var firstEmpty = buffer.Capture(empty);
            Check(firstEmpty.Count == 0, "empty frame yields empty snapshot");
            buffer.Release(firstEmpty);
            var again = buffer.Capture(empty);
            Check(ReferenceEquals(firstEmpty, again), "empty frames reuse the same list");
            buffer.Release(again);

            var oldValues = Make(70);
            var newValues = Make(70);
            var oldOrder = OldTick(oldValues);
            var newOrder = NewTick(newValues, buffer);
            Check(oldOrder.SequenceEqual(newOrder), "mixed active and expired entries keep the old snapshot order");
            SameState(oldValues, newValues, "mixed expiration");
            Check(newValues.Count == 60, "expired items are removed exactly once");
            Check(OldTick(oldValues).SequenceEqual(NewTick(newValues, buffer)),
                "second frame visits only surviving effects");
            SameState(oldValues, newValues, "second frame");
            Check(oldValues.Values.All(e => e.Updates == 2) && newValues.Values.All(e => e.Updates == 2),
                "surviving projectile and VFX updates continue normally");

            // Native VFX despawn callbacks can add new effects, remove peers, or
            // reenter update. The snapshot must remain independent of the live
            // dictionary for the entire current call.
            var legacyReentrant = Make(10);
            var optimizedReentrant = Make(10);
            Action<int> mutateLegacy = id =>
            {
                if (id == 0)
                {
                    legacyReentrant.Remove(4);
                    legacyReentrant.Add(99, new Effect(99));
                }
            };
            Action<int> mutateNew = id =>
            {
                if (id == 0)
                {
                    optimizedReentrant.Remove(4);
                    optimizedReentrant.Add(99, new Effect(99));
                }
            };
            var originalVisited = OldTick(legacyReentrant, mutateLegacy);
            var optimizedVisited = NewTick(optimizedReentrant, buffer, mutateNew);
            Check(originalVisited.SequenceEqual(optimizedVisited),
                "VFX native callback mutations do not change the current snapshot");
            SameState(legacyReentrant, optimizedReentrant, "callback mutation");
            Check(optimizedReentrant[99].Updates == 0,
                "effect born during callback is not processed until a later frame");
            Check(OldTick(legacyReentrant).SequenceEqual(NewTick(optimizedReentrant, buffer)),
                "a newly born effect is processed on the next snapshot");
            SameState(legacyReentrant, optimizedReentrant, "newly born frame");

            var nestedSource = Make(9);
            var outer = buffer.Capture(nestedSource);
            nestedSource.Add(99, new Effect(99));
            var nested = buffer.Capture(nestedSource);
            Check(!ReferenceEquals(nested, outer) && nested.Count == outer.Count + 1,
                "nested VFX update gets an independent snapshot");
            Check(outer.Count == 9 && outer.All(pair => pair.Key != 99),
                "nested snapshot does not corrupt the enclosing traversal");
            buffer.Release(nested);
            Check(outer.Count == 9 && outer[0].Key == 0,
                "nested release cannot clear the enclosing frame");
            buffer.Release(outer);
            var afterNested = buffer.Capture(nestedSource);
            Check(ReferenceEquals(afterNested, outer) && afterNested.Count == 10,
                "outer release restores reuse and next frame sees the new effect");
            buffer.Release(afterNested);

            // Error in native ReleaseBattleEffect must not leave a stale lease.
            var failing = Make(2);
            Check(Throws<InvalidOperationException>(() => NewTick(failing, buffer,
                id => { throw new InvalidOperationException("simulated native pool exception"); })),
                "native despawn exception is propagated");
            var clean = buffer.Capture(failing);
            Check(ReferenceEquals(clean, outer) && clean.Count == failing.Count,
                "exceptional update releases buffer and its retained object references");
            buffer.Release(clean);
            Check(clean.Count == 0, "release clears all strong object references");

            var pendingReset = buffer.Capture(Make(100));
            Check(pendingReset.Capacity >= 100, "large scene snapshot grows only as needed");
            buffer.Reset();
            Check(pendingReset.Count == 100,
                "scene cleanup while a frame is leased defers storage reset safely");
            buffer.Release(pendingReset);
            Check(pendingReset.Count == 0 && pendingReset.Capacity == 0,
                "deferred cleanup returns large snapshot storage after enclosing frame");
            var afterReset = buffer.Capture(Make(3));
            Check(afterReset.Count == 3 && afterReset.Capacity < 100,
                "subsequent battle starts with correctly sized snapshot storage");
            buffer.Release(afterReset);
            buffer.Reset();
            Check(afterReset.Capacity == 0,
                "normal scene shutdown returns the backing storage to the runtime");

            // The authoritative capture path has two independently mutable
            // dictionaries. It snapshots projectiles first, then VFX; native
            // callbacks may add VFX while capturing the earlier dictionary.
            var oldProjectiles = Make(48);
            var newProjectiles = Make(48);
            var oldVfx = Make(18);
            var newVfx = Make(18);
            var projectileCapture = new BattleEffectFrameSnapshot<int, Effect>();
            var vfxCapture = new BattleEffectFrameSnapshot<int, Effect>();
            Action<int> originalProjectileCallback = id =>
            {
                if (id == 0) oldVfx.Add(500, new Effect(500));
            };
            Action<int> newProjectileCallback = id =>
            {
                if (id == 0) newVfx.Add(500, new Effect(500));
            };
            var originalProjectiles = OldCapture(oldProjectiles, originalProjectileCallback);
            var optimizedProjectiles = NewCapture(newProjectiles, projectileCapture, newProjectileCallback);
            var originalVfx = OldCapture(oldVfx);
            var optimizedVfx = NewCapture(newVfx, vfxCapture);
            Check(originalProjectiles.SequenceEqual(optimizedProjectiles) &&
                originalVfx.SequenceEqual(optimizedVfx),
                "authoritative projectile and VFX capture retain old ordering and contents");
            Check(oldProjectiles.Keys.SequenceEqual(newProjectiles.Keys) &&
                oldVfx.Keys.SequenceEqual(newVfx.Keys),
                "both native dictionaries remove inactive entries exactly as before");
            Check(optimizedVfx.Contains(500),
                "VFX born during projectile capture appears in the subsequent VFX snapshot");
            Check(originalProjectiles.Count == 41 && originalVfx.Count == 16,
                "mixed expired instances do not enter the full native effect checkpoint");

            var originalVfxMutation = Make(12);
            var newVfxMutation = Make(12);
            Action<int> oldVfxCallback = id =>
            {
                if (id == 0)
                {
                    originalVfxMutation.Remove(4);
                    originalVfxMutation.Add(900, new Effect(900));
                }
            };
            Action<int> newVfxCallback = id =>
            {
                if (id == 0)
                {
                    newVfxMutation.Remove(4);
                    newVfxMutation.Add(900, new Effect(900));
                }
            };
            var previousVfxCapture = OldCapture(originalVfxMutation, oldVfxCallback);
            var currentVfxCapture = NewCapture(newVfxMutation, vfxCapture, newVfxCallback);
            Check(previousVfxCapture.SequenceEqual(currentVfxCapture),
                "native VFX spawn/despawn callback keeps the pre-capture snapshot intact");
            Check(!currentVfxCapture.Contains(900) &&
                newVfxMutation.ContainsKey(900) && newVfxMutation.Keys.SequenceEqual(originalVfxMutation.Keys),
                "VFX created during its own capture appears on the next frame, not midway");
            Check(OldCapture(originalVfxMutation).SequenceEqual(NewCapture(newVfxMutation, vfxCapture)),
                "next VFX capture sees prior callback updates");

            var captureFailure = Make(4);
            Check(Throws<InvalidOperationException>(() => NewCapture(captureFailure, projectileCapture,
                id => { if (id == 0) throw new InvalidOperationException("native pose read failed"); })),
                "native pose read error is propagated without leaking the capture lease");
            var recovered = projectileCapture.Capture(captureFailure);
            Check(recovered.Count == captureFailure.Count,
                "failed capture leaves the production buffer immediately reusable");
            projectileCapture.Release(recovered);
            Check(recovered.Count == 0,
                "failed snapshot does not retain native projectile object references");
            Check(Throws<InvalidOperationException>(() => NewCapture(newVfxMutation, vfxCapture,
                id => { if (id == 1) throw new InvalidOperationException("native VFX pose read failed"); })),
                "native VFX capture exception is propagated without leaving a snapshot leased");
            var recoveredVfx = vfxCapture.Capture(newVfxMutation);
            Check(recoveredVfx.Count == newVfxMutation.Count,
                "VFX capture also releases the reusable snapshot after an exceptional frame");
            vfxCapture.Release(recoveredVfx);

            var largeProjectileFrame = projectileCapture.Capture(Make(150));
            var largeVfxFrame = vfxCapture.Capture(Make(90));
            Check(largeProjectileFrame.Capacity >= 150 && largeVfxFrame.Capacity >= 90,
                "independent capture buffers can each grow for bursty battle scenes");
            projectileCapture.Release(largeProjectileFrame);
            vfxCapture.Release(largeVfxFrame);
            projectileCapture.Reset(); vfxCapture.Reset();
            Check(largeProjectileFrame.Capacity == 0 && largeVfxFrame.Capacity == 0,
                "ending the battle releases both large native capture backing arrays");

            const int liveProjectiles = 320, liveVfx = 96, captureFrames = 1000;
            var stableProjectiles = new Dictionary<int, Effect>();
            var stableVfx = new Dictionary<int, Effect>();
            for (int i = 0; i < liveProjectiles; i++)
                stableProjectiles.Add(i, new Effect(i));
            for (int i = 0; i < liveVfx; i++)
                stableVfx.Add(i, new Effect(i));
            Action legacyCapture = () =>
            {
                GC.KeepAlive(stableProjectiles.ToArray());
                GC.KeepAlive(stableVfx.ToArray());
            };
            Action optimizedCapture = () =>
            {
                var projectiles = projectileCapture.Capture(stableProjectiles);
                try { if (projectiles.Count != liveProjectiles) throw new Exception("Missing projectile"); }
                finally { projectileCapture.Release(projectiles); }
                var vfx = vfxCapture.Capture(stableVfx);
                try { if (vfx.Count != liveVfx) throw new Exception("Missing VFX"); }
                finally { vfxCapture.Release(vfx); }
            };
            long oldCaptureAllocated = Allocated(legacyCapture, captureFrames);
            long newCaptureAllocated = Allocated(optimizedCapture, captureFrames);
            Check(oldCaptureAllocated > 0 && newCaptureAllocated == 0,
                "repeated authoritative capture avoids two native dictionary snapshot arrays");
            Console.WriteLine("INFO: " + captureFrames + " synthetic host captures with " +
                liveProjectiles + " projectiles and " + liveVfx + " VFX: old ToArray " +
                oldCaptureAllocated + " B, two reused production buffers " +
                newCaptureAllocated + " B (managed snapshot allocations only)");

            const int effects = 256, frames = 1000;
            var stable = Make(effects);
            Action legacy = () => { var snapshot = stable.ToArray(); GC.KeepAlive(snapshot); };
            Action optimized = () =>
            {
                var snapshot = buffer.Capture(stable);
                try { if (snapshot.Count != effects) throw new Exception("snapshot lost effects"); }
                finally { buffer.Release(snapshot); }
            };
            long oldAllocated = Allocated(legacy, frames);
            long optimizedAllocated = Allocated(optimized, frames);
            Check(oldAllocated > 0 && optimizedAllocated == 0,
                "stable active scene avoids dictionary-sized arrays after buffer warmup");
            Console.WriteLine("INFO: " + frames + " synthetic updates with " + effects +
                " effects: old ToArray " + oldAllocated + " B, reusable production snapshot " +
                optimizedAllocated + " B (managed allocations only)");
            Console.WriteLine("PASS: " + checks + " linked battle effect snapshot checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
