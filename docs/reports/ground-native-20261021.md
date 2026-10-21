# ground-native-20261021

状态：Game 源码完成并冻结，交主控整合。未编译、测试、运行游戏、安装、提交或操作 Git index；Project3 与运行副本只读。

## 本轮文件

新增 `src/Game/NativeGroundOperation.cs`。
窄改 `src/Game/GameBridge.cs`、`GameHooks.cs`、`NativeGroundCommandOwnership.cs`、`NativeCommands.cs`、`NativeGroundLoot.cs`、`NativeRaid.cs`、`NativeRaidLoot.cs`、`NativeTransfers.cs`。
`NativeGroundOperationUi.cs` 由 ground_ui 独立负责；本 Agent 只在 GameHooks 添加 Install 接线。

## 行为与真实调用链

- GameBridge.Route 和 ExecuteCommand 都检查当前操作 UID；执行实体必须仍归命令玩家。UI 图纸修改/删除、工作指派、物品手动拿取/丢弃/使用/装备与原版人员设置编辑都有相应意图门禁；唯一模拟者不等于本机玩家可操作。
- 新 Bridge.HasGroundPlayerHeroes(mapId, playerId) 读取实际持久角色是否在图且未死亡，包含倒地及机器人，服务普通驻留恢复操作槽。
- NativeRaid.AreDefendersDefeated 实时读取基地所有者及其双向友好盟友的活动人类，机器人以 FWHeroBean.isAndroid 排除；无主图检查所有其他玩家人类。继续使用真实倒地/死亡/囚犯及可恢复 KnockedOut 状态；角色恢复后须再次击倒。
- 原版远征链：New_UIExpedition_PanelBottom.onEvent_Create → ExpeditionTransportStartStarMapInfo → LocalCoreHandle.Expedition_Go → EarthSender.createExpedition，已接 SubmitNativeTransfer。含本场己方小人的运输现在也归 EvacuateRaid，一次空手撤人同样消费现有唯一机会；纯己方物资补给不因此变成唯一撤离。受保护物资仍逐来源重判守军与容量，不新增物品含义或全局运输次数。
- GameBridge.PumpGroundOperationLifecycle 由 Entry Update 接入，非地表观察、操作槽为空、非本机模拟且非母舰时，通过原版 GameRoot.destroyMapManager 真正销毁地表运行实例，保留持久地图 Bean；丢失本机操作权时清除未提交图纸预览。SetMapAuthority 也接同一卸载条件。
- AI 路径与玩家意图区分：原生 Control_Fight/征召 UI 等在既有 NativeCommands UI scope 中；三战斗入口及 NativeCommands.NativePrefix 非玩家意图只在唯一模拟者执行原版方法，不借观察者本机 UID 路由。真实 Hero_Action_SurgicalOperation.cs:80 调用 order_Hero_GoBed 是自动治疗，已保留该原生路径；生产/治疗/炮塔/守军 AI 不读操作槽来停止自身。

## 跨模块依赖

Core/Session 的 BridgeContext.CanOperateMap、OperatorOfMap、MapAccess.OperatorPlayerId 与 IGameBridge.HasGroundPlayerHeroes 已对齐；Entry 的 PumpGroundOperationLifecycle 接线由 ground_ui 完成。普通180秒、攻击10分钟、掉线、原子落人抢槽、保存屏障均由 ground_slot 维护。

主控统一三端 BuildOnly；全部真实双端行为由用户验收。保留所有历史 dirty，本轮基线为 artifacts/ground-operation-slot-20261021/baseline。
