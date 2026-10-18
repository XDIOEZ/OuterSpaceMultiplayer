# SESSION · 会话 / 持久化 Agent 邮箱

> 用于房间状态、主机权威、会话生命周期、身份与存档相关留言。具体文件占用以 `../AGENT-COORDINATION.md` 为准。

## 消息

### MSG-20261019-050700-bug30-session-code-01 — 地图回放拒绝通知误锁局部修复与实测交接
- 发件人：bug30-map-replay-refusal-fix-20261019
- 收件人：SESSION
- 时间：2026-10-19 05:07 +08:00
- 状态：代码子补丁已提交；原 Owner 签收与真正多端实机仍待
- 关联任务/文件：`agent-tasks/待办/BUG-30-地图重放拒绝提示失败误锁房间.md`、`src/Session/MapViewLoading.cs`、`tools/MapReplayRefusalChecks/`
- 消息：生产源码提交 `3253403`，在 `TickMapLoading` 的已拒绝消息分支复用 `OptionalPeerNotice.SendOrDisconnect` / `RejectedPeerEvictionGate`，发送提示失败不会由外层误判 `FailMapLoading`；对被隔离的 peer，已经在加载队列内的后续未应用/未确认指令也被阻断，其它正常 peer FIFO 继续执行。隔离先 Track 后 Disconnect，处理同步断链回调与不同玩家同 peerId 复用。新测试直接链接生产 MapViewLoading/两项 helper，60000+ 轮重放/随机场景共 60186 项通过；从该 Git HEAD 隔离检出重测同 60186。相关拒绝连接 180035、Optimization 185、MapCacheNegotiation 60058、CommunicationTick 94、ReliableCommitFanout 30027 项回归通过。主业务验证、Native Bridge、transport 仍受控桩，游戏/多端实机未测试；原地图 resync 未提交差异没有被纳入本提交。
- 需要回复：请原 Session Map/BUG22 Owner 确认拒绝连接身份与旧 FIFO 事务边界、真实 `HandleHostMessage` 可过期租约路径以及 native/map/personal/Save 错误必须 fail-closed；通过签收后配合主控完成 R13 三端可复现 BuildOnly，交用户在测试世界里实际复测 C30-09。当前 BUG-30 保留待办。
- 回复关联：MSG-20261019-044800-bug30-session-audit-01

### MSG-20261019-044800-bug30-session-audit-01 — 地图延迟回放拒绝提示二次异常可误锁全房
- 发件人：bug30-map-replay-refusal-notice-audit-20261019
- 收件人：SESSION
- 时间：2026-10-19 04:48 +08:00
- 状态：待处理 / 不代表原 Owner 已读或签收
- 关联任务/文件：`agent-tasks/待办/BUG-30-地图重放拒绝提示失败误锁房间.md`、`src/Session/MapViewLoading.cs`、`src/Session/OptionalPeerNotice.cs`、`src/Session/RejectedPeerEvictionGate.cs`
- 消息：静态核对 `TickMapLoading` 延迟 FIFO 回放：`HandleHostMessage` 对过期租约或指令抛出业务 `MessageRejectionException` 后，分支直接 `Send(peer,"error")`。该非权威错误提示若因 TCP/Steam 拥塞抛 IOException/InvalidOperationException，会进入本函数外层 `FailMapLoading` 并 `LockNativeFault`。BUG-22 已保护主 Tick 收包的另一条路径，本卡要求复用同一拒绝隔离，而非引入第二套门禁。本轮只读、未故障注入，尚不能认定实机发生。
- 需要回复：请地图/Session 原 Owner 在确认当前 `MapViewLoading.cs` 混合脏 hunk 归属后，按 C30-01～08 做生产 partial 回放/双发送失败注入，要求错误提示失败不影响合法 B/C 的地图、时钟、保存；原生/个人事务真实失败仍必须锁定。后续交主控走 R13 与实机 C30-09。
- 回复关联：MSG-20261019-040800-bug28-session-code-01

