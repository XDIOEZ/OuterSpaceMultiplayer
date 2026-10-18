using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int assertions;
    private const string Session = "room-20261018";
    private const string Simulator = "simulator";
    private const string Visitor = "visitor";
    private const string Owner = "base-owner";
    private const string Host = "host";

    private static void Check(bool good, string message)
    {
        if (!good) throw new InvalidOperationException("FAILED: " + message);
        assertions++;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private sealed class Room
    {
        internal readonly Dictionary<string, PlayerRecord> Players =
            new Dictionary<string, PlayerRecord>(StringComparer.Ordinal);
        internal readonly Dictionary<long, string> Canonical =
            new Dictionary<long, string>();
        internal readonly Dictionary<long, string> Checkpoint =
            new Dictionary<long, string>();
        internal readonly Dictionary<long, string> Remote =
            new Dictionary<long, string>();
        internal int NativeApplications;
        internal int CanonicalWrites;

        internal Room()
        {
            foreach (string id in new[] { Host, Simulator, Visitor, Owner })
                Players.Add(id, new PlayerRecord { PlayerId = id });
            Players[Simulator].IssuedUidRanges.Add(new UidRange {
                First = 1000001, Last = 2000000,
                Next = 1000001, SessionId = Session
            });
            Players[Visitor].IssuedUidRanges.Add(new UidRange {
                First = 2000001, Last = 3000000,
                Next = 2000001, SessionId = Session
            });
            Players[Host].IssuedUidRanges.Add(new UidRange {
                First = 1, Last = 1000000,
                Next = 1, SessionId = Session
            });
        }

        internal void Validate()
        {
            MapEntityUidValidation.RequireRemoteProvenance(
                Remote, Canonical, Checkpoint, Players, Simulator, Session);
        }

        internal void HostAccept()
        {
            // Synthetic application checkpoint: exactly the real host order
            // of preflight-before-native-mutation, without fake native success.
            Validate();
            NativeApplications++;
            foreach (var entry in Remote)
            {
                Canonical[entry.Key] = entry.Value;
                CanonicalWrites++;
            }
        }
    }

    private static void ExpectRejectedBeforeNative(Room room, string label)
    {
        int oldApply = room.NativeApplications;
        int oldWrites = room.CanonicalWrites;
        var previous = new Dictionary<long, string>(room.Canonical);
        Check(Throws<InvalidOperationException>(room.HostAccept),
            label + ": unauthorised remote delta is rejected");
        Check(room.NativeApplications == oldApply && room.CanonicalWrites == oldWrites,
            label + ": rejected claim does not change map nor durable owner index");
        Check(room.Canonical.Count == previous.Count,
            label + ": known owner registry size was unchanged");
        foreach (var entry in previous)
            Check(room.Canonical[entry.Key] == entry.Value,
                label + ": known owner identity was unchanged");
    }

    private static void Cases()
    {
        var empty = new Room();
        empty.HostAccept();
        Check(empty.NativeApplications == 1 && empty.Canonical.Count == 0,
            "no-ownership map metadata update remains valid");
        Check(Throws<InvalidDataException>(() =>
            MapEntityUidValidation.RequireRemoteProvenance(
                null, empty.Canonical, empty.Checkpoint, empty.Players, Simulator, Session)),
            "null remote ownership registry fails before any native change");
        Check(Throws<InvalidDataException>(() =>
            MapEntityUidValidation.RequireRemoteProvenance(
                empty.Remote, null, empty.Checkpoint, empty.Players, Simulator, Session)),
            "null host canonical ownership registry fails closed");
        Check(Throws<InvalidOperationException>(() =>
            MapEntityUidValidation.RequireRemoteProvenance(
                empty.Remote, empty.Canonical, empty.Checkpoint, empty.Players, "ghost", Session)),
            "unregistered map simulator cannot submit ownership");
        Check(Throws<InvalidOperationException>(() =>
            MapEntityUidValidation.RequireRemoteProvenance(
                empty.Remote, empty.Canonical, empty.Checkpoint, empty.Players, Simulator, null)),
            "missing host session cannot establish a UID source");

        var normal = new Room();
        normal.Remote.Add(1000001, Simulator);
        normal.Remote.Add(1000002, Owner); // Visiting simulator builds for base owner.
        normal.Remote.Add(1999999, Visitor); // Simulator creates visitor-owned entity.
        normal.Remote.Add(2000000, Simulator);
        normal.HostAccept();
        Check(normal.NativeApplications == 1 && normal.Canonical.Count == 4
            && normal.Canonical[1000002] == Owner,
            "new authorised remote UIDs may belong to base or visitor, not just simulator");

        // The host's issued range has a consumed Next cursor that is LOCAL to
        // the guest. The host cannot infer which of those numbers were used.
        var high = new Room();
        high.Players[Simulator].IssuedUidRanges[0].Next = 1000001;
        high.Remote.Add(1999999, Simulator);
        high.HostAccept();
        Check(high.Canonical[1999999] == Simulator,
            "authoritative range bounds not the guest's unreplicated Next are verified");

        var separate = new Room();
        separate.Remote.Add(2000001, Visitor);
        ExpectRejectedBeforeNative(separate,
            "foreign visitor UID range cannot be fabricated by map simulator");

        var stolen = new Room();
        stolen.Remote.Add(15, Simulator);
        ExpectRejectedBeforeNative(stolen,
            "simulator cannot claim new host-issued UID without prior host registration");

        var zero = new Room();
        zero.Remote.Add(0, Simulator);
        ExpectRejectedBeforeNative(zero,
            "unknown UID zero is not in the host-issued nonzero simulator block");

        var forged = new Room();
        forged.Remote.Add(999999999, Simulator);
        ExpectRejectedBeforeNative(forged, "UID outside every host-issued block");

        var negative = new Room();
        negative.Remote.Add(-1, Simulator);
        ExpectRejectedBeforeNative(negative, "negative UID sentinel");

        var absent = new Room();
        absent.Remote.Add(1000010, "not-in-room");
        ExpectRejectedBeforeNative(absent, "unknown claimed owner");

        var nullOwner = new Room();
        nullOwner.Remote.Add(1000010, null);
        ExpectRejectedBeforeNative(nullOwner, "null owner");

        var changedOwner = new Room();
        changedOwner.Canonical.Add(88, Visitor);
        changedOwner.Remote.Add(88, Owner);
        ExpectRejectedBeforeNative(changedOwner, "canonical owner cannot be transferred by map delta");

        var checkpointHijack = new Room();
        checkpointHijack.Checkpoint.Add(99999999, Visitor);
        checkpointHijack.Remote.Add(99999999, Owner);
        ExpectRejectedBeforeNative(checkpointHijack,
            "old complete-map checkpoint identity cannot be retagged");

        var contradictory = new Room();
        contradictory.Canonical.Add(99, Visitor);
        contradictory.Checkpoint.Add(99, Owner);
        contradictory.Remote.Add(99, Visitor);
        ExpectRejectedBeforeNative(contradictory,
            "conflicting host-known/checkpoint owner is never blindly accepted");

        var noCurrentSession = new Room();
        noCurrentSession.Players[Simulator].IssuedUidRanges.Clear();
        noCurrentSession.Players[Simulator].IssuedUidRanges.Add(new UidRange {
            First = 1000001, Last = 2000000, SessionId = "old-room"
        });
        noCurrentSession.Remote.Add(1000001, Simulator);
        ExpectRejectedBeforeNative(noCurrentSession,
            "same numeric UID but old room session is not a valid new allocation");

        var multiple = new Room();
        multiple.Players[Simulator].IssuedUidRanges.Add(new UidRange {
            First = 4000001, Last = 5000000, SessionId = Session
        });
        multiple.Remote.Add(4000001, Simulator);
        multiple.Remote.Add(5000000, Owner);
        multiple.HostAccept();
        Check(multiple.Canonical.Count == 2,
            "new UID from any older current-session simulator block is valid");

        var oldHost = new Room();
        oldHost.Canonical.Add(25, Visitor);
        oldHost.Remote.Add(25, Visitor);
        oldHost.HostAccept();
        Check(oldHost.Canonical[25] == Visitor,
            "host-issued known transferred actor remains valid without simulator provenance");

        var oldCheckpoint = new Room();
        oldCheckpoint.Checkpoint.Add(25, Visitor);
        oldCheckpoint.Remote.Add(25, Visitor);
        oldCheckpoint.HostAccept();
        Check(oldCheckpoint.Canonical[25] == Visitor,
            "old checkpoint's known visitor is not reattributed to the current simulator");

        var alreadyRegisteredVisitor = new Room();
        alreadyRegisteredVisitor.Canonical.Add(2000001, Visitor);
        alreadyRegisteredVisitor.Remote.Add(2000001, Visitor);
        alreadyRegisteredVisitor.HostAccept();
        Check(alreadyRegisteredVisitor.Canonical[2000001] == Visitor,
            "foreign UID range is legal if the host previously authenticated that actor");

        var malformedRanges = new Room();
        malformedRanges.Players[Simulator].IssuedUidRanges.Clear();
        malformedRanges.Players[Simulator].IssuedUidRanges.Add(null);
        malformedRanges.Players[Simulator].IssuedUidRanges.Add(new UidRange {
            First = 2000000, Last = 1000001, SessionId = Session
        });
        malformedRanges.Players[Simulator].IssuedUidRanges.Add(new UidRange {
            First = -100, Last = 1000002, SessionId = Session
        });
        malformedRanges.Remote.Add(1000001, Simulator);
        ExpectRejectedBeforeNative(malformedRanges,
            "broken grants cannot authorise new entities");

        var longBoundaries = new Room();
        long max = long.MaxValue;
        longBoundaries.Players[Simulator].IssuedUidRanges.Add(new UidRange {
            First = max - 1, Last = max, SessionId = Session
        });
        longBoundaries.Remote.Add(max, Owner);
        longBoundaries.HostAccept();
        Check(longBoundaries.Canonical[max] == Owner,
            "long.MaxValue end boundary accepted without arithmetic overflow");
    }

    private static void PreflightAndIsolation()
    {
        var room = new Room();
        for (int i = 0; i < 2500; i++)
        {
            long uid = 1000001 + i;
            room.Canonical.Add(uid, i % 2 == 0 ? Owner : Simulator);
            room.Remote.Add(uid, room.Canonical[uid]);
        }
        room.Remote.Add(1005000, Simulator);
        room.HostAccept();
        Check(room.NativeApplications == 1,
            "large valid map registry is checked as a batch before native application");

        // Put an invalid foreign UID AFTER thousands of valid entries;
        // preflight must still leave the native transaction entirely unapplied.
        var batch = new Room();
        for (int i = 0; i < 4000; i++)
        {
            long uid = 1000001 + i;
            batch.Remote.Add(uid, Simulator);
        }
        batch.Remote.Add(2999999, Visitor);
        ExpectRejectedBeforeNative(batch,
            "late invalid owner after 4,000 valid entries is rejected atomically");

        var miss = new Room();
        miss.Players[Simulator].IssuedUidRanges = null;
        miss.Remote.Add(1000042, Simulator);
        ExpectRejectedBeforeNative(miss,
            "older malformed save without a range must fail closed on unknown new UID");
        miss.Remote.Clear();
        miss.Canonical[22] = Owner;
        miss.Remote[22] = Owner;
        miss.HostAccept();
        Check(miss.Canonical[22] == Owner,
            "older player with missing grant list can still maintain already registered actor");
    }

    private static void RandomPreflight()
    {
        var random = new Random(20261018);
        for (int trial = 0; trial < 14000; trial++)
        {
            var room = new Room();
            int count = random.Next(1, 14);
            bool invalid = false;
            for (int index = 0; index < count; index++)
            {
                long uid = trial * 100 + index;
                int kind = random.Next(6);
                switch (kind)
                {
                    case 0:
                        // Preexisting remote actor with old arbitrary UID.
                        room.Canonical[uid] = Owner;
                        room.Remote[uid] = Owner;
                        break;
                    case 1:
                        // Preexisting checkpoint actor, not in the room index.
                        room.Checkpoint[uid] = Visitor;
                        room.Remote[uid] = Visitor;
                        break;
                    case 2:
                        // New valid simulator-created UID.
                        room.Remote[1000001 + (uid % 1000000)] = Owner;
                        break;
                    case 3:
                        // Foreign issued UID cannot be forged by simulator.
                        room.Remote[2000001 + (uid % 1000000)] = Visitor;
                        invalid = true;
                        break;
                    case 4:
                        // Invented UID outside any issued range.
                        room.Remote[90000000 + uid] = Simulator;
                        invalid = true;
                        break;
                    default:
                        // Mutate the owner of a known UID.
                        room.Canonical[uid] = Visitor;
                        room.Remote[uid] = Owner;
                        invalid = true;
                        break;
                }
            }
            if (invalid)
            {
                Check(Throws<InvalidOperationException>(room.HostAccept),
                    "random invalid batch cannot pass remote map entity preflight " + trial);
                Check(room.NativeApplications == 0 && room.CanonicalWrites == 0,
                    "random invalid batch cannot mutate native or ownership ledger " + trial);
            }
            else
            {
                room.HostAccept();
                Check(room.NativeApplications == 1,
                    "random legal batch can reach real native commit boundary " + trial);
            }
        }
    }

    private static int Main()
    {
        try
        {
            Cases();
            PreflightAndIsolation();
            RandomPreflight();
            Console.WriteLine("PASS: " + assertions +
                " production-linked map entity UID provenance checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
