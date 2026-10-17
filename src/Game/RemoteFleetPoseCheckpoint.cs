namespace FreeWorldMultiplayer
{
    // The interpolated presentation state of one remote fleet in one room.
    // Reusing the same object for subsequent authoritative checkpoints avoids
    // creating a new reference object on every received movement update.
    // Only the owning GameBridge's UID dictionary keeps these objects.
    internal sealed class RemoteFleetPoseCheckpoint<TPose, TPosition>
    {
        internal TPose Pose;
        internal long Sequence;
        internal double Receipt, StartedAt, Duration, PauseAt = -1;
        internal TPosition From;
        internal float FromAngle;

        internal RemoteFleetPoseCheckpoint(TPose pose, long sequence,
            double receipt, TPosition from, float fromAngle, double duration, double pauseAt)
        {
            Replace(pose, sequence, receipt, from, fromAngle, duration, pauseAt);
        }

        internal void Replace(TPose pose, long sequence,
            double receipt, TPosition from, float fromAngle, double duration, double pauseAt)
        {
            // All prior interpolation data has already been read before this
            // assignment. In particular, pause transitions may have updated
            // StartedAt in TryGetSynchronizedFleetPose earlier in this frame.
            Pose = pose;
            Sequence = sequence;
            Receipt = receipt;
            From = from;
            FromAngle = fromAngle;
            StartedAt = receipt;
            Duration = duration;
            PauseAt = pauseAt;
        }
    }
}
