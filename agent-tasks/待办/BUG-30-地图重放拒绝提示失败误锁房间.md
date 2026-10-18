# BUG-30 / P1 · 地图延迟重放的拒绝提示发送失败可能误锁房间

> **状态：生产局部代码修复已提交 + 本机真实重放 partial/隔离门禁故障注入通过；Owner 签收、完整主循环/三端构建和实机验收仍待办**
>
> **发现：** 2026-10-19；`codex/multiplayer-v1`；静态审查 HEAD `5442553`。
>
> **建议 Owner：** Session 地图加载/消息重放原 Owner 主导；Session 拒绝消息隔离（BUG-22）Owner 复核复用方式，Transport Owner 验证 TCP/Steam 出站拒绝语义；原生/保存和三端发布由主控协调。**本文件不代表已经接管任何源码或解除冻结。**
>
> **重要限定：** 下面是从真实调用顺序得出的条件性故障路径，**没有证明玩家实机已出现整房停顿**。不能把这个风险混同为业务提交已损坏，也不能用忽略 map/ACK/存档错误来“修复”。

## 1. 具体风险与触发链

地图原生初始化期间，房主会把需要延迟应用的可靠消息保存在有序 `loadingMessages` 队列中。等原生地图就绪后，`TickMapLoading` 按原顺序调用 `HandleHostMessage`，某些**未获授权或已经过期的指令**可以合法地产生 `MessageRejectionException`。在**初次建卡的旧源码**中，这个拒绝分支会直接发送一次非权威 `error` 提示给该 peer。

**旧版本问题：** `Send(queued.Peer, "error", rejected.Message)` 未隔离 TCP/Steam 队列已满、断链/旧 peer 等异常。如果这个**辅助提示**也发送失败，会穿过 `catch (MessageRejectionException)`，被更外层的 `catch (Exception)` 当作 **地图加载失败** 处理，最终执行 `FailMapLoading`、`mapLoadingFailed = true`、`LockNativeFault`。这条条件性风险已通过下文所述的局部代码修复隔离；**不等于已在真实游戏中确认发生或完成全部验收。**

### 已核实的生产调用链

| 环节 | 文件 / 方法 | 可核实行为与条件 |
| --- | --- | --- |
| 入站推迟 | `src/Session/Networking.cs::HandleTransportCore → DeferMapLoadingMessage` | 已认证消息在地图障碍存在时进入 `loadingMessages`；非豁免类如 `visit.cancel / visit.decision` 可能等待本次地图初始化结束 |
| 状态可能过期 | `src/Session/PlayerVisits.cs::HostVisitCancel` 等 | 参观申请已撤销/不存在、该请求已不属于当前玩家等情况下抛 `MessageRejectionException`；时间流逝与正常断线清理可改变旧申请有效性 |
| 延迟执行 | `src/Session/MapViewLoading.cs::TickMapLoading` | 队首先删除、扣回 `loadingWireBytes`，设置 `replayingLoadingMessage = true`，房主将当初的 peer/player 映射核对后再调用 `HandleHostMessage` |
| 合法拒绝 | 同文件 `catch (MessageRejectionException rejected)` | 注释明确：“旧租约/指令可能在原生初始化完成前过期，尚未应用，也不应使地图加载失败” |
| **旧版未隔离的通知（已修复）** | 同一 `catch` | 首次建卡时在 `Error` 后直接 `Send(peer, "error")`；提交 `3253403` 后改为既有可选通知/隔离策略，不让该二次发送异常冒充地图应用错误 |
| 二次异常进入故障（已对可选通知收口） | 同方法外层 `catch (Exception error)` | 旧版的 `Send` 异常可以进入 `FailMapLoading`；现行代码仍保留**真正地图/原生故障**的外层 fail-closed |
| 房间后果 | `FailMapLoading` | 设置 `mapLoadingFailed`，调用故障报告及 `LockNativeFault`；后续正常地图重放/世界推进按原 fail-closed 规则受限 |
| 传输证据 | `src/Transport/TcpTransport.cs::Send`、`SteamTransport.cs::Send` | 满队列/连接关闭会同步拒绝；BUG-29 已调整 TCP 在途字节预算，使超限更早报告 `IOException`，但不应让正常拒绝回报变成地图事务错误 |

**示例（尚未注入）：** A 的 `visit.cancel` 在地图加载障碍中排队；该申请在回放时已失效，房主按业务规则拒绝；与此同时 A 的 TCP 或 Steam 发送队列拥塞，连“申请已失效”的 `error` 都无法入队。业务未被应用却触发地图故障，房主对正常 B/C 的地图、时间或保存可能受到影响。这个风险是否在全部实际状态机条件下可达，应通过生产 partial 故障注入证实。

