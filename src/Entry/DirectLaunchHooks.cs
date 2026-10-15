using System;
using System.Reflection;
using HarmonyLib;

namespace FreeWorldMultiplayer
{
    internal static class DirectLaunchHooks
    {
        private const string Id = "com.catstudio.freeworldmultiplayer.direct";
        private static Harmony harmony;
        private static bool reported;
        internal static void Install()
        {
            if (harmony != null) return;
            harmony = new Harmony(Id);
            try
            {
                // CHANNEL_STEAM is absent in some reference builds. Those legitimately have no channel login methods.
                MethodInfo login = AccessTools.Method(typeof(ChannelSteam), "requestUID");
                MethodInfo launch = AccessTools.Method(typeof(ChannelSteam), "TryLaunchSteam");
                if (login != null) harmony.Patch(login, prefix: new HarmonyMethod(typeof(DirectLaunchHooks), nameof(RejectSteamLogin)));
                if (launch != null) harmony.Patch(launch, prefix: new HarmonyMethod(typeof(DirectLaunchHooks), nameof(RejectSteamLogin)));
            }
            catch { Uninstall(); throw; }
        }
        private static bool RejectSteamLogin()
        {
            var runtime = MultiplayerRuntime.Instance;
            if (runtime == null || !runtime.DirectLaunch) return true;
            const string message = "当前为独立 IP 直连模式，Steam 登录与自动启动已禁用。请通过“联机 V1”使用本机联机身份。";
            runtime.ReportUiFailure(message);
            if (!reported)
            {
                reported = true;
                Logger.LogEngine("[FreeWorldMultiplayer] 直连启动拒绝了 Steam 渠道登录/自动启动；未提供虚构 Steam UID。");
                if (UIDispatcher.Canvas != null) FairyUIDispatcher.showToast(message);
            }
            return false;
        }
        internal static void Uninstall()
        {
            if (harmony != null) harmony.UnpatchAll(Id);
            harmony = null; reported = false;
        }
    }
}
