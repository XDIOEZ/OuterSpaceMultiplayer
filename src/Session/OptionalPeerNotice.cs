using System;
using System.IO;

namespace FreeWorldMultiplayer
{
    internal enum OptionalPeerNoticeOutcome { Sent, Disconnected, DisconnectFailed }

    // Used only for non-authoritative diagnostics / refusal notices. Never call
    // this for game commands, map/personal ACKs, or durable save confirmations.
    internal static class OptionalPeerNotice
    {
        internal static OptionalPeerNoticeOutcome SendOrDisconnect(Action send, Action disconnect)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            if (disconnect == null) throw new ArgumentNullException(nameof(disconnect));
            try { send(); return OptionalPeerNoticeOutcome.Sent; }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            // Failure to close the unreliable peer must not cascade into the
            // host's next authoritative message, clock or autosave.
            try { disconnect(); return OptionalPeerNoticeOutcome.Disconnected; }
            catch (Exception) { return OptionalPeerNoticeOutcome.DisconnectFailed; }
        }
    }
}
