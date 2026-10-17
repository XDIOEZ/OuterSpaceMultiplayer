namespace FreeWorldMultiplayer
{
    // A native fighter wing declares at most 256 slots, indexed 0..255.
    // The received authoritative state already rejects out-of-range slots
    // immediately after its duplicate check. Four 64-bit masks replace a
    // separately allocated HashSet<int> for EVERY validated wing.
    internal struct BattleFighterSlotBitSet
    {
        private ulong first, second, third, fourth;

        internal bool TryAdd(int index)
        {
            // Preserve the existing guard order:
            // !slots.Add(index) || index < 0 || index > 255.
            // Invalid indices have not been seen before and are rejected by
            // the following range check, before another slot is inspected.
            if (index < 0 || index > 255) return true;

            ulong mask = 1UL << (index & 63);
            if (index < 64) return Add(ref first, mask);
            if (index < 128) return Add(ref second, mask);
            if (index < 192) return Add(ref third, mask);
            return Add(ref fourth, mask);
        }

        private static bool Add(ref ulong bits, ulong mask)
        {
            if ((bits & mask) != 0) return false;
            bits |= mask;
            return true;
        }
    }
}
