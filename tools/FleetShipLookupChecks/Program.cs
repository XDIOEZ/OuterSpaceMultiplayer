using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private class NativeShip
    {
        internal readonly long Id;
        internal long NativeReads;
        internal NativeShip(long id) { Id = id; }
        internal long GetUid() { NativeReads++; return Id; }
    }
    private sealed class FighterShip : NativeShip
    {
        internal FighterShip(long uid) : base(uid) { }
    }
    private sealed class CargoShip : NativeShip
    {
        internal CargoShip(long uid) : base(uid) { }
    }

    private static readonly Func<NativeShip, long> Uid = ship => ship.GetUid();
    private static int assertions;
    private static void Check(bool yes, string description)
    {
        if (!yes) throw new InvalidOperationException("FAILED: " + description);
        assertions++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static string Result(Func<NativeShip> read, out NativeShip ship)
    {
        ship = null;
        try { ship = read(); return "ok"; }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }

    private static NativeShip OldLookup(IEnumerable<NativeShip> stored,
        IEnumerable<NativeShip> fleet, long target)
    {
        // The precise pre-optimization source precedence and predicate.
        return stored.SingleOrDefault(ship => ship.GetUid() == target)
            ?? fleet.SingleOrDefault(ship => ship.GetUid() == target);
    }
    private static void CompareLookups(List<NativeShip> stored, List<NativeShip> fleet,
        long[] requested, string label)
    {
        var index = new NativeFleetShipLookup<NativeShip>(stored, fleet, Uid);
        foreach (long uid in requested)
        {
            NativeShip original, found;
            string oldResult = Result(() => OldLookup(stored, fleet, uid), out original);
            string newResult = Result(() => index.Find(uid), out found);
            Check(oldResult == newResult && ReferenceEquals(original, found),
                label + " UID=" + uid + " old=" + oldResult + " new=" + newResult);
        }
    }

    private static string OldValidation(List<NativeShip> incoming,
        List<NativeShip> stored, List<NativeShip> fleet)
    {
        try
        {
            if (incoming == null) throw new InvalidOperationException("Fleet ship content is null.");
            var ids = new HashSet<long>();
            foreach (NativeShip ship in incoming)
            {
                if (ship == null || !ids.Add(ship.GetUid()))
                    throw new InvalidOperationException("Fleet ship content has a missing or duplicated identity.");
                NativeShip current = OldLookup(stored, fleet, ship.GetUid());
                if (current != null && current.GetType() != ship.GetType())
                    throw new InvalidOperationException("Fleet ship content changed its native type.");
            }
            return "ok";
        }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static string NewValidation(List<NativeShip> incoming,
        List<NativeShip> stored, List<NativeShip> fleet)
    {
        try
        {
            if (incoming == null) throw new InvalidOperationException("Fleet ship content is null.");
            var ids = new HashSet<long>();
            var index = NativeFleetShipLookup<NativeShip>.ShouldIndex(incoming.Count)
                ? new NativeFleetShipLookup<NativeShip>(stored, fleet, Uid) : null;
            foreach (NativeShip ship in incoming)
            {
                if (ship == null || !ids.Add(ship.GetUid()))
                    throw new InvalidOperationException("Fleet ship content has a missing or duplicated identity.");
                NativeShip current = index == null
                    ? OldLookup(stored, fleet, ship.GetUid())
                    : index.Find(ship.GetUid());
                if (current != null && current.GetType() != ship.GetType())
                    throw new InvalidOperationException("Fleet ship content changed its native type.");
            }
            return "ok";
        }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static void CompareValidation(List<NativeShip> incoming,
        List<NativeShip> stored, List<NativeShip> fleet, string label)
    {
        string oldResult = OldValidation(incoming, stored, fleet);
        string newResult = NewValidation(incoming, stored, fleet);
        Check(oldResult == newResult, label + " original=" + oldResult + " indexed=" + newResult);
    }

    private static NativeShip Ship(long id, bool fighter)
    { return fighter ? (NativeShip)new FighterShip(id) : new CargoShip(id); }
    private static List<NativeShip> Ships(int count, long start, bool fighter = false)
    {
        var list = new List<NativeShip>(count);
        for (int i = 0; i < count; i++) list.Add(Ship(start + i, fighter));
        return list;
    }
    private static long Reads(IEnumerable<NativeShip> ships)
    { return ships.Where(ship => ship != null).Sum(ship => ship.NativeReads); }
    private static void ResetReads(IEnumerable<NativeShip> ships)
    { foreach (NativeShip ship in ships) if (ship != null) ship.NativeReads = 0; }

    private static int Main()
    {
        try
        {
            Check(!NativeFleetShipLookup<NativeShip>.ShouldIndex(0)
                && !NativeFleetShipLookup<NativeShip>.ShouldIndex(7)
                && NativeFleetShipLookup<NativeShip>.ShouldIndex(8)
                && NativeFleetShipLookup<NativeShip>.ShouldIndex(512),
                "dense ship-content threshold leaves small deltas on old LINQ path");
            Check(Throws<ArgumentNullException>(() =>
                new NativeFleetShipLookup<NativeShip>(new NativeShip[0],
                    new NativeShip[0], null)),
                "null native UID selector cannot be installed");
            var stored = Ships(16, 1000);
            var fleet = Ships(16, 2000);
            var lookup = new NativeFleetShipLookup<NativeShip>(stored, fleet, Uid);
            Check(Reads(stored) == 0 && Reads(fleet) == 0,
                "creating the per-content index does not enumerate native collections");
            Check(ReferenceEquals(lookup.Find(1000), stored[0]) && Reads(stored) == 16,
                "first stored-asset match indexes the owner catalog only once");
            Check(Reads(fleet) == 0, "owner-first match never enumerates fleet fallback");
            Check(ReferenceEquals(lookup.Find(1000), stored[0]) && Reads(stored) == 16,
                "repeat owner lookup retains native object and does not rescan stored ships");
            Check(ReferenceEquals(lookup.Find(2000), fleet[0]) && Reads(fleet) == 16,
                "fleet-only native object indexed on first fallback");
            Check(lookup.Find(9999) == null && Reads(stored) == 16 && Reads(fleet) == 16,
                "missing ship UID never creates or re-reads authoritative assets");
            CompareLookups(stored, fleet,
                new long[] { 999, 1000, 1007, 1015, 2000, 2007, 2015, 9999 },
                "standard owner-first native ship lookup");

            var sharedStored = new NativeShip(5);
            var sharedFleet = new NativeShip(5);
            CompareLookups(new List<NativeShip> { sharedStored },
                new List<NativeShip> { sharedFleet },
                new long[] { 5, 5, -1 },
                "same UID in warehouse and fleet retains warehouse identity precedence");
            var highestBits = new List<NativeShip>
                { Ship(long.MinValue, false), Ship(long.MaxValue, false),
                  Ship(1L << 32, false), Ship(-1, false), Ship(0, false) };
            CompareLookups(highestBits, new List<NativeShip>(),
                new long[] { long.MinValue, long.MaxValue, 1L << 32, -1, 0, 1 },
                "full 64-bit UID and missing sentinel isolation");
            var unrelatedDuplicates = new List<NativeShip>
                { Ship(11, false), Ship(12, false), Ship(12, false),
                  Ship(13, false), Ship(14, false) };
            CompareLookups(unrelatedDuplicates, new List<NativeShip>(),
                new long[] { 11, 12, 13, 14, 15 },
                "malformed owner list duplicate only rejects requested matching UID");
            var duplicateFleet = new List<NativeShip>
                { Ship(21, false), Ship(22, false), Ship(22, false),
                  Ship(23, false), Ship(24, false) };
            CompareLookups(new List<NativeShip>(), duplicateFleet,
                new long[] { 21, 22, 23, 25 },
                "malformed fleet list duplicate preserves LINQ SingleOrDefault behavior");
            CompareLookups(new List<NativeShip> { Ship(1, false), null, Ship(3, false) },
                new List<NativeShip> { Ship(4, false) },
                new long[] { 1, 3, 4, 999 },
                "null native stored ship triggers original exception precedence");
            CompareLookups(new List<NativeShip> { Ship(99, false) },
                new List<NativeShip> { Ship(4, false), null, Ship(5, false) },
                new long[] { 99, 4, 5, 999 },
                "null native fleet fallback still fails only if consulted");
            CompareValidation(null, stored, fleet, "null incoming list is rejected before any index");
            CompareValidation(new List<NativeShip> { null }, stored, fleet,
                "null incoming ship is rejected before scanning native sources");
            CompareValidation(new List<NativeShip> { Ship(777, false), Ship(777, false) },
                stored, fleet, "duplicate incoming UID retains the old rejection");
            CompareValidation(new List<NativeShip> { Ship(1000, true) },
                stored, fleet, "existing native UID with changed ship type still rejects");
            CompareValidation(new List<NativeShip> { Ship(2000, true) },
                stored, fleet, "fallback fleet UID with changed native ship type still rejects");
            CompareValidation(new List<NativeShip> { Ship(777, true) },
                stored, fleet, "new incoming ship type is valid when UID is unclaimed");
            CompareValidation(Ships(10, 1000), stored, fleet,
                "ten incoming ships use indexed path with identical type checks");
            CompareValidation(Ships(10, 2000), stored, fleet,
                "ten fleet-only incoming ships use indexed fallback");

            // The helper exists only for prewrite validation of one incoming
            // fleet-content record. A later generation creates a new index
            // after adding/replacing an underlying native ship.
            var firstGen = new List<NativeShip> { Ship(7, false) };
            var oldIndex = new NativeFleetShipLookup<NativeShip>(firstGen,
                new NativeShip[0], Uid);
            NativeShip oldReference = oldIndex.Find(7);
            firstGen[0] = Ship(7, false);
            var newIndex = new NativeFleetShipLookup<NativeShip>(firstGen,
                new NativeShip[0], Uid);
            Check(ReferenceEquals(oldIndex.Find(7), oldReference)
                && ReferenceEquals(newIndex.Find(7), firstGen[0])
                && !ReferenceEquals(newIndex.Find(7), oldReference),
                "reconnect/replacement rebuilds an isolated native UID index");

            var random = new Random(20261018);
            for (int run = 0; run < 2400; run++)
            {
                var ownerShips = new List<NativeShip>();
                var fleetShips = new List<NativeShip>();
                var incoming = new List<NativeShip>();
                for (int i = 0, n = random.Next(0, 40); i < n; i++)
                    ownerShips.Add(random.Next(0, 27) == 0 ? null :
                        Ship(random.Next(-10, 64), random.Next(0, 2) == 0));
                for (int i = 0, n = random.Next(0, 40); i < n; i++)
                    fleetShips.Add(random.Next(0, 27) == 0 ? null :
                        Ship(random.Next(-10, 64), random.Next(0, 2) == 0));
                for (int i = 0, n = random.Next(0, 25); i < n; i++)
                    incoming.Add(random.Next(0, 30) == 0 ? null :
                        Ship(random.Next(-10, 64), random.Next(0, 2) == 0));
                var requests = new long[8];
                for (int i = 0; i < requests.Length; i++) requests[i] = random.Next(-10, 64);
                CompareLookups(ownerShips, fleetShips, requests, "random native lookup " + run);
                CompareValidation(incoming, ownerShips, fleetShips,
                    "random authoritative fleet payload " + run);
            }

            // Synthetic large-content read cost, excluding JSON/Unity/state
            // application. The old SingleOrDefault MUST inspect all stored
            // entries to rule out duplicate requested identities.
            const int storedCount = 256, fleetCount = 128, batch = 384, frames = 90;
            var warehouse = Ships(storedCount, 10000);
            var localFleet = Ships(fleetCount, 20000);
            var targets = new long[batch];
            for (int i = 0; i < storedCount; i++) targets[i] = 10000 + i;
            for (int i = 0; i < fleetCount; i++) targets[storedCount + i] = 20000 + i;
            long oldSourceReads = 0, newSourceReads = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                ResetReads(warehouse); ResetReads(localFleet);
                foreach (long uid in targets)
                    if (OldLookup(warehouse, localFleet, uid) == null)
                        throw new Exception("Old lookup lost ship");
                oldSourceReads += Reads(warehouse) + Reads(localFleet);

                ResetReads(warehouse); ResetReads(localFleet);
                var indexed = new NativeFleetShipLookup<NativeShip>(warehouse, localFleet, Uid);
                foreach (long uid in targets)
                    if (indexed.Find(uid) == null) throw new Exception("Index lost ship");
                newSourceReads += Reads(warehouse) + Reads(localFleet);
            }
            long expectedOld = (long)frames * (batch * storedCount + fleetCount * fleetCount);
            long expectedNew = (long)frames * (storedCount + fleetCount);
            Check(oldSourceReads == expectedOld && newSourceReads == expectedNew,
                "prewrite validation reduces quadratic UID reads to one owner/fleet scan");
            Console.WriteLine("INFO: " + frames + " content frames × " + batch +
                " incoming ships (256 warehouse + 128 fleet): legacy source UID reads " +
                oldSourceReads + ", indexed reads " + newSourceReads);
            Console.WriteLine("PASS: " + assertions +
                " linked native fleet ship validation checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
