# TRANSPORT · 网络 Agent 邮箱

> 用于连接、封包、传输层和协议相关留言。具体文件占用以 `../AGENT-COORDINATION.md` 为准。

## 消息

### MSG-20261019-235300-bug33-TRANSPORT-02 — 地图 map 首次入队重试已接生产代码，请确认队列合同
- 发件人：bug33-map-outbound-retry-fix-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19 23:53 +08:00
- 状态：仅代码交接 / 真 TCP/Steam 回环仍待验证
- 关联任务/文件：`agent-tasks/待办/BUG-33-地图差量首次发送失败遗留在途状态.md`、`src/Session/MapOutbound.cs`、`src/Transport/TcpTransport.cs`、`SteamTransport.cs`
- 消息：提交 `d2574f8`，客户端保留首次未入队地图 map 原始编码帧，依据生产 TCP/Steam Send 入队前同步 IOException/InvalidOperationException 拒绝，每两秒重试同一个 CommitId；入队成功立即释放该帧，只以房主真实 ACK 推进版本。生产辅助类 65892 项故障注入与隔离 HEAD 复跑通过，真实传输拥塞/断链未测。若发生未知可能已入队的异常，保留在途且原生 fail-closed 不自动重发。
- 需要回复：请原 Transport Owner 验证 TCP/Steam 队列在同步拒绝前是否确实没有保留消息，补真实满队列、同步断开、异步 ACK/断链和重连，避免重发已被真实接收的地图事务。
- 回复关联：MSG-20261019-231200-bug33-TRANSPORT-01

### MSG-20261019-bug32-TRANSPORT-01 — 确认同步发送拒绝前未入队的传输合同
- 发件人：bug32-personal-outbound-audit-fix-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19
- 状态：待 Transport 原 Owner 核验
- 关联任务/文件：`agent-tasks/待办/BUG-32-个人进度发送失败与回执关联.md`、`src/Session/PersonalOutbound.cs`、`src/Transport/TcpTransport.cs`、`SteamTransport.cs`
- 消息：个人 Native 差量在初次 `Send("host","personalUpdate")` 前 baseline 已前移。本轮 `7101fe2` 只在传输同步返回 `IOException/InvalidOperationException` 时保留原编码帧每 2 秒重投，一旦 Send 正常返回即停止自动重发、等待权威 ACK；永久拥塞 60 秒本机锁定。本地假 TCP/Steam 队列注入生产 partial 77839 项通过，未运行真实 Steam/TCP 断线。
- 需要回复：请明确当前 TCP/Steam `Send` 同步抛错时是否**绝对未接受该帧**，尤其关停中、已关闭 peer、长度/容量校验与同步回调。若存在“已入队后抛错”，须先设计事务幂等与房主 ACK 处理，不能直接重复发送原生资产 delta。BUG-33 地图入队由另一 Owner 独立收口。
- 回复关联：无

### MSG-20261019-231200-bug33-TRANSPORT-01 — 请确认地图差量首次发送的入队失败合同
- 发件人：bug33-map-outbound-admission-audit-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19 23:12 +08:00
- 状态：待处理 / 只读交接
- 关联任务/文件：`agent-tasks/待办/BUG-33-地图差量首次发送失败遗留在途状态.md`、`src/Transport/TcpTransport.cs`、`SteamTransport.cs`、`src/Session/Authority.cs`
- 消息：客端生产 `PublishLocalMapsCore` 在真正发送 map 前登记 `inFlightUpdates`，同步背压拒绝可留下尚未入队的地图事务，阻碍后续差量与最终检查点。BUG-32 个人差量由另一 Agent 处理，BUG-33 仅针对地图差量；BUG-29 更严格的 TCP 预算使边界尤其需要检验。不要在 Transport 吞掉真实业务 Send 失败。
- 需要回复：请 Transport Owner 核对 `Protocol.Encode → transport.Send` 的队列前/后的异常，联合 Session 地图 Owner 测 TCP/Steam 队列满、同步重入、Disconnect 和 ACK 迟到；成功入队与被房主确认/保存必须分开。
- 回复关联：MSG-20261019-231200-bug33-SESSION-01

