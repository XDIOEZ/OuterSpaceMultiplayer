# BUG-30 / P1 · 地图延迟重放的拒绝提示发送失败可能误锁房间

> **状态：待办 / S0 只读源码审查已完成；未修复、未做故障注入或实机验收**
>
> **发现：** 2026-10-19；`codex/multiplayer-v1`；静态审查 HEAD `5442553`。
>
> **建议 Owner：** Session 地图加载/消息重放原 Owner 主导；Session 拒绝消息隔离（BUG-22）Owner 复核复用方式，Transport Owner 验证 TCP/Steam 出站拒绝语义；原生/保存和三端发布由主控协调。**本文件不代表已经接管任何源码或解除冻结。**
>
> **重要限定：** 下面是从真实调用顺序得出的条件性故障路径，**没有证明玩家实机已出现整房停顿**。不能把这个风险混同为业务提交已损坏，也不能用忽略 map/ACK/存档错误来“修复”。

## 1. 具体风险与触发链

地图原生初始化期间，房主会把需要延迟应用的可靠消息保存在有序 `loadingMessages` 队列中。等原生地图就绪后，`TickMapLoading` 按原顺序调用 `HandleHostMessage`，某些**未获授权或已经过期的指令**可以合法地产生 `MessageRejectionException`。现有代码的拒绝分支会发送一次非权威 `error` 提示给该 peer。

但是 `Send(queued.Peer, "error", rejected.Message)` 未隔离 TCP/Steam 队列已满、断链/旧 peer 等异常。如果这个**辅助提示**也发送失败，会穿过 `catch (MessageRejectionException)`，被更外层的 `catch (Exception)` 当作 **地图加载失败** 处理，最终执行 `FailMapLoading`、`mapLoadingFailed = true`、`LockNativeFault`。因此**已被安全拒绝的一条过期消息**，理论上可能让健康玩家仍需依赖的地图回放和房主后续推进进入不可恢复锁定状态。

### 已核实的生产调用链

| 环节 | 文件 / 方法 | 可核实行为与条件 |
| --- | --- | --- |
| 入站推迟 | `src/Session/Networking.cs::HandleTransportCore → DeferMapLoadingMessage` | 已认证消息在地图障碍存在时进入 `loadingMessages`；非豁免类如 `visit.cancel / visit.decision` 可能等待本次地图初始化结束 |
| 状态可能过期 | `src/Session/PlayerVisits.cs::HostVisitCancel` 等 | 参观申请已撤销/不存在、该请求已不属于当前玩家等情况下抛 `MessageRejectionException`；时间流逝与正常断线清理可改变旧申请有效性 |
| 延迟执行 | `src/Session/MapViewLoading.cs::TickMapLoading` | 队首先删除、扣回 `loadingWireBytes`，设置 `replayingLoadingMessage = true`，房主将当初的 peer/player 映射核对后再调用 `HandleHostMessage` |
| 合法拒绝 | 同文件 `catch (MessageRejectionException rejected)` | 注释明确：“旧租约/指令可能在原生初始化完成前过期，尚未应用，也不应使地图加载失败” |
| **未隔离的通知** | 同一 `catch` | `Error("等待地图载入的消息已失效...")` 之后，若房主且 `peers.ContainsKey`，直接 `Send(peer, "error", rejected.Message)`，没有单独捕获可恢复发送异常 |
| 二次异常进入故障 | 同方法外层 `catch (Exception error)` | `Send` 的 `IOException / InvalidOperationException` 也会进入 `FailMapLoading(error)`，并非只处理原生地图初始化失败 |
| 房间后果 | `FailMapLoading` | 设置 `mapLoadingFailed`，调用故障报告及 `LockNativeFault`；后续正常地图重放/世界推进按原 fail-closed 规则受限 |
| 传输证据 | `src/Transport/TcpTransport.cs::Send`、`SteamTransport.cs::Send` | 满队列/连接关闭会同步拒绝；BUG-29 已调整 TCP 在途字节预算，使超限更早报告 `IOException`，但不应让正常拒绝回报变成地图事务错误 |

