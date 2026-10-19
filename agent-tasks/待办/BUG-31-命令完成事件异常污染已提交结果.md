# BUG-31 / P1 · 命令完成监听器异常可能污染已提交结果与消息处理

> **状态：待办 / 局部生产源码分阶段隔离已提交、本机生产 partial 故障注入通过；原 Owner、三端完整构建与真实联机仍待验收**
>
> **源码基线：** `codex/multiplayer-v1`，建卡 HEAD `aae510d8dc3a668c31c52df3362a173733af9b16`。
>
> **建议 Owner：** Session 命令结果/权威事务 Owner 主导，Entry/UI Owner 与 Game 原生舰队/地图解析 Owner 共同签收观察者职责；主控协调 R13 源码闭包、版本与三端发布。**本卡不构成接管他人混合未提交源码的许可。**
>
> **等级依据：** P1（已应用命令被 UI/本地回调异常误判或引起错误断链的条件性风险）。**未证明玩家实机已发生，不可表述为实际资产复制、丢失或已经提交到磁盘的结果被回滚。**

## 1. 从生产源码确认的路径（以下是首次建卡时的旧版调用链）

| 路径 | 已核实的调用/顺序 | 如果监听器抛异常的潜在影响 |
| --- | --- | --- |
| `src/Session/MultiplayerSession.cs::NotifyCommandResult` | 先 `TraceCompletedFleetResult`，然后删除一次性 `commandCallbacks[CommandId]` 并调用专属回调；该回调已单独 `try/catch`，但 `catch` 内再调用未保护的 `Error`。最后直接 `CommandCompleted?.Invoke(result)`，**未有单独观察者异常边界** | 专属回调本身抛错后，`StatusChanged` 若再抛错，仍能从 `Error` 逃逸；公共事件任一监听器失败也直接穿透 |
| `src/Session/Commands.cs::Submit` | 房主执行 `ExecuteHostCommand(command)`，得到结果后 `NotifyCommandResult(result)`，最后才 `return result`。`ExecuteHostCommand` 可已写入 `World.Transactions`，关键命令在此之前尝试 `SaveOrThrow` | **本地命令可能已应用，甚至关键结果已由房主保存**，但仅由于后续监听器失败，`Submit` 对外抛异常而不是返回真实结果；调用端可能认为操作未执行 |
| `src/Session/Commands.cs::AcceptExecution` | 远程模拟者回传结果后先移除 `pendingExecutions`、登记 `World.Transactions[key]`；没有 `commandReplyPeers` 时调用 `NotifyCommandResult(result)` | 已修改主机事务内存状态后，监听器异常可穿透回负责接收远程 `execution` 的网络分发；不能将本机通知失败重新解释为模拟者的恶意/无效包 |
| `src/Session/Networking.cs::HandleClientMessage("result")` | 从房主收到 `result`，直接调用 `NotifyCommandResult(JsonData.Payload<CommandResult>(...))` | 合法的房主结果已收到，UI 监听器故障可能进入 `MultiplayerSession.Tick` 通用报错路径，生成“联机消息拒绝”或污染故障诊断 |
| `src/Session/MultiplayerSession.cs::Tick` | `HandleTransport(item)` 抛异常在主入站 `catch (Exception)` 中调用 `ReportMessageFault`、`Error("联机消息拒绝...")`；房主可能继续尝试通知或隔离消息来源 | 对来自远端模拟者的 `execution`，本机结果观察者异常理论上会被错误归责给发送该消息的 peer；可能使其遭受不应有的错误回复或隔离 |
| `src/Entry/ModPluginLauncher.cs::CommandCompleted` | 当前订阅者先执行 `Bridge.ObserveFleetCommandResult` 与 `Bridge.ObserveTravelCommandResult`，再处理错误日志/`Feedback` 文案 | **它不是单纯的 Toast**：前两步会完成原生舰队 callback、解析权威地图信息与调用原版地块回调，出现真实原生投影/身份不匹配时不能简单全部吞掉；但错误也不该令已经执行的主机 `CommandId` 反向失效 |
| `src/Game/NativeTravel.cs::ObserveTravelCommandResult` | 用 `pendingMapResolutions` 找到回调，对 `PayloadJson` 解析并校验星球/地图信息，在成功校验后才移除待回调并执行原生 callback | 可能有真实的原生目录/身份校验失败，必须诊断且让相应原生入口 fail-closed；不要因统一忽略异常把不存在的地图解析为有效世界 |

