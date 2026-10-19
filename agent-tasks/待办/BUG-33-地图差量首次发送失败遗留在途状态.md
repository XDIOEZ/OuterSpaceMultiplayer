# BUG-33 / P1 · 客端地图差量首次发送失败可能留下幽灵在途状态

> **状态：待办 / 只读源码静态审查（S0）完成，未修改功能源码、未执行故障注入或游戏实测。**
>
> **建卡：** 2026-10-19，`codex/multiplayer-v1`；首次检查 HEAD `4776799ca81210556194b87e1e3e10a4ea2707f5`。
>
> **建议 Owner：** Session/Authority 地图同步原 Owner；Transport Owner 验证出站是否实际入队；GameBridge Owner 审核捕获检查点；主控负责 R13 混合源码签收、构建和真实验收。
>
> **并行认领避让：** 协调表已存在 `bug32-personal-outbound-audit-fix-20261019`，另一 Agent 正认领**个人差量** `personalInFlight` 的失败恢复。本卡只管理**地图差量** `inFlightUpdates`，不接手对方 BUG-32 或修改其源码。个人与地图发布相互阻塞的交集仅作为集成依赖。

## 1. 源码证据与条件性风险

V1 要求**房主唯一权威**地记录地图 `CommitId/MapRevision`，客端原生地表状态只能在完整确认后推进自己的网络基线；离开/退出时的最终检查点不能把未得到主机回执的差量报告为已保存。

| 生产文件/方法 | 当前源码事实 | 风险与限制 |
| --- | --- | --- |
| `src/Session/Authority.cs::PublishLocalMapsCore` | 捕获 `bridge.CaptureMapUpdate` 后，填入 `CommitId`、`SessionId`、`MapId`、`AuthorityEpoch`、`BaseRevision/Revision`，然后在客户端路径执行 `inFlightUpdates[map.MapId] = update; Send("host", "map", WireMapUpdate(update))` | **先添加本地 in-flight，再进行序列化/发送**。若本次编码或传输在实际入队前抛错，没有针对这条新预留的明确回滚；房主没有对应消息，也无法生成该 `CommitId` 的 ACK |
| `src/Session/Authority.cs::PublishLocalMapsCore` | 下次遍历时，对 `inFlightUpdates.ContainsKey(map.MapId)` 的地块直接跳过 | 已被标记但实际上没有送出的地图可能持续不被再次捕获/提交；不能只靠网络恢复期待房主回包 |
| `src/Session/SlowOperations.cs::PublishLocalMaps` | `try { PublishLocalMapsCore(force); } finally { ReportSlowOperation(...); }` | 只保证耗时诊断，**没有撤销或重试已经登记的 in-flight** |
| `src/Session/MultiplayerSession.cs::Tick` | `maps.publish` 后执行 `personal.publish`；最终检查点根据 `inFlightUpdates.Count != 0 || personalInFlight.Count != 0` 决定能否提交 `finalDone`；外层 `catch` 设置 `Paused` 并提示失败 | 客端被暂停不会自动清理没有实际提交的在途记录；即使改为手动重试，旧记录也可能阻断安全退出 |
| `src/Session/Authority.cs::ReceiveMapAcknowledgement` | 真正地图 ACK 要匹配 `CommitId/SessionId/MapId/AuthorityEpoch/BaseRevision/Revision`、本地待提交基线与当前 MapRecord 版本，然后推进 Revision 并移除 in-flight | **不能为了恢复发送而随意清空 in-flight / 改写 CommitId**；成功入队但 ACK 晚到和完全未入队必须区分 |
| `src/Session/Networking.cs` | `mapRejected` 可移除目标地图 in-flight，`view` 成功恢复后也能清理；两者都需要房主真的收包或发回有效权威状态 | 本次消息如果确定没有进入发送队列，不能期待正常 `mapRejected/view` 自动到来 |
| `src/Transport/TcpTransport.cs` / `SteamTransport.cs` | 队列满、关闭连接等情况会明确同步拒绝 `Send`（如 `IOException`） | 这是实际可注入的失败源，不表示所有 `Send` 异常都保证“未入队”；编码异常、同步回调重入等必须分别核对 |

### 静态故障推演（未经实机复现）

