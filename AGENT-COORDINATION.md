# Agent 协作记录

用于本仓库各 Agent 共享规划、文件分工、接口约定和阻塞信息。需求以 `联机方案 V1.md` 为准，开发与同步边界见 `AGENTS.md`、`Agent.md`；本文件不替代需求，也不代表游戏已验收。

## 使用方式

1. 开始或继续任务时，先读本文件，再登记自己的任务、文件范围和依赖；改动重叠时，在下面留言并明确拆分后再编辑。
2. 修改接口前记录签名、调用方和准备就绪状态；依赖未完成时保留现状，不能用空实现让构建通过。
3. 只更新自己的任务和留言。提交仅包含自己的改动；完成后写明提交、剩余事项并释放文件占用。
4. 构建或同步前检查阻塞记录；构建失败、源码变化或运行目标未关闭时，按现有统一脚本停止，不能覆盖别人的代码或强停游戏。
5. 本文件需要各 Agent **主动读取**；它不是自动通知、即时消息或后台监听服务。文件本身也不提供并发写入锁：多人修改前由主控协调写入时段，提交前检查内容，遇到冲突停止并保留所有已记录消息。
6. 任务修改应遵守 `GIT-COMMIT-POLICY.md`；同一 Git 暂存区不能由多个 Agent 同时操作，已有未提交代码不得擅自暂存。协作留言禁止填写登录恢复码、密钥、房间号等敏感数据。

记录保持简短；状态变化后更新原记录，过时事项移到结果栏，避免重复堆积。

## 任务与文件分工

| 任务 ID | 负责人 | 目标 / 文件范围 | 状态与下一步 |
| --- | --- | --- | --- |
| optimization-map-replay-20261016 | 本轮主控（无可用子 Agent） | `src/Session/MapSnapshotCache.cs`、`MapViewLoading.cs`、`src/Core/MapSnapshotDeltaCodec.cs`、`SessionPerformance.cs`；优化回归用例与实施说明 | 已完成源码实施并释放占用：客端缓存完整图保留在原 FIFO 槽，避免二次 JSON 化且走原 `ReceiveMapView`；重组只分配一次完整缓冲；编码/重组/应用及缓存块复用增加可关闭的聚合指标。40 项纯逻辑检查通过；P3/Steam/TapTap 三端真实引用 Roslyn 静态编译通过。原三份地图源文件属于历史未跟踪内容，不可整份暂存，保留本轮窄改与其原内容。正式 `-BuildOnly` 因本机 PowerShell 5.1 不满足 7.0 被拒，未安装或实测联机；故障根因、性能收益、多人资产验收仍未确认。 |
| multiplayer-optimization-plan-refresh-20261016 | 本轮方案维护 Agent | 仅 `docs/联机插件优化开发案.md` 的执行状态和下一轮任务卡；协调记录只增本行 | 已完成并释放占用：复核既有代码及已提交实施记录，更新 S0～S5 状态、G0/F1/M1/O1～O3/R1 任务、依赖、证据、验收和回退要求；仅文档，不改功能/协议/存档/游戏本体，未执行构建、安装或游戏测试。 |
| optimize-s0-s1-20261016 | 当前实施主控（本会话；无可用子 Agent） | S0 舰队诊断：`src/Game/NativeGalaxy.cs`、`src/Session/Commands.cs`、`Networking.cs`、`MultiplayerSession.cs`；S1 性能窗口与队列只读统计；S2/S4 房主广播编码去重；S5 纯逻辑回归、文档 | 代码修改已完成、源码占用释放：固定阶段有界舰队诊断、可关闭 30 秒性能直方图、接收队列采样、广播复用；纯逻辑检查 23 项通过，P3/Steam/TapTap 复用历史真实引用的 Roslyn 静态编译均通过（非正式打包）。本机未发现 PowerShell 7，统一构建/安装未执行；未启动游戏/PlayMode/跨机测试，舰队不能移动的实际根因与 S3 热点仍需实测。保留所有其他 Agent 的未提交改动。 |
| multiplayer-optimization-plan-20261016 | 当前文档规划 Agent（本会话） | `docs/联机插件优化开发案.md`、`docs/联机优化实施路线.svg`；不占用功能源码 | 已完成并释放文档占用：建立 S0～S5 分阶段方案、可复现性能基线、回归矩阵和路线图；仅文档改动，未变更接口/协议/存档/版本，未构建、安装或运行游戏。 |
| host-exit-v1.12 | 本次修复主 Agent | 整合房主离开后的客人退出、版本和三端构建 | 源码已提交 `2972361`；统一构建被下方依赖阻断，尚未安装 |
| host-exit-session | 子 Agent `/root/host_exit_session` | `src/Session/MultiplayerSession.cs`、`src/Session/Networking.cs` | 实现与接口说明已交主 Agent 整合；文件占用已释放；未运行测试或游戏 |
| host-exit-entry | 子 Agent `/root/host_exit_entry` | `src/Entry/ModPluginLauncher.cs`、`src/Game/GameHooks.cs` | 已整合，占用已释放；原生返回保留 `callbackInt`，待会话清理后执行；封面就绪后仅向 `HostLeft` 客人提示一次；未运行游戏验收 |
| agent-coordination-file | 本次修复主 Agent | `AGENT-COORDINATION.md`、`AGENTS.md` | 协作入口、登记模板和本轮分工已建立，两位子 Agent 已各自更新交接；文件占用已释放 |
| UI-20261016-01 | 本轮 UI 结构代理 | `src/Entry/MultiplayerVisualTheme.cs` | 初始化拆为反馈、房间、连接和输入四个步骤；本地提交 `0fbf8c7`；未做游戏内验收，文件占用已释放 |
| UI-20261016-02 | 待认领 | `src/Entry/MultiplayerRoomDialog.cs`、`src/Entry/MultiplayerLobbyPages.cs`、`src/Entry/MultiplayerRoomPages.cs` | 计划梳理导航与状态刷新边界；这些文件已有未提交改动，必须先确认负责人和交接，未获许可不修改 |
| UI-20261016-03 | 本轮 UI 结构代理 | `src/Entry/MultiplayerVisualTheme.cs`、`src/Entry/MultiplayerPlayerRosterVisual.cs` | 已完成：视觉刷新和玩家列表数据 / 显示职责拆分，本地提交 `a7c6d6c`；未构建或游戏内验收，文件占用已释放 |
| landing-capture-scan | 当前性能优化代理（本会话） | `src/Game/NativeLanding.cs` | 已撤销并释放文件：发现 `transport-pvp-game` 正在使用此文件，试验改动已精确撤回，未提交；需主控明确协调后才能继续 |
| transit-manifest-skip-empty | 当前性能优化代理（本会话） | `src/Game/NativeTransitProjection.cs` | 已完成 `e7a09fb`：货运空乘客清单跳过雇员/动物列表扫描；仅静态差异检查，未构建、未安装、未游戏验收；无接口、协议、存档变更；文件占用已释放 |

