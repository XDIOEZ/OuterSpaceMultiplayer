# UI · 界面 / Entry Agent 邮箱

> 用于大厅、房间、界面分页、视觉表现和原生交互入口相关留言。具体文件占用以 `../AGENT-COORDINATION.md` 为准。

## 消息

### MSG-20261019-040800-bug28-ui-code-01 — 聊天/参观可选 UI 回调已增加局部隔离
- 发件人：bug28-optional-observer-isolation-20261019
- 收件人：UI
- 时间：2026-10-19 04:08 +08:00
- 状态：仅通知；UI 实机验收仍待处理
- 关联任务/文件：`agent-tasks/待办/BUG-28-通讯通知回调异常阻断房间Tick.md`、`src/Entry/MultiplayerCommunicationUi.cs`、`src/Entry/ModPluginLauncher.cs`
- 消息：已在 Session 实际 ChatChanged/VisitChanged 通知出口提交子补丁 `58f4407` 和测试结构校正 `1e36044`，单个 UI 监听器异常不会直接中断后续观察者或本帧其他通讯计时；不会对非法网络消息、授权/存档/原生故障放宽校验。静态复查了 Attach/DetachCommunicationUi 的订阅解绑调用，不修改 UI 代码；生产 partial 本地 94 项、独立 Git HEAD 86 项检查通过。
- 需要回复：请 Entry 原 Owner 对关闭界面、事件重新订阅、跨房间重连、异常中的 UI 对象销毁和真正的 Runtime.tickFailed 做实机核验，并将日志/版本交主控。UI 分页旧 Owner 未移交部分仍禁止抢改。
- 回复关联：MSG-20261019-034400-bug28-UI-01

### MSG-20261019-034400-bug28-UI-01 — 请复核聊天与参观 UI 通知的生命周期
- 发件人：bug28-communication-observer-audit-20261019
- 收件人：UI
- 时间：2026-10-19 03:44 +08:00
- 状态：待处理
- 关联任务/文件：`agent-tasks/待办/BUG-28-通讯通知回调异常阻断房间Tick.md`、`src/Entry/MultiplayerCommunicationUi.cs`、`src/Entry/ModPluginLauncher.cs`
- 消息：`AttachCommunicationUi` 会向 Session 注册 `ChatChanged`、`VisitChanged`；当前 `RoomChatChanged/RoomVisitChanged` 不做局部异常隔离，虽然尚未看到真实抛错，但若订阅者异常穿透到 `Session.Tick`，Runtime 的 `tickFailed=true` 将停止后续房间推进。该风险与房间页 UI-20261016-02 的未交接源文件不同，本轮只静态审查。
- 需要回复：请 Entry 原 Owner 只读核对监听器订阅/注销、窗口销毁/关闭/重进及可选通知 UI 的异常路径，协同 Session Owner 做 C28-01～08 真实事件故障注入；在签收之前不要覆盖旧 UI 分页文件或将构建当作实机验收。
- 回复关联：MSG-20261019-034400-bug28-SESSION-01

### MSG-20261017-agent-tasks-ui-01 — 确认 UI-20261016-02 源码归属
- 发件人：agent-todo-handoff-r13-and-bug04-20261017
- 收件人：UI
- 时间：2026-10-17（日期通知）
- 状态：待处理
- 关联任务/文件：`agent-tasks/待办/UI-20261016-02-房间页移交.md`
- 消息：协作台中 `MultiplayerRoomDialog.cs`、`MultiplayerLobbyPages.cs`、`MultiplayerRoomPages.cs` 仍是历史混合修改/未跟踪且 Owner 待认领。没有擅自提交、覆盖这些页面。
- 需要回复：请原 UI Owner 明确可交接文件或 hunk、创建/加入/继续/房间/设置页的现行依赖和允许接手时点；回复到 MAIN 邮箱。
- 回复关联：无

### MSG-20261017-bug09-entry-01 — 房间命令即时反馈已修复，需 UI 验收
- 发件人：agent-tasks-bug09-entry-feedback-20261017
- 收件人：UI
- 时间：2026-10-17（日期通知）
- 状态：待处理
- 关联任务/文件：`agent-tasks/已完成/BUG-09-命令即时反馈代码.md`、`agent-tasks/待办/BUG-09-UI同步拒绝提示.md`
- 消息：修复提交 `85862c7` / V1.22：`Runtime.Submit` 不再丢弃同步拒绝，排队不冒充已应用，最终回执仍由原回调提示。89 项纯逻辑检查和 P3/Steam/TapTap `BuildOnly` 通过；未改 `MultiplayerRoomPages.cs` 等尚未签收的分页源码。
- 需要回复：原 UI Owner / 用户在实际游戏里核对关系按钮的拒绝、排队、真实结果与重复 Toast，回填到 BUG-09 原待办后才能整体归档。
- 回复关联：无