1. 客端正在模拟地图 `M`，在 Revision 17 生成新 `CommitId=C`、Revision 18 的增量。
2. 客端先保存本地 `inFlightUpdates[M]=update`，尚未拿到房主确认。
3. `Send("host", "map", ...)` 在真正进入 TCP/Steam 发送队列前同步抛出“队列已满”或“连接已关闭”。目前没有该 `M/C` 对应的明确撤销/有界重发路径。
4. 后续正常 `PublishLocalMapsCore` 看到 `M` 已在途，会跳过。由于房主没看到 `C`，不会产生与之匹配的 ACK；最终检查点仍把本地标记视作未完成。

**这是一个条件性可靠性风险，不是已经测到的死锁、内存泄漏或玩家资产丢失。** `Tick` 外层可能因发送异常暂停业务，但“已暂停”与“已经安全撤销未送出的事务”是不同概念。

## 2. 与已有任务和并行 Owner 的边界

- **并行 BUG-32（个人差量）**：另一个 Agent 已在 `AGENT-COORDINATION.md` 登记 `bug32-personal-outbound-audit-fix-20261019`，计划处理 `personalInFlight`、个人 ACK/Reset 和 NativePersonalSync 比较基线。本卡不编辑对方目标文件或测试；两卡共同验证个人在途门禁不会让地图重试死等。
- [BUG-01 远程命令提交时序](BUG-01-远程地图提交时序.md)：涉及 `command/execution` 和地图权威提交的因果关系；本卡仅针对**正常地图差量的首次 Send**，不能把“传输队列成功入队”说成“房主 Save 了命令”。
- [BUG-08 广播阻断 ACK](BUG-08-广播失败阻断ACK.md)：是**房主已提交之后**的 ACK/旁观者扇出失败；本卡是**客端首次发往房主之前**的在途预留。
- [BUG-10 最终检查点](BUG-10-退出最终检查点.md)：会等待新地图在途，不负责辨别首次入队根本失败的幽灵记录。
- [BUG-24 地图重同步发送失败](BUG-24-地图重同步发送失败.md)、[BUG-27 地图缓存协商](BUG-27-地图缓存协商发送失败与重试收口.md) 使用独立的 `resync/map.cache-*` 控制消息，不是 `map` 业务差量。
- [BUG-29 TCP 出站内存预算](BUG-29-TCP发送中帧未计入队列内存预算.md) 可能更早触发同步队列拒绝，传输层不应为 Session 隐藏这个失败。
- [R13-01 原 Owner 签收](R13-01-源码Owner签收.md) 和 [R13-01B 当前源码闭包](R13-01B-20261019-构建源码依赖闭包与Owner签收.md) 继续阻塞；当前 `Authority.cs` 有大量其他 Agent 未提交 hunk，不得整文件暂存/覆盖。

## 3. 必须保持的安全及事务不变量

1. **入队确认和房主权威确认分离：** 已编码/已入队 ≠ 已由房主应用 ≠ 已保存。`World.Maps[M].Revision` 与 `LastCommitId` 只能按已验证 ACK 或有效完整基线推进。
2. **只对确定未入队的尝试回滚：** 如果传输证明 Send 在队列变动前同步拒绝，才能撤销对应预留；如果曾入队但 ACK 丢失，保留原 `CommitId` 与版本，用房主幂等对账/安全重试。
3. **只回滚自己的对象：** 发送前后可以同步重入收到 `map` ACK、`mapRejected`、`view`，或断线更换新的 map in-flight。只能按引用/尝试 ID 比较，不能误删重入产生的新任务。
4. **保留捕获源与原生错误：** `CaptureMapUpdate`、`WireMapUpdate`、原生检查点序列化若异常，不能被“可恢复传输故障”无条件吞掉；需评审是否可以复用原始编码与原始 `CommitId`。
5. **有界缓存与退避：** 如决定保留可靠重试帧，必须限制地图大小、待发总字节、重发频率，按会话/世界/地图任期过期清理，不在每帧重复捕获大地图。
6. **不压住其它事务：** 正常 B/C 客端更新和房主世界推进不应被单个坏发送者连坐；本客户端与 BUG-32 的 `personalInFlight` 一致地处理跨门禁依赖，退出时不能假报 `finalDone/Saved`。
7. **协议与身份兼容：** 不擅自变更当前 `Protocol` Kind、存档 DTO、实体 UID 归属、个人资金/科技和文件路径；发布前由主控统一处理版本。

## 4. 待办实施分工（本轮未执行）

