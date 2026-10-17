using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    // A map transaction is atomic. Duplicated personal updates could individually
    // pass the preflight against the same old revision, then fail after the first
    // one has already modified native inventory/credits/tech.
    internal static class MapTransactionPlayerValidation
    {
        internal static void RequireUnique(IList<PlayerUpdate> players)
        {
            if (players == null)
                throw new InvalidDataException("地图事务缺少个人变更列表。");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlayerUpdate update in players)
                if (update == null || String.IsNullOrWhiteSpace(update.PlayerId) ||
                    !seen.Add(update.PlayerId))
                    throw new InvalidDataException("地图事务包含空、无效或重复的个人更新，整组未应用。");
        }
    }
}
