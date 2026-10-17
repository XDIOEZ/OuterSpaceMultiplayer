using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class WireValue
    {
        public string Name, NativeValue;
    }
    private sealed class FleetContent
    {
        public List<WireValue> Fields;
        public FleetContent() { Fields = new List<WireValue>(); }
        public FleetContent(List<WireValue> fields) { Fields = fields; }
    }

    private static int passed;
    private static int created;
    private static readonly Func<string, string, WireValue> Factory = (name, value) =>
    {
        created++;
        return new WireValue { Name = name, NativeValue = value };
    };

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + reason);
        passed++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static Dictionary<string, NativeWire.CachedValue> Sample(params string[] values)
    {
        var result = new Dictionary<string, NativeWire.CachedValue>(StringComparer.Ordinal);
        for (int i = 0; i < values.Length; i += 2)
            result.Add(values[i], new NativeWire.CachedValue(values[i + 1]));
        return result;
    }
    private static Dictionary<string, string> Known(Dictionary<string, NativeWire.CachedValue> sample)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in sample) result.Add(entry.Key, entry.Value.Encoded);
        return result;
    }

    // Independent reference equivalent to the pre-optimization production
    // CaptureFleetFrame loop. It eagerly creates a content object and empty
    // list on every fleet, even when returning no content delta.
    private static FleetContent Legacy(Dictionary<string, NativeWire.CachedValue> sample,
        Dictionary<string, string> known, bool full)
    {
        var content = new FleetContent();
        foreach (var field in sample)
        {
            string previous;
            if (full || !known.TryGetValue(field.Key, out previous)
                || !String.Equals(previous, field.Value.Encoded, StringComparison.Ordinal))
                content.Fields.Add(new WireValue { Name = field.Key, NativeValue = field.Value.Encoded });
        }
        return content.Fields.Count == 0 ? null : content;
    }
    private static FleetContent Lazy(Dictionary<string, NativeWire.CachedValue> sample,
        Dictionary<string, string> known, bool full)
    {
        List<WireValue> delta = FleetDeltaFieldSelector.Select(sample, known, full, Factory);
        return delta == null ? null : new FleetContent(delta);
    }
    private static bool Identical(FleetContent oldResult, FleetContent result)
    {
        if (oldResult == null || result == null) return oldResult == result;
        return oldResult.Fields.Count == result.Fields.Count
            && oldResult.Fields.Zip(result.Fields, (a, b) => a.Name == b.Name
                    && String.Equals(a.NativeValue, b.NativeValue, StringComparison.Ordinal)).All(match => match);
    }
    private static long Measure(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    public static int Main()
    {
        try
        {
            var sample = Sample("uid", "1001", "ships", "[5,6]", "serializedHeros", "中", "status", "active");
            var known = Known(sample);
            created = 0;
            Check(FleetDeltaFieldSelector.Select(sample, known, false, Factory) == null
                && created == 0, "stable fleet has no content DTO, field list or wire values");
            var first = Lazy(sample, null, true);
            Check(Identical(Legacy(sample, null, true), first)
                && first.Fields.Count == sample.Count && created == sample.Count,
                "first full baseline contains all sampled fields in legacy order");
            Check(first.Fields[0].Name == "uid" && first.Fields[1].Name == "ships"
                && first.Fields[2].Name == "serializedHeros" && first.Fields[3].Name == "status",
                "first baseline preserves field order, including Unicode payloads");
            Check(!ReferenceEquals(first.Fields, Lazy(sample, null, true).Fields),
                "each full baseline owns its wire field list");
            var blank = Sample();
            Check(Identical(Legacy(blank, null, true), Lazy(blank, null, true)),
                "empty full-content sample still emits no content entry");
            Check(Identical(Legacy(blank, new Dictionary<string, string>(), false),
                Lazy(blank, new Dictionary<string, string>(), false)),
                "empty stable sample emits no unnecessary content");

            // Per-recipient confirmation remains the only committed baseline:
            // a failed send does not advance known fields.
            var guestKnown = Known(sample);
            guestKnown["ships"] = "[4]";
            var pending = Lazy(sample, guestKnown, false);
            Check(Identical(Legacy(sample, guestKnown, false), pending)
                && pending.Fields.Count == 1 && pending.Fields[0].Name == "ships",
                "changed cargo only produces its one changed field");
            Check(Identical(pending, Lazy(sample, guestKnown, false)),
                "before send confirmation the exact same delta can be retried");
            guestKnown["ships"] = sample["ships"].Encoded;
            Check(Lazy(sample, guestKnown, false) == null,
                "after confirmation that recipient no longer receives the field");
            var otherPeerKnown = Known(sample);
            otherPeerKnown["serializedHeros"] = "旧";
            Check(Lazy(sample, otherPeerKnown, false).Fields.Single().Name == "serializedHeros",
                "another recipient's older confirmation remains independent");
            Check(Lazy(sample, known, false) == null,
                "unchanged first recipient stays silent while another gets a delta");

            known.Remove("status");
            Check(Identical(Legacy(sample, known, false), Lazy(sample, known, false))
                && Lazy(sample, known, false).Fields[0].Name == "status",
                "unknown field is included even if its value is unchanged");
            known["status"] = sample["status"].Encoded;
            known.Add("obsolete", "extra");
            Check(Lazy(sample, known, false) == null,
                "extra previously confirmed fields do not generate unrelated removals");
            sample["uid"] = new NativeWire.CachedValue("1001");
            Check(Lazy(sample, known, false) == null,
                "a new CachedValue wrapper with identical bytes stays unchanged");
            sample["status"] = new NativeWire.CachedValue("paused");
            sample["ships"] = new NativeWire.CachedValue("[7,8]");
            var multiple = Lazy(sample, known, false);
            Check(Identical(Legacy(sample, known, false), multiple)
                && multiple.Fields.Count == 2 && multiple.Fields[0].Name == "ships"
                && multiple.Fields[1].Name == "status",
                "multiple modified fields preserve original order and wire values");

            var nullable = Sample("field", null);
            var nullableKnown = new Dictionary<string, string> { { "field", null } };
            Check(Lazy(nullable, nullableKnown, false) == null,
                "null confirmed and current wire value compare identically");
            nullableKnown["field"] = "before";
            Check(Identical(Legacy(nullable, nullableKnown, false), Lazy(nullable, nullableKnown, false)),
                "transition to null still sends the same wire delta");
            Check(Throws<ArgumentNullException>(() => FleetDeltaFieldSelector.Select<WireValue>(
                null, known, false, Factory)), "null sampled dictionary is rejected");
            Check(Throws<ArgumentNullException>(() => FleetDeltaFieldSelector.Select<WireValue>(
                sample, known, false, null)), "null wire-field constructor is rejected");

            // Reference allocation includes the temporary DTO + List which was
            // thrown away for *every* unchanged fleet in the old hot path.
            var allSamples = new List<Dictionary<string, NativeWire.CachedValue>>();
            var allKnown = new List<Dictionary<string, string>>();
            for (int i = 0; i < 96; i++)
            {
                var state = new Dictionary<string, NativeWire.CachedValue>(StringComparer.Ordinal);
                for (int field = 0; field < 16; field++)
                    state.Add("field-" + field, new NativeWire.CachedValue("same-value-" + field));
                allSamples.Add(state);
                allKnown.Add(Known(state));
            }
            const int frames = 300;
            Action oldBatch = () =>
            {
                for (int i = 0; i < allSamples.Count; i++)
                    GC.KeepAlive(Legacy(allSamples[i], allKnown[i], false));
            };
            Action newBatch = () =>
            {
                for (int i = 0; i < allSamples.Count; i++)
                    GC.KeepAlive(Lazy(allSamples[i], allKnown[i], false));
            };
            long oldAllocated = Measure(oldBatch, frames);
            long newAllocated = Measure(newBatch, frames);
            Check(oldAllocated > 0 && newAllocated == 0,
                "300 stable 96-fleet frames eliminate temporary FleetContent and Fields allocations");
            Console.WriteLine("INFO: " + frames + " stable frames × 96 fleets: old " + oldAllocated
                + " B allocated, new selector " + newAllocated + " B (managed allocations)");
            Console.WriteLine("PASS: " + passed + " linked fleet delta field selector checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
