using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The simulator sends a new checkpoint every ~0.1s while up to twelve
    // earlier revisions are awaiting host ACK. The old Keys.Max() extension
    // boxed Dictionary.KeyCollection's enumerator on some Unity/.NET targets.
    // Scan the live pending window once without LINQ or cached high-water
    // state: an ACK can remove the highest pending revision at any time.
    internal static class BattleFrameRevisionMax
    {
        internal static long Highest<TFrame>(Dictionary<long, TFrame> pending)
        {
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            bool found = false;
            long highest = 0;
            foreach (long revision in pending.Keys)
            {
                if (!found || revision > highest)
                {
                    highest = revision;
                    found = true;
                }
            }
            // Preserve Enumerable.Max's failure for an empty sequence. The
            // production caller already skips this method when Count == 0.
            if (!found) throw new InvalidOperationException("Sequence contains no elements");
            return highest;
        }
    }
}