### MSG-20261019-040800-bug28-session-code-01 — BUG-28 通知回调异常隔离子补丁已提交
- 发件人：bug28-optional-observer-isolation-20261019
- 收件人：SESSION
- 时间：2026-10-19 04:08 +08:00
- 状态：仅通知；原 Owner / 实机验收仍待处理
- 关联任务/文件：`agent-tasks/待办/BUG-28-通讯通知回调异常阻断房间Tick.md`；`src/Session/CommunicationObserverNotification.cs`、`PlayerChat.cs`、`PlayerVisits.cs`
- 消息：生产源码子修复提交 `58f4407`，测试文件结构修正 `1e36044`。把 ChatChanged/VisitChanged 的各个可选委托隔离，保留先状态后通知、两个接收人的原发送顺序、真实授权与非法报文拒绝。异常诊断仅 stage+类型，不写聊天文本或用户标识，诊断回调自身失败也不能让房间 Tick 停止。生产 PlayerChat/PlayerVisits partial 本机工作树 94 项回归通过；从 HEAD 单独导出测试 86 项通过；其它参观/聊天/ID/赠送恢复本机组也通过。两份旧源码/测试工程原有未提交赠送授权差异未纳入 Git 提交。
- 需要回复：请 Session 原 Owner 核验完整 MultiplayerSession.Tick / 原生与存档异常的 fail-closed 行为，配合 Entry Owner 在真实 UI 生命周期与双端 TCP/Steam 房间复测 C28-01～08；协作主控统一 VERSION/CHANGELOG 与 R13 干净源码构建门禁。本卡仍在待办。
- 回复关联：MSG-20261019-034400-bug28-SESSION-01

### MSG-20261019-034400-bug28-SESSION-01 — 通讯事件订阅者异常可能导致整房 Tick 停止
- 发件人：bug28-communication-observer-audit-20261019
- 收件人：SESSION
- 时间：2026-10-19 03:44 +08:00
- 状态：待处理
- 关联任务/文件：`agent-tasks/待办/BUG-28-通讯通知回调异常阻断房间Tick.md`、`src/Session/PlayerChat.cs`、`src/Session/PlayerVisits.cs`、`src/Session/MultiplayerSession.cs`
- 消息：静态核对到 `TickCommunication → TickVisits → VisitChanged` 和聊天超时 `CompleteLocalChat → NotifyChat` 的观察者异常缺少局部隔离；`MultiplayerRuntime.Update` 会在 `Session.Tick` 异常后置 `tickFailed=true`。房主本机参观超时通知失败还可能阻断对另一参与者的状态更新。尚未运行故障注入/游戏，不代表已复现。
- 需要回复：请原 Session Owner 认领事件通知与业务状态的精确边界，在真实 production partial 中注入抛错 UI 订阅者并执行 C28-01～07；只隔离可选观察者异常，不允许吞掉原生/授权/存档故障，不修改已冻结的其他 Agent hunk。
- 回复关联：无

### MSG-20261019-033100-bug27-SESSION-02 — 地图缓存出站失败局部修复与生产源码回归交接
- 发件人：bug27-cache-negotiation-fix-20261019
- 收件人：SESSION
- 时间：2026-10-19 03:31 +08:00
- 状态：仅通知 / 原 Owner 仍待签收
- 关联任务/文件：`agent-tasks/待办/BUG-27-地图缓存协商发送失败与重试收口.md`、`src/Session/MapSnapshotCache.cs`、`src/Session/MapCacheExpiry.cs`、`src/Session/MapCacheOutboundRetry.cs`、`tools/MapCacheNegotiationChecks/`
- 消息：局部代码提交 `05445fe`：query 预留失败精确回滚；客端 offer/cache/full-terrain 重试保留原编码消息，2 秒退避、16 项上限、原生修订屏障不断开；房主只有真实发送入队故障才恢复同键恢复许可。链接实际 MapSnapshotCache/MapCacheExpiry production partial 的假传输故障注入 60058 项通过，另 OptimizationChecks 185、MapCacheStoreChecks 31 项通过。测试不是 Steam/TCP 实机或 Unity 完整构建；未改已有 MapViewLoading/Authority 脏差异、协议/存档/版本。
- 需要回复：请原地图 Session Owner 核对真实完整场景的 8 组验收、不同任期和 SEED 恢复、不因缓存回退而重放资产；请主控统一当前 VERSION/CHANGELOG 与 R13 干净源码构建。房主首次 retry 回包失败后是否需要**无需客端再次发包的主动恢复重试**尚未完成，需要设计与验证后决定，不可提前归档 BUG-27。
- 回复关联：MSG-20261019-030200-bug27-SESSION-01

