using System;
using FreeWorldMultiplayer;

namespace FreeWorldMultiplayer
{
    // Minimal stand-ins for the queue's event carrier. All queue code comes from production.
    public enum TransportEventKind { Connected, Disconnected, Message, Error }
    public sealed class TransportEvent
    {
        public TransportEventKind Kind;
        public byte[] Data;
        public string Error;
    }
}

internal static class Program
{
    private static int checks;

    private static void Check(bool condition, string reason)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("FAILED " + reason);
    }

    private static void Histogram()
    {
        var window = new PerformanceWindow();
        Check(window.Samples == 0 && window.UpperPercentile(95) == 0, "empty histogram");
        window.Record(0.1, 10);
        window.Record(0.3, 20);
        window.Record(2, 30);
        window.Record(20, -1);
        window.Record(500, 40);
        Check(window.Samples == 5 && window.TotalBytes == 100 && window.MaxBytes == 40, "counts and byte bounds");
        Check(window.UpperPercentile(50) == 2, "nearest-rank p50 upper bound");
        Check(window.UpperPercentile(95) == 500, "nearest-rank p95 upper bound");
        Check(window.UpperPercentile(99) == 500 && window.MaxMs == 500, "tail bound");
        bool invalidValue = false;
        try { window.Record(Double.NaN, 0); } catch (ArgumentOutOfRangeException) { invalidValue = true; }
        Check(invalidValue && window.Samples == 5, "invalid sample rejected without recording");
        bool invalidPercentile = false;
        try { window.UpperPercentile(101); } catch (ArgumentOutOfRangeException) { invalidPercentile = true; }
        Check(invalidPercentile, "invalid percentile rejected");
        window.Clear();
        Check(window.Samples == 0 && window.TotalBytes == 0 && window.MaxMs == 0, "reset");
    }

    private static void Queue()
    {
        var queue = new TransportQueue();
        Check(queue.TryReserve(12), "reserve message");
        Check(queue.Snapshot().ReservedBytes == 12, "reserved bytes visible");
        Check(queue.EnqueueReserved(new TransportEvent
            { Kind = TransportEventKind.Message, Data = new byte[12] }), "queue reserved message");
        var snap = queue.Snapshot();
        Check(snap.Events == 1 && snap.ReservedBytes == 0 && snap.QueuedBytes == 12, "queued bytes visible");
        Check(queue.Enqueue(new TransportEvent { Kind = TransportEventKind.Disconnected }), "terminal slot");
        snap = queue.Snapshot();
        Check(snap.Events == 2 && snap.QueuedBytes == 12, "terminal has no message bytes");
        TransportEvent result;
        Check(queue.TryDequeue(out result) && result.Kind == TransportEventKind.Message, "payload before disconnect");
        Check(queue.TryDequeue(out result) && result.Kind == TransportEventKind.Disconnected, "terminal after payload");
        Check(!queue.TryDequeue(out result), "drained queue");
        Check(queue.Snapshot().Events == 0 && queue.Snapshot().QueuedBytes == 0, "queue counters after drain");
        Check(!queue.TryReserve(0) && !queue.TryReserve(TransportQueue.MaxMessageBytes + 1), "invalid reservations");
        Check(queue.TryReserve(TransportQueue.MaxMessageBytes), "max first reservation");
        Check(queue.TryReserve(TransportQueue.MaxMessageBytes), "max second reservation");
        Check(!queue.TryReserve(1), "backpressure at bound");
        queue.ReleaseReservation(TransportQueue.MaxMessageBytes);
        queue.ReleaseReservation(TransportQueue.MaxMessageBytes);
        Check(queue.Snapshot().ReservedBytes == 0, "reservation cleanup");
    }

    private static int Main()
    {
        try
        {
            Histogram();
            Queue();
            Console.WriteLine("PASS: " + checks + " optimization logic checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
