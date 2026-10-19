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
            internal bool Queued, AwaitingFullEnqueue, Issuing;
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
            if (!request.Queued || request.AwaitingFullEnqueue || request.Issuing
                || elapsed < request.NextAllowedAt) return false;
            // Do not consume a request if the native reset itself fails.
            // A later Tick can retry it without any extra client message.
            request.Issuing = true;
            try { resetRecipient(playerId); }
            finally { request.Issuing = false; }
            // A native callback may synchronously disconnect and replace this
            // player. Never record a completed reset on a discarded lease.
            Request current;
            if (!requests.TryGetValue(playerId, out current) || !ReferenceEquals(current, request))
                return false;
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
            // Snapshot only DUE work, before invoking any native callback:
            // disconnect/reconnect can mutate the live player dictionary.
            // An idle Tick does not allocate a snapshot.
            List<KeyValuePair<string, Request>> due = null;
            foreach (var entry in requests)
                if (entry.Value.Queued && !entry.Value.AwaitingFullEnqueue
                    && !entry.Value.Issuing && elapsed >= entry.Value.NextAllowedAt)
                {
                    if (due == null) due = new List<KeyValuePair<string, Request>>();
                    due.Add(entry);
                }
            if (due == null) return;
            foreach (var entry in due)
            {
                Request current;
                if (requests.TryGetValue(entry.Key, out current) && ReferenceEquals(current, entry.Value))
                    TryIssue(entry.Key, current, resetRecipient);
            }
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
