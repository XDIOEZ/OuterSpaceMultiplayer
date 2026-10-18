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
    private static System.Collections.IDictionary PoseBuffers(GameBridge bridge)
    {
        return (System.Collections.IDictionary)typeof(GameBridge)
            .GetField("remoteFleetPoses", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(bridge);
    }
    private static void PushPose(GameBridge bridge, long uid, string galaxy,
        float x, float angle, long sequence, bool reset = false)
    {
        Type poseType = typeof(GameBridge).GetNestedType("FleetPose", BindingFlags.NonPublic);
        object pose = Activator.CreateInstance(poseType);
        poseType.GetField("FleetId").SetValue(pose, uid);
        poseType.GetField("PlayerId").SetValue(pose, "guest");
        poseType.GetField("GalaxyId").SetValue(pose, galaxy);
        poseType.GetField("X").SetValue(pose, x);
        poseType.GetField("Y").SetValue(pose, 7f);
        poseType.GetField("Angle").SetValue(pose, angle);
        typeof(GameBridge).GetMethod("PushRemoteFleetPose",
            BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(bridge, new object[] { pose, sequence, reset });
    }
    private static object PoseValue(object buffer, string member)
    {
        return buffer.GetType().GetField(member,
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(buffer);
    }
    private static float SynchronizedX(GameBridge bridge, long uid)
    {
        object[] args = { uid, new UnityEngine.Vector3(), 0f };
        bool exists = (bool)typeof(GameBridge)
            .GetMethod("TryGetSynchronizedFleetPose",
                BindingFlags.NonPublic | BindingFlags.Instance).Invoke(bridge, args);
        Check(exists, "actual game bridge interpolates an existing native fleet");
        return ((UnityEngine.Vector3)args[1]).x;
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

            // True production PresentRemoteFleetProxy integration: a dirty
            // unrelated content field must not rebuild unchanged formation,
            // whereas ship resource/UID/order changes must rebuild it once.
            RemotePlayerFleetProxyView visualProxy = proxies[1001];
            FWFleet visualFleet = visualProxy.BoundFleet;
            string initialKey = visualProxy.VisualKey;
            int visualBuilds = visualProxy.Visual.CreateCalls;
            visualProxy.ContentDirty = true;
            bridge.PresentRemoteFleetProxy(visualProxy);
            Check(ReferenceEquals(initialKey, visualProxy.VisualKey) &&
                visualProxy.Visual.CreateCalls == visualBuilds && !visualProxy.ContentDirty,
                "real dirty-but-unchanged fleet reuses original key and native visual");
            visualFleet.ships[0].id = "destroyer|α:2";
            visualProxy.ContentDirty = true;
            bridge.PresentRemoteFleetProxy(visualProxy);
            string exactResourceKey = visualFleet.moveSeed + ":" + "test-mothership" + ":" +
                String.Join("|", visualFleet.ships.Select(ship => ship.getUID() + ":" + ship.id));
            Check(visualProxy.VisualKey == exactResourceKey &&
                visualProxy.Visual.CreateCalls == ++visualBuilds,
                "native resource change preserves exact legacy key and rebuilds visual once");
            string stableChangedKey = visualProxy.VisualKey;
            visualProxy.ContentDirty = true;
            bridge.PresentRemoteFleetProxy(visualProxy);
            Check(visualProxy.Visual.CreateCalls == visualBuilds &&
                ReferenceEquals(visualProxy.VisualKey, stableChangedKey),
                "subsequent unchanged dirty frame skips rebuilding native formation");
            visualFleet.ships.Add(new FWAirShip { Uid = long.MaxValue, id = null });
            visualProxy.ContentDirty = true;
            bridge.PresentRemoteFleetProxy(visualProxy);
            Check(visualProxy.Visual.CreateCalls == ++visualBuilds &&
                visualProxy.VisualKey == visualFleet.moveSeed + ":test-mothership:" +
                    String.Join("|", visualFleet.ships.Select(ship => ship.getUID() + ":" + ship.id)),
                "adding a native ship with null resource preserves old visual formatting");
            visualFleet.ships.Reverse();
            visualProxy.ContentDirty = true;
            bridge.PresentRemoteFleetProxy(visualProxy);
            Check(visualProxy.Visual.CreateCalls == ++visualBuilds &&
                visualProxy.VisualKey == visualFleet.moveSeed + ":test-mothership:" +
                    String.Join("|", visualFleet.ships.Select(ship => ship.getUID() + ":" + ship.id)),
                "formation order remains part of the native visual cache identity");
            visualProxy.ContentDirty = true;
            bridge.PresentRemoteFleetProxy(visualProxy);
            Check(visualProxy.Visual.CreateCalls == visualBuilds &&
                !visualProxy.ContentDirty,
                "rechecked matching fleet content does not spawn duplicate native visuals");

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

            // Exercise the ACTUAL PushRemoteFleetPose production method, not
            // only a stand-alone numerical model. Verify its private cache
            // holds one object across movement, snapping and resync.
            var poseBuffers = PoseBuffers(bridge);
            UnityEngine.Time.unscaledTimeAsDouble = 100;
            PushPose(bridge, 1000, "alpha", 10, 0, 1);
            object originalPoseBuffer = poseBuffers[1000L];
            Check(originalPoseBuffer != null && poseBuffers.Count == 1,
                "first received remote fleet creates exactly one pose checkpoint");
            UnityEngine.Time.unscaledTimeAsDouble = 100.1;
            PushPose(bridge, 1000, "alpha", 50, 90, 2);
            Check(ReferenceEquals(originalPoseBuffer, poseBuffers[1000L]) &&
                (long)PoseValue(originalPoseBuffer, "Sequence") == 2 &&
                (double)PoseValue(originalPoseBuffer, "Duration") > 0,
                "second authoritative pose reuses existing native checkpoint");
            UnityEngine.Time.unscaledTimeAsDouble = 100.15;
            float interpolatedX = SynchronizedX(bridge, 1000);
            Check(interpolatedX > 10 && interpolatedX < 50,
                "real production interpolation still progresses between received poses");
            PushPose(bridge, 1000, "alpha", 500, 180, 1);
            Check(ReferenceEquals(poseBuffers[1000L], originalPoseBuffer) &&
                (long)PoseValue(originalPoseBuffer, "Sequence") == 2,
                "out-of-order native pose cannot overwrite the current replica state");
            bridge.Context.Paused = true;
            UnityEngine.Time.unscaledTimeAsDouble = 100.18;
            float frozenX = SynchronizedX(bridge, 1000);
            UnityEngine.Time.unscaledTimeAsDouble = 105;
            Check(SynchronizedX(bridge, 1000) == frozenX,
                "host pause freezes the real remote pose presentation time");
            bridge.Context.Paused = false;
            UnityEngine.Time.unscaledTimeAsDouble = 105.05;
            Check(SynchronizedX(bridge, 1000) >= frozenX,
                "resuming synchronized time does not reverse a fleet's progress");
            UnityEngine.Time.unscaledTimeAsDouble = 106;
            PushPose(bridge, 1000, "beta", 90, -20, 3);
            Check(ReferenceEquals(poseBuffers[1000L], originalPoseBuffer) &&
                (double)PoseValue(originalPoseBuffer, "Duration") == 0 &&
                SynchronizedX(bridge, 1000) == 90,
                "cross-galaxy fleet movement snaps in place without creating another object");
            UnityEngine.Time.unscaledTimeAsDouble = 107;
            PushPose(bridge, 1000, "beta", -40, 11, 1, reset: true);
            Check(ReferenceEquals(poseBuffers[1000L], originalPoseBuffer) &&
                (long)PoseValue(originalPoseBuffer, "Sequence") == 1 &&
                SynchronizedX(bridge, 1000) == -40,
                "full galaxy resync accepts a lower sequence and snaps to its authoritative pose");
            typeof(GameBridge).GetMethod("DisposeRemoteFleetProxies",
                BindingFlags.NonPublic | BindingFlags.Instance).Invoke(bridge, null);
            Check(poseBuffers.Count == 0,
                "room cleanup discards all pose checkpoints for the previous multiplayer world");
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
