# ground-entry-session-20261021

### MSG-20261021-ground-entry-session-01 — 加载意图与模拟可推进状态分离
- 发件人：entry_session / ground-entry-freeze-20261021
- 收件人：主控 / entry_native
- 时间：2026-10-21
- 状态：已完成；源码冻结，交主控整合
- 关联任务/文件：`src/Core/Contracts.cs`、`src/Session/MultiplayerSession.cs`、`src/Session/GroundOperationSlots.cs`
- 消息：新增本进程上下文委托 `IsLocalGroundViewActive(long mapId)`。本次已授权的 `nativeMapLoad` 或 `awaitingLocalReady` 优先表明查看意图，否则检查正式本机 `CurrentMapId` 与地块 `Visitors`。客户端完整视图先于策略消息，策略必须排在原生加载 FIFO 后，不能用旧策略提前卸载新图。该委托不授予操作或模拟权限，不改变协议、持久化数据、原生就绪条件、加载超时或 Ready 握手。
- 证据：TapTap 原日志 seq4194 首次野地 `localSimulator=True/awaitingReady=True`，同次 view.applied 后仍 `loading=True`。`CanSimulateMap` 在这两个合法加载阶段返回 false 是既有安全屏障，不能作为原生地图应卸载的证据。`TickMapLoading` 等待真实 `bridge.IsMapReady`，再清 native 屏障、按序处理 policy，`TickLocalAuthorityReady` 最后确认模拟租约，Session 无需绕过这些条件。
- 保留：主控基线中 Contracts/MultiplayerSession 已有混合修改全部保留，仅增加委托字段、接线与一个查询方法；GroundOperationSlots 本轮只增加查询。不动 Game/Entry、版本、协调台、Git index、本体及存档。
- 验证：只读源码及已有日志调查；未运行编译、测试、打包、游戏或安装。主控负责统一 BuildOnly，运行验收交用户。
- 需要回复：无；entry_native 已收到并使用精确委托名，主控已同意接口及客户端加载过渡处理。
- 回复关联：ground-entry-freeze-20261021
