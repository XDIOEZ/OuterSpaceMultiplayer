using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    // A reply route is only needed while the remote map simulator owns
    // an outstanding execution. Immediate results have already been sent.
    internal static class DeferredCommandReplyMap
    {
        internal static void Update(Dictionary<string, string> routes, string key, string peer,
            CommandResult immediate, bool isPending)
        {
            if (routes == null) throw new ArgumentNullException(nameof(routes));
            if (isPending && immediate != null && immediate.Success && !immediate.Applied)
                routes[key] = peer;
            else
                routes.Remove(key);
        }

        internal static void RemoveForPeer(Dictionary<string, string> routes, string peer)
        {
            if (routes == null) throw new ArgumentNullException(nameof(routes));
            foreach (string key in routes.Where(x => x.Value == peer).Select(x => x.Key).ToList())
                routes.Remove(key);
        }
    }
}
