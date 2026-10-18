# BUG-27 / P1 · 地图缓存协商发送失败后状态滞留与重试收口

> **状态：待原 Owner 签收与故障注入（本轮仅静态审计）**
> **发现日期：** 2026-10-19（主仓库 `codex/multiplayer-v1`，审查时 HEAD `8276a7b`）
> **风险等级：** P1 / 地图加载可用性；**尚未用真实游戏或故障注入证明已经发生**
> **预期负责人：** Session 地图缓存/加载 Owner 主导，Transport Owner 复核 TCP/Steam 入队异常；涉及 Game 原生 SEED 回退时由 Game Owner 复核。不代表已经取得接管授权。
> **唯一需求依据：** [联机方案 V1](../../联机方案%20V1.md) 的地块加载、有序差量、本地缓存失效时回退权威地图、断线和原生状态一致性规则。

## 1. 为什么值得优先排查

网络消息**入队成功**和**收到对端完成回执**不同。客户端缓存协商的若干路径在 `Send` 前已经创建了等待槽、请求去重标志或退避状态；`TcpTransport.Send` 和 `SteamTransport.Send` 在队列满、连接关闭时可抛出 `IOException`，TCP 找不到 peer 还可能抛 `InvalidOperationException`。如果这些异常发生在**真正入队前**，当前状态可能阻止重新发同一个协商请求。

影响是**条件性**的：可能导致地图 FIFO 等待、缓存重组回退无法继续或房主为未发出的视图长期保留内存，随后触发既有 60 秒超时。不应把这份静态推理写成“地图必然卡死”或“多人实测已复现”；也不能通过丢弃未确认的地图/个人事务让界面看似恢复。

## 2. 实际生产调用链与可疑断点

| 方向 / 方法 | 已核实源码顺序 | 发送失败后的条件风险 | 后续应验证的边界 |
| --- | --- | --- | --- |
| **房主 → 客端** `SendNetworkMapView` | `MapSnapshotCache.cs`：先 `offeredMapViews[ViewId] = ...`、增加 `retainedMapViewBytes`，再 `Send(peer, "map.cache-query", query)` | 未成功入队时仍占 8 个/玩家、房间 256 MiB 准备预算，等待 60 秒回收；其它有效请求可能被背压拒绝 | 必须验证首发失败能撤销**本次同一 ViewId** 的预留，不能误删嵌套回调生成的新视图 |
| **客端 → 房主** `ReceiveMapCacheQuery` | 先 `waitingMapCaches.Add(ViewId,...)` / `waitingMapCacheSeconds.Add` 与 `EnqueueLoadingMessage(CacheViewId)`，再 `Send("host", "map.cache-offer", offer)`；重复同 ViewId 直接 `return` | 首次 offer 入队失败后，没有明确同 ViewId 的自动重试；重复 query 不重发 offer，FIFO 占位等待超时 | 成功/失败必须维持单个 FIFO 槽；失败可限频重试，不擅自恢复过期任期 |
| **房主应答客端回退** `ReceiveMapCacheRetry` | `retriedCacheViews.Add(key)` 先于 `SendMapView(...)`；重复 key 被拒绝 | 房主二次 query 出站失败时，同一允许的恢复请求仍被标为已用，重发可能被错误拒绝 | 按已完成入队/等待中/失败区分状态，保留一次性恢复与防全量重传风暴 |
| **客端缓存解码失败** `StageNetworkMapView` 的 `catch` | `retriedCacheViews.Add("local:" + ViewId + ":cache")` 后先 `Send("host", "map.cache-retry", ...)`，成功后才 `RemoveMapCachePlaceholder`、`AddSeedRecoveryBarrier` | 该 Send 抛错时，已占用重试键但 FIFO 槽及等待表仍在；通用消息异常日志不等于可靠重试 | 失败须保留当前有效状态并在有界时间内重发；成功入队后再执行与之匹配的占位/屏障迁移 |
| **客端 SEED 全地形回退** `RetryFullTerrain` | 先 `fullTerrainRetries.Add(ViewId)`、`requiresFullTerrain.Add(MapId)`、`AddSeedRecoveryBarrier(view)`，之后发送 `map.cache-retry(FullTerrain=true)` | 发送异常后本地已进入“用过恢复机会/需要全地形”的状态，是否还能继续合法恢复未被该方法显式保证 | 只允许从房主真实完整状态恢复，不能解除模拟冻结或重放旧请求 |