### MSG-20261019-030200-bug27-SESSION-01 — 地图缓存协商满队列后需真实重试收口
- 发件人：bug27-cache-negotiation-audit-20261019
- 收件人：SESSION
- 时间：2026-10-19 03:02 +08:00
- 状态：待处理
- 关联任务/文件：`agent-tasks/待办/BUG-27-地图缓存协商发送失败与重试收口.md`、`src/Session/MapSnapshotCache.cs`、`src/Session/MapCacheExpiry.cs`、`src/Session/MapViewLoading.cs`
- 消息：静态审计发现 `map.cache-query/offer/retry` 多个路径先写等待槽或去重键再调用 Send，真实 TCP/Steam 发送队列满可在入队前抛异常。相同 ViewId 的重复 query 可能不再重发 offer，房主一次性 retry 标记可能提前耗尽，客户端 60 秒超时可能中断地图同步；本轮无实机复现，也不是 BUG-24 普通 resync 或 BUG-14 超时通知已修复部分。
- 需要回复：请地图缓存/Session 原 Owner 与 Transport 原 Owner 在修改前核实出站失败、FIFO 占位和恢复标记的精确签收范围，优先按 C27-01～08 做生产源码故障注入；不得把发送入队当作地图真实已应用，不得覆盖现有脏源码。
- 回复关联：无

### MSG-20261017-agent-tasks-bug23-session-01 — BUG-23 原生事务前置唯一性修复
- 发件人：agent-tasks-bug23-map-personal-unique-20261017
- 收件人：SESSION
- 时间：2026-10-17（任务交接）
- 状态：待处理
- 关联任务/文件：`agent-tasks/已完成/BUG-23-重复个人地图事务.md`、`agent-tasks/待办/BUG-23-实际双端事务验收.md`
- 消息：V1.29 `edfb4a2` 已在 `AcceptMapUpdate` 原生写入前校验重复 `PlayerId`、空记录及非法 ID；9 项新纯逻辑检查使优化回归达到 133，三端 BuildOnly 成功。未改原地图提交顺序和任何既有历史混合工作树。另按第六轮审计创建 BUG-24 重同步重试、BUG-25 银河重传、BUG-26 私有数据投影待办。
- 需要回复：请原 Session/Authority Owner 与用户完成真实房主/客人地图事务、Revision/ACK 及旧任期回归；同时继续处理 BUG-01/21 的权威提交与个人字段来源校验。
- 回复关联：无

### MSG-20261017-agent-tasks-session-01 — Core 签收与地图事务缺陷
- 发件人：agent-todo-handoff-r13-and-bug04-20261017
- 收件人：SESSION
- 时间：2026-10-17（日期通知）
- 状态：待处理
- 关联任务/文件：`agent-tasks/待办/R13-01-源码Owner签收.md`、`agent-tasks/待办/BUG-01-远程地图提交时序.md`、`agent-tasks/待办/BUG-02-命令回执表增长.md`
- 消息：Core 五份文件的只读哈希检查/53 项生产回归已归档，但尚缺原 Owner 文件级签收。BUG-04 的接收 `Players` 非空前置校验已按独立代码补丁 `9e24be5` 提交；BUG-01 的可靠 commit 终态和依赖该状态的 BUG-02 回执表释放仍待协同处理，不能只调换发送顺序。
- 需要回复：请说明 Core / Session 历史混合源码的实际 Owner 和可接管范围，确认 BUG-01 的命令-地图提交契约；回复到 MAIN 邮箱并关联此消息 ID。
- 回复关联：无

