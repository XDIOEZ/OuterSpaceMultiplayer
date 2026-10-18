# BUG-29 / P1 · TCP 发送中帧未计入每连接发送内存预算

> **状态：待办 / 已完成只读生产源码链审查；未修复、未执行故障注入、未做游戏实测**
>
> **发现日期：** 2026-10-19；只读基线：`codex/multiplayer-v1` / `3272c427a53ad890db7d14504f6cccc053c1cd00`。
>
> **建议 Owner：** Transport / TCP 原 Owner；Session 地图和大消息发送 Owner 只读复核投递失败语义；R13 主控协调混合源码签收与发布。**本卡不是源码接管授权。**
>
> **优先级解释：** P1 / 连接背压与多人大地图时的内存稳定性风险。源码能证明计数口径不一致，但尚未取得持续慢网、多客户端峰值分配或进程 OOM 的实测证据。

## 一、影响与触发边界

联机方案 [V1](../../联机方案%20V1.md) 允许玩家进入他人的大地图、获取权威检查点，并希望支持 3～4 人以上共同游戏。同一房主可能向多个 TCP 客端同时发送体积较大的地图视图。当前 TCP 的“排队字节限额”**没有涵盖已从等待队列移出、正在 NetworkStream 写入的那一帧**。

正常高速网络可能很快完成写入；当客户端长期不读取、网络很慢或发生 TCP 背压时，正在发送的大消息会持续占有托管 `byte[]`，随后业务层继续允许较多新消息进入排队。可能导致单个连接的实际发送侧托管消息缓冲超出原本的 256 MiB 队列预算，多个拥塞客户端叠加后增加 GC / OOM 与房主卡顿风险。

**限定：** 这是**有界预算口径不一致**，不是证明发生“无限内存泄漏”。旧代码仍有每帧 128 MiB 和排队字节/512 个排队项上限；真正进程峰值还受到原始编码数据、对象生命周期、网络/原生分配等因素影响，不应把下面的数字直接当作实测内存峰值。

## 二、实际生产调用链与源码证据

| 来源 | 可核实事实 | 对本卡的意义 |
| --- | --- | --- |
| `src/Transport/TransportQueue.cs::MaxMessageBytes` | 单个消息最多 `128 * 1024 * 1024` 字节 | 单个合法地图帧可以接近 128 MiB |
| `src/Transport/TransportQueue.cs::MaxQueuedBytes` | 接收/发送相关常量为 `256L * 1024 * 1024` 字节 | TCP `Send` 目前把该数作为排队缓存预算 |
| `src/Transport/TcpTransport.cs::Peer` | 同时存储 `Queue<byte[]> Outgoing`、`long OutgoingBytes` 和 `int InFlightBytes`，写线程与入队通过 `peer.Gate` 同步 | 已有在途帧大小可供入队时核算；不需要新网络协议 |
| `TcpTransport.WriteLoop` | 在 `peer.Gate` 下 `payload = peer.Outgoing.Dequeue(); peer.OutgoingBytes -= payload.Length; peer.InFlightBytes = payload.Length`，之后退出锁并分别 `Stream.Write(header)`、`Stream.Write(payload)` | **该消息在整个阻塞写入期间仍有字节数组引用，但已经从 OutgoingBytes 扣除** |
| `TcpTransport.Send` | 在 `peer.Gate` 下检查 `peer.Outgoing.Count >= 512` 或 `peer.OutgoingBytes + data.Length > TransportQueue.MaxQueuedBytes`，之后 `Outgoing.Enqueue((byte[])data.Clone())` 并增加 `OutgoingBytes` | 容量公式**没有** `InFlightBytes`；当写线程取走一帧后，新 Send 可重复使用其原先额度 |
| `TcpTransport.IsDrained` | 显式同时检查 `Outgoing.Count != 0 || InFlightBytes != 0` | 该模块自身已经承认“排队为空”和“真正写完”是两种状态 |
| `TcpTransport.ClosePeer` | 清除 `Outgoing`、将两类字节计数置零、关闭连接 | 后续修复不能引入发送中/关闭中的负计数、死锁或已关闭 peer 复活 |
| `src/Transport/SteamTransport.cs::Send/Flush` | `SteamTransport.Flush` 只有当整条 `SendItem` 完全发出后才从 `Outgoing` 出队并减 `OutgoingBytes` | Steam 保留片段项目的内存预算口径不同；本卡**不要盲目照搬 TCP 补丁到 Steam** |

### 静态容量推演（不是实验测量）

假设 TCP 写线程已经从 `Outgoing` 移出一条合法的 **128 MiB** 消息，并因对端慢速读取而仍在 `Stream.Write` 内：

1. `InFlightBytes ≈ 128 MiB`、`OutgoingBytes = 0`，实际仍持有发送中消息数组。
2. 按现有 `Send` 公式，新的发送可以继续将排队字节推到 **256 MiB**。
3. 因此**单个 peer 的“发送中 + 等待发送”消息数据理论上可达到约 384 MiB**（128 + 256），比目前仅对 `OutgoingBytes` 强制执行的 256 MiB 预算多出最多一条合法消息。
4. 2～4 个同时拥塞的 peer 会重复这一计数缺口；仍不等于证明实际进程常驻内存增加了固定数量，因为调用者编码缓冲和 GC 分配额外影响总量。

