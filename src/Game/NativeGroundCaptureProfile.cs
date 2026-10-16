using System;
using System.Diagnostics;
using System.Globalization;

namespace FreeWorldMultiplayer
{
    internal sealed partial class NativeMapReplicator
    {
        // Only the authoritative map capture path calls these probes, on Unity's main
        // thread. Opt-in matches SessionPerformance; no per-frame logs or worker access.
        private static readonly bool groundProfileEnabled =
            String.Equals(Environment.GetEnvironmentVariable("FWM_PROFILE"), "1", StringComparison.Ordinal)
            || Array.IndexOf(Environment.GetCommandLineArgs(), "-fwm-profile") >= 0;
        private static readonly GroundCaptureMetrics groundProfile = new GroundCaptureMetrics();
        private static long groundProfileLastReport = Stopwatch.GetTimestamp();

        private static long GroundProfileStart()
        {
            return groundProfileEnabled ? Stopwatch.GetTimestamp() : 0;
        }

        private static void GroundProfileStage(GroundCaptureStage stage, long started)
        {
            if (!groundProfileEnabled || started == 0) return;
            try
            {
                double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                groundProfile.Record(stage, ms);
            }
            catch { /* Observation must not alter a native capture transaction. */ }
        }

        private static void GroundProfileCompleted(long started, int scannedEntities, int changedEntities,
            int scannedFields, long scannedTiles, int changedTiles, int sampledPoses, int changedPoses)
        {
            if (!groundProfileEnabled || started == 0) return;
            bool flush = false;
            try
            {
                GroundProfileStage(GroundCaptureStage.Total, started);
                groundProfile.Complete(scannedEntities, changedEntities, scannedFields,
                    scannedTiles, changedTiles, sampledPoses, changedPoses);
                long now = Stopwatch.GetTimestamp();
                double seconds = (now - groundProfileLastReport) / (double)Stopwatch.Frequency;
                if (seconds < 30) return;
                groundProfileLastReport = now;
                flush = true;
                foreach (GroundCaptureStage stage in Enum.GetValues(typeof(GroundCaptureStage)))
                {
                    PerformanceWindow window = groundProfile.Window(stage);
                    if (window.Samples == 0) continue;
                    Logger.LogEngine("[FreeWorldMultiplayer][profile] stage=" + GroundCaptureMetrics.StageNames[(int)stage]
                        + " windowSeconds=" + seconds.ToString("F1", CultureInfo.InvariantCulture)
                        + " count=" + window.Samples
                        + " p50UpperMs=" + window.UpperPercentile(50).ToString("F2", CultureInfo.InvariantCulture)
                        + " p95UpperMs=" + window.UpperPercentile(95).ToString("F2", CultureInfo.InvariantCulture)
                        + " p99UpperMs=" + window.UpperPercentile(99).ToString("F2", CultureInfo.InvariantCulture)
                        + " maxMs=" + window.MaxMs.ToString("F2", CultureInfo.InvariantCulture)
                        + " histV=" + PerformanceWindow.HistogramSchemaVersion
                        + " bins=" + window.ExportBinCounts());
                }
                Logger.LogEngine("[FreeWorldMultiplayer][ground-counts] captures=" + groundProfile.Captures
                    + " entitiesScanned=" + groundProfile.ScannedEntities
                    + " entityChanges=" + groundProfile.ChangedEntities
                    + " fieldsScanned=" + groundProfile.ScannedFields
                    + " tilesScanned=" + groundProfile.ScannedTiles
                    + " tileChanges=" + groundProfile.ChangedTiles
                    + " posesSampled=" + groundProfile.SampledPoses
                    + " poseChanges=" + groundProfile.ChangedPoses);
            }
            catch { /* Diagnostic failures must never reject a map revision or lose an ACK. */ }
            finally { if (flush) groundProfile.Clear(); }
        }
    }
}
