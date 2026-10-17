using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Fleet
    {
        internal readonly long Uid;
        internal readonly string Owner;
        internal Fleet(long uid, string owner) { Uid = uid; Owner = owner; }
    }

    private sealed class Source
    {
        internal readonly Dictionary<string, List<Fleet>> ByOwner = new Dictionary<string, List<Fleet>>(StringComparer.Ordinal);
        internal long SourceReads, Visited;

        internal IEnumerable<Fleet> Owned(string player)
        {
            SourceReads++;
            foreach (Fleet fleet in ByOwner[player])
            {
                Visited++;
                yield return fleet;
            }
        }

        internal void Add(string owner, long uid)
        {
            List<Fleet> items;
            if (!ByOwner.TryGetValue(owner, out items)) ByOwner.Add(owner, items = new List<Fleet>());
            items.Add(new Fleet(uid, owner));
        }
    }

    private static int passed;
    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + description);
        passed++;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static int Main()
    {
        try
        {
            Check(!FleetFrameIndex<Fleet>.ShouldIndex(0, 0), "empty frame stays on the original linear path");
            Check(!FleetFrameIndex<Fleet>.ShouldIndex(3, 4), "small content/pose deltas retain the old path");
            Check(FleetFrameIndex<Fleet>.ShouldIndex(4, 4), "dense frame selects one-shot index");
            Check(FleetFrameIndex<Fleet>.ShouldIndex(0, 100), "pose-only dense deltas use one-shot index");
            Check(FleetFrameIndex<Fleet>.ShouldIndex(100, 0), "content-only dense deltas use one-shot index");
            Check(FleetFrameIndex<Fleet>.ShouldIndex(int.MaxValue, int.MaxValue),
                "count threshold uses overflow-safe arithmetic");
            Check(Throws<ArgumentNullException>(() => new FleetFrameIndex<Fleet>(null, f => f.Uid)),
                "missing owned-fleet source is rejected");
            Check(Throws<ArgumentNullException>(() => new FleetFrameIndex<Fleet>(_ => new Fleet[0], null)),
                "missing fleet identity selector is rejected");

            var source = new Source();
            for (int i = 0; i < 96; i++) source.Add("host", 1000 + i);
            for (int i = 0; i < 96; i++) source.Add("guest", 2000 + i);
            var indexed = new FleetFrameIndex<Fleet>(source.Owned, f => f.Uid);
            Check(source.SourceReads == 0, "creating an index does not scan any player");
            Check(indexed.Contains("guest", 2000) && source.SourceReads == 1,
                "first guest lookup scans only the corresponding player");
            Check(!indexed.Contains("guest", 1000) && indexed.SingleOrDefault("guest", 1000) == null,
                "owner-isolated lookup never hands out another player's fleet");
            Check(indexed.SingleOrDefault("guest", 2000).Owner == "guest"
                && source.SourceReads == 1,
                "repeated guest probes reuse the exact native instance without scanning");
            Check(indexed.Single("host", 1000).Owner == "host" && source.SourceReads == 2,
                "second owner is loaded on demand and kept separate");
            Check(Throws<InvalidOperationException>(() => indexed.Single("host", -1)),
                "required pose fleet is rejected when missing");
            Check(indexed.All("host").Count() == 96 && indexed.All("guest").Count() == 96,
                "full baseline iteration preserves all original fleets and their order");

            // Reference behavior is LINQ SingleOrDefault over OwnedFleets.
            // A dense 512-pose frame used to scan 96 members on every lookup.
            const int probes = 512;
            source.SourceReads = source.Visited = 0;
            for (int i = 0; i < probes; i++)
            {
                long uid = 1000 + i % 96;
                Check(ReferenceEquals(source.Owned("host").SingleOrDefault(f => f.Uid == uid),
                    indexed.SingleOrDefault("host", uid)),
                    "indexed pose returns the same native object as the old SingleOrDefault");
            }
            long previousWalks = source.Visited, previousReads = source.SourceReads;
            Check(previousReads == probes && previousWalks == probes * 96,
                "reference traversal exposes the original pose-by-fleet scan cost");
            source.SourceReads = source.Visited = 0;
            var newFrame = new FleetFrameIndex<Fleet>(source.Owned, f => f.Uid);
            for (int i = 0; i < probes; i++)
                Check(newFrame.Single("host", 1000 + i % 96).Uid == 1000 + i % 96,
                    "new frame indexes dense poses without losing an identity");
            Check(source.SourceReads == 1 && source.Visited == 96,
                "512 pose lookups enumerate 96 source fleets only once");
            Console.WriteLine("INFO: 512 synthetic pose lookups across 96 fleets: old visited " +
                previousWalks + " items in " + previousReads +
                " source reads; indexed version visited " + source.Visited +
                " items in " + source.SourceReads + " source read");

            // Native full snapshot creates a new FWFleet before applying its
            // corresponding FleetPose. That created instance must be registered
            // without reloading the source or falling back to a stale UID.
            var created = new Fleet(5000, "guest");
            long reads = source.SourceReads;
            Check(!newFrame.Contains("host", 5000), "unregistered UID is not implicitly considered present");
            Check(Throws<InvalidOperationException>(() => newFrame.RegisterCreated("host", 5000, null)),
                "a null created fleet is rejected");
            Check(Throws<InvalidOperationException>(() => newFrame.RegisterCreated("host", 5000, new Fleet(6000, "host"))),
                "created fleet cannot be registered under a different UID");
            newFrame.RegisterCreated("guest", 5000, created);
            Check(ReferenceEquals(newFrame.Single("guest", 5000), created)
                && !newFrame.Contains("host", 5000),
                "new fleet is available only to its owning player during same-frame pose apply");
            Check(newFrame.All("guest").Last() == created && source.SourceReads == reads + 1,
                "created fleet appends to guest snapshot without a second owner scan");

            // Preserve original SingleOrDefault semantics for duplicate native
            // UIDs: unrelated duplicates do not make different UID probes fail.
            var dupSource = new Source();
            dupSource.Add("guest", 7);
            dupSource.Add("guest", 7);
            dupSource.Add("guest", 9);
            var duplicated = new FleetFrameIndex<Fleet>(dupSource.Owned, f => f.Uid);
            Check(duplicated.Contains("guest", 7) && duplicated.Contains("guest", 9),
                "Any-style pose validation still finds a duplicated UID");
            Check(Throws<InvalidOperationException>(() => duplicated.SingleOrDefault("guest", 7)),
                "duplicate requested UID retains old LINQ SingleOrDefault rejection");
            Check(duplicated.SingleOrDefault("guest", 9).Uid == 9 && duplicated.All("guest").Count() == 3,
                "unrelated duplicate UIDs retain their prior nonblocking lookup behavior");
            duplicated.RegisterCreated("guest", 9, new Fleet(9, "guest"));
            Check(Throws<InvalidOperationException>(() => duplicated.Single("guest", 9)),
                "newly registered duplicate fleet identity is rejected on Single lookup");
            Check(Throws<KeyNotFoundException>(() => duplicated.Contains("missing", 7)),
                "unknown player retains failure from the underlying ownership source");

            // Indexes are never retained across frames; a new frame can see
            // replacement native objects after a baseline or ownership change.
            var previousNative = source.ByOwner["host"][0];
            source.ByOwner["host"][0] = new Fleet(1000, "host");
            Check(ReferenceEquals(indexed.Single("host", 1000), previousNative),
                "one frame holds its original native reference throughout transaction");
            var following = new FleetFrameIndex<Fleet>(source.Owned, f => f.Uid);
            Check(!ReferenceEquals(following.Single("host", 1000), previousNative)
                && ReferenceEquals(following.Single("host", 1000), source.ByOwner["host"][0]),
                "next frame observes replacement object and cannot reuse the old frame's cache");

            Console.WriteLine("PASS: " + passed + " linked fleet-frame UID index checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
