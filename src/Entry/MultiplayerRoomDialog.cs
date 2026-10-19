using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FreeWorldMultiplayer
{
    /// <summary>Native modal lifecycle, native font and button templates; all business writes go through the session.</summary>
    public sealed class MultiplayerRoomDialog : BaseMonoDialog
    {
        public UnityEngine.UI.Button ButtonTemplate;
        public TMP_FontAsset Font;
        public Material FontMaterial;
        private static MultiplayerRoomDialog open;
        private static MultiplayerRoomDialog constructing;
        private bool built;
        private GameObject[] panes;
        private int tab;
        private TMP_InputField nickname;
        private TMP_InputField worldName;
        private TMP_InputField maxPlayers;
        private TMP_InputField port;
        private TMP_InputField joinAddress;
        private TMP_InputField joinPort;
        private TMP_InputField room;
        private TMP_Text status;
        private TMP_Text roomStatus;
        private TMP_Text playerStatus;
        private TMP_Text mapStatus;
        private TMP_Text saveStatus;
        private TMP_Text clockStatus;
        private TMP_Text recoveryTarget;
        private TMP_InputField recoveryCode;
        private string armedRecoveryPlayer;
        private string armedRecoveryCode;
        private UnityEngine.UI.Button rebindButton;
        private UnityEngine.UI.Button steamButton;
        private UnityEngine.UI.Button worldButton;
        private UnityEngine.UI.Button playerButton;
        private UnityEngine.UI.Button mapButton;
        private UnityEngine.UI.Button loadButton;
        private UnityEngine.UI.Button startRaidButton;
        private UnityEngine.UI.Button leaveButton;
        private readonly List<UnityEngine.UI.Button> connectionButtons = new List<UnityEngine.UI.Button>();
        private bool useSteam;
        private IList<WorldSummary> worlds = new List<WorldSummary>();
        private int worldIndex;
        private string selectedPlayer;
        private long selectedMap = -1;
        private string localFeedback;
        private string lastRuntimeFeedback;
        private MultiplayerRuntime Runtime { get { return MultiplayerRuntime.Instance; } }

        internal static void Open(UnityEngine.UI.Button template, TMP_Text fontSource)
        {
            if (open != null) { open.transform.SetAsLastSibling(); return; }
            var source = MultiplayerUi.Node("FWM.RoomPanel", null);
            source.SetActive(false);
            ((RectTransform)source.transform).sizeDelta = new Vector2(1040, 820);
            var dialog = source.AddComponent<MultiplayerRoomDialog>();
            dialog.ButtonTemplate = template; dialog.Font = fontSource.font; dialog.FontMaterial = fontSource.fontSharedMaterial;
            try
            {
                open = UIHelp.getPage<MultiplayerRoomDialog>(source, false, true, 0.72f);
                if (open == null) throw new InvalidOperationException("原生 UI 加载器未创建联机面板。");
            }
            catch
            {
                if (constructing != null)
                {
                    var failed = constructing;
                    bool registered = false;
                    foreach (BaseMonoDialog candidate in UIDispatcher.NotifyArrays) if (candidate == failed) { registered = true; break; }
                    if (registered) failed.closeDialog(); else UIDispatcher.destoryPanel(failed.gameObject);
                }
                throw;
            }
            finally { constructing = null; UnityEngine.Object.Destroy(source); }
        }
        internal static void RefreshOpenState() { if (open != null) open.RefreshState(); }

        public override void initWidget()
        {
            if (built) return;
            constructing = this;
            if (Runtime == null || Font == null || ButtonTemplate == null) throw new InvalidOperationException("联机面板缺少运行实例或原生控件。");
            base.initWidget();
            overlayKeyEvent(KEY_OPTION.REVICE_ALL);
            ((RectTransform)transform).sizeDelta = new Vector2(1040, 820);
            gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color32(29, 34, 36, 255);
            Label(transform, "Title", "联机 V1", 28, 28, 20, 830, 42);
            Button(transform, "Close", "×", 970, 20, 42, 42, closeDialog);
            Button(transform, "RoomTab", "房间", 28, 80, 220, 42, () => SetTab(0));
            Button(transform, "WorldTab", "玩家与地块", 260, 80, 220, 42, () => SetTab(1));
            Button(transform, "SaveTab", "联机存档", 492, 80, 220, 42, () => { RefreshWorlds(); SetTab(2); });
            panes = new GameObject[3];
            for (int i = 0; i < panes.Length; i++)
            {
                panes[i] = MultiplayerUi.Node("FWM.Page" + i, transform);
                MultiplayerUi.Top((RectTransform)panes[i].transform, 28, 140, 984, 484);
            }
            BuildRoom(panes[0].transform); BuildWorld(panes[1].transform); BuildSave(panes[2].transform);
            clockStatus = Label(transform, "Clock", "", 18, 28, 640, 340, 30);
            Button(transform, "Pause", "暂停 / 继续", 390, 632, 170, 40, () => { var s = Runtime.Session; if (s != null) Runtime.SetClock(!s.Paused, s.Speed); });
            Button(transform, "Speed1", "1 倍", 574, 632, 100, 40, () => Runtime.SetClock(false, 1));
            Button(transform, "Speed3", "3 倍", 686, 632, 100, 40, () => Runtime.SetClock(false, 3));
            Button(transform, "Speed5", "5 倍", 798, 632, 100, 40, () => Runtime.SetClock(false, 5));
            status = Label(transform, "Feedback", "", 18, 28, 696, 984, 92);
            status.enableWordWrapping = true;
            status.alignment = TextAlignmentOptions.TopLeft;
            SetTab(0); built = true; RefreshState();
        }

        private void BuildRoom(Transform parent)
        {
            Label(parent, "IdentityLabel", "显示名称", 18, 0, 0, 120, 30);
            nickname = Input(parent, "Nickname", Environment.UserName, 126, 0, 300, 40, 32);
            Label(parent, "IdentityHint", "身份凭证保存在本机，昵称不决定资产归属。", 17, 444, 2, 540, 56);
            Label(parent, "HostHeading", "新建联机世界", 23, 0, 70, 450, 34);
            worldName = Input(parent, "WorldName", "联机世界", 0, 116, 450, 42, 64);
            Label(parent, "LimitLabel", "人数上限", 18, 0, 174, 110, 30);
            maxPlayers = Input(parent, "MaxPlayers", "4", 126, 168, 100, 42, 2);
            Label(parent, "PortLabel", "监听端口", 18, 246, 174, 110, 30);
            port = Input(parent, "HostPort", "27515", 358, 168, 92, 42, 5);
            steamButton = Button(parent, "EnableSteam", "Steam 好友房间：关", 0, 226, 450, 42, () => { if (Runtime.DirectLaunch) throw new InvalidOperationException("当前为独立直连启动，请使用 IP 房间。"); useSteam = !useSteam; SetCaption(steamButton, useSteam ? "Steam 好友房间：开（同时支持 IP）" : "Steam 好友房间：关"); });
            Button(parent, "HostNew", "新建并开房", 0, 284, 450, 46, () => Safe(() =>
            {
                Runtime.HostNew(nickname.text, new WorldOptions { Name = worldName.text.Trim(), MaxPlayers = Int(maxPlayers, 2, 64) }, HostOptions());
            }));
            Label(parent, "HostHint", "只创建联机世界，不导入单机存档。续开请到“联机存档”。", 17, 0, 346, 450, 66);
            Label(parent, "JoinHeading", "加入房间", 23, 510, 70, 474, 34);
            joinAddress = Input(parent, "JoinAddress", "127.0.0.1", 510, 116, 330, 42, 255);
            joinPort = Input(parent, "JoinPort", "27515", 852, 116, 132, 42, 5);
            Button(parent, "JoinTcp", "IP 直连", 510, 174, 474, 46, () => Safe(() => Runtime.Join(nickname.text, new TransportOptions { Address = joinAddress.text.Trim(), Port = Int(joinPort, 1, 65535), MaxPlayers = 64 })));
            room = Input(parent, "SteamRoom", Runtime.PendingInvite ?? "", 510, 236, 474, 42, 20);
            Button(parent, "JoinSteam", "加入 Steam 房间号", 510, 294, 474, 46, () => Safe(() => Runtime.Join(nickname.text, new TransportOptions { UseSteam = true, RoomId = room.text.Trim(), MaxPlayers = 64 })));
            Button(parent, "Invite", "邀请 Steam 好友", 510, 356, 230, 42, Runtime.Invite);
            leaveButton = Button(parent, "Leave", "保存并退出房间", 754, 356, 230, 42, Runtime.Leave);
            roomStatus = Label(parent, "RoomStatus", "", 17, 0, 426, 984, 58);
        }

        private TransportOptions HostOptions() { return new TransportOptions { Address = "0.0.0.0", Port = Int(port, 1, 65535), MaxPlayers = Int(maxPlayers, 2, 64), UseSteam = useSteam }; }
        private static int Int(TMP_InputField field, int min, int max)
        {
            int value;
            if (!Int32.TryParse(field.text.Trim(), out value) || value < min || value > max) throw new ArgumentException("请输入 " + min + "～" + max + " 的整数。");
            return value;
        }
        private void BuildWorld(Transform parent)
        {
            Label(parent, "RelationsHeading", "玩家关系", 23, 0, 0, 984, 34);
            playerButton = Button(parent, "PlayerSelector", "选择玩家 ›", 0, 52, 450, 44, NextPlayer);
            Button(parent, "Friendly", "请求 / 同意友好", 474, 52, 170, 44, () => SetRelation(Relation.Friendly));
            Button(parent, "Neutral", "设为中立", 656, 52, 150, 44, () => SetRelation(Relation.Neutral));
            Button(parent, "Hostile", "设为敌对", 818, 52, 166, 44, () => SetRelation(Relation.Hostile));
            playerStatus = Label(parent, "PlayerStatus", "", 17, 0, 110, 984, 56);
            Label(parent, "MapHeading", "占领地块与访问", 23, 0, 184, 984, 34);
            mapButton = Button(parent, "MapSelector", "选择该玩家地块 ›", 0, 234, 580, 44, NextMap);
            Button(parent, "EnterMap", "参观 / 进入", 598, 234, 184, 44, () => SendMap(CommandKind.EnterMap, SelectedMap()));
            Button(parent, "LeaveMap", "离开当前地表", 800, 234, 184, 44, () => SendMap(CommandKind.LeaveMap, CurrentMap()));
            Button(parent, "ClaimMap", "占领当前地块", 0, 296, 186, 44, () => SendMap(CommandKind.ClaimMap, CurrentMap()));
            Button(parent, "SetBase", "设为正式基地", 202, 296, 186, 44, () => SendMap(CommandKind.SetBase, CurrentMap()));
            Button(parent, "PrepareRaid", "准备远征", 404, 296, 174, 44, () => Safe(() => { RequireSession(); long map = SelectedMap(); closeDialog(); Runtime.Bridge.OpenRaidPreparation(map); }));
            startRaidButton = Button(parent, "StartRaid", "发起袭击", 594, 296, 174, 44, () => Safe(() =>
            {
                RequireSession();
                RaidRequest request = Runtime.Bridge.BuildRaidRequest(SelectedMap());
                Runtime.Submit(new GameCommand { Kind = CommandKind.StartRaid, MapId = request.MapId, PayloadJson = JsonConvert.SerializeObject(request) });
            }));
            Button(parent, "Evacuate", "唯一一次撤离", 784, 296, 200, 44, () => Safe(() =>
            {
                var session = RequireSession();
                AttackRecord attack = session.World.Attacks.Values.FirstOrDefault(a => a.AttackerPlayerId == session.LocalPlayerId && a.Stage != AttackStage.Finished);
                if (attack == null) throw new InvalidOperationException("当前没有可撤离的进攻。");
                RaidEvacuation evacuation = Runtime.Bridge.BuildRaidEvacuation(attack);
                Runtime.Submit(new GameCommand { Kind = CommandKind.EvacuateRaid, MapId = attack.MapId, PayloadJson = JsonConvert.SerializeObject(evacuation) });
            }));
            mapStatus = Label(parent, "MapStatus", "", 17, 0, 362, 984, 112);
            mapStatus.enableWordWrapping = true;
        }

        private void BuildSave(Transform parent)
        {
            Label(parent, "SaveHeading", "独立联机存档", 23, 0, 0, 984, 34);
            worldButton = Button(parent, "WorldSelector", "选择联机存档 ›", 0, 60, 760, 48, NextWorld);
            Button(parent, "RefreshWorlds", "刷新列表", 780, 60, 204, 48, RefreshWorlds);
            loadButton = Button(parent, "LoadWorld", "续开所选世界", 0, 132, 450, 48, () => Safe(() =>
            {
                WorldSummary chosen = SelectedWorld();
                if (!String.IsNullOrEmpty(chosen.Error)) throw new InvalidOperationException("存档损坏：" + chosen.Error);
                Runtime.HostLoad(nickname.text, chosen.WorldId, HostOptions());
            }));
            Button(parent, "SaveNow", "房主立即保存", 474, 132, 510, 48, Runtime.Save);
            saveStatus = Label(parent, "SaveDetails", "", 17, 0, 210, 984, 112);
            saveStatus.enableWordWrapping = true;
            Button(parent, "CopyRecovery", "复制本机登录恢复码", 0, 338, 270, 40, () => Safe(() =>
            {
                GUIUtility.systemCopyBuffer = Runtime.GetRecoveryCode(nickname.text);
                localFeedback = "已复制本机凭证编号与验证值，不包含 Secret。凭证丢失时请房主选择原来的离线玩家后重绑。";
            }));
            recoveryTarget = Label(parent, "RecoveryTarget", "", 16, 286, 332, 698, 58);
            recoveryCode = Input(parent, "RecoveryCode", "", 0, 404, 694, 44, 1024);
            rebindButton = Button(parent, "RebindCredential", "确认重绑离线玩家", 710, 404, 274, 44, () => Safe(RebindCredential));
        }

        private void RebindCredential()
        {
            var session = RequireSession();
            if (!session.IsHost) throw new InvalidOperationException("只有房主可以恢复玩家登录绑定。");
            PlayerRecord player;
            if (String.IsNullOrEmpty(selectedPlayer) || !session.World.Players.TryGetValue(selectedPlayer, out player)) throw new InvalidOperationException("先到“玩家与地块”选择明确的原玩家记录。");
            if (player.Online) throw new InvalidOperationException("只能重绑已经离线的玩家。");
            string code = recoveryCode.text.Trim();
            var data = Newtonsoft.Json.Linq.JObject.Parse(code);
            string newId = data.Value<string>("CredentialId");
            if (String.IsNullOrEmpty(newId) || String.IsNullOrEmpty(data.Value<string>("Verifier")) || data.Properties().Any(p => p.Name != "CredentialId" && p.Name != "Verifier"))
                throw new InvalidOperationException("恢复码只能包含 CredentialId 和 Verifier。");
            if (armedRecoveryPlayer != player.PlayerId || armedRecoveryCode != code)
            {
                armedRecoveryPlayer = player.PlayerId; armedRecoveryCode = code;
                localFeedback = "即将重绑“" + player.Nickname + "” [" + player.PlayerId + "] 到新凭证 " + newId + "。再次点击确认；原资产和人物保持同一份。";
                SetCaption(rebindButton, "再次点击，保存重绑"); return;
            }
            CommandResult result = session.RebindOfflinePlayerCredential(player.PlayerId, code);
            armedRecoveryPlayer = null; armedRecoveryCode = null; SetCaption(rebindButton, "确认重绑离线玩家");
            if (!result.Success || !result.Saved) throw new InvalidOperationException(result.Error ?? "重绑尚未可靠保存，不能确认成功。");
            localFeedback = "原玩家登录绑定已重设并保存（代次 " + result.SaveGeneration + "）；使用新凭证重连即可恢复原资产。";
        }

        private void SetTab(int value)
        {
            tab = value;
            for (int i = 0; i < panes.Length; i++) panes[i].SetActive(i == tab);
            RefreshState();
        }
        private void RefreshWorlds()
        {
            Safe(() => { worlds = SaveStore.ListWorlds(Runtime.StorageRoot); if (worldIndex >= worlds.Count) worldIndex = 0; });
            RefreshState();
        }
        private void NextWorld() { if (worlds.Count != 0) worldIndex = (worldIndex + 1) % worlds.Count; RefreshState(); }
        private WorldSummary SelectedWorld() { if (worlds.Count == 0) throw new InvalidOperationException("没有联机存档，请先新建世界。"); return worlds[worldIndex]; }
        private void NextPlayer()
        {
            Safe(() =>
            {
                var session = RequireSession();
                var players = session.World.Players.Values.Where(p => p.PlayerId != session.LocalPlayerId).OrderBy(p => p.Nickname).ToList();
                if (players.Count == 0) throw new InvalidOperationException("世界中还没有其他玩家。");
                int index = players.FindIndex(p => p.PlayerId == selectedPlayer);
                selectedPlayer = players[(index + 1) % players.Count].PlayerId; selectedMap = -1;
            });
            RefreshState();
        }
        private void NextMap()
        {
            Safe(() =>
            {
                var session = RequireSession();
                string owner = selectedPlayer ?? session.LocalPlayerId;
                var maps = session.World.Maps.Values.Where(m => m.OwnerPlayerId == owner).OrderBy(m => m.MapId).ToList();
                if (maps.Count == 0) throw new InvalidOperationException("该玩家没有已登记的占领地块。");
                int index = maps.FindIndex(m => m.MapId == selectedMap);
                selectedMap = maps[(index + 1) % maps.Count].MapId;
            });
            RefreshState();
        }
        private MultiplayerSession RequireSession()
        {
            var session = Runtime.Session;
            if (session == null || !session.IsConnected || session.World == null) throw new InvalidOperationException("请先加入联机世界。");
            return session;
        }
        private long CurrentMap() { RequireSession(); long map = Runtime.Bridge.CurrentMapId; if (map < 0) throw new InvalidOperationException("当前未进入地表。"); return map; }
        private long SelectedMap() { RequireSession(); if (selectedMap < 0) throw new InvalidOperationException("请先选择地块。"); return selectedMap; }
        private void SetRelation(Relation relation)
        {
            Safe(() =>
            {
                RequireSession();
                if (String.IsNullOrEmpty(selectedPlayer)) throw new InvalidOperationException("请先选择玩家。");
                Runtime.Submit(new GameCommand { Kind = CommandKind.Relation, TargetPlayerId = selectedPlayer, PayloadJson = JsonConvert.SerializeObject(new { Relation = relation.ToString() }) });
            });
        }
        private void SendMap(CommandKind kind, long map)
        {
            Safe(() =>
            {
                var session = RequireSession();
                MapRecord record; session.World.Maps.TryGetValue(map, out record);
                Runtime.Submit(new GameCommand { Kind = kind, MapId = map, AuthorityEpoch = record == null ? 0 : record.AuthorityEpoch, BaseRevision = record == null ? 0 : record.Revision });
            });
        }
        private void Safe(Action action)
        {
            try { localFeedback = null; action(); }
            catch (Exception error) { localFeedback = MultiplayerDiagnostics.RedactForDisplay(error.ToString()); if (Runtime != null) Runtime.ReportUiFailure(localFeedback); }
        }
        public override void uiUpdate() { RefreshState(); }
        private void RefreshState()
        {
            if (!built && roomStatus == null) return;
            var session = Runtime == null ? null : Runtime.Session;
            WorldState world = session == null ? null : session.World;
            bool connectionBusy = Runtime != null && (Runtime.IsStarting || (session != null && session.IsClosing));
            foreach (UnityEngine.UI.Button button in connectionButtons) if (button != null) button.interactable = !connectionBusy;
            if (leaveButton != null)
            {
                SetCaption(leaveButton, Runtime != null && Runtime.IsStarting ? "取消创建" : "保存并退出房间");
                leaveButton.interactable = session != null && !session.IsClosing;
            }
            if (startRaidButton != null) startRaidButton.interactable = world != null && selectedMap >= 0 && !world.Players.Values.Any(p => p.MotherShipMapId == selectedMap);
            if (roomStatus != null)
            {
                string roomId = Runtime.Transport == null ? "" : Runtime.Transport.RoomId;
                roomStatus.text = world == null ? "尚未进入联机世界。" : world.Name + "  ·  在线 " + world.Players.Values.Count(p => p.Online) + "/" + world.Options.MaxPlayers + (String.IsNullOrEmpty(roomId) ? "  ·  IP 房间" : "  ·  Steam 房间号 " + roomId);
            }
            if (clockStatus != null) clockStatus.text = session == null ? "房主控制暂停与倍速" : (session.IsHost ? "房主" : "客人") + "  ·  " + (session.Paused ? "已暂停" : session.Speed + " 倍运行");
            if (worldButton != null) SetCaption(worldButton, worlds.Count == 0 ? "尚无联机存档" : worlds[worldIndex].Name + "  ›");
            if (loadButton != null) loadButton.interactable = !connectionBusy && worlds.Count > 0 && String.IsNullOrEmpty(worlds[worldIndex].Error);
            if (saveStatus != null)
            {
                string chosen = worlds.Count == 0 ? "没有已保存的联机世界。" : worlds[worldIndex].Name + "\n保存代次 " + worlds[worldIndex].SaveGeneration + "  ·  " + worlds[worldIndex].SavedAtUtc + (String.IsNullOrEmpty(worlds[worldIndex].Error) ? "" : "\n损坏原因：" + worlds[worldIndex].Error);
                saveStatus.text = chosen + "\n数据目录：" + Runtime.StorageRoot + "\n续开使用“房间”页端口与 Steam 开关；普通未保存进度可能崩溃回退。";
            }
            if (recoveryTarget != null)
            {
                PlayerRecord target = null;
                if (world != null && selectedPlayer != null) world.Players.TryGetValue(selectedPlayer, out target);
                recoveryTarget.text = target == null ? "房主恢复：先在“玩家与地块”选择原离线玩家。" : "重绑目标：" + target.Nickname + (target.Online ? "（在线，不可重绑）" : "（离线）") + "\nPlayerId: " + target.PlayerId;
            }
            if (world != null && playerStatus != null)
            {
                PlayerRecord player;
                if (!String.IsNullOrEmpty(selectedPlayer) && world.Players.TryGetValue(selectedPlayer, out player))
                {
                    SetCaption(playerButton, player.Nickname + (player.Online ? "（在线） ›" : "（离线） ›"));
                    var relation = world.Relationships.FirstOrDefault(r => (r.PlayerA == session.LocalPlayerId && r.PlayerB == selectedPlayer) || (r.PlayerB == session.LocalPlayerId && r.PlayerA == selectedPlayer));
                    playerStatus.text = "当前关系：" + (relation == null ? "中立" : relation.Relation == Relation.Friendly ? "友好" : relation.Relation == Relation.Hostile ? "敌对" : "中立") + "。友好需双方同意；敌对单方生效。";
                }
                else { SetCaption(playerButton, "选择玩家 ›"); playerStatus.text = "其他玩家的母舰、雇员、资金、科技与任务相互独立。"; }
                MapRecord map;
                if (world.Maps.TryGetValue(selectedMap, out map))
                {
                    PlayerRecord owner; world.Players.TryGetValue(map.OwnerPlayerId ?? "", out owner);
                    string place = owner != null && owner.FormalBaseMapId == map.MapId ? "正式基地" : "营地 / 占领地块";
                    SetCaption(mapButton, (owner == null ? "未知归属" : owner.Nickname) + "的" + place + " ›");
                    var attack = world.Attacks.Values.FirstOrDefault(a => a.MapId == map.MapId && a.Stage != AttackStage.Finished);
                    mapStatus.text = "状态：" + (map.Mode == MapMode.Battle ? "战斗" : map.Mode == MapMode.Realtime ? "实时模拟" : map.Mode == MapMode.Offline ? "离线托管" : "冻结") +
                        (map.RaidCooldownSeconds > 0 ? "  ·  再次袭击冷却 " + Clock(map.RaidCooldownSeconds) : "") +
                        (attack == null ? "" : "\n进攻剩余 " + Clock(Math.Max(0, 600 - attack.ElapsedSeconds)) + "；仅一次撤离，最多一个出征空投仓容量。") +
                        "\n出征沿原版远征与空投流程；友好随身物品可带走，中立和敌对不能自动拿取基地物品。";
                }
                else { SetCaption(mapButton, "选择该玩家地块 ›"); mapStatus.text = "选择玩家后切换地块；未选择玩家时列出自己的地块。"; }
            }
            string runtimeFeedback = Runtime == null ? "" : Runtime.Feedback;
            if (lastRuntimeFeedback != runtimeFeedback) { lastRuntimeFeedback = runtimeFeedback; localFeedback = null; }
            if (status != null) SetFeedbackContent(localFeedback ?? runtimeFeedback);
        }
        private static string Clock(double seconds) { return ((int)Math.Ceiling(seconds) / 60).ToString("00") + ":" + ((int)Math.Ceiling(seconds) % 60).ToString("00"); }
        private void SetCaption(UnityEngine.UI.Button button, string caption) { if (button == null) return; foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true)) text.text = caption; }

        private UnityEngine.UI.Button Button(Transform parent, string name, string caption, float x, float y, float width, float height, UnityEngine.Events.UnityAction action)
        {
            var result = MultiplayerUi.CloneButton(ButtonTemplate, parent, "FWM." + name, caption, () => Safe(() => action()));
            if (new[] { "HostNew", "JoinTcp", "JoinSteam", "LoadWorld" }.Contains(name)) connectionButtons.Add(result);
            MultiplayerUi.Top((RectTransform)result.transform, x, y, width, height);
            return result;
        }
        private TMP_Text Label(Transform parent, string name, string caption, float size, float x, float y, float width, float height)
        {
            var result = MultiplayerUi.Node("FWM." + name, parent).AddComponent<TextMeshProUGUI>();
            result.font = Font; if (FontMaterial != null) result.fontSharedMaterial = FontMaterial;
            result.fontSize = size; result.enableAutoSizing = false; result.richText = false;
            result.color = new Color32(225, 234, 236, 255); result.text = caption; result.raycastTarget = false;
            result.alignment = TextAlignmentOptions.MidlineLeft;
            MultiplayerUi.Top(result.rectTransform, x, y, width, height);
            return result;
        }
        private TMP_InputField Input(Transform parent, string name, string initial, float x, float y, float width, float height, int limit)
        {
            var node = MultiplayerUi.Node("FWM." + name, parent);
            var image = node.AddComponent<UnityEngine.UI.Image>(); image.color = new Color32(43, 52, 55, 255);
            MultiplayerUi.Top((RectTransform)node.transform, x, y, width, height);
            var viewport = MultiplayerUi.Node("Viewport", node.transform); MultiplayerUi.Stretch((RectTransform)viewport.transform, 10, 10, 5, 5);
            viewport.AddComponent<UnityEngine.UI.RectMask2D>();
            TMP_Text text = Label(viewport.transform, "Value", "", 19, 0, 0, width - 20, height - 10);
            MultiplayerUi.Stretch(text.rectTransform, 0, 0, 0, 0); text.enableWordWrapping = false;
            var field = node.AddComponent<TMP_InputField>(); field.targetGraphic = image; field.textViewport = (RectTransform)viewport.transform;
            field.textComponent = text; field.fontAsset = Font; field.richText = false; field.characterLimit = limit;
            field.lineType = TMP_InputField.LineType.SingleLine; field.restoreOriginalTextOnEscape = false;
            field.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            field.text = initial; return field;
        }
        internal static void SetInvitation(string roomId) { if (open != null && open.room != null) { open.room.text = roomId; open.SetTab(0); } }
        public override bool triggerKeyboard(KeyCode sourceKc, KeyCode execKc) { if (execKc == KeyCode.Escape && !ClosePlayerDropdown()) closeDialog(); return true; }
        public override bool keyEventESCToClose() { if (!ClosePlayerDropdown()) closeDialog(); return true; }
        public override void OnDestroy()
        {
            if (open == this) open = null;
            if (constructing == this) constructing = null;
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(transform)) EventSystem.current.SetSelectedGameObject(null);
            base.OnDestroy();
        }
        internal static void CloseOpen() { if (open != null) open.closeDialog(); }
    }

    internal static class MultiplayerUi
    {
        internal static GameObject Node(string name, Transform parent)
        {
            var node = new GameObject(name, typeof(RectTransform)); if (parent != null) node.transform.SetParent(parent, false); return node;
        }
        internal static UnityEngine.UI.Button CloneButton(UnityEngine.UI.Button template, Transform parent, string name, string caption, UnityEngine.Events.UnityAction action)
        {
            var staging = Node("FWM.ButtonStaging", parent); staging.SetActive(false);
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(template.gameObject, staging.transform, false); clone.SetActive(false); clone.name = name;
                foreach (MonoBehaviour behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour is UnityEngine.UI.Graphic || behaviour is UnityEngine.UI.Button || behaviour is UnityEngine.UI.LayoutGroup || behaviour is UnityEngine.UI.LayoutElement || behaviour is UnityEngine.UI.ContentSizeFitter) continue;
                    UnityEngine.Object.DestroyImmediate(behaviour);
                }
                foreach (var button in clone.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                {
                    button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                    button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
                    button.interactable = button.gameObject == clone;
                }
                var result = clone.GetComponent<UnityEngine.UI.Button>();
                var labels = clone.GetComponentsInChildren<TMP_Text>(true);
                if (result == null || labels.Length == 0 || result.targetGraphic == null) throw new InvalidOperationException("原生按钮模板缺少 Button、TMP 文本或点击图形。");
                foreach (var graphic in clone.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) graphic.raycastTarget = false;
                result.targetGraphic.raycastTarget = true;
                foreach (TMP_Text text in labels) { text.text = caption; text.richText = false; text.fontSize = 18; text.enableAutoSizing = false; text.enableWordWrapping = false; text.alignment = TextAlignmentOptions.Center; }
                result.onClick.AddListener(action); clone.transform.SetParent(parent, false); clone.transform.localScale = Vector3.one; clone.transform.localRotation = Quaternion.identity;
                clone.SetActive(true); return result;
            }
            catch { if (clone != null) UnityEngine.Object.Destroy(clone); throw; }
            finally { UnityEngine.Object.Destroy(staging); }
        }
        internal static void Top(RectTransform rect, float x, float y, float width, float height) { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); }
        internal static void Stretch(RectTransform rect, float left, float right, float top, float bottom) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(-right, -top); }
    }
}
