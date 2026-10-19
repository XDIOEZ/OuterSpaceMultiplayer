using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    // Only game/runtime dependencies are synthetic; the result notification,
    // critical callback boundary, observer snapshot and fault behavior above
    // compile and run from the real CommandResultNotifications.cs.
    public sealed class CommandResult
    {
        internal string CommandId, PlayerId, PayloadJson;
        internal bool Success, Applied, Saved;
        internal long SaveGeneration;
    }
    internal sealed class WorldState
    {
        internal readonly Dictionary<string, CommandResult> Transactions =
            new Dictionary<string, CommandResult>();
    }
    internal static class Logger
    {
        internal static readonly List<string> Warnings = new List<string>();
        internal static readonly List<string> Errors = new List<string>();
        internal static bool ThrowOnLog;
        internal static void LogWarningEngine(string value)
        {
            if (ThrowOnLog) throw new InvalidOperationException("synthetic log failure");
            Warnings.Add(value);
        }
        internal static void LogErrorEngine(string value)
        {
            if (ThrowOnLog) throw new InvalidOperationException("synthetic log failure");
            Errors.Add(value);
        }
    }
    public sealed partial class MultiplayerSession
    {
        private readonly Dictionary<string, Action<CommandResult>> commandCallbacks =
            new Dictionary<string, Action<CommandResult>>();
        private string nativeFault;
        private bool Paused;
        public event Action<CommandResult> CommandCompleted;
        private readonly WorldState World = new WorldState();

        internal int TraceCount;
        internal int LockCount;
        internal bool ThrowClock, ThrowStatus, ThrowTrace;
        internal string LastError;
        internal bool IsFaulted { get { return nativeFault != null && Paused; } }
        internal int CallbackCount { get { return commandCallbacks.Count; } }
        internal bool IsPaused { get { return Paused; } }
        internal string Fault { get { return nativeFault; } }
        internal IDictionary<string, CommandResult> Stored { get { return World.Transactions; } }

        private void TraceCompletedFleetResult(CommandResult result)
        {
            TraceCount++;
            if (ThrowTrace) throw new InvalidOperationException("real business trace failure");
        }
        private void LockNativeFault(string reason)
        {
            LockCount++;
            nativeFault = reason;
            Paused = true;
            if (ThrowClock) throw new InvalidOperationException("pause notification failed");
            Error("原生事务中断，保留现场并禁止保存或重试副作用：" + reason);
        }
        private void Error(string value)
        {
            LastError = value;
            if (ThrowStatus) throw new IOException("UI status changed failed");
        }

        internal void Direct(string commandId, Action<CommandResult> callback)
        { commandCallbacks.Add(commandId, callback); }
        internal void Emit(CommandResult result) { NotifyCommandResult(result); }
        internal CommandResult HostFinish(CommandResult result)
        {
            World.Transactions.Add(result.CommandId, result);
            NotifyCommandResult(result);
            return result;
        }
        internal void ReceiveHostResult(CommandResult result)
        { NotifyCommandResult(result); }
        internal CommandResult AcceptRemoteExecution(CommandResult result)
        {
            World.Transactions.Add(result.CommandId, result);
            NotifyCommandResult(result);
            return result;
        }
    }
}
