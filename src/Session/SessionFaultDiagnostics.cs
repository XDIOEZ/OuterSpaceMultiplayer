using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private double giftWaitDiagnosticElapsed;
        private readonly HashSet<string> loggedSessionFaults = new HashSet<string>();

        // Tick and local structural commands have no receiving envelope. Record their
        // original exception without borrowing a stale network-message identity.
        private void ReportSessionFault(string stage, Exception error, GameCommand command = null)
        {
            if (error == null || error is NativeRejectionException || error is MessageRejectionException) return;
            var bridgeError = error as BridgeOperationException;
            if (bridgeError != null && bridgeError.NativeUnchanged) return;
            try
            {
                string reason = error.Message ?? error.GetType().FullName;
                int newline = reason.IndexOfAny(new[] { '\r', '\n' });
                if (newline >= 0) reason = reason.Substring(0, newline);
                if (reason.Length > 1024) reason = reason.Substring(0, 1024);
                string signature = "session|" + stage + "|" + error.GetType().FullName + "|" + reason;
                if (loggedSessionFaults.Contains(signature) || loggedSessionFaults.Count >= 64) return;
                loggedSessionFaults.Add(signature);
                long mapId = command == null ? DiagnosticMapId() : command.MapId;
                MapRecord map = null;
                bool hasMap = World != null && World.Maps.TryGetValue(mapId, out map);
                string context = "stage=" + stage + " role=" + (IsHost ? "host" : "guest")
                    + " map=" + mapId + " paused=" + Paused + " nativeFault=" + (nativeFault != null)
                    + " mapLoading=" + HasMapLoadingBarrier + " mapInFlight=" + inFlightUpdates.Count
                    + " personalInFlight=" + personalInFlight.Count + " giftFlushes=" + giftFlushes.Count
                    + " giftBarriers=" + giftBarriers.Count + " giftPaused=" + giftPausedMaps.Count;
                if (hasMap) context += " epoch=" + map.AuthorityEpoch + " revision=" + map.Revision;
                if (command != null) context += " command=" + command.Kind;
                MessageFaulted?.Invoke("[session-fault] " + context + Environment.NewLine + error.ToString());
            }
            catch { /* Diagnostic observers must not replace the original failure. */ }
        }

        private void TraceTransportSubmission(string stage, GameCommand command)
        {
            if (command == null || command.Kind != CommandKind.DepartTransfer) return;
            try
            {
                TransferRequest request = JsonData.Read<TransferRequest>(command.PayloadJson);
                if (request == null) return;
                Logger.LogEngine("[FreeWorldMultiplayer][transport-submit] stage=" + stage
                    + " role=" + (IsHost ? "host" : "guest") + " sourceMap=" + request.SourceMapId
                    + " sourceFleet=" + request.SourceFleetId + " destinationMap=" + request.DestinationMapId
                    + " destinationFleet=" + request.DestinationFleetId);
            }
            catch { /* Trace parsing/logging never authorizes or changes a transport. */ }
        }

        private void TickGiftFlushDiagnostics(double seconds)
        {
            if (giftPausedMaps.Count == 0) { giftWaitDiagnosticElapsed = 0; return; }
            giftWaitDiagnosticElapsed += seconds;
            if (giftWaitDiagnosticElapsed < 10) return;
            giftWaitDiagnosticElapsed = 0;
            try
            {
                // Only numeric transaction state is recorded. Never dump manifests,
                // player identities, peer tokens or the request's transfer capability.
                GiftCheckpointRequest request = null;
                foreach (GiftCheckpointRequest candidate in pausedGiftRequests.Values) { request = candidate; break; }
                MapRecord map;
                if (request == null || World == null || !World.Maps.TryGetValue(request.MapId, out map)) return;
                MapUpdate pending;
                bool hasPending = inFlightUpdates.TryGetValue(request.MapId, out pending);
                // End enumeration before Logger can invoke arbitrary observers.
                Logger.LogWarningEngine("[FreeWorldMultiplayer][gift-checkpoint-wait] phase="
                    + (giftFlushes.ContainsKey(request.MapId) ? "flush" : "resume") + " map=" + request.MapId
                    + " epoch=" + map.AuthorityEpoch + " revision=" + map.Revision
                    + " frozenMaps=" + giftPausedMaps.Count + " mapInFlight=" + inFlightUpdates.Count
                    + " personalInFlight=" + personalInFlight.Count
                    + " pendingMapRevision=" + (hasPending ? pending.Revision : -1)
                    + " mapLoading=" + HasMapLoadingBarrier + " paused=" + Paused);
            }
            catch { /* This probe must not change checkpoint or resume behavior. */ }
        }
    }
}