**可疑事件示例（尚未注入）：** 房主 A 本地执行原生命令后，`World.Transactions` 已记录真实结果；某个 `CommandCompleted` 监听器因 UI 对象被销毁、舰队反馈对象不可用或投影数据不一致而抛异常。`Submit` 的异常与真实 `CommandResult` 不一致。另一个场景是远端模拟者回 `execution`，房主完成内存登记之后，本地监听器异常进入“联机消息拒绝”的通用收包分支。**这个风险不意味着现有 GameBridge 一定抛错，也不代表房主已经确认 Save。**

## 2. 与已登记 BUG 的区别

- [BUG-09 · UI 同步拒绝](BUG-09-UI同步拒绝提示.md) 是 `Submit` 返回即时 `CommandResult.Fail` 却被 UI 忽略以及等待文案问题；**本卡是已经产生结果之后的结果监听器反向抛错与归责问题**，不能重复改 `CommandSubmitStatus` 的等待语义。
- [BUG-28 · 聊天/参观回调异常](BUG-28-通讯通知回调异常阻断房间Tick.md) 已将 `ChatChanged / VisitChanged` 可选观察者隔离；**本卡针对 `CommandCompleted`、专属 `commandCallbacks`、Entry 的实际 Native callback**，这些回调含有重要的原生完成逻辑，不可直接照搬“全部按 UI 忽略异常”。
- [BUG-01 · 地图权威提交时序](BUG-01-远程地图提交时序.md) 仍需解决命令 ↔ MapCommit/Revision 的最终关联。**本卡不改变 `Applied/Saved` 何时成立**，只确保最终本地回调失败不改写或伪造房主事实。
- [BUG-22](BUG-22-二次错误回送.md)、[BUG-30](BUG-30-地图重放拒绝提示失败误锁房间.md) 管理网络拒绝及可选错误提示失败；这里的前置事件是**有效结果的本机监听器异常**，不能因为公用网络 `catch` 的拒绝隔离就把已合法完成的消息标为坏 peer。
- [R13-01](R13-01-源码Owner签收.md) / [R13-01B](R13-01B-20261019-构建源码依赖闭包与Owner签收.md) 尚有未提交源码/Owner 签收依赖；本卡不解除任何原有源码冻结。

## 3. 必须保持的业务和安全不变量

1. **主机结果不可被事后观察者改写：** `CommandId / PlayerId / SessionId`、`Applied`、`Saved`、`SaveGeneration` 只由房主事务和真正持久保存决定；本地 UI/Native callback 抛错不能把已经应用的命令变成可重复执行的“未收到结果”。
2. **不吞原生错误：** 舰队回调、地图目录注册、`NativeWire.Unpack`、原生世界入口可能有真实状态不一致；必须保留明确的本机原生/投影故障处理，必要时只暂停相关操作/投影，不得假装成功、返回虚假 `MapSnapshot` 或解除 `nativeFault`。
3. **可靠结果仍然唯一：** 专属 `commandCallbacks` 原本取出一次就移除，应明确失败后的重试/清理策略，不允许因 UI 重放重新发送 `execute`、重复生产资产、重复保存或抹掉别的 CommandId 回调。
4. **单个坏观察者不影响健康者：** 如果故障仅属于可选 UI 文案，不能阻止下一个已注册的独立订阅者获得同一个结果；已登记的房主 `World.Transactions` 不因监听器错误而重写。
5. **真正非法输入仍需拒绝：** 坏 `CommandResult` 契约、房主/模拟者身份不符、过期 AuthorityEpoch、伪造结果和原生提交失败必须保留原 `MessageRejectionException / InvalidDataException` 或权威拒绝边界；不能用通用 `catch` 把业务输入验证也隔离。
6. **保密/诊断：** 日志只标注固定阶段、回调类型、结果状态与必要的非敏感关联信息；不要记录原始 `PayloadJson`、凭证、密钥、聊天、完整个人存档或异常内含的敏感原生 JSON。
7. **兼容与版本：** 事件公开签名、现有协议 Kind/CommandResult 契约、文件存档格式、Fleet/Travel 原生回调生命周期在兼容审查前不改变。

