using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private class Bean
    {
        internal readonly string Marker;
        internal Bean(string marker) { Marker = marker; }
    }
    private sealed class Fleet : Bean
    {
        internal readonly long Uid;
        internal readonly bool Fault;
        internal Fleet(string marker, long uid, bool fault = false) : base(marker)
        { Uid = uid; Fault = fault; }
        internal long GetUid()
        {
            if (Fault) throw new InvalidOperationException("Native fleet UID unavailable: " + Marker);
            return Uid;
        }
    }
    private class Galaxy
    {
        internal readonly string Name;
        internal readonly List<Bean> Fleets = new List<Bean>();
        internal Galaxy(string name) { Name = name; }
    }
    private sealed class EqualGalaxy : Galaxy
    {
        internal readonly int EqualityId;
        internal EqualGalaxy(string name, int equalityId) : base(name)
        { EqualityId = equalityId; }
        public override bool Equals(object other)
        { return other is EqualGalaxy && EqualityId == ((EqualGalaxy)other).EqualityId; }
        public override int GetHashCode() { return EqualityId; }
    }
    private sealed class Player
    {
        internal readonly string Name;
        internal List<Galaxy> Known;
        internal Player(string name, List<Galaxy> known) { Name = name; Known = known; }
    }
    private static int checks;
    private static readonly Func<Player, IEnumerable<Galaxy>> KnownGalaxies = p => p.Known;
    private static readonly Action<Galaxy, long> RemoveFleet = (galaxy, uid) =>
        galaxy.Fleets.RemoveAll(bean => bean is Fleet && ((Fleet)bean).GetUid() == uid);
    private static readonly Action<Galaxy, long> IgnoreVisit = (galaxy, uid) => { };

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + name);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static string Outcome(Action action)
    {
        try { action(); return "accepted"; }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static List<Player> Clone(List<Player> players)
    {
        var galaxies = new Dictionary<Galaxy, Galaxy>();
        var result = new List<Player>();
        foreach (Player player in players)
        {
            if (player == null) { result.Add(null); continue; }
            if (player.Known == null) { result.Add(new Player(player.Name, null)); continue; }
            var known = new List<Galaxy>();
            foreach (Galaxy galaxy in player.Known)
            {
                if (galaxy == null) { known.Add(null); continue; }
                Galaxy duplicate;
                if (!galaxies.TryGetValue(galaxy, out duplicate))
                {
                    duplicate = new Galaxy(galaxy.Name);
                    foreach (Bean bean in galaxy.Fleets)
                    {
                        Fleet fleet = bean as Fleet;
                        duplicate.Fleets.Add(fleet == null
                            ? new Bean(bean.Marker)
                            : (Bean)new Fleet(fleet.Marker, fleet.Uid, fleet.Fault));
                    }
                    galaxies.Add(galaxy, duplicate);
                }
                known.Add(duplicate);
            }
            result.Add(new Player(player.Name, known));
        }
        return result;
    }
    private static void Legacy(List<Player> players, IEnumerable<long> removed, List<string> visits)
    {
        // Previous production expression inside foreach (long uid in removed),
        // including LINQ's default equality and its per-UID rescan.
        foreach (long uid in removed)
        {
            visits.Add("proxy:" + uid);
            foreach (Galaxy galaxy in players.SelectMany(p => p.Known).Distinct())
            {
                visits.Add(uid + ":" + (galaxy == null ? "NULL" : galaxy.Name));
                RemoveFleet(galaxy, uid);
            }
        }
    }
    private static void Optimized(List<Player> players, IEnumerable<long> removed, List<string> visits)
    {
        FleetGalaxyRemovalWalker<Player, Galaxy> walker = null;
        foreach (long uid in removed)
        {
            visits.Add("proxy:" + uid);
            if (walker == null) walker = new FleetGalaxyRemovalWalker<Player, Galaxy>();
            walker.VisitDistinct(players, KnownGalaxies, uid, (galaxy, id) =>
            {
                visits.Add(id + ":" + (galaxy == null ? "NULL" : galaxy.Name));
                RemoveFleet(galaxy, id);
            });
        }
    }
    private static void Compare(List<Player> source, long[] removed, string context)
    {
        List<Player> before = Clone(source), after = Clone(source);
        var oldVisits = new List<string>();
        var newVisits = new List<string>();
        string oldOutcome = Outcome(() => Legacy(before, removed, oldVisits));
        string newOutcome = Outcome(() => Optimized(after, removed, newVisits));
        Check(oldOutcome == newOutcome,
            context + " error/acceptance old=" + oldOutcome + " new=" + newOutcome);
        Check(oldVisits.SequenceEqual(newVisits), context + " per-UID native galaxy visit order");
        Check(before.Count == after.Count, context + " player count");
        for (int i = 0; i < before.Count; i++)
        {
            Player a = before[i], b = after[i];
            if (a == null || b == null)
            { Check(a == b, context + " null player"); continue; }
            if (a.Known == null || b.Known == null)
            { Check(a.Known == b.Known, context + " null native galaxy collection"); continue; }
            Check(a.Known.Count == b.Known.Count, context + " native galaxy count");
            for (int j = 0; j < a.Known.Count; j++)
            {
                Galaxy oldGalaxy = a.Known[j], newGalaxy = b.Known[j];
                if (oldGalaxy == null || newGalaxy == null)
                { Check(oldGalaxy == newGalaxy, context + " null galaxy"); continue; }
                Check(oldGalaxy.Name == newGalaxy.Name, context + " native galaxy identity");
                Check(oldGalaxy.Fleets.Select(x => x.Marker).SequenceEqual(
                    newGalaxy.Fleets.Select(x => x.Marker)), context + " stable original fleet order");
            }
        }
    }
    private static List<Player> Make(int playerCount, int galaxyCount, int fleetCount, int seed)
    {
        var random = new Random(seed);
        var allGalaxies = new List<Galaxy>();
        for (int i = 0; i < galaxyCount; i++)
        {
            var galaxy = new Galaxy("galaxy-" + i);
            for (int j = 0; j < fleetCount; j++)
            {
                long uid = random.Next(-5, 65);
                galaxy.Fleets.Add(j % 7 == 0
                    ? (Bean)new Bean("environment-" + i + "-" + j)
                    : new Fleet("fleet-" + i + "-" + j, uid));
            }
            allGalaxies.Add(galaxy);
        }
        var players = new List<Player>();
        for (int i = 0; i < playerCount; i++)
        {
            var known = new List<Galaxy>();
            int entries = random.Next(0, galaxyCount * 2 + 1);
            for (int j = 0; j < entries; j++)
                if (galaxyCount > 0) known.Add(allGalaxies[random.Next(galaxyCount)]);
            players.Add(new Player("player-" + i, known));
        }
        return players;
    }
    private static long Measure(Action action, int frames)
    {
        for (int i = 0; i < 80; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static int Main()
    {
        try
        {
            var empty = new List<Player>();
            Compare(empty, new long[0], "empty frame has no removal work");
            Compare(empty, new long[] { 4 }, "removed UID with no players");
            Compare(new List<Player> { new Player("host", new List<Galaxy>()) },
                new long[] { 1, 2 }, "no known galaxies");
            Compare(Make(2, 6, 6, 1), new long[] { 2 },
                "single removal leaves unrelated native fleet identities");
            Compare(Make(6, 18, 17, 2), new long[] { long.MinValue, -1, 0,
                1, 1L << 32, (1L << 32) | 1, long.MaxValue },
                "signed full 64-bit UID range");
            Compare(Make(20, 40, 9, 3),
                new long[] { 5, 6, 7, 8, 9, 10, 11, 12 },
                "shared galaxies across many distinct players");

            var nullGalaxy = new Galaxy("alpha");
            var malformed = new List<Player> {
                new Player("host", new List<Galaxy> { nullGalaxy, null }) };
            Compare(malformed, new long[] { 1, 2 },
                "null native galaxy still raises old exception after prior visited lists");
            Compare(new List<Player> { new Player("host", null) },
                new long[] { 1 }, "missing native knownGalaxy collection");
            Compare(new List<Player> { null },
                new long[] { 1 }, "missing player record");
            var throwingGalaxy = new Galaxy("invalid-native");
            throwingGalaxy.Fleets.Add(new Bean("terrain"));
            throwingGalaxy.Fleets.Add(new Fleet("throws-on-uid", 8, true));
            Compare(new List<Player> {
                new Player("host", new List<Galaxy> { throwingGalaxy }) },
                new long[] { 3, 8 }, "native getUID fault retains original partial mutation/error");

            var equatable1 = new EqualGalaxy("same-one", 12);
            var equatable2 = new EqualGalaxy("same-two", 12);
            var equalityPlayers = new List<Player> {
                new Player("host", new List<Galaxy> { equatable1, equatable2, equatable1 })
            };
            var equalityOld = new List<string>();
            var equalityNew = new List<string>();
            foreach (Galaxy galaxy in equalityPlayers.SelectMany(p => p.Known).Distinct())
                equalityOld.Add(galaxy.Name);
            var equalsWalker = new FleetGalaxyRemovalWalker<Player, Galaxy>();
            equalsWalker.VisitDistinct(equalityPlayers, KnownGalaxies, 77,
                (galaxy, uid) => equalityNew.Add(galaxy.Name));
            Check(equalityOld.SequenceEqual(equalityNew) &&
                equalityNew.SequenceEqual(new[] { "same-one" }),
                "Distinct and HashSet use identical default IEqualityComparer, not reference-only");

            // A native disposal callback may synchronously reenter a galaxy
            // transaction. The inner invocation needs an independent seen set.
            var nestedPlayers = new List<Player> {
                new Player("host", new List<Galaxy> {
                    new Galaxy("A"), new Galaxy("B"), new Galaxy("A") }) };
            nestedPlayers[0].Known.Add(nestedPlayers[0].Known[0]);
            var nestedWalker = new FleetGalaxyRemovalWalker<Player, Galaxy>();
            var nestedOrder = new List<string>();
            nestedWalker.VisitDistinct(nestedPlayers, KnownGalaxies, 1, (galaxy, uid) =>
            {
                nestedOrder.Add("outer-" + galaxy.Name);
                if (ReferenceEquals(galaxy, nestedPlayers[0].Known[0]))
                    nestedWalker.VisitDistinct(nestedPlayers, KnownGalaxies, 2,
                        (inner, id) => nestedOrder.Add("inner-" + inner.Name));
            });
            Check(nestedOrder.SequenceEqual(new[] {
                "outer-A", "inner-A", "inner-B", "inner-A", "outer-B", "outer-A" }),
                "reentrant visitor gets independent duplicate filter and outer order survives: " +
                String.Join(",", nestedOrder));

            // Verify the old List<T>.Enumerator mutation guard and cleanup
            // after a callback error, not a silently tolerant index loop.
            var mutable = new List<Player> {
                new Player("host", new List<Galaxy> { new Galaxy("first"), new Galaxy("second") })
            };
            var mutatingWalker = new FleetGalaxyRemovalWalker<Player, Galaxy>();
            Check(Throws<InvalidOperationException>(() =>
                mutatingWalker.VisitDistinct(mutable, KnownGalaxies, 1, (galaxy, uid) =>
                {
                    if (galaxy.Name == "first")
                        mutable[0].Known.Add(new Galaxy("third"));
                })), "native list mutation during enumerator still raises original exception");
            int returned = 0;
            mutatingWalker.VisitDistinct(mutable, KnownGalaxies, 2,
                (galaxy, uid) => returned++);
            Check(returned == 3, "exception fully clears filter for retry");
            Check(Throws<InvalidOperationException>(() =>
                mutatingWalker.VisitDistinct(mutable, KnownGalaxies, 3,
                    (galaxy, uid) => { throw new InvalidOperationException("native disposal failed"); })),
                "native removal exceptions propagate unmodified");
            returned = 0;
            mutatingWalker.VisitDistinct(mutable, KnownGalaxies, 4,
                (galaxy, uid) => returned++);
            Check(returned == 3, "retry after a callback exception sees every galaxy again");

            // Re-enumerate current player/galaxy membership AFTER each
            // RemoveRemoteFleetProxy callback, rather than caching a global
            // distinct-galaxy list across removed UIDs.
            var evolving = new List<Player> {
                new Player("host", new List<Galaxy> { new Galaxy("old") }) };
            var evolvingWalker = new FleetGalaxyRemovalWalker<Player, Galaxy>();
            var changing = new List<string>();
            foreach (long uid in new long[] { 1, 2 })
            {
                if (uid == 2) evolving[0].Known.Add(new Galaxy("new"));
                evolvingWalker.VisitDistinct(evolving, KnownGalaxies, uid,
                    (galaxy, id) => changing.Add(id + "/" + galaxy.Name));
            }
            Check(changing.SequenceEqual(new[] { "1/old", "2/old", "2/new" }),
                "newly discovered galaxies join only later removed-UID traversals");

            var random = new Random(20261018);
            for (int iteration = 0; iteration < 1800; iteration++)
            {
                int players = random.Next(0, 9), galaxies = random.Next(0, 30);
                int beans = random.Next(0, 24), removals = random.Next(0, 24);
                var ids = new long[removals];
                for (int j = 0; j < removals; j++)
                    ids[j] = random.Next(0, 8) == 0
                        ? long.MaxValue - random.Next(0, 800)
                        : random.Next(-10, 70);
                Compare(Make(players, galaxies, beans, iteration * 11 + 7), ids,
                    "random galaxy removal transaction " + iteration);
            }

            // Allocation comparison is for the Distinct-scanning path only;
            // native RemoveAll and Unity scene/proxy cleanup are excluded.
            const int frames = 320, removalCount = 64;
            var dense = Make(12, 36, 0, 103);
            var removalIds = Enumerable.Range(0, removalCount)
                .Select(i => (long)i + 10).ToArray();
            Action legacyFrame = () =>
            {
                foreach (long uid in removalIds)
                    foreach (Galaxy galaxy in dense.SelectMany(p => p.Known).Distinct())
                        IgnoreVisit(galaxy, uid);
            };
            Action newFrame = () =>
            {
                FleetGalaxyRemovalWalker<Player, Galaxy> walker = null;
                foreach (long uid in removalIds)
                {
                    if (walker == null) walker = new FleetGalaxyRemovalWalker<Player, Galaxy>();
                    walker.VisitDistinct(dense, KnownGalaxies, uid, IgnoreVisit);
                }
            };
            long oldBytes = Measure(legacyFrame, frames);
            long newBytes = Measure(newFrame, frames);
            Check(oldBytes > newBytes && oldBytes - newBytes >= 1000000,
                "reusable production duplicate filter reduces dense galaxy-removal allocations");
            Console.WriteLine("INFO: " + frames + " synthetic frames × " + removalCount +
                " removed fleets across 12 player galaxy lists: old SelectMany/Distinct " +
                oldBytes + " B, production frame walker " + newBytes +
                " B (only distinct iteration managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks + " linked native galaxy removal checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