## 2. 不与现有 BUG 重复

- [BUG-22 · 拒绝后的二次错误回送](BUG-22-二次错误回送.md) 已在 **`MultiplayerSession.Tick` 的主网络收包 `catch`** 接入 `OptionalPeerNotice` 与 `RejectedPeerEvictionGate`，防止“拒绝提示和 Disconnect 又失败”连坐整房。本卡最初审查的是不同入口 `TickMapLoading` 的延迟重放 `catch`，**本轮已在此入口复用同一现有生产策略**，没有创建另一套拒绝门禁。
- [BUG-14 · 缓存超时通知](BUG-14-跨机运行回归.md) 是地图缓存 offer 超时后的错误通知；本卡是**被重放的过期业务消息已被拒绝之后**，其可选 `error` 发送再失败。
- [BUG-24 · resync 请求](BUG-24-地图重同步发送失败.md)、[BUG-27 · 缓存协商](BUG-27-地图缓存协商发送失败与重试收口.md) 均处理正常地图恢复请求**入队失败**，本卡不能重新修改它们的恢复额度或 FIFO。
- [BUG-29 · TCP 预算](BUG-29-TCP发送中帧未计入队列内存预算.md) 为传输层入队容量提供边界；本卡要求 Session 在更早收到 `IOException` 时保持正确的“辅助提示失败”语义。
- [R13 源码 Owner/闭包](R13-01-源码Owner签收.md) 和 [R13-01B](R13-01B-20261019-构建源码依赖闭包与Owner签收.md) 仍阻塞完整交付：`MapViewLoading.cs` 本来就有混合未提交差异，必须先由原 Owner 核对 hunk，不能直接整体暂存。

## 3. 修复必须满足的不变量

1. **拒绝不是应用：** 只有经过原业务检查的合法 `map/personal/visit/command` 才可应用/提交/ACK；`MessageRejectionException` 对应请求不得被报告为 `Applied/Saved/Delivered`，更不得因为提示失败重放或重复消耗资产。
2. **仅隔离可选错误回送：** 捕获范围只限向原 peer 的 `error` 发送或关闭隔离，不包住 `HandleHostMessage`、`ReceiveMapView`、`ApplyLocalMapView`、`SaveOrThrow` 或整个 `TickMapLoading`；真正原生不确定、个人进度、存档、地图覆盖缺失仍应 fail-closed。
3. **队列原有顺序与预算：** 已出队且**未应用的拒绝包**继续按现行行为消费；后面 B/C 的合法包按 FIFO 重放。保持 `loadingMessages` 128 条、32 MiB 软上限、60 秒初始化/重同步期限，且不清空合法在途事务。
4. **隔离有界且按连接：** A 的错误提示与断链都失败时应借用已有 `RejectedPeerEvictionGate` 隔离真正故障的 peer，并按原设计重试关闭；A 不可在断链前继续变更权威状态，B/C 不受连坐。同步重入/旧 peerId 复用和当前 player 映射要单独验证。
5. **状态提示是诊断，不是权限：** `Error`/`StatusChanged` 的界面通知与传输 `error` 不能替代房主的授权撤销。诊断不能泄漏登录秘密、恢复码、聊天正文或玩家个人资产。
6. **兼容两种传输：** 对 TCP/Steam 的已认证 peer 同样适用，不新增 `WireMessage.Kind`、`Protocol.CurrentVersion` 或 `WorldState` 存档字段。

## 4. 建议实施（待原 Owner 签收）

- [ ] **B30-1 · 确认真实边界：** Session Owner 在 HEAD + 工作树核对 `MapViewLoading.cs` 与 `BUG-22` 的已有隔离代码，确认 `MessageRejectionException` 的所有可达来源、合法旧租约的失效方式及同一 peer / player 绑定。
- [x] **B30-2 · 局部隔离代码已完成：** `TickMapLoading` 的拒绝回送已使用原有 `OptionalPeerNotice.SendOrDisconnect` / `RejectedPeerEvictionGate`；提示成功不隔离，失败时仅跟踪该 peer 的真实断链且隔离其**已经排入地图 FIFO 的后续消息**；异常状态不能变成业务成功 ACK。**完整房主业务验收另待原 Owner。**
- [x] **B30-3 · 局部诊断与同步重入代码已完成：** `Error/StatusChanged` 的可选 UI 故障和 `Disconnect` 的二次错误限定隔离；跟踪拒绝连接发生于实际 Disconnect 前，同步 Disconnected 回调能清掉旧跟踪，不能在回调后把旧记录补回。真正业务处理、地图应用或存档异常仍走原错误路径。**真实 UI/原生运行未验收。**
- [x] **B30-4 · 本地生产 partial 联编已完成：** 新增 `tools/MapReplayRefusalChecks/`，直接编译 `MapViewLoading.cs`、`OptionalPeerNotice.cs`、`RejectedPeerEvictionGate.cs`；真实 `TickMapLoading` 执行延迟 FIFO、拒绝、错误回送、隔离、有效玩家继续回放及超时/背压逻辑。其 `HandleHostMessage/ClientMessage`、原生 bridge 和传输依然使用受控桩；**不是完整业务生产方法联编或用户实机**。
- [ ] **B30-5 · Owner/Git/版本：** 旧 `MapViewLoading`、`MultiplayerSession`、`Networking` 都可能混有别的 Agent 的未提交行；按精确 hunk 暂存，原 Owner 检查版本/CHANGELOG 占用。主控在 R13 干净源码闭包后安排三端 `BuildOnly`，用户再做游戏联机验收。