## 4. 修复与集成任务（局部已完成，仍需 Session + Entry + Game 原 Owner）

- [x] **T31-1a / 初步生产回调分类：** 已确认 `Submit`、`AcceptExecution`、客户端 `result` 仍调用同一个真实 `NotifyCommandResult`。Entry 唯一内部订阅器已将 `ObserveFleetCommandResult / ObserveTravelCommandResult` 分到**原生投影事件**，`Feedback` 留在原 `CommandCompleted` 可选 UI 事件；新增代码不改游戏协议与 `CommandResult` DTO。
- [ ] **T31-1b / 其它 Game 回调 Owner 签收：** `RegisterCommandCallback` 的实际注册点还覆盖 `NativeBaseCreation`、`NativeBuildingCommands`、`NativeTransfers`、`NativeTravel`，这些回调既可能修改原生状态，也可能仅弹 Toast。当前默认专属回调错误按潜在原生不确定**保守锁定**；需原 Game Owner 逐项区分纯 UI 与关键 native，避免 UI Toast 异常导致过度冻结。
- [x] **T31-2a / 结果通知代码已分阶段：** 生产 `NotifyCommandResult` 已移到 `src/Session/CommandResultNotifications.cs`；先做真实专属回调和原生投影（异常只暂停/标记本机原生故障，不重写房主结果），再对可选 `CommandCompleted` 逐观察者隔离；仍保留本来由上层实现的事务验证、存档和身份检查。
- [x] **T31-3a / 诊断二次异常局部完成：** 本机原生故障通过 `LockNativeFault` 保留 `nativeFault/Paused`；暂停广播或 `StatusChanged` 再抛异常时保留障碍；可选 UI 错误不会穿透网络/命令调用。日志只含固定 stage/异常类型、每会话至多 32 种签名，不含结果载荷或异常消息。
- [ ] **T31-4 / 原 Owner 与统一发布：** 代码子补丁已按纯自己 hunk 精确提交，其它 Session/Entry/Game 未提交代码完整保留；但 Owner 正式签收、VERSION/CHANGELOG、R13 源码闭包、三端 BuildOnly 未完成，**不得勾选完整发布项**。
- [x] **T31-5a / 生产通知部分链接测试：** 新建 `tools/CommandResultObserverChecks/`，直接编译真实 `CommandResultNotifications.cs`，测试专属回调、两阶段观察者、故障锁、重复 CommandId、同步重入与日志二次失败。
- [ ] **T31-5b / 真实业务与游戏验收：** 测试目前没有完整编译 `Commands.cs/Networking.cs/NativeTravel.cs/NativeGalaxy.cs` 或真实 Unity Bridge，尚缺跨端 ACK、资产与 SaveGeneration 的端到端证据；须在原 Owner 签收后完成。

## 5. 真实生产故障注入与验收矩阵（部分 S1 已执行，S2～S4 未执行）

