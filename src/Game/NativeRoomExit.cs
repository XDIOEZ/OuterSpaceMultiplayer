using System;
using System.Linq;
using Script.FreeWorldAirShip.Galaxy;
using Script.FreeWorldAirShip.Galaxy.GalaxyField;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        // Retained after Dispose: Session may finish disconnecting inside Tick before
        // Entry observes termination. Every exit from a bound game needs native cover cleanup.
        internal bool RequiresMenuReturn { get; private set; }

        private void StopNativeRoomPresentation()
        {
            UserBean user = UserBean.tryGetInstance();
            if (user == null || user.gameData == null || ReferenceEquals(user.gameData, singlePlayerData)) return;
            if (!ReferenceEquals(user.gameData, World)
                && !Players.Values.Any(data => ReferenceEquals(data, user.gameData))) return;
            if (UIUntils.isInScene(UIUntils.SCENE_LIST.SCENE_COVER)) return;

            RequiresMenuReturn = true;
            // Final save/checkpoint collection has already completed in Session. Stop
            // native readers synchronously, while their OnDisable still has valid data.
            // backCover queues its scene unload via Loom and cannot close this frame gap.
            MapManager.isInitFinish = false;
            GameUiRoot ui = GameUiRoot.getInstance();
            if (ui != null)
            {
                // Keep the shared Canvas/dispatcher and runtime alive for native loading.
                // Native gameplay dialogs (including popups) must not Update the old world.
                foreach (BaseMonoDialog dialog in ui.GetComponentsInChildren<BaseMonoDialog>(true))
                    if (dialog != null) StopRoomObject(dialog.gameObject);
                StopRoomObject(ui.getGameLayer());
            }
            if (GameRoot.instance != null) StopRoomObject(GameRoot.instance.gameObject);
            if (GalaxySceneInterface.getInstance() != null) StopRoomObject(GalaxySceneInterface.getInstance().gameObject);
            if (GalaxyScene.Instance != null) StopRoomObject(GalaxyScene.Instance.gameObject);
            if (GalaxyFieldScene.Instance != null) StopRoomObject(GalaxyFieldScene.Instance.gameObject);
            Logger.LogEngine("[FreeWorldMultiplayer][RoomExit] native gameplay stopped before restoring the previous user context; returning to cover.");
        }

        private static void StopRoomObject(GameObject target)
        {
            if (target != null && target.activeSelf) target.SetActive(false);
        }
    }
}
