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
    // Stand-in for the UI's result classification check; the actual
    // CommandSubmitStatus is compiled directly from production source.
    public sealed class CommandResult
    {
        public bool Success;
        public bool Applied;
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
        Check(PerformanceWindow.HistogramSchemaVersion == 1, "stable histogram schema identifier");
        Check(window.ExportBinCounts() == "1,1,0,1,0,0,0,1,0,0,0,1,0,0",
            "exported 14 fixed buckets preserve all five actual samples");
        bool invalidValue = false;
        try { window.Record(Double.NaN, 0); } catch (ArgumentOutOfRangeException) { invalidValue = true; }
        Check(invalidValue && window.Samples == 5, "invalid sample rejected without recording");
        bool invalidPercentile = false;
        try { window.UpperPercentile(101); } catch (ArgumentOutOfRangeException) { invalidPercentile = true; }
        Check(invalidPercentile, "invalid percentile rejected");
        window.Clear();
        Check(window.Samples == 0 && window.TotalBytes == 0 && window.MaxMs == 0, "reset");
        Check(window.ExportBinCounts() == "0,0,0,0,0,0,0,0,0,0,0,0,0,0",
            "cleared histogram does not leak prior window samples");
        window.Record(1200, -1);
        window.Record(2100, -1);
        Check(window.ExportBinCounts() == "0,0,0,0,0,0,0,0,0,0,0,0,0,2",
            "overflow bucket records >1000ms samples without losing counts");
        Check(window.UpperPercentile(95) == 2100,
            "overflow percentile is bounded by observed max, not invented bucket size");
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
            GroundCaptureChecks.Run(Check);
            RuntimeRevisionChecks.Run(Check);
            FleetMotionChecks.Run(Check);
            MapCodecChecks.Run(Check);
            CommandSubmitStatusChecks.Run(Check);
            DeferredCommandReplyChecks.Run(Check);
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
