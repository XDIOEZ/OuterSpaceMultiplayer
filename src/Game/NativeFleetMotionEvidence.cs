using System;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        // Enable only for deliberate multiplayer profiling. The normal hot path
        // remains unchanged unless a real Move/MoveUi command is being observed.
        private static readonly bool hostMotionEvidenceEnabled =
            String.Equals(Environment.GetEnvironmentVariable("FWM_PROFILE"), "1", StringComparison.Ordinal)
            || Array.IndexOf(Environment.GetCommandLineArgs(), "-fwm-profile") >= 0;
        private readonly FleetMotionEvidence hostMotionEvidence = new FleetMotionEvidence();

        private bool TryBeginFleetMotionEvidence(AirShipFleetInfoBean view, out Vector3 start, out string galaxy)
        {
            start = Vector3.zero;
            galaxy = null;
            if (!hostMotionEvidenceEnabled) return false;
            try
            {
                start = nativeFleetPosition(view);
                galaxy = view.getCurGalaxyId();
                return true;
            }
            catch
            {
                // Telemetry is never allowed to reject a valid native fleet order.
                return false;
            }
        }

        private void ArmFleetMotionEvidence(GameCommand command, Vector3 start, string galaxy)
        {
            try
            {
                FleetMotionObservation replaced = hostMotionEvidence.Start(command.CommandId,
                    command.EntityId, galaxy, start.x, start.y);
                ReportFleetMotionEvidence(replaced);
                if (hostMotionEvidence.HasPending(command.EntityId))
                    FleetCommandTrace.Emit("host.motion.armed", command.CommandId, command.EntityId, true, "awaiting-host-tick");
            }
            catch
            {
                FleetCommandTrace.Emit("host.motion", command.CommandId, command.EntityId, true, "observer-unavailable");
            }
        }

        private void TickHostFleetMotionEvidence(AirShipFleetInfoBean view)
        {
            if (!hostMotionEvidenceEnabled || view == null || view.bean == null) return;
            long uid = view.bean.getUID();
            if (!hostMotionEvidence.HasPending(uid)) return;
            try
            {
                Vector3 position = nativeFleetPosition(view);
                ReportFleetMotionEvidence(hostMotionEvidence.Sample(uid, view.getCurGalaxyId(), position.x, position.y));
            }
            catch
            {
                ReportFleetMotionEvidence(hostMotionEvidence.Cancel(uid, "observer-unavailable"));
            }
        }

        private static void ReportFleetMotionEvidence(FleetMotionObservation observation)
        {
            if (observation != null)
                FleetCommandTrace.Emit("host.motion", observation.CommandId,
                    observation.FleetUid, true, observation.Outcome);
        }

        private void ClearHostFleetMotionEvidence() { hostMotionEvidence.Clear(); }
    }
}
