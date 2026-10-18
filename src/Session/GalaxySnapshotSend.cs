namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        // Both periodic host broadcast and battle entry can send a full
        // baseline. Release the reset gate only after this exact frame has
        // entered transport AND its outgoing baseline was committed.
        private void SendAndConfirmGalaxy(string peer, string playerId)
        {
            GalaxySnapshot snapshot = bridge.CaptureGalaxy(playerId);
            Send(peer, "galaxy", snapshot);
            bridge.ConfirmGalaxySent(playerId);
            if (snapshot.IsFullBaseline) galaxyResetRequests.FullEnqueued(playerId);
        }
    }
}