### 与既有 BUG 的区别

- [BUG-24](BUG-24-地图重同步发送失败.md) 是普通 `resync` 请求（`MapViewLoading.RequestMapResync`）的出站门禁；工作树已有未提交的 `MapResyncRetryGate`。**本卡检查不同的 `map.cache-query / map.cache-offer / map.cache-retry / view` 协商事务**，不要新增第二份普通 resync gate。
- [BUG-14 实机回归](BUG-14-跨机运行回归.md) 对应房主**缓存 offer 超时之后通知/断开失败**是否阻塞房主 Tick。本卡检查**超时之前消息本身未成功入队**的状态回滚与收敛；仍需联合测试，但不是将 BUG-14 重新打开。
- [BUG-25](BUG-25-银河全量重传滥用.md) 已对 `galaxyReset` 做定向退避，不是地图缓存协商协议的解决方案。

## 3. 必须遵守的不变量

1. **一请求一槽：** 同一 `WorldId / SessionId / MapId / ViewId / AuthorityEpoch / Revision` 的合法协商不创建重复有序 FIFO 占位，不绕开 `ValidateLoadingView`、`ReceiveMapView` 和房主版本水位。
2. **仅对真实入队计为发送：** `Send` 发生队列满、连接关闭或编码异常时，不得永久锁死“已发送”“已使用恢复机会”状态；同样不能每帧无冷却重发制造大地图发送风暴。
3. **无重复资产：** 缓存与 SEED 是派生传输，不是新的地图/资产权威；不能因重试重复生成雇员、转移物资、推进个人 Revision 或另开模拟者。
4. **收口有界：** 已有 8 个待缓存视图上限、房主 256 MiB、客端 FIFO 容量、60 秒失败边界继续有效；断线、退出或任期变化后清理与当前请求一致的状态和保留字节。
5. **分清成功入队和真实应用：** 入队成功并不能保证客端已完成 `view` 验证与原生 `ApplyMap`；若协议需要新回执必须先评审协议兼容，不能伪造 `Applied/Saved`。

## 4. 建议实现路径（需原 Owner 审核）

### A. 最小局部补偿（建议优先考虑）

- 用**同一 ViewId 对应对象身份**在出站异常时撤回房主 `offeredMapViews` 和累计字节；不要用全局清空处理一个失败 peer。
- 对客端 `map.cache-offer`、`map.cache-retry` 引入**显式“待发送—已入队—等待完成/失败”状态**，仅有限次数和退避重试，保留原消息顺序与当前合法租约。
- 对房主 `retriedCacheViews` 分离“已受理重试意图”与“已经成功入队恢复 query”；失败仍允许后续合法重试，但不允许超过同一方式的恢复额度。
- 约束发送异常捕获范围：只将已知传输队列/断链异常当作可重试，编码/合同校验错误必须按真实错误暴露并安全终止；恢复失败仍应明确告知用户，而不是静默卡加载。

### B. 统一缓存协商状态机（备选）

- 若多处补偿难以证明一致性，再考虑以 `ViewId` 为键统一组织缓存事务及定时器，保留原 `WorldId / MapId / Epoch / Revision` 校验和唯一 FIFO 槽。
- **优点：** 统一管理成功、超时、重试与释放；**缺点：** 需要跨多个历史混合脏文件，回归和协议兼容风险高。未证明必要性前不应为“优雅”重构整个地图加载链。

## 5. 故障注入与验收（全部待执行）

