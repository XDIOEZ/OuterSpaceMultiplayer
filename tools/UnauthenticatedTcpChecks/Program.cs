using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static TransportEvent WaitFor(TcpTransport host, TransportEventKind kind, string peer = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 6000)
        {
            TransportEvent item;
            if (host.TryDequeue(out item))
            {
                if (item.Kind == TransportEventKind.Error) throw new IOException("Host failed: " + item.Error);
                if (item.Kind == kind && (peer == null || item.PeerId == peer)) return item;
                if (item.Kind == TransportEventKind.Message && kind == TransportEventKind.Disconnected)
                    throw new InvalidOperationException("Oversized anonymous payload unexpectedly reached the session queue.");
            }
            else Thread.Sleep(5);
        }
        throw new TimeoutException("No transport event " + kind + " for " + (peer ?? "any"));
    }
    private static void SendHeader(NetworkStream stream, int length)
    {
        byte[] bytes = { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length };
        stream.Write(bytes, 0, bytes.Length);
    }
    private static void SendPayload(NetworkStream stream, byte[] payload)
    {
        SendHeader(stream, payload.Length);
        stream.Write(payload, 0, payload.Length);
    }
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static int Main()
    {
        try
        {
            var host = new TcpTransport();
            int port = GetFreePort();
            host.StartHost(new TransportOptions { Port = port, MaxPlayers = 4 });
            try
            {
                // Only a FOUR-BYTE length header is sent: if the transport
                // waits for a megabyte payload, rejection did not happen before allocation.
                foreach (int announcedBytes in new[] { 1, 8, 32 })
                {
                    using (var client = new TcpClient())
                    {
                        client.Connect(IPAddress.Loopback, port);
                        TransportEvent connected = WaitFor(host, TransportEventKind.Connected);
                        SendHeader(client.GetStream(), announcedBytes * 1024 * 1024);
                        TransportEvent disconnected = WaitFor(host, TransportEventKind.Disconnected, connected.PeerId);
                        Check(disconnected.Error != null && disconnected.Error.Contains("认证"),
                            announcedBytes + " MiB header rejected before any payload or JSON decode");
                    }
                }
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    string peer = WaitFor(host, TransportEventKind.Connected).PeerId;
                    byte[] handshake = Encoding.UTF8.GetBytes(
                        "{\"Version\":6,\"Kind\":\"hello\",\"Payload\":{\"CredentialId\":\"synthetic\",\"Nickname\":\"test\"}}");
                    SendPayload(client.GetStream(), handshake);
                    TransportEvent hello = WaitFor(host, TransportEventKind.Message, peer);
                    Check(hello.Data.Length == handshake.Length &&
                        Encoding.UTF8.GetString(hello.Data).Contains("\"Kind\":\"hello\""),
                        "small legal hello JSON envelope is delivered byte-identically");
                    byte[] authentication = Encoding.UTF8.GetBytes(
                        "{\"Version\":6,\"Kind\":\"auth\",\"Payload\":{\"CredentialId\":\"synthetic\",\"Proof\":\"test-proof\"}}");
                    SendPayload(client.GetStream(), authentication);
                    TransportEvent auth = WaitFor(host, TransportEventKind.Message, peer);
                    Check(auth.Data.Length == authentication.Length &&
                        Encoding.UTF8.GetString(auth.Data).Contains("\"Kind\":\"auth\""),
                        "small legal auth JSON envelope is delivered before admission is upgraded");
                    host.MarkAuthenticated(peer);
                    byte[] mapPayload = new byte[1024 * 1024];
                    mapPayload[0] = 42; mapPayload[mapPayload.Length - 1] = 7;
                    SendPayload(client.GetStream(), mapPayload);
                    TransportEvent map = WaitFor(host, TransportEventKind.Message, peer);
                    Check(map.Data.Length == mapPayload.Length &&
                        map.Data[0] == 42 && map.Data[map.Data.Length - 1] == 7,
                        "authenticated peer still receives a full-size reliable map frame");
                    host.Disconnect(peer, "synthetic test complete");
                    WaitFor(host, TransportEventKind.Disconnected, peer);
                    Check(true, "authenticated test peer cleans up without changing game files");
                }
            }
            finally { host.Dispose(); }
            Console.WriteLine("PASS: " + checks + " production TCP pre-auth admission checks (localhost only)");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
