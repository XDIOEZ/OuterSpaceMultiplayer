using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Test-only external state needed to compile the actual new production
    // MultiplayerSession partial, without linking Unity/Transport classes.
    public sealed partial class MultiplayerSession
    {
        private readonly List<RoomChatMessage> chatHistory = new List<RoomChatMessage>();
        private static RoomChatMessage CopyChat(RoomChatMessage message)
        { return Program.LegacyCopy(message); }
        internal void TestAddChat(RoomChatMessage message) { chatHistory.Add(message); }
    }
}
