using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private New_UIExpeditionDialog visitTransportDialog;
        private PlayerVisitGrant visitTransportGrant;

        public BridgeResult ValidatePlayerFleetContact(string actorPlayerId, long actorFleetId, string targetPlayerId, long targetFleetId)
        {
            return Run(() =>
            {
                if (actorPlayerId == targetPlayerId || !Players.ContainsKey(actorPlayerId) || !Players.ContainsKey(targetPlayerId))
                    throw new NativeRejectionException("交流目标不是另一位玩家。");
                FWFleet actor = TransferFleet(actorPlayerId, actorFleetId), target = TransferFleet(targetPlayerId, targetFleetId);
                if (Context.OwnerOfEntity(actorFleetId) != actorPlayerId || Context.OwnerOfEntity(targetFleetId) != targetPlayerId)
                    throw new NativeRejectionException("交互舰队归属已变化。");
                AirShipFleetInfoBean a = FleetView(actorPlayerId, actor), b = FleetView(targetPlayerId, target);
                if (!FWPlayerLocationGuard.HasValidFleetPosition(Players[actorPlayerId], actor)
                    || !FWPlayerLocationGuard.HasValidFleetPosition(Players[targetPlayerId], target)
                    || a.getGalaxyId() != b.getGalaxyId()) throw new NativeRejectionException("双方舰队不在同一物理星系。");
                Vector3 ap = (Vector3)NativeWire.Call(a, "getCurPos"), bp = (Vector3)NativeWire.Call(b, "getCurPos");
                if (AirShipTools.getDistance(ap, bp) > G_GalaxyStellarElement.getDefaultInteractionDistance(15))
                    throw new NativeRejectionException("双方舰队已超出原生交互距离。");
            });
        }

        public BridgeResult OpenVisitTransport(PlayerVisitGrant grant)
        {
            return Run(() =>
            {
                if (grant == null || grant.RequesterPlayerId != Context.LocalPlayerId || string.IsNullOrEmpty(grant.Token))
                    throw new NativeRejectionException("本次参观赠送授权无效。");
                FWGameData sender = Players[grant.RequesterPlayerId], recipient = Players[grant.RecipientPlayerId];
                FWStarMapInfo source = sender.getMothershipStarMapInfo(), destination = recipient.getMothershipStarMapInfo();
                if (source.starMapId != grant.SourceMotherShipMapId || destination.starMapId != grant.DestinationMotherShipMapId
                    || sender.galaxyBlock.playerFleet.getUID() != grant.RequesterFleetId || recipient.galaxyBlock.playerFleet.getUID() != grant.RecipientFleetId)
                    throw new NativeRejectionException("本次赠送母舰端点已变化，请重新申请。");
                using (EnterPlayer(Context.LocalPlayerId))
                {
                    New_UIExpeditionDialog dialog = FairyUIDispatcher.showNewExpeditionDialog_Start(FleetView(Context.LocalPlayerId), false);
                    visitTransportDialog = dialog; visitTransportGrant = grant;
                    dialog.doExpedition_Start(source);
                    MethodInfo create = typeof(Expedition_PointInfoMothership).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                        .Single(m => m.Name == "TryCreate" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(object));
                    object[] args = { source, destination, true, null };
                    if (!(bool)create.Invoke(null, args)) throw new InvalidOperationException("原生母舰运输路线卡创建失败。");
                    Expedition_PointInfoBase point = (Expedition_PointInfoBase)args[3];
                    point.distance = 0; point.distance_str = "0"; point.needTime_second = 0; point.totleHour = 0;
                    foreach (ExpeditionVehicleBase vehicle in point.vehicleList) { vehicle.needTime_second = 0; vehicle.totleHour = 0; }
                    PanelStartShowBaseItems list = dialog.PanelReady.PanelStart.panelStartShowBaseItems;
                    UIExpeditionSelectItem_Point row = list.createBaseItem();
                    NativeWire.Call(row, "setContent", point);
                    NativeWire.Call(list, "selectItem", row, true);
                    LockVisitTransport(dialog);
                }
            });
        }

        internal bool IsVisitDialog(New_UIExpeditionDialog dialog) { return dialog != null && dialog == visitTransportDialog && visitTransportGrant != null; }
        internal void LockVisitTransport(New_UIExpeditionDialog dialog)
        {
            if (!IsVisitDialog(dialog)) return;
            dialog.PanelReady.PanelStart.StartBaseItem.setInteractable(false);
            dialog.PanelReady.PanelStart.TargetBaseItem.setInteractable(false);
            Transform swap = dialog.transform.Find("PanelReady/but_Switch");
            if (swap == null) throw new InvalidOperationException("原版运输页缺少交换端点按钮。");
            swap.GetComponent<Button>().interactable = false;
            Transform role = dialog.transform.Find("PanelFill/Role");
            if (role == null) throw new InvalidOperationException("原版运输页缺少人员选择面板。");
            role.gameObject.SetActive(false); dialog.rolePicker.hide();
        }
        internal bool ConfirmVisitTransport(New_UIExpeditionDialog dialog)
        {
            if (!IsVisitDialog(dialog)) return false;
            New_ExpeditionPakTeams team = dialog.pakTeam;
            PlayerVisitGrant grant = visitTransportGrant;
            if (team.startMapInfo == null || team.targetStarMapInfo == null
                || team.startMapInfo.starMapId != grant.SourceMotherShipMapId || team.targetStarMapInfo.starMapId != grant.DestinationMotherShipMapId
                || team.startFleet != null || team.targetFleet != null || team.roles.Count != 0 || team.heroBeans.Count != 0 || team.animalBeans.Count != 0)
                throw new NativeRejectionException("参观赠送仅允许从自己的母舰向已同意的母舰赠送物资。");
            if (team.things.Count == 0 || team.vehicleBean == null) { FairyUIDispatcher.showToast("请选择赠送物资和原生运输载具。"); return true; }
            if (team.totleWeight > team.getVechicleBeringSupport()) { FairyUIDispatcher.showToast("所选赠送物资超过载具容量。"); return true; }
            FairyUIDispatcher.showTip("确认将所选物资赠送给对方母舰？", LanguageDef.getTextByID(Lan.btn_Confirm), LanguageDef.getTextByID(Lan.btn_Cancel), choice =>
            {
                if (choice != Tip.TIP_CONFIRM || !IsVisitDialog(dialog)) return;
                try { SubmitNativeTransfer(new FWExpeditionGoalPos(FWBagGoalPos.BagType._无, FWGoalPos.GoalType._STAR_MAP_INFO, grant.SourceMotherShipMapId),
                    new FWExpeditionGoalPos(FWBagGoalPos.BagType._无, FWGoalPos.GoalType._STAR_MAP_INFO, grant.DestinationMotherShipMapId), -1,
                    null, null, team.things, team.vehicleBean.getVechicleDef().ID, false, () =>
                    { if (dialog != null) NativeWire.Call(dialog, "close"); visitTransportDialog = null; visitTransportGrant = null; }); }
                catch (NativeRejectionException rejected) { FairyUIDispatcher.showToast(rejected.Message); }
            });
            return true;
        }
        private void ApplyVisitAuthorization(TransferRequest request, Manifest manifest)
        {
            if (visitTransportDialog == null || visitTransportGrant == null) return;
            if (request.SourceMapId != visitTransportGrant.SourceMotherShipMapId || request.DestinationMapId != visitTransportGrant.DestinationMotherShipMapId) return;
            if (manifest.Heroes.Count != 0 || manifest.Animals.Count != 0 || request.SourceFleetId >= 0 || request.DestinationFleetId >= 0)
                throw new NativeRejectionException("参观赠送不允许转移雇员、动物或交换方向。");
            request.VisitToken = visitTransportGrant.Token;
            if (!Context.IsVisitTransportAuthorized(request)) throw new NativeRejectionException("参观赠送授权已失效，请重新申请。");
        }
        private void ValidateGiftCarrier(TransferRecord transfer, Manifest manifest)
        {
            TransferRequest r = transfer.Request;
            string recipient = transfer.GiftRecipientPlayerId;
            string destinationOwner = r.DestinationMapId >= 0 ? Context.OwnerOfMap(r.DestinationMapId) : null;
            bool foreignMother = destinationOwner != null && destinationOwner != transfer.PlayerId
                && Players[destinationOwner].getMothershipStarMapInfo().starMapId == r.DestinationMapId;
            if (string.IsNullOrEmpty(recipient))
            { if (foreignMother) throw new InvalidOperationException("向其他玩家母舰赠送需要对方本次同意。"); return; }
            if (!Players.ContainsKey(recipient) || recipient == transfer.PlayerId || !foreignMother || recipient != destinationOwner
                || r.SourceMapId != Players[transfer.PlayerId].getMothershipStarMapInfo().starMapId
                || r.DestinationMapId != Players[recipient].getMothershipStarMapInfo().starMapId
                || r.SourceFleetId >= 0 || r.DestinationFleetId >= 0 || manifest.Heroes.Count != 0 || manifest.Animals.Count != 0
                || transfer.GiftSourceFleetId != Players[transfer.PlayerId].galaxyBlock.playerFleet.getUID()
                || transfer.GiftDestinationFleetId != Players[recipient].galaxyBlock.playerFleet.getUID()
                || !Context.IsVisitTransportAuthorized(r)) throw new InvalidOperationException("参观赠送方向、资产或一次授权不匹配。");
            BridgeResult contact = ValidatePlayerFleetContact(transfer.PlayerId, transfer.GiftSourceFleetId, recipient, transfer.GiftDestinationFleetId);
            if (!contact.Success) throw new InvalidOperationException("赠送提交时双方真实母舰不再接触：" + contact.Error);
            NativeGiftDropPoint(recipient);
        }
        private FWPoint NativeGiftDropPoint(string recipient)
        {
            FWStarMapInfo target = Players[recipient].getMothershipStarMapInfo();
            FWPoint cell = target.starSpread.zones.Where(z => z.allianceID == RoleAlliance.Alliance_MINE && z.getDef().SubType == 5)
                .SelectMany(z => z.cells).FirstOrDefault(p => p != null);
            // Empty mothership presets need not define a transport zone. Use an existing real
            // resident/storage/floor cell, never invented coordinates or another scene's grid.
            if (cell == null) cell = PlayerHeroes(recipient).Where(h => h.isInMap(target.starMapId) && !h.isDead()).Select(h => h.cell).FirstOrDefault(p => p != null);
            if (cell == null) cell = target.starSpread.things.Select(t => t.cell).Concat(target.starSpread.floors.Select(f => f.cell)).FirstOrDefault(p => p != null);
            if (cell == null) throw new NativeRejectionException("收货母舰尚无已确认的卸货位置；请先在原版母舰内设置运输区。");
            return cell;
        }
        private void DeliverNativeGift(TransferRecord transfer, FWExpedition expedition, Cargo cargo)
        {
            FWStarMapInfo target = Players[transfer.GiftRecipientPlayerId].getMothershipStarMapInfo();
            if (expedition.endStarMapId != target.starMapId || expedition.roleUidList.Count != 0 || expedition.animalUidList.Count != 0)
                throw new InvalidOperationException("赠送唯一运输载体与已保存目标不一致。");
            FWPoint cell = NativeGiftDropPoint(transfer.GiftRecipientPlayerId);
            if (expedition.things.Any(t => target.starSpread.things.Any(x => x.getUID() == t.getUID())))
                throw new InvalidOperationException("赠送物资同时存在于运输单与收货母舰。");
            FWThingBean[] delivered = expedition.things.ToArray();
            foreach (FWThingBean item in delivered)
            {
                item.inBuildUID = -1; item.clearTOHR(); item.setCell(new FWPoint(cell.x, cell.y));
                target.starSpread.things.Add(item);
                expedition.things.Remove(item);
            }
            Players[transfer.PlayerId].expeditionSeq.Remove(expedition);
            cargo.Delivered = true; transfer.NativeCargoJson = NativeWire.Json(cargo);
            // Presentation may throw. Complete the unique persistent move first, so a retry
            // never leaves or credits the same UID in both inventories.
            if (CurrentMapId == target.starMapId && MapManager.isInitFinish)
                using (EnterPlayer(transfer.GiftRecipientPlayerId)) foreach (FWThingBean item in delivered) target.starSpread.addThingToMap(item);
        }
    }
    internal static class NativePlayerVisitHooks
    {
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(New_UIExpeditionDialog), "organizeUI", null, "RefreshPostfix");
            Patch(harmony, typeof(New_UIExpeditionDialog), "showTab", null, "RefreshPostfix");
            Patch(harmony, typeof(New_UIExpedition_PanelReady), "onEvent_Switch", "ReadyPrefix", null);
            Patch(harmony, typeof(New_UIExpedition_PanelStart), "showItems", "StartPrefix", null);
            Patch(harmony, typeof(New_UIExpeditionDialog), "showPlayerOrigins", "DialogPrefix", null);
            Patch(harmony, typeof(New_UIExpedition_PanelBottom), "onEvent_Create", "CreatePrefix", null);
        }
        private static void Patch(Harmony h, Type t, string n, string pre, string post)
        {
            MethodInfo m = AccessTools.Method(t, n); if (m == null) throw new MissingMethodException(t.FullName, n);
            h.Patch(m, pre == null ? null : new HarmonyMethod(typeof(NativePlayerVisitHooks), pre), post == null ? null : new HarmonyMethod(typeof(NativePlayerVisitHooks), post));
        }
        private static void RefreshPostfix(New_UIExpeditionDialog __instance) { if (Bridge != null) Bridge.LockVisitTransport(__instance); }
        private static bool DialogPrefix(New_UIExpeditionDialog __instance) { return Bridge == null || !Bridge.IsVisitDialog(__instance); }
        private static bool ReadyPrefix(New_UIExpedition_PanelReady __instance) { return DialogPrefix(__instance.dialog); }
        private static bool StartPrefix(New_UIExpedition_PanelStart __instance) { return DialogPrefix(__instance.ready.dialog); }
        private static bool CreatePrefix(New_UIExpedition_PanelBottom __instance)
        {
            try { return Bridge == null || !Bridge.ConfirmVisitTransport((New_UIExpeditionDialog)NativeWire.Get(__instance, "dialog")); }
            catch (NativeRejectionException rejected) { FairyUIDispatcher.showToast(rejected.Message); return false; }
        }
    }
}
