using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The room keeps its bounded, complete conversation history. The UI only
    // renders its most recent matching records, so do not clone older entries
    // merely to discard them with Reverse().Take().Reverse().
    internal static class ChatHistoryWindow
    {
        internal static List<RoomChatMessage> Recent(
            IList<RoomChatMessage> history, string playerId, int maximum,
            Func<RoomChatMessage, RoomChatMessage> copy)
        {
            if (history == null) throw new ArgumentNullException(nameof(history));
            if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
            if (copy == null) throw new ArgumentNullException(nameof(copy));
            var recent = new List<RoomChatMessage>(Math.Min(maximum, history.Count));
            // Work backwards to stop after the last N matches, rather than
            // cloning every message first. Return chronological order.
            for (int i = history.Count - 1; i >= 0 && recent.Count < maximum; i--)
            {
                RoomChatMessage message = history[i];
                if (message.SenderPlayerId == playerId || message.TargetPlayerId == playerId)
                    recent.Add(copy(message));
            }
            recent.Reverse();
            return recent;
        }
    }

    public sealed partial class MultiplayerSession
    {
        // Reuse PlayerChat's existing defensive copier, with a cached delegate
        // so opening/refreshing the dialog does not allocate an extra closure.
        private static readonly Func<RoomChatMessage, RoomChatMessage> ChatWindowCopy = CopyChat;

        // The full-history GetChatMessages API stays untouched for other callers.
        // Only the live dialog requests a bounded, chronological display view.
        internal IList<RoomChatMessage> GetRecentChatMessages(string playerId, int maximum)
        { return ChatHistoryWindow.Recent(chatHistory, playerId, maximum, ChatWindowCopy); }
    }
}