## 5. 验收与故障注入（S1 局部生产方法已执行；S2～S4 待完成）

| 用例 | 条件 / 注入点 | 必须断言 |
| --- | --- | --- |
| C30-01 | 房主原生地图加载时缓存 A 的合法到达但稍后失效的 `visit.cancel`；队列回放时主动制造 `MessageRejectionException` | 旧请求无权限副作用、无 ACK、已消费；下一条 B 的合法消息继续 FIFO 重放 |
| C30-02 | 在 C30-01 的 `Send(A,"error")` 同步抛 `IOException`（TCP/Steam 队列满） | **不会因单条提示发送失败调用 `FailMapLoading/LockNativeFault`**，健康玩家地图与通讯继续推进 |
| C30-03 | 同一拒绝提示触发 `InvalidOperationException`（旧 peer 或已关闭队列） | 不重发已拒绝业务、不会误清 B/C 的事务；旧 peer 没有复活 |
| C30-04 | 错误通知失败，`Disconnect` 首发和后续数次也失败 | 只有 A 被隔离、停止处理其新指令，按真实断链事件收口；隔离状态不永久占用其它玩家 |
| C30-05 | `Send(A,"error")` 正常入队但客户端尚未收到 | 保持原先请求已拒绝的事实、不会伪造客户已知晓或 `Saved`；后续可靠数据仍按协议排队 |
| C30-06 | 延迟回放真正的地图 / 个人事务发生原生桥接失败、修订覆盖不足或存档错误 | 必须仍触发原有地图/原生 fail-closed，不能被“诊断失败保护”误吞 |
| C30-07 | 128 条/32 MiB 回放背压、60 秒到期、客户端 `IsHost=false` 的拒绝路径 | 容量与超时维持、不会丢弃未验证内容或制造双模拟者；客户端不尝试冒充房主错误回送 |
| C30-08 | A 断开后复用相同临时 peer ID、B/C 正常加入或重连，包含同步 Disconnect 回调 | 原始 `queued.Player` 身份校验和隔离记录生命周期不混淆，房主不会对新玩家应用旧指令 |
| C30-09 | 用户在隔离测试世界中用 TCP / Steam 两至三端：A 在地图初始化期间提交后续失效的访问/指令，模拟满队列 | 房主日志能区分“合法拒绝”和“地图故障”；B/C 能继续 Tick / 存档，双方 `MapRevision/PersonalRevision`、资产与世界一致 |

**证据层级：** S0 只读调用链（完成）；S1 真实生产地图回放 partial + 假传输/假业务处理故障注入（已做局部覆盖）；S2 本地 TCP/Steam 传输级验证（未做此用例）；S3 从原 Owner 已签收、无未跟踪生产输入的干净 Git 检出构建 Project3/Steam/TapTap（未做）；S4 用户真实游戏双端/多人验收（未做）。**不能拿 S1 的受控桩测试声称完整联机通过。**

### 2026-10-19 · BUG-30 局部代码及测试执行回执

**代码提交：** `32534032be9e986ff44193a607892f8b9331fde2`（`fix: isolate deferred map replay refusal notice failures`）。

- **`src/Session/MapViewLoading.cs`：** 只对 `TickMapLoading` 的拒绝提示 `Error` 和 `Send(peer,"error")` 做异常隔离，复用 `OptionalPeerNotice.SendOrDisconnect` 和 `RejectedPeerEvictionGate`；拒绝目标仍须与队列原有 `Peer/Player` 映射一致。仅在需要断开异常 peer 时先 `Track` 后 `Disconnect`，确保同步终止回调不产生遗留隔离记录；非 IO 的可选错误提示故障也只隔离仍绑定的异常 peer。
- **另一处同文件入口：** `HandleHostMessage` 重放之前新增 `rejectedPeerEvictions.Contains` 检查。错误提示无法送达后，A 在同一个加载队列里**尚未应用/确认的后续请求**被拒绝继续执行，但 B/C 已排队的合法事务仍保持 FIFO。没有改动 `CoalesceLoadingViews`、`AcceptMapUpdate`、地图权威版本水位或事务保存语义。
- **测试工程：** 新 `tools/MapReplayRefusalChecks/` 链接真正的生产 `MapViewLoading.cs` / `OptionalPeerNotice.cs` / `RejectedPeerEvictionGate.cs`，外围游戏业务处理、原生桥接和传输由可控桩承接；没有复制 `TickMapLoading` 逻辑。两处源码 hunk 从其他 Agent 的 `MapResyncRetryGate` 工作区脏差异中**精确选择入 Git**。

