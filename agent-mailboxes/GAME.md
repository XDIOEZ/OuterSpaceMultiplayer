# GAME · 游戏适配 Agent 邮箱

> 用于原生 Hooks、舰队、星系、地图、战斗与地表同步相关留言。具体文件占用以 `../AGENT-COORDINATION.md` 为准。

## 消息

### MSG-20261019-bug31-GAME-02 — 原生回调按 fail-closed 路径隔离，请识别纯 Toast 回调
- 发件人：bug31-command-result-observer-fix-20261019
- 收件人：GAME
- 时间：2026-10-19
- 状态：局部代码已交付；Game 原 Owner 分类审查待办
- 关联任务/文件：`agent-tasks/待办/BUG-31-命令完成事件异常污染已提交结果.md`、`src/Game/NativeTravel.cs`、`NativeGalaxy.cs`、`NativeBaseCreation.cs`、`NativeTransfers.cs`、`NativeBuildingCommands.cs`
- 消息：本轮 `710f975` 把 Entry 绑定的 Fleet/Travel 真实原生投影从普通 CommandCompleted UI 反馈中拆出；注册到 `RegisterCommandCallback` 的专属回调仍按先移除再通知的 once-only 路径，异常时保守锁住 nativeFault/Paused，避免地图目录/舰队投影不一致时产生 UI 虚假成功。生产通知部分链接本地与隔离 HEAD 45025 项通过，但没有真正加载原生 FWStarMapInfo 或跨设备执行。
- 需要回复：实际 Game 注册点混合原版成功 callback 与 FairyUIDispatcher.showToast，需 Game Owner 对纯展示 Toast 失败和原生状态可能已变化的回调进行明确分类，测试确认错误可见、可正确暂停/重试且不能重复基地/运输/舰队副作用；如需新 UI-only callback 合同先与 Session/Entry Owner 协调，不要直接吞全部异常。
- 回复关联：MSG-20261019-bug31-GAME-01

### MSG-20261019-bug31-GAME-01 — 请界定舰队与地图原生完成回调的 fail-closed 语义
- 发件人：bug31-command-result-observer-audit-20261019
- 收件人：GAME
- 时间：2026-10-19
- 状态：待处理 / 不代表已接管
- 关联任务/文件：`agent-tasks/待办/BUG-31-命令完成事件异常污染已提交结果.md`、`src/Game/NativeGalaxy.cs`、`NativeTravel.cs`、`src/Entry/ModPluginLauncher.cs`
- 消息：`CommandCompleted` 的 Entry 订阅器会执行 `ObserveFleetCommandResult`、`ObserveTravelCommandResult`；后者可解析原生 FWStarMapInfo、验证权威 MapId/目录并触发原版 callback，前者消费一次性 fleetCallbacks。任一监听器异常可能从 `NotifyCommandResult` 穿透后影响房主/客户端对已确认命令结果的诊断。简单把所有回调当可选 UI 吞掉会掩盖原生投影/地图身份冲突。
- 需要回复：请 Game 原 Owner 定义哪些 native callback 失败须暂停本地投影或明确拒绝原生 UI 入口，哪些仅显示错误；协调 Session/Entry 保留唯一 CommandId、不重新执行资产侧副作用，联测 C31-05/06/09/10。
- 回复关联：MSG-20261019-bug31-UI-01

### MSG-20261018-bug25-game-01 — 银河重置隔离共享舰队与世界缓存
- 发件人：bug25-galaxy-reset-backoff-20261018
- 收件人：GAME
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Game/NativeFleetSynchronization.cs`、`src/Session/GalaxySnapshotSend.cs`、`agent-tasks/待办/BUG-25-银河全量重传滥用.md`
- 消息：已在提交 `4300276` 将 `ResetGalaxyRecipient` 改成只丢弃目标玩家出站银河基线，不再重置全局采样帧与共享世界缓存的强制刷新时钟；周期 Tick 和舰战入场共享先 Send 后 ConfirmGalaxySent 的完整水位逻辑。测试合成 10 秒 100 次银河 reset 为 3 次完整帧/17 次增量帧，其它玩家 20 次增量，生产链接 67924 项通过。未修改真实舰队推进、人物归属、存档及协议。
- 需要回复：请原 Game Owner 在实际 P3/Steam/TapTap 真实星系业务、实时舰队移动及战斗入场中检验去掉强制全局采样失效后仍能正确收到当帧及下一采样周期的资产与共享地图变化；本次不代表实机已验收。
- 回复关联：无

### MSG-20261018-bug17-map-uid-game-01 — 新实体 UID 必须有模拟者合法发行来源
- 发件人：bug17-map-uid-provenance-20261018
- 收件人：GAME
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/MapEntityUidValidation.cs`、`src/Game/NativeMapReplicator.cs`、`src/Game/NativeEntityOwnership.cs`、`agent-tasks/待办/BUG-17-地图增量UID号段校验.md`
- 消息：源码 `99ac673` 已限制 host 接收的远端新地图实体 UID：未登记 ID 只可信任本次模拟者当前 SessionId 内由房主发放的号段；已有 World/MapCheckpoint 的原有实体（含访客/运输）保持原归属并不强制重新走当前号段。严禁把其他用户未登记的 UID 当成同图新实体混入 Native MapUpdate；本机生产源码与真实 DTO 联编 26,959 项通过。
- 需要回复：请 Game Owner 对原生建造、合作访客创建的基地资产、跨图运输人员、NPC/怪物与读取旧地图完整检查点逐项检验 `RegisterNativeOwnership` / `RegisterEntityOwner` 的来源顺序，尤其是合法客端新实体是否真实使用当前 simulator-issued UID。未知旧实体可能暴露缺失的原生登记路径，不能简单白名单绕过；仍缺真实 P3/Steam/TapTap 运行验收。
- 回复关联：MSG-20261018-bug25-game-01

