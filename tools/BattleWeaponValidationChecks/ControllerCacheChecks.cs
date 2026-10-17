using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

// This checks the ACTUAL BattleWeaponSlotKey compiled from production
// NativeBattleWeaponValidation.cs, while modeling the Unity controller
// identity and fallback contract from ResolveEffectController.
internal static class ControllerCacheChecks
{
    private sealed class Controller
    {
        internal readonly long Ship;
        internal readonly string Slot;
        internal string Resource;
        internal bool Destroyed;
        internal Controller(long ship, string slot, string resource)
        { Ship = ship; Slot = slot; Resource = resource; }
    }

    private sealed class World
    {
        internal readonly Dictionary<long, List<Controller>> Ships =
            new Dictionary<long, List<Controller>>();
        internal int NativeSearches;
        internal List<Controller> Weapons(long ship)
        {
            NativeSearches++;
            return Ships[ship];
        }
    }

    // Independent exact old reference: before the optimization the cache
    // used an ordinal string "shipUid:slotId" on every successful lookup.
    private sealed class OldControllerCache
    {
        private readonly World world;
        private readonly Dictionary<string, Controller> cache =
            new Dictionary<string, Controller>(StringComparer.Ordinal);
        internal OldControllerCache(World world) { this.world = world; }
        internal Controller Resolve(long ship, string slot, string resource)
        {
            if (!world.Ships.ContainsKey(ship)) return null;
            string key = ship + ":" + slot;
            Controller weapon;
            if (!cache.TryGetValue(key, out weapon) || weapon == null || weapon.Destroyed)
            {
                weapon = world.Weapons(ship).SingleOrDefault(w => w != null && w.Slot == slot);
                if (weapon == null)
                    throw new InvalidOperationException("本机缺少本场舰船武器插槽：" + key);
                cache[key] = weapon;
            }
            if (weapon.Resource == null || weapon.Resource != resource)
                throw new InvalidOperationException("玩家舰战武器插槽资源不一致：" + key);
            return weapon;
        }
        internal void Clear() { cache.Clear(); }
    }

    // Exactly the production cache key, early destroyed ship return and
    // allocation-on-error diagnostic behavior in ResolveEffectController.
    private sealed class NewControllerCache
    {
        private readonly World world;
        private readonly Dictionary<BattleWeaponSlotKey, Controller> cache =
            new Dictionary<BattleWeaponSlotKey, Controller>();
        internal NewControllerCache(World world) { this.world = world; }
        private static Controller FindOnMiss(World world, long ship, string slot)
        {
            // The actual production path now creates this closure only
            // when a native controller lookup really misses the cache.
            return world.Weapons(ship).SingleOrDefault(w => w != null && w.Slot == slot);
        }
        internal Controller Resolve(long ship, string slot, string resource)
        {
            if (!world.Ships.ContainsKey(ship)) return null;
            var key = new BattleWeaponSlotKey(ship, slot);
            Controller weapon;
            if (!cache.TryGetValue(key, out weapon) || weapon == null || weapon.Destroyed)
            {
                weapon = FindOnMiss(world, ship, slot);
                if (weapon == null)
                    throw new InvalidOperationException("本机缺少本场舰船武器插槽：" + ship + ":" + slot);
                cache[key] = weapon;
            }
            if (weapon.Resource == null || weapon.Resource != resource)
                throw new InvalidOperationException("玩家舰战武器插槽资源不一致：" + ship + ":" + slot);
            return weapon;
        }
        internal void Clear() { cache.Clear(); }
    }

