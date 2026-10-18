using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Host-side, per authenticated PlayerId. A reset is an instruction to
    // discard just that player's outgoing baseline, NOT an acknowledgement
    // that the client could apply the next full frame.
    internal sealed class GalaxyResetRequestGate
    {
        private sealed class Request
        {
            internal bool Queued, AwaitingFullEnqueue;
            internal int Attempts;
            internal double LastResetAt, NextAllowedAt;
        }

        private readonly Dictionary<string, Request> requests =
            new Dictionary<string, Request>(StringComparer.Ordinal);
        private double elapsed;

        // First reset is immediate. Subsequent resets have 2/4/8/16-second
        // spacing, capped at 16 seconds until 30 seconds of quiet. Requests
        // during cooldown are coalesced into one automatic future reset.
        internal bool RequestReset(string playerId, Action<string> resetRecipient)
        {
            if (String.IsNullOrEmpty(playerId)) throw new ArgumentException("Missing galaxy reset player ID.", nameof(playerId));
            if (resetRecipient == null) throw new ArgumentNullException(nameof(resetRecipient));
            Request request;
            if (!requests.TryGetValue(playerId, out request))
            {
                request = new Request();
                requests.Add(playerId, request);
            }
            if (request.AwaitingFullEnqueue || request.Queued) return false;
            request.Queued = true;
            return TryIssue(playerId, request, resetRecipient);
        }

        private bool TryIssue(string playerId, Request request, Action<string> resetRecipient)
        {
            if (!request.Queued || request.AwaitingFullEnqueue || elapsed < request.NextAllowedAt) return false;
            // Do not consume a request if the native reset itself fails.
            // A later Tick can retry it without any extra client message.
            resetRecipient(playerId);
            if (request.Attempts > 0 && elapsed - request.LastResetAt >= 30)
                request.Attempts = 0;
            if (request.Attempts < 4) request.Attempts++;
            double spacing = Math.Min(16, 1 << request.Attempts);
            request.LastResetAt = elapsed;
            request.NextAllowedAt = elapsed + spacing;
            request.Queued = false;
            request.AwaitingFullEnqueue = true;
            return true;
        }

        internal void Advance(double seconds, Action<string> resetRecipient)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            if (resetRecipient == null) throw new ArgumentNullException(nameof(resetRecipient));
            elapsed += seconds;
            // Most host ticks have no queued resets. Avoid allocating a list or
            // manipulating other players' state for an idle room.
            foreach (var entry in requests)
                if (entry.Value.Queued && !entry.Value.AwaitingFullEnqueue)
                    TryIssue(entry.Key, entry.Value, resetRecipient);
        }

        internal void FullEnqueued(string playerId)
        {
            Request request;
            if (playerId != null && requests.TryGetValue(playerId, out request))
                request.AwaitingFullEnqueue = false;
        }

        internal void ForgetPlayer(string playerId)
        {
            if (playerId != null) requests.Remove(playerId);
        }

        internal bool WaitingForFull(string playerId)
        {
            Request request;
            return playerId != null && requests.TryGetValue(playerId, out request) && request.AwaitingFullEnqueue;
        }

        internal bool HasDeferredReset(string playerId)
        {
            Request request;
            return playerId != null && requests.TryGetValue(playerId, out request) && request.Queued;
        }
    }

    public sealed partial class MultiplayerSession
    {
        private readonly GalaxyResetRequestGate galaxyResetRequests = new GalaxyResetRequestGate();
    }
}