| ID | 故障注入 / 状态 | 完成时必须观察 |
| --- | --- | --- |
| C31-01 | 房主本地 `Submit` 一条可确证的成功命令，`CommandCompleted` 的**纯 UI**监听器抛 `InvalidOperationException` | 权威 `CommandResult.Applied/Saved` 仍与原始事务、保存证据一致；本机 Submit 不虚报“命令被拒绝”，不再次执行 Native |
| C31-02 | 作为客端接收合法 `result`，让第一个 UI 监听器抛错，第二个监听器健康 | 结果只消费一次，第二个观察者仍能看到同一 CommandId；不能报告为房主恶意报文，也不能误断房主 |
| C31-03 | 房主 `AcceptExecution` 已移除 `pendingExecutions` 并记录事务后，本机监听器抛错 | 不能将来自有效模拟者的 `execution` 作为非法输入隔离，命令不被二次执行；与 BUG-01 的地图提交确认分开检查 |
| C31-04 | 专属 `commandCallbacks` 本身抛错，处理提示时 `StatusChanged` 又抛错 | 本机反馈失败不污染已完成事务，专属回调不会重新安装或重复调用；其它已排队结果继续正常处理 |
| C31-05 | `ObserveTravelCommandResult` 解析出无效星球/MapId、`NativeWire.Unpack` 异常或原生地图目录不匹配 | **真实本机原生投影故障必须可见且 fail-closed**，不返回伪造地图、不把已确认房主 CommandResult 改成“未执行” |
| C31-06 | `ObserveFleetCommandResult` 的 callback 抛错，随后收到同 CommandId 重发结果 | 一次性 Fleet callback 语义保留；不重放原生控制、不重复返还物资或航行消耗；独立健康观察者不会被其阻塞 |
| C31-07 | 同时处理 A、B、C 多名玩家结果，只有 A 的 UI 观察者失效 | 其他结果按真实网络顺序处理，正常回执、房主 Tick 和保存不因 A UI 错误连坐 |
| C31-08 | 回调同步重入 `Submit`、解绑自己、再次订阅或在退出房间期间处理结果 | 事件快照、CommandId 去重及 Session 生命周期一致；不复活关闭连接、不重复资产副作用 |
| C31-09 | 注入真 `SaveOrThrow` 失败、原生事务不确定、旧任期模拟者 `execution` 及伪造结果 | 这些真实错误**依然可被拒绝/暂停**，不能被可选回调保护吞掉；日志不含私有 PayloadJson |
| C31-10 | 用户 TCP/Steam 双端/3 人测试：房主控制、远程执行、地图进入、舰队航行及同步拒绝 | 记录原生状态、命令 ID/SaveGeneration、ACK、双端地图/个人 Revision 与舰队状态；没有假成功、重复运行或错误断链 |

**证据分级：** S0 源码静态审查（完成）→ S1 真实生产 `NotifyCommandResult` partial 与模拟结果/异常注入（本轮局部完成）→ S2 完整 `Commands/Networking`、Unity Native Travel/Fleet 和实际传输失败注入（未做）→ S3 原 Owner 签收的干净源码同源三端 `BuildOnly`（未做）→ S4 用户真实 TCP/Steam 联机验收（未做）。**不把静态条件风险写成已复现，也不把 partial 逻辑测试当作整套联机通过。**

## 6. 关闭标准和本轮边界

需全部满足：Session/Entry/Game 原 Owner 签收并保留真实 Native/Save fail-closed；C31-01～09 必要生产接线/负向故障注入有可复核结果；R13 源码输入 0U/0M、版本/CHANGELOG 和同源三端 BuildOnly；用户 C31-10 真实 TCP/Steam 多人世界回归。**仅修复代码仍必须保留 `待办/`，不得提前归档。**

### 2026-10-19 / 生产局部修复与本机证据

**源码与测试提交：** `710f975ef86598f6cd29419b81f8b158f09c6f9e` — `fix: separate native command callbacks from optional result observers`。在其它 Agent 同轮更新 HEAD 为 `cd09129` 后重新检查缓存 index，**仅暂存 6 个本轮文件和精确 hunk**，其余旧 `MultiplayerSession.cs`/Entry、GAME、VERSION/CHANGELOG 混合差异原样保留。

**代码实际接线：**

