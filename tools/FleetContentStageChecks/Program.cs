using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Content
    {
        internal long FleetUid;
        internal bool Created, Invalid;
        internal Dictionary<string, object> Decoded = new Dictionary<string, object>();
        internal Content(long uid, bool created) { FleetUid = uid; Created = created; }
    }
    private sealed class Fleet
    {
        internal long Uid;
        internal readonly Dictionary<string, object> Values = new Dictionary<string, object>();
        internal Fleet(long uid) { Uid = uid; }
    }
    private sealed class Frame
    {
        internal readonly List<Content> Content = new List<Content>();
        internal readonly Dictionary<long, Fleet> Fleets = new Dictionary<long, Fleet>();
        internal readonly List<string> Trace = new List<string>();
    }
    private static int checks;

    private static void Check(bool pass, string reason)
    {
        if (!pass) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static Frame Clone(Frame source)
    {
        var frame = new Frame();
        foreach (var fleet in source.Fleets)
        {
            var copied = new Fleet(fleet.Key);
            foreach (var field in fleet.Value.Values)
                copied.Values.Add(field.Key, field.Value);
            frame.Fleets.Add(fleet.Key, copied);
        }
        foreach (Content item in source.Content)
        {
            if (item == null) { frame.Content.Add(null); continue; }
            var copy = new Content(item.FleetUid, item.Created) { Invalid = item.Invalid };
            foreach (var field in item.Decoded)
                copy.Decoded.Add(field.Key, field.Value);
            frame.Content.Add(copy);
        }
        return frame;
    }
    private static void ValidateStage(Frame frame, Content content, HashSet<long> seen,
        out Fleet fleet, out Dictionary<string, object> decoded, out bool created)
    {
        // Both implementations must preflight every content record before
        // applying any native fleet field. The helper must not influence
        // any decoded object, callback, error or write ordering.
        if (content == null || content.Invalid || !seen.Add(content.FleetUid))
            throw new InvalidOperationException("Fleet content ownership is missing or duplicated.");
        created = !frame.Fleets.TryGetValue(content.FleetUid, out fleet);
        if (created && !content.Created)
            throw new InvalidOperationException("New fleet replica requires complete content.");
        if (!created && content.Created)
            throw new InvalidOperationException("Existing fleet cannot be created again.");
        decoded = new Dictionary<string, object>();
        foreach (var field in content.Decoded)
        {
            if (field.Value == null) throw new InvalidOperationException("Fleet delta field is null.");
            decoded.Add(field.Key, field.Value);
        }
        if (created) fleet = new Fleet(content.FleetUid);
    }
    private static void Write(Frame frame, Content content, Fleet fleet,
        Dictionary<string, object> decoded, bool created, int failAt, ref int applied)
    {
        if (++applied == failAt)
            throw new InvalidOperationException("Native field write failed.");
        foreach (var field in decoded)
        {
            fleet.Values[field.Key] = field.Value;
            frame.Trace.Add(content.FleetUid + ":" + field.Key);
        }
        if (created)
        {
            frame.Fleets.Add(content.FleetUid, fleet);
            frame.Trace.Add("created:" + content.FleetUid);
        }
    }
    private static string OldApply(Frame frame, int failAt)
    {
        try
        {
            var staged = new List<Tuple<Content, Fleet, Dictionary<string, object>, bool>>();
            var seen = new HashSet<long>();
            foreach (Content item in frame.Content)
            {
                Fleet fleet; Dictionary<string, object> decoded; bool created;
                ValidateStage(frame, item, seen, out fleet, out decoded, out created);
                staged.Add(Tuple.Create(item, fleet, decoded, created));
            }
            int applied = 0;
            foreach (var stage in staged)
                Write(frame, stage.Item1, stage.Item2, stage.Item3, stage.Item4, failAt, ref applied);
            return "accepted";
        }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static string NewApply(Frame frame, int failAt)
    {
        try
        {
            var staged = new List<FleetContentStage<Content, Fleet>>(
                Math.Min(frame.Content.Count, 512));
            var seen = new HashSet<long>();
            foreach (Content item in frame.Content)
            {
                Fleet fleet; Dictionary<string, object> decoded; bool created;
                ValidateStage(frame, item, seen, out fleet, out decoded, out created);
                staged.Add(new FleetContentStage<Content, Fleet>(item, fleet, decoded, created));
            }
            int applied = 0;
            foreach (var stage in staged)
                Write(frame, stage.Item1, stage.Item2, stage.Item3, stage.Item4, failAt, ref applied);
            return "accepted";
        }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static void Compare(Frame original, int failAt, string reason)
    {
        Frame before = Clone(original), updated = Clone(original);
        string oldResult = OldApply(before, failAt);
        string newResult = NewApply(updated, failAt);
        Check(oldResult == newResult,
            reason + " exception and acceptance: " + oldResult + " vs " + newResult);
        Check(before.Trace.SequenceEqual(updated.Trace),
            reason + " exact native write order and partial writes");
        Check(before.Fleets.Keys.SequenceEqual(updated.Fleets.Keys),
            reason + " retained/new fleet identity order");
        foreach (var pair in before.Fleets)
        {
            Fleet fleet;
            Check(updated.Fleets.TryGetValue(pair.Key, out fleet),
                reason + " full signed UID remains present");
            Check(pair.Value.Values.Count == fleet.Values.Count &&
                pair.Value.Values.Keys.SequenceEqual(fleet.Values.Keys),
                reason + " native fields update in identical order");
            foreach (var field in pair.Value.Values)
                Check(fleet.Values.TryGetValue(field.Key, out var value) &&
                    Equals(field.Value, value),
                    reason + " identical native fleet field value");
        }
        if (oldResult != "accepted" && oldResult !=
            "InvalidOperationException: Native field write failed.")
            Check(before.Trace.Count == 0 && updated.Trace.Count == 0,
                reason + " failed preflight never mutates native game state");
    }
    private static long Allocated(Action action, int frames)
    {
        for (int i = 0; i < 50; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    private static void Benchmark()
    {
        const int fleetCount = 384, frames = 1000;
        var contents = new Content[fleetCount];
        var fleets = new Fleet[fleetCount];
        var dictionaries = new Dictionary<string, object>[fleetCount];
        for (int i = 0; i < fleetCount; i++)
        {
            contents[i] = new Content(10000L + i, i % 2 == 0);
            fleets[i] = new Fleet(10000L + i);
            dictionaries[i] = new Dictionary<string, object> { { "ships", i } };
        }
        Action legacy = () =>
        {
            var stages = new List<Tuple<Content, Fleet, Dictionary<string, object>, bool>>();
            for (int i = 0; i < fleetCount; i++)
                stages.Add(Tuple.Create(contents[i], fleets[i], dictionaries[i], contents[i].Created));
            int count = 0;
            foreach (var stage in stages)
                if (ReferenceEquals(stage.Item2, fleets[count++]) && stage.Item3.Count == 1) { }
                else throw new Exception("Old native staging identity changed");
        };
        Action modern = () =>
        {
            var stages = new List<FleetContentStage<Content, Fleet>>(Math.Min(fleetCount, 512));
            for (int i = 0; i < fleetCount; i++)
                stages.Add(new FleetContentStage<Content, Fleet>(contents[i], fleets[i],
                    dictionaries[i], contents[i].Created));
            int count = 0;
            foreach (var stage in stages)
                if (ReferenceEquals(stage.Item2, fleets[count++]) && stage.Item3.Count == 1) { }
                else throw new Exception("Optimized native staging identity changed");
        };
        long oldBytes = Allocated(legacy, frames);
        long newBytes = Allocated(modern, frames);
        Check(oldBytes > newBytes && oldBytes - newBytes > 1000000,
            "value-type production staging reduces per-content heap objects");
        Console.WriteLine("INFO: " + frames + " synthetic content baselines × " +
            fleetCount + " staged native fleets: old List<Tuple> " + oldBytes +
            " B, production List<FleetContentStage> " + newBytes +
            " B (managed allocations after warmup)");
    }
    private static int Main()
    {
        try
        {
            // The structural representation must never clone these objects:
            // content aliases, decoded dictionaries and canonical fleet
            // references are required by native gameplay and asset wrappers.
            var content = new Content(long.MaxValue, false);
            var fleet = new Fleet(long.MaxValue);
            var decoded = new Dictionary<string, object> { { "uid", long.MaxValue } };
            var item = new FleetContentStage<Content, Fleet>(content, fleet, decoded, false);
            Check(ReferenceEquals(content, item.Item1) &&
                ReferenceEquals(fleet, item.Item2) &&
                ReferenceEquals(decoded, item.Item3) && !item.Item4,
                "all four production staging fields preserve source identity and created flag");
            decoded["ships"] = "native-shell";
            Check(item.Item3.ContainsKey("ships"),
                "decoded dictionary modifications stay visible through value-type stage");
            var stagedCopy = item;
            Check(ReferenceEquals(stagedCopy.Item1, item.Item1) &&
                ReferenceEquals(stagedCopy.Item2, item.Item2) &&
                ReferenceEquals(stagedCopy.Item3, item.Item3),
                "struct copies never create independent native game objects");

            Compare(new Frame(), 0, "empty galaxy content delta");
            for (int count = 0; count <= 20; count++)
            {
                var frame = new Frame();
                for (int i = 0; i < count; i++)
                {
                    long uid = i + 1;
                    bool created = i % 3 == 0;
                    if (!created) frame.Fleets.Add(uid, new Fleet(uid));
                    var wire = new Content(uid, created);
                    wire.Decoded.Add("uid", uid);
                    wire.Decoded.Add("name", "ship-" + i);
                    frame.Content.Add(wire);
                }
                Compare(frame, 0, "galaxy baseline size " + count);
                Compare(frame, 2, "partial native write fault baseline size " + count);
                if (count > 0)
                {
                    frame.Content[count - 1].Invalid = true;
                    Compare(frame, 0, "last malformed fleet prevents native writes " + count);
                    frame.Content[count - 1].Invalid = false;
                    frame.Content.Add(frame.Content[0]);
                    Compare(frame, 0, "duplicate UID prevented before staging " + count);
                }
            }
            long[] extreme = { long.MinValue, -1, 0, 1, 1L << 32, long.MaxValue };
            var boundary = new Frame();
            foreach (long uid in extreme)
            {
                var record = new Content(uid, true);
                record.Decoded["ships"] = "native";
                boundary.Content.Add(record);
            }
            Compare(boundary, 0, "complete signed 64-bit galaxy fleet identities");
            Compare(boundary, 4, "partial native write after several new fleets");

            // Reservation stops at 512 entries, so both the direct-capacity
            // path and the fallback List<T> growth path must preserve the
            // original ordered native stage application and failure behavior.
            foreach (int count in new int[] { 511, 512, 513, 1024 })
            {
                var frame = new Frame();
                for (int i = 0; i < count; i++)
                {
                    var wire = new Content(100000L + i, true);
                    wire.Decoded.Add("uid", wire.FleetUid);
                    frame.Content.Add(wire);
                }
                Compare(frame, 0, "bounded capacity near 512 stages: " + count);
                Compare(frame, count / 2, "native write error near capacity threshold: " + count);
            }

            var random = new Random(20261018);
            for (int iteration = 0; iteration < 5000; iteration++)
            {
                var frame = new Frame();
                int count = random.Next(0, 85);
                for (int i = 0; i < count; i++)
                {
                    long uid = random.Next(0, 8) == 0
                        ? extreme[random.Next(extreme.Length)]
                        : random.Next(-500, 1200);
                    bool created = random.Next(0, 2) == 0;
                    if (!created && !frame.Fleets.ContainsKey(uid))
                        frame.Fleets.Add(uid, new Fleet(uid));
                    var wire = new Content(uid, created) {
                        Invalid = random.Next(0, 75) == 0
                    };
                    int fields = random.Next(0, 6);
                    for (int f = 0; f < fields; f++)
                        wire.Decoded["field-" + f] =
                            random.Next(0, 45) == 0 ? null : (object)random.Next();
                    frame.Content.Add(wire);
                }
                Compare(frame, random.Next(0, 10) == 0 ? random.Next(1, count + 2) : 0,
                    "random galaxy fleet preflight/stage/apply " + iteration);
            }
            Benchmark();
            Console.WriteLine("PASS: " + checks + " linked fleet content staging checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
