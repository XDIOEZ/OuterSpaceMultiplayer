# BUG-29 / P1 · TCP 发送中帧未计入每连接发送内存预算

> **状态：局部生产修复及本机 TCP/生产源码回归已提交；完整 Owner 签收、R13 Git 闭包、三端构建及用户真实多人验收仍待办**
>
> **发现日期：** 2026-10-19；只读基线：`codex/multiplayer-v1` / `3272c427a53ad890db7d14504f6cccc053c1cd00`。
>
> **建议 Owner：** Transport / TCP 原 Owner；Session 地图和大消息发送 Owner 只读复核投递失败语义；R13 主控协调混合源码签收与发布。**本卡不是源码接管授权。**
>
> **优先级解释：** P1 / 连接背压与多人大地图时的内存稳定性风险。源码能证明计数口径不一致，但尚未取得持续慢网、多客户端峰值分配或进程 OOM 的实测证据。

## 一、影响与触发边界

联机方案 [V1](../../docs/plans/联机方案%20V1.md) 允许玩家进入他人的大地图、获取权威检查点，并希望支持 3～4 人以上共同游戏。同一房主可能向多个 TCP 客端同时发送体积较大的地图视图。**首次建卡时** TCP 的“排队字节限额”没有涵盖已从等待队列移出、正在 NetworkStream 写入的那一帧；本轮已提交下面“七、本轮实现回执”所述的发送入队限额修复。

正常高速网络可能很快完成写入；当客户端长期不读取、网络很慢或发生 TCP 背压时，正在发送的大消息会持续占有托管 `byte[]`，随后业务层继续允许较多新消息进入排队。可能导致单个连接的实际发送侧托管消息缓冲超出原本的 256 MiB 队列预算，多个拥塞客户端叠加后增加 GC / OOM 与房主卡顿风险。

**限定：** 这是**有界预算口径不一致**，不是证明发生“无限内存泄漏”。旧代码仍有每帧 128 MiB 和排队字节/512 个排队项上限；真正进程峰值还受到原始编码数据、对象生命周期、网络/原生分配等因素影响，不应把下面的数字直接当作实测内存峰值。

## 二、实际生产调用链与源码证据

| 来源 | 可核实事实 | 对本卡的意义 |
| --- | --- | --- |
| `src/Transport/TransportQueue.cs::MaxMessageBytes` | 单个消息最多 `128 * 1024 * 1024` 字节 | 单个合法地图帧可以接近 128 MiB |
| `src/Transport/TransportQueue.cs::MaxQueuedBytes` | 接收/发送相关常量为 `256L * 1024 * 1024` 字节 | TCP `Send` 目前把该数作为排队缓存预算 |
| `src/Transport/TcpTransport.cs::Peer` | 同时存储 `Queue<byte[]> Outgoing`、`long OutgoingBytes` 和 `int InFlightBytes`，写线程与入队通过 `peer.Gate` 同步 | 已有在途帧大小可供入队时核算；不需要新网络协议 |
| `TcpTransport.WriteLoop` | 在 `peer.Gate` 下 `payload = peer.Outgoing.Dequeue(); peer.OutgoingBytes -= payload.Length; peer.InFlightBytes = payload.Length`，之后退出锁并分别 `Stream.Write(header)`、`Stream.Write(payload)` | **该消息在整个阻塞写入期间仍有字节数组引用，但已经从 OutgoingBytes 扣除** |
| `TcpTransport.Send`（首次建卡时） | 在 `peer.Gate` 下检查 `peer.Outgoing.Count >= 512` 或 `peer.OutgoingBytes + data.Length > TransportQueue.MaxQueuedBytes`，之后 `Outgoing.Enqueue((byte[])data.Clone())` 并增加 `OutgoingBytes` | **旧容量公式没有 `InFlightBytes`；该处已在 `cf39a82` 中进行最小修复** |
| `TcpTransport.IsDrained` | 显式同时检查 `Outgoing.Count != 0 || InFlightBytes != 0` | 该模块自身已经承认“排队为空”和“真正写完”是两种状态 |
| `TcpTransport.ClosePeer` | 清除 `Outgoing`、将两类字节计数置零、关闭连接 | 后续修复不能引入发送中/关闭中的负计数、死锁或已关闭 peer 复活 |
| `src/Transport/SteamTransport.cs::Send/Flush` | `SteamTransport.Flush` 只有当整条 `SendItem` 完全发出后才从 `Outgoing` 出队并减 `OutgoingBytes` | Steam 保留片段项目的内存预算口径不同；本卡**不要盲目照搬 TCP 补丁到 Steam** |