- [ ] **B33-1 / Session + Transport：** 核对 `Protocol.Encode → transport.Send` 的每类异常是在排队前、已经排队后还是不确定阶段；区分 TCP/Steam 和客户端断开/重连。
- [ ] **B33-2 / Session：** 对 `PublishLocalMapsCore` 的首次 `map` 业务发送设计最窄的“尝试预留 → 确认入队 / 明确回滚 / 原 CommitId 的有界重试”状态机；标记只对应唯一事务对象。
- [ ] **B33-3 / Game + Session：** 确认 `bridge.CaptureMapUpdate` 的基线、副作用与原生 Revision；新尝试必须保留完整正确的 checkpoint，不得从已经消费的本地 delta 错误重捕获。
- [ ] **B33-4 / Session：** 与 BUG-32 当前个人发送 Owner 明确先后顺序、互斥门禁和 ACK/Reset/关闭恢复接口；不抢改 `PersonalOutbound`、个人接收和测试。
- [ ] **B33-5 / Test：** 新增链接真实 `Authority.PublishLocalMapsCore/ReceiveMapAcknowledgement` 的故障注入，覆盖编码失败、纯排队拒绝、实际入队 ACK 丢失、同步重入、重复/旧任期收包、退出与地图强制完整基线。不可复制一份假发送算法当作生产覆盖。
- [ ] **B33-6 / 主控：** 原 Owner 及所有混合 hunk 签收，真正可复现的 Git 0U/0M 源码闭包，独立小提交与 VERSION/CHANGELOG 收口，再按适用的用户指令执行三端 BuildOnly 和多人验收。

## 5. 故障注入验收矩阵（当前未执行）

| 场景 | 注入点 | 期望验证 |
| --- | --- | --- |
| C33-01 | 已生成 MapId/CommitId/Revision 后，首次 Send 在 TCP 队列入队前抛 `IOException` | 不产生幽灵在途阻断；仅自身预留回滚或同事务保留可靠待重试，无虚假 ACK |
| C33-02 | Steam 发送队列满、TCP 已关闭连接、短时间恢复 | 失败原因分类正确，下轮不无限重新 Clone；B/C 正常玩家不受阻 |
| C33-03 | `WireMapUpdate`/Protocol 编码失败、`CaptureMapUpdate` 原生失败 | 不把编程/原生故障误判为“未入队可安全重来”；保持原 fail-closed |
| C33-04 | 实际已入队但房主 ACK 丢失/迟到/重复 | 同一 `CommitId/Revision` 幂等恢复，不重复应用地图资产或推进两次 MapRevision |
| C33-05 | `Send` 同步回调清理旧记录、替换新记录，然后旧发送方抛错 | 只能清理同一事务对象，不删除回调后更新的在途地图 |
| C33-06 | `mapRejected`、完整 `view`、模拟权 AuthorityEpoch 切换、重连新 SessionId | 按真实房主权威恢复，不应用旧任期提交或残留别的会话回执 |
| C33-07 | 同一客户端地图失败与并行 BUG-32 个人进度更新一起发生 | 两种在途门禁都能按合同收口，不造成跨通道互相永久等待或私有数据丢失 |
| C33-08 | `checkpointRequest/finalDone` 与尚未确认地图、退出、关房交错 | 没有真实提交 ACK 时不得谎称 Save/已完成；超时或拒绝有明确状态和一致存档 |
| C33-09 | 真 Native Save/授权/伪造 MapRevision 失败 | 真权限、原生、存档错误照旧阻断，不被普通发送恢复保护吞掉 |
| C33-10 | 用户以 TCP/Steam 2～3 端做大地图、发送背压、断线恢复、保存续开 | 房主和客户端 MapRevision/CommitId/实体归属、个人版本与 `SaveGeneration` 一致，不重放资产副作用 |

**证据层级：** S0 实际源码静态审查（本轮完成）；S1 生产 partial 故障注入、S2 TCP/Steam 入队回环、S3 经 R13 签收的三端同源 `BuildOnly`、S4 用户多人游戏状态核对（本轮均未执行）。

## 6. 归档要求与本轮边界

须由 Session/Transport/Game 原 Owner 签收、C33-01～09 的生产回归和故障注入留存、R13 干净源码三端构建、用户 C33-10 双端/多人日志及游戏状态验收全部完成后方可归档。**仅代码阶段完成也需将本卡留在 `agent-tasks/待办/`。**

本轮只读浏览 `Authority/Networking/MultiplayerSession/SlowOperations/Transport` 与旧任务、协调表；发现并行 BUG-32 Owner 后收窄地图边界，新增本任务和索引/邮箱/协作记录。**未修改 MOD 功能源码、版本、协议、存档或三端副本，未编译、BuildOnly、启动游戏或推送。**历史未提交源码均保留。
