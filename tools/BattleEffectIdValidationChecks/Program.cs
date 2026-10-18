using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Pose
    {
        internal long Id;
        internal string Resource = "projectile";
        internal int Team;
        internal float X;
        internal Pose(long id) { Id = id; }
    }
    private sealed class Event
    {
        internal long Serial;
        internal int Kind;
        internal Pose Pose;
        internal Event(long serial, int kind, Pose pose)
        { Serial = serial; Kind = kind; Pose = pose; }
    }
    private sealed class Frame
    {
        internal int Schema = 1;
        internal long Sequence = 1;
        internal List<Pose> Projectiles = new List<Pose>();
        internal List<Pose> Vfx = new List<Pose>();
        internal List<Event> Events = new List<Event>();
        internal List<int> Weapons = new List<int>();
    }

    private static int checks;
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static void ValidatePose(Pose pose)
    {
        // Source-equivalent guard used before duplicate ID registration.
        if (pose == null || pose.Id <= 0 || String.IsNullOrEmpty(pose.Resource)
            || pose.Resource.Length > 128 || pose.Team < 0 || pose.Team > 2
            || float.IsNaN(pose.X) || float.IsInfinity(pose.X))
            throw new InvalidOperationException("玩家舰战表现身份、资源或坐标无效。");
    }
    private static void CheckFrameShape(Frame frame)
    {
        if (frame == null || frame.Schema != 1 || frame.Sequence <= 0 ||
            frame.Projectiles == null || frame.Vfx == null || frame.Events == null ||
            frame.Weapons == null || frame.Projectiles.Count > 16000 ||
            frame.Vfx.Count > 16000 || frame.Events.Count > 32000 ||
            frame.Weapons.Count > 8192)
            throw new InvalidOperationException("玩家舰战表现快照结构或数量无效。");
    }
    private static void ValidateContents(Frame frame, HashSet<long> ids)
    {
        // Exact order of the production ValidateEffectLimits body after the
        // structural guard. Only native field/beam details are stubbed.
        foreach (Pose pose in frame.Projectiles)
        {
            ValidatePose(pose);
            if (!ids.Add(pose.Id))
                throw new InvalidOperationException("玩家舰战表现实例编号重复。");
        }
        foreach (Pose pose in frame.Vfx)
        {
            ValidatePose(pose);
            if (!ids.Add(pose.Id))
                throw new InvalidOperationException("玩家舰战表现实例编号重复。");
        }
        long previous = 0;
        foreach (Event item in frame.Events)
        {
            if (item == null || item.Serial <= previous || item.Kind < 1 || item.Kind > 3)
                throw new InvalidOperationException("玩家舰战表现事件序列无效。");
            ValidatePose(item.Pose);
            if (item.Kind != 2 && item.Serial != item.Pose.Id ||
                item.Kind == 2 && item.Serial <= item.Pose.Id)
                throw new InvalidOperationException("舰战表现事件和实例编号不一致。");
            previous = item.Serial;
        }
        // Same ordering as calling ValidateBattleWeaponEffects last.
        foreach (int weapon in frame.Weapons)
            if (weapon < 0)
                throw new InvalidOperationException("舰战武器表现字段或插槽身份无效。");
    }
    private static string Outcome(Action action)
    {
        try { action(); return "accepted"; }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static string OldValidate(Frame frame)
    {
        return Outcome(() =>
        {
            CheckFrameShape(frame);
            var ids = new HashSet<long>();
            ValidateContents(frame, ids);
        });
    }
    private static string NewValidate(Frame frame, BattleEffectIdValidationBuffer buffer)
    {
        return Outcome(() =>
        {
            CheckFrameShape(frame);
            HashSet<long> ids = buffer.Rent();
            try { ValidateContents(frame, ids); }
            finally { buffer.Release(ids); }
        });
    }
    private static void Same(Frame frame, BattleEffectIdValidationBuffer buffer, string label)
    {
        string oldResult = OldValidate(frame);
        string current = NewValidate(frame, buffer);
        Check(oldResult == current,
            label + ": old=" + oldResult + ", new=" + current);
    }
    private static long Allocated(Action action, int loops)
    {
        for (int i = 0; i < 90; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < loops; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Main()
    {
        try
        {
            var buffer = new BattleEffectIdValidationBuffer();
            Check(Throws<ArgumentNullException>(() => buffer.Release(null)),
                "null validation lease rejects explicitly");
            var first = buffer.Rent();
            Check(first.Count == 0, "new battle begins with empty uniqueness registry");
            Check(first.Add(long.MinValue) && first.Add(long.MaxValue) &&
                first.Add(-1) && first.Add(0) && first.Add(1L << 32),
                "registry preserves full signed 64-bit UID values");
            Check(!first.Add(long.MinValue),
                "same native visual ID is rejected within one frame");
            var nested = buffer.Rent();
            Check(!ReferenceEquals(nested, first) && nested.Count == 0 &&
                nested.Add(long.MinValue),
                "reentrant validation cannot reuse outer frame's UID registry");
            buffer.Release(nested);
            Check(first.Count == 5 && first.Contains(long.MinValue),
                "nested return cannot clear outer authoritative uniqueness checks");
            buffer.Release(first);
            var again = buffer.Rent();
            Check(ReferenceEquals(first, again) && again.Count == 0 &&
                again.Add(long.MinValue),
                "next received frame can reuse ID without false duplicate rejection");
            buffer.Release(again);

            Check(Throws<InvalidOperationException>(() =>
            {
                var lease = buffer.Rent();
                try
                {
                    lease.Add(15);
                    throw new InvalidOperationException("native validation failed");
                }
                finally { buffer.Release(lease); }
            }), "native validation exception propagates unchanged");
            var recovered = buffer.Rent();
            Check(ReferenceEquals(recovered, first) && recovered.Count == 0 &&
                recovered.Add(15),
                "failed validation is fully cleared for the next checkpoint");
            buffer.Release(recovered);

            var highWater = buffer.Rent();
            for (int i = 0; i < 32000; i++) highWater.Add(i);
            Check(highWater.Count == 32000,
                "maximum projectile plus VFX IDs can be validated");
            buffer.Reset();
            Check(highWater.Count == 32000 && highWater.Contains(31999),
                "room teardown defers reset if native validation is active");
            var innerDuringReset = buffer.Rent();
            Check(!ReferenceEquals(innerDuringReset, highWater) &&
                innerDuringReset.Add(0),
                "nested callback during scene cleanup gets independent registry");
            buffer.Release(innerDuringReset);
            buffer.Release(highWater);
            Check(highWater.Count == 0, "cleanup after lease release drops all native IDs");
            var afterRoom = buffer.Rent();
            Check(ReferenceEquals(afterRoom, first) && afterRoom.Count == 0,
                "new battle never inherits old unique IDs");
            buffer.Release(afterRoom);
            buffer.Reset();

            Same(null, buffer, "null battle frame structure");
            Same(new Frame(), buffer, "empty valid battle frame");
            var frame = new Frame();
            frame.Projectiles.Add(new Pose(1));
            frame.Vfx.Add(new Pose(2));
            Same(frame, buffer, "distinct projectile and VFX IDs");
            frame.Vfx[0].Id = 1;
            Same(frame, buffer, "projectile and VFX duplicate rejected");
            frame.Vfx[0].Id = 2;
            frame.Projectiles.Add(new Pose(1));
            Same(frame, buffer, "duplicate within projectile list rejected");
            frame.Projectiles.RemoveAt(1);
            frame.Vfx.Add(new Pose(2));
            Same(frame, buffer, "duplicate within VFX list rejected");
            frame.Vfx.RemoveAt(1);
            frame.Projectiles[0].Id = 0;
            Same(frame, buffer, "invalid projectile UID rejected before VFX");
            frame.Projectiles[0].Id = 1;
            frame.Vfx[0].X = float.NaN;
            Same(frame, buffer, "invalid VFX telemetry rejected before duplicate check");
            frame.Vfx[0].X = 0;
            frame.Events.Add(new Event(3, 1, new Pose(3)));
            frame.Events.Add(new Event(5, 2, new Pose(4)));
            Same(frame, buffer, "ordered launch and terminal events accepted");
            frame.Events[1].Serial = 1;
            Same(frame, buffer, "non-monotonic event sequence rejected");
            frame.Events[1].Serial = 5;
            frame.Events[1].Pose.Id = 6;
            Same(frame, buffer, "terminal event serial not greater than pose UID");
            frame.Events[1].Pose.Id = 4;
            frame.Weapons.Add(-1);
            Same(frame, buffer, "weapon validation still happens after events");
            frame.Weapons.Clear();
            frame.Vfx[0] = null;
            Same(frame, buffer, "null VFX rejected before validating valid events");

            // Structural rejection happens without renting or mutating the
            // per-room ID set, including lists at each native network limit.
            var shape = new Frame { Schema = 2 };
            Same(shape, buffer, "schema rejection");
            shape.Schema = 1; shape.Sequence = 0;
            Same(shape, buffer, "non-positive receive sequence");
            shape.Sequence = 1; shape.Projectiles = null;
            Same(shape, buffer, "null native projectile collection");
            shape.Projectiles = new List<Pose>(); shape.Vfx = null;
            Same(shape, buffer, "null VFX collection");
            shape.Vfx = new List<Pose>(); shape.Events = null;
            Same(shape, buffer, "null event collection");
            shape.Events = new List<Event>(); shape.Weapons = null;
            Same(shape, buffer, "null weapon collection");
            shape.Weapons = new List<int>();
            shape.Projectiles = Enumerable.Repeat(new Pose(1), 16001).ToList();
            Same(shape, buffer, "over-limit projectile collection rejected structurally");
            shape.Projectiles = new List<Pose>();
            shape.Vfx = Enumerable.Repeat(new Pose(1), 16001).ToList();
            Same(shape, buffer, "over-limit VFX collection rejected structurally");

            // Old/new differential tests cover valid frames, duplicate IDs,
            // invalid pose resources, events, and intermittent failed frames.
            var random = new Random(20261018);
            for (int iteration = 0; iteration < 18000; iteration++)
            {
                var candidate = new Frame();
                int pCount = random.Next(0, 80);
                int vCount = random.Next(0, 40);
                for (int i = 0; i < pCount; i++)
                {
                    long id = random.Next(0, 5) == 0
                        ? (long)random.Next(0, 20000) << 32 | (uint)random.Next()
                        : random.Next(-3, 160);
                    var pose = new Pose(id);
                    if (random.Next(0, 37) == 0) pose.Resource = "";
                    if (random.Next(0, 61) == 0) pose.X = float.PositiveInfinity;
                    candidate.Projectiles.Add(random.Next(0, 71) == 0 ? null : pose);
                }
                for (int i = 0; i < vCount; i++)
                {
                    long id = random.Next(0, 5) == 0
                        ? (long)random.Next(0, 20000) << 32 | (uint)random.Next()
                        : random.Next(-3, 160);
                    var pose = new Pose(id);
                    if (random.Next(0, 37) == 0) pose.Team = 3;
                    candidate.Vfx.Add(random.Next(0, 71) == 0 ? null : pose);
                }
                int eCount = random.Next(0, 14);
                for (int i = 0; i < eCount; i++)
                {
                    long serial = (i + 1) * 10L;
                    int kind = random.Next(1, 4);
                    long uid = kind == 2 ? serial - 1 : serial;
                    if (random.Next(0, 25) == 0) uid = serial + 1;
                    candidate.Events.Add(random.Next(0, 63) == 0
                        ? null : new Event(serial, kind, new Pose(uid)));
                }
                if (random.Next(0, 13) == 0) candidate.Weapons.Add(-1);
                Same(candidate, buffer, "random native battle effect frame " + iteration);
            }

            // Measure ONLY this HashSet lifetime; JSON parsing, native
            // weapon validation, Unity and visual object creation are out.
            const int projectiles = 256, vfx = 64, ticks = 1500;
            long[] projectileIds = new long[projectiles];
            long[] vfxIds = new long[vfx];
            for (int i = 0; i < projectiles; i++) projectileIds[i] = 10000 + i;
            for (int i = 0; i < vfx; i++) vfxIds[i] = 20000 + i;
            Action previous = () =>
            {
                var ids = new HashSet<long>();
                foreach (long id in projectileIds)
                    if (!ids.Add(id)) throw new Exception("duplicate projectile");
                foreach (long id in vfxIds)
                    if (!ids.Add(id)) throw new Exception("duplicate VFX");
                if (ids.Count != projectiles + vfx) throw new Exception("missing IDs");
            };
            Action optimized = () =>
            {
                var ids = buffer.Rent();
                try
                {
                    foreach (long id in projectileIds)
                        if (!ids.Add(id)) throw new Exception("duplicate projectile");
                    foreach (long id in vfxIds)
                        if (!ids.Add(id)) throw new Exception("duplicate VFX");
                    if (ids.Count != projectiles + vfx) throw new Exception("missing IDs");
                }
                finally { buffer.Release(ids); }
            };
            long oldBytes = Allocated(previous, ticks);
            long newBytes = Allocated(optimized, ticks);
            Check(oldBytes > 0 && newBytes == 0,
                "linked production UID lease avoids stable frame HashSet allocation");
            Console.WriteLine("INFO: " + ticks + " battle frame validations × " +
                projectiles + " projectiles + " + vfx + " VFX: old HashSet " +
                oldBytes + " B, production reused validation set " + newBytes +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " linked battle effect UID validation and lifecycle checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