### 原始版本静态容量推演（不是实验测量，也不是修复后状态）

假设 TCP 写线程已经从 `Outgoing` 移出一条合法的 **128 MiB** 消息，并因对端慢速读取而仍在 `Stream.Write` 内：

1. `InFlightBytes ≈ 128 MiB`、`OutgoingBytes = 0`，实际仍持有发送中消息数组。
2. 按现有 `Send` 公式，新的发送可以继续将排队字节推到 **256 MiB**。
3. 因此**单个 peer 的“发送中 + 等待发送”消息数据理论上可达到约 384 MiB**（128 + 256），比目前仅对 `OutgoingBytes` 强制执行的 256 MiB 预算多出最多一条合法消息。
4. 2～4 个同时拥塞的 peer 会重复这一计数缺口；仍不等于证明实际进程常驻内存增加了固定数量，因为调用者编码缓冲和 GC 分配额外影响总量。

本轮已按保守的“发送中 + 等待中总计 256 MiB”口径完成原子入队判断；**是否作为正式 Transport 合同**仍需原 Owner 签收。新版本不再允许上述 384 MiB 的同一 peer 排队加在途情况，但本地测试没有测量 Unity 进程实际堆峰值。

## 三、范围及既有 BUG 的区分

- [BUG-19](BUG-19-双端真实握手与地图回归.md) 限制**认证前入站帧**大小，本卡是**已建立连接的出站 TCP 写缓冲核算**，不改认证大小限额。
- [BUG-27](BUG-27-地图缓存协商发送失败与重试收口.md) 处理 Session 的 `map.cache-query/offer/retry` **入队失败后的恢复**；本卡约束传输层允许入队的内存预算，修复后可能更早返回 `IOException`，必须让 Session Owner 校验 BUG-27 的退避及其它地图可靠发送不能被误判为成功。
- [BUG-25](BUG-25-银河全量重传滥用.md) 已针对 `galaxyReset` 的重传需求做合并与退避，本卡不更改银河同步业务逻辑。
- [BUG-08](BUG-08-广播失败阻断ACK.md) 要求合法提交 ACK 不被拥塞旁观者阻断，本卡修改队列边界后必须继续验证房主对其它健康客端的可靠发送。
- [R13-01](R13-01-源码Owner签收.md) 与 [R13-01B](R13-01B-20261019-构建源码依赖闭包与Owner签收.md) 的原 Owner / 干净 Git 闭包仍是前置门禁。本卡没有权限取消历史冻结或把其它 Agent 的未提交内容 stage 进自己的提交。

## 四、建议实现方案（待原 Owner 接手）

- [ ] **T29-1 / 审计入队合同：** 原 Transport Owner 确认 256 MiB 是 `OutgoingBytes` 的纯排队额度，还是希望限制 `OutgoingBytes + InFlightBytes` 的发送侧在途缓冲。记录现状、预期和上层 `IOException` 语义，特别是 BUG-27 和最终检查点的调用方。
- [x] **T29-2 / 最小精确修复：** 已在 `peer.Gate` 下通过 `OutgoingBytes > MaxQueuedBytes - (long)InFlightBytes - data.Length` 检测超限，同时对异常负计数 fail-closed；在克隆/入队前拒绝，保持原 FIFO、格式和异常类型。独立子提交 `cf39a82`。
- [ ] **T29-3 / 生命周期：** 已静态复核现有 `WriteLoop/ClosePeer/IsDrained` 且通过独立生产 Send 并发/本机 FIFO 基础检查；**真实阻塞写出时并发断线 / Dispose / BeginDrain 的长期竞态、负计数及内存释放仍待故障注入**，暂不打勾。
- [ ] **T29-4 / 诊断与可观测性：** 本地反射受控状态已核验排队/在途/总量、拒绝前后队列项、400 次并发送入和 3 个正常 peer 独立队列；**真实 GC/进程常驻内存/网络吞吐与有界生产指标尚未采集**，故保持未完成。
- [ ] **T29-5 / 兼容性：** 真实 TCP 回环已复核长度前缀、FIFO、非 Steam 独立连接，合成队列已复核 512 待发数上限和入队拒绝；但 **Steam/TCP 混合房间以及 128 MiB 实际慢读大帧** 仍未测，保持未完成。
- [ ] **T29-6 / Git 与发布：** 本轮独立源码/测试精确提交完成（仅 `TcpTransport.Send` 一处和新测试工程）；原 Owner 签收、`VERSION/CHANGELOG` 以及三端可复现构建/安装尚未完成，不能整项勾选。

