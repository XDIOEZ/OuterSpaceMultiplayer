using System;

namespace FreeWorldMultiplayer
{
    // Only immediate refusals and queue notices need feedback here.
    // Completed operations already raise CommandCompleted; do not show them twice.
    internal static class CommandSubmitStatus
    {
        internal static string ImmediateFeedback(CommandResult result)
        {
            if (result == null) return "操作未完成：会话未返回命令状态。";
            if (!result.Success)
                return "操作未完成：" + (String.IsNullOrWhiteSpace(result.Error) ? "请求被拒绝。" : result.Error);
            if (!result.Applied)
                return String.IsNullOrWhiteSpace(result.Error)
                    ? "操作已提交，等待房主执行回执。"
                    : result.Error;
            return null;
        }
    }
}