目标是基于 `OutgoingBytes + InFlightBytes + encodedDataLength` 作一致的**原子入队判断**，同时确认该限制确实是面向“仍由发送端持有的消息缓冲”，而非只限制等待队列长度。由 Transport Owner 决定最终语义并留存测试证据。

## 三、范围及既有 BUG 的区分

- [BUG-19](BUG-19-双端真实握手与地图回归.md) 限制**认证前入站帧**大小，本卡是**已建立连接的出站 TCP 写缓冲核算**，不改认证大小限额。
- [BUG-27](BUG-27-地图缓存协商发送失败与重试收口.md) 处理 Session 的 `map.cache-query/offer/retry` **入队失败后的恢复**；本卡约束传输层允许入队的内存预算，修复后可能更早返回 `IOException`，必须让 Session Owner 校验 BUG-27 的退避及其它地图可靠发送不能被误判为成功。
- [BUG-25](BUG-25-银河全量重传滥用.md) 已针对 `galaxyReset` 的重传需求做合并与退避，本卡不更改银河同步业务逻辑。
- [BUG-08](BUG-08-广播失败阻断ACK.md) 要求合法提交 ACK 不被拥塞旁观者阻断，本卡修改队列边界后必须继续验证房主对其它健康客端的可靠发送。
- [R13-01](R13-01-源码Owner签收.md) 与 [R13-01B](R13-01B-20261019-构建源码依赖闭包与Owner签收.md) 的原 Owner / 干净 Git 闭包仍是前置门禁。本卡没有权限取消历史冻结或把其它 Agent 的未提交内容 stage 进自己的提交。

## 四、建议实现方案（待原 Owner 接手）

- [ ] **T29-1 / 审计入队合同：** 原 Transport Owner 确认 256 MiB 是 `OutgoingBytes` 的纯排队额度，还是希望限制 `OutgoingBytes + InFlightBytes` 的发送侧在途缓冲。记录现状、预期和上层 `IOException` 语义，特别是 BUG-27 和最终检查点的调用方。
- [ ] **T29-2 / 最小精确修复：** 如果确认为发送缓冲总上限，直接在 `peer.Gate` 已有原子临界区检查 `InFlightBytes + OutgoingBytes + data.Length`。遵守 `long` 算术，避免溢出；在`排队前`拒绝，不生成数组副本、不篡改队列顺序、不假装成功。
- [ ] **T29-3 / 生命周期：** 复核写线程“出队 → 正在写 → 完成/失败/断开”的每一个 `InFlightBytes` 清理点；不能在写未结束时提前认为 `IsDrained`，不能在 `ClosePeer` 与写线程竞争时产生计数负值、死锁或错误复用旧 peer。
- [ ] **T29-4 / 诊断与可观测性：** 局部测试分别记录排队字节、在途字节、总字节、拒绝次数、其它健康 peer 的吞吐；不能输出地图 JSON、身份令牌、聊天正文或私有进度。若有生产指标改动，默认关闭或遵守现有采样窗口。
- [ ] **T29-5 / 兼容性：** 保持当前 TCP 4 字节长度前缀、消息最大 128 MiB、每 peer 512 个等待项和 FIFO 发送行为不变；Steam 通道仍独立计数，不能额外要求 Steam 初始化才能使用 IP 直连。
- [ ] **T29-6 / Git 与发布：** 原 Owner 提交独立窄补丁（必要时新建可测试的生产辅助方法/工具），避免整体暂存 `TcpTransport.cs` / `SteamTransport.cs` 现有历史差异；完整 BUG 修复按项目版本与 CHANGELOG 规则由主控统一收口，三端构建/运行验证分开。

## 五、可执行验收矩阵（当前均未执行）

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

## 六、任务签收与关闭标准

**建议修改边界：** `src/Transport/TcpTransport.cs` 的发送计数 / `Send` 判断 / 写线程生命周期；必要时新增 `tools/TcpOutgoingBudgetChecks/`。`TransportQueue.cs` 仅引用已有常量，`SteamTransport.cs`、`RoomTransport.cs` 和各 Session/ Game 源码先只读，确需跨目录变动则由对应 Owner 单独签收。

关闭前必须同时满足：原 Owner 及混合 hunk 签收、C29-01～08 适用的真实生产源码/localhost 故障注入、S3 干净源码三端构建与 `BUILD-INFO` 证据、用户 C29-09 的双端实测。若实现了队列判断但还缺用户游戏回归，只能记录子提交，**本 MD 继续放在 `agent-tasks/待办/`**。

**本轮操作范围：** 只读检查 `TcpTransport`、`SteamTransport`、`TransportQueue`、`RoomTransport` 和已有 BUG / V1；仅新建本任务文档、更新索引和 Agent 协作信息。**没有修改功能源码、协议、存档、VERSION / CHANGELOG、游戏本体或三端 MOD 安装副本；没有构建、执行测试、启动游戏或推送。**
