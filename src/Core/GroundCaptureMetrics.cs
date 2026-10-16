using System;

namespace FreeWorldMultiplayer
{
    // Fixed slots: neither arbitrary log labels nor one dictionary entry per entity/map.
    internal enum GroundCaptureStage
    {
        Entities,
        Fields,
        Tiles,
        Poses,
        Personal,
        Total
    }

    // Pure counters used by the production 2D map capture probe and by logic tests.
    // Counting a scanned cell is not evidence that it changed or was sent on the wire.
    internal sealed class GroundCaptureMetrics
    {
        internal static readonly string[] StageNames =
        {
            "ground.capture.entities", "ground.capture.fields", "ground.capture.tiles",
            "ground.capture.poses", "ground.capture.personal", "ground.capture.total"
        };

        private readonly PerformanceWindow[] windows = new PerformanceWindow[StageNames.Length];
        internal long Captures { get; private set; }
        internal long ScannedEntities { get; private set; }
        internal long ChangedEntities { get; private set; }
        internal long ScannedFields { get; private set; }
        internal long ScannedTiles { get; private set; }
        internal long ChangedTiles { get; private set; }
        internal long SampledPoses { get; private set; }
        internal long ChangedPoses { get; private set; }

        internal GroundCaptureMetrics()
        {
            for (int i = 0; i < windows.Length; i++) windows[i] = new PerformanceWindow();
        }

        internal PerformanceWindow Window(GroundCaptureStage stage) { return windows[(int)stage]; }
        internal void Record(GroundCaptureStage stage, double elapsedMs)
        {
            Window(stage).Record(elapsedMs, -1);
        }

        internal void Complete(int scannedEntities, int changedEntities, int scannedFields,
            long scannedTiles, int changedTiles, int sampledPoses, int changedPoses)
        {
            if (scannedEntities < 0 || changedEntities < 0 || scannedFields < 0 ||
                scannedTiles < 0 || changedTiles < 0 || sampledPoses < 0 || changedPoses < 0)
                throw new ArgumentOutOfRangeException(nameof(scannedEntities), "Map capture counters cannot be negative.");
            Captures++;
            ScannedEntities += scannedEntities;
            ChangedEntities += changedEntities;
            ScannedFields += scannedFields;
            ScannedTiles += scannedTiles;
            ChangedTiles += changedTiles;
            SampledPoses += sampledPoses;
            ChangedPoses += changedPoses;
        }

        internal void Clear()
        {
            foreach (PerformanceWindow window in windows) window.Clear();
            Captures = ScannedEntities = ChangedEntities = ScannedFields = 0;
            ScannedTiles = ChangedTiles = SampledPoses = ChangedPoses = 0;
        }
    }
}