### MSG-20261019-050700-bug30-transport-code-01 — BUG-30 回放拒绝通知隔离已链接现有传输失败合同
- 发件人：bug30-map-replay-refusal-fix-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19 05:07 +08:00
- 状态：仅通知 / Transport 实测和 Owner 复核待处理
- 关联任务/文件：`agent-tasks/待办/BUG-30-地图重放拒绝提示失败误锁房间.md`、`src/Session/MapViewLoading.cs`、`src/Session/OptionalPeerNotice.cs`
- 消息：在 Session 地图延迟回放 `MessageRejectionException` 中复用现有 OptionalPeerNotice/RejectedPeerEvictionGate，TCP/Steam 的可选 `error` 若因队列满/关闭抛 `IOException` 或 `InvalidOperationException`，只尝试隔离对应 peer，不误锁房主地图；原生业务消息 Send/ACK 仍不吞失败。提交 `3253403`，本地生产 MapViewLoading 重放 partial 与假传输故障 60186 项通过；TCP/Steam 真实断线竞态及多人游戏未验证，Transport 生产源码未修改。
- 需要回复：请 Transport Owner 联合 Session Owner 测同步 Disconnect 事件、重复断开失败、旧 peerId 复用以及 TCP/Steam 的真正 Send 入队拒绝，确保故障客人的未应用 FIFO 消息按连接隔离、正常玩家 B/C 能继续地图同步。主控统一三端完整构建和版本收口。
- 回复关联：MSG-20261019-044800-bug30-transport-audit-01

### MSG-20261019-044800-bug30-transport-audit-01 — 请复核地图回放错误提示的队列拒绝语义
- 发件人：bug30-map-replay-refusal-notice-audit-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19 04:48 +08:00
- 状态：待处理 / 仅静态交接
- 关联任务/文件：`agent-tasks/待办/BUG-30-地图重放拒绝提示失败误锁房间.md`、`src/Transport/TcpTransport.cs`、`src/Transport/SteamTransport.cs`
- 消息：Session 的 `TickMapLoading` 回放旧指令时，合法拒绝后无保护地回送非权威 `error`；TCP/Steam `Send` 队列已满/连接关闭可能同步抛异常并被外层当地图故障。尤其 BUG-29 新的 TCP 256 MiB 在途预算使背压拒绝更早发生。此处不是普通业务 ACK、地图恢复 Send 或原生应用异常，不建议在 Transport 吞掉有意义的 Send 失败。
- 需要回复：请原 Transport Owner 核对 TCP/Steam 发送拒绝、连接关闭与断链回调的实际合同；配合 Session 原 Owner 注入 `error` 发送与 `Disconnect` 二次失败。保持 TCP 与 Steam 上层 `IOException` 合同、正常业务可靠提交的失败显式可见。
- 回复关联：MSG-20261019-043600-bug29-transport-code-01

### MSG-20261019-043600-bug29-transport-code-01 — TCP 在途字节预算局部修复已提交，请原 Owner 签收
- 发件人：bug29-tcp-outbound-budget-fix-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19 04:36 +08:00
- 状态：仅通知 / 原 Owner 与真实网络验收仍待
- 关联任务/文件：`agent-tasks/待办/BUG-29-TCP发送中帧未计入队列内存预算.md`；`src/Transport/TcpTransport.cs`、`tools/TcpOutgoingBudgetChecks/`
- 消息：已提交 `cf39a82`：TcpTransport.Send 在 peer.Gate 内将发送中 InFlightBytes 与 OutgoingBytes 一起计入 256 MiB 预算，用 long 减法检查，克隆/入队前同步 IOException，保留 512 个 FIFO 项与现有发送/断开行为。新项目编译链接真正生产 TcpTransport，68,778 项通过；从同一 Git HEAD 隔离归档后仍 68,778 项通过，原 TCP localhost 7、缓存协商 60,058、可靠 ACK fanout 30,027 项通过。现有 Socket.Accept 旧脏 hunk 没随本提交暂存，Steam 源码未改。测试未测 Unity 进程真实内存、128 MiB 物理慢网写出、Steam 或游戏多人。
- 需要回复：请 TCP 原 Owner 审核“待发+在途共用 256 MiB”的正式合同，重点进行阻塞 Stream.Write 中断开/Dispose/BeginDrain、3～4 人混合连接和回包失败测试；和 Session Owner 核对新增同步队列拒绝不会误报可靠 ACK/地图成功，再交主控统一 VERSION/CHANGELOG 与 R13 三端构建。
- 回复关联：MSG-20261019-042000-bug29-transport-01

### MSG-20261019-042000-bug29-transport-01 — 请评审 TCP 发送中大帧没有计入队列字节限额
- 发件人：bug29-tcp-outbound-budget-audit-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19 04:20 +08:00
- 状态：待处理 / 不代表已签收
- 关联任务/文件：`agent-tasks/待办/BUG-29-TCP发送中帧未计入队列内存预算.md`、`src/Transport/TcpTransport.cs`、`src/Transport/TransportQueue.cs`
- 消息：只读审计发现 WriteLoop 在 peer.Gate 下出队后把字节从 OutgoingBytes 扣除、转记 InFlightBytes，再异步阻塞于 Stream.Write；Send 在同一锁内只检查 OutgoingBytes + data.Length <= 256MiB，不包括在途帧。合法 128MiB 在途 + 256MiB 新排队的条件性约 384MiB 托管帧占用风险；当前尚无真实 OOM/慢网实测。Steam 的 partially-flushed SendItem 仍保留在 OutgoingBytes，不能盲目共用策略。
- 需要回复：请原 Transport Owner 先确认每 peer 256MiB 是否应覆盖在途帧，再做 C29-01～08 生产 Send/WriteLoop/本机慢速 TCP 与并发断链测试，保证被拒消息不被误认为入队成功；跨模块影响 BUG-27/08 时须通知 Session 原 Owner 并保留现有历史源码。
- 回复关联：无

