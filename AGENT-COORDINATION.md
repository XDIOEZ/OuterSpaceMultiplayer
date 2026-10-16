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
| optimization-ninth-acceptance-plan-20261017 | 本轮优化案主控（文档占用已释放） | 仅新增 `docs/联机插件优化开发案-第九轮可交付闭环-20261017.md`，在 `docs/联机插件优化开发案.md` 更新最新入口；此协调表只新增本条任务行 | 已完成第九轮方案编写：依据已核实 V1.19、此前 `HEAD=4957ba5`、91 项既有工作树变化、128 构建输入（52 未跟踪、32 修改）、内部三端 `BuildOnly` 和缺失新版双端日志，明确 N9-01～N9-06 原 Owner 交接、真实舰队/2D 性能证据、唯一最慢热点修复、回归与发布门禁。本轮仅改文档，未动功能代码、构建安装或运行游戏。 |
| optimization-e8-measurable-profiles-20261017 | 本轮主控（已释放源码占用，无子 Agent 可用） | `src/Session/PerformanceWindow.cs`、`SessionPerformance.cs`、`src/Game/NativeGroundCaptureProfile.cs` 的可关闭固定 14 桶数据，`tools/Analyze-MultiplayerDiagnostics.ps1`、新 `tools/Compare-MultiplayerPerformance.ps1` / `Test-Compare-MultiplayerPerformance.ps1`、`OptimizationChecks/Program.cs`、实测指南/模板/回执 | 已完成 E8-04 的可合并计量与 E8-05 的样本对照门禁工具，代码独立提交 `6e02b26`：81 项纯逻辑检查、解析器自检、三轮合成样本正反对照及缺失证据拒绝通过；当前冻结源 `ED5675C025C721FAA7764663DD4DD879BAB6A16CED6553D83D9A91DEDDA6AC0F` 的 P3/Steam/TapTap `BuildOnly`、ZIP/DLL hash 核对成功。128 构建输入中仍 52 未跟踪/32 历史修改，`CleanGitClosure=false`、严格发布门禁正确拒绝；旧双方日志没有新版桶或舰队追踪。未安装、未启动游戏、未修复实际舰队 BUG 或报告 P95 收益，保留他人工作区内容，未推送。 |
| optimization-v8-acceptance-execution-plan-20261017 | 本轮优化规划主控（文档占用已释放） | 仅新增 `docs/联机插件优化开发案-第八轮实测驱动实施-20261017.md`，在 `docs/联机插件优化开发案.md` 增一条最新入口；此表只登记本行，绝不占用 Game/Session/Core/Transport 功能文件 | 已完成新执行开发案：依据 V1.19、第七轮房主真实位移取证及 76 项逻辑检查、126 输入/52 未跟踪/32 修改的历史审计、冻结三端 BuildOnly 和缺新双端日志，明确 E8-01～E8-06 源码归属、现场实测、条件优化、多人回归与发布回滚；本轮只做文档，不构建、安装、启动游戏，不改功能源码。 |
| optimization-t7-motion-evidence-20261017 | 本轮主控（源码占用已释放，无子 Agent 环境） | `src/Core/FleetMotionEvidence.cs`、`src/Game/NativeFleetMotionEvidence.cs`；`src/Game/NativeGalaxy.cs` 中经 contextual patch 精准暂存的 10 行观察接线；`tools/OptimizationChecks/FleetMotionChecks.cs`、分析器、实施回执 | 已完成取证工具及代码提交 `9a426ae`：原版 `Move/MoveUi` 成功后观察房主位置，发生真实位移或 120 次 host tick 未变化时各只记录一次、64 条容量上限、默认 `FWM_PROFILE` 关闭且无坐标泄露；76 项逻辑检查及解析器自检通过，冻结同源指纹 `00E96D69971807EB9CE821645169D5E12D221437BC95FE0A7C985B0D6BC64421` 的 P3/Steam/TapTap 官方 `BuildOnly` 与 ZIP/DLL hash 校验通过。源码 126 输入中 52 未跟踪、32 旧修改，Git release gate 拒绝；无安装或双端运行，不宣称舰队已修复或性能已提速；历史混合代码仍原样保留。 |
| optimization-v7-measured-hotspots-plan-20261017 | 本轮规划主控（已释放文档占用） | 仅新增 `docs/联机插件第七轮优化开发案-热点实验与实测验收-20261017.md`；更新 `docs/联机插件优化开发案.md` 的最新执行入口；本协作表仅增加本任务行，不占用任何 Game/Session/Core 功能源码 | 已完成方案编写：对照当前 V1.19 的 `NativeMapReplicator.CaptureUpdate`、房主舰队指令与运动、事务主线程处理、地图 ready/FIFO、现有耗时桶和旧源码审计；创建 T7-00～T7-06 开发实验、Owner、前置门禁、P95 目标、2～4 人回归及回滚标准。仅文档，不构建安装或运行游戏；既有 91 条工作区变化保留。 |
| optimization-v6-evidence-closure-20261017 | 本轮实现主控（功能源码占用已释放） | `src/Entry/ModVersion.cs`、`src/Core/RuntimeSourceRevision.cs`、`tools/Analyze-MultiplayerDiagnostics.ps1`、`tools/Audit-SourceClosure.ps1`、`tools/Test-SourceClosure.ps1`、`tools/OptimizationChecks/*`，`src/Entry/MultiplayerDiagnostics.cs` 原未跟踪源码只加一行 `SourceRevision` 头；第六轮实施回执及复测手册 | 已完成工具/诊断代码并独立本地提交 `5d12d26`；59 项纯逻辑检查与独立 Git fixture 的干净/仅暂存/未跟踪检查通过；源码指纹 `D7F5CBF40A03C4A1A1738598AABE3F45CC3B0FC0C079C4C505732EFA193C66BB` 的冻结源码 Project3/Steam/TapTap 官方 BuildOnly、ZIP/DLL SHA、生成程序集版本属性核验通过。123 构建输入中 52 未跟踪、32 旧修改，Git 闭包**未通过**，严格发布门禁按预期拒绝；未安装、未执行游戏/Play Mode、未证明舰队根因或 P95。旧 `MultiplayerDiagnostics.cs` 完整文件未暂存、其他 Agent 全部改动保留，不推送。 |
| optimization-v6-delivery-plan-20261017 | 本轮方案主控（文档占用已释放） | 仅新增 `docs/联机插件第六轮优化开发案-源码闭环与实机验收-20261017.md`；在 `docs/联机插件优化开发案.md` 增加入口，协调表仅本任务登记行；不占用功能源码 | 已完成并释放文档占用：据 V1.19、120 个构建输入（53 未跟踪、32 已修改）、冻结三端 BuildOnly/ZIP 核验、旧双端日志不足制定 V6-01～V6-05 门禁、单热点条件优化、正确性回归和回滚要求。本轮仅文档修改，未触碰 Game/Session/Core 代码或 Project3 本体，未构建安装或运行游戏。 |
| optimization-f5-frozen-build-20261017 | 本轮实施主控（源码占用已释放） | 新增 `tools/Export-FrozenSourceSnapshot.ps1`、`Verify-FrozenSourceBuild.ps1`，增强 `Analyze-MultiplayerDiagnostics.ps1`；本轮新增第五轮实施回执及源码交接单，使用独立 `artifacts` 冻结副本执行 `-BuildOnly` | 已完成 F5-00 工作树冻结/核验（不是 Git 干净闭包）、F5-01 冻结源码三端官方 BuildOnly 和独立 ZIP/DLL hash 复核、F5-02 双端日志质量检查；最终 SHA256 `EC585BD2A375B25B1336A3F5D8A635A7FD49E27E7B3E0696ADEB68FBBAECF1D3`，120 输入中 53 未跟踪/32 修改，严格发布门禁正确拒绝；53 项逻辑测试、分析器自检通过。工具提交 `4bdcb87`，只读保留原有混合源码。缺新双端实机日志，舰队根因/P95 和后续 F5-03～F5-06 尚未完成；无安装、无游戏运行、无推送。 |
| optimization-phase5-closure-plan-20261017 | 本轮规划主控（文档占用已释放） | 只新建 `docs/联机插件优化开发案-第五轮闭环与实测-20261017.md`；在 `docs/联机插件优化开发案.md` 加入口；本协作文件仅增一条登记，不占用 Game/Session/Core 功能源码 | 已完成方案编写：根据已核实的 V1.19、2D 分段采样、三端历史 BuildOnly、118 个编译输入中 53 未跟踪/32 已修改、旧双端日志无最新舰队与 2D 性能窗口，形成 F5-00～F5-06 依赖任务、双端数据采集、条件优化、正确性回归及发布门禁。本轮仅文档修改；未改功能、构建安装或运行游戏。 |
| optimization-2d-profile-20261017 | 本轮 2D 优化实施主控（源码占用已释放） | `src/Game/NativeMapReplicator.cs` 的 `CaptureUpdate` 窄接线；新增 `src/Game/NativeGroundCaptureProfile.cs`、`src/Core/GroundCaptureMetrics.cs`、`tools/OptimizationChecks/GroundCaptureChecks.cs`，日志分析器和本轮回执；A0/A1 测试 | 已完成静态实施：实体/字段/格子/2D 位姿/个人字段分段采样＋数量汇总，使用 `FWM_PROFILE` 显式开关，默认无日志；53 项纯逻辑检查和分析器自检通过，三端官方 `BuildOnly` 成功、同源指纹 `B2C46B89C26FEDCFFC9560C896BE65AEFF7ACFB243983E947D16DA827F2ABFBD`。`NativeMapReplicator.cs` 已有其他 Agent 混合未提交改动且 HEAD 是另一套旧实现，**本轮的真实调用链接线仅保留在工作区，不整体暂存**；新独立类/工具按本任务提交。A0 闭包仍未通过，未安装、未运行、未宣称 P95/舰队故障修复，后续需要源码交接与双方实机日志。 |
| optimization-2d-performance-plan-20261017 | 本轮优化方案 Agent | 仅新建 `docs/联机插件优化开发案-2D稳定性与性能-20261017.md`；在 `docs/联机插件优化开发案.md` 加导航入口；本协作文件只登记本行 | 已完成并释放文档占用：依据 V1.19、历史三端 BuildOnly/源码审计、2D 实体/格子/位姿差量及 FIFO/ACK 实现创建 A0/A1/A2、B1～B6、C0 任务与回归门禁；仅文档修改，未改功能/协议/存档，未构建安装或运行游戏。 |
| optimize-execution-g0-g1-20261016 | 本次实施主控（已释放源码占用） | `tools/Audit-SourceClosure.ps1`、`tools/OptimizationChecks/Directory.Build.props`、`tools/Analyze-MultiplayerDiagnostics.ps1` 及新增实施回执，主控执行官方 `Sync-ModCopies.ps1 -BuildOnly` | 已完成：便携 PowerShell 7.5.4 的官方 SHA-256 校验，G0 源码审计与复核（115 个输入，53 个未跟踪/32 个已修改，Git 干净闭包仍**未通过**），40 项纯逻辑检查与日志工具自检通过；G1 三端 `BuildOnly` 正式包成功并验证相同源码指纹 `31909374BC70C30A95C08CBCBA2D1AE2BAD8FCBEBC82190DE22511F32AB05F2F`。旧双端日志无新舰队命令/性能窗口证据，F1/M1 仍无法得出修复与 P95 结论；不执行安装和游戏验收。新增工具独立提交 `3324ce9`、`8c871ba`、`2c8dd78`；旧 92 项工作区改动保留，未推送。 |
| multiplayer-optimization-next-plan-20261016 | 本轮优化案维护 Agent | 仅新增 `docs/联机插件下一阶段优化开发案.md` 和 `docs/联机优化下一阶段路线.svg`；`docs/联机插件优化开发案.md` 仅加入口；本协调文件仅本行 | 已完成，文档占用释放：核实 V1.19/协议6/存档1、Git 未跟踪地图源码、历史双端慢日志及 PowerShell 7 门禁；新方案按 G0/G1/F1/M1/O1～O3/R1 拆单并含 SVG、验收和回退要求；未修改任何功能源码，未构建安装或启动游戏。 |
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
