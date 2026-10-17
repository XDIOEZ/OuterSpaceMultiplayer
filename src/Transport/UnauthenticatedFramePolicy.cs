namespace FreeWorldMultiplayer
{
    // Optional capability: an authenticated session may receive normal-size
    // map frames. The host never grants this before the welcome is sent.
    public interface ITransportPeerAdmission
    {
        void MarkAuthenticated(string peerId);
    }

    internal static class UnauthenticatedFramePolicy
    {
        internal const int MaxHandshakeBytes = 16 * 1024;
        internal const int MaxFrameBytes = TransportQueue.MaxMessageBytes;

        // Called before socket payload allocation, Steam reassembly allocation,
        // and again before Json.NET creates a full main-thread JToken tree.
        internal static bool AllowsFrame(bool host, bool authenticated, int byteCount)
        {
            return byteCount > 0 && byteCount <= MaxFrameBytes &&
                (!host || authenticated || byteCount <= MaxHandshakeBytes);
        }

        internal static bool IsHandshakeKind(string kind)
        {
            return kind == "hello" || kind == "auth";
        }
    }
}