| 编号 | 故障位置与注入方式 | 必须观察/断言 |
| --- | --- | --- |
| C27-01 | 房主 `map.cache-query` 首发在 TCP/Steam 队列满时抛 `IOException` | 保留字节与 ViewId 的占用正确回滚；合法其它玩家的视图不受影响 |
| C27-02 | 客端收到一次 query，offer 出站首发抛错，再重复收到相同 query | 只维持一个 FIFO 槽，能补发真实 offer；无需等 60 秒直接重进 |
| C27-03 | 恢复请求已验证，但房主 `SendMapView` 在发下一次 query 时抛错 | 合法同键重试不会误判“额度已经用尽”，重复请求有界且不能变成无界全量 |
| C27-04 | 客端损坏一个缓存 chunk，`map.cache-retry` 首发入队失败 | 不误清/重复 FIFO 槽；后续一次合法恢复能取房主原始完整图，旧视图无法越过前序事务 |
| C27-05 | SEED mismatch 触发 `FullTerrain=true`，发送异常后重试 | 仍停用不可信地形并请求真实完整状态；不重复原生 Apply 或无限递归 |
| C27-06 | 断线重连、房主换模拟者/AuthorityEpoch、旧 ViewId 晚到 | 旧会话与旧任期请求无法污染新地图；状态/字节/重试键完整释放 |
| C27-07 | 2～4 人：拥塞者 A、正常 B/C 同时查看/操作其他地图 | 正常玩家继续 Tick/同步/保存；失败协商最多影响当次地图或断开拥塞 peer |
| C27-08 | 超过容量、超时、重复恶意 offer/retry 大量输入 | 重试、字节和项目计数均保持上限；没有反复全量或缓存内存泄漏 |

**证据分层：** 静态调用链 → 链接真实生产方法的假传输故障注入 → 原 Owner 签收并从干净 Git 检出重建 → P3/Steam/TapTap 各自真实引用 BuildOnly → 用户执行 TCP/Steam 双端或 3 人游戏回归。仅本地 helper 通过不代表跨端加载/本体运行通过。

## 6. 工作范围、阻塞与交接

- **候选文件（先由原 Owner 确认）：** `src/Session/MapSnapshotCache.cs`、`src/Session/MapCacheExpiry.cs`、`src/Session/MapViewLoading.cs`、`src/Session/Authority.cs` 的 `RetryFullTerrain` 调用；若需要测试新建 `tools/MapCacheNegotiationChecks/`，必要时复用既有地图重同步测试。
- **Transport 只读协作：** `src/Transport/TcpTransport.cs::Send`、`SteamTransport.cs::Send` 的异常语义；除确认必要接口问题外不动传输层，避免扩大代码范围。
- **协调条件：** [AGENT-COORDINATION.md](../../AGENT-COORDINATION.md) 仍存在停写/未签收记录，[R13-01](R13-01-源码Owner签收.md) 与 [R13-01B](R13-01B-20261019-构建源码依赖闭包与Owner签收.md) 要求逐 hunk 接管。后续开发须主控协调，不能把本卡当作解除冻结或接管其它 Agent 源码的许可。
- **提交与版本：** 实际独立 BUG 修复按 `GIT-COMMIT-POLICY.md` / `Agent.md` 处理版本、CHANGELOG 与原子提交；当前二者已有历史未提交改动，不能抢写。未经授权不 push、不安装或替换游戏本体、共享 Harmony 与其它 MOD。

## 7. 归档条件

- [ ] Session/Transport/Game 相应原 Owner 对精确 hunk 与调用契约签收。
- [ ] C27-01～08 的适用生产关联场景执行并有异常前后状态/保留字节记录；处理断线、重复消息和编码错误边界。
- [ ] 干净 Git 构建输入闭包和三端目标 BuildOnly 证据齐全。
- [ ] 用户完成至少 TCP 与 Steam 双端相关缓存/SEED 恢复及正常玩家不受影响的实机回归。
- [ ] 更新此任务卡的真实提交 SHA、版本、测试日志和剩余依赖后，**才可由待办移动到已完成**。

**本轮实际动作：** 仅浏览仓库与代码路径，创建本任务卡。未改业务源码、协议、存档、版本；未运行游戏或故障注入。以上都是条件性静态风险，须验证后才认定为实机 BUG。
