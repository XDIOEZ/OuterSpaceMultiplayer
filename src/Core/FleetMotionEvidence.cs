using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Pure logic: correlate one accepted native Move command with an observed
    // host-authority fleet pose. This is diagnostic evidence, not a motion
    // controller, position history or a gameplay timeout.
    internal sealed class FleetMotionObservation
    {
        internal string CommandId;
        internal long FleetUid;
        internal string Outcome;
    }

    internal sealed class FleetMotionEvidence
    {
        internal const int MaxPending = 64;
        internal const int MaxObservedTicks = 120;
        private const float Epsilon = 0.001f;

        private sealed class Pending
        {
            internal string CommandId;
            internal string Galaxy;
            internal float X, Y;
            internal int Samples;
        }

        private readonly Dictionary<long, Pending> pending = new Dictionary<long, Pending>();

        internal int Count { get { return pending.Count; } }
        internal bool HasPending(long fleetUid) { return pending.ContainsKey(fleetUid); }

        internal FleetMotionObservation Start(string commandId, long fleetUid, string galaxy, float x, float y)
        {
            if (String.IsNullOrWhiteSpace(commandId) || commandId.Length > 128 || fleetUid < 0
                || Single.IsNaN(x) || Single.IsInfinity(x) || Single.IsNaN(y) || Single.IsInfinity(y))
                throw new ArgumentException("Motion evidence requires a valid command, fleet UID and finite start position.");

            Pending prior;
            if (pending.TryGetValue(fleetUid, out prior) && prior.CommandId == commandId) return null;
            if (prior == null && pending.Count >= MaxPending)
                return new FleetMotionObservation { CommandId = commandId, FleetUid = fleetUid, Outcome = "capacity-unobserved" };

            var dropped = prior == null ? null : new FleetMotionObservation
            {
                CommandId = prior.CommandId, FleetUid = fleetUid, Outcome = "superseded"
            };
            pending[fleetUid] = new Pending { CommandId = commandId, Galaxy = galaxy, X = x, Y = y };
            return dropped;
        }

        internal FleetMotionObservation Sample(long fleetUid, string galaxy, float x, float y)
        {
            Pending item;
            if (!pending.TryGetValue(fleetUid, out item)) return null;
            if (Single.IsNaN(x) || Single.IsInfinity(x) || Single.IsNaN(y) || Single.IsInfinity(y))
                return null; // Cannot label an invalid pose "unchanged" or "moved".

            item.Samples++;
            bool changed = !String.Equals(item.Galaxy, galaxy, StringComparison.Ordinal)
                || Math.Abs(x - item.X) > Epsilon || Math.Abs(y - item.Y) > Epsilon;
            if (!changed && item.Samples < MaxObservedTicks) return null;
            pending.Remove(fleetUid);
            return new FleetMotionObservation
            {
                CommandId = item.CommandId,
                FleetUid = fleetUid,
                Outcome = changed ? "position-changed" : "unchanged-after-120-host-ticks"
            };
        }

        internal FleetMotionObservation Cancel(long fleetUid, string outcome)
        {
            Pending item;
            if (!pending.TryGetValue(fleetUid, out item)) return null;
            pending.Remove(fleetUid);
            return new FleetMotionObservation { CommandId = item.CommandId, FleetUid = fleetUid, Outcome = outcome };
        }

        internal void Clear() { pending.Clear(); }
    }
}
