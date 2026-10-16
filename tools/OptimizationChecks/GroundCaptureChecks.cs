using System;
using FreeWorldMultiplayer;

internal static class GroundCaptureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var profile = new GroundCaptureMetrics();
        check(GroundCaptureMetrics.StageNames.Length == 6, "fixed ground profiling stage count");
        check(GroundCaptureMetrics.StageNames[(int)GroundCaptureStage.Tiles] == "ground.capture.tiles",
            "ground tile profile name remains stable");
        check(profile.Captures == 0 && profile.Window(GroundCaptureStage.Total).Samples == 0,
            "new ground profiling window is empty");

        profile.Record(GroundCaptureStage.Entities, 3.5);
        profile.Record(GroundCaptureStage.Tiles, 12.0);
        profile.Record(GroundCaptureStage.Total, 18.0);
        profile.Complete(120, 4, 20, 65536, 2, 16, 7);
        profile.Record(GroundCaptureStage.Entities, 1.2);
        profile.Record(GroundCaptureStage.Tiles, 7.9);
        profile.Record(GroundCaptureStage.Total, 11.0);
        profile.Complete(120, 0, 20, 65536, 0, 16, 0);

        check(profile.Captures == 2, "ground capture count");
        check(profile.ScannedEntities == 240 && profile.ChangedEntities == 4,
            "entity scans and changed entities remain distinct");
        check(profile.ScannedFields == 40, "native map field scan count");
        check(profile.ScannedTiles == 131072 && profile.ChangedTiles == 2,
            "tile scans and real modifications remain distinct");
        check(profile.SampledPoses == 32 && profile.ChangedPoses == 7,
            "pose sampling and pose change counts remain distinct");
        check(profile.Window(GroundCaptureStage.Tiles).Samples == 2
            && profile.Window(GroundCaptureStage.Tiles).UpperPercentile(95) == 16,
            "ground tile histogram p95 upper bucket");
        check(profile.Window(GroundCaptureStage.Total).Samples == 2
            && profile.Window(GroundCaptureStage.Total).MaxMs == 18,
            "ground total captures retain maximum");
        bool negativeRejected = false;
        try { profile.Complete(1, 0, 1, -1, 0, 1, 0); }
        catch (ArgumentOutOfRangeException) { negativeRejected = true; }
        check(negativeRejected && profile.Captures == 2,
            "invalid count rejected without polluting aggregates");

        profile.Clear();
        check(profile.Captures == 0 && profile.ScannedTiles == 0 && profile.SampledPoses == 0,
            "reset clears ground quantities");
        check(profile.Window(GroundCaptureStage.Entities).Samples == 0
            && profile.Window(GroundCaptureStage.Total).Samples == 0,
            "reset clears every ground timing histogram");
    }
}