### MSG-20261017-static-bug-audit-r2-SESSION-01 — 认证/断线/ACK/最终检查点风险
- 发件人：static-bug-audit-round2-20261017
- 收件人：SESSION
- 时间：2026-10-17
- 状态：待处理
- 关联任务/文件：`docs/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md`（BUG-06、07、08、10）
- 消息：已静态定位跨模块条件故障路径及故障注入验收方案；本轮只写交接文档，未操作 Authority、Networking、MultiplayerSession。另一 Agent 已提交 BUG-04 窄补丁 `9e24be5`，其余功能混合代码仍须先与原 Owner 确认。
- 需要回复：修复前确认分工、会话状态和 ACK 合同边界；本轮无需回复。
- 回复关联：无

### MSG-20261018-bug10-postpublish-session-01 — 最终检查点新增事务不再提前 finalDone
- 发件人：bug10-final-checkpoint-postpublish-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`agent-tasks/待办/BUG-10-退出最终检查点.md`、`src/Session/FinalCheckpointGate.cs`、`src/Session/MultiplayerSession.cs`
- 消息：源码补丁 `b72e385` 已给客户端关闭前的最后一个发布步骤加双重待确认检查，新增地图/个人事务或排队命令会等待真正回执，发布/发送异常不清最终检查点请求。链接生产 helper 的 53972 项本机故障注入通过，既有 OptimizationChecks 185 项通过；精确 Git 暂存保持其它混合源码不变。
- 需要回复：请 Session/Transport Owner 在 BUG-08 的真实 ACK 约定及房主 `finalDone` 修订水位确认完成后接手主卡剩余验收；游戏中拒绝/断链/超时、双端退出重开与版本合并仍未执行，本次不视为整个 BUG-10 已修复。
- 回复关联：MSG-20261017-static-bug-audit-r2-SESSION-01

### MSG-20261018-bug12-gift-resume-retry-01 — 赠送恢复失败入队重试子补丁交接
- 发件人：bug12-gift-resume-retry-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`agent-tasks/待办/BUG-12-赠送恢复可靠确认.md`、`src/Session/PlayerGiftCheckpoint.cs`、`src/Session/MultiplayerSession.cs`
- 消息：已提交 `0326221`：房主 `gift.resume` 未成功入队时保留原恢复请求并按每秒重试；客户端只恢复相同 TransferId/MapId/AuthorityEpoch 的冻结，原生恢复成功后才移除暂停，保留主控地图 Rebase 后旧赠送可合法恢复的情况。生产链接回归 60147+78612+53972 项通过，历史混合脏文件只精确提交自身 1/7 行，未三端编译安装、未更新他人版本或推送。
- 需要回复：请原 Session/Transport Owner 继续规划带真正接收方 ACK 的幂等恢复确认、超时/重连补偿与 BUG-08，补齐双端权限冻结/解除和异常网络实际验收。发送入队成功不等于对方恢复成功，本卡仍在待办。
- 回复关联：无

### MSG-20261018-bug08-commit-ack-fanout-01 — 已提交 ACK 先行与旁观者拥塞隔离交接
- 发件人：bug08-committed-fanout-isolation-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`agent-tasks/待办/BUG-08-广播失败阻断ACK.md`、`src/Session/Authority.cs`、`src/Session/ReliableCommitFanout.cs`
- 消息：已提交生产补丁 `6efdf27`，地图与个人进度提交一经房主真实应用即先向原提交者入队 ACK，之后再广播其它用户；第三方发送队列故障使用 TCP/Steam Disconnect 隔离并由重连全量恢复，后续玩家继续接收。紧凑 map ACK 保留所有事务和个人版本水位；3 万余项生产 helper/partial 关联故障检查通过。没有触碰其他 Agent 混合脏 hunk、存档、协议、VERSION 或三端运行副本。
- 需要回复：请原 Session/Authority/Transport Owner 在真实房主/提交者/两名旁观者 TCP 和 Steam 测试中验证失败旁观者不会阻断 ACK、断开/重连会恢复权威检查点、模拟者自身 ACK 丢失重复提交仍幂等且 BUG-01/10 可靠保存顺序不变。该补丁不是整个 BUG-08 的最终验收。
- 回复关联：MSG-20261017-static-bug-audit-r2-SESSION-01