    private static int checks;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + message);
        checks++;
    }
    private static string Outcome(Func<Controller> resolve, out Controller resolved)
    {
        resolved = null;
        try { resolved = resolve(); return "accepted"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
    private static void Same(OldControllerCache original, NewControllerCache updated,
        long ship, string slot, string resource, string label)
    {
        Controller oldWeapon, newWeapon;
        string old = Outcome(() => original.Resolve(ship, slot, resource), out oldWeapon);
        string current = Outcome(() => updated.Resolve(ship, slot, resource), out newWeapon);
        Check(old == current && ReferenceEquals(oldWeapon, newWeapon),
            label + " old=" + old + " new=" + current);
    }
    private static long Allocated(Action action, int frames)
    {
        for (int i = 0; i < 100; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    internal static int Run()
    {
        var world = new World();
        long[] carriers = { 1, -1, 0, long.MinValue, long.MaxValue,
            1L << 32, (1L << 32) | 1, -(1L << 32) };
        string[] slots = { "main", "MAIN", "left:right", "右舷", "e\u0301",
            "\u00e9", "port\\weapon", "cannon α" };
        var all = new List<Controller>();
        foreach (long ship in carriers)
        {
            var weapons = new List<Controller>();
            foreach (string slot in slots)
            {
                Controller controller = new Controller(ship, slot, "resource-" + slot);
                weapons.Add(controller);
                all.Add(controller);
            }
            world.Ships.Add(ship, weapons);
        }
        var oldCache = new OldControllerCache(world);
        var newCache = new NewControllerCache(world);

        foreach (Controller weapon in all)
            Same(oldCache, newCache, weapon.Ship, weapon.Slot, weapon.Resource,
                "first authoritative controller resolution");
        int initialSearches = world.NativeSearches;
        Check(initialSearches == all.Count * 2,
            "each cache resolves its own native object once per distinct ship/slot");
        foreach (Controller weapon in all)
            Same(oldCache, newCache, weapon.Ship, weapon.Slot, weapon.Resource,
                "already cached controller keeps same exact native object");
        Check(initialSearches == world.NativeSearches,
            "cache hits skip the native component traversal in both implementations");

        Same(oldCache, newCache, -2, "main", "resource-main",
            "destroyed/departed ship returns without searching or allocating a cache entry");
        Check(initialSearches == world.NativeSearches,
            "unregistered ship does not search the native weapon manager");
        Same(oldCache, newCache, 1, "missing-slot", "resource",
            "missing registered slot reports unchanged identity-specific error");
        Same(oldCache, newCache, 1, "main", "WRONG-RESOURCE",
            "resource mismatch keeps original Chinese error and exact ship/slot");
        Same(oldCache, newCache, 1, "main", "resource-main",
            "previous failure does not invalidate a legitimate cached controller");

        // Unity destroyed-object equality compares a dead component to null.
        // Both paths must search a replacement rather than reuse the stale
        // cache value, without persisting a previous object's reference.
        var originalShipWeapons = world.Ships[1];
        var previous = originalShipWeapons[0];
        previous.Destroyed = true;
        var replacement = new Controller(1, "main", "resource-main");
        originalShipWeapons[0] = replacement;
        Same(oldCache, newCache, 1, "main", "resource-main",
            "native controller destroyed and reconstructed under same slot key");
        Controller oldController, newController;
        Check(Outcome(() => oldCache.Resolve(1, "main", "resource-main"), out oldController)
            == Outcome(() => newCache.Resolve(1, "main", "resource-main"), out newController)
            && ReferenceEquals(oldController, replacement)
            && ReferenceEquals(newController, replacement),
            "after native replacement neither cache retains stale controller reference");

        // Clearing the cache must not delete or mutate original controllers.
        oldCache.Clear(); newCache.Clear();
        Same(oldCache, newCache, 1, "main", "resource-main",
            "battle end/scene transition forces native controller resolution again");
        world.Ships.Remove(1);
        Same(oldCache, newCache, 1, "main", "resource-main",
            "dead ship does not leak a previously cached live controller");
        world.Ships.Add(1, originalShipWeapons);

        // Two unrelated native controllers can have exactly the same slot
        // text; case, Unicode normalization, signs and 64-bit hash collisions
        // must all match the old ordinal string-key behavior.
        var random = new Random(20261018);
        for (int i = 0; i < 14000; i++)
        {
            long ship = carriers[random.Next(carriers.Length)];
            string slot = slots[random.Next(slots.Length)];
            string resource = "resource-" + slot;
            if (random.Next(40) == 0) resource = "wrong";
            Same(oldCache, newCache, ship, slot, resource, "random validated wire key " + i);
        }

        // Isolate key-allocation cost during steady state: native wrappers
        // and returned data have already been cached by both implementations.
        oldCache.Clear(); newCache.Clear();
        foreach (Controller controller in all)
            if (world.Ships.ContainsKey(controller.Ship) && !controller.Destroyed)
                Same(oldCache, newCache, controller.Ship, controller.Slot,
                    controller.Resource, "warm cache for synthetic received frames");
        const int frames = 1200;
        Action oldFrame = () =>
        {
            foreach (Controller c in all)
                if (!c.Destroyed) oldCache.Resolve(c.Ship, c.Slot, c.Resource);
        };
        Action newFrame = () =>
        {
            foreach (Controller c in all)
                if (!c.Destroyed) newCache.Resolve(c.Ship, c.Slot, c.Resource);
        };
        long before = Allocated(oldFrame, frames);
        long after = Allocated(newFrame, frames);
        Check(before > 0 && after == 0,
            "production composite cache key has zero steady-frame managed allocations");
        Console.WriteLine("INFO: " + frames + " synthetic receive frames × " +
            (all.Count - 1) + " cached weapon controllers: old key strings " +
            before + " B, production BattleWeaponSlotKey " + after +
            " B (managed key allocations after warmup)");
        Console.WriteLine("INFO: " + checks +
            " native controller cache compatibility and allocation checks");
        return checks;
    }
}
