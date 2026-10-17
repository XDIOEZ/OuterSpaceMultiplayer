using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // ACKs often retire exactly one pending battle frame. The previous
    // Keys.Where(...).ToList() allocated an iterator, closure and List even
    // when nothing (or only one revision) needed removal.
    internal static class BattleFrameWindowPruner
    {
        internal static int DiscardThrough<T>(Dictionary<long, T> frames,
            long acknowledgedRevision, Func<T, int> nativeLength, ref int characters)
        {
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            if (nativeLength == null) throw new ArgumentNullException(nameof(nativeLength));

            bool found = false;
            long first = 0;
            List<long> remaining = null;
            // Snapshot all keys first. Removing while enumerating Dictionary
            // invalidates the enumerator on Unity/Mono even where modern
            // .NET permits some mutations.
            foreach (long revision in frames.Keys)
            {
                if (revision > acknowledgedRevision) continue;
                if (!found) { first = revision; found = true; }
                else
                {
                    if (remaining == null) remaining = new List<long>();
                    remaining.Add(revision);
                }
            }
            if (!found) return 0;

            // Preserve the previous enumeration order and partial effects:
            // subtract NativeJson.Length BEFORE removing each frame. If the
            // accessor fails, preceding frames remain removed and the failing
            // frame is still pending, just as in the original foreach.
            characters -= nativeLength(frames[first]);
            frames.Remove(first);
            int removed = 1;
            if (remaining != null)
                foreach (long revision in remaining)
                {
                    characters -= nativeLength(frames[revision]);
                    frames.Remove(revision);
                    removed++;
                }
            return removed;
        }
    }
}
