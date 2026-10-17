using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class MapCacheExpiryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var host = new MultiplayerSession();
        var sendTo = new List<string>();
        var disconnected = new List<string>();
        var warnings = new List<string>();
        host.SeedPeer("bad", "bad-peer");
        host.SeedPeer("good", "good-peer");
        host.SeedOffer("expired-bad", "bad", 59, 512);
        host.SeedOffer("expired-good", "good", 59, 256);
        host.OnDisconnect((peer, reason) => disconnected.Add(peer));
        host.SendHandler = (peer, kind, payload) =>
        {
            if (peer == "bad-peer") throw new IOException("simulated queue overflow");
            sendTo.Add(peer + ":" + kind);
        };
        host.MessageFaulted += message => warnings.Add(message);

        host.AdvanceCache(0.5);
        check(host.PendingOffers == 2 && host.RetainedBytes == 768,
            "cache offers remain pending before 60 real seconds");
        host.AdvanceCache(0.5);
        check(host.PendingOffers == 0 && host.RetainedBytes == 0,
            "all expired offers release cached bytes even if one send fails");
        check(disconnected.Count == 1 && disconnected[0] == "bad-peer",
            "full transport queue isolates the offending peer");
        check(sendTo.Count == 1 && sendTo[0] == "good-peer:error",
            "healthy peer receives notice in the same host cache tick");
        check(warnings.Count == 1 && warnings[0].Contains("断开"),
            "host records isolated cache failure without reporting global map commit success");
        host.AdvanceCache(1);
        check(disconnected.Count == 1 && sendTo.Count == 1,
            "expired offers cannot produce repeat disconnections or notifications");

        var absent = new MultiplayerSession();
        absent.SeedOffer("orphan", "gone", 60, 42);
        absent.SendHandler = (peer, kind, payload) => throw new InvalidOperationException("unexpected send");
        absent.AdvanceCache(1);
        check(absent.RetainedBytes == 0 && absent.PendingOffers == 0,
            "disconnected lookup skips optional notice and releases lease");

        var closed = new MultiplayerSession();
        closed.SeedPeer("closed", "closed-peer");
        closed.SeedPeer("ok", "other-peer");
        closed.SeedOffer("a", "closed", 60, 12);
        closed.SeedOffer("b", "ok", 60, 17);
        int healthySends = 0, closedAttempts = 0, warningCount = 0;
        closed.OnDisconnect((peer, reason) =>
        {
            closedAttempts++;
            throw new IOException("simulated closed transport");
        });
        closed.SendHandler = (peer, kind, payload) =>
        {
            if (peer == "closed-peer") throw new InvalidOperationException("connection no longer exists");
            healthySends++;
        };
        closed.MessageFaulted += _ => { warningCount++; throw new Exception("fault observer is unavailable"); };
        closed.AdvanceCache(0.1);
        check(closedAttempts == 1 && healthySends == 1 && closed.RetainedBytes == 0,
            "broken disconnect and observer cannot stop another peer or leak cache bytes");
        check(warningCount == 1, "failed transport cleanup warns once per expired offer");

        var corrupted = new MultiplayerSession();
        corrupted.SeedPeer("test", "one");
        corrupted.SeedOffer("x", "test", 60, 4);
        corrupted.SendHandler = (peer, kind, payload) => throw new FormatException("not a transport failure");
        bool programmerFaultPropagates = false;
        try { corrupted.AdvanceCache(0); }
        catch (FormatException) { programmerFaultPropagates = true; }
        check(programmerFaultPropagates,
            "unrelated serialization faults are not disguised as optional transport errors");

        var localTimeout = new MultiplayerSession();
        localTimeout.SeedWaiting("view", 123, 59);
        bool realMapTimeoutPropagates = false;
        try { localTimeout.AdvanceCache(1); }
        catch (TimeoutException ex) { realMapTimeoutPropagates = ex.Message.Contains("123"); }
        check(realMapTimeoutPropagates,
            "authoritative local map loading timeouts still fail rather than disappearing");

        var warning = new MultiplayerSession();
        warning.SeedWarning("derived local cache unavailable");
        warning.MessageFaulted += _ => throw new InvalidOperationException("optional warning observer failed");
        warning.SeedPeer("okay", "okay-peer");
        warning.SeedOffer("okay-view", "okay", 60, 8);
        int continued = 0;
        warning.SendHandler = (_, __, ___) => continued++;
        warning.AdvanceCache(0);
        check(continued == 1 && warning.RetainedBytes == 0,
            "failed advisory cache-warning callback cannot prevent cache tick");
    }
}