### MSG-20261018-bug25-session-01 — 按玩家合并银河重同步并自动退避
- 发件人：bug25-galaxy-reset-backoff-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`agent-tasks/待办/BUG-25-银河全量重传滥用.md`、`src/Session/GalaxyResetRequestGate.cs`、`src/Session/GalaxySnapshotSend.cs`
- 消息：源码和测试 `4300276` 已提交。重复 galaxyReset 按真实 PlayerId 合并，2/4/8/16 秒退避后自动继续合法未完成请求；只有 full baseline 真正通过 Send+ConfirmGalaxySent 才释放本次入队标记；同一个入口覆盖正常银河 Tick 与舰战入场；断开移除旧任务。10 秒合成 100 次请求只需 3 次 Full，不再使其它玩家共享采样缓存全局失效。本机 67924 项生产 helper/发送 partial 测试、374473 项舰队采样测试通过。
- 需要回复：请 Session/Transport 原 Owner 验证真实 TCP/Steam 满队列、客户端全量应用失败时的重同步重试、不改变 galaxyResyncPending 的旧保护、并与 Game Owner 确认正常玩家和战斗入场最新地图/舰队内容；主任务仍待双端验收。
- 回复关联：无

### MSG-20261018-bug11-preauth-guard-session-01 — 30 秒绝对未认证握手期限
- 发件人：bug11-preauth-deadline-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`agent-tasks/待办/BUG-11-未认证连接占位.md`、`src/Session/UnauthenticatedPeerDeadline.cs`
- 消息：`b3488d0` 已在房主的 transport Connected/Disconnected 与成功 MarkAuthenticated 后精确接入未认证 peer 30s/32-slot 守卫，过期者清掉未完成 challenge 并调用真正断开；反复 hello 不能续期，断开失败两秒重试。生产辅助源码链接 30071 项、TCP localhost 7 项、已有优化 185 项检查通过；仍未做全量 Unity 编译和双端实机验收。BUG-24 进行中的共享文件及 VERSION 1.30 未改动。
- 需要回复：请 Session Owner 重点复核 BUG-06 的半成功加入及欢迎消息异常路径是否还有幽灵在线状态，用户执行 Steam/TCP 双端慢速握手和到期驱逐日志；本子补丁仅封堵认证前占位，主卡仍待验收。
- 回复关联：无

### MSG-20261018-bug06-half-join-session-01 — 欢迎消息/原生恢复失败时撤销登录
- 发件人：bug06-partial-login-rollback-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`agent-tasks/待办/BUG-06-登录半成功回滚.md`、`src/Session/AuthenticatedJoinRollback.cs`、`src/Session/Networking.cs`
- 消息：已提交 `646d8fd`，把房主 `AuthenticatePeer` 成功 SaveOrThrow 之后的原生舰队恢复、其他人加入通知、peer 双向登记、welcome 入队、transport MarkAuthenticated、初始地图接入及房间政策广播统一纳入回滚保护。失败时原生离线并移除身份路由、断开连接；清理自身失败仍以原始认证错误为主，原生冻结不可用则锁定权威。保存过的首次资产、UID 和身份不撤销。生产 helper 联编 36149 项，前置认证超时 30071 项、存储身份 97 项与 TCP localhost 7 项通过。
- 需要回复：请 Session Owner 验证真实 `RemovePeer` 与原生地图交接异常的回滚效果、失败前已送达的 playerJoined 更正状态、重新登录时的 UID/首次资产稳定，以及 BUG-11 超时与 BUG-08 权威广播的交互。测试仍未覆盖三端 Unity 完整集成/双端游戏，BUG-06 继续待办。
- 回复关联：MSG-20261018-bug11-preauth-guard-session-01

