using System;
using System.IO;
using FreeWorldMultiplayer;

internal static class OptionalPeerNoticeChecks
{
    internal static void Run(Action<bool, string> check)
    {
        int notices = 0, disconnected = 0;
        OptionalPeerNoticeOutcome normal = OptionalPeerNotice.SendOrDisconnect(
            () => notices++, () => disconnected++);
        check(normal == OptionalPeerNoticeOutcome.Sent && notices == 1 && disconnected == 0,
            "ordinary refusal notifies sender without disconnecting");

        OptionalPeerNoticeOutcome overflow = OptionalPeerNotice.SendOrDisconnect(
            () => throw new IOException("synthetic full queue"),
            () => disconnected++);
        check(overflow == OptionalPeerNoticeOutcome.Disconnected && disconnected == 1,
            "failed refusal notice isolates only the blocked sender");

        int otherPeerHandled = 0;
        OptionalPeerNotice.SendOrDisconnect(
            () => throw new InvalidOperationException("peer no longer connected"),
            () => disconnected++);
        otherPeerHandled++;
        check(disconnected == 2 && otherPeerHandled == 1,
            "missing-peer refusal does not stop processing another player's next message");

        OptionalPeerNoticeOutcome doubleFault = OptionalPeerNotice.SendOrDisconnect(
            () => throw new IOException("reply send failed"),
            () => throw new IOException("disconnect failed"));
        check(doubleFault == OptionalPeerNoticeOutcome.DisconnectFailed,
            "even double-faulted advisory send and disconnect cannot escape the host loop");

        int authoritativeCommits = 0;
        OptionalPeerNotice.SendOrDisconnect(() => notices++, () => disconnected++);
        authoritativeCommits++;
        check(authoritativeCommits == 1 && disconnected == 2,
            "healthy subsequent processing still reaches its independent authority path");

        bool unrelatedFaultEscapes = false;
        try
        {
            OptionalPeerNotice.SendOrDisconnect(
                () => throw new FormatException("invalid protocol serialization"),
                () => disconnected++);
        }
        catch (FormatException) { unrelatedFaultEscapes = true; }
        check(unrelatedFaultEscapes && disconnected == 2,
            "nontransport/programmer errors are not silently converted into success");

        bool missingDependencyRejected = false;
        try { OptionalPeerNotice.SendOrDisconnect(null, () => disconnected++); }
        catch (ArgumentNullException) { missingDependencyRejected = true; }
        check(missingDependencyRejected, "missing notice callback rejects invalid helper setup");
    }
}
