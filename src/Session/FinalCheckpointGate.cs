using System;

namespace FreeWorldMultiplayer
{
    // A finalDone means every local map/personal delta queued through this
    // barrier has been acknowledged. Publishing at the barrier can itself
    // create new in-flight transactions; check both before and AFTER publish.
    internal static class FinalCheckpointGate
    {
        internal static bool TryPublishAndConfirm(
            Func<bool> hasOutstanding,
            Action publishMaps,
            Action publishPersonal,
            Action sendFinalDone)
        {
            if (hasOutstanding == null) throw new ArgumentNullException(nameof(hasOutstanding));
            if (publishMaps == null) throw new ArgumentNullException(nameof(publishMaps));
            if (publishPersonal == null) throw new ArgumentNullException(nameof(publishPersonal));
            if (sendFinalDone == null) throw new ArgumentNullException(nameof(sendFinalDone));

            if (hasOutstanding()) return false;
            publishMaps();
            publishPersonal();
            if (hasOutstanding()) return false;
            // Only a successful transport enqueue completes the barrier.
            // On exceptions the caller retains the pending request to retry.
            sendFinalDone();
            return true;
        }
    }
}
