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
| host-exit-v1.12 | 本次修复主 Agent | 整合房主离开后的客人退出、版本和三端构建 | 源码已提交 `2972361`；统一构建被下方依赖阻断，尚未安装 |
| host-exit-session | 子 Agent `/root/host_exit_session` | `src/Session/MultiplayerSession.cs`、`src/Session/Networking.cs` | 实现与接口说明已交主 Agent 整合；文件占用已释放；未运行测试或游戏 |
| host-exit-entry | 子 Agent `/root/host_exit_entry` | `src/Entry/ModPluginLauncher.cs`、`src/Game/GameHooks.cs` | 已整合，占用已释放；原生返回保留 `callbackInt`，待会话清理后执行；封面就绪后仅向 `HostLeft` 客人提示一次；未运行游戏验收 |
| agent-coordination-file | 本次修复主 Agent | `AGENT-COORDINATION.md`、`AGENTS.md` | 协作入口、登记模板和本轮分工已建立，两位子 Agent 已各自更新交接；文件占用已释放 |
| UI-20261016-01 | 本轮 UI 结构代理 | `src/Entry/MultiplayerVisualTheme.cs` | 初始化拆为反馈、房间、连接和输入四个步骤；本地提交 `0fbf8c7`；未做游戏内验收，文件占用已释放 |
| UI-20261016-02 | 待认领 | `src/Entry/MultiplayerRoomDialog.cs`、`src/Entry/MultiplayerLobbyPages.cs`、`src/Entry/MultiplayerRoomPages.cs` | 计划梳理导航与状态刷新边界；这些文件已有未提交改动，必须先确认负责人和交接，未获许可不修改 |
| UI-20261016-03 | 本轮 UI 结构代理 | `src/Entry/MultiplayerVisualTheme.cs`、`src/Entry/MultiplayerPlayerRosterVisual.cs` | 已完成：视觉刷新和玩家列表数据 / 显示职责拆分，本地提交 `a7c6d6c`；未构建或游戏内验收，文件占用已释放 |

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
