using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class NativeField
    {
        internal readonly string FieldName;
        internal int Reads;
        internal NativeField(string name) { FieldName = name; }
        internal string Name { get { Reads++; return FieldName; } }
    }
    private sealed class Value
    {
        internal string Name;
        internal int Payload;
        internal Value(string name, int payload) { Name = name; Payload = payload; }
    }
    private sealed class Outcome
    {
        internal string Error = "accepted";
        internal readonly List<NativeField> Fields = new List<NativeField>();
        internal readonly List<int> Values = new List<int>();
    }
    #pragma warning disable CS0649 // Reflected game-style fields are deliberately not instantiated.
    private sealed class ActualNativeFleet
    {
        public long uid, ownerId;
        public int type, status, ships, morale, food, travel, caravan;
        public string name, galaxy, player, waypoints, weapons, cargo, buffs;
        public float x, y, z, heading, velocity, distance, fuel, armor, health;
    }
    #pragma warning restore CS0649

    private static int checks;
    private static readonly Func<NativeField, string> FieldName = f => f.Name;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
    }
    private static string ExceptionText(Exception error)
    { return error.GetType().Name + ": " + error.Message; }
    private static NativeField[] Table(params string[] names)
    { return names.Select(name => new NativeField(name)).ToArray(); }
    private static NativeField[] Table(int size)
    { return Enumerable.Range(0, size).Select(i => new NativeField("field-" + i)).ToArray(); }

    // The original production decode validates a field's native identity
    // BEFORE duplicate incoming names and before writing any native bean.
    private static Outcome Decode(NativeField[] native, List<Value> incoming, bool indexed)
    {
        var result = new Outcome();
        try
        {
            var decoded = new Dictionary<string, int>();
            var lookup = indexed && incoming != null && native != null &&
                FleetContentFieldLookup<NativeField>.ShouldIndex(incoming.Count, native.Length)
                ? new FleetContentFieldLookup<NativeField>(native, FieldName) : null;
            foreach (Value value in incoming)
            {
                NativeField field = lookup == null
                    ? native.SingleOrDefault(f => f.Name == value.Name)
                    : lookup.Find(value.Name);
                if (field == null || decoded.ContainsKey(value.Name))
                    throw new InvalidOperationException("Fleet delta references an unknown or duplicated field.");
                result.Fields.Add(field);
                result.Values.Add(value.Payload);
                decoded.Add(value.Name, value.Payload);
            }
        }
        catch (Exception error) { result.Error = ExceptionText(error); }
        return result;
    }
    private static void Compare(NativeField[] fields, List<Value> values, string label)
    {
        Outcome previous = Decode(fields, values, false);
        Outcome optimized = Decode(fields, values, true);
        Check(previous.Error == optimized.Error,
            label + " previous=" + previous.Error + ", current=" + optimized.Error);
        Check(previous.Fields.Count == optimized.Fields.Count,
            label + " completed decoded-field count before error");
        for (int i = 0; i < previous.Fields.Count; i++)
        {
            Check(ReferenceEquals(previous.Fields[i], optimized.Fields[i]),
                label + " field identity at " + i);
            Check(previous.Values[i] == optimized.Values[i],
                label + " payload order at " + i);
        }
    }
    private static void CompareQuery(NativeField[] fields, string[] names, string label)
    {
        var lookup = new FleetContentFieldLookup<NativeField>(fields, FieldName);
        foreach (string name in names)
        {
            string oldError = "accepted", currentError = "accepted";
            NativeField oldField = null, newField = null;
            try { oldField = fields.SingleOrDefault(f => f.Name == name); }
            catch (Exception error) { oldError = ExceptionText(error); }
            try { newField = lookup.Find(name); }
            catch (Exception error) { currentError = ExceptionText(error); }
            Check(oldError == currentError && ReferenceEquals(oldField, newField),
                label + " requested=" + name + ", old=" + oldError + ", new=" + currentError);
        }
    }
    private static long Allocated(Action action, int frames)
    {
        for (int i = 0; i < 70; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static int Main()
    {
        try
        {
            for (int count = 0; count <= 12; count++)
            {
                var fields = Table(count);
                var values = new List<Value>();
                for (int i = 0; i < count; i++) values.Add(new Value("field-" + i, i * 12));
                Check(FleetContentFieldLookup<NativeField>.ShouldIndex(count, count)
                    == (count >= 8), "dense decode threshold " + count);
                Compare(fields, values, "wire field threshold " + count);
            }
            Check(!FleetContentFieldLookup<NativeField>.ShouldIndex(12, 7)
                && !FleetContentFieldLookup<NativeField>.ShouldIndex(7, 12)
                && FleetContentFieldLookup<NativeField>.ShouldIndex(8, 8),
                "sparse native field metadata stays on original path");
            Check(Throws<ArgumentNullException>(() =>
                new FleetContentFieldLookup<NativeField>(Table(8), null)),
                "missing native name selector is rejected");
            var actualFields = typeof(ActualNativeFleet).GetFields(BindingFlags.Instance | BindingFlags.Public);
            Check(actualFields.Length >= 20, "reflection fixture exposes dense real CLR metadata");
            var nativeReflection = new FleetContentFieldLookup<FieldInfo>(actualFields, f => f.Name);
            foreach (FieldInfo field in actualFields)
                Check(ReferenceEquals(nativeReflection.Find(field.Name), field),
                    "linked helper returns actual reflected FieldInfo: " + field.Name);
            Check(nativeReflection.Find("uidX") == null
                && nativeReflection.Find("UID") == null
                && nativeReflection.Find(null) == null,
                "unknown, wrong-case and null field names never resolve as valid metadata");

            var denseFields = Table(24);
            Compare(denseFields,
                Enumerable.Range(0, 24).Select(i => new Value("field-" + i, i)).ToList(),
                "dense full baseline preserves incoming field and payload order");
            Compare(denseFields, new List<Value> { new Value("field-0", 0),
                new Value("FIELD-0", 1) },
                "ordinal comparer must reject different-case unknown field");
            Compare(denseFields, Enumerable.Range(0, 12).Select(i =>
                    new Value("field-" + i, i)).Concat(new[] { new Value("field-1", 888) }).ToList(),
                "duplicate incoming field after earlier valid data retains original rejection");
            Compare(denseFields, Enumerable.Range(0, 11).Select(i =>
                    new Value("field-" + i, i)).Concat(new[] { new Value("missing", 88) }).ToList(),
                "unknown field is rejected before any native object write");
            Compare(denseFields, Enumerable.Range(0, 11).Select(i =>
                    new Value("field-" + i, i)).Concat(new Value[] { null }).ToList(),
                "null incoming wire field preserves exception category and order");
            Compare(denseFields, Enumerable.Range(0, 11).Select(i =>
                    new Value("field-" + i, i)).Concat(new[] { new Value(null, 12) }).ToList(),
                "null network field name retains original source metadata handling");

            var malformed = Table("a", "b", "b", "c", "d", "e", "f", "g", "h");
            CompareQuery(malformed, new[] { "a", "b", "c", "f", "missing", null },
                "duplicated native reflection names only reject requested duplicates");
            malformed = Table("a", "b", "c", "d", "e", "f", "g", "h", "i");
            malformed[2] = null;
            CompareQuery(malformed, new[] { "a", "b", "c", "f", "missing", null },
                "malformed native fields retain old LINQ null dereference");
            malformed = Table("a", "b", "c", "d", "e", "f", "g", "h", "i");
            malformed[2] = new NativeField(null);
            CompareQuery(malformed, new[] { "a", "b", "c", "f", "missing", null },
                "null native field name retains exact legacy lookup semantics");
            CompareQuery(null, new[] { "a", "b", null },
                "null reflection source maintains original argument-null behavior");

            var unicode = Table("ship-id", "Ship-Id", "主舰", "e\u0301", "\u00e9",
                "slot:primary", "ship\\primary", " ", "uid");
            CompareQuery(unicode, new[] { "ship-id", "Ship-Id", "SHIP-ID", "主舰",
                "\u00e9", "e\u0301", "slot:primary", "ship\\primary", " ", "uid" },
                "case Unicode normalization and separators compare ordinally");

            var random = new Random(20261018);
            string[] names = { "ships", "serializedHeros", "uid", "ownerId",
                "buffs", "galaxyId", "playerId", "startTime", "endTime",
                "héros", "heros", "ships:main", "ShipS" };
            for (int trial = 0; trial < 4600; trial++)
            {
                int fieldCount = random.Next(0, 75);
                var fields = new NativeField[fieldCount];
                for (int i = 0; i < fields.Length; i++)
                {
                    int mode = random.Next(0, 35);
                    fields[i] = mode == 0 ? null : new NativeField(mode == 1 ? null :
                        random.Next(0, 5) == 0 ? names[random.Next(names.Length)] :
                        "field-" + random.Next(0, 85));
                }
                int wireCount = random.Next(0, 80);
                var values = new List<Value>(wireCount);
                for (int i = 0; i < wireCount; i++)
                {
                    string key = random.Next(0, 6) == 0
                        ? names[random.Next(names.Length)] : "field-" + random.Next(0, 85);
                    if (random.Next(0, 55) == 0) key = null;
                    values.Add(random.Next(0, 53) == 0 ? null : new Value(key, random.Next()));
                }
                Compare(fields, values, "randomized native metadata/wire decode " + trial);
            }

            // Reindex every incoming content frame: a native source may be
            // replaced after a rebind or reconnect and must never be cached.
            var previousFields = Table(10);
            var previousIndex = new FleetContentFieldLookup<NativeField>(previousFields, FieldName);
            NativeField oldNativeField = previousIndex.Find("field-3");
            previousFields[3] = new NativeField("field-3");
            var nextIndex = new FleetContentFieldLookup<NativeField>(previousFields, FieldName);
            Check(ReferenceEquals(previousIndex.Find("field-3"), oldNativeField)
                && ReferenceEquals(nextIndex.Find("field-3"), previousFields[3])
                && !ReferenceEquals(previousFields[3], oldNativeField),
                "incoming frame rebuild observes new native reflection reference");

            const int fieldCountDense = 64, frames = 600;
            var stable = Table(fieldCountDense);
            string[] requested = Enumerable.Range(0, fieldCountDense)
                .Select(i => "field-" + i).ToArray();
            Action legacy = () =>
            {
                foreach (string name in requested)
                    if (stable.SingleOrDefault(f => f.Name == name) == null)
                        throw new InvalidOperationException("Original dense content missing field");
            };
            Action optimized = () =>
            {
                var lookup = new FleetContentFieldLookup<NativeField>(stable, FieldName);
                foreach (string name in requested)
                    if (lookup.Find(name) == null)
                        throw new InvalidOperationException("Indexed dense content missing field");
            };
            legacy(); optimized();
            foreach (NativeField f in stable) f.Reads = 0;
            for (int i = 0; i < frames; i++) legacy();
            int legacyReads = stable.Sum(f => f.Reads);
            foreach (NativeField f in stable) f.Reads = 0;
            for (int i = 0; i < frames; i++) optimized();
            int indexedReads = stable.Sum(f => f.Reads);
            Check(legacyReads == fieldCountDense * fieldCountDense * frames
                && indexedReads == fieldCountDense * frames,
                "linked production index removes repeated native field name inspections");
            long oldBytes = Allocated(legacy, frames);
            long newBytes = Allocated(optimized, frames);
            Console.WriteLine("INFO: " + frames + " dense content records × " +
                fieldCountDense + " real-style fields: old FieldInfo.Name comparisons " +
                legacyReads + " vs indexed " + indexedReads +
                "; managed allocations old " + oldBytes + " B vs indexed " + newBytes + " B");
            Console.WriteLine("PASS: " + checks +
                " linked fleet content field validation and lookup checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
