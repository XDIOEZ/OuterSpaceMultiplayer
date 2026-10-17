using System;
using System.Collections.Generic;
using FreeWorldMultiplayer;

internal static class DeferredCommandReplyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var routes = new Dictionary<string, string>();
        var immediate = new CommandResult { Success = true, Applied = true };
        var rejected = new CommandResult { Success = false };
        var pending = new CommandResult { Success = true, Applied = false };

        for (int i = 0; i < 10000; i++)
        {
            DeferredCommandReplyMap.Update(routes, "sync:" + i, "peerA", immediate, false);
            DeferredCommandReplyMap.Update(routes, "reject:" + i, "peerA", rejected, false);
        }
        check(routes.Count == 0, "10k completed and 10k rejected commands keep no reply routes");
        DeferredCommandReplyMap.Update(routes, "waiting", "peerA", pending, false);
        check(routes.Count == 0, "queued result alone is not enough without a remote execution");

        DeferredCommandReplyMap.Update(routes, "waiting", "peerA", pending, true);
        check(routes.Count == 1 && routes["waiting"] == "peerA", "remote pending execution retains reply target");
        DeferredCommandReplyMap.Update(routes, "waiting", "peerA", pending, true);
        check(routes.Count == 1, "retry with identical command ID does not duplicate reply route");

        DeferredCommandReplyMap.Update(routes, "other", "peerB", pending, true);
        DeferredCommandReplyMap.RemoveForPeer(routes, "peerA");
        check(!routes.ContainsKey("waiting") && routes.Count == 1 &&
            routes["other"] == "peerB", "disconnected peer routes removed without disturbing others");
        DeferredCommandReplyMap.Update(routes, "other", "peerB", immediate, false);
        check(routes.Count == 0, "terminal reply frees the remaining route");

        DeferredCommandReplyMap.Update(routes, "send-failed", "peerA", pending, true);
        DeferredCommandReplyMap.Update(routes, "send-failed", "peerA", rejected, true);
        check(routes.Count == 0, "failed dispatch does not leave a false pending reply route");

        for (int i = 0; i < 1000; i++)
            DeferredCommandReplyMap.Update(routes, "async:" + i, "peerA", pending, true);
        DeferredCommandReplyMap.RemoveForPeer(routes, "peerA");
        check(routes.Count == 0, "disconnect reclaims 1k outstanding remote reply routes");
    }
}