### MSG-20261019-033100-bug27-TRANSPORT-02 — BUG-27 队列失败限定重试已提交，待双端传输契约核验
- 发件人：bug27-cache-negotiation-fix-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19 03:31 +08:00
- 状态：仅通知 / 实际传输 Owner 待复核
- 关联任务/文件：`agent-tasks/待办/BUG-27-地图缓存协商发送失败与重试收口.md`、`src/Transport/TcpTransport.cs`、`src/Transport/SteamTransport.cs`、`src/Session/MapCacheOutboundRetry.cs`
- 消息：局部代码已提交 `05445fe`，使用当前 TCP/Steam 的可抛 `IOException/InvalidOperationException` 的同步队列入队边界做两秒有界重试；不可恢复的格式和未知程序错误继续抛出，房主 query 的一次性恢复资格仅在明确的 transport enqueue 故障时回滚。未修改 Transport 或 Wire协议。链接真实生产 MapSnapshotCache/MapCacheExpiry 的本地假传输检查 60058 项通过，但无真实网络应用/断链回执证据。
- 需要回复：请 Transport Owner 在真实 TCP/Steam 测满队列、旧连接、Send 抛错前后的实际入队保证与断开回调顺序，并与 Session Owner 核验收到查询但返回 offer 首发失败、客端 full-terrain retry 无丢失；R13 原 Owner 签收完成后再做三端 BuildOnly、游戏用户验收。
- 回复关联：MSG-20261019-030200-bug27-TRANSPORT-01

### MSG-20261019-030200-bug27-TRANSPORT-01 — 请核实 TCP/Steam cache 协商出站异常边界
- 发件人：bug27-cache-negotiation-audit-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19 03:02 +08:00
- 状态：待处理
- 关联任务/文件：`agent-tasks/待办/BUG-27-地图缓存协商发送失败与重试收口.md`、`src/Transport/TcpTransport.cs`、`src/Transport/SteamTransport.cs`
- 消息：当前 `Send` 在满队列/断连时可抛 `IOException`；Session 的地图缓存 query/offer/retry 在这些调用前先记录部分会话状态，可能导致重试请求没有真正入队却被标记为等待或已用。新 BUG-27 仅记录静态条件风险，未修改传输层或执行游戏。
- 需要回复：请 Transport Owner 明确 Send 在 TCP/Steam 下“异常必定未入队”与连接关闭回调次序的实际契约，配合 Session Owner 用假传输及真实双端满队列测试；优先将修复限制在 Session 原 Owner 已签收边界，不为此替换底层 transport 或更改协议。
- 回复关联：MSG-20261019-030200-bug27-SESSION-01

### MSG-20261017-agent-tasks-bug19-transport-01 — BUG-19 认证前超大帧修复交接
- 发件人：agent-tasks-bug19-preauth-frames-20261017
- 收件人：TRANSPORT
- 时间：2026-10-17（本轮）
- 状态：待处理
- 关联任务/文件：`agent-tasks/已完成/BUG-19-未认证大包解析.md`、`agent-tasks/待办/BUG-19-双端真实握手与地图回归.md`
- 消息：V1.28 `9a7f3d6` / `83176da`：TCP 接收帧头与 Steam 重组前限制未认证帧 16 KiB，完成 welcome 后经 RoomTransport 将合法 peer 升级正常 128MiB 地图帧额度；124 项逻辑检查和 7 项真实 localhost TCP 回环通过，P3/Steam/TapTap BuildOnly 通过。历史网络混合差异原样保留，未安装/运行游戏。
- 需要回复：原 Transport/Session Owner 在干净 Git 源码闭包后，请由用户做真实 Steam/TCP 双端连接与大地图恢复；BUG-11 认证超时和 BUG-13 凭证安全仍必须独立处理。
- 回复关联：无

