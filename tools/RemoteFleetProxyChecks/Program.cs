using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + message);
        checks++;
    }
    private static FWFleet CreateFleet(long id, float x, string name)
    {
        return new FWFleet
        {
            Uid = id, Name = name, moveSeed = 3, factionUid = 1,
            pos = new FWGalaxyPos { galaxyId = "alpha", gx = x, gy = 7, angle = 13 },
            ships = new List<FWAirShip> { new FWAirShip { Uid = id + 50000, id = "frigate" } }
        };
    }
    private static Dictionary<long, RemotePlayerFleetProxyView> Proxies(GameBridge bridge)
    {
        return (Dictionary<long, RemotePlayerFleetProxyView>)typeof(GameBridge)
            .GetField("remoteFleetProxies", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(bridge);
    }
    private static void Refresh(GameBridge bridge, GalaxyScenePlayerFleetManager manager, string galaxy)
    {
        typeof(GameBridge).GetMethod("RefreshRemoteFleetProxies", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(bridge, new object[] { manager, galaxy });
    }
    public static int Main()
    {
        try
        {
            const int fleetCount = 96, frames = 300;
            var bridge = new GameBridge();
            GameBridge.Active = bridge;
            bridge.Players.Add("host", new FakePlayer());
            bridge.Players.Add("guest", new FakePlayer());
            bridge.Online.Add("host");
            bridge.Online.Add("guest");
            for (int i = 0; i < fleetCount; ++i)
            {
                FWFleet fleet = CreateFleet(1000 + i, i, "fleet-" + i);
                bridge.Players["guest"].Fleets.Add(fleet);
                bridge.Context.Owners.Add(fleet.Uid, "guest");
            }
            var manager = new GalaxyScenePlayerFleetManager();
            Refresh(bridge, manager, "alpha");
            Dictionary<long, RemotePlayerFleetProxyView> proxies = Proxies(bridge);
            Check(proxies.Count == fleetCount && bridge.OwnedFleetQueries == 1,
                "visibility refresh indexes remote fleets once per owning player");
            Check(proxies.All(pair => ReferenceEquals(pair.Value.BoundFleet,
                    bridge.Players["guest"].Fleets[(int)(pair.Key - 1000)])),
                "every proxy binds the exact authoritative fleet object");
            string resolvedOwner;
            long resolvedUid;
            Check(bridge.TryResolvePlayerFleetProxy(1000, out resolvedOwner, out resolvedUid)
                && resolvedOwner == "guest" && resolvedUid == 1000,
                "original player fleet UID still maps to the owning player for interaction");
            int interactions = 0;
            bridge.Context.RequestPlayerFleetInteraction = (player, uid) =>
            {
                if (player == "guest" && uid == 1000) interactions++;
            };
            bridge.OpenRemoteFleetInteraction(proxies[1000].Native);
            Check(interactions == 1, "native proxy click invokes the existing real-player interaction entry");

            bridge.OwnedFleetQueries = 0;
            for (int frame = 0; frame < frames; frame++)
                foreach (RemotePlayerFleetProxyView proxy in proxies.Values)
                    bridge.PresentRemoteFleetProxy(proxy);
            Check(bridge.OwnedFleetQueries == 0,
                "28,800 presentation updates require zero fleet-list rescans");
            Check(proxies.Values.All(proxy => !proxy.ContentDirty && proxy.Visual.CreateCalls == 1),
                "unchanged fleet content renders without rebuilding its native visual");
            Check(proxies[1000].Visual.PresentationCalls == frames,
                "bound fleet proxies continue presenting position and heading every frame");
            Console.WriteLine("INFO: " + (frames * fleetCount) +
                " production presentation calls, zero OwnedFleets rescans (previously one per call)");

            RemotePlayerFleetProxyView first = proxies[1000];
            FWFleet replacement = CreateFleet(1000, 500, "replacement");
            bridge.Players["guest"].Fleets[0] = replacement;
            Refresh(bridge, manager, "alpha");
            Check(ReferenceEquals(first, proxies[1000]) && ReferenceEquals(first.BoundFleet, replacement)
                && first.ContentDirty && first.VisualKey == null,
                "replacement under same UID rebinds the proxy and invalidates cached visual identity");
            bridge.OwnedFleetQueries = 0;
            bridge.PresentRemoteFleetProxy(first);
            Check(bridge.OwnedFleetQueries == 0 && first.Visual.CreateCalls == 2
                && first.Visual.LastName.Contains("replacement")
                && first.transform.localPosition.x == 500,
                "rebinding recomputes visual once and uses new fleet name and coordinates");
            Check(first.BoundFleet.getUID() == first.FleetUid, "bound UID stays canonical");

            int rendered = first.Visual.PresentationCalls;
            bridge.Context.Owners[1000] = "intruder";
            bridge.PresentRemoteFleetProxy(first);
            Check(first.Visual.PresentationCalls == rendered,
                "ownership change immediately blocks stale presentation before next refresh");
            bool rejected = false;
            try { bridge.OpenRemoteFleetInteraction(first.Native); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && interactions == 1,
                "proxy click rejects stale ownership rather than handing out another player's controls");
            bridge.Context.Owners[1000] = "guest";
            bridge.Online.Remove("guest");
            bridge.PresentRemoteFleetProxy(first);
            Check(first.Visual.PresentationCalls == rendered,
                "offline peer stops presenting before proxy cleanup");
            Check(!bridge.TryResolvePlayerFleetProxy(1000, out resolvedOwner, out resolvedUid),
                "offline players are not considered valid native interaction targets");
            Refresh(bridge, manager, "alpha");
            Check(proxies.Count == 0 && first.BoundFleet == null && first.Bridge == null
                && first.gameObject.Destroyed && first.Visual.Disposed,
                "offline refresh destroys proxies and releases every retained fleet reference");

            bridge.Online.Add("guest");
            Refresh(bridge, manager, "alpha");
            Check(proxies.Count == fleetCount && !ReferenceEquals(proxies[1000], first),
                "reconnected player gets new presentation shells, not stale prior proxies");
            var next = proxies[1000];
            replacement.pos.galaxyId = "beta";
            Refresh(bridge, manager, "alpha");
            Check(!proxies.ContainsKey(1000) && next.BoundFleet == null,
                "leaving the visible galaxy removes the bound proxy");
            Check(!bridge.TryResolvePlayerFleetProxy(1000, out _, out _),
                "removed proxy cannot be used for multiplayer fleet interaction");
            RemotePlayerFleetProxyView remaining = proxies[1001];
            UnityEngine.Object.Destroy(remaining.gameObject);
            Check(remaining.BoundFleet == null && remaining.Bridge == null && remaining.Visual.Disposed,
                "native scene teardown also releases the bound fleet before a visibility refresh");
            Console.WriteLine("PASS: " + checks + " linked NativeRemoteFleetProxy lifecycle checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
