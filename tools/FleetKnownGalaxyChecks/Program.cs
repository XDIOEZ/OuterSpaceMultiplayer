using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Galaxy
    {
        internal string Id;
        internal int Reads;
        internal Galaxy(string id) { Id = id; }
        internal string GetId() { Reads++; return Id; }
    }
    private sealed class Pose
    {
        internal readonly string Player, GalaxyId;
        internal readonly long Uid;
        internal readonly bool Owned;
        internal readonly float X;
        internal Pose(string player, string galaxy, long uid, bool owned = true, float x = 1)
        { Player = player; GalaxyId = galaxy; Uid = uid; Owned = owned; X = x; }
    }
    private sealed class Frame
    {
        internal readonly Dictionary<string, IEnumerable<Galaxy>> Players =
            new Dictionary<string, IEnumerable<Galaxy>>(StringComparer.Ordinal);
        internal readonly List<Pose> Poses = new List<Pose>();
    }
    private sealed class OneShot : IEnumerable<Galaxy>
    {
        internal int Visits;
        public IEnumerator<Galaxy> GetEnumerator()
        {
            Visits++;
            yield return new Galaxy("first");
            throw new InvalidOperationException("second native read unavailable");
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    private static readonly Func<Galaxy, string> GalaxyId = value => value.GetId();
    private static int checks;
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static string Error(Action action)
    {
        try { action(); return "accepted"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
    private static string ReadOld(Frame frame)
    {
        return Error(() =>
        {
            var ids = new HashSet<long>();
            foreach (Pose pose in frame.Poses)
            {
                IEnumerable<Galaxy> known;
                if (!frame.Players.TryGetValue(pose.Player, out known)
                    || !pose.Owned || !ids.Add(pose.Uid)
                    || String.IsNullOrEmpty(pose.GalaxyId) || pose.GalaxyId == "invalid"
                    || !known.Any(galaxy => galaxy.GetId() == pose.GalaxyId)
                    || float.IsNaN(pose.X) || float.IsInfinity(pose.X))
                    throw new InvalidOperationException("Invalid authoritative fleet pose.");
            }
        });
    }
    private static string ReadNew(Frame frame)
    {
        return Error(() =>
        {
            var index = FleetKnownGalaxyIndex<Galaxy>.ShouldIndex(frame.Poses.Count)
                ? new FleetKnownGalaxyIndex<Galaxy>(GalaxyId) : null;
            var ids = new HashSet<long>();
            foreach (Pose pose in frame.Poses)
            {
                IEnumerable<Galaxy> known;
                if (!frame.Players.TryGetValue(pose.Player, out known)
                    || !pose.Owned || !ids.Add(pose.Uid)
                    || String.IsNullOrEmpty(pose.GalaxyId) || pose.GalaxyId == "invalid"
                    || !(index == null
                        ? known.Any(galaxy => galaxy.GetId() == pose.GalaxyId)
                        : index.Contains(pose.Player, known, pose.GalaxyId))
                    || float.IsNaN(pose.X) || float.IsInfinity(pose.X))
                    throw new InvalidOperationException("Invalid authoritative fleet pose.");
            }
        });
    }
    private static void Same(Frame frame, string label)
    {
        string old = ReadOld(frame), current = ReadNew(frame);
        Check(old == current, label + " legacy=" + old + " indexed=" + current);
    }
    private static List<Galaxy> MakeGalaxies(int count, string prefix = "galaxy-")
    {
        var galaxies = new List<Galaxy>(count);
        for (int i = 0; i < count; i++)
            galaxies.Add(new Galaxy(prefix + i));
        return galaxies;
    }
    private static long CountReads(IEnumerable<Galaxy> galaxies)
    { return galaxies.Where(x => x != null).Sum(x => (long)x.Reads); }
    private static void ClearReads(IEnumerable<Galaxy> galaxies)
    { foreach (Galaxy galaxy in galaxies) if (galaxy != null) galaxy.Reads = 0; }
    private static long Allocated(Action action, int count)
    {
        for (int i = 0; i < 40; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    private static void ExpectSameQuery(FleetKnownGalaxyIndex<Galaxy> index,
        string player, IEnumerable<Galaxy> galaxies, string id, string label)
    {
        string legacy = null, modern = null;
        bool oldValue = false, newValue = false;
        try { oldValue = galaxies.Any(g => g.GetId() == id); }
        catch (Exception ex) { legacy = ex.GetType().Name + ": " + ex.Message; }
        try { newValue = index.Contains(player, galaxies, id); }
        catch (Exception ex) { modern = ex.GetType().Name + ": " + ex.Message; }
        Check(legacy == modern && (legacy != null || oldValue == newValue),
            label + " previous=" + legacy + " new=" + modern);
    }

    public static int Main()
    {
        try
        {
            Check(!FleetKnownGalaxyIndex<Galaxy>.ShouldIndex(0)
                && !FleetKnownGalaxyIndex<Galaxy>.ShouldIndex(7)
                && FleetKnownGalaxyIndex<Galaxy>.ShouldIndex(8)
                && FleetKnownGalaxyIndex<Galaxy>.ShouldIndex(16000),
                "only dense pose frames attempt a per-player membership cache");
            Check(Error(() => new FleetKnownGalaxyIndex<Galaxy>(null))
                .StartsWith("ArgumentNullException:"),
                "native galaxy ID selector is required");

            var index = new FleetKnownGalaxyIndex<Galaxy>(GalaxyId);
            var known = MakeGalaxies(16);
            foreach (Galaxy galaxy in known)
                ExpectSameQuery(index, "player-A", known, galaxy.Id,
                    "known legitimate physical galaxy");
            ExpectSameQuery(index, "player-A", known, "GALAXY-0",
                "case-sensitive membership matches original == string operator");
            ExpectSameQuery(index, "player-A", known, "not-discovered",
                "unknown galaxy remains unauthorized");
            long readsBeforeCachedHit = CountReads(known);
            for (int i = 0; i < 50; i++)
                Check(index.Contains("player-A", known, "galaxy-15"),
                    "repeated indexed hit preserves membership");
            Check(CountReads(known) == readsBeforeCachedHit,
                "once built, the same owner's cached IDs require no further native reads");

            var p1 = MakeGalaxies(10, "alpha-");
            var p2 = MakeGalaxies(10, "beta-");
            ExpectSameQuery(index, "player-A", p1, "alpha-9",
                "replaced native known-galaxy list refreshes the same player's index");
            ExpectSameQuery(index, "player-B", p2, "beta-9",
                "other player receives its own known-galaxy membership");
            ExpectSameQuery(index, "player-A", p1, "beta-9",
                "a galaxy learned by another player is not authorized");
            p1.Add(new Galaxy("alpha-10"));
            ExpectSameQuery(index, "player-A", p1, "alpha-10",
                "native list count change within a frame rebuilds the index");

            var fullIds = MakeGalaxies(11);
            fullIds[0].Id = "星系🌌";
            fullIds[1].Id = "e\u0301";
            fullIds[2].Id = "\u00e9";
            fullIds[3].Id = "star:home";
            fullIds[4].Id = "";
            fullIds[5].Id = null;
            var cultureIndex = new FleetKnownGalaxyIndex<Galaxy>(GalaxyId);
            foreach (string text in new string[] { "星系🌌", "e\u0301", "\u00e9",
                "star:home", "STAR:HOME", "", null, "missing" })
                ExpectSameQuery(cultureIndex, "unicode-owner", fullIds, text,
                    "ordinal native galaxy membership including null IDs");

            var malformed = MakeGalaxies(12);
            malformed[9] = null;
            var badIndex = new FleetKnownGalaxyIndex<Galaxy>(GalaxyId);
            ExpectSameQuery(badIndex, "bad-owner", malformed, "galaxy-0",
                "null native galaxy after an early match must not reject that match");
            ExpectSameQuery(badIndex, "bad-owner", malformed, "galaxy-11",
                "null native galaxy still fails at original predicate position");
            ExpectSameQuery(badIndex, "bad-owner", malformed, "missing",
                "missing galaxy with malformed native list preserves exception type/text");
            ExpectSameQuery(badIndex, "bad-owner", null, "galaxy-0",
                "null native collection preserves Enumerable.Any error");

            var duplicate = MakeGalaxies(10);
            duplicate[9].Id = duplicate[1].Id;
            ExpectSameQuery(new FleetKnownGalaxyIndex<Galaxy>(GalaxyId),
                "duplicate-owner", duplicate, "galaxy-1",
                "duplicate native galaxy IDs still satisfy Any without new rejection");
            Check(Error(() => duplicate.Single(g => g.GetId() == "galaxy-1"))
                .StartsWith("InvalidOperationException:"),
                "original creation-time Single still rejects duplicate native galaxy IDs");

            var sparse = MakeGalaxies(7);
            var sparseIndex = new FleetKnownGalaxyIndex<Galaxy>(GalaxyId);
            for (int i = 0; i < 30; i++)
                ExpectSameQuery(sparseIndex, "small-owner", sparse, "galaxy-6",
                    "sparse known-galaxy list keeps original enumeration");
            Check(CountReads(sparse) == 30 * 7 * 2,
                "sparse path performs the same linear scan as the original");

            var oneShot = new OneShot();
            var providerIndex = new FleetKnownGalaxyIndex<Galaxy>(GalaxyId);
            ExpectSameQuery(providerIndex, "lazy-owner", oneShot, "first",
                "unknown IEnumerable is never eagerly indexed beyond old Any");
            Check(oneShot.Visits == 2,
                "unusual enumerable retains exactly one visit per query");

            var frame = new Frame();
            frame.Players.Add("owner-A", MakeGalaxies(32));
            frame.Players.Add("owner-B", MakeGalaxies(32, "remote-"));
            for (int i = 0; i < 30; i++)
                frame.Poses.Add(new Pose(i % 2 == 0 ? "owner-A" : "owner-B",
                    i % 2 == 0 ? "galaxy-31" : "remote-31", i));
            Same(frame, "valid high-density authoritative fleet pose frame");
            frame.Poses[27] = new Pose("owner-A", "remote-31", 27);
            Same(frame, "cross-player galaxy permission is never merged");
            frame.Poses[27] = new Pose("owner-A", "galaxy-31", 27, owned: false);
            Same(frame, "ownership check rejects before known-galaxy lookup");
            frame.Poses[27] = new Pose("owner-A", "galaxy-31", 27, x: float.NaN);
            Same(frame, "finite coordinate guard retains the prior rejection");
            frame.Poses[27] = new Pose("owner-A", "galaxy-31", 0);
            Same(frame, "duplicate fleet UID rejects ahead of galaxy validation");
            frame.Poses[27] = new Pose("owner-A", "invalid", 27);
            Same(frame, "bad physical galaxy syntax rejects ahead of galaxy membership");
            frame.Poses[27] = new Pose("owner-A", "galaxy-31", 27);

            var random = new Random(20261018);
            for (int trial = 0; trial < 6500; trial++)
            {
                var state = new Frame();
                int players = random.Next(1, 6);
                for (int p = 0; p < players; p++)
                {
                    int n = random.Next(0, 65);
                    List<Galaxy> list = MakeGalaxies(n, "planet-" + p + "-");
                    if (n != 0 && random.Next(0, 16) == 0) list[random.Next(n)] = null;
                    if (n > 2 && list[n - 1] != null && random.Next(0, 10) == 0)
                        list[n - 1].Id = list[0] == null ? "same" : list[0].Id;
                    state.Players.Add("player-" + p,
                        random.Next(0, 50) == 0 ? null : list);
                }
                int poseCount = random.Next(0, 115);
                for (int i = 0; i < poseCount; i++)
                {
                    int p = random.Next(players + 2);
                    string user = "player-" + p;
                    string galaxyId = "planet-" + p + "-" + random.Next(0, 72);
                    if (random.Next(0, 19) == 0) galaxyId = "invalid";
                    if (random.Next(0, 40) == 0) galaxyId = null;
                    long uid = random.Next(0, 22) == 0
                        ? random.Next(0, 30) : trial * 1000L + i;
                    state.Poses.Add(new Pose(user, galaxyId, uid,
                        owned: random.Next(0, 17) != 0,
                        x: random.Next(0, 25) == 0 ? float.NaN : 1));
                }
                Same(state, "random authoritative fleet frame " + trial);
            }

            // The test counts real ID reads through the production-linked
            // selector rather than treating HashSet lookups as equivalent CPU
            // instructions. Native pose decoding/writes are not measured.
            const int playerCount = 4, galaxyCount = 128, poseCountDense = 1024, rounds = 400;
            var densePlayers = new Dictionary<string, List<Galaxy>>(StringComparer.Ordinal);
            var densePoses = new List<Pose>(poseCountDense);
            for (int p = 0; p < playerCount; p++)
                densePlayers.Add("owner-" + p, MakeGalaxies(galaxyCount, "g-" + p + "-"));
            for (int i = 0; i < poseCountDense; i++)
            {
                int owner = i % playerCount;
                densePoses.Add(new Pose("owner-" + owner,
                    "g-" + owner + "-" + (galaxyCount - 1), i));
            }
            Action oldFrame = () =>
            {
                foreach (Pose pose in densePoses)
                    if (!densePlayers[pose.Player].Any(g => g.GetId() == pose.GalaxyId))
                        throw new InvalidOperationException("Old fleet pose lost physical galaxy");
            };
            Action newFrame = () =>
            {
                var check = new FleetKnownGalaxyIndex<Galaxy>(GalaxyId);
                foreach (Pose pose in densePoses)
                    if (!check.Contains(pose.Player, densePlayers[pose.Player], pose.GalaxyId))
                        throw new InvalidOperationException("Indexed fleet pose lost physical galaxy");
            };
            foreach (List<Galaxy> values in densePlayers.Values) ClearReads(values);
            for (int i = 0; i < rounds; i++) oldFrame();
            long oldReads = densePlayers.Values.Sum(CountReads);
            foreach (List<Galaxy> values in densePlayers.Values) ClearReads(values);
            for (int i = 0; i < rounds; i++) newFrame();
            long newReads = densePlayers.Values.Sum(CountReads);
            Check(oldReads == (long)rounds * poseCountDense * galaxyCount,
                "old Any scans every known galaxy for every fleet pose");
            Check(newReads == (long)rounds * playerCount * galaxyCount,
                "new index reads each owner's known galaxies once per complete frame");
            long oldBytes = Allocated(oldFrame, rounds);
            long newBytes = Allocated(newFrame, rounds);
            Check(oldBytes > newBytes,
                "per-frame native membership index reduces managed allocations");
            Console.WriteLine("INFO: " + rounds + " pose frames with " + poseCountDense +
                " fleet poses, " + playerCount + " owners × " + galaxyCount +
                " known galaxies: old native ID reads " + oldReads +
                ", indexed reads " + newReads + "; managed allocation old " +
                oldBytes + " B, indexed " + newBytes + " B (after warmup)");
            Console.WriteLine("PASS: " + checks +
                " production-linked known-galaxy membership checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