1. `src/Session/MultiplayerSession.cs` 只移出旧 `NotifyCommandResult` 方法（12 行），由**新生产 partial** `src/Session/CommandResultNotifications.cs` 承接，保持原有调用方法名与 `Submit`、`AcceptExecution`、`HandleClientMessage("result")` 接口不变。
2. 新增内部 `CommandProjectionRequired` 原生投影事件，专属 `commandCallbacks` 保持**先移除后通知**；同步重入同一 `PlayerId/CommandId` 只递送一次活动调用栈，其他 CommandId 可重入。原生回调异常锁住本机 `nativeFault/Paused`，即使广播暂停/状态 UI 再抛错，也不会把已保存结果变成来自远端的非法报文或 UI 假成功。
3. 原公开 `CommandCompleted` 保持 `Action<CommandResult>` 签名，作为**可选结果 UI 通知**逐订阅者派发；一个坏监听器不阻止后续健康监听器，不覆盖 `Applied`、`Saved` 或 `SaveGeneration`。日志仅固定 `command.native-callback` / `command.native-projection` / `command.presentation` 及异常类型，最多 32 种签名每 Session。
4. `src/Entry/ModPluginLauncher.cs` 仅精确 stage 三处窄范围：创建 Session 时订阅新原生投影事件；把 `Bridge.ObserveFleetCommandResult`、`ObserveTravelCommandResult` 分离到 `ProjectCommandResult`，`CommandCompleted` 保留现有 UI 文案/错误反馈；退出房间时显式解除原生事件订阅。**没有修改 Game 的 native 回调实现、网络 DTO、协议、存档或原生对象。**

| 实际执行 | 观察结果 | 不能替代的证据 |
| --- | --- | --- |
| `dotnet run -c Release --project tools/CommandResultObserverChecks/CommandResultObserverChecks.csproj --no-restore` | **45,025 项通过**，直接编译生产通知 partial；涵盖成功/失败/未保存结果原对象与水位不变、一次性 callback、坏 UI 与健康 UI、原生投影失败本机锁、Toast/状态/log 二次异常、同步重入及 15,000 轮随机检查 | `CommandResult` DTO 和外围 `HostFinish/ReceiveHostResult` 在测试工程中模拟；不代表完整生产 `ExecuteHostCommand`、网络或 GameBridge 路径 |
| `git archive HEAD src/Session/CommandResultNotifications.cs tools/CommandResultObserverChecks` 导出至 `artifacts/bug31-clean-head` 后独立运行 | **同样 45,025 项通过**，确保本提交的生产 partial 与测试没有依赖其它 Agent 的未提交源码 | 只是此小范围 HEAD 产物，不是整个 MOD 的干净源码闭包 |
| `tools/RemoteCommandDispatchChecks`、`OptimizationChecks`、`ReliableCommitFanoutChecks`、`FinalCheckpointGateChecks`、`CommunicationTickChecks` | 分别 **242,846 / 185 / 30,027 / 53,972 / 94 项通过** | 均是独立生产辅助方法/局部场景，未直接联编真正 Entry.NativeTravel 回调或双端事务 |

**C31 细分：** C31-01～04、06～08 的部分会话通知逻辑和纯回调注入已过；C31-05/06 的真实 FWStarMapInfo / 舰队回调对象未运行；C31-09 仅确认真实通知方法不会吞模拟的前置跟踪异常，未注入真的 `SaveOrThrow`、旧任期结果或桥接原生错误；C31-10 完全未执行。旧代码的 BUG-01 **命令 ↔ 地图权威 CommitId/Revision 因果关联** 仍独立待办，不能以通知隔离视作已解决。

**需原 Owner 重点审查的设计限制：** `Context.RegisterCommandCallback` 是原生基地、建筑、运输、地块访问等多种回调共用入口。它们既可能只显示 Toast，也可能调用原版完成函数或修改原生状态。当前收到专属回调异常时采用保守的本机原生锁，安全但可能因纯提示故障而**过度冻结**，必须由 Game/Entry Owner 明确分类后再细化。原 `CommandCompleted` 是公开事件；其它未知订阅方是否进行不可忽略的 Native 副作用尚未核实。正式交付前还需检查注册/解绑生命周期、实际 Native 回调异常不会伪造 ACK/已保存，以及 UI 完全失败时玩家仍能获知原因。

**发布边界：** 本轮不修改 `VERSION/CHANGELOG.md`（已有其他 Agent 未提交版本日志）、不运行 Unity/Play Mode，不对 Project3/Steam/TapTap 安装副本构建同步、不运行多人游戏或推送远程。BUG-31 **继续留在 `agent-tasks/待办/`，待 Owner、R13 与用户完整联机验收齐全才能归档**。

**历史首次建卡动作：** 上一轮创建 BUG-31 时只做静态审查与交接，没有修改业务源码；**本轮已实施上述局部生产通知分层修复**及测试，未修改其他 Agent 的历史差异。
