using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace FreeWorldMultiplayer
{
    internal static class NativeGuestExitHooks
    {
        private static bool GuestWorld
        {
            get { return GameBridge.Active != null && !GameBridge.Active.Context.IsHost; }
        }

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(MenuDialog), "onEvent_Backcover", "ReturnPrefix");
            Patch(harmony, typeof(MenuDialog), "onEvent_Exitgame", "ExitPrefix");
            Patch(harmony, typeof(FairyUIDispatcher), "showArchiveDialog", "ArchiveDialogPrefix");
            MethodInfo[] saves = typeof(FairyUIDispatcher).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(method => method.Name == "saveArchive").ToArray();
            if (saves.Length == 0) throw new MissingMethodException(typeof(FairyUIDispatcher).FullName, "saveArchive");
            foreach (MethodInfo save in saves)
                harmony.Patch(save, new HarmonyMethod(typeof(NativeGuestExitHooks), "SaveArchivePrefix"));
        }

        private static void Patch(Harmony harmony, Type owner, string name, string prefix)
        {
            MethodInfo method = AccessTools.Method(owner, name);
            if (method == null) throw new MissingMethodException(owner.FullName, name);
            harmony.Patch(method, new HarmonyMethod(typeof(NativeGuestExitHooks), prefix));
        }

        private static bool ReturnPrefix()
        {
            MultiplayerRuntime runtime = MultiplayerRuntime.Instance;
            return runtime == null || !runtime.TryQueueGuestExit(false);
        }

        private static bool ExitPrefix()
        {
            MultiplayerRuntime runtime = MultiplayerRuntime.Instance;
            return runtime == null || !runtime.TryQueueGuestExit(true);
        }

        private static bool ArchiveDialogPrefix(bool __0)
        {
            if (!GuestWorld || !__0) return true;
            ShowSaveNotice();
            return false;
        }

        private static bool SaveArchivePrefix(object[] __args)
        {
            if (!GuestWorld) return true;
            // SaveAs writes Archive directly, so the lower UserBean save guard is too late.
            // An automatic save must remain silent; callers receive failure, never fake success.
            bool automatic = __args.Any(argument => argument is ArchiveSaveDialog.SaveMod
                && (ArchiveSaveDialog.SaveMod)argument == ArchiveSaveDialog.SaveMod.AutoSave);
            if (!automatic) ShowSaveNotice();
            foreach (object argument in __args)
                if (argument is callbackInt) ((callbackInt)argument)(Callback.FAILED);
            return false;
        }

        private static void ShowSaveNotice()
        {
            FairyUIDispatcher.showToast("联机世界由房主统一保存，客人无需创建存档。", false);
        }
    }
}