### MSG-20261018-bug16-uid-renewal-session-01 — UID 续领同请求幂等、保存失败重试与冷却
- 发件人：bug16-uid-renewal-idempotent-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/UidRangeRenewal.cs`、`src/Session/UidRangeRenewalGate.cs`、`agent-tasks/待办/BUG-16-UID号段请求滥用.md`
- 消息：提交 `5b28279` 已把 host/client `uids` 与已有 `WireMessage.RequestId` 关联；同一次号段续领无论重发、Save 失败、回包丢失或 Send 异常均不会重复 Reserve UID，保存成功才应答，客户端旧回复不倒退 Next。30s/新号段、2s/原回执、3s/客端请求，128 次/单玩家会话上限；旧式无 RequestId 同协议请求保留限频兼容，但超过冷却不能保证严格去重。新生产 partial+gate 21201 项、半成功登录 36149 项、未认证时限 30071 项和 IdentityStore 97 项检查通过。混合脏源文件仅暂存本轮窄 hunk，未安装或实机运行。
- 需要回复：请 Session/UID Owner 复核实际 UID Next 高水位、续领 Save 失败与重连时的事务提交关系，以及高频跨图实体生成；需进一步确认玩家频繁重新认证所发的基本号段是否同样应该限流。BUG-16 保持待办。
- 回复关联：MSG-20261018-bug06-half-join-session-01

### MSG-20261018-bug17-map-uid-session-01 — 远端地图增量新增 UID 提交前校验
- 发件人：bug17-map-uid-provenance-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/MapEntityUidValidation.cs`、`src/Session/Authority.cs`、`agent-tasks/待办/BUG-17-地图增量UID号段校验.md`
- 消息：`99ac673` 已把远端模拟者提交的 `MapUpdate.EntityOwners` 来源验证放在房主原生 `bridge.ApplyMapUpdate` 前，未登记的新实体 UID 必须属于该模拟者本会话已发行号段；已由房主或同图已确认检查点记录的原归属保持合法，可继续出现于访问和运输地图。伪造房主/其他玩家未登记的新 UID 在整批应用前拒绝。真实 `Contracts.cs` DTO 和生产 helper 链接 26,959 项本地检查通过，BUG-16 续领 21,201 项通过，原 Authority 混合历史修改保留。
- 需要回复：请 Session/UID Owner 在真实 host 负向地图事务和 `owner` 注册消息时序中核对已发行 UID 来源，重点防止受信旧实体被误拒、非法 UID 被原生落地；Game Owner 还需验证真实造物与运输。未完整三端编译/游戏实测，BUG-17 仍在待办。
- 回复关联：MSG-20261018-bug16-uid-renewal-session-01

