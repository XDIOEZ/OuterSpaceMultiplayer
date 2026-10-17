using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private class Ship
    {
        internal long Uid, Reads;
        internal int Hull;
        internal string Name, Identity;
        internal Ship(long id, string identity, int hull = 10)
        { Uid = id; Identity = identity; Hull = hull; Name = "native-" + id; }
        internal long GetUid() { Reads++; return Uid; }
    }
    private sealed class CarrierShip : Ship
    {
        internal CarrierShip(long id, string identity) : base(id, identity) { }
    }
    private sealed class World
    {
        internal List<Ship> Warehouse = new List<Ship>();
        internal List<Ship> Fleet = new List<Ship>();
        internal List<Ship> Incoming = new List<Ship>();
    }

    private static readonly Func<Ship, long> Uid = s => s.GetUid();
    private static int checks;
    private static void Check(bool yes, string label)
    {
        if (!yes) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static void Copy(Ship incoming, Ship current)
    {
        if (incoming.GetType() != current.GetType())
            throw new InvalidOperationException("Native Bean type changed without a remove/create transaction.");
        // Model NativeWire.Copy: it copies native payload and UID in place,
        // never destroys the canonical native instance or its asset wrappers.
        current.Uid = incoming.Uid;
        current.Hull = incoming.Hull;
        current.Name = incoming.Name;
    }
    private static void OldApply(List<Ship> incoming, IEnumerable<Ship> stored,
        List<Ship> fleet, Action<Ship, Ship> copy)
    {
        // Exact previous ApplyFleetShips implementation, including source
        // precedence, native copy order, and delayed list replacement.
        var canonical = new List<Ship>(incoming.Count);
        foreach (Ship value in incoming)
        {
            Ship current = stored.SingleOrDefault(s => s.GetUid() == value.GetUid())
                ?? fleet.SingleOrDefault(s => s.GetUid() == value.GetUid());
            if (current == null) current = value;
            else copy(value, current);
            canonical.Add(current);
        }
        fleet.Clear();
        fleet.AddRange(canonical);
    }
    private static void NewApply(World world, Action<Ship, Ship> copy = null)
    {
        NativeFleetShipApply.Apply(world.Incoming, world.Warehouse, world.Fleet, Uid,
            copy ?? Copy);
    }
    private static string Outcome(Action action)
    {
        try { action(); return "accepted"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
    private static World Clone(World world)
    {
        var result = new World();
        var copied = new Dictionary<Ship, Ship>();
        Func<Ship, Ship> duplicate = original =>
        {
            if (original == null) return null;
            Ship clone;
            if (!copied.TryGetValue(original, out clone))
            {
                clone = original is CarrierShip
                    ? (Ship)new CarrierShip(original.Uid, original.Identity)
                    : new Ship(original.Uid, original.Identity);
                clone.Hull = original.Hull;
                clone.Name = original.Name;
                copied.Add(original, clone);
            }
            return clone;
        };
        foreach (Ship ship in world.Warehouse) result.Warehouse.Add(duplicate(ship));
        foreach (Ship ship in world.Fleet) result.Fleet.Add(duplicate(ship));
        foreach (Ship ship in world.Incoming) result.Incoming.Add(duplicate(ship));
        return result;
    }
    private static void CheckStates(World reference, World actual, string reason)
    {
        Check(reference.Fleet.Count == actual.Fleet.Count,
            reason + ": incoming fleet count");
        for (int i = 0; i < reference.Fleet.Count; i++)
        {
            Ship old = reference.Fleet[i], current = actual.Fleet[i];
            Check((old == null && current == null) ||
                (old != null && current != null &&
                old.Identity == current.Identity && old.Uid == current.Uid &&
                old.Hull == current.Hull && old.Name == current.Name &&
                old.GetType() == current.GetType()),
                reason + ": final canonical native instance and copied field #" + i);
        }
        Check(reference.Warehouse.Select(s => s == null ? null : s.Identity)
            .SequenceEqual(actual.Warehouse.Select(s => s == null ? null : s.Identity)),
            reason + ": canonical warehouse reference identities");
        for (int i = 0; i < reference.Warehouse.Count; i++)
        {
            Ship old = reference.Warehouse[i], current = actual.Warehouse[i];
            Check((old == null && current == null) ||
                (old != null && current != null && old.Uid == current.Uid &&
                old.Hull == current.Hull && old.Name == current.Name),
                reason + ": warehouse payload in-place copy #" + i);
        }
    }
    private static void Differential(World seed, string label)
    {
        World old = Clone(seed), modern = Clone(seed);
        string previous = Outcome(() => OldApply(old.Incoming, old.Warehouse, old.Fleet, Copy));
        string current = Outcome(() => NewApply(modern));
        Check(previous == current, label + ": exception class/message and acceptance, old="
            + previous + ", new=" + current + ", incoming=" + seed.Incoming.Count
            + ", warehouse=" + seed.Warehouse.Count + ", fleet=" + seed.Fleet.Count);
        CheckStates(old, modern, label);
    }
    private static List<Ship> Ships(int count, long start, string prefix)
    {
        var result = new List<Ship>();
        for (int i = 0; i < count; i++)
            result.Add(new Ship(start + i, prefix + i));
        return result;
    }
    private static long Reads(IEnumerable<Ship> ships)
    { return ships.Where(x => x != null).Sum(x => x.Reads); }
    private static void ResetReads(IEnumerable<Ship> ships)
    { foreach (Ship item in ships) if (item != null) item.Reads = 0; }

    private static int Main()
    {
        try
        {
            for (int count = 0; count <= 12; count++)
            {
                var state = new World {
                    Warehouse = Ships(20, 1000, "warehouse-"),
                    Fleet = Ships(20, 2000, "existing-fleet-")
                };
                for (int i = 0; i < count; i++)
                    state.Incoming.Add(new Ship(i % 2 == 0 ? 1000 + i : 2000 + i,
                        "network-" + i, 200 + i));
                Differential(state, "threshold " + count);
            }
            var newFleet = new World
            {
                Warehouse = Ships(32, 1000, "stored-"),
                Fleet = Ships(16, 2000, "fleet-"),
                Incoming = Ships(60, 9000, "new-")
            };
            Differential(newFleet, "all incoming ships are new");
            var malformedEmpty = new World {
                Warehouse = new List<Ship>(), Fleet = new List<Ship>(),
                Incoming = new List<Ship> { null, null, null, null,
                    null, null, null, null, null } };
            Differential(malformedEmpty,
                "null incoming entries with no sources retain the old LINQ empty-source behavior");
            malformedEmpty.Warehouse.Add(new Ship(123, "stored-existing"));
            Differential(malformedEmpty,
                "null incoming entries with populated native sources still reject as before");
            var overlap = new World {
                Warehouse = Ships(48, 4000, "warehouse-"),
                Fleet = Ships(24, 4000, "fleet-")
            };
            for (int i = 0; i < 70; i++)
                overlap.Incoming.Add(new Ship(i < 48 ? 4000 + i : 7000 + i,
                    "inbound-" + i, 900 + i));
            Differential(overlap, "warehouse asset wins when also in old fleet list");
            var ownFleet = new World {
                Warehouse = Ships(32, 1000, "warehouse-"),
                Fleet = Ships(48, 2000, "fleet-"),
                Incoming = Ships(48, 2000, "inbound-")
            };
            Differential(ownFleet, "existing fleet native ships are copied in place");
            var high = new World
            {
                Warehouse = new List<Ship> {
                    new Ship(long.MinValue, "stored-min"),
                    new Ship(1L << 32, "stored-high"),
                    new Ship(long.MaxValue, "stored-max") },
                Fleet = new List<Ship> { new Ship(-1, "fleet-neg") },
                Incoming = new List<Ship> {
                    new Ship(long.MinValue, "input-min"),
                    new Ship(1L << 32, "input-high"),
                    new Ship(long.MaxValue, "input-max"),
                    new Ship(-1, "input-neg"),
                    new Ship(0, "input-zero"),
                    new Ship(1, "input-one"),
                    new Ship(2, "input-two"),
                    new Ship(3, "input-three") }
            };
            Differential(high, "64-bit UID boundaries and brand-new UID 0");

            var duplicateWarehouse = new World {
                Warehouse = new List<Ship> {
                    new Ship(1, "warehouse-1"),
                    new Ship(2, "warehouse-2a"),
                    new Ship(2, "warehouse-2b"),
                    new Ship(3, "warehouse-3") },
                Fleet = Ships(8, 30, "fleet-"),
                Incoming = Ships(8, 30, "input-")
            };
            Differential(duplicateWarehouse,
                "unrelated warehouse duplicate must not reject fleet-only matches");
            duplicateWarehouse.Incoming[2].Uid = 2;
            Differential(duplicateWarehouse,
                "requested duplicate UID must keep SingleOrDefault rejection and prior copies");
            var duplicateFleet = new World {
                Warehouse = Ships(4, 90, "stored-"),
                Fleet = new List<Ship> { new Ship(5, "fleet-5a"),
                    new Ship(5, "fleet-5b"), new Ship(6, "fleet-6") },
                Incoming = Ships(8, 5, "incoming-")
            };
            Differential(duplicateFleet,
                "duplicate requested fleet UID rejects without partial list replacement");
            duplicateFleet.Incoming[0].Uid = 7;
            Differential(duplicateFleet,
                "unrelated duplicate in old fleet does not reject other UID");

            var malformed = new World {
                Warehouse = new List<Ship> { new Ship(1, "stored-1"), null,
                    new Ship(3, "stored-3") },
                Fleet = Ships(12, 40, "fleet-"),
                Incoming = Ships(8, 40, "incoming-")
            };
            Differential(malformed, "null warehouse object preserves native exception");
            malformed.Incoming.Insert(0, new Ship(1, "incoming-stored-1"));
            Differential(malformed,
                "first match before bad native element still follows Single semantics");
            var malformedFleet = new World {
                Warehouse = Ships(4, 1, "stored-"),
                Fleet = new List<Ship> { new Ship(40, "fleet-40"),
                    null, new Ship(41, "fleet-41") },
                Incoming = Ships(8, 40, "incoming-")
            };
            Differential(malformedFleet,
                "null native fleet member retains failure before final replacement");

            var repeatedIdentity = new World {
                Warehouse = Ships(12, 100, "stored-"),
                Fleet = Ships(12, 200, "fleet-"),
                Incoming = Ships(9, 100, "inbound-")
            };
            repeatedIdentity.Incoming[8].Uid = repeatedIdentity.Incoming[0].Uid;
            Differential(repeatedIdentity,
                "even an unvalidated repeated input UID retains old apply order");

            // Test partial write ordering on native Copy failure. Earlier
            // native bean copies are NOT rolled back, but the fleet array is
            // still only replaced after all requested copies succeed.
            var exceptional = new World {
                Warehouse = Ships(16, 10, "stored-"),
                Fleet = Ships(16, 100, "fleet-"),
                Incoming = Ships(12, 10, "incoming-")
            };
            foreach (Ship s in exceptional.Incoming) s.Hull = 900;
            var oldFault = Clone(exceptional);
            var newFault = Clone(exceptional);
            int oldCount = 0, newCount = 0;
            string oldError = Outcome(() => OldApply(oldFault.Incoming,
                oldFault.Warehouse, oldFault.Fleet, (source, target) => {
                    if (++oldCount == 5) throw new InvalidOperationException("Native copy fault");
                    Copy(source, target);
                }));
            string newError = Outcome(() => NewApply(newFault, (source, target) => {
                if (++newCount == 5) throw new InvalidOperationException("Native copy fault");
                Copy(source, target);
            }));
            Check(oldError == newError && newCount == oldCount && oldCount == 5,
                "native copy exception and first five writes occur at original point");
            CheckStates(oldFault, newFault,
                "exception preserves original fleet list and partial prior native copies");

            // Frame-local scope: another received baseline can contain a new
            // native instance with the same UID, never reuse old references.
            var firstGeneration = new World {
                Warehouse = Ships(10, 100, "first-"),
                Fleet = Ships(2, 200, "fleet-"),
                Incoming = Ships(10, 100, "input-")
            };
            NewApply(firstGeneration);
            Ship previouslyStored = firstGeneration.Fleet[0];
            firstGeneration.Warehouse[0] = new Ship(100, "replacement-native");
            firstGeneration.Incoming[0] = new Ship(100, "new-wire");
            NewApply(firstGeneration);
            Check(!ReferenceEquals(firstGeneration.Fleet[0], previouslyStored)
                && ReferenceEquals(firstGeneration.Fleet[0], firstGeneration.Warehouse[0]),
                "new incoming frame refreshes native object identity by rebuilding source lookup");

            var random = new Random(20261018);
            for (int run = 0; run < 2800; run++)
            {
                var source = new World();
                int ownerCount = random.Next(0, 48);
                int fleetCount = random.Next(0, 48);
                int incomingCount = random.Next(0, 52);
                for (int i = 0; i < ownerCount; i++)
                    source.Warehouse.Add(random.Next(0, 29) == 0 ? null :
                        new Ship(random.Next(-4, 80), "owned-" + run + "-" + i));
                for (int i = 0; i < fleetCount; i++)
                    source.Fleet.Add(random.Next(0, 29) == 0 ? null :
                        new Ship(random.Next(-4, 80), "fleet-" + run + "-" + i));
                for (int i = 0; i < incomingCount; i++)
                {
                    Ship ship = random.Next(0, 25) == 0 ? null :
                        new Ship(random.Next(-4, 80), "wire-" + run + "-" + i,
                            random.Next(1, 400));
                    source.Incoming.Add(ship);
                }
                Differential(source, "randomized native fleet apply " + run);
            }

            // Each native item is read across 384 incoming ships when
            // resolving warehouse and fleet originals in every content
            // frame. Indexing one time per apply cuts the repeated probes.
            const int storedCount = 256, fleetCountDense = 128,
                incomingDense = 384, frames = 90;
            var baseline = new World {
                Warehouse = Ships(storedCount, 10000, "stored-"),
                Fleet = Ships(fleetCountDense, 20000, "fleet-")
            };
            for (int i = 0; i < storedCount; i++)
                baseline.Incoming.Add(new Ship(10000 + i, "incoming-stored-" + i));
            for (int i = 0; i < fleetCountDense; i++)
                baseline.Incoming.Add(new Ship(20000 + i, "incoming-fleet-" + i));
            var oldDense = Clone(baseline);
            var newDense = Clone(baseline);
            List<Ship> originalOldFleet = new List<Ship>(oldDense.Fleet);
            List<Ship> originalNewFleet = new List<Ship>(newDense.Fleet);
            long legacyReads = 0, indexedReads = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                oldDense.Fleet.Clear(); oldDense.Fleet.AddRange(originalOldFleet);
                ResetReads(oldDense.Warehouse); ResetReads(originalOldFleet);
                OldApply(oldDense.Incoming, oldDense.Warehouse, oldDense.Fleet, Copy);
                legacyReads += Reads(oldDense.Warehouse) + Reads(originalOldFleet);

                newDense.Fleet.Clear(); newDense.Fleet.AddRange(originalNewFleet);
                ResetReads(newDense.Warehouse); ResetReads(originalNewFleet);
                NewApply(newDense);
                indexedReads += Reads(newDense.Warehouse) + Reads(originalNewFleet);
            }
            long expectedLegacy = (long)frames *
                (incomingDense * storedCount + fleetCountDense * fleetCountDense);
            long expectedIndexed = (long)frames * (storedCount + fleetCountDense);
            Check(legacyReads == expectedLegacy && indexedReads == expectedIndexed,
                "actual apply-phase native UID readings match quadratic vs indexed counts");
            Console.WriteLine("INFO: " + frames + " synthetic applies × " +
                incomingDense + " ships (256 warehouse + 128 existing fleet): old native UID reads " +
                legacyReads + ", production apply index reads " + indexedReads);

            Console.WriteLine("PASS: " + checks + " production-linked fleet ship apply checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
