namespace FreeWorldMultiplayer
{
    /// <summary>Account identities obtained from Steam itself, never from wire payloads or peer labels.</summary>
    public interface ITransportSteamIdentity
    {
        // Returns null when Steam is not ready. Only request this for a Steam-enabled room.
        string GetLocalSteamId();
        // Hosts only: TCP returns null; a Steam route without a verified connected
        // socket throws InvalidOperationException instead of falling back to TCP login.
        string GetRemoteSteamId(string peerId);
    }
}
