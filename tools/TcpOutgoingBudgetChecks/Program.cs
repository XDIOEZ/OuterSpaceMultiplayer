using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception("FAILED: " + reason);
        checks++;
    }
    private static bool Throws<T>(Action invoke) where T : Exception
    {
        try { invoke(); }
        catch (T) { return true; }
        return false;
    }
    private static FieldInfo Field(Type type, string name)
    {
        FieldInfo field = type.GetField(name, Instance);
        if (field == null) throw new Exception("Missing production field: " + name);
        return field;
    }
    private static void Set(object source, string name, object value)
    { Field(source.GetType(), name).SetValue(source, value); }
    private static T Get<T>(object source, string name)
    { return (T)Field(source.GetType(), name).GetValue(source); }
    private static object AddSyntheticPeer(TcpTransport transport, string id)
    {
        Type peerType = typeof(TcpTransport).GetNestedType("Peer", BindingFlags.NonPublic);
        object peer = Activator.CreateInstance(peerType, true);
        Set(peer, "Id", id);
        // Dispose() closes every registered peer; even synthetic peers have
        // a real, unconnected TcpClient so teardown follows production logic.
        Set(peer, "Client", new TcpClient());
        var peers = (IDictionary)Field(typeof(TcpTransport), "peers").GetValue(transport);
        peers.Add(id, peer);
        return peer;
    }
    private static Queue<byte[]> Outgoing(object peer) { return Get<Queue<byte[]>>(peer, "Outgoing"); }

    private static void SyntheticSendChecks()
    {
        // Simulate the precise state created by the real WriteLoop after it
        // moves the head from Outgoing into InFlightBytes. These tests invoke
        // production TcpTransport.Send: its lock, capacity check and Clone.
        using (var transport = new TcpTransport())
        {
            object slow = AddSyntheticPeer(transport, "slow");
            object healthy = AddSyntheticPeer(transport, "healthy");
            object healthyTwo = AddSyntheticPeer(transport, "healthy-two");
            object healthyThree = AddSyntheticPeer(transport, "healthy-three");
            long limit = TransportQueue.MaxQueuedBytes;
            int frameLimit = TransportQueue.MaxMessageBytes;
            byte[] one = { 17 };
            Set(slow, "InFlightBytes", frameLimit);
            Set(slow, "OutgoingBytes", limit - frameLimit);
            for (int i = 0; i < 40; ++i)
                Check(Throws<IOException>(() => transport.Send("slow", one)) &&
                    Outgoing(slow).Count == 0 && Get<long>(slow, "OutgoingBytes") == limit - frameLimit,
                    "in-flight 128MiB + queued 128MiB rejects before Clone, even on repeated requests");
            transport.Send("healthy", one);
            transport.Send("healthy-two", one);
            transport.Send("healthy-three", one);
            Check(Outgoing(healthy).Count == 1 && Outgoing(healthyTwo).Count == 1
                && Outgoing(healthyThree).Count == 1 && Outgoing(slow).Count == 0,
                "a congested peer cannot block any of three other independently queued players");

            Set(slow, "OutgoingBytes", limit - frameLimit - 1);
            transport.Send("slow", one);
            Check(Outgoing(slow).Count == 1 && Get<long>(slow, "OutgoingBytes") == limit - frameLimit
                  && !Object.ReferenceEquals(Outgoing(slow).Peek(), one),
                "exact total-budget boundary accepts one frame and clones it once");
            Check(Throws<IOException>(() => transport.Send("slow", one)) && Outgoing(slow).Count == 1,
                "one additional byte after exact 256MiB total is rejected");
            Set(slow, "InFlightBytes", 0);
            transport.Send("slow", one);
            Check(Outgoing(slow).Count == 2, "after the in-flight writer completes, budget becomes available");

            Set(slow, "InFlightBytes", -1);
            Check(Throws<IOException>(() => transport.Send("slow", one)),
                "corrupt negative in-flight accounting fails closed");
            Set(slow, "InFlightBytes", 0);
            Set(slow, "OutgoingBytes", -1L);
            Check(Throws<IOException>(() => transport.Send("slow", one)),
                "corrupt negative queued accounting fails closed");
            Set(slow, "OutgoingBytes", long.MaxValue);
            Check(Throws<IOException>(() => transport.Send("slow", one)),
                "huge queued accounting cannot overflow into acceptance");

            Set(slow, "OutgoingBytes", 0L);
            Outgoing(slow).Clear();
            for (int i = 0; i < 512; i++) Outgoing(slow).Enqueue(new byte[] { (byte)i });
            Set(slow, "OutgoingBytes", 512L);
            Check(Throws<IOException>(() => transport.Send("slow", one)) && Outgoing(slow).Count == 512,
                "512-item FIFO limit continues to apply");
            Outgoing(slow).Dequeue();
            Set(slow, "OutgoingBytes", 511L);
            transport.Send("slow", one);
            Check(Outgoing(slow).Count == 512 && Get<long>(slow, "OutgoingBytes") == 512,
                "one freed FIFO slot can be reused without changing the budget");

            Set(slow, "Closed", true);
            Check(Throws<IOException>(() => transport.Send("slow", one)),
                "closed peer rejects instead of resuming its old queue");
            Check(Throws<InvalidOperationException>(() => transport.Send("missing-peer", one)),
                "unknown peer remains a synchronous enqueue failure");
            Check(Throws<ArgumentOutOfRangeException>(() => transport.Send("healthy", null)) &&
                  Throws<ArgumentOutOfRangeException>(() => transport.Send("healthy", Array.Empty<byte>())),
                "null and empty frames remain invalid");

            var concurrent = AddSyntheticPeer(transport, "concurrent");
            Set(concurrent, "InFlightBytes", frameLimit);
            Set(concurrent, "OutgoingBytes", limit - frameLimit - 100L);
            int acceptedConcurrent = 0, rejectedConcurrent = 0;
            Parallel.For(0, 400, _ =>
            {
                try { transport.Send("concurrent", one); Interlocked.Increment(ref acceptedConcurrent); }
                catch (IOException) { Interlocked.Increment(ref rejectedConcurrent); }
            });
            Check(acceptedConcurrent == 100 && rejectedConcurrent == 300 &&
                Outgoing(concurrent).Count == 100 &&
                Get<long>(concurrent, "OutgoingBytes") == limit - frameLimit,
                "concurrent sends respect the same 100-byte remaining budget under peer.Gate");

            var randomized = AddSyntheticPeer(transport, "randomized");
            var random = new Random(29029);
            for (int i = 0; i < 25000; i++)
            {
                int flight = random.Next(0, frameLimit + 1);
                long queued = (long)(random.NextDouble() * limit);
                byte[] data = new byte[random.Next(1, 1024)];
                Set(randomized, "InFlightBytes", flight);
                Set(randomized, "OutgoingBytes", queued);
                Outgoing(randomized).Clear();
                bool allowed = queued + flight + data.Length <= limit;
                bool accepted = !Throws<IOException>(() => transport.Send("randomized", data));
                Check(accepted == allowed, "random admission matches full queued+in-flight capacity");
                Check(Outgoing(randomized).Count == (allowed ? 1 : 0),
                    "random rejection never enqueues or duplicates a message");
                if (allowed)
                    Check(Get<long>(randomized, "OutgoingBytes") == queued + data.Length,
                        "random successful enqueue charges only the newly cloned payload");
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
    private static string WaitConnected(TcpTransport transport)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 6500)
        {
            TransportEvent item;
            if (transport.TryDequeue(out item))
            {
                if (item.Kind == TransportEventKind.Error)
                    throw new IOException("TCP host failed: " + item.Error);
                if (item.Kind == TransportEventKind.Connected) return item.PeerId;
            }
            else Thread.Sleep(4);
        }
        throw new TimeoutException("No TCP localhost Connected event.");
    }
    private static void ReadExactly(NetworkStream stream, byte[] result, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(result, offset, count - offset);
            if (read <= 0) throw new EndOfStreamException("TCP writer closed mid-frame");
            offset += read;
        }
    }
    private static byte[] ReadFrame(NetworkStream stream)
    {
        byte[] header = new byte[4];
        ReadExactly(stream, header, 4);
        int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        if (length < 1 || length > 8 * 1024 * 1024)
            throw new IOException("Invalid synthetic frame length: " + length);
        byte[] body = new byte[length];
        ReadExactly(stream, body, length);
        return body;
    }
    private static void LocalhostWriterChecks()
    {
        // The production Socket.Accept / NetworkStream.Write worker is used,
        // not a synthetic call to WriteLoop or a game/Steam session.
        using (var host = new TcpTransport())
        {
            int port = FreePort();
            host.StartHost(new TransportOptions { Port = port, MaxPlayers = 4 });
            using (var raw = new TcpClient())
            {
                raw.Connect(IPAddress.Loopback, port);
                raw.ReceiveTimeout = 7000;
                string peer = WaitConnected(host);
                byte[] first = { 2, 3, 5, 7, 11 };
                byte[] second = new byte[512 * 1024];
                second[0] = 13; second[second.Length - 1] = 29;
                host.Send(peer, first);
                host.Send(peer, second);
                // Deliberately delay reading to exercise queued/in-flight
                // writer state without high memory or an external network.
                Thread.Sleep(50);
                byte[] gotFirst = ReadFrame(raw.GetStream());
                byte[] gotSecond = ReadFrame(raw.GetStream());
                Check(gotFirst.Length == first.Length && gotFirst[0] == 2 && gotFirst[4] == 11,
                    "real TCP localhost writer preserves first frame payload and length prefix");
                Check(gotSecond.Length == second.Length && gotSecond[0] == 13 &&
                      gotSecond[gotSecond.Length - 1] == 29,
                    "real TCP localhost writer preserves second ordered frame after delayed reader");
                var clock = Stopwatch.StartNew();
                while (!host.IsDrained && clock.ElapsedMilliseconds < 6500) Thread.Sleep(3);
                Check(host.IsDrained, "drain becomes true only after queued and in-flight writes finish");
                host.Disconnect(peer, "outbound budget local test finished");
            }
        }
    }
    private static int Main()
    {
        try
        {
            SyntheticSendChecks();
            LocalhostWriterChecks();
            Console.WriteLine("PASS: " + checks + " production TCP outbound budget and localhost FIFO checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
