using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Pose
    {
        internal readonly long Id;
        internal readonly string Resource;
        internal Pose(long id, string resource) { Id = id; Resource = resource; }
    }
    private sealed class EffectEvent
    {
        internal int Kind;
        internal Pose Pose;
        internal EffectEvent(int kind, Pose pose) { Kind = kind; Pose = pose; }
    }
    private sealed class Preflight
    {
        internal readonly List<string> Visited = new List<string>();
        internal string FailAt;
        internal int KindReads, PoseReads;
        internal List<EffectEvent> MutateEvents;
        internal bool Mutated;
        internal void Check(string type, Pose pose)
        {
            // Closely models both native resource lookups: a missing/invalid
            // resource rejects before any visual instance is applied.
            string resource = pose.Resource;
            string entry = type + ":" + resource;
            Visited.Add(entry);
            if (FailAt == entry)
                throw new InvalidOperationException(type == "weapon"
                    ? "本机缺少已登记武器的弹丸表现资源：" + resource
                    : "本机缺少已登记战斗特效：" + resource);
            if (MutateEvents != null && !Mutated)
            {
                Mutated = true;
                MutateEvents.Add(new EffectEvent(1, new Pose(888, "late")));
            }
        }
    }
    private static int checks;
    private static void Assert(bool valid, string reason)
    {
        if (!valid) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static readonly Func<EffectEvent, int> KindOf = e => e.Kind;
    private static readonly Func<EffectEvent, Pose> PoseOf = e => e.Pose;
    private static readonly Action<Preflight, Pose> CheckWeapon =
        (context, pose) => context.Check("weapon", pose);
    private static readonly Action<Preflight, Pose> CheckVfx =
        (context, pose) => context.Check("vfx", pose);
    private static readonly Action<Preflight, Pose> NoAllocWeapon =
        (context, pose) => context.KindReads++;
    private static readonly Action<Preflight, Pose> NoAllocVfx =
        (context, pose) => context.PoseReads++;
    private static readonly Func<EffectEvent, int> CountedKind = e => e.Kind;

    // Exactly the pre-optimization receiver loops: the two collections
    // must be traversed in this order, without deduplication.
    private static void Legacy<TContext>(TContext ctx, List<Pose> projectiles,
        List<Pose> vfx, List<EffectEvent> events,
        Action<TContext, Pose> weapon, Action<TContext, Pose> effect)
    {
        foreach (Pose pose in projectiles.Concat(events.Where(e => e.Kind != 3).Select(e => e.Pose)))
            weapon(ctx, pose);
        foreach (Pose pose in vfx.Concat(events.Where(e => e.Kind == 3).Select(e => e.Pose)))
            effect(ctx, pose);
    }
    private static void Current<TContext>(TContext ctx, List<Pose> projectiles,
        List<Pose> vfx, List<EffectEvent> events,
        Action<TContext, Pose> weapon, Action<TContext, Pose> effect)
    {
        BattleEffectResourcePreflight.Check(ctx, projectiles, vfx, events,
            KindOf, PoseOf, weapon, effect);
    }
    private static string Outcome(Action work)
    {
        try { work(); return "accepted"; }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static void Compare(List<Pose> projectile, List<Pose> vfx,
        List<EffectEvent> events, string reason, string fail = null)
    {
        var old = new Preflight { FailAt = fail };
        var current = new Preflight { FailAt = fail };
        string before = Outcome(() => Legacy(old, projectile, vfx, events, CheckWeapon, CheckVfx));
        string after = Outcome(() => Current(current, projectile, vfx, events, CheckWeapon, CheckVfx));
        Assert(before == after, reason + " exact exception and error priority: " + before + " versus " + after);
        Assert(old.Visited.SequenceEqual(current.Visited),
            reason + " exact preflight call order and duplicate lookup count");
        for (int i = 0; i < old.Visited.Count; i++)
            Assert(old.Visited[i] == current.Visited[i], reason + " native preflight event " + i);
    }
    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 90; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private static void LegacyValidatePoseIds(List<Pose> projectile,
        List<Pose> vfx, HashSet<long> ids)
    {
        foreach (Pose pose in projectile.Concat(vfx))
            if (!ids.Add(pose.Id))
                throw new InvalidOperationException("玩家舰战表现实例编号重复。");
    }
    private static void DirectValidatePoseIds(List<Pose> projectile,
        List<Pose> vfx, HashSet<long> ids)
    {
        foreach (Pose pose in projectile)
            if (!ids.Add(pose.Id))
                throw new InvalidOperationException("玩家舰战表现实例编号重复。");
        foreach (Pose pose in vfx)
            if (!ids.Add(pose.Id))
                throw new InvalidOperationException("玩家舰战表现实例编号重复。");
    }
    private static void ComparePoseIds(List<Pose> projectile, List<Pose> vfx, string reason)
    {
        var oldIds = new HashSet<long>();
        var newIds = new HashSet<long>();
        string old = Outcome(() => LegacyValidatePoseIds(projectile, vfx, oldIds));
        string current = Outcome(() => DirectValidatePoseIds(projectile, vfx, newIds));
        Assert(old == current && oldIds.SetEquals(newIds),
            reason + " validation duplicate-UID error priority and observed IDs");
    }
    private static int Main()
    {
        try
        {
            var empty = new List<Pose>();
            var emptyEvents = new List<EffectEvent>();
            Compare(empty, empty, emptyEvents, "empty checkpoint");
            ComparePoseIds(empty, empty, "empty duplicate-ID validation");
            Compare(new List<Pose> { new Pose(1, "w") }, empty, emptyEvents,
                "one authoritative projectile");
            Compare(empty, new List<Pose> { new Pose(2, "v") }, emptyEvents,
                "one authoritative VFX");

            var p = new List<Pose> { new Pose(1, "gun"), new Pose(2, "gun"),
                new Pose(3, "主炮"), new Pose(4, "e\u0301") };
            var v = new List<Pose> { new Pose(5, "flare"), new Pose(6, "\u00e9") };
            var history = new List<EffectEvent> {
                new EffectEvent(3, new Pose(7, "vfx-event")),
                new EffectEvent(1, new Pose(8, "weapon-event")),
                new EffectEvent(2, new Pose(9, "weapon-end")),
                new EffectEvent(3, new Pose(10, "vfx-event-two")),
                new EffectEvent(2, new Pose(11, "weapon-end"))
            };
            Compare(p, v, history, "all four resource-preflight stages and repeated kinds");
            ComparePoseIds(p, v, "ordinary existing projectile/VFX snapshot");
            ComparePoseIds(new List<Pose> { new Pose(99, "w"), new Pose(99, "w") },
                v, "duplicate projectile UID rejects before VFX");
            ComparePoseIds(p, new List<Pose> { new Pose(1, "vfx") },
                "duplicate UID across projectile and VFX preserves rejection");
            ComparePoseIds(p, new List<Pose> { new Pose(44, "vfx"), new Pose(44, "vfx") },
                "duplicate VFX UID rejection remains unchanged");

            // Every failure point is checked, including later event stages;
            // a failure must prevent all subsequent native resource checks.
            var reference = new Preflight();
            Legacy(reference, p, v, history, CheckWeapon, CheckVfx);
            foreach (string entry in reference.Visited)
                Compare(p, v, history, "resource failure at " + entry, entry);
            Compare(p, v, history, "no-error after rejected frames");
            Compare(new List<Pose> { null }, v, history, "invalid null projectile");
            Compare(p, new List<Pose> { null }, history, "invalid null VFX");
            Compare(p, v, new List<EffectEvent> { null }, "invalid null event");
            Compare(p, v, new List<EffectEvent> { new EffectEvent(3, null) },
                "invalid VFX event without pose");
            Compare(p, v, new List<EffectEvent> { new EffectEvent(1, null) },
                "invalid projectile event without pose");
            Compare(p, v, new List<EffectEvent> { new EffectEvent(2, new Pose(40, "end")),
                new EffectEvent(3, new Pose(41, "birth")) }, "ended projectile and born VFX");
            Compare(p, v, new List<EffectEvent> { new EffectEvent(0, new Pose(40, "illegal")),
                new EffectEvent(9, new Pose(41, "illegal2")) },
                "preflight behavior for invalid kinds still matches old loop");

            // Mutating the event List during the predicate must still throw
            // InvalidOperationException on both old LINQ and typed foreach.
            var oldMutationEvents = new List<EffectEvent> {
                new EffectEvent(1, new Pose(101, "first")),
                new EffectEvent(2, new Pose(102, "second"))
            };
            var newMutationEvents = new List<EffectEvent>(oldMutationEvents);
            var oldMutator = new Preflight { MutateEvents = oldMutationEvents };
            var newMutator = new Preflight { MutateEvents = newMutationEvents };
            string oldFailure = Outcome(() => Legacy(oldMutator, empty, empty,
                oldMutationEvents, CheckWeapon, CheckVfx));
            string newFailure = Outcome(() => Current(newMutator, empty, empty,
                newMutationEvents, CheckWeapon, CheckVfx));
            Assert(oldFailure == newFailure &&
                oldFailure.StartsWith("InvalidOperationException:", StringComparison.Ordinal),
                "event List mutation during preflight preserves enumerator failure");
            Assert(oldMutator.Visited.SequenceEqual(newMutator.Visited),
                "mutation exception occurs after the same native resource lookups");

            string[] resources = { "w", "W", "weapon", "effect", "主炮",
                "\u00e9", "e\u0301", "resource:slot", "" };
            var random = new Random(20261018);
            for (int run = 0; run < 6500; run++)
            {
                var projectiles = new List<Pose>();
                var vfx = new List<Pose>();
                var events = new List<EffectEvent>();
                for (int i = 0, n = random.Next(0, 42); i < n; i++)
                    projectiles.Add(new Pose(random.Next(-5, 70),
                        resources[random.Next(resources.Length)]));
                for (int i = 0, n = random.Next(0, 20); i < n; i++)
                    vfx.Add(new Pose(random.Next(-5, 70),
                        resources[random.Next(resources.Length)]));
                for (int i = 0, n = random.Next(0, 65); i < n; i++)
                    events.Add(new EffectEvent(random.Next(1, 4), new Pose(random.Next(-5, 70),
                        resources[random.Next(resources.Length)])));
                Compare(projectiles, vfx, events, "random authoritative resource preflight " + run);
                ComparePoseIds(projectiles, vfx, "random existing effect UID validation " + run);
                if (run % 7 == 0 && projectiles.Count != 0)
                    Compare(projectiles, vfx, events,
                        "random deliberate native missing resource " + run,
                        "weapon:" + projectiles[random.Next(projectiles.Count)].Resource);
            }

            // Measure just the two resource-preflight traversals, excluding
            // JSON parsing, validation, resource loading or Unity instancing.
            const int frames = 3000;
            var stableProjectiles = new List<Pose>();
            var stableVfx = new List<Pose>();
            var stableEvents = new List<EffectEvent>();
            for (int i = 0; i < 48; i++) stableProjectiles.Add(new Pose(i, "laser"));
            for (int i = 0; i < 16; i++) stableVfx.Add(new Pose(i + 1000, "flare"));
            for (int i = 0; i < 80; i++)
                stableEvents.Add(new EffectEvent(i % 3 + 1, new Pose(i + 2000, "event")));
            var perf = new Preflight();
            Action oldLoop = () => Legacy(perf, stableProjectiles, stableVfx,
                stableEvents, NoAllocWeapon, NoAllocVfx);
            Action newLoop = () => Current(perf, stableProjectiles, stableVfx,
                stableEvents, NoAllocWeapon, NoAllocVfx);
            long oldBytes = Allocated(oldLoop, frames);
            long newBytes = Allocated(newLoop, frames);
            Assert(oldBytes > newBytes && newBytes == 0,
                "linked production preflight removes iterator allocations in stable frames");
            Console.WriteLine("INFO: " + frames + " resource preflights × 48 projectiles, " +
                "16 VFX and 80 historical events: old Concat/Where/Select " +
                oldBytes + " B, linked production direct enumeration " + newBytes +
                " B (managed iterator allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " linked battle effects resource preflight checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
