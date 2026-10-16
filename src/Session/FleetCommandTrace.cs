using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FreeWorldMultiplayer
{
    // Only user-triggered command transitions are logged. No per-frame fleet poses, identities,
    // passwords, names, destination coordinates or untrusted command payloads are emitted.
    internal static class FleetCommandTrace
    {
        private const int MaxEventsPerMinute = 192;
        private static long windowStart;
        private static int windowEvents;

        internal static void Emit(string stage, string commandId, long fleetUid, bool host, string outcome)
        {
            // All invocations originate from Unity's main-thread hooks or session dispatch.
            long now = Stopwatch.GetTimestamp();
            if (windowStart == 0 || (now - windowStart) / (double)Stopwatch.Frequency >= 60)
            {
                windowStart = now;
                windowEvents = 0;
            }
            if (windowEvents >= MaxEventsPerMinute) return;
            windowEvents++;
            try
            {
                Logger.LogEngine("[FreeWorldMultiplayer][fleet-command] stage=" + stage
                    + " command=" + (String.IsNullOrEmpty(commandId) ? "pending" : commandId)
                    + " fleet=" + fleetUid + " role=" + (host ? "host" : "guest")
                    + " outcome=" + outcome);
            }
            catch { /* A diagnostic log must not change the result of a gameplay command. */ }
        }
    }

    public sealed partial class MultiplayerSession
    {
        // Bounded correlation only; the authoritative command/transaction registries remain
        // untouched. An unresolved command can be discarded from diagnostics without affecting it.
        private readonly Dictionary<string, long> tracedLocalFleetCommands = new Dictionary<string, long>();

        private void TraceLocalFleetCommand(GameCommand command, string stage, string outcome)
        {
            if (command == null || command.Kind != CommandKind.FleetCommand) return;
            if (stage == "session.submit" && !String.IsNullOrEmpty(command.CommandId))
            {
                if (tracedLocalFleetCommands.Count >= 128) tracedLocalFleetCommands.Clear();
                tracedLocalFleetCommands[command.CommandId] = command.EntityId;
            }
            FleetCommandTrace.Emit(stage, command.CommandId, command.EntityId, IsHost, outcome);
        }

        private void TraceHostFleetResult(GameCommand command, CommandResult result)
        {
            if (command == null || command.Kind != CommandKind.FleetCommand) return;
            FleetCommandTrace.Emit("host.result", command.CommandId, command.EntityId, true,
                result != null && result.Success ? "accepted" : "rejected");
        }

        private void TraceCompletedFleetResult(CommandResult result)
        {
            if (result == null || String.IsNullOrEmpty(result.CommandId)) return;
            long fleet;
            if (!tracedLocalFleetCommands.TryGetValue(result.CommandId, out fleet)) return;
            tracedLocalFleetCommands.Remove(result.CommandId);
            FleetCommandTrace.Emit("client.result", result.CommandId, fleet, IsHost,
                result.Success ? "accepted" : "rejected");
        }
    }
}
