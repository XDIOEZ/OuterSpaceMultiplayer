using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private readonly Dictionary<string, long> surfaceMapDiagnosticTimes = new Dictionary<string, long>();

        // Boundary events only. Repeated rejected receipts are throttled; payloads,
        // player/peer identities and capability identifiers must never enter this trace.
        private void TraceSurfaceMap(string stage, long mapId, long incomingEpoch = -1, long incomingRevision = -1, bool mismatch = false)
        {
            try
            {
                if (mismatch)
                {
                    string key = stage + ":" + mapId;
                    long now = Stopwatch.GetTimestamp(), previous;
                    if (surfaceMapDiagnosticTimes.TryGetValue(key, out previous)
                        && (now - previous) / (double)Stopwatch.Frequency < 5) return;
                    if (surfaceMapDiagnosticTimes.Count >= 128 && !surfaceMapDiagnosticTimes.ContainsKey(key)) return;
                    surfaceMapDiagnosticTimes[key] = now;
                }
                MapRecord map = null;
                if (World != null) World.Maps.TryGetValue(mapId, out map);
                MapSnapshot baseline;
                localBaselines.TryGetValue(mapId, out baseline);
                MapUpdate pending;
                inFlightUpdates.TryGetValue(mapId, out pending);
                string state = "[FreeWorldMultiplayer][surface-map] stage=" + stage
                    + " role=" + (IsHost ? "host" : "guest") + " map=" + mapId
                    + " epoch=" + (map == null ? -1 : map.AuthorityEpoch)
                    + " revision=" + (map == null ? -1 : map.Revision)
                    + " incomingEpoch=" + incomingEpoch + " incomingRevision=" + incomingRevision
                    + " localSimulator=" + (map != null && map.SimulatorPlayerId == LocalPlayerId)
                    + " awaitingReady=" + (map != null && map.AwaitingAuthorityReady)
                    + " visitors=" + (map == null ? 0 : map.Visitors.Count)
                    + " checkpointRevision=" + (map == null || map.Checkpoint == null ? -1 : map.Checkpoint.Revision)
                    + " checkpointDelta=" + (map != null && map.Checkpoint != null && map.Checkpoint.IsDeltaBaseline)
                    + " baselineRevision=" + (baseline == null ? -1 : baseline.Revision)
                    + " baselineDelta=" + (baseline != null && baseline.IsDeltaBaseline)
                    + " pendingBase=" + (pending == null ? -1 : pending.BaseRevision)
                    + " pendingRevision=" + (pending == null ? -1 : pending.Revision)
                    + " mapInFlight=" + inFlightUpdates.Count + " personalInFlight=" + personalInFlight.Count
                    + " loading=" + HasMapLoadingBarrier + " loadingQueue=" + loadingMessages.Count
                    + " paused=" + Paused + " giftPaused=" + giftPausedMaps.Contains(mapId);
                if (mismatch) Logger.LogWarningEngine(state); else Logger.LogEngine(state);
            }
            catch { /* A diagnostic must never change native or acknowledgement state. */ }
        }
    }
}
