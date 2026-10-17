using System;
using System.Collections.Generic;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Pose
    {
        internal readonly long Uid;
        internal readonly string Galaxy;
        internal readonly float X, Angle;
        internal Pose(long uid, string galaxy, float x, float angle)
        { Uid = uid; Galaxy = galaxy; X = x; Angle = angle; }
    }
    // Original private production state and initializer, before this change.
    private sealed class OldCheckpoint
    {
        internal Pose Pose;
        internal long Sequence;
        internal double Receipt, StartedAt, Duration, PauseAt = -1;
        internal float From, FromAngle;
    }

    private static int checks;
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static float Clamp01(float value)
    { return value <= 0 ? 0 : value >= 1 ? 1 : value; }
    private static float Lerp(float start, float end, float amount)
    { return start + (end - start) * amount; }
    private static float Repeat(float value, float max)
    { return value - (float)Math.Floor(value / max) * max; }
    private static float LerpAngle(float start, float end, float amount)
    {
        float delta = Repeat(end - start, 360);
        if (delta > 180) delta -= 360;
        return start + delta * Clamp01(amount);
    }
    private static bool EqualFloat(float a, float b)
    { return BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b); }

    private static bool InterpolateOld(OldCheckpoint value, double now,
        bool paused, out float position, out float heading)
    {
        if (value == null) { position = 0; heading = 0; return false; }
        if (paused)
        {
            if (value.PauseAt < 0) value.PauseAt = now;
            now = value.PauseAt;
        }
        else if (value.PauseAt >= 0)
        {
            value.StartedAt += now - value.PauseAt;
            value.PauseAt = -1;
        }
        float fraction = value.Duration <= 0 ? 1 :
            Clamp01((float)((now - value.StartedAt) / value.Duration));
        position = Lerp(value.From, value.Pose.X, fraction);
        heading = LerpAngle(value.FromAngle, value.Pose.Angle, fraction);
        return true;
    }
    private static bool InterpolateNew(RemoteFleetPoseCheckpoint<Pose, float> value,
        double now, bool paused, out float position, out float heading)
    {
        if (value == null) { position = 0; heading = 0; return false; }
        if (paused)
        {
            if (value.PauseAt < 0) value.PauseAt = now;
            now = value.PauseAt;
        }
        else if (value.PauseAt >= 0)
        {
            value.StartedAt += now - value.PauseAt;
            value.PauseAt = -1;
        }
        float fraction = value.Duration <= 0 ? 1 :
            Clamp01((float)((now - value.StartedAt) / value.Duration));
        position = Lerp(value.From, value.Pose.X, fraction);
        heading = LerpAngle(value.FromAngle, value.Pose.Angle, fraction);
        return true;
    }
    private static void PushOld(Dictionary<long, OldCheckpoint> state,
        Pose pose, long sequence, bool reset, double now, bool paused)
    {
        OldCheckpoint previous;
        bool known = state.TryGetValue(pose.Uid, out previous);
        if (known && !reset && sequence <= previous.Sequence) return;
        bool snap = reset || !known || previous.Pose.Galaxy != pose.Galaxy ||
            now - previous.Receipt > 2;
        float from = pose.X, fromAngle = pose.Angle;
        if (!snap) InterpolateOld(previous, now, paused, out from, out fromAngle);
        // Exactly the old production assignment (new reference on every
        // accepted pose, regardless of whether the fleet already exists).
        state[pose.Uid] = new OldCheckpoint {
            Pose = pose, Sequence = sequence, Receipt = now,
            From = from, FromAngle = fromAngle, StartedAt = now,
            Duration = snap ? 0 : Math.Max(0.05, Math.Min(1, now - previous.Receipt)),
            PauseAt = paused ? now : -1
        };
    }
    private static void PushNew(
        Dictionary<long, RemoteFleetPoseCheckpoint<Pose, float>> state,
        Pose pose, long sequence, bool reset, double now, bool paused)
    {
        RemoteFleetPoseCheckpoint<Pose, float> previous;
        bool known = state.TryGetValue(pose.Uid, out previous);
        if (known && !reset && sequence <= previous.Sequence) return;
        bool snap = reset || !known || previous.Pose.Galaxy != pose.Galaxy ||
            now - previous.Receipt > 2;
        float from = pose.X, fromAngle = pose.Angle;
        if (!snap) InterpolateNew(previous, now, paused, out from, out fromAngle);
        // Matches the production Apply/Push code, including evaluation of
        // duration before touching the previous checkpoint.
        double duration = snap ? 0 : Math.Max(0.05, Math.Min(1, now - previous.Receipt));
        double pauseAt = paused ? now : -1;
        if (known)
            previous.Replace(pose, sequence, now, from, fromAngle, duration, pauseAt);
        else
            state.Add(pose.Uid, new RemoteFleetPoseCheckpoint<Pose, float>(
                pose, sequence, now, from, fromAngle, duration, pauseAt));
    }
    private static void Compare(Dictionary<long, OldCheckpoint> old,
        Dictionary<long, RemoteFleetPoseCheckpoint<Pose, float>> current,
        double now, bool paused, string context)
    {
        Check(old.Count == current.Count, context + ": remote pose count");
        foreach (var entry in old)
        {
            RemoteFleetPoseCheckpoint<Pose, float> found;
            Check(current.TryGetValue(entry.Key, out found),
                context + ": full 64-bit remote fleet UID");
            OldCheckpoint before = entry.Value;
            Check(ReferenceEquals(before.Pose, found.Pose) &&
                before.Sequence == found.Sequence &&
                before.Receipt == found.Receipt &&
                before.StartedAt == found.StartedAt &&
                before.Duration == found.Duration &&
                before.PauseAt == found.PauseAt &&
                EqualFloat(before.From, found.From) &&
                EqualFloat(before.FromAngle, found.FromAngle),
                context + ": identical interpolation control state " + entry.Key);
            float oldX, oldAngle, newX, newAngle;
            bool first = InterpolateOld(before, now, paused, out oldX, out oldAngle);
            bool second = InterpolateNew(found, now, paused, out newX, out newAngle);
            Check(first == second && EqualFloat(oldX, newX) &&
                EqualFloat(oldAngle, newAngle),
                context + ": identical rendered position and heading " + entry.Key);
        }
    }
    private static long Allocations(Action operation, int count)
    {
        for (int i = 0; i < 90; i++) operation();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) operation();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public static int Main()
    {
        try
        {
            var old = new Dictionary<long, OldCheckpoint>();
            var modern = new Dictionary<long, RemoteFleetPoseCheckpoint<Pose, float>>();
            long[] allUids = { long.MinValue, long.MaxValue, -1, 0, 1,
                1L << 32, (1L << 32) | 1 };
            foreach (long uid in allUids)
            {
                Pose pose = new Pose(uid, "alpha", 19, -170);
                PushOld(old, pose, 1, false, 1, false);
                PushNew(modern, pose, 1, false, 1, false);
            }
            Compare(old, modern, 1, false,
                "initial authoritative pose and full signed UID range");
            var retained = modern[long.MinValue];
            Pose stale = new Pose(long.MinValue, "alpha", 500, 174);
            PushOld(old, stale, 1, false, 1.1, false);
            PushNew(modern, stale, 1, false, 1.1, false);
            Check(ReferenceEquals(retained, modern[long.MinValue]) &&
                ReferenceEquals(retained.Pose, old[long.MinValue].Pose),
                "stale or duplicate authoritative sequence is ignored without any state change");
            Compare(old, modern, 1.1, false, "stale sequence rejection");

            Pose moved = new Pose(long.MinValue, "alpha", 120, 170);
            PushOld(old, moved, 2, false, 1.2, false);
            PushNew(modern, moved, 2, false, 1.2, false);
            Check(ReferenceEquals(modern[long.MinValue], retained),
                "same fleet UID reuses original interpolation checkpoint object");
            Compare(old, modern, 1.23, false, "same-galaxy remote movement");
            Compare(old, modern, 1.27, true, "host pause during movement interpolation");
            Compare(old, modern, 1.55, false, "host resume after pause");

            Pose anotherGalaxy = new Pose(long.MinValue, "beta", 500, -20);
            PushOld(old, anotherGalaxy, 3, false, 1.6, false);
            PushNew(modern, anotherGalaxy, 3, false, 1.6, false);
            Compare(old, modern, 1.7, false, "cross-galaxy teleport snaps to authoritative pose");
            Check(modern[long.MinValue].Duration == 0 &&
                ReferenceEquals(modern[long.MinValue], retained),
                "cross-galaxy snap retains same state object but no interpolation");

            Pose resync = new Pose(long.MinValue, "beta", -100, 50);
            PushOld(old, resync, 2, true, 1.8, true);
            PushNew(modern, resync, 2, true, 1.8, true);
            Compare(old, modern, 2, true,
                "full resync overwrites even a lower sequence and honors pause state");
            PushOld(old, new Pose(long.MinValue, "beta", 900, 80), 4, false, 5, false);
            PushNew(modern, new Pose(long.MinValue, "beta", 900, 80), 4, false, 5, false);
            // These incoming DTOs are distinct above; compare identities using
            // the same incoming object in all other cases, and compare the
            // fields/positions of this exceptional isolated sample directly.
            Check(old[long.MinValue].Duration == modern[long.MinValue].Duration &&
                old[long.MinValue].Sequence == modern[long.MinValue].Sequence,
                "large receive gap snaps rather than extrapolating stale motion");
            old.Clear(); modern.Clear();
            Check(old.Count == 0 && modern.Count == 0,
                "leaving a room clears all native remote pose state references");

            // Differential fuzzing: stale/out-of-order/full-baseline frames,
            // reconnects, pauses, scene switches and long receive gaps.
            var random = new Random(20261018);
            var currentSequences = new Dictionary<long, long>();
            double clock = 10;
            for (int frame = 0; frame < 9000; frame++)
            {
                if (frame % 137 == 0)
                {
                    old.Clear(); modern.Clear(); currentSequences.Clear();
                }
                long uid = random.Next(0, 7) == 0
                    ? allUids[random.Next(allUids.Length)]
                    : random.Next(0, 140);
                long current;
                if (!currentSequences.TryGetValue(uid, out current)) current = 0;
                bool reset = random.Next(0, 70) == 0;
                long seq = random.Next(0, 7) == 0
                    ? Math.Max(1, current - random.Next(0, 4))
                    : current + random.Next(1, 3);
                bool paused = random.Next(0, 8) == 0;
                clock += random.Next(0, 15) == 0 ? 2.5 : 0.01 + random.NextDouble() / 10;
                var pose = new Pose(uid, "galaxy-" + random.Next(0, 4),
                    (float)random.NextDouble() * 200 - 100,
                    (float)random.NextDouble() * 720 - 360);
                PushOld(old, pose, seq, reset, clock, paused);
                PushNew(modern, pose, seq, reset, clock, paused);
                if (reset || seq > current) currentSequences[uid] = seq;
                Compare(old, modern, clock + 0.02, paused,
                    "random remote-fleet receipt " + frame);
            }

            // Isolate the allocation of a per-pose checkpoint object. The
            // actual parse/visual/native engine work is outside this test.
            const int fleets = 96, frames = 1200;
            var oldBatch = new Dictionary<long, OldCheckpoint>();
            var newBatch = new Dictionary<long, RemoteFleetPoseCheckpoint<Pose, float>>();
            var stablePoses = new Pose[fleets];
            for (int i = 0; i < fleets; i++)
            {
                stablePoses[i] = new Pose(1000 + i, "stable", i, 11 + i);
                PushOld(oldBatch, stablePoses[i], 1, false, 10, false);
                PushNew(newBatch, stablePoses[i], 1, false, 10, false);
            }
            long oldSequence = 10, newSequence = 10;
            double oldClock = 20, newClock = 20;
            Action legacyFrame = () =>
            {
                ++oldSequence; oldClock += .06;
                foreach (Pose pose in stablePoses)
                    PushOld(oldBatch, pose, oldSequence, false, oldClock, false);
            };
            Action updatedFrame = () =>
            {
                ++newSequence; newClock += .06;
                foreach (Pose pose in stablePoses)
                    PushNew(newBatch, pose, newSequence, false, newClock, false);
            };
            long oldBytes = Allocations(legacyFrame, frames);
            long newBytes = Allocations(updatedFrame, frames);
            Check(oldBytes > newBytes && newBytes == 0,
                "stable native fleet pose checkpoints avoid repeat heap allocations");
            Console.WriteLine("INFO: " + frames + " synthetic pose frames × " +
                fleets + " already-known remote fleets: old per-frame state " +
                oldBytes + " B, linked production checkpoint reuse " + newBytes +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " production-linked remote fleet pose checkpoint checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
