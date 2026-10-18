using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

// Models the SendVisitUpdate notification loop, including the existing
// (deliberately non-deduplicated) pair of recipients. The iterator itself
// links the exact BattleParticipantPair compiled into production.
internal static class VisitRecipientChecks
{
    private sealed class Request
    {
        internal string Requester, Recipient;
        internal Request(string requester, string recipient)
        { Requester = requester; Recipient = recipient; }
    }
    private sealed class Visit
    {
        internal Request Request;
        internal bool Approved;
        internal int Remaining = 45;
        internal string Reason = "等待回复";
        internal object Grant;
        internal Visit(string requester, string recipient, bool approved)
        {
            Request = new Request(requester, recipient);
            Approved = approved;
            Grant = approved ? new object() : null;
        }
    }
    private sealed class Delivery
    {
        internal string Recipient, Reason;
        internal int Remaining;
        internal bool HasGrant;
    }
    private static int checks;
    private static long sink;
    private static void Check(bool pass, string reason)
    {
        if (!pass) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }

    private static string Notify(Visit visit, bool optimized,
        List<Delivery> deliveries, bool mutate, bool fail)
    {
        try
        {
            // Source-equivalent old/new production loops: the two IDs are
            // both captured before any TrySendCommunication callback.
            if (optimized)
                foreach (string player in new BattleParticipantPair(
                    visit.Request.Requester, visit.Request.Recipient))
                    Send(player);
            else
                foreach (string player in new[] {
                    visit.Request.Requester, visit.Request.Recipient })
                    Send(player);
            return "accepted";
        }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }

        void Send(string player)
        {
            // Preserve real SendVisitUpdate's per-send reading of Request,
            // State, RemainingSeconds, Reason, Grant rather than eagerly
            // computing both payloads before a synchronous callback.
            deliveries.Add(new Delivery {
                Recipient = player,
                Reason = visit.Reason,
                Remaining = visit.Remaining,
                HasGrant = player == visit.Request.Requester && visit.Approved
                    && visit.Grant != null
            });
            if (mutate && deliveries.Count == 1)
            {
                visit.Request.Requester = "callback-requester";
                visit.Request.Recipient = "callback-recipient";
                visit.Reason = "状态改变";
                visit.Remaining = 12;
                visit.Approved = !visit.Approved;
            }
            if (fail && deliveries.Count == 1)
                throw new InvalidOperationException("visit.update transport callback failed");
        }
    }

    private static void Equal(string requester, string recipient,
        bool approved, bool mutate, bool fail, string context)
    {
        var old = new Visit(requester, recipient, approved);
        var next = new Visit(requester, recipient, approved);
        var a = new List<Delivery>();
        var b = new List<Delivery>();
        string oldResult = Notify(old, false, a, mutate, fail);
        string newResult = Notify(next, true, b, mutate, fail);
        Check(oldResult == newResult,
            context + ": identical callback failure/acceptance");
        Check(a.Count == b.Count, context + ": number of notifications");
        for (int i = 0; i < a.Count; i++)
            Check(a[i].Recipient == b[i].Recipient &&
                a[i].Reason == b[i].Reason &&
                a[i].Remaining == b[i].Remaining &&
                a[i].HasGrant == b[i].HasGrant,
                context + ": exact recipient order and granted payload after callback");
        Check(old.Request.Requester == next.Request.Requester &&
            old.Request.Recipient == next.Request.Recipient &&
            old.Remaining == next.Remaining && old.Approved == next.Approved,
            context + ": synchronous callback mutations identical");
        if (!fail)
        {
            Check(b.Count == 2 && b[0].Recipient == requester &&
                b[1].Recipient == recipient,
                context + ": second recipient remains captured before first send");
        }
        else
            Check(b.Count == 1, context + ": a failed first send suppresses second");
    }

    private static long Allocation(Action loop, int count)
    {
        for (int i = 0; i < 100; i++) loop();
        long from = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) loop();
        return GC.GetAllocatedBytesForCurrentThread() - from;
    }
    private static void OldHotPath(string requester, string recipient)
    {
        string[] recipients = { requester, recipient };
        foreach (string player in recipients) sink += player == null ? 0 : player.Length;
        GC.KeepAlive(recipients);
    }
    private static void NewHotPath(string requester, string recipient)
    {
        foreach (string player in new BattleParticipantPair(requester, recipient))
            sink += player == null ? 0 : player.Length;
    }

    internal static int Run()
    {
        var players = new[] { null, "", "host", "guest", "HOST", "主控",
            "a:b", "a/b", "e\u0301", "\u00e9", "重复", " " };
        foreach (string requester in players)
            foreach (string recipient in players)
                foreach (bool approved in new[] { false, true })
                    foreach (bool mutate in new[] { false, true })
                        foreach (bool fail in new[] { false, true })
                            Equal(requester, recipient, approved, mutate, fail,
                                "visit boundary and callback");

        var random = new Random(20261018);
        for (int i = 0; i < 22000; i++)
        {
            string from = players[random.Next(players.Length)];
            string to = players[random.Next(players.Length)];
            Equal(from, to, random.Next(2) == 0, random.Next(2) == 0,
                random.Next(4) == 0, "random native visit.update frame " + i);
        }
        var same = new Visit("same", "same", true);
        var records = new List<Delivery>();
        Check(Notify(same, true, records, false, false) == "accepted" &&
            records.Count == 2 && records.All(x => x.Recipient == "same" &&
                x.HasGrant),
            "both recipients equal: two notifications and both keep original grant rule");
        var callback = new Visit("first", "second", true);
        records.Clear();
        Check(Notify(callback, true, records, true, false) == "accepted" &&
            records.Count == 2 && records[0].HasGrant &&
            records[1].Recipient == "second" && records[1].Reason == "状态改变",
            "recipient pair is captured, but payload fields remain live per callback");
        records.Clear();
        Check(Notify(new Visit("first", "second", true), true, records, false, true)
            == "InvalidOperationException: visit.update transport callback failed" &&
            records.Count == 1, "send failure preserves original immediate abort");

        const int dispatches = 300000;
        long previous = Allocation(() => OldHotPath("host", "guest"), dispatches);
        long optimized = Allocation(() => NewHotPath("host", "guest"), dispatches);
        Check(previous > optimized && optimized == 0,
            "zero arrays allocated on the pair iteration path after warmup");
        Console.WriteLine("INFO: " + dispatches + " synthetic visit state notifications: old string[2] " +
            previous + " B, production struct iterator " + optimized +
            " B (managed allocation after warmup)");
        Console.WriteLine("INFO: " + checks +
            " extra visit recipient-order/callback/grant/allocation checks");
        GC.KeepAlive(sink);
        return checks;
    }
}