其他正在进行的任务请由对应负责人自行登记，不根据文件名推断归属。

## 接口与依赖

| 提供方 | 契约 | 调用方 / 状态 |
| --- | --- | --- |
| Session | `public bool HostLeft { get; private set; }` | Entry 在终止清理后读取；已实现，握手失败与客人主动退出不置位 |
| Runtime | `internal bool TryQueueNativeReturn(callbackInt callback)` | `GameHooks.BackCoverPrefix`；已接入，保留原生回调，先结束会话再返回主菜单 |

## 阻塞与协作留言

- **2026-10-16 / host-exit-v1.12 → 并行开发 Agent：** 最近一次 `Sync-ModCopies.ps1` 编译被 `src/Game/NativeFleetSynchronization.cs` 的缺失依赖阻断：`FleetFrame.Sync`、`MarkRemoteFleetContentChanged`、`PushRemoteFleetPose`、`RemoveRemoteFleetProxy`。请负责该实现的 Agent 登记归属并补齐自身依赖；本次修复不会删除这些代码或填入空实现。构建失败时未安装任何运行包。
- **2026-10-16 / host-exit-v1.12：** 游戏内、Play Mode 和跨端验收由用户执行；本轮未运行。
- **2026-10-16 / UI-20261016-02 → 负责 `src/Entry/` UI 分页的 Agent：** 请先确认 `MultiplayerRoomDialog.cs`、`MultiplayerLobbyPages.cs`、`MultiplayerRoomPages.cs` 的当前负责人、未提交内容与可移交范围。保持 V1.3 的创建 / 加入 / 房间 / 关系 / 设置页结构与原生操作调用链。**待回复**；此留言不表示已经自动通知或取得回应。

## 新任务登记模板

```text
任务 ID / 负责人：
目标与范围：
准备修改的文件：
提供或依赖的接口：
当前状态 / 下一步：
需要其他 Agent 配合：
完成结果 / 提交 / 已释放文件：
```

跨模块消息在「阻塞与协作留言」中注明发送方、接收方、关联任务、涉及文件或接口、需要的明确回复及处理状态。交接要报告编译、安装和游戏验收各自的真实结果，不将未验证内容标为完成。