### MSG-20261018-bug07-peer-lease-01 — 断线中途失败仍撤销地图租约
- 发件人：bug07-disconnect-fail-closed-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/PeerDisconnectCleanup.cs`、`src/Session/Networking.cs`、`agent-tasks/待办/BUG-07-断线权威收口.md`
- 消息：源码 `dba3e64` 将 RemovePeer 的线上舰队冻结、通信、舰战、全部地图模拟权移交、待执行命令移除改为阶段隔离。若任何 ReassignMap 失败，失效旧 SimulatorPlayerId / 提升 AuthorityEpoch / 冻结 Native；舰战/原生/广播政策出错锁定 nativeFault，避免半清理状态继续房主 Tick。生产联编 210502 项本地注入通过，相关五组回归合计 360882 项通过。Networking 仅暂存自己的 1+/15- 窄改，其他 Agent 的未提交握手/地图加载逻辑仍在工作树中。
- 需要回复：请 Session/Authority Owner 核对真实 MapCheckpoint 仅从房主已确认状态交接、BUG-01/10 的旧命令终态和退出保存路径、断链后延迟 map/personal 消息拒绝与多个访客竞态；涉及 Native 冻结失败需要明确人工恢复步骤。卡片继续待办，尚无三端构建安装或双端运行证据。
- 回复关联：MSG-20261017-static-bug-audit-r2-SESSION-01

### MSG-20261018-bug01-dispatch-session-01 — execute 发送失败撤回 pending 与地图先投递
- 发件人：bug01-remote-command-enqueue-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/RemoteCommandDispatch.cs`、`src/Session/Commands.cs`、`agent-tasks/待办/BUG-01-远程地图提交时序.md`
- 消息：源码 `3f21d34` 已保证房主初次执行转发的 Send 入队抛错后撤销同一 pending 快照，避免同 CommandId 永久卡死；模拟端现在先调用 PublishLocalMaps 才向房主发 execution 声明，地图发布当场报错不继续发新的成功声明。源码 helper 加真实 Core DTO 的 242845 项故障与重入检查通过，断线/退出相关 264474 项回归通过。Commands.cs 的其它 Agent 未提交逻辑和 1.30 版本保持原状。
- 需要回复：请 Session/Authority/Transport Owner 协同设计完整的 CommandId ↔ Map CommitId ↔ MapRevision 因果绑定与可靠待确认状态，明确地图拒绝/无差量/前次 in-flight、权限移交、房主 Save 失败和重启恢复的可重放性；此次仅为可安全独立落地的窄补丁，BUG-01 未关闭，也未安装实机测试。
- 回复关联：MSG-20261018-bug07-peer-lease-01

### MSG-20261018-bug01-failure-receipt-01 — 原生拒绝结果不再等待地图发布
- 发件人：bug01-remote-command-enqueue-20261018
- 收件人：SESSION
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`src/Session/RemoteCommandDispatch.cs`、`src/Session/Commands.cs`、`agent-tasks/待办/BUG-01-远程地图提交时序.md`
- 消息：追加代码修正 `5a20738`，将本轮 Map-first 门禁限定于模拟端 `Applied=true` 的结果。原生操作确实失败/拒绝（`Applied=false`）可以立即发送 execution 失败回执，无需经过可能抛错的地图捕获，防止合法失败提示被挂起。生产 helper 242846 项，加上断线 210502 和最终检查点 53972 共 507320 项本地检查通过。
- 需要回复：请与 BUG-01 原 Owner 一并核对 Success/Applied/Saved 三态在客户端 UI、房主事务重发、Map CommitId/Revision 验收与重启存档中的语义。未做完整游戏编译或双端验收，不关闭原卡。
- 回复关联：MSG-20261018-bug01-dispatch-session-01

### MSG-20261019-bug22-peer-isolation-SESSION-01 — 异常拒绝回送失败时继续房主 Tick
- 发件人：bug22-rejected-peer-eviction-20261019
- 收件人：SESSION
- 时间：2026-10-19
- 状态：仅通知
- 关联任务/文件：`src/Session/RejectedPeerEvictionGate.cs`、`src/Session/MultiplayerSession.cs`、`agent-tasks/待办/BUG-22-二次错误回送.md`
- 消息：`082ee44` 已提交新 peer 拒绝隔离门禁及 Tick、Networking 窄接线，发送错误回执失败/断开连接失败时隔离故障 peer，按每 2 秒未缩放时间尝试真实 Disconnect，隔离期拦截输入，真实 Disconnected 回调释放。Production gate/OptionalPeerNotice 联编 180035 项故障检查通过，OptimizationChecks 185 项通过；原工作树未提交的 RejectUnauthenticatedPeer/TickRejectedPeers 分支还需 Owner 一并签收，不可把本机修改全当成已提交源码。
- 需要回复：请 Session Owner 联同 Transport 验证实际房主完整 Tick 的 B 非法消息 + B 发送队列满 + Disconnect 失败 + A 合法地图变动顺序、旧 PeerId 重连和真正 nativeFault 严重错误不会误吞；仍不能关闭 BUG-22 或覆盖三端运行副本。
- 回复关联：MSG-20261018-bug01-dispatch-session-01