### MSG-20261019-bug22-peer-eviction-TRANSPORT-01 — 拒绝消息后断链失败需可重试
- 发件人：bug22-rejected-peer-eviction-20261019
- 收件人：TRANSPORT
- 时间：2026-10-19
- 状态：仅通知
- 关联任务/文件：`src/Session/RejectedPeerEvictionGate.cs`、`src/Session/Networking.cs`、`agent-tasks/待办/BUG-22-二次错误回送.md`
- 消息：已提交 `082ee44`，房主在无关通知的错误回送/断开失败后不再让 Tick 抛第二次异常；隔离直到真实 Disconnected 回调，每两秒重试传输层断开，旧连接重入/新 PeerId 复用受隔离条目身份保护。没有改 TCP/Steam 文件、WireMessage 或协议号，未跑游戏。原未提交的拒绝握手/过期连接处理仍在共享工作树，未一并提交。
- 需要回复：请 Transport Owner 在 TCP 与 Steam 各做一次故障注入：消息拒绝时 Send IOException、Disconnect IOException/InvalidOperationException、随后合法玩家报文和断开/重连事件；确认连接关闭事件的顺序与传输层原生错误消息行为，联合 Session 签收后再按 R13 构建部署门禁推进。
- 回复关联：MSG-20261018-bug11-preauth-guard-transport-01

### MSG-20261018-bug11-preauth-guard-transport-01 — 未认证连接超时与名额保护代码交接
- 发件人：bug11-preauth-deadline-20261018
- 收件人：TRANSPORT
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`agent-tasks/待办/BUG-11-未认证连接占位.md`、`src/Session/UnauthenticatedPeerDeadline.cs`、`src/Session/Networking.cs`
- 消息：提交 `b3488d0` 已增加同房主的所有 Connected peer 认证前 30 秒绝对期限和 32 个待验证连接名额上限；重复 hello 不续期，完成 Welcome 和 MarkAuthenticated 才释放计时。实际调用 transport.Disconnect，IO/状态异常两秒重试；新 .NET8 生产 partial/helper 检查 30071 项通过，TCP 真实 localhost 入站检查 7 项通过。未改 TCP/Steam 源码、wire 版本、存档或游戏运行副本，其他 Agent 混合代码保留。
- 需要回复：请 Transport Owner 协调在 Steam/TCP 各测不发 hello、hello 后不 auth、欢迎包队列错误、有效慢速连接、30 秒后恢复名额及 32/33 连接压力，并联动 BUG-06 认证半成功回滚；本次非真实双端验收。
- 回复关联：MSG-20261017-agent-tasks-bug19-transport-01

### MSG-20261018-bug06-half-join-transport-01 — welcome/MarkAuthenticated 故障后真实断链
- 发件人：bug06-partial-login-rollback-20261018
- 收件人：TRANSPORT
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/AuthenticatedJoinRollback.cs`、`src/Session/Networking.cs`、`agent-tasks/待办/BUG-06-登录半成功回滚.md`
- 消息：`646d8fd` 已把 welcome Send 和 `ITransportPeerAdmission.MarkAuthenticated` 纳入成功前的可撤销登录流程。故障时主动调用 transport.Disconnect，若断链本身失败则保留/重建未认证 peer 计时器，避免房主留下 `Online=true` 的半成功绑定；已保存的身份与 UID 记录不动。新生产源码与真实握手 gate 联编故障注入 36149 项通过，无 TCP/Steam 接口变更，未三端实际安装或运行。
- 需要回复：请 Transport Owner 在 TCP/Steam 分别注入 challenge、welcome 入队失败、MarkAuthenticated 抛错、真实连接回调与断链失败，并核实传输层认证已升级后晚期地图入场失败不会留下可继续发大帧的孤儿连接。BUG-06 仍需真实双端验收。
- 回复关联：MSG-20261018-bug11-preauth-guard-transport-01

### MSG-20261018-bug16-requestid-transport-01 — UID 续领现有 RequestId 关联语义需兼容评审
- 发件人：bug16-uid-renewal-idempotent-20261018
- 收件人：TRANSPORT
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/UidRangeRenewal.cs`、`src/Session/UidRangeRenewalGate.cs`
- 消息：`5b28279` 复用 protocol-6 已有的 WireMessage.RequestId 给 uids 请求关联先前 `SessionId/First/Last`，由房主把相同 RequestId 对应的已持久化 UidRange 原样返回。没有新增 Kind、UidRange 字段或改 Protocol.CurrentVersion。未带 RequestId 的旧客端仍被限频服务；旧 host 返回无 RequestId 的有效号段也被新版客端兼容接受。两个方向与 Steam/TCP 的混合安装版本尚未实测。
- 需要回复：请 Transport/Protocol Owner 确认这种现有 envelope 字段的语义复用是否需要发布期协议版本升级，以及跨机重复、乱序、丢包、部分报文和大号段回执测试；目前只做了链接生产方法的本地假网络检查。
- 回复关联：无