### MSG-20261018-bug07-native-freeze-01 — 断线原生冻结失败的房主权威锁
- 发件人：bug07-disconnect-fail-closed-20261018
- 收件人：GAME
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/PeerDisconnectCleanup.cs`、`src/Session/Authority.cs` 的 `ReassignMap`、`agent-tasks/待办/BUG-07-断线权威收口.md`
- 消息：`dba3e64` 在房主 RemovePeer 断线收口时隔离原生 SetPlayerOnline(false)/舰战/MapAuthority 设置故障。若某图的 ReassignMap 在任何阶段失败，重新失效模拟者并提高 AuthorityEpoch、Frozen 后请求 Native SetMapAuthority；任一权威核心步骤失败则 LockNativeFault 暂停房主，不允许保存未经确认的半次移交。生产联编假原生故障注入 210502 项通过，无真实 Unity 双端验收。
- 需要回复：请 Game Owner 在 P3/Steam/TapTap 的多地图、地表访客、原生英雄在异地图、太空舰队离线与舰战进行中测试冻结/解冻及真实回调异常；确认原生无法冻结时的恢复策略和已确认地图检查点有效性，不能因 UI 通知成功而视为状态安全。
- 回复关联：MSG-20261018-bug17-map-uid-game-01

### MSG-20261018-bug01-game-map-commit-01 — 地图差量与执行声明关联仍待验证
- 发件人：bug01-remote-command-enqueue-20261018
- 收件人：GAME
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/Commands.cs`、`src/Session/RemoteCommandDispatch.cs`、`agent-tasks/待办/BUG-01-远程地图提交时序.md`
- 消息：`3f21d34` 将模拟端的地图发布安排在 execution 声明发送之前；新的成功声明只会在 PublishLocalMaps 本身没有抛错后入队，原生 commandId 缓存不变。尚未保证捕获地图一定包含此次命令、没有既有 in-flight 差量，也没有增加提交身份/存档水位；不能单凭发送顺序声称结算完成。
- 需要回复：请 Game/Session Owner 逐项验证原生地表建造、拆除、移动、工作和背包指令在 `CaptureMapUpdate` 中哪些操作会产生可确认的实际 MapRevision；用真实地表测试地图拒绝/断线重播，不要重复原生命令，也不要把无差量执行误报为已保存。
- 回复关联：MSG-20261018-bug07-native-freeze-01

### MSG-20261019-guest-conscript-GAME-01 — 解除征召参数恢复
- 发件人：guest-conscript-20261019 / conscript_fix
- 收件人：GAME / 主控
- 时间：2026-10-19 +08:00
- 状态：源码冻结，待主控 BuildOnly；运行验收待用户
- 关联任务/文件：src/Game/GameBridge.cs:507；artifacts/guest-conscript-20261019/conscript-functional.patch
- 消息：原生 UI 传入 false 表示解除征召，GameHooks 已捕获 Manual=false，但 GameBridge 的 Conscript 执行固定 true，导致开启和关闭请求均变成开启。仅将该执行参数恢复为 payload.Value<bool>("Manual")，保留权限与模拟者路径、原生任务释放/自动模式处理。修改前完整工作树文件已备份至 artifacts/guest-conscript-20261019/baseline/src/Game/GameBridge.cs；其他旧 dirty 保留。
- 需要回复：主控统一版本与三端 BuildOnly、窄提交；用户选择客机自己雇员，分别在本人模拟地块和他人模拟地块执行开启、解除及批量开关，确认解除后恢复自动工作。另有独立 UI 投影缺口：NativeMapReplicator.Pose 不含运行时 fightMode，非模拟视图 Hero_MainView_Cmd 读取 getRunAI().isFightMode，可能缺失状态回显，已通知主控，不在本一行修复中扩大。
- 回复关联：无
- 处理记录：未启动游戏、未执行测试、未编译、未安装、未提交；遵用户后续统一安装要求。- 后续处理记录：主控授权同链 UI 回显修复，增加 NativeMapReplicator.Pose.FightActive/FightManual 两个 nullable bool，采集当前真实战斗 AI 活跃引用和手动模式，SamePose 纳入比较。非模拟端 ApplyPose 只 setFightMode 纯赋值并缓存反射直接设置 BaseRole.runAI 展示引用；不调用 setRunAI/doFightMode/onActiveEvent/closeAI，不推进 AI。自动战斗保持 fightAI+自动模式；仅远端 FightActive=false 且当前引用是 fightAI 才切回 baseAI，不覆写其它 AI 展示引用。新基线 NativeMapReplicator.cs 已备份，同一个本轮 patch 已含两个文件全部窄差异；V1.40 共用，源码冻结等主控 BuildOnly。未做测试或实机验收。

### MSG-20261019-ground-rules-native-01 — 地块搜刮原生接入冻结
- 发件人：ground-rules-implementation / ground_native
- 状态：源码冻结交主控；游戏验收由用户执行
- 文件：GameHooks.cs、NativeRaid.cs、NativeRaidLoot.cs、NativeTransfers.cs、NativeBaseCreation.cs、新 NativeGroundLoot.cs。
- 结果：已落地真实运输原单恢复本场容量；保护逐储存区识别并以现有Loan预占禁止提前消耗/装备/落地转移；同场合作先成功者物资入其舰队，运输前重判守卫/关系；公共/自己库存普通运输无搜刮配额限制；原版正式基地废弃入口改为转中立并保作者；已恢复的HP清除KnockedOut标记。
- 边界：不改本体/运行副本，未测试、构建、运行、安装或提交；主控统一BuildOnly。
- 需要回复：编译若有签名问题由主控分发；无新增玩法待定项。
