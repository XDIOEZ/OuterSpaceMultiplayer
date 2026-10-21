# ground-slot-20261021 · Core / Session

- 状态：源码已交主控整合；未测试、未构建、未运行游戏、未安装、未操作 Git index。
- 范围：仅本 MOD 的 `src/Core` / `src/Session`，保留任务前所有脏改动；修改基线由主控保存在 `artifacts/ground-operation-slot-20261021/baseline`。

## 接口与实际链路

- `MapRecord.OperatorPlayerId` 唯一操作 UID，独立于 Owner / Simulator；`OperatorReleaseAtRoomSeconds` 为房间实时时刻，0 表示没有普通离图宽限。
- `MapAccess.OperatorPlayerId` 跟随权威视图和 Frozen 撤权，避免误把冻结模拟当操作槽已释放；`SessionPolicy` 与客户端 MapView 同步该 UID。
- `Session` / `BridgeContext` 提供 `CanOperateMap(string playerId, long mapId)`、`OperatorOfMap(long mapId)`、`GroundObserverCount(long mapId)`。观察人数仅统计在线 Visitors 中非 Operator 的玩家。
- `IGameBridge.HasGroundPlayerHeroes(long mapId, string playerId)` 由 Game Agent 实现，普通基地及已有己方留守人员的空槽地块可恢复控制；不会抢非空操作槽。
- 网络协议由 12 改为 13，拒绝旧客户端绕过唯一操作规则。

## 行为

- 原版空投的出发提交和真正到达均由房主检查目标槽。物资赠送不占槽；第二 UID 的在途人员保留原运输单等待，未落人前不提前占槽。
- 真实落仓取得 UID 后，将唯一模拟优先交给操作玩家；其他观察者可承接纯 AI 模拟，不能发送玩家操作。
- 普通离开至其他地表立即释放；进入母舰、太空或掉线设置 180 秒宽限；回到同图取消宽限。到期释放并交接/冻结，Game Agent 负责真实卸载桥接。
- 地面行动中持槽者切图、回太空、掉线均不释放；无实时加载者时保留原生检查点冻结，房主继续本场现实时间倒计时，到期走旧结算，不重新开始。
- 敌对空投可对在线但未占操作槽的地块开启本场；友好/中立落人不自动建战斗。旧多人加入分支仅允许同一个攻击 UID 的真实运输单，不再允许第二玩家共同操作/共同搜刮。
- 新场固定 600 秒，从实际落仓开始保留时间；中立落地后再触发战斗沿用原运输单落仓时刻。既存存档战场保留已保存 Duration / Elapsed。
- 路由玩家指令、模拟端接令、HasPermission、创建/废弃基地、普通地表出发均检查操作槽；母舰自己的操作独立。
- 本场直接普通运输带人被 Session 拒绝，必须走唯一 `EvacuateRaid`。空手撤人不要求击倒守军；受保护战利品仍需实时守军条件及运输容量。Game Agent 把一次原版远征撤人接至既有 `EvacuationUsed`。
- 搜刮统一依赖 Game 的 `AreDefendersDefeated` 实时判断，人类防守者和友好盟友恢复后重新失去资格，机器人排除；操作槽不改变实体资产归属。
- 结算后槽暂空，但 `pendingGroundSettlements` 在保存成功前阻止任何人取得/操作/新空投。成功保存后优先为当前在线基地所有者/驻留己方人员恢复空槽并广播，保存失败不能开启新行动。
- 获取、宽限、释放、繁忙拒绝、结算保存边界输出 `ground-slot` 日志，含 Map / Actor UID / Operator UID / 原因 / 剩余时间；繁忙同状态重试不持续刷日志。

## 文件

- `src/Core/Contracts.cs`
- `src/Core/Protocol.cs`
- `src/Session/GroundOperationSlots.cs`（新增）
- `src/Session/Authority.cs`
- `src/Session/BaseRules.cs`
- `src/Session/Commands.cs`
- `src/Session/GroundRules.cs`
- `src/Session/RaidRules.cs`
- `src/Session/MapViewLoading.cs`
- `src/Session/MultiplayerSession.cs`
- `src/Session/Networking.cs`
- `src/Session/PeerDisconnectCleanup.cs`

## 交接边界

- 运行及双端验收由用户执行，源码交付不等于玩法验收。
- Native Agent 需完成新的 Bridge 方法、纯 AI 与玩家输入的分层、守军实时判断、原版撤人接线及 180 秒后的地图真实卸载；UI Agent 使用同一操作 UID 与观察人数查询。
- 无加载者时不会额外创建后台 Unity 地图实例；本场倒计时仍继续，原生战斗在有在线操作方或观察方加载时推进。