## 五、可执行验收矩阵（局部生产源码检查已执行，真实游戏验收未执行）

| ID | 故障/压力场景 | 必须断言 |
| --- | --- | --- |
| C29-01 | 假 `NetworkStream` 或受控 localhost 让一条最大合法帧保持 `InFlightBytes > 0` | 写出期间内存核算包含该帧；不会将等待队列再填到超过约定的总上限 |
| C29-02 | 在途 128 MiB，待发 128 MiB；再尝试入队 1 字节 | 若总预算目标为 256 MiB，应在**克隆新数据之前**抛出明确队列满异常；绝不假装已经入队 |
| C29-03 | 在途帧实际写完后，发送容量重新释放 | 之前被背压拒绝的合法消息随后可重试；没有自动重复发送已经入队的同一消息 |
| C29-04 | 房主同时连接 3～4 个 peer：A 长时间不读取，B/C 正常读取 | A 的背压上限有效，B/C 的合法地图/ACK/心跳不被 A 的满队列连坐 |
| C29-05 | 满队列、`ClosePeer`、`Dispose`、`BeginDrain` 与发送线程交错 | 无锁顺序倒置、负计数、写完前假 `IsDrained`、已关闭 peer 意外重进或无界残留字节 |
| C29-06 | 单消息 128 MiB 上限、512 个等待项、空包、`null`、断开的 peer | 保留旧协议/参数拒绝规则，拒绝消息不进入队列、不产生重复副本 |
| C29-07 | TCP 原有 7 项 localhost 认证/收包回环、现有地图缓存协商退避及 Session 可靠 ACK 测试 | 新背压限额不会改变认证状态，业务层能够正确处理 `IOException`，未送达不能被报告成已保存 |
| C29-08 | Steam 发送 partially-flushed 的分片、房主 TCP+Steam 混合房间 | Steam 仍以自己的 `OutgoingBytes` 统计待发送完整对象；共享房间原定轮换、TCP 独立使用均保留 |
| C29-09 | 用户隔离测试世界：TCP 双人/3 人发送大地图、断线重连、房主保存与退出 | 记录真实内存峰值、GC、发送字节及双端世界 Revision/资产，无假回执、无二次地图应用、无丢档 |

**验证层级：** S0 静态调用链（本轮已完成） → S1 真实生产 `TcpTransport.Send / WriteLoop` 受控桩、容量和断链交错 → S2 TCP localhost 实际缓慢读取（测试机器/本地网络，不影响真实玩家） → S3 主控从干净 Git 检出构建 Project3 / Steam / TapTap 三个不同程序集目标 → S4 用户完成游戏内联网、日志与世界/个人资产验收。任何一个层级都不能用前一级替代。

**本次覆盖映射：** C29-01～03 已用真实 `TcpTransport.Send` + 受控内部计数验证；C29-04 测了 1 个拥塞、3 个健康 peer 的**单机 Send 隔离**（未真实 4 人）；C29-05 验证 400 次并发送入及基础 localhost Disconnect/Drained，**尚未覆盖阻塞 Stream.Write 与 Dispose 的全部交错**；C29-06 验证 512 项、空/null/未知/已关闭 peer，未用 128 MiB 物理数组边界压力；C29-07 的 TCP 已有 7 项回环、地图缓存退避和可靠 fanout 分别通过；C29-08 只读评估 Steam 实现、未运行 Steam；C29-09 完全未执行。

## 六、任务签收与关闭标准

**建议修改边界：** `src/Transport/TcpTransport.cs` 的发送计数 / `Send` 判断 / 写线程生命周期；必要时新增 `tools/TcpOutgoingBudgetChecks/`。`TransportQueue.cs` 仅引用已有常量，`SteamTransport.cs`、`RoomTransport.cs` 和各 Session/ Game 源码先只读，确需跨目录变动则由对应 Owner 单独签收。

