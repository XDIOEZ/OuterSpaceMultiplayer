using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Ship
    {
        internal long Uid;
        internal string Resource;
        internal Action OnUid;
        internal Ship(long uid, string resource) { Uid = uid; Resource = resource; }
        internal long GetUid() { if (OnUid != null) OnUid(); return Uid; }
    }

    private static readonly Func<Ship, long> UidOf = s => s.GetUid();
    private static readonly Func<Ship, string> ResourceOf = s => s.Resource;
    private static int checks;
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static string Old(int seed, string motherType, IEnumerable<Ship> ships)
    {
        // Exact prior NativeRemoteFleetProxy content-key expression.
        return seed + ":" + motherType + ":" +
            String.Join("|", ships.Select(ship => ship.GetUid() + ":" + ship.Resource));
    }
    private static string ExceptionText(Action call)
    {
        try { call(); return "accepted"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
    private static void Equal(RemoteFleetVisualKeyBuilder builder,
        int seed, string motherType, IEnumerable<Ship> ships, string reason)
    {
        string oldKey = Old(seed, motherType, ships);
        string newKey = builder.Build(seed, motherType, ships, UidOf, ResourceOf, null);
        Check(String.Equals(oldKey, newKey, StringComparison.Ordinal),
            reason + " exact pre-optimization formation key");
        Check(ReferenceEquals(newKey,
                builder.Build(seed, motherType, ships, UidOf, ResourceOf, newKey)),
            reason + " unchanged visual identity returns existing string instance");
    }
    private static long Allocated(Action action, int samples)
    {
        for (int i = 0; i < 100; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    private static IEnumerable<Ship> AsIterator(List<Ship> source)
    {
        foreach (Ship ship in source) yield return ship;
    }
    private static void Run()
    {
        var builder = new RemoteFleetVisualKeyBuilder();
        var empty = new List<Ship>();
        Equal(builder, 0, null, empty, "empty fleet with null mothership resource");
        Equal(builder, -1, "", empty, "negative move seed and empty native formation");
        Equal(builder, int.MaxValue, "母舰/α", new[] {
            new Ship(long.MinValue, "frigate"), new Ship(long.MaxValue, "战机/旗舰")
        }, "full signed UID range and Unicode asset names");
        Equal(builder, int.MinValue, ":|", new List<Ship> {
            new Ship(0, null), new Ship(1L << 32, ""),
            new Ship(-1, ":|"), new Ship((1L << 32) | 1, "e\u0301"),
            new Ship(5, "\u00e9"), new Ship(6, "a\0b")
        }, "null IDs, embedded delimiters, normalization and zero char");
        Equal(builder, 12, "civilian", AsIterator(new List<Ship> {
            new Ship(1, "frigate"), new Ship(2, "frigate"), new Ship(1, "frigate")
        }), "general IEnumerable uses the same native iteration order");

        // Some operating environments change CurrentCulture when launching
        // a mod; both numeric append and string concatenation must match.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "tr-TR", "ar-EG" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Equal(builder, -123, "mothership:|", new[] {
                    new Ship(long.MinValue, "a"), new Ship(long.MaxValue, "兵器")
                }, "CurrentCulture " + culture);
            }
        }
        finally { CultureInfo.CurrentCulture = original; }

        var changing = new List<Ship> {
            new Ship(77, "frigate"), new Ship(88, "escort"), new Ship(99, "freighter")
        };
        string first = builder.Build(41, "patrol", changing, UidOf, ResourceOf, null);
        Check(ReferenceEquals(first, builder.Build(41, "patrol", changing,
            UidOf, ResourceOf, first)), "stable content returns exact original VisualKey reference");
        changing[0].Resource = "destroyer";
        string next = builder.Build(41, "patrol", changing, UidOf, ResourceOf, first);
        Check(next != first && next == Old(41, "patrol", changing),
            "native ship resource change invalidates visual identity");
        changing.Reverse();
        string reordered = builder.Build(41, "patrol", changing, UidOf, ResourceOf, next);
        Check(reordered != next && reordered == Old(41, "patrol", changing),
            "ship formation ordering remains part of the visual identity");
        changing[0].Uid = long.MaxValue;
        Check(builder.Build(41, "patrol", changing, UidOf, ResourceOf, reordered)
            == Old(41, "patrol", changing),
            "native ship UID change invalidates the exact formation key");
        changing[0].Resource = null;
        Equal(builder, 41, "patrol", changing, "null native ID preserved");

        // Error behavior mirrors LINQ Select and the old per-ship
        // concatenation, including malformed native ship references.
        var broken = new List<Ship> { new Ship(1, "safe"), null };
        Check(ExceptionText(() => Old(0, "m", broken)) ==
            ExceptionText(() => builder.Build(0, "m", broken, UidOf, ResourceOf, null)),
            "null native ship throws the original exception");
        Check(ExceptionText(() => Old(0, "m", null)) ==
            ExceptionText(() => builder.Build(0, "m", (IEnumerable<Ship>)null,
                UidOf, ResourceOf, null)),
            "null source retains original LINQ ArgumentNullException");
        var recovered = new[] { new Ship(2, "after-error") };
        Equal(builder, 0, "m", recovered,
            "builder is reusable following an exception during native enumeration");

        var mutating = new List<Ship> { new Ship(1, "one"), new Ship(2, "two") };
        mutating[0].OnUid = () => { mutating.Add(new Ship(3, "extra")); };
        string oldMutation = ExceptionText(() => Old(0, "m", mutating));
        mutating.RemoveAt(2);
        string newMutation = ExceptionText(() => builder.Build(0, "m", mutating,
            UidOf, ResourceOf, null));
        Check(oldMutation == newMutation && oldMutation.StartsWith("InvalidOperationException:"),
            "native list mutation retains the original enumerator failure");
        mutating[0].OnUid = null;
        Equal(builder, 0, "m", mutating, "failed enumeration releases the scratch builder");

        // A native visual-generation callback may reenter content evaluation
        // and/or end the scene. Both calls must retain independent buffers.
        var outer = new List<Ship> { new Ship(100, "outer"), new Ship(101, "tail") };
        outer[0].OnUid = () =>
        {
            string inner = builder.Build(22, "nested",
                new[] { new Ship(300, "inner") }, UidOf, ResourceOf, null);
            Check(inner == Old(22, "nested", new[] { new Ship(300, "inner") }),
                "reentrant native proxy receives an independent key");
            builder.Reset();
        };
        Check(builder.Build(20, "outer", outer, UidOf, ResourceOf, null) ==
            Old(20, "outer", new[] { new Ship(100, "outer"), new Ship(101, "tail") }),
            "native callback reentry and deferred reset preserve outer key");
        outer[0].OnUid = null;
        Equal(builder, 21, "new room", outer, "room reset does not leak prior visual contents");
        builder.Reset();

        var random = new Random(20261018);
        string[] resources = { null, "", "frigate", "cannon", "CV:|91",
            "旗艦", "e\u0301", "\u00e9", "a\0b", "ship/weapon" };
        string[] mothers = { null, "", "mothership", "mothership:fast", "主舰|旗" };
        for (int trial = 0; trial < 11000; trial++)
        {
            int count = random.Next(0, 65);
            var ships = new List<Ship>(count);
            for (int i = 0; i < count; i++)
            {
                long uid = random.Next(0, 5) == 0
                    ? ((long)random.Next(-2000000, 2000000) << 32) |
                        (uint)random.Next()
                    : random.Next(-20000, 20000);
                ships.Add(new Ship(uid, resources[random.Next(resources.Length)]));
            }
            int seed = random.Next(0, 10) == 0 ? int.MinValue :
                random.Next(-999, 999);
            string mother = mothers[random.Next(mothers.Length)];
            Equal(builder, seed, mother, ships, "random native visual frame " + trial);
            if (ships.Count != 0 && trial % 4 == 0)
            {
                string oldKey = Old(seed, mother, ships);
                ships[random.Next(ships.Count)].Resource =
                    resources[random.Next(resources.Length)];
                Check(builder.Build(seed, mother, ships, UidOf, ResourceOf, oldKey)
                    == Old(seed, mother, ships),
                    "random dirty visual change " + trial);
            }
        }

        // A dirty attribute (e.g., ammo) may not change the formation. The
        // previous LINQ/String.Join still rebuilt per-ship strings; the
        // production builder now returns the identical cached key for free.
        const int shipCount = 64, frames = 3000;
        var stable = new List<Ship>();
        for (int i = 0; i < shipCount; i++)
            stable.Add(new Ship(long.MaxValue - i, "frigate:" + i));
        string previousKey = Old(731, "mothership", stable);
        string currentKey = builder.Build(731, "mothership", stable,
            UidOf, ResourceOf, null);
        Action oldFrame = () =>
        {
            string key = Old(731, "mothership", stable);
            if (key != previousKey) previousKey = key;
        };
        Action newFrame = () =>
        {
            string key = builder.Build(731, "mothership", stable,
                UidOf, ResourceOf, currentKey);
            if (key != currentKey) currentKey = key;
        };
        long oldBytes = Allocated(oldFrame, frames);
        long newBytes = Allocated(newFrame, frames);
        Check(oldBytes > newBytes && newBytes == 0,
            "unchanged dirty fleet formations allocate no temporary string or selector");
        Console.WriteLine("INFO: " + frames + " dirty-but-unchanged synthetic frames × " +
            shipCount + " ships: original LINQ/string join " + oldBytes +
            " B vs linked production builder " + newBytes +
            " B (managed allocations after warmup)");

        // Actual composition change still requires one output key, but no
        // intermediate UID/resource strings per ship.
        int oldSeed = 0, newSeed = 0;
        long oldChanging = Allocated(() =>
        {
            string key = Old(++oldSeed, "mothership", stable);
            GC.KeepAlive(key);
        }, 1200);
        long newChanging = Allocated(() =>
        {
            string key = builder.Build(++newSeed, "mothership", stable,
                UidOf, ResourceOf, null);
            GC.KeepAlive(key);
        }, 1200);
        Check(oldChanging > newChanging,
            "true formation changes avoid per-ship string allocations too");
        Console.WriteLine("INFO: 1200 distinct visual-key generations: old " +
            oldChanging + " B vs production " + newChanging + " B");
    }

    private static int Main()
    {
        try
        {
            Run();
            Console.WriteLine("PASS: " + checks +
                " linked remote fleet visual-key compatibility checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
