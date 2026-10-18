using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int assertions;

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + reason);
        assertions++;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void FanoutUnit()
    {
        Check(Throws<ArgumentNullException>(() => ReliableCommitFanout.SendOrDisconnect(null, () => {})),
            "null send is never treated as success");
        Check(Throws<ArgumentNullException>(() => ReliableCommitFanout.SendOrDisconnect(() => {}, null)),
            "null disconnect fails validation");
        int deliveries = 0, kicks = 0;
        Check(ReliableCommitFanout.SendOrDisconnect(() => deliveries++, () => kicks++),
            "successful committed delta returns sent");
        Check(deliveries == 1 && kicks == 0, "success does not remove a healthy peer");
        Check(!ReliableCommitFanout.SendOrDisconnect(() => throw new IOException("queue full"), () => kicks++),
            "IO congestion disconnects instead of dropping update");
        Check(!ReliableCommitFanout.SendOrDisconnect(() => throw new InvalidOperationException("peer closed"), () => kicks++),
            "invalid transport state is also isolated");
        Check(kicks == 2, "both transport failures attempted peer disconnection");
        Check(Throws<ArgumentException>(() => ReliableCommitFanout.SendOrDisconnect(
            () => throw new ArgumentException("invalid payload"), () => kicks++)),
            "bad authoritative data must not be reclassified as recipient congestion");
        Check(kicks == 2, "programming/data errors never disconnect arbitrary peers");
        Check(Throws<InvalidOperationException>(() => ReliableCommitFanout.SendOrDisconnect(
            () => throw new IOException("queue full"),
            () => throw new InvalidOperationException("disconnect failed"))),
            "failed disconnect propagates: do not continue with a stale replica");
    }

    private static void AuthoritativeMapAndPersonal()
    {
        var receiptRoom = new MultiplayerSession();
        var full = new MapUpdate { CommitId = "c42", SessionId = "session-x", MapId = 922337200, AuthorityEpoch = 91,
            BaseRevision = 340, Revision = 341, NativeDeltaJson = "large canonical map payload" };
        full.Players.Add(new PlayerUpdate { PlayerId = "author", BaseRevision = 4, Revision = 5 });
        full.Players.Add(new PlayerUpdate { PlayerId = "owner", BaseRevision = 9, Revision = 10 });
        MapUpdate compact = receiptRoom.BuildReceipt(full);
        Check(compact.CommitId == full.CommitId && compact.SessionId == full.SessionId &&
            compact.MapId == full.MapId && compact.AuthorityEpoch == full.AuthorityEpoch &&
            compact.BaseRevision == full.BaseRevision && compact.Revision == full.Revision,
            "committed map ACK retains session/map/epoch/revision identity");
        Check(compact.NativeDeltaJson == null && compact.Players.Count == 2,
            "map ACK does not echo already-owned native delta to simulator");
        for (int i = 0; i < compact.Players.Count; i++)
            Check(compact.Players[i].PlayerId == full.Players[i].PlayerId &&
                compact.Players[i].Revision == full.Players[i].Revision &&
                compact.Players[i].BaseRevision == full.Players[i].BaseRevision &&
                !ReferenceEquals(compact.Players[i], full.Players[i]),
                "map ACK carries copied, exact personal revision watermarks " + i);
        var room = new MultiplayerSession();
        room.AddPeer("submitter"); room.AddPeer("observer-1"); room.AddPeer("observer-2");
        var transport = room.TestTransport;
        transport.SendFailures.Add("observer-1");
        transport.Send("submitter", System.Text.Encoding.UTF8.GetBytes("mapAck:committed:77"));
        room.PublishMapTo("observer-1", new MapUpdate { CommitId = "committed", Revision = 77 });
        room.PublishMapTo("observer-2", new MapUpdate { CommitId = "committed", Revision = 77 });
        Check(transport.Messages["submitter"].Count == 1 &&
            transport.Messages["submitter"][0] == "mapAck:committed:77",
            "successful author received its canonical ACK before third-party fanout");
        Check(transport.Disconnected.Contains("observer-1"),
            "queue-full observer was detached, not falsely recorded as up to date");
        Check(transport.Messages["observer-2"].Count == 1 &&
            transport.Messages["observer-2"][0] == "map:committed:77",
            "another observer still receives the committed map delta");
        Check(transport.Events[0].StartsWith("send:submitter:mapAck"),
            "map ACK is first transport event, not dependent on observer queue");
        Check(transport.Events[1].StartsWith("send:observer-1") &&
            transport.Events[2] == "disconnect:observer-1" &&
            transport.Events[3].StartsWith("send:observer-2"),
            "observer failure never blocks subsequent recipient");

        transport.SendFailures.Clear();
        room.ResetMessages();
        transport.SendFailures.Add("observer-2");
        transport.Send("submitter", System.Text.Encoding.UTF8.GetBytes("personalAck:72"));
        room.PublishPersonal("submitter", new PlayerUpdate { PlayerId = "submitted", Revision = 72 });
        Check(transport.Messages["submitter"].Count == 1 &&
            transport.Messages["submitter"][0] == "personalAck:72",
            "personal origin gets ACK regardless of observer send failures");
        Check(transport.Encodes == 1,
            "personal observer fanout shares exactly one authoritative serialization");
        Check(transport.Messages.ContainsKey("observer-1") == false &&
            transport.Disconnected.Contains("observer-2"),
            "already-disconnected peer cannot be treated as a good replica");
        Check(transport.Events[0].StartsWith("send:submitter:personalAck") &&
            transport.Events[1].StartsWith("send:observer-1") &&
            transport.Events[2] == "disconnect:observer-1" &&
            transport.Events[3].StartsWith("send:observer-2") &&
            transport.Events[4] == "disconnect:observer-2",
            "healthy sender unaffected by repeated failed observer queues");

        var fresh = new MultiplayerSession();
        fresh.AddPeer("p1"); fresh.AddPeer("p2"); fresh.AddPeer("p3");
        fresh.PublishPersonal("p1", new PlayerUpdate { PlayerId = "p1", Revision = 5 });
        Check(fresh.TestTransport.Encodes == 1 &&
            fresh.TestTransport.Messages["p2"][0] == fresh.TestTransport.Messages["p3"][0],
            "multiple healthy observers share the identical encoded payload");
        Check(!fresh.TestTransport.Messages.ContainsKey("p1"),
            "origin peer is not sent its own personal echo");
        fresh.SetHost(false);
        fresh.ResetMessages();
        fresh.PublishPersonal(null, new PlayerUpdate { PlayerId = "p2", Revision = 6 });
        Check(fresh.TestTransport.Messages.Count == 0, "clients never host-broadcast canonical updates");
        fresh.SetHost(true);
        fresh.TestTransport.IsRunning = false;
        fresh.PublishPersonal(null, new PlayerUpdate { PlayerId = "p2", Revision = 6 });
        Check(fresh.TestTransport.Messages.Count == 0,
            "stopped transport cannot pretend a committed update was queued");
    }

    private static void Stress()
    {
        var rng = new Random(20261018);
        for (int cycle = 0; cycle < 10000; cycle++)
        {
            var room = new MultiplayerSession();
            var transport = room.TestTransport;
            int n = rng.Next(2, 18), bad = rng.Next(1, n);
            for (int i = 0; i < n; i++)
            {
                string peer = "p" + i;
                room.AddPeer(peer);
                if (i == bad) transport.SendFailures.Add(peer);
            }
            room.PublishPersonal("p0", new PlayerUpdate { PlayerId = "p0", Revision = cycle });
            Check(transport.Disconnected.Count == 1 &&
                transport.Disconnected.Contains("p" + bad),
                "random faulty observer isolated exactly once cycle " + cycle);
            Check(!transport.Messages.ContainsKey("p0") && transport.Messages.Count == n-2,
                "other viewers all retained their committed delta cycle " + cycle);
            Check(transport.Encodes == 1,
                "one encoding per original room fanout cycle " + cycle);
        }
    }

    private static int Main()
    {
        try
        {
            FanoutUnit();
            AuthoritativeMapAndPersonal();
            Stress();
            Console.WriteLine("PASS: " + assertions +
                " production-linked reliable committed observer fanout checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