关闭前必须同时满足：原 Owner 及混合 hunk 签收、C29-01～08 适用的真实生产源码/localhost 故障注入、S3 干净源码三端构建与 `BUILD-INFO` 证据、用户 C29-09 的双端实测。若实现了队列判断但还缺用户游戏回归，只能记录子提交，**本 MD 继续放在 `agent-tasks/待办/`**。

## 七、2026-10-19 局部生产代码及测试交付回执

**Git 代码提交：** `cf39a823cf104a7b9df825b5882cd90ccfbac53d` — `fix: include TCP in-flight frames in outbound buffer budget`。

**实际改动范围：** 仅修改 `src/Transport/TcpTransport.cs::Send` 发送队列容量判断，在克隆和入队前、原 `peer.Gate` 临界区内判断 `OutgoingBytes` 与 `InFlightBytes` 之和。负计数保护、减法型 `long` 边界判断避免超大输入计算溢出；`IOException` 和其它既有 API/网络帧类型、协议版本均保持不变。旧文件另一 Agent 的 `TcpListener → Socket.Accept` 兼容改动**没有随本提交暂存**，并原封保留在工作区。新建 `tools/TcpOutgoingBudgetChecks/`，将**真正的生产 `TcpTransport.cs` / `TransportQueue.cs` / `UnauthenticatedFramePolicy.cs` 编译链接**进 .NET8 回归，不复制判定算法。

| 实际测试 / 命令 | 结果 | 不能据此断言 |
| --- | --- | --- |
| `dotnet run -c Release --project tools/TcpOutgoingBudgetChecks/TcpOutgoingBudgetChecks.csproj` | **68,778 项通过**：真实生产 Send 在 128 MiB 在途 / 128 MiB 排队 / +1 字节、精确剩余额度、计数异常与 `long.MaxValue`、512 条上限、同源数据克隆/FIFO、3 个健康 peer、400 次并发、25,000 轮随机预算测试；另用本机 `TcpClient` 延迟接收检查两条帧按顺序读完和 `IsDrained` | 尚未测生产 Unity 进程内存峰值、4 个真实玩家或 128 MiB 真实慢网阻塞写入 |
| 从本次提交的 `git archive HEAD src/Transport tools/TcpOutgoingBudgetChecks tools/UnauthenticatedTcpChecks` 导出到 `artifacts/bug29-clean-head/` 后独立运行新测试 | **68,778 项通过**；可验证当前代码提交不依赖其它 Agent 的未提交 TCP Socket 兼容 hunk | 不是整个 MOD 的 Git 0U/0M 或三端游戏 DLL 构建 |
| `dotnet run -c Release --project tools/UnauthenticatedTcpChecks/UnauthenticatedTcpChecks.csproj` 与同一 HEAD 隔离回归 | **各 7 项通过**；未认证帧限制和合法认证后消息原有功能保持通过 | 不是 Steam 联机/跨版本兼容性 |
| `dotnet run -c Release --project tools/MapCacheNegotiationChecks/MapCacheNegotiationChecks.csproj` | **60,058 项通过** | 地图成功入队不等于权威状态已应用、持久保存 |
| `dotnet run -c Release --project tools/ReliableCommitFanoutChecks/ReliableCommitFanoutChecks.csproj` | **30,027 项通过** | 不是实际拥塞旁观者、游戏广播 ACK 或跨机实测 |

**测试实现限定：** 128 MiB/256 MiB 边界由测试反射设置的生产 TCP peer 计数构成，不会为模拟每次内存条件而实际分配几百 MiB；真实 localhost 测试只写 5 B 与 512 KiB 两帧、将接收端读取延迟 50 ms，验证帧/有序写出。故此不是物理 384 MiB 内存压力测试，也没有测出 CPU、GC P95 或房主全局冻结概率。

**历史与当前状态：** 首次建卡提交 `eff09fb` 只进行了静态审查与计划，没有动代码；本次已交付独立子补丁及本地测试，但不视作整条 BUG-29 完全关闭。仍需 Transport 原 Owner 的容量合同签收、C29-05/08/09 的真实断线/Steam/多人游戏与慢网络证据、R13 源码闭包及主控统一整理被其它 Agent 占用的 `VERSION/CHANGELOG`。本轮**没有修改** Steam/Session/Game、协议、存档、游戏本体或三端 MOD 运行副本；未执行完整游戏构建、游戏运行或远程推送。
