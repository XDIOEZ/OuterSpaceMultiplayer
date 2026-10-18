using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private const string RemovalError = "A delta cannot remove an updated or primary player fleet.";

    private sealed class Fleet
    {
        internal readonly long Id;
        internal bool ThrowOnRead;
        internal int Reads;
        internal Fleet(long id, bool throws = false) { Id = id; ThrowOnRead = throws; }
        internal long GetUid()
        {
            Reads++;
            if (ThrowOnRead)
                throw new InvalidOperationException("native primary fleet UID unavailable");
            return Id;
        }
    }
    private sealed class GalaxyBlock
    {
        internal Fleet Primary;
    }
    private sealed class Player
    {
        internal GalaxyBlock GalaxyBlock;
        internal Player(Fleet fleet)
        { GalaxyBlock = new GalaxyBlock { Primary = fleet }; }
    }
    private static readonly Func<Player, bool> HasPrimary =
        player => player.GalaxyBlock.Primary != null;
    private static readonly Func<Player, long> PrimaryId =
        player => player.GalaxyBlock.Primary.GetUid();

    private static int checks;
    private static void Check(bool success, string label)
    {
        if (!success) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static string Outcome(Action run)
    {
        try { run(); return "accepted"; }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static int ReadCount(List<Player> players)
    {
        int count = 0;
        foreach (Player player in players)
            if (player != null && player.GalaxyBlock != null && player.GalaxyBlock.Primary != null)
                count += player.GalaxyBlock.Primary.Reads;
        return count;
    }
    private static void ResetReads(List<Player> players)
    {
        foreach (Player player in players)
            if (player != null && player.GalaxyBlock != null && player.GalaxyBlock.Primary != null)
                player.GalaxyBlock.Primary.Reads = 0;
    }
    private static List<Player> ClonePlayers(List<Player> source)
    {
        var result = new List<Player>(source.Count);
        foreach (Player player in source)
        {
            if (player == null) { result.Add(null); continue; }
            if (player.GalaxyBlock == null)
            {
                result.Add(new Player(null) { GalaxyBlock = null });
                continue;
            }
            Fleet fleet = player.GalaxyBlock.Primary;
            result.Add(new Player(fleet == null ? null :
                new Fleet(fleet.Id, fleet.ThrowOnRead)));
        }
        return result;
    }

    private static void LegacyCheck(List<Player> players, HashSet<long> removed,
        HashSet<long> updated, HashSet<long> posed)
    {
        foreach (long uid in removed)
            if (updated.Contains(uid) || posed.Contains(uid) ||
                players.Any(p => p.GalaxyBlock.Primary != null &&
                    p.GalaxyBlock.Primary.GetUid() == uid))
                throw new InvalidOperationException(RemovalError);
    }
    private static void IndexedCheck(List<Player> players, HashSet<long> removed,
        HashSet<long> updated, HashSet<long> posed)
    {
        // Mirrors precisely the production guard, including sparse fallback
        // and ids/poseIds short-circuit priority.
        var primaryIndex = removed.Count >= 8 && players.Count >= 3
            ? new FleetPrimaryRemovalIndex<Player>(players, HasPrimary, PrimaryId) : null;
        foreach (long uid in removed)
            if (updated.Contains(uid) || posed.Contains(uid) ||
                (primaryIndex == null
                    ? players.Any(p => p.GalaxyBlock.Primary != null &&
                        p.GalaxyBlock.Primary.GetUid() == uid)
                    : primaryIndex.Contains(uid)))
                throw new InvalidOperationException(RemovalError);
    }
    private static void Compare(List<Player> players, HashSet<long> removed,
        HashSet<long> updated, HashSet<long> posed, string reason)
    {
        var oldPlayers = ClonePlayers(players);
        var newPlayers = ClonePlayers(players);
        string old = Outcome(() => LegacyCheck(oldPlayers, removed, updated, posed));
        string current = Outcome(() => IndexedCheck(newPlayers, removed, updated, posed));
        Check(old == current, reason + " old=" + old + " new=" + current);
    }
    private static HashSet<long> Ids(params long[] ids) { return new HashSet<long>(ids); }
    private static long Allocated(Action action, int iterations, Action afterWarmup = null)
    {
        for (int i = 0; i < 100; i++) action();
        if (afterWarmup != null) afterWarmup();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    public static int Main()
    {
        try
        {
            Check(Throws<ArgumentNullException>(() =>
                new FleetPrimaryRemovalIndex<Player>(null, HasPrimary, PrimaryId)),
                "missing authoritative player registry cannot be indexed");
            Check(Throws<ArgumentNullException>(() =>
                new FleetPrimaryRemovalIndex<Player>(new Player[0], null, PrimaryId)),
                "missing native primary-existence selector cannot be indexed");
            Check(Throws<ArgumentNullException>(() =>
                new FleetPrimaryRemovalIndex<Player>(new Player[0], HasPrimary, null)),
                "missing native UID selector cannot be indexed");

            var boundary = new List<Player> {
                new Player(new Fleet(long.MinValue)), new Player(null),
                new Player(new Fleet(0)), new Player(new Fleet(1L << 32)),
                new Player(new Fleet(long.MaxValue)) };
            Compare(boundary, Ids(-1, 5, 9, 14, 20, 31, 42, 53),
                Ids(), Ids(), "complete signed 64-bit UID range with absent removals");
            Compare(boundary, Ids(-1, 5, 9, 0, 20, 31, 42, 53),
                Ids(), Ids(), "UID 0 is a real protected primary");
            Compare(boundary, Ids(-1, 5, 9, 14, 20, 31, long.MinValue, 53),
                Ids(), Ids(), "minimum signed UID primary is protected");
            Compare(boundary, Ids(-1, 5, 9, 14, 20, 31, long.MaxValue, 53),
                Ids(), Ids(), "maximum signed UID primary is protected");
            Compare(boundary, Ids(-1, 5, 9, 14, 20, 31, 1L << 32, 53),
                Ids(), Ids(), "UID high bits are significant");
            Compare(boundary, Ids(-1, 5, 9, 14, 20, 31, 42, 53),
                Ids(14), Ids(), "content identity conflict overrides primary scan");
            Compare(boundary, Ids(-1, 5, 9, 14, 20, 31, 42, 53),
                Ids(), Ids(42), "pose identity conflict overrides primary scan");

            for (int count = 0; count <= 12; count++)
            {
                var removed = new HashSet<long>();
                for (int i = 0; i < count; i++) removed.Add(100 + i);
                Compare(new List<Player> { new Player(new Fleet(200)),
                    new Player(new Fleet(300)), new Player(null) },
                    removed, Ids(), Ids(), "removed UID threshold " + count);
                Compare(new List<Player> { new Player(new Fleet(200)),
                    new Player(new Fleet(300)), new Player(null) },
                    removed, Ids(100), Ids(), "preflight short-circuit at threshold " + count);
            }
            var shortCircuit = new List<Player> {
                new Player(new Fleet(10)), null,
                new Player(new Fleet(50, true)), new Player(new Fleet(70)) };
            Compare(shortCircuit, Ids(10, 100, 200, 300, 400, 500, 600, 700),
                Ids(), Ids(), "matching first primary rejects before malformed later player");
            Compare(shortCircuit, Ids(11, 100, 200, 300, 400, 500, 600, 700),
                Ids(), Ids(), "unmatched first removal exposes null player at old location");
            Compare(shortCircuit, Ids(11, 100, 200, 300, 400, 500, 600, 700),
                Ids(11), Ids(), "updated UID rejection must skip malformed player scan");
            Compare(shortCircuit, Ids(11, 100, 200, 300, 400, 500, 600, 700),
                Ids(), Ids(11), "pose UID rejection must skip malformed player scan");

            var throwers = new List<Player> {
                new Player(new Fleet(11)), new Player(new Fleet(33, true)),
                new Player(new Fleet(55)), new Player(new Fleet(77)) };
            Compare(throwers, Ids(11, 100, 200, 300, 400, 500, 600, 700),
                Ids(), Ids(), "first matching primary must not touch a throwing later getter");
            Compare(throwers, Ids(12, 100, 200, 300, 400, 500, 600, 700),
                Ids(), Ids(), "native primary UID getter exception text/order retained");
            Compare(new List<Player> { new Player(new Fleet(11)),
                new Player(new Fleet(11)), new Player(null) },
                Ids(99, 100, 200, 300, 400, 500, 600, 700),
                Ids(), Ids(), "duplicate existing primary IDs do not confuse the cache");

            // The first unsuccessful full scan publishes no cache. A retry
            // after repairing a bad native getter sees the new authoritative
            // state (and never a partially indexed value).
            var recoverable = new List<Player> {
                new Player(new Fleet(11)), new Player(new Fleet(33, true)),
                new Player(new Fleet(55)) };
            var guard = new FleetPrimaryRemovalIndex<Player>(recoverable, HasPrimary, PrimaryId);
            Check(Throws<InvalidOperationException>(() => guard.Contains(999)),
                "failed first scan preserves native getter exception");
            recoverable[1].GalaxyBlock.Primary.ThrowOnRead = false;
            Check(guard.Contains(33) && !guard.Contains(999),
                "partial failure does not publish a cache; repaired UID remains protected");

            var random = new Random(20261018);
            long[] extremes = { long.MinValue, long.MaxValue, -1, 0, 1L << 32,
                (1L << 32) | 1 };
            for (int batch = 0; batch < 7000; batch++)
            {
                var players = new List<Player>();
                int n = random.Next(0, 45);
                for (int i = 0; i < n; i++)
                {
                    int mode = random.Next(0, 55);
                    if (mode == 0) { players.Add(null); continue; }
                    if (mode == 1) { players.Add(new Player(null) { GalaxyBlock = null }); continue; }
                    if (mode <= 8) { players.Add(new Player(null)); continue; }
                    long uid = random.Next(0, 5) == 0 ?
                        extremes[random.Next(extremes.Length)] :
                        random.Next(-300, 300);
                    players.Add(new Player(new Fleet(uid, mode == 9)));
                }
                var removed = new HashSet<long>();
                var changed = new HashSet<long>();
                var poses = new HashSet<long>();
                int m = random.Next(0, 100);
                for (int i = 0; i < m; i++)
                {
                    long uid = random.Next(0, 7) == 0 ?
                        extremes[random.Next(extremes.Length)] :
                        random.Next(-450, 450);
                    removed.Add(uid);
                    if (random.Next(0, 50) == 0) changed.Add(uid);
                    if (random.Next(0, 50) == 0) poses.Add(uid);
                }
                Compare(players, removed, changed, poses,
                    "random full/delta fleet-removal preflight " + batch);
            }

            // Only the primary-UID membership check is benchmarked, not
            // NativeWire, galaxy list mutation, Unity rendering or send ACK.
            const int playerCount = 32, removalCount = 256, frames = 800;
            var stable = new List<Player>();
            for (int i = 0; i < playerCount; i++)
                stable.Add(new Player(new Fleet(100000 + i)));
            var removals = new HashSet<long>();
            for (int i = 0; i < removalCount; i++) removals.Add(200000 + i);
            var empty = new HashSet<long>();
            Action oldFrame = () => LegacyCheck(stable, removals, empty, empty);
            Action indexedFrame = () => IndexedCheck(stable, removals, empty, empty);
            long oldBytes = Allocated(oldFrame, frames, () => ResetReads(stable));
            int oldReads = ReadCount(stable);
            long newBytes = Allocated(indexedFrame, frames, () => ResetReads(stable));
            int newReads = ReadCount(stable);
            Check(oldReads == playerCount * removalCount * frames &&
                newReads == playerCount * frames,
                "large batch scans all primary fleets once, not once per deleted UID");
            Check(oldBytes > newBytes,
                "dense removal preflight avoids repeated LINQ closures/enumerators");
            Console.WriteLine("INFO: " + frames + " dense removal validations × " +
                removalCount + " absent fleet IDs over " + playerCount +
                " player primaries: old native UID reads " + oldReads +
                ", indexed reads " + newReads +
                "; old managed allocations " + oldBytes + " B, indexed " +
                newBytes + " B (post-warmup, guard only)");
            Console.WriteLine("PASS: " + checks +
                " linked galaxy primary-fleet removal checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