| 已实际运行 | 结果 | 严格范围 |
| --- | --- | --- |
| `dotnet run -c Release --project tools/MapReplayRefusalChecks/MapReplayRefusalChecks.csproj` | **60,186 项通过**；含 IO / InvalidOperationException / 未知可选通知异常、延迟多玩家 FIFO、已隔离 peer 旧排队包阻断、同步 Disconnect/peerId 复用、错误诊断二次失败、60 秒等待超时、128 条消息容量、1.2 万轮随机组合 | 生产 `TickMapLoading` + 现有隔离 helper；`HandleHostMessage` 内部业务授权及真实 Unity bridge 是桩 |
| 从 `3253403` 的 `git archive HEAD` 导出三份生产源码和新工程至忽略目录 `artifacts/bug30-clean-head/` 并单独构建运行 | **同样 60,186 项通过** | 证明本修复代码/测试不需要其它 Agent 未跟踪 `MapResyncRetryGate`；**不是完整 MOD clean Git 闭包或三端构建** |
| `tools/RejectedPeerEvictionChecks/RejectedPeerEvictionChecks.csproj` | **180,035 项通过** | 既有拒绝连接隔离 production helper 关联测试 |
| `tools/OptimizationChecks/OptimizationChecks.csproj` | **185 项通过** | 原地图优化及其它逻辑检查 |
| `tools/MapCacheNegotiationChecks/MapCacheNegotiationChecks.csproj` | **60,058 项通过** | 地图缓存协商生产 partial |
| `tools/CommunicationTickChecks/CommunicationTickChecks.csproj` | **94 项通过** | 通讯/参观等待实际生产 partial；工作树含他人未提交赠送授权测试 |
| `tools/ReliableCommitFanoutChecks/ReliableCommitFanoutChecks.csproj` | **30,027 项通过** | 原权威提交旁观者 fanout 关联测试，非真实多客户端 ACK |

**C30 完成范围：** C30-01～05 的通用回放、拒绝、健康者 FIFO 和可选通知本机合成路径通过；C30-06 本机模拟 `InvalidDataException` 触发真实 `FailMapLoading`，但 **真正地图/个人/存档原生提交失败未注入**；C30-07 的 128 条队列/60 秒与客端不回送已测，真实 32 MiB 和 Steam/TCP 背压未测；C30-08 同步断链/不同玩家复用 peerId 已测，真实 peerId 同一玩家重新绑定与多线程回调时序仍待验证；C30-09 完全未做。所有实际命令均在主仓库的 .NET 工具或忽略目录执行，**没有启动游戏或写三端安装副本**。测试桩的未赋值字段有 `CS0649/CS0414` 警告，退出码均为 0。

**上线仍需：** Session 地图加载/拒绝连接原 Owner 签收新两处 hunk；补真正 `HandleHostMessage` 合法租约到期的生产端到端注入，验证真正 Map/Personal/Native Save 错误仍 fail-closed；R13 的当前脏源码/版本日志统一签收并干净构建三端，之后由用户实际运行 C30-09。当前不能把 BUG-30 移动到已完成。

## 6. 完成与归档门槛

- [ ] 原 Session/Transport Owner 逐文件或逐 hunk 签收、确认复用 BUG-22/28 的边界。
- [ ] C30-01～08 的生产接线故障注入通过，有拒绝前后状态、隔离记录、健康玩家顺序与真正原生失败证据。
- [ ] 干净 Git 同源三端 `BuildOnly`、版本/CHANGELOG、Build-Info 交接和发布阻塞已解决。
- [ ] 用户 C30-09 的 TCP/Steam 实机地图、断线重连与保存测试通过，日志可复核、未丢失权威事务。

**历史首次建卡范围：** 初次仅静态审查并创建文档，没有动功能代码。**本次**已提交 `MapViewLoading` 拒绝提示相关两处小范围生产代码 hunk 与新的 production partial 故障注入项目；其他 Session/Transport/Game/Core、版本、协议、存档及游戏本体、三端运行副本均未改，未安装、未跑游戏、不 push。本 MD 保持在 `agent-tasks/待办/`。
