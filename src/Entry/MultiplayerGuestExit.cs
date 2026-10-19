namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerRuntime
    {
        private bool guestExitQueued;
        private bool guestExitToDesktop;
        private bool guestDesktopExitRequested;

        internal bool TryQueueGuestExit(bool toDesktop)
        {
            if (quitting || unloading) return false;
            // Keep the first destination and one final-checkpoint exchange for repeated clicks.
            if (guestExitQueued)
            {
                // A failed first Disconnect can be retried; an in-progress close is idempotent.
                if (Session != null && !awaitingMenuReturn) TryQueueNativeReturn(null);
                return true;
            }
            if (Session == null || Session.IsHost) return false;
            guestExitQueued = true;
            guestExitToDesktop = toDesktop;
            TryQueueNativeReturn(null);
            return true;
        }

        private bool CompleteGuestDesktopExit()
        {
            if (!guestExitQueued || !guestExitToDesktop) return false;
            if (guestDesktopExitRequested) return true;
            // FinalizeSession has completed Disconnect, including the host acknowledgement or
            // its explicit timeout error, and disposed the guest bridge before we reach here.
            guestDesktopExitRequested = true;
            Feedback = "正在返回桌面。";
            MultiplayerRoomDialog.CloseOpen();
            NativeWire.Call(JniHandler.getInstance(), "exitGame");
            return true;
        }

        private void ResetGuestExit()
        {
            guestExitQueued = false;
            guestExitToDesktop = false;
            guestDesktopExitRequested = false;
        }
    }
}
