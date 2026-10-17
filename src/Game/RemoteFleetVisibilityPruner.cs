using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // RefreshRemoteFleetProxies runs while the galaxy view is active. Keep
    // its remove-after-enumeration semantics, but do not allocate LINQ
    // closures, iterators or arrays on frames where every proxy remains.
    internal static class RemoteFleetVisibilityPruner
    {
        internal static List<long> FindMissing<TProxy>(
            Dictionary<long, TProxy> proxies, HashSet<long> visibleUids)
        {
            List<long> missing = null;
            foreach (long uid in proxies.Keys)
            {
                if (visibleUids.Contains(uid)) continue;
                if (missing == null) missing = new List<long>();
                missing.Add(uid);
            }
            return missing;
        }
    }
}
