using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Stage
    {
        internal string Player;
        internal long Uid;
        internal bool Created;
        internal Stage(string player, long uid, bool created)
        { Player = player; Uid = uid; Created = created; }
    }
    private sealed class Pose
    {
        internal string Player;
        internal long Uid;
        internal float X;
        internal long WorldTime;
        internal Pose(string player, long uid)
        { Player = player; Uid = uid; WorldTime = 123; }
    }
    private sealed class Result
    {
        internal string Outcome;
        internal readonly List<Pose> Applied = new List<Pose>();
    }
    private static int checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static bool LegacyCreated(List<Stage> stages, string player, long uid)
    { return stages.Any(s => s.Created && s.Uid == uid && s.Player == player); }
    private static bool LegacyHasPose(List<Pose> poses, long uid)
    { return poses.Any(p => p.Uid == uid); }
    private static Pose LegacySinglePose(List<Pose> poses, long uid)
    { return poses.Single(p => p.Uid == uid); }

    // Models the three actual integration points, keeping the existing
    // prewrite validation guards and their failure ordering. The pose and
    // stage types are simplified private DTOs, the index is production source.
    private static Result Evaluate(List<Stage> stages, List<Pose> poses,
        HashSet<long> existingUids, bool indexed, bool full)
    {
        var result = new Result();
        try
        {
            int createdCount = stages.Count(s => s.Created);
            var lookup = indexed && FleetCreatedPoseIndex<Pose>.ShouldIndex(createdCount)
                ? new FleetCreatedPoseIndex<Pose>() : null;
            if (lookup != null)
                foreach (Stage stage in stages)
                    if (stage.Created) lookup.RegisterCreated(stage.Player, stage.Uid);

            var seen = new HashSet<long>();
            foreach (Pose pose in poses)
            {
                // Original pre-application owner/duplicate/position checks.
                if (pose == null || String.IsNullOrEmpty(pose.Player)
                    || !seen.Add(pose.Uid)
                    || float.IsNaN(pose.X) || float.IsInfinity(pose.X)
                    || pose.WorldTime < 0
                    || !existingUids.Contains(pose.Uid) &&
                       !(lookup == null
                         ? LegacyCreated(stages, pose.Player, pose.Uid)
                         : lookup.HasCreated(pose.Player, pose.Uid)))
                    throw new InvalidOperationException("Invalid authoritative fleet pose.");
            }

            if (lookup != null)
                foreach (Pose pose in poses) lookup.RegisterValidatedPose(pose.Uid, pose);
            foreach (Stage stage in stages)
                if (stage.Created && !(lookup == null
                    ? LegacyHasPose(poses, stage.Uid)
                    : lookup.HasPose(stage.Uid)))
                    throw new InvalidOperationException("New fleet replica has no physical galaxy pose.");
            if (full && stages.Any(stage => !seen.Contains(stage.Uid)))
                throw new InvalidOperationException("A complete galaxy baseline is missing a fleet pose.");
            // Mirrors native creation after all prewrite checks.
            foreach (Stage stage in stages)
                if (stage.Created)
                    result.Applied.Add(lookup == null
                        ? LegacySinglePose(poses, stage.Uid)
                        : lookup.SinglePose(stage.Uid));
            result.Outcome = "accepted";
        }
        catch (Exception error)
        { result.Outcome = error.GetType().Name + ": " + error.Message; }
        return result;
    }
    private static void Compare(List<Stage> stages, List<Pose> poses,
        HashSet<long> existing, bool full, string label)
    {
        var original = Evaluate(stages, poses, existing, false, full);
        var optimized = Evaluate(stages, poses, existing, true, full);
        Check(original.Outcome == optimized.Outcome,
            label + " old=" + original.Outcome + " new=" + optimized.Outcome);
        Check(original.Applied.Count == optimized.Applied.Count,
            label + " native creations preserve count");
        for (int i = 0; i < original.Applied.Count; i++)
            Check(ReferenceEquals(original.Applied[i], optimized.Applied[i]),
                label + " native creation pose object #" + i);
    }
    private static void SyntheticDense()
    {
        const int fleets = 512;
        var stages = new List<Stage>(fleets);
        var poses = new List<Pose>(fleets);
        var index = new FleetCreatedPoseIndex<Pose>();
        for (int i = 0; i < fleets; i++)
        {
            string player = i % 2 == 0 ? "host" : "guest";
            stages.Add(new Stage(player, 10000 + i, true));
            poses.Add(new Pose(player, 10000 + i));
        }
        int legacyStageComparisons = 0, legacyPoseAnyComparisons = 0;
        int legacyPoseSingleComparisons = 0;
        foreach (Pose pose in poses)
        {
            bool found = false;
            foreach (Stage stage in stages)
            {
                legacyStageComparisons++;
                if (stage.Created && stage.Uid == pose.Uid && stage.Player == pose.Player)
                { found = true; break; }
            }
            if (!found) throw new InvalidOperationException("Old stage membership missing");
        }
        foreach (Stage stage in stages)
        {
            bool found = false;
            foreach (Pose pose in poses)
            {
                legacyPoseAnyComparisons++;
                if (pose.Uid == stage.Uid) { found = true; break; }
            }
            if (!found) throw new InvalidOperationException("Old pose membership missing");
            Pose selected = null;
            foreach (Pose pose in poses)
            {
                legacyPoseSingleComparisons++;
                if (pose.Uid == stage.Uid)
                {
                    if (selected != null) throw new InvalidOperationException("Unexpected duplicate pose");
                    selected = pose;
                }
            }
            if (selected == null) throw new InvalidOperationException("Old pose lookup missing");
        }
        foreach (Stage stage in stages) index.RegisterCreated(stage.Player, stage.Uid);
        foreach (Pose pose in poses) index.RegisterValidatedPose(pose.Uid, pose);
        foreach (Pose pose in poses)
            Check(index.HasCreated(pose.Player, pose.Uid),
                "dense frame maps new fleet ownership " + pose.Uid);
        foreach (Stage stage in stages)
        {
            Check(index.HasPose(stage.Uid),
                "dense frame has validated native pose " + stage.Uid);
            Check(ReferenceEquals(index.SinglePose(stage.Uid), poses[(int)(stage.Uid - 10000)]),
                "dense frame returns the exact validated pose identity " + stage.Uid);
        }
        int legacyTotal = legacyStageComparisons + legacyPoseAnyComparisons + legacyPoseSingleComparisons;
        Check(legacyStageComparisons == fleets * (fleets + 1) / 2 &&
            legacyPoseAnyComparisons == fleets * (fleets + 1) / 2 &&
            legacyPoseSingleComparisons == fleets * fleets,
            "dense legacy loop comparison accounting matches three actual scan phases");
        Console.WriteLine("INFO: 512 freshly created fleets + 512 authoritative poses: " +
            "legacy repeated scans " + legacyTotal + " element comparisons (" +
            legacyStageComparisons + " ownership, " + legacyPoseAnyComparisons +
            " pose membership, " + legacyPoseSingleComparisons +
            " pose Single); indexed path 512 created UID registrations + 512 validated pose registrations + 1536 lookups");
    }
    private static int Main()
    {
        try
        {
            Check(!FleetCreatedPoseIndex<Pose>.ShouldIndex(0) &&
                !FleetCreatedPoseIndex<Pose>.ShouldIndex(7) &&
                FleetCreatedPoseIndex<Pose>.ShouldIndex(8) &&
                FleetCreatedPoseIndex<Pose>.ShouldIndex(512),
                "small creation deltas retain original LINQ path");

            var index = new FleetCreatedPoseIndex<Pose>();
            index.RegisterCreated("Owner", long.MinValue);
            index.RegisterCreated("owner", long.MaxValue);
            index.RegisterCreated("Owner", 1L << 32);
            Check(index.HasCreated("Owner", long.MinValue) &&
                !index.HasCreated("owner", long.MinValue) &&
                index.HasCreated("owner", long.MaxValue) &&
                !index.HasCreated("Owner", long.MaxValue) &&
                index.HasCreated("Owner", 1L << 32),
                "new fleet identity preserves exact player case and full signed 64-bit UID");
            var pose = new Pose("Owner", long.MinValue);
            index.RegisterValidatedPose(pose.Uid, pose);
            Check(index.HasPose(long.MinValue) &&
                ReferenceEquals(index.SinglePose(long.MinValue), pose) &&
                !index.HasPose(long.MaxValue),
                "validated pose lookup preserves native wire instance and UID");
            Check(Throws<InvalidOperationException>(() => index.SinglePose(42)),
                "missing required validated pose still rejects explicitly");

            for (int size = 0; size <= 16; size++)
            {
                var stages = new List<Stage>();
                var poses = new List<Pose>();
                for (int i = 0; i < size; i++)
                {
                    stages.Add(new Stage(i % 2 == 0 ? "host" : "guest", 100 + i, true));
                    poses.Add(new Pose(stages[i].Player, stages[i].Uid));
                }
                Compare(stages, poses, new HashSet<long>(), true,
                    "creation threshold complete frame " + size);
                if (size == 0) continue;
                poses.RemoveAt(size - 1);
                Compare(stages, poses, new HashSet<long>(), true,
                    "creation threshold last pose missing " + size);
                Compare(stages, poses, new HashSet<long>(), false,
                    "creation threshold partial frame missing pose " + size);
            }

            // A different player's pose cannot satisfy a just-created fleet.
            var eightStages = new List<Stage>();
            var eightPoses = new List<Pose>();
            for (int i = 0; i < 8; i++)
            {
                eightStages.Add(new Stage("player-A", 9000 + i, true));
                eightPoses.Add(new Pose("player-A", 9000 + i));
            }
            eightPoses[3].Player = "player-B";
            Compare(eightStages, eightPoses, new HashSet<long>(), true,
                "wrong player does not claim another owner's new fleet UID");
            eightPoses[3].Player = "player-A";
            eightPoses[3].X = float.NaN;
            Compare(eightStages, eightPoses, new HashSet<long>(), true,
                "invalid numeric pose still fails before indexing validated poses");
            eightPoses[3].X = 0;
            eightPoses[3].WorldTime = -1;
            Compare(eightStages, eightPoses, new HashSet<long>(), true,
                "negative time pose retains original rejection order");
            eightPoses[3].WorldTime = 123;
            eightPoses[3] = eightPoses[2];
            Compare(eightStages, eightPoses, new HashSet<long>(), true,
                "duplicate pose UID fails before staged native creation");
            eightPoses[3] = null;
            Compare(eightStages, eightPoses, new HashSet<long>(), true,
                "null pose still fails during validation before any index build");

            // A full frame can combine old fleets and new fleets: only
            // newly-created fleet IDs require the special staged lookup.
            var mixedStages = new List<Stage>();
            var mixedPoses = new List<Pose>();
            var alreadyKnown = new HashSet<long>();
            for (int i = 0; i < 60; i++)
            {
                bool created = i % 3 != 0;
                mixedStages.Add(new Stage("p" + (i % 4), 5000 + i, created));
                mixedPoses.Add(new Pose(mixedStages[i].Player, 5000 + i));
                if (!created) alreadyKnown.Add(5000 + i);
            }
            Compare(mixedStages, mixedPoses, alreadyKnown, true,
                "full baseline with real existing and newly created fleets");
            Compare(mixedStages, mixedPoses, alreadyKnown, false,
                "delta checkpoint with a mixed fleet population");
            mixedPoses.RemoveAt(31);
            Compare(mixedStages, mixedPoses, alreadyKnown, true,
                "missing newly created fleet pose is rejected in a full baseline");

            var random = new Random(20261018);
            for (int iteration = 0; iteration < 3500; iteration++)
            {
                int n = random.Next(0, 110);
                var stages = new List<Stage>();
                var poses = new List<Pose>();
                var existing = new HashSet<long>();
                for (int i = 0; i < n; i++)
                {
                    long uid = random.Next(0, 8) == 0
                        ? long.MaxValue - iteration * 128 - i
                        : iteration * 200L + i;
                    string player = "player-" + random.Next(0, 7);
                    bool created = random.Next(0, 5) != 0;
                    stages.Add(new Stage(player, uid, created));
                    if (!created) existing.Add(uid);
                    if (random.Next(0, 8) != 0)
                    {
                        var generated = new Pose(player, uid);
                        if (random.Next(0, 30) == 0) generated.Player = "wrong-player";
                        if (random.Next(0, 45) == 0) generated.X = float.PositiveInfinity;
                        poses.Add(generated);
                    }
                }
                if (random.Next(0, 18) == 0 && poses.Count > 0)
                    poses.Add(new Pose(poses[0].Player, poses[0].Uid));
                if (random.Next(0, 30) == 0)
                    poses.Add(new Pose("unregistered", long.MinValue + iteration));
                Compare(stages, poses, existing, random.Next(2) == 0,
                    "random native creation/pose frame " + iteration);
            }

            SyntheticDense();
            var abandoned = new FleetCreatedPoseIndex<Pose>();
            abandoned.RegisterCreated("host", 77);
            abandoned.RegisterValidatedPose(77, new Pose("host", 77));
            var nextFrame = new FleetCreatedPoseIndex<Pose>();
            Check(!nextFrame.HasCreated("host", 77) && !nextFrame.HasPose(77),
                "reconnect or world reset cannot retain the previous frame's staged fleet or pose");
            Console.WriteLine("PASS: " + checks + " production linked fleet creation/pose checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
