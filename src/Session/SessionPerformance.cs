using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private const double ProfileReportSeconds = 30;
        // Opt-in so a release session has no histogram work or periodic logs.
        private readonly bool profileEnabled = String.Equals(
            Environment.GetEnvironmentVariable("FWM_PROFILE"), "1", StringComparison.Ordinal)
            || Array.IndexOf(Environment.GetCommandLineArgs(), "-fwm-profile") >= 0;
        private readonly Dictionary<string, PerformanceWindow> profileStages = new Dictionary<string, PerformanceWindow>
        {
            { "transport.encode", new PerformanceWindow() },
            { "transport.decode", new PerformanceWindow() },
            { "transport.handle", new PerformanceWindow() },
            { "maps.publish", new PerformanceWindow() },
            { "personal.publish", new PerformanceWindow() },
            { "galaxy.broadcast", new PerformanceWindow() },
            { "world.save", new PerformanceWindow() }
        };
        private double profileElapsed;
        private long profileReceived, profileBatchPeak, profileBudgetStops, profileMapBackpressure;
        private long profileQueuedEventPeak, profileQueuedBytePeak, profileReservedBytePeak;

        private void RecordProfile(string stage, long startTicks, long bytes = -1)
        {
            if (!profileEnabled) return;
            PerformanceWindow window;
            if (!profileStages.TryGetValue(stage, out window)) return; // Fixed whitelist of metric names.
            double ms = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            window.Record(ms, bytes);
        }

        private void RecordInboundBatch(int count, bool queueDrained, bool mapBackpressure)
        {
            if (!profileEnabled) return;
            profileReceived += count;
            if (count > profileBatchPeak) profileBatchPeak = count;
            // This measures budget/backpressure stops, not the unknown exact transport queue depth.
            if (!queueDrained) profileBudgetStops++;
            if (mapBackpressure) profileMapBackpressure++;
            TransportQueueSnapshot pending;
            if (transport is RoomTransport) pending = ((RoomTransport)transport).ReadQueueSnapshot();
            else if (transport is TcpTransport) pending = ((TcpTransport)transport).ReadQueueSnapshot();
            else if (transport is SteamTransport) pending = ((SteamTransport)transport).ReadQueueSnapshot();
            else return; // Custom/test transport: its queue depth is unknown, not zero.
            if (pending.Events > profileQueuedEventPeak) profileQueuedEventPeak = pending.Events;
            if (pending.QueuedBytes > profileQueuedBytePeak) profileQueuedBytePeak = pending.QueuedBytes;
            if (pending.ReservedBytes > profileReservedBytePeak) profileReservedBytePeak = pending.ReservedBytes;
        }

        private void TickProfileReport(double seconds)
        {
            if (!profileEnabled) return;
            profileElapsed += seconds;
            if (profileElapsed < ProfileReportSeconds) return;
            double secondsInWindow = profileElapsed;
            profileElapsed = 0;
            try
            {
                foreach (var pair in profileStages)
                {
                    PerformanceWindow metric = pair.Value;
                    if (metric.Samples == 0) continue;
                    Logger.LogEngine("[FreeWorldMultiplayer][profile] stage=" + pair.Key
                        + " windowSeconds=" + secondsInWindow.ToString("F1", CultureInfo.InvariantCulture)
                        + " count=" + metric.Samples
                        + " p50UpperMs=" + metric.UpperPercentile(50).ToString("F2", CultureInfo.InvariantCulture)
                        + " p95UpperMs=" + metric.UpperPercentile(95).ToString("F2", CultureInfo.InvariantCulture)
                        + " p99UpperMs=" + metric.UpperPercentile(99).ToString("F2", CultureInfo.InvariantCulture)
                        + " maxMs=" + metric.MaxMs.ToString("F2", CultureInfo.InvariantCulture)
                        + " totalBytes=" + metric.TotalBytes + " maxBytes=" + metric.MaxBytes
                        + " role=" + (IsHost ? "host" : "guest"));
                }
                Logger.LogEngine("[FreeWorldMultiplayer][profile] stage=inbound.rounds"
                    + " windowSeconds=" + secondsInWindow.ToString("F1", CultureInfo.InvariantCulture)
                    + " handled=" + profileReceived + " batchPeak=" + profileBatchPeak
                    + " notDrainedFrames=" + profileBudgetStops + " mapBackpressureFrames=" + profileMapBackpressure
                    + " queuedEventPeak=" + profileQueuedEventPeak
                    + " queuedBytePeak=" + profileQueuedBytePeak + " reservedBytePeak=" + profileReservedBytePeak
                    + " role=" + (IsHost ? "host" : "guest"));
            }
            catch { /* Diagnostics must not affect authoritative state transitions. */ }
            finally
            {
                foreach (PerformanceWindow metric in profileStages.Values) metric.Clear();
                profileReceived = profileBatchPeak = profileBudgetStops = profileMapBackpressure = 0;
                profileQueuedEventPeak = profileQueuedBytePeak = profileReservedBytePeak = 0;
            }
        }
    }
}
