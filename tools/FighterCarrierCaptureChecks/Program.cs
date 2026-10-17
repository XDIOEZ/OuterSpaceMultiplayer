using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Carrier
    {
        internal readonly long NativeId;
        internal Carrier(long nativeId) { NativeId = nativeId; }

        // Unity Object equality can differ from reference identity for a
        // destroyed native component. Verify we never call object.Equals.
        public override bool Equals(object other) { return other is Carrier; }
        public override int GetHashCode() { return 1; }
    }

    private static int assertions;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + message);
        assertions++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static void LegacyRegister(Dictionary<long, Carrier> values, long uid, Carrier carrier)
    {
        // Exactly the old RememberPlayerBattleFighterCarrier registry update.
        foreach (long old in values.Where(pair => pair.Key != uid && ReferenceEquals(pair.Value, carrier))
            .Select(pair => pair.Key).ToArray()) values.Remove(old);
        values[uid] = carrier;
    }
    private static void IndexedRegister(Dictionary<long, Carrier> values, long uid, Carrier carrier)
    {
        NativeFighterCarrierAliases.RemoveOldIds(values, uid, carrier);
        values[uid] = carrier;
    }
    private static void Same(Dictionary<long, Carrier> expected, Dictionary<long, Carrier> actual, string message)
    {
        Check(expected.Count == actual.Count, message + " record count");
        Check(expected.Keys.SequenceEqual(actual.Keys), message + " key enumeration order");
        foreach (var entry in expected)
        {
            Carrier candidate;
            Check(actual.TryGetValue(entry.Key, out candidate) && ReferenceEquals(entry.Value, candidate),
                message + " matching native reference " + entry.Key);
        }
    }
    private static Dictionary<long, Carrier> Copy(Dictionary<long, Carrier> old)
    {
        return new Dictionary<long, Carrier>(old);
    }
    private static List<long> LegacyCapture(Dictionary<long, Carrier> carriers,
        Action<long, Dictionary<long, Carrier>> onVisit = null)
    {
        var seen = new List<long>();
        foreach (var pair in carriers.ToArray())
        {
            if (onVisit != null) onVisit(pair.Key, carriers);
            seen.Add(pair.Key);
            if (pair.Value == null) carriers.Remove(pair.Key);
        }
        return seen;
    }
    private static List<long> IndexedCapture(Dictionary<long, Carrier> carriers,
        BattleEffectFrameSnapshot<long, Carrier> buffer,
        Action<long, Dictionary<long, Carrier>> onVisit = null)
    {
        var seen = new List<long>();
        var snapshot = buffer.Capture(carriers);
        try
        {
            foreach (var pair in snapshot)
            {
                if (onVisit != null) onVisit(pair.Key, carriers);
                seen.Add(pair.Key);
                if (pair.Value == null) carriers.Remove(pair.Key);
            }
        }
        finally { buffer.Release(snapshot); }
        return seen;
    }
    private static long Allocated(Action action, int samples)
    {
        for (int i = 0; i < 80; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Main()
    {
        try
        {
            Check(Throws<ArgumentNullException>(() =>
                NativeFighterCarrierAliases.RemoveOldIds<Carrier>(null, 1, new Carrier(1))),
                "null native registry is rejected");

            var first = new Carrier(10);
            var second = new Carrier(11);
            var third = new Carrier(12);
            var starting = new Dictionary<long, Carrier>
            {
                { 10, first }, { 20, second }, { 30, first },
                { -17, first }, { 40, third }, { long.MaxValue, first }
            };
            var old = Copy(starting); var current = Copy(starting);
            LegacyRegister(old, 10, first);
            IndexedRegister(current, 10, first);
            Same(old, current, "multiple aliases are removed without touching the canonical UID");
            Check(current.Count == 3 && ReferenceEquals(current[10], first),
                "all duplicate native IDs are removed, canonical ID stays bound");

            old = Copy(starting); current = Copy(starting);
            LegacyRegister(old, 1234, first);
            IndexedRegister(current, 1234, first);
            Same(old, current, "canonical identity migrates to newly allocated global UID");
            Check(!current.ContainsKey(10) && !current.ContainsKey(30)
                && !current.ContainsKey(-17) && !current.ContainsKey(long.MaxValue)
                && ReferenceEquals(current[1234], first),
                "migrated identity no longer appears under an old UID");

            old = Copy(starting); current = Copy(starting);
            LegacyRegister(old, 20, first);
            IndexedRegister(current, 20, first);
            Same(old, current, "canonical UID previously owned by a different native object");
            Check(ReferenceEquals(current[20], first) && !current.ContainsKey(10),
                "canonical replacement and alias cleanup retain original ownership semantics");

            var equalButNotSame = new Carrier(99);
            Check(first.Equals(equalButNotSame) && !ReferenceEquals(first, equalButNotSame),
                "reference test fixture intentionally has broad Equals semantics");
            old = Copy(starting); current = Copy(starting);
            LegacyRegister(old, 70, equalButNotSame);
            IndexedRegister(current, 70, equalButNotSame);
            Same(old, current, "Unity equality does not merge unrelated native objects");
            Check(current.ContainsKey(10) && current.ContainsKey(30)
                && ReferenceEquals(current[70], equalButNotSame),
                "different object instances remain distinct even with identical equality");

            var random = new Random(20261018);
            var sources = Enumerable.Range(0, 30).Select(i => new Carrier(i)).ToArray();
            for (int run = 0; run < 1500; run++)
            {
                var seed = new Dictionary<long, Carrier>();
                int count = random.Next(0, 65);
                for (int id = 0; id < count; id++)
                    seed.Add(id, sources[random.Next(sources.Length)]);
                old = Copy(seed); current = Copy(seed);
                for (int j = 0; j < 12; j++)
                {
                    long uid = random.Next(-5, 70);
                    Carrier carrier = sources[random.Next(sources.Length)];
                    LegacyRegister(old, uid, carrier);
                    IndexedRegister(current, uid, carrier);
                }
                Same(old, current, "randomized carrier alias migration " + run);
            }

            var originalCarriers = new Dictionary<long, Carrier>
            {
                { 1, new Carrier(1) }, { 2, null }, { 3, new Carrier(3) },
                { 4, new Carrier(4) }, { 5, new Carrier(5) }
            };
            var oldCapture = Copy(originalCarriers);
            var newCapture = Copy(originalCarriers);
            var buffer = new BattleEffectFrameSnapshot<long, Carrier>();
            var callbackCarrier = new Carrier(77);
            Action<long, Dictionary<long, Carrier>> callback = (uid, table) =>
            {
                if (uid == 1)
                {
                    table.Remove(4); // Original snapshot still visits this carrier.
                    table.Add(77, callbackCarrier); // Newly spawned only next capture.
                }
            };
            List<long> oldSeen = LegacyCapture(oldCapture, callback);
            List<long> newSeen = IndexedCapture(newCapture, buffer, callback);
            Check(oldSeen.SequenceEqual(newSeen),
                "despawn, new source ID and null shell preserve snapshot-before-mutation ordering");
            Same(oldCapture, newCapture, "native registry after mutation during capture");
            Check(!newSeen.Contains(77) && newCapture.ContainsKey(77)
                && !newCapture.ContainsKey(2),
                "new carrier is deferred, null shell is removed from the live registry");
            Check(LegacyCapture(oldCapture).SequenceEqual(IndexedCapture(newCapture, buffer)),
                "next capture sees the new carrier and excludes removed native shells");
            Same(oldCapture, newCapture, "next native carrier capture");

            var nestedSource = new Dictionary<long, Carrier> { { 1, first } };
            var outer = buffer.Capture(nestedSource);
            nestedSource.Add(2, second);
            var inner = buffer.Capture(nestedSource);
            Check(!ReferenceEquals(outer, inner) && outer.Count == 1 && inner.Count == 2,
                "nested native callback gets a distinct immutable snapshot");
            buffer.Release(inner);
            Check(outer.Count == 1 && outer[0].Key == 1,
                "reentrant snapshot release cannot invalidate its enclosing capture");
            buffer.Release(outer);
            var reused = buffer.Capture(nestedSource);
            Check(ReferenceEquals(reused, outer) && reused.Count == 2,
                "next native capture reuses the original buffer after reentrancy");
            buffer.Release(reused);

            Check(Throws<InvalidOperationException>(() =>
                IndexedCapture(nestedSource, buffer, (uid, table) =>
                { throw new InvalidOperationException("simulated native wing exception"); })),
                "a native wing exception propagates");
            var afterFault = buffer.Capture(nestedSource);
            Check(ReferenceEquals(afterFault, outer) && afterFault.Count == 2,
                "exceptional native capture releases the buffer and its shell references");
            buffer.Release(afterFault);

            var large = new Dictionary<long, Carrier>();
            for (int i = 0; i < 256; i++) large.Add(i, new Carrier(i));
            var largeFrame = buffer.Capture(large);
            Check(largeFrame.Capacity >= 256, "large battle grows the registry capture buffer as needed");
            buffer.Reset();
            Check(largeFrame.Count == 256, "cleanup while leased defers reset until release");
            buffer.Release(largeFrame);
            Check(largeFrame.Capacity == 0, "battle shutdown releases the large native capture backing array");

            const int stableCarriers = 48, stableFrames = 700;
            var registered = new Dictionary<long, Carrier>();
            for (int i = 0; i < stableCarriers; i++) registered.Add(i, new Carrier(i));
            var allCarriers = registered.Values.ToArray();
            var steadyBuffer = new BattleEffectFrameSnapshot<long, Carrier>();
            Action legacy = () =>
            {
                for (int i = 0; i < stableCarriers; i++)
                    LegacyRegister(registered, i, allCarriers[i]);
                GC.KeepAlive(registered.ToArray());
            };
            Action optimized = () =>
            {
                for (int i = 0; i < stableCarriers; i++)
                    IndexedRegister(registered, i, allCarriers[i]);
                var snapshot = steadyBuffer.Capture(registered);
                try
                {
                    if (snapshot.Count != stableCarriers)
                        throw new Exception("Host lost a registered carrier");
                }
                finally { steadyBuffer.Release(snapshot); }
            };
            long oldBytes = Allocated(legacy, stableFrames);
            long newBytes = Allocated(optimized, stableFrames);
            Check(oldBytes > 0 && newBytes == 0,
                "stable native carrier sampling avoids per-carrier LINQ and whole-dictionary arrays");
            Console.WriteLine("INFO: " + stableFrames + " checkpoints × " + stableCarriers +
                " carriers: previous alias scan + ToArray allocated " + oldBytes +
                " bytes; production alias pruning + reusable capture " + newBytes +
                " bytes (managed allocations, warmed)");
            Console.WriteLine("PASS: " + assertions + " linked native fighter carrier capture checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
