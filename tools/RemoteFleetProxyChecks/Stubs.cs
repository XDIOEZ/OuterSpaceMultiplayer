using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        { return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
    }
    public struct Quaternion { public static Quaternion identity { get { return new Quaternion(); } } }
    public static class Mathf
    {
        public static float Clamp01(float value) { return Math.Max(0, Math.Min(1, value)); }
        public static float LerpAngle(float a, float b, float fraction) { return a + (b - a) * fraction; }
    }
    public static class Time { public static double unscaledTimeAsDouble = 10; }
    public class Object
    {
        public static GameObject Instantiate(GameObject prefab, Transform parent, bool worldPositionStays)
        { return new GameObject { Parent = parent }; }
        public static void Destroy(GameObject gameObject)
        {
            gameObject.Destroyed = true;
            foreach (MonoBehaviour component in gameObject.Components)
                component.GetType().GetMethod("OnDestroy", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(component, null);
        }
    }
    public sealed class Transform
    {
        public Vector3 localPosition;
        public Quaternion localRotation;
    }
    public class MonoBehaviour : Object
    {
        public bool enabled = true;
        public GameObject gameObject;
        public Transform transform { get { return gameObject.transform; } }
        public T GetComponent<T>() where T : MonoBehaviour { return gameObject.GetComponent<T>(); }
    }
    public class GameObject : Object
    {
        private readonly Dictionary<Type, MonoBehaviour> components = new Dictionary<Type, MonoBehaviour>();
        public readonly Transform transform = new Transform();
        public Transform Parent;
        public string name;
        public bool Destroyed;
        public IEnumerable<MonoBehaviour> Components { get { return components.Values; } }
        public T GetComponent<T>() where T : MonoBehaviour
        {
            MonoBehaviour value;
            return components.TryGetValue(typeof(T), out value) ? (T)value : null;
        }
        public T AddComponent<T>() where T : MonoBehaviour, new()
        {
            var instance = new T { gameObject = this };
            components.Add(typeof(T), instance);
            return instance;
        }
    }
}

namespace HarmonyLib
{
    public class Harmony
    {
        public void Patch(MethodInfo method, HarmonyMethod prefix) { }
    }
    public class HarmonyMethod
    {
        public HarmonyMethod(Type type, string name) { }
    }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name) { return type.GetMethod(name); }
    }
}

namespace Script.FreeWorldAirShip.Galaxy.NPCFleet
{
    public sealed class GalaxyNPCFleet : UnityEngine.MonoBehaviour { }
}

namespace FreeWorldMultiplayer
{
    public sealed class FWGalaxyPos { public string galaxyId; public float gx, gy, angle; }
    public sealed class FWAirShip
    {
        public long Uid;
        public string id;
        public long getUID() { return Uid; }
    }
    public sealed class FWFleet
    {
        public long Uid;
        public string Name;
        public int moveSeed;
        public long factionUid;
        public FWGalaxyPos pos;
        public List<FWAirShip> ships = new List<FWAirShip>();
        public long getUID() { return Uid; }
        public string getName() { return Name; }
    }
    public sealed class AirShipFleetInfoBean
    {
        public FWFleet bean;
    }
    public sealed class GalaxyPlayerFleet : UnityEngine.MonoBehaviour
    {
        public AirShipFleetInfoBean fleetbean;
    }
    public sealed class GalaxyScenePlayerFleetManager
    {
        public readonly UnityEngine.Transform transform = new UnityEngine.Transform();
    }
    public sealed class GalaxyFleetVisual : UnityEngine.MonoBehaviour
    {
        public int CreateCalls, PresentationCalls;
        public bool Disposed;
        public string LastName;
        public struct PresentationState { public float Angle; public bool InBattle; }
        public void InitWidget() { }
        public void SetNameplate(string text, string kind) { LastName = text; }
        public IEnumerable<object> CreateFleetVisualOverFrames(List<FWAirShip> ships, int seed,
            long factionUid, bool enableShipIdleDrift, string mothershipType,
            long fleetUid, bool fitSelectionCircleToFormation)
        {
            CreateCalls++;
            return new object[0];
        }
        public void ApplyPresentation(PresentationState state) { PresentationCalls++; }
        public void Dispose() { Disposed = true; }
    }
    public static class GameAssets { public const string GalaxyNPCFleet = "fleet-proxy"; }
    public static class ResourceLoader
    {
        public static T Load<T>(string id) where T : class, new() { return new T(); }
        public static void Release(object value) { }
    }
    public static class NativeWire
    {
        public static void Set(object obj, string name, long id) { }
    }
    public static class GalaxyMothershipVisual
    {
        public static string ResolveType(AirShipFleetInfoBean view) { return "test-mothership"; }
    }
    public static class AirShipUIUtils
    {
        public static bool isCanTouch() { return true; }
    }
    public sealed class FakeContext
    {
        public string LocalPlayerId = "host";
        public bool IsHost = true;
        public bool Paused;
        public Func<long, bool> IsPlayerFleetBattleLocked;
        public Action<string, long> RequestPlayerFleetInteraction;
        public Dictionary<long, string> Owners = new Dictionary<long, string>();
        public string OwnerOfEntity(long uid)
        {
            string owner;
            return Owners.TryGetValue(uid, out owner) ? owner : null;
        }
        public bool IsPaused() { return Paused; }
        public string GetPlayerDisplayName(string id) { return id; }
    }
    public sealed class FakePlayer
    {
        public readonly List<FWFleet> Fleets = new List<FWFleet>();
    }
    public sealed partial class GameBridge
    {
        private sealed class FleetPose
        {
            public long FleetId;
            public string PlayerId, GalaxyId;
            public float X, Y, Angle;
        }
        public static GameBridge Active;
        internal readonly FakeContext Context = new FakeContext();
        internal readonly Dictionary<string, FakePlayer> Players = new Dictionary<string, FakePlayer>();
        internal readonly HashSet<string> Online = new HashSet<string>();
        internal bool disposed;
        internal int OwnedFleetQueries;
        private sealed class NoopScope : IDisposable { public void Dispose() { } }
        private IDisposable EnterPlayer(string playerId) { return new NoopScope(); }
        private IEnumerable<FWFleet> OwnedFleets(string playerId)
        {
            OwnedFleetQueries++;
            return Players[playerId].Fleets;
        }
        private AirShipFleetInfoBean FleetView(string playerId, FWFleet fleet)
        { return new AirShipFleetInfoBean { bean = fleet }; }
        private static UnityEngine.Vector3 nativeFleetPosition(AirShipFleetInfoBean view)
        { return new UnityEngine.Vector3(view.bean.pos.gx, view.bean.pos.gy, 0); }
        private static float NativeFleetHeading(AirShipFleetInfoBean view) { return view.bean.pos.angle; }
    }
}
