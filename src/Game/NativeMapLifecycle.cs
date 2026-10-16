using System;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class NativeMapLoad
        {
            internal long MapId, Generation;
            internal MapManager Manager;
            internal UserFWStarSpread Spread;
            internal bool SystemsStarted, Ready;
        }
        private NativeMapLoad nativeMapLoad;
        private long nativeMapLoadGeneration;

        internal void BeginNativeMapLoad(long mapId)
        {
            if (nativeMapLoad != null && !nativeMapLoad.Ready)
                throw new NativeRejectionException("地图仍在加载，不能重复进入：loadingMap=" + nativeMapLoad.MapId
                    + " requestedMap=" + mapId + " generation=" + nativeMapLoad.Generation);
            nativeMapLoad = new NativeMapLoad { MapId = mapId, Generation = checked(++nativeMapLoadGeneration) };
        }

        private NativeMapLoad RequireNativeMapLoad(MapManager manager, long generation)
        {
            NativeMapLoad load = nativeMapLoad;
            FWStarMapInfo canonical;
            UserFWStarSpread spread = UserBean.getInstance().curStarMap;
            if (load == null || generation != load.Generation || manager == null || !ReferenceEquals(MapManager.instance, manager)
                || spread == null || spread.starMapId != load.MapId || !Maps.TryGetValue(load.MapId, out canonical)
                || !ReferenceEquals(canonical.starSpread, spread) || !ReferenceEquals(NativeWire.Get(spread, "map"), manager))
                throw new InvalidOperationException("原生地图加载上下文不匹配：expectedMap=" + (load == null ? -1 : load.MapId)
                    + " actualMap=" + (spread == null ? -1 : spread.starMapId) + " expectedGeneration="
                    + (load == null ? -1 : load.Generation) + " actualGeneration=" + generation);
            return load;
        }

        internal long StartNativeMapSystems(MapManager manager)
        {
            NativeMapLoad load = RequireNativeMapLoad(manager, nativeMapLoad == null ? -1 : nativeMapLoad.Generation);
            if (load.Ready || load.Manager != null)
                throw new InvalidOperationException("原生地图系统不能重复初始化：map=" + load.MapId + " generation=" + load.Generation);
            load.Manager = manager;
            load.Spread = UserBean.getInstance().curStarMap;
            return load.Generation;
        }

        internal void CompleteNativeMapSystems(MapManager manager, long generation)
        {
            NativeMapLoad load = RequireNativeMapLoad(manager, generation);
            if (!ReferenceEquals(load.Manager, manager) || !ReferenceEquals(load.Spread, UserBean.getInstance().curStarMap))
                throw new InvalidOperationException("原生地图系统初始化期间实例发生变化：map=" + load.MapId + " generation=" + generation);
            load.SystemsStarted = true;
        }

        internal void CompleteNativeMapLoad(MapManager manager)
        {
            NativeMapLoad load = RequireNativeMapLoad(manager, nativeMapLoad == null ? -1 : nativeMapLoad.Generation);
            if (!load.SystemsStarted || !ReferenceEquals(load.Manager, manager) || !ReferenceEquals(load.Spread, UserBean.getInstance().curStarMap)
                || !MapManager.isInitFinish)
                throw new InvalidOperationException("原生地图尚未完成本次系统初始化：map=" + load.MapId + " generation=" + load.Generation);
            // Native initEnd projects the active player's employees. Project the other real
            // players' already-restored employees before releasing any queued pose updates.
            EnsureMapCharacters();
            load.Ready = true;
        }

        internal bool IsNativeMapReady(long mapId)
        {
            NativeMapLoad load = nativeMapLoad;
            if (load == null || !load.Ready || !load.SystemsStarted || load.MapId != mapId || load.Manager == null
                || !MapManager.isInitFinish || !ReferenceEquals(MapManager.instance, load.Manager)) return false;
            FWStarMapInfo canonical;
            UserFWStarSpread spread = UserBean.getInstance().curStarMap;
            return spread != null && spread.starMapId == mapId && ReferenceEquals(spread, load.Spread)
                && Maps.TryGetValue(mapId, out canonical) && ReferenceEquals(canonical.starSpread, spread)
                && ReferenceEquals(NativeWire.Get(spread, "map"), load.Manager);
        }

        internal void RequireNativeMapDeltaReady(long mapId)
        {
            if ((CurrentMapId == mapId && MapManager.instance != null || nativeMapLoad != null && !nativeMapLoad.Ready && nativeMapLoad.MapId == mapId)
                && !IsNativeMapReady(mapId))
                throw new NativeRejectionException("地图尚未就绪，增量必须等待本次加载完成：map=" + mapId
                    + " generation=" + (nativeMapLoad == null ? -1 : nativeMapLoad.Generation));
        }

        internal void ClearNativeMapLifecycle()
        { nativeMapLoad = null; nativeMapLoadGeneration = 0; }
    }
}
