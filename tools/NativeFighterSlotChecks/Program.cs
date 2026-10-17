using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Slot
    {
        internal int Id;
        internal long Reads;
        internal readonly object Identity = new object();
        internal int Index
        {
            get { Reads++; return Id; }
        }
        internal Slot(int id) { Id = id; }
    }

    private static readonly Func<Slot, int> GetIndex = s => s.Index;
    private static int checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static string Outcome(Func<Slot> action, out Slot result)
    {
        result = null;
        try { result = action(); return "ok"; }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }
    private static void EqualLookups(Slot[] native, int[] incoming, string context)
    {
        NativeFighterSlotLookup<Slot> index =
            native.Length >= 8 ? NativeFighterSlotLookup<Slot>.TryCreate(native, GetIndex) : null;
        foreach (int key in incoming)
        {
            bool original = native.Any(s => s.Index == key);
            bool optimized = index == null
                ? native.Any(s => s.Index == key) : index.Contains(key);
            Check(original == optimized, context + " Any slot " + key);
            Slot oldSlot, newSlot;
            string oldResult = Outcome(() => native.Single(s => s.Index == key), out oldSlot);
            string newResult = Outcome(() => index == null
                ? native.Single(s => s.Index == key) : index.Single(key), out newSlot);
            Check(oldResult == newResult && ReferenceEquals(oldSlot, newSlot),
                context + " Single slot " + key + " old=" + oldResult + " new=" + newResult);
        }
    }

    private static void Reset(Slot[] slots)
    { foreach (Slot slot in slots) if (slot != null) slot.Reads = 0; }
    private static long Reads(Slot[] slots)
    { return slots.Sum(s => s == null ? 0 : s.Reads); }

    private static int Main()
    {
        try
        {
            Check(Throws<ArgumentNullException>(() =>
                    NativeFighterSlotLookup<Slot>.TryCreate(null, GetIndex)),
                "missing native wing slots are rejected");
            Check(Throws<ArgumentNullException>(() =>
                    NativeFighterSlotLookup<Slot>.TryCreate(new Slot[0], null)),
                "missing native slot selector is rejected");
            for (int count = 0; count <= 12; count++)
            {
                var slots = Enumerable.Range(0, count).Select(i => new Slot(i)).ToArray();
                var requested = Enumerable.Range(-1, count + 2).ToArray();
                EqualLookups(slots, requested, "size threshold " + count);
            }
            for (int count = 31; count <= 256; count += 37)
            {
                var slots = Enumerable.Range(0, count).Select(i => new Slot(1000 + i)).ToArray();
                EqualLookups(slots, new[] { 999, 1000, 1000 + count / 2, 1000 + count - 1, 1000 + count },
                    "large native wing " + count);
            }
            var dup = new[] { new Slot(3), new Slot(4), new Slot(5), new Slot(3),
                new Slot(6), new Slot(7), new Slot(7), new Slot(8), new Slot(9) };
            EqualLookups(dup, new[] { 3, 4, 5, 6, 7, 8, 9, 10 },
                "duplicated target slot and unrelated duplicate");
            var indexedDup = NativeFighterSlotLookup<Slot>.TryCreate(dup, GetIndex);
            Check(indexedDup.Contains(3) && indexedDup.Contains(7)
                && indexedDup.Contains(9) && !indexedDup.Contains(100),
                "Any-style membership allows duplicate matching slots");
            Check(Throws<InvalidOperationException>(() => indexedDup.Single(3))
                && Throws<InvalidOperationException>(() => indexedDup.Single(7)),
                "Single-style selection rejects duplicate requested slot");
            Check(ReferenceEquals(indexedDup.Single(4), dup[1]),
                "unrelated native duplicates do not interfere with requested slot");
            var nullSlot = new[] { new Slot(1), new Slot(2), null, new Slot(3),
                new Slot(4), new Slot(5), new Slot(6), new Slot(7), new Slot(8) };
            Check(NativeFighterSlotLookup<Slot>.TryCreate(nullSlot, GetIndex) == null,
                "malformed native arrays stay on the old linear lookup path");
            Check(nullSlot.Any(s => s.Index == 1)
                && Throws<NullReferenceException>(() => nullSlot.Any(s => s.Index == 7))
                && Throws<NullReferenceException>(() => nullSlot.Single(s => s.Index == 1)),
                "old Any early match and Single null failure are preserved by fallback");

            // A per-frame native index is not shared across new Unity wing
            // instances. Replacing a slot under the same ID must use the new
            // reference, never the object from the preceding checkpoint.
            var originalNative = Enumerable.Range(0, 16).Select(i => new Slot(i)).ToArray();
            var previousFrame = NativeFighterSlotLookup<Slot>.TryCreate(originalNative, GetIndex);
            Slot oldRef = previousFrame.Single(0);
            var replacement = new Slot(0);
            originalNative[0] = replacement;
            var nextFrame = NativeFighterSlotLookup<Slot>.TryCreate(originalNative, GetIndex);
            Check(ReferenceEquals(previousFrame.Single(0), oldRef)
                && ReferenceEquals(nextFrame.Single(0), replacement)
                && !ReferenceEquals(oldRef, replacement),
                "new native wing frame observes re-instantiated slot reference");

            Console.WriteLine("INFO: standalone native slot membership and Single compatibility established");

            var random = new Random(20261018);
            for (int iteration = 0; iteration < 2400; iteration++)
            {
                int size = random.Next(0, 65);
                var slots = new Slot[size];
                for (int i = 0; i < size; i++) slots[i] = new Slot(random.Next(0, 50));
                var requests = new int[6];
                for (int i = 0; i < requests.Length; i++) requests[i] = random.Next(-1, 51);
                EqualLookups(slots, requests, "random same-frame slot lookup " + iteration);
            }

            // The wire DTO for a validated wing and its native slot array both
            // contain up to 256 slots. Previously ResolveReplicaWing.Any
            // scanned from the beginning per slot, and Apply...Single scanned
            // the entire array per slot to detect duplicates. The new index
            // performs one native scan for both phases per wing and frame.
            const int wings = 8, slotsPerWing = 128, frames = 60;
            var liveWings = new Slot[wings][];
            for (int wing = 0; wing < wings; wing++)
                liveWings[wing] = Enumerable.Range(0, slotsPerWing)
                    .Select(i => new Slot(wing * 1000 + i)).ToArray();
            long legacyReads = 0, indexedReads = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                for (int wing = 0; wing < wings; wing++)
                {
                    Slot[] slots = liveWings[wing];
                    Reset(slots);
                    for (int i = 0; i < slotsPerWing; i++)
                        if (!slots.Any(s => s.Index == wing * 1000 + i))
                            throw new InvalidOperationException("Old native slot unexpectedly missing");
                    for (int i = 0; i < slotsPerWing; i++)
                        if (!ReferenceEquals(slots.Single(s => s.Index == wing * 1000 + i), slots[i]))
                            throw new InvalidOperationException("Old native slot mismatch");
                    legacyReads += Reads(slots);
                    Reset(slots);
                    var index = NativeFighterSlotLookup<Slot>.TryCreate(slots, GetIndex);
                    for (int i = 0; i < slotsPerWing; i++)
                        if (!index.Contains(wing * 1000 + i)) throw new InvalidOperationException("Index missing slot");
                    for (int i = 0; i < slotsPerWing; i++)
                        if (!ReferenceEquals(index.Single(wing * 1000 + i), slots[i]))
                            throw new InvalidOperationException("Index resolved wrong reference");
                    indexedReads += Reads(slots);
                }
            }
            long expectedOld = (long)frames * wings *
                (slotsPerWing * (slotsPerWing + 1) / 2 + slotsPerWing * slotsPerWing);
            long expectedNew = (long)frames * wings * slotsPerWing;
            Check(legacyReads == expectedOld && indexedReads == expectedNew,
                "production index resolves both validation and assignment with one native scan per wing");
            Console.WriteLine("INFO: " + frames + " frames × " + wings +
                " wings × " + slotsPerWing + " native slots: old slot-index reads " +
                legacyReads + ", new frame-local index reads " + indexedReads);

            Console.WriteLine("PASS: " + checks + " linked native fighter slot lookup checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