**示例（尚未注入）：** A 的 `visit.cancel` 在地图加载障碍中排队；该申请在回放时已失效，房主按业务规则拒绝；与此同时 A 的 TCP 或 Steam 发送队列拥塞，连“申请已失效”的 `error` 都无法入队。业务未被应用却触发地图故障，房主对正常 B/C 的地图、时间或保存可能受到影响。这个风险是否在全部实际状态机条件下可达，应通过生产 partial 故障注入证实。

## 2. 不与现有 BUG 重复

- [BUG-22 · 拒绝后的二次错误回送](BUG-22-二次错误回送.md) 已在 **`MultiplayerSession.Tick` 的主网络收包 `catch`** 接入 `OptionalPeerNotice` 与 `RejectedPeerEvictionGate`，防止“拒绝提示和 Disconnect 又失败”连坐整房。**本卡是另一条入口：`TickMapLoading` 的延迟重放自己的 `catch`，没有复用相同保护。**实现上应尽量共用既有生产策略，不创造第二套独立拒绝机制。
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
- [ ] **B30-2 · 复用隔离策略：** 优先把延迟重放的 `error` 通知接到经过测试的 `OptionalPeerNotice.SendOrDisconnect` / `QuarantineRejectedPeer`（或者原 Owner 明确验证的共享辅助入口）。已成功入队提示时不提前断链；已拒绝的消息不能变成业务成功回执；即使 `Disconnect` 也抛错，房主照样处理后续合法队列。
- [ ] **B30-3 · 日志与重入：** 不让日志输出、`StatusChanged` 或断链回调的二次异常冒充地图应用失败；明确与 BUG-28 的“UI 可选事件隔离”关系，避免给所有原生/存档事件加无条件 `catch`。
- [ ] **B30-4 · 链接生产源码测试：** 尽量扩展现有 `tools/RejectedPeerEvictionChecks/` 或建 `tools/MapReplayRefusalChecks/`，直接编译生产 `MapViewLoading` / `OptionalPeerNotice` / `RejectedPeerEvictionGate`，而不复制拒绝逻辑、用固定“通过”伪桩代替。
- [ ] **B30-5 · Owner/Git/版本：** 旧 `MapViewLoading`、`MultiplayerSession`、`Networking` 都可能混有别的 Agent 的未提交行；按精确 hunk 暂存，原 Owner 检查版本/CHANGELOG 占用。主控在 R13 干净源码闭包后安排三端 `BuildOnly`，用户再做游戏联机验收。

## 5. 验收与故障注入（本轮均未执行）

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

**证据层级：** S0 只读调用链（本轮完成）；S1 生产方法+假传输故障注入；S2 本地 TCP/Steam 传输级验证；S3 从原 Owner 已签收、无未跟踪生产输入的干净 Git 检出构建 Project3/Steam/TapTap；S4 用户真实游戏双端/多人验收。**不能拿 S0 结论或单独 .NET helper 检查声称完整联机通过。**

## 6. 完成与归档门槛

- [ ] 原 Session/Transport Owner 逐文件或逐 hunk 签收、确认复用 BUG-22/28 的边界。
- [ ] C30-01～08 的生产接线故障注入通过，有拒绝前后状态、隔离记录、健康玩家顺序与真正原生失败证据。
- [ ] 干净 Git 同源三端 `BuildOnly`、版本/CHANGELOG、Build-Info 交接和发布阻塞已解决。
- [ ] 用户 C30-09 的 TCP/Steam 实机地图、断线重连与保存测试通过，日志可复核、未丢失权威事务。

**本轮仅审查并创建文档**：没有改 `MapViewLoading`、`MultiplayerSession`、Transport、Game、Core、协议、存档、版本，也没有编译、构建安装、启动游戏、替换运行副本或推送。全部源码/测试修改均交原 Owner 签收后独立进行；本 MD 保持在 `agent-tasks/待办/`。
