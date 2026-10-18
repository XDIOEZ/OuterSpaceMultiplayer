using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FreeWorldMultiplayer
{
    // Uses the WireMessage.RequestId already carried by protocol 6. A stable
    // token identifies the PREVIOUS granted range, not each network attempt.
    // It contains no credential or private identity data.
    internal static class UidRangeRenewalToken
    {
        internal static string For(UidRange range)
        {
            if (range == null || String.IsNullOrEmpty(range.SessionId) || range.First < 0
                || range.Last < range.First)
                throw new InvalidDataException("Invalid UID allocation range for renewal.");
            return "uid:" + range.SessionId + ":" +
                range.First.ToString(CultureInfo.InvariantCulture) + ":" +
                range.Last.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal enum UidRangeRenewalAction
    {
        Invalid, Cooldown, Issue, RetryPersist, Resend
    }

    internal sealed class HostUidRangeRenewalGate
    {
        internal const double MinGrantIntervalSeconds = 30;
        internal const double MinReplyRetrySeconds = 2;
        internal const int MaxGrantsPerSession = 128;
        private sealed class Issuance
        {
            internal string PreviousKey, NewKey, RoomSessionId;
            internal bool Saved;
            internal int Grants;
            internal double LastGrantAt, LastReplyAttemptAt = Double.NegativeInfinity;
        }
        private readonly Dictionary<string, Issuance> byPlayer =
            new Dictionary<string, Issuance>(StringComparer.Ordinal);
        private double roomSeconds;

        internal void Advance(double seconds)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            roomSeconds += seconds;
        }

        internal UidRangeRenewalAction Decide(string playerId, UidRange current, string requestKey)
        {
            if (String.IsNullOrEmpty(playerId) || String.IsNullOrEmpty(requestKey)
                || requestKey.Length > 128) return UidRangeRenewalAction.Invalid;
            string currentKey = UidRangeRenewalToken.For(current);
            Issuance issuance;
            if (byPlayer.TryGetValue(playerId, out issuance))
            {
                // A reconnect in the SAME room issues a new base range but
                // MUST NOT reset the abuse budget. Only a genuinely new room
                // SessionId can clear the old session's counters.
                if (issuance.RoomSessionId != current.SessionId && requestKey == currentKey)
                {
                    byPlayer.Remove(playerId);
                    issuance = null;
                }
            }
            if (issuance != null)
            {
                // Never allow a stale prior-grant retry to mint a second range.
                if (issuance.PreviousKey == requestKey && issuance.NewKey == currentKey)
                {
                    if (roomSeconds - issuance.LastReplyAttemptAt < MinReplyRetrySeconds)
                        return UidRangeRenewalAction.Cooldown;
                    return issuance.Saved ? UidRangeRenewalAction.Resend : UidRangeRenewalAction.RetryPersist;
                }
                if (requestKey != currentKey) return UidRangeRenewalAction.Invalid;
                if (issuance.Grants >= MaxGrantsPerSession) return UidRangeRenewalAction.Invalid;
                if (roomSeconds - issuance.LastGrantAt < MinGrantIntervalSeconds)
                    return UidRangeRenewalAction.Cooldown;
            }
            else if (requestKey != currentKey) return UidRangeRenewalAction.Invalid;
            return UidRangeRenewalAction.Issue;
        }

        // Older peers running the same protocol version did not include a
        // RequestId on "uids". Preserve their existing null-payload behavior
        // without allowing every duplicate to allocate/save another 1M IDs.
        // Unlike tokenized requests, legacy duplicates beyond the cooldown
        // cannot be proven old: they remain rate-limited, not fully idempotent.
        internal UidRangeRenewalAction DecideLegacy(string playerId, UidRange current)
        {
            string key = UidRangeRenewalToken.For(current);
            Issuance issued;
            if (byPlayer.TryGetValue(playerId, out issued) && issued.NewKey == key
                && (roomSeconds - issued.LastGrantAt < MinGrantIntervalSeconds || !issued.Saved))
            {
                if (roomSeconds - issued.LastReplyAttemptAt < MinReplyRetrySeconds)
                    return UidRangeRenewalAction.Cooldown;
                return issued.Saved ? UidRangeRenewalAction.Resend : UidRangeRenewalAction.RetryPersist;
            }
            return Decide(playerId, current, key);
        }

        internal void RecordIssued(string playerId, string previousKey, UidRange newRange)
        {
            Issuance issuance;
            if (!byPlayer.TryGetValue(playerId, out issuance))
            {
                issuance = new Issuance();
                byPlayer.Add(playerId, issuance);
            }
            issuance.PreviousKey = previousKey;
            issuance.NewKey = UidRangeRenewalToken.For(newRange);
            issuance.RoomSessionId = newRange.SessionId;
            issuance.LastGrantAt = roomSeconds;
            issuance.LastReplyAttemptAt = Double.NegativeInfinity;
            issuance.Grants++;
            issuance.Saved = false;
        }

        internal void ReplyAttempt(string playerId, UidRange current)
        {
            Issuance issuance;
            if (!byPlayer.TryGetValue(playerId, out issuance)
                || issuance.NewKey != UidRangeRenewalToken.For(current))
                throw new InvalidOperationException("UID reply does not match the pending grant.");
            issuance.LastReplyAttemptAt = roomSeconds;
        }

        internal void Saved(string playerId, UidRange range)
        {
            Issuance issuance;
            if (!byPlayer.TryGetValue(playerId, out issuance)
                || issuance.NewKey != UidRangeRenewalToken.For(range))
                throw new InvalidOperationException("UID grant save does not match the pending reservation.");
            issuance.Saved = true;
        }

        internal void Clear() { byPlayer.Clear(); roomSeconds = 0; }
    }

    internal sealed class ClientUidRangeRenewalGate
    {
        internal const double RetrySeconds = 3;
        private string pendingKey;
        private double retryAfterSeconds;

        internal string PendingKey { get { return pendingKey; } }

        internal bool TryRequest(UidRange current, Action<string> send)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            if (current == null) return false;
            if (current.Last - current.Next > 1000) return false;
            string key = UidRangeRenewalToken.For(current);
            if (pendingKey != key) { pendingKey = key; retryAfterSeconds = 0; }
            if (retryAfterSeconds > 0) return false;
            retryAfterSeconds = RetrySeconds;
            try { send(key); return true; }
            catch (IOException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal void Advance(double seconds)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            retryAfterSeconds = Math.Max(0, retryAfterSeconds - seconds);
        }

        internal bool Accept(UidRange current, UidRange received, string requestKey)
        {
            if (current == null || received == null || received.SessionId != current.SessionId
                || received.First < 0 || received.Last < received.First || received.Next != received.First)
                throw new InvalidDataException("UID grant is not a valid range in this room session.");
            // A duplicate, or a late reply to a previous range, must never
            // replace the guest's already consumed Next cursor.
            if (received.First <= current.Last) return false;
            if (pendingKey == null || requestKey != null && pendingKey != requestKey
                || pendingKey != UidRangeRenewalToken.For(current))
                return false;
            pendingKey = null;
            retryAfterSeconds = 0;
            return true;
        }

        internal void Clear() { pendingKey = null; retryAfterSeconds = 0; }
    }
}
