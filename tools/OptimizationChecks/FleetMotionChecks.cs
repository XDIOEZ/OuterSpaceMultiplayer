using System;
using FreeWorldMultiplayer;

internal static class FleetMotionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var tracker = new FleetMotionEvidence();
        check(tracker.Count == 0 && !tracker.HasPending(4), "fleet movement observer begins empty");
        check(tracker.Start("cmd-a", 4, "alpha", 2, 3) == null
            && tracker.Count == 1 && tracker.HasPending(4), "one native command is armed");
        check(tracker.Start("cmd-a", 4, "alpha", 2, 3) == null
            && tracker.Count == 1, "duplicate command does not restart observation");
        check(tracker.Sample(5, "alpha", 900, 900) == null
            && tracker.HasPending(4), "another fleet cannot resolve the pending command");
        check(tracker.Sample(4, "alpha", 2, 3) == null
            && tracker.HasPending(4), "unchanged pose does not prematurely report movement");
        FleetMotionObservation changed = tracker.Sample(4, "alpha", 2.02f, 3);
        check(changed != null && changed.CommandId == "cmd-a"
            && changed.Outcome == "position-changed" && tracker.Count == 0,
            "real host motion is reported and cleared exactly once");
        check(tracker.Sample(4, "alpha", 2.5f, 3) == null, "completed observation is one-shot");

        tracker.Start("old-command", 1, "alpha", 0, 0);
        FleetMotionObservation replaced = tracker.Start("new-command", 1, "alpha", 0, 0);
        check(replaced != null && replaced.CommandId == "old-command"
            && replaced.Outcome == "superseded" && tracker.Count == 1,
            "new order explicitly supersedes prior pending observation");
        changed = tracker.Sample(1, "beta", 0, 0);
        check(changed != null && changed.CommandId == "new-command"
            && changed.Outcome == "position-changed", "cross-galaxy motion is also observable");

        tracker.Start("no-move-yet", 7, "alpha", 4, 5);
        check(tracker.Sample(7, "alpha", float.NaN, 5) == null
            && tracker.HasPending(7), "invalid position is not misreported as movement");
        bool samplesSilent = true;
        for (int i = 1; i < FleetMotionEvidence.MaxObservedTicks; i++)
            samplesSilent &= tracker.Sample(7, "alpha", 4, 5) == null;
        check(samplesSilent && tracker.HasPending(7), "unchanged samples are quiet");
        FleetMotionObservation unchanged = tracker.Sample(7, "alpha", 4, 5);
        check(unchanged != null && unchanged.Outcome == "unchanged-after-120-host-ticks"
            && unchanged.CommandId == "no-move-yet" && tracker.Count == 0,
            "120 sampled host ticks give one neutral unchanged observation");

        for (int i = 0; i < FleetMotionEvidence.MaxPending; i++)
            tracker.Start("f" + i, 1000 + i, "alpha", 0, 0);
        check(tracker.Count == FleetMotionEvidence.MaxPending, "bounded pending fleet diagnostics");
        FleetMotionObservation capped = tracker.Start("overflow", 10000, "alpha", 0, 0);
        check(capped != null && capped.Outcome == "capacity-unobserved"
            && !tracker.HasPending(10000), "capacity cannot grow or silently replace a fleet");
        check(tracker.Cancel(1000, "observer-unavailable") != null && tracker.Count == 63,
            "invalid native observation is dropped once");
        tracker.Clear();
        check(tracker.Count == 0, "session release clears pending diagnostic references");
        bool rejected = false;
        try { tracker.Start("x", -1, "alpha", 0, 0); }
        catch (ArgumentException) { rejected = true; }
        check(rejected && tracker.Count == 0, "invalid UID does not pollute evidence");
    }
}
