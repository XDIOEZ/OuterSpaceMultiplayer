using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        // Acknowledged native callbacks and projections are NOT UI notices.
        // If one fails, this machine's native projection must be locked rather
        // than turning an already committed host result into a network reject.
        // The existing public CommandCompleted event is presentation-only.
        internal event Action<CommandResult> CommandProjectionRequired;
        private readonly HashSet<string> notifyingCommandResults = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> reportedCommandObserverFaults = new HashSet<string>(StringComparer.Ordinal);

        private void NotifyCommandResult(CommandResult result)
        {
            if (result == null) return;
            if (nativeFault != null) return;
            // Synchronous native/UI callbacks may reenter with this result.
            // Only the currently active stack is guarded; later definitive
            // results are still handled by their original command lifecycle.
            string key = result.PlayerId + "\n" + result.CommandId;
            if (!notifyingCommandResults.Add(key)) return;
            try { DeliverCommandResult(result); }
            finally { notifyingCommandResults.Remove(key); }
        }

        private void DeliverCommandResult(CommandResult result)
        {
            TraceCompletedFleetResult(result);

            Action<CommandResult> callback;
            if ((result.Applied || !result.Success) && commandCallbacks.TryGetValue(result.CommandId, out callback))
            {
                // Remove before the callback: synchronous nested results or a
                // retry of the same CommandId must not debit/apply a second time.
                commandCallbacks.Remove(result.CommandId);
                try { callback(result); }
                catch (Exception error)
                {
                    ReportCommandCompletionFault("command.native-callback", error, true);
                    return;
                }
            }
            if (nativeFault != null) return;

            Action<CommandResult> projections = CommandProjectionRequired;
            if (projections != null)
            {
                foreach (Action<CommandResult> projection in projections.GetInvocationList())
                {
                    try { projection(result); }
                    catch (Exception error)
                    {
                        // Native fleet/map callbacks can have changed original
                        // game objects. Do not continue with the next native
                        // callback or display a misleading success toast.
                        ReportCommandCompletionFault("command.native-projection", error, true);
                        return;
                    }
                    if (nativeFault != null) return;
                }
            }

            // Take a multicast snapshot once: a bad optional UI listener may
            // not prevent a later healthy listener from observing this exact
            // result, nor make Submit/result/execution handlers throw.
            Action<CommandResult> observers = CommandCompleted;
            if (observers == null) return;
            foreach (Action<CommandResult> observer in observers.GetInvocationList())
            {
                try { observer(result); }
                catch (Exception error)
                { ReportCommandCompletionFault("command.presentation", error, false); }
            }
        }

        private void ReportCommandCompletionFault(string stage, Exception error, bool nativeProjection)
        {
            // Never format error.Message/stack, PayloadJson, credentials or
            // private progress into a diagnostic emitted by this observer.
            string type = error.GetType().Name;
            if (nativeProjection && nativeFault == null)
            {
                string reason = "命令结果本机原生回调未能确认；阶段=" + stage + "；异常类型=" + type;
                try { LockNativeFault(reason); }
                catch (Exception)
                {
                    // LockNativeFault marks the barrier BEFORE broadcasting a
                    // pause/status. An unavailable UI or congested peer cannot
                    // undo that fail-closed state or escape back to the sender.
                    nativeFault = reason; Paused = true;
                    try { Error("本机原生结果回调中断，已暂停并保留现场：" + reason); }
                    catch (Exception) { }
                }
            }
            // Repeated failing UI listeners cannot flood logs or cause an
            // ever-growing dictionary keyed by untrusted player/payload data.
            string signature = stage + ":" + type;
            if (!reportedCommandObserverFaults.Contains(signature) && reportedCommandObserverFaults.Count < 32)
            {
                reportedCommandObserverFaults.Add(signature);
                try
                {
                    if (nativeProjection)
                        Logger.LogErrorEngine("[FreeWorldMultiplayer][command-result] stage=" + stage + " exceptionType=" + type);
                    else
                        Logger.LogWarningEngine("[FreeWorldMultiplayer][command-result] stage=" + stage + " exceptionType=" + type);
                }
                catch (Exception) { } // Logging never decides authoritative completion.
            }
        }
    }
}
