using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + label);
        assertions++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void TokenAndClient()
    {
        var original = new UidRange { First = 1, Last = 1000000, Next = 999001, SessionId = "session-a" };
        var next = new UidRange { First = 1000001, Last = 2000000, Next = 1000001, SessionId = "session-a" };
        string key = UidRangeRenewalToken.For(original);
        Check(key == "uid:session-a:1:1000000", "token stable and independent of consumed Next cursor");
        original.Next++;
        Check(UidRangeRenewalToken.For(original) == key, "consuming UID never changes the original request ID");
        Check(Throws<InvalidDataException>(() => UidRangeRenewalToken.For(null)), "null range rejected");
        Check(Throws<InvalidDataException>(() => UidRangeRenewalToken.For(new UidRange())),
            "missing original room identity rejected");

        var gate = new ClientUidRangeRenewalGate();
        int sends = 0;
        Check(gate.TryRequest(original, id =>
        {
            Check(id == key, "client uses stable old-range token");
            sends++;
        }), "first near-exhaustion request queued");
        for (int i = 0; i < 1000; i++)
            Check(!gate.TryRequest(original, id => sends++),
                "duplicate native UID allocations never flood outgoing request queue " + i);
        Check(sends == 1, "1,000 duplicate calls are coalesced");
        gate.Advance(2.99);
        Check(!gate.TryRequest(original, id => sends++), "3 second retry interval not reached");
        gate.Advance(.01);
        Check(gate.TryRequest(original, id => sends++) && sends == 2,
            "missing host reply retried with same request identity");
        var badSession = MultiplayerSession.Copy(next);
        badSession.SessionId = "other";
        Check(Throws<InvalidDataException>(() => gate.Accept(original, badSession, key)),
            "cross-session UID reply cannot overwrite current player state");
        var malformed = MultiplayerSession.Copy(next);
        malformed.Next = malformed.First + 1;
        Check(Throws<InvalidDataException>(() => gate.Accept(original, malformed, key)),
            "incoming UID range cannot secretly skip first allocation");
        Check(!gate.Accept(original, next, "wrong-key"),
            "late/forged response with mismatched requestId is ignored");
        Check(gate.Accept(original, next, key), "matching new range acknowledged and cursor advances once");
        Check(gate.PendingKey == null, "client releases resolved request key after acceptance");
        next.Next = next.First + 17;
        var duplicate = MultiplayerSession.Copy(next);
        duplicate.Next = duplicate.First;
        Check(!gate.Accept(next, duplicate, key),
            "duplicate reply cannot rewind already consumed UID cursor");
        var old = MultiplayerSession.Copy(original);
        old.Next = old.First;
        Check(!gate.Accept(next, old, key), "stale previous-range reply is harmless");
        var ahead = new UidRange { First = 2000001, Last = 3000000,
            Next = 2000001, SessionId = "session-a" };
        Check(!gate.Accept(next, ahead, key),
            "unrequested future grant never replaces the real current range");
        var full = new ClientUidRangeRenewalGate();
        original.Next = original.First;
        Check(!full.TryRequest(original, id => sends++),
            "adequate UID headroom causes no request or allocation");
        original.Next = original.Last + 1;
        Check(full.TryRequest(original, id => sends++),
            "fully exhausted client still requests a replacement with its last real range token");

        var sendFailure = new ClientUidRangeRenewalGate();
        original.Next = original.Last;
        Check(!sendFailure.TryRequest(original, id => throw new IOException("queue full")),
            "failed prefetch enqueue is retained for retry");
        Check(!sendFailure.TryRequest(original, id => sends++),
            "queue failure does not spin inside the same frame");
        sendFailure.Advance(3);
        Check(sendFailure.TryRequest(original, id => sends++),
            "retries failed prefetch after bounded unscaled delay");
        Check(Throws<ArgumentOutOfRangeException>(() => sendFailure.Advance(double.NaN)),
            "client gate rejects NaN clock delta");
        Check(Throws<ArgumentNullException>(() => sendFailure.TryRequest(original, null)),
            "null transport callback never loses request state silently");
    }

    private static void HostIdempotence()
    {
        var host = new MultiplayerSession(true);
        string first = UidRangeRenewalToken.For(host.GuestRecord.Uids);
        Check(Throws<MessageRejectionException>(() => host.AskHost("wrong")),
            "forged request identity cannot reserve UID ranges");
        Check(host.HighWater == 1000000 && host.SaveCalls == 0,
            "invalid requests never save or issue");
        host.AskHost(first);
        Check(host.ReserveCalls == 1 && host.IssuedCount == 2
            && host.SaveCalls == 1 && host.HighWater == 2000000
            && host.SentKeys[0] == first,
            "first renewal reserves a single million UIDs, saves, then echoes request ID");
        for (int i = 0; i < 1000; i++) host.AskHost(first);
        Check(host.ReserveCalls == 1 && host.IssuedCount == 2
            && host.SaveCalls == 1 && host.SendCalls == 1,
            "1k same-range requests do not repeat the grant, save or response inside the throttle");
        host.Tick(2);
        host.AskHost(first);
        Check(host.ReserveCalls == 1 && host.SaveCalls == 1 && host.SendCalls == 2
            && host.SentRanges[1].First == host.SentRanges[0].First,
            "bounded retry returns the identical original saved range after cooldown");

        string current = UidRangeRenewalToken.For(host.GuestRecord.Uids);
        for (int i = 0; i < 1000; i++) host.AskHost(current);
        Check(host.ReserveCalls == 1 && host.SaveCalls == 1,
            "1k malicious next-range claims during cooldown produce zero new grants or saves");
        host.Tick(27.99);
        host.AskHost(current);
        Check(host.ReserveCalls == 1, "new grant still throttled before 30s host-clock bound");
        host.Tick(.01);
        host.AskHost(current);
        Check(host.ReserveCalls == 2 && host.IssuedCount == 3 && host.HighWater == 3000000,
            "new distinct consumed range may legitimately renew after cooldown");
        Check(Throws<MessageRejectionException>(() => host.AskHost(first)),
            "very old request must be rejected instead of reminted");
        Check(host.ReserveCalls == 2 && host.SaveCalls == 2,
            "replayed older-than-immediate request never creates a third grant");
        Check(Throws<MessageRejectionException>(() => host.AskHost("uid:old-session:1:1000000")),
            "old-session request cannot be relabeled as a current UID renewal");
        Check(Throws<MessageRejectionException>(() => host.AskHost("uid:session-a:999:99")),
            "fabricated UID ownership token never creates a grant");

        var separate = new HostUidRangeRenewalGate();
        var old = new UidRange { First = 1, Last = 1000000, Next = 1, SessionId = "room-a" };
        var newSession = new UidRange { First = 1000001, Last = 2000000,
            Next = 1000001, SessionId = "room-b" };
        separate.RecordIssued("same-player", UidRangeRenewalToken.For(old), newSession);
        var later = new UidRange { First = 2000001, Last = 3000000,
            Next = 2000001, SessionId = "room-c" };
        Check(separate.Decide("same-player", later, UidRangeRenewalToken.For(later))
            == UidRangeRenewalAction.Issue,
            "new room session for same player never inherits previous throttle");
        var sameRoomReconnect = new HostUidRangeRenewalGate();
        var firstRoom = new UidRange { First = 1, Last = 1000000, Next = 1, SessionId = "room-x" };
        var granted = new UidRange { First = 1000001, Last = 2000000,
            Next = 1000001, SessionId = "room-x" };
        sameRoomReconnect.RecordIssued("same-player", UidRangeRenewalToken.For(firstRoom), granted);
        sameRoomReconnect.Saved("same-player", granted);
        var reconnect = new UidRange { First = 2000001, Last = 3000000,
            Next = 2000001, SessionId = "room-x" };
        Check(sameRoomReconnect.Decide("same-player", reconnect, UidRangeRenewalToken.For(reconnect))
            == UidRangeRenewalAction.Cooldown,
            "disconnect/rejoin in one room cannot bypass UID grant cooldown");
        sameRoomReconnect.Advance(30);
        Check(sameRoomReconnect.Decide("same-player", reconnect, UidRangeRenewalToken.For(reconnect))
            == UidRangeRenewalAction.Issue,
            "same-room reconnection can legitimately renew after the old safety window");
        Check(Throws<ArgumentOutOfRangeException>(() => separate.Advance(-.1)),
            "host throttling uses validated unscaled clock time");
    }

    private static void LegacySameProtocolCompatibility()
    {
        var legacyHost = new MultiplayerSession(true);
        legacyHost.AskHost(null);
        Check(legacyHost.ReserveCalls == 1 && legacyHost.SaveCalls == 1 &&
            legacyHost.SentKeys[0] == null && legacyHost.SentRanges[0].First == 1000001,
            "old client with null RequestId still receives compatible UID range");
        for (int i = 0; i < 1000; i++) legacyHost.AskHost(null);
        Check(legacyHost.ReserveCalls == 1 && legacyHost.SaveCalls == 1 &&
            legacyHost.SentRanges.Count == 1,
            "1k legacy duplicate requests avoid both save and response flood");
        legacyHost.Tick(29.99);
        legacyHost.AskHost(null);
        Check(legacyHost.ReserveCalls == 1, "old client cannot bypass the thirty-second issuance throttle");
        legacyHost.Tick(.01);
        legacyHost.AskHost(null);
        Check(legacyHost.ReserveCalls == 2 && legacyHost.SaveCalls == 2,
            "legacy client may eventually renew again after safety cooldown");

        var oldServerGuest = new MultiplayerSession(false);
        oldServerGuest.SetCursor(999001);
        oldServerGuest.Request();
        var reply = new UidRange { First = 1000001, Last = 2000000,
            Next = 1000001, SessionId = "room-20261018" };
        oldServerGuest.Incoming(reply, null);
        Check(oldServerGuest.GuestRecord.Uids.First == 1000001 &&
            oldServerGuest.PendingKey == null,
            "new client can accept valid null-token reply from older host");
        oldServerGuest.SetCursor(1000022);
        var duplicateReply = MultiplayerSession.Copy(reply);
        duplicateReply.Next = duplicateReply.First;
        oldServerGuest.Incoming(duplicateReply, null);
        Check(oldServerGuest.GuestRecord.Uids.Next == 1000022,
            "legacy duplicate reply never rewinds UID use after client accepts it");
    }

    private static void DurableSaveAndSendFailure()
    {
        var host = new MultiplayerSession(true);
        string key = UidRangeRenewalToken.For(host.GuestRecord.Uids);
        host.FailSaves = 2;
        Check(Throws<IOException>(() => host.AskHost(key)),
            "first durable write failure surfaces to normal host fault handler");
        Check(host.ReserveCalls == 1 && host.IssuedCount == 2 &&
            host.HighWater == 2000000 && host.SentRanges.Count == 0,
            "failed write reserves just one high-water range, never reports unpersisted success");
        for (int i = 0; i < 1000; i++) host.AskHost(key);
        Check(host.SaveCalls == 1 && host.ReserveCalls == 1,
            "1k immediate replays after disk failure cannot hammer the SaveStore");
        host.Tick(2);
        Check(Throws<IOException>(() => host.AskHost(key)),
            "second save fault reuses pending range instead of allocating another");
        Check(host.ReserveCalls == 1 && host.SaveCalls == 2 && host.SentRanges.Count == 0,
            "two failed writes still consume one range and zero reply bytes");
        host.FailSends = 1;
        host.Tick(2);
        Check(Throws<IOException>(() => host.AskHost(key)),
            "successful save followed by full send queue propagates actual send error");
        Check(host.ReserveCalls == 1 && host.SaveCalls == 3 &&
            host.SentRanges.Count == 0, "send failure never re-issues or re-saves a durable range");
        host.Tick(2);
        host.AskHost(key);
        Check(host.ReserveCalls == 1 && host.SaveCalls == 3 &&
            host.SentRanges.Count == 1 && host.SentRanges[0].First == 1000001,
            "next identical request replays the same committed UID range");
        for (int i = 0; i < 1000; i++) host.AskHost(key);
        Check(host.ReserveCalls == 1 && host.SaveCalls == 3,
            "1k retries after reply loss do not advance the high-water mark");
    }

    private static void HostGuestRoundTrip()
    {
        var host = new MultiplayerSession(true);
        var guest = new MultiplayerSession(false);
        guest.SetCursor(guest.GuestRecord.Uids.Last - 999);
        int dropped = 1;
        guest.OnSend = (peer, kind, payload, requestId) =>
        {
            Check(peer == "host" && kind == "uids" && payload == null,
                "client preserves existing uids null-payload wire kind");
            host.AskHost(requestId);
        };
        host.OnSend = (peer, kind, payload, requestId) =>
        {
            Check(peer == "guest-peer" && kind == "uids" && payload is UidRange,
                "host returns a typed UID range to original authenticated peer");
            if (dropped-- > 0) return;
            guest.Incoming((UidRange)payload, requestId);
        };
        guest.Request();
        Check(guest.PendingKey != null && host.SaveCalls == 1 &&
            host.ReserveCalls == 1 && guest.GuestRecord.Uids.First == 1,
            "dropped host response does not modify client allocation cursor");
        host.Tick(2.99);
        guest.Tick(2.99);
        Check(host.SendCalls == 1, "preemptive retry respects time");
        host.Tick(.01);
        guest.Tick(.01);
        Check(host.SendCalls == 2 && host.SaveCalls == 1 &&
            guest.PendingKey == null && guest.GuestRecord.Uids.First == 1000001,
            "idempotent retry recovers the same host-saved range without new allocation");
        Check(guest.GuestRecord.Uids.Next == guest.GuestRecord.Uids.First,
            "returned UID cursor starts at the first never-used UID");
        guest.SetCursor(guest.GuestRecord.Uids.Last - 999);
        guest.Request();
        Check(host.SaveCalls == 1 && guest.PendingKey != null,
            "legitimate next renewal waits during host 30s anti-flood window");
        host.Tick(30);
        guest.Tick(3);
        Check(host.SaveCalls == 2 && guest.GuestRecord.Uids.First == 2000001,
            "eventual honest exhaustion can renew beyond the previously issued range");
        var stale = new UidRange { First = 1000001, Last = 2000000,
            Next = 1000001, SessionId = "room-20261018" };
        guest.Incoming(stale, UidRangeRenewalToken.For(new UidRange {
            First = 1, Last = 1000000, Next = 999001, SessionId = "room-20261018" }));
        Check(guest.GuestRecord.Uids.First == 2000001 &&
            guest.GuestRecord.Uids.Next == 2000001,
            "old delayed packet cannot roll back the client's newest range");
    }

    private static void SessionCapAndPlayerIsolation()
    {
        var gate = new HostUidRangeRenewalGate();
        var other = new UidRange { First = 10000001, Last = 11000000,
            Next = 10000001, SessionId = "room" };
        var first = new UidRange { First = 1, Last = 1000000, Next = 1, SessionId = "room" };
        Check(gate.Decide("noisy", first, UidRangeRenewalToken.For(first)) ==
            UidRangeRenewalAction.Issue, "first noisy player may request a grant");
        gate.RecordIssued("noisy", UidRangeRenewalToken.For(first),
            new UidRange { First = 1000001, Last = 2000000, Next = 1000001, SessionId = "room" });
        Check(gate.Decide("healthy", other, UidRangeRenewalToken.For(other)) ==
            UidRangeRenewalAction.Issue,
            "another authenticated player is not throttled by noisy peer's quota");
        gate.RecordIssued("healthy", UidRangeRenewalToken.For(other),
            new UidRange { First = 11000001, Last = 12000000, Next = 11000001, SessionId = "room" });

        var capped = new HostUidRangeRenewalGate();
        var current = first;
        for (int i = 0; i < HostUidRangeRenewalGate.MaxGrantsPerSession; i++)
        {
            string prior = UidRangeRenewalToken.For(current);
            Check(capped.Decide("player", current, prior) == UidRangeRenewalAction.Issue,
                "valid request " + i + " can use distinct new range after 30s");
            var next = new UidRange {
                First = current.Last + 1, Last = current.Last + 1000000,
                Next = current.Last + 1, SessionId = "room" };
            capped.RecordIssued("player", prior, next);
            capped.ReplyAttempt("player", next);
            capped.Saved("player", next);
            current = next;
            capped.Advance(30);
        }
        Check(capped.Decide("player", current, UidRangeRenewalToken.For(current)) ==
            UidRangeRenewalAction.Invalid,
            "per-room range quota has a finite ceiling even over long-running hostile sessions");
        Check(capped.Decide("different-player", other, UidRangeRenewalToken.For(other)) ==
            UidRangeRenewalAction.Issue,
            "hitting per-player quota never blocks another player's legitimate UID allocation");
    }

    private static void RandomRateBounds()
    {
        var rng = new Random(20261018);
        for (int round = 0; round < 10000; round++)
        {
            var host = new MultiplayerSession(true);
            string key = UidRangeRenewalToken.For(host.GuestRecord.Uids);
            int requests = rng.Next(1, 150);
            for (int n = 0; n < requests; n++)
            {
                host.AskHost(key);
                if (rng.Next(4) == 0) host.Tick(rng.NextDouble() * .05);
            }
            Check(host.ReserveCalls == 1 && host.SaveCalls == 1 &&
                host.IssuedCount == 2 && host.HighWater == 2000000,
                "random duplicates never reserve extra UID ranges " + round);
            Check(host.SentKeys.Count == 1 &&
                host.SentRanges[0].First == 1000001,
                "random duplicates are answered at most once inside the response cooldown " + round);
        }
    }

    private static int Main()
    {
        try
        {
            TokenAndClient();
            HostIdempotence();
            LegacySameProtocolCompatibility();
            DurableSaveAndSendFailure();
            HostGuestRoundTrip();
            SessionCapAndPlayerIsolation();
            RandomRateBounds();
            Console.WriteLine("PASS: " + assertions + " production-linked UID renewal checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
