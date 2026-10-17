using System;
using FreeWorldMultiplayer;

internal static class CommandSubmitStatusChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string missing = CommandSubmitStatus.ImmediateFeedback(null);
        check(missing != null && missing.Contains("未完成"), "missing result fails closed");

        string rejected = CommandSubmitStatus.ImmediateFeedback(new CommandResult
            { Success = false, Error = "银河状态正在重新同步" });
        check(rejected.Contains("未完成") && rejected.Contains("银河状态正在重新同步"),
            "synchronous refusal is visible with the actual reason");

        string generic = CommandSubmitStatus.ImmediateFeedback(new CommandResult { Success = false });
        check(generic.Contains("请求被拒绝"), "empty refusal has a safe fallback");

        string loading = CommandSubmitStatus.ImmediateFeedback(new CommandResult
            { Success = true, Applied = false, Error = "正在载入并同步地图" });
        check(loading == "正在载入并同步地图", "queued local command retains its waiting reason");

        string waiting = CommandSubmitStatus.ImmediateFeedback(new CommandResult
            { Success = true, Applied = false });
        check(waiting.Contains("等待房主") && !waiting.Contains("已完成"),
            "queued remote command is not mistaken for completed success");

        check(CommandSubmitStatus.ImmediateFeedback(new CommandResult
            { Success = true, Applied = true }) == null,
            "completed result leaves final feedback to the existing CommandCompleted event");
    }
}
