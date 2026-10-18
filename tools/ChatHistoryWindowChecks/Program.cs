using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }

    // Independent reference: original GetChatMessages + dialog's
    // Reverse().Take(maximum).Reverse() composition.
    internal static RoomChatMessage LegacyCopy(RoomChatMessage message)
    {
        return new RoomChatMessage
        {
            MessageId = message.MessageId, ClientMessageId = message.ClientMessageId,
            SenderPlayerId = message.SenderPlayerId, TargetPlayerId = message.TargetPlayerId,
            Text = message.Text, Error = message.Error, Delivery = message.Delivery,
            DeliverySequence = message.DeliverySequence
        };
    }

    private static List<RoomChatMessage> LegacyVisible(
        List<RoomChatMessage> source, string peer, int maximum)
    {
        var full = source.Where(x => x.SenderPlayerId == peer || x.TargetPlayerId == peer)
            .Select(LegacyCopy).ToList();
        return full.AsEnumerable().Reverse().Take(maximum).Reverse().ToList();
    }

    private static bool Same(RoomChatMessage a, RoomChatMessage b)
    {
        return a != null && b != null && a.MessageId == b.MessageId
            && a.ClientMessageId == b.ClientMessageId
            && a.SenderPlayerId == b.SenderPlayerId && a.TargetPlayerId == b.TargetPlayerId
            && a.Text == b.Text && a.Error == b.Error && a.Delivery == b.Delivery
            && a.DeliverySequence == b.DeliverySequence;
    }

    private static void Compare(List<RoomChatMessage> history,
        string peer, int maximum, string label)
    {
        var expected = LegacyVisible(history, peer, maximum);
        var actual = ChatHistoryWindow.Recent(history, peer, maximum, LegacyCopy);
        Check(expected.Count == actual.Count, label + ": count");
        for (int i = 0; i < actual.Count; i++)
        {
            Check(Same(expected[i], actual[i]), label + ": chronological fields " + i);
            Check(!ReferenceEquals(actual[i], history.First(m => m.MessageId == actual[i].MessageId)),
                label + ": returned chat record is a defensive copy " + i);
        }
    }

    private static long Allocated(Action action, int count)
    {
        for (int i = 0; i < 100; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; }
    }

    private static void MainChecks()
    {
        Check(Throws<ArgumentNullException>(() => ChatHistoryWindow.Recent(null, "a", 40, LegacyCopy)),
            "reject null history explicitly");
        Check(Throws<ArgumentOutOfRangeException>(() =>
            ChatHistoryWindow.Recent(new List<RoomChatMessage>(), "a", -1, LegacyCopy)),
            "reject negative requested window");
        Check(Throws<ArgumentNullException>(() =>
            ChatHistoryWindow.Recent(new List<RoomChatMessage>(), "a", 40, null)),
            "reject missing original defensive copier");
        var empty = new List<RoomChatMessage>();
        Compare(empty, "a", 0, "empty zero");
        Compare(empty, "a", 40, "empty limit");

        var history = new List<RoomChatMessage>();
        for (int i = 0; i < 256; i++)
        {
            history.Add(new RoomChatMessage
            {
                MessageId = "id" + i, ClientMessageId = "client" + i,
                SenderPlayerId = i % 3 == 0 ? "Alice" : i % 3 == 1 ? "Bob" : "Other",
                TargetPlayerId = i % 5 == 0 ? "Alice" : "Bob",
                Text = "message " + i, Error = i % 7 == 0 ? "lost" : null,
                Delivery = (RoomChatDelivery)(i % 3), DeliverySequence = i
            });
        }
        foreach (string peer in new[] { "Alice", "Bob", "Other", "alice", "none", null })
            foreach (int max in new[] { 0, 1, 2, 39, 40, 41, 128, 256, 10000 })
                Compare(history, peer, max, "fixed case peer=" + peer + " limit=" + max);

        var last = ChatHistoryWindow.Recent(history, "Alice", 40, LegacyCopy);
        Check(last.Count == 40, "dense conversation shows precisely the final 40");
        Check(last[0].DeliverySequence < last[39].DeliverySequence,
            "messages remain chronological");
        string oldText = history[history.Count - 1].Text;
        last[39].Text = "modified UI row";
        Check(history[history.Count - 1].Text == oldText,
            "editing displayed copy cannot mutate stored chat");

        var random = new Random(20261018);
        string[] ids = { "Alice", "Bob", "Carol", "alice", "", null, "玩家🙂" };
        for (int round = 0; round < 5000; round++)
        {
            int total = random.Next(0, 300);
            var source = new List<RoomChatMessage>(total);
            for (int i = 0; i < total; i++)
                source.Add(new RoomChatMessage
                {
                    MessageId = round + ":" + i, ClientMessageId = "c:" + i,
                    SenderPlayerId = ids[random.Next(ids.Length)],
                    TargetPlayerId = ids[random.Next(ids.Length)],
                    Delivery = (RoomChatDelivery)random.Next(3),
                    Text = i % 4 == 0 ? "中文换行\nemoji🙂" : "text-" + i,
                    Error = i % 9 == 0 ? "failed" : null,
                    DeliverySequence = i
                });
            string peer = ids[random.Next(ids.Length)];
            int maximum = new[] { 0, 1, 10, 40, 41, 255, 512 }[random.Next(7)];
            Compare(source, peer, maximum, "random room " + round);
        }

        // A newly received/acknowledged/failed message must retain its latest
        // payload on the next UI refresh without changing prior snapshots.
        var before = ChatHistoryWindow.Recent(history, "Alice", 40, LegacyCopy);
        history.Add(new RoomChatMessage { MessageId = "latest", ClientMessageId = "latest",
            SenderPlayerId = "Alice", TargetPlayerId = "Bob", Text = "pending",
            Delivery = RoomChatDelivery.Sending, DeliverySequence = 999 });
        var pending = ChatHistoryWindow.Recent(history, "Alice", 40, LegacyCopy);
        Check(pending[pending.Count - 1].Delivery == RoomChatDelivery.Sending,
            "new pending messages appear in the newest window");
        history[history.Count - 1].Delivery = RoomChatDelivery.Delivered;
        history[history.Count - 1].Text = "acknowledged";
        var delivered = ChatHistoryWindow.Recent(history, "Alice", 40, LegacyCopy);
        Check(delivered[delivered.Count - 1].Delivery == RoomChatDelivery.Delivered
            && delivered[delivered.Count - 1].Text == "acknowledged",
            "ACK refresh shows the real final delivery status");
        Check(pending[pending.Count - 1].Delivery == RoomChatDelivery.Sending,
            "previous UI snapshot does not change retroactively");
        history[history.Count - 1].Delivery = RoomChatDelivery.Failed;
        history[history.Count - 1].Error = "server rejected";
        var failed = ChatHistoryWindow.Recent(history, "Alice", 40, LegacyCopy);
        Check(failed[failed.Count - 1].Delivery == RoomChatDelivery.Failed
            && failed[failed.Count - 1].Error == "server rejected",
            "failure reason is available to the same dialog");
        Check(before.Count == 40 && before[39].MessageId != "latest",
            "old UI snapshot cannot see later arrivals");

        var dense = new List<RoomChatMessage>();
        for (int i = 0; i < 256; i++)
            dense.Add(new RoomChatMessage
            {
                MessageId = "dense:" + i, ClientMessageId = "c",
                SenderPlayerId = "Alice", TargetPlayerId = "Bob", Text = "ok",
                Delivery = RoomChatDelivery.Delivered, DeliverySequence = i
            });
        Action original = () => { if (LegacyVisible(dense, "Bob", 40).Count != 40) throw new InvalidOperationException(); };
        var actualSession = new MultiplayerSession();
        foreach (RoomChatMessage message in dense) actualSession.TestAddChat(message);
        Compare(dense, "Bob", 40, "production-session-parity");
        var sessionRows = actualSession.GetRecentChatMessages("Bob", 40);
        Check(sessionRows.Count == 40 && sessionRows[0].DeliverySequence == 216
            && sessionRows[39].DeliverySequence == 255,
            "actual production session partial returns exactly the last 40 messages");
        Action optimized = () => { if (actualSession.GetRecentChatMessages("Bob", 40).Count != 40) throw new InvalidOperationException(); };
        const int refreshes = 12000;
        long legacyBytes = Allocated(original, refreshes);
        long currentBytes = Allocated(optimized, refreshes);
        Check(currentBytes < legacyBytes, "UI window avoids cloning the oldest 216 messages");
        Console.WriteLine("INFO: " + refreshes + " synthetic chat UI refreshes × 256 messages / 40 shown: original "
            + legacyBytes + " B, production window " + currentBytes + " B (managed allocations, warmed)");
    }

    public static int Main()
    {
        try
        {
            MainChecks();
            Console.WriteLine("PASS: " + checks + " production-linked chat history window checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
