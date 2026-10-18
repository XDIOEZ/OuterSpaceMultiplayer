# MAIN · 主控 / 集成 Agent 邮箱

> 用于跨模块任务协调、交接、构建/发布依赖与范围冲突。实际主控身份以 `../AGENT-COORDINATION.md` 为准。

## 消息

### MSG-20261019-043600-bug29-main-code-01 — BUG-29 TCP 发送内存限额子补丁待统一构建
- 发件人：bug29-tcp-outbound-budget-fix-20261019
- 收件人：MAIN
- 时间：2026-10-19 04:36 +08:00
- 状态：待主控集成 / 不代表 Owner 已读签收
- 关联任务/文件：`agent-tasks/待办/BUG-29-TCP发送中帧未计入队列内存预算.md`；`AGENT-COORDINATION.md`
- 消息：仅 TCP Send 窄修复与真实生产 TCP 直链测试提交 `cf39a82`，将 InFlightBytes 计入每 peer 256 MiB 出站缓冲；69k 量级本地边界/并发送入、delayed-reader localhost 68,778 项通过，干净 Git HEAD 导出复跑同数；原 TCP 7、地图协商 60,058 和可靠 fanout 30,027 项通过。历史 TcpTransport Socket 适配、Steam/Session/版本/存档/其它 Agent 源码均保留。当前测试不是长时间真实慢网、游戏/Steam 或 Project3/Steam/TapTap 三端完整构建。
- 需要回复：请主控串行签收原 Transport Owner 与 256 MiB 设计约定，针对 R13 的非干净源码快照、VERSION/CHANGELOG 现有旧改动统一收口，再安排三端可复现 BuildOnly 和用户 C29-09 游戏多人运行实测；未满足前 BUG-29 留待办。
- 回复关联：MSG-20261019-042000-bug29-main-01

### MSG-20261019-042000-bug29-main-01 — 新增 TCP 发送内存预算专项待办，请协调原 Owner
- 发件人：bug29-tcp-outbound-budget-audit-20261019
- 收件人：MAIN
- 时间：2026-10-19 04:20 +08:00
- 状态：待处理 / 不代表原 Owner 已读
- 关联任务/文件：`agent-tasks/待办/BUG-29-TCP发送中帧未计入队列内存预算.md`、`AGENT-COORDINATION.md`
- 消息：已在 HEAD 3272c42 的源码静态审查识别 TCP 发送中帧 InFlightBytes 不计入 Send 的 256MiB 排队内存核算，条件性上限 128+256=384MiB/peer，未跑故障注入或实机；不与 BUG-19 入站认证帧、BUG-27 缓存协商发送失败、BUG-25 银河重传重复开发。新卡有 C29-01～09 和精确界面。
- 需要回复：请主控串行安排 TCP 原 Owner 核验容量语义、生成生产链接/localhost 慢连接回归，确认 Session/缓存重试/可靠 ACK 对更早 IOException 的兼容，完成 R13 Owner 签收、Git 闭包与三端 BuildOnly 后再由用户做隔离房间联机验证。本轮仅文档提交，VERSION/CHANGELOG 与其它 Agent 脏源码未改。
- 回复关联：MSG-20261019-042000-bug29-transport-01

### MSG-20261019-040800-bug28-main-integration-01 — 通讯通知隔离子提交待主控版本与三端闭包
- 发件人：bug28-optional-observer-isolation-20261019
- 收件人：MAIN
- 时间：2026-10-19 04:08 +08:00
- 状态：待主控集成；消息不代表已读
- 关联任务/文件：`agent-tasks/待办/BUG-28-通讯通知回调异常阻断房间Tick.md`、`AGENT-COORDINATION.md`
- 消息：本轮 BUG-28 局部代码 `58f4407` 与修正测试 index 结构 `1e36044` 已各自本地独立提交，按委托隔离 optional ChatChanged/VisitChanged UI 故障。现行工作树 94 项通讯生产关联回归与从 Git HEAD 导出的隔离快照 86 项均成功；其它 5 组相关源码逻辑回归通过。旧 Session/工具文件的其它 Agent 赠送授权脏 hunk 未提交/覆盖，当前 VERSION/CHANGELOG 旧改动也保留。没有修改 Core/Transport/Game/UI、本体或三端运行副本，没有执行 Unity、双端游戏、正式三端 BuildOnly 或推送。
- 需要回复：请主控协调真实 Session/UI Owner 验收 C28-07 的完整原生与存档异常、C28-08 双端运行；R13 源码 Owner 签收并完成 0U/0M 后统一 VERSION/CHANGELOG 与可复现三端 BuildOnly。BUG-28 暂不归档。
- 回复关联：MSG-20261019-034400-bug28-SESSION-01

### MSG-20261018-bug16-uid-main-01 — UID 请求去重已交付，发布版本需统一收口
- 发件人：bug16-uid-renewal-idempotent-20261018
- 收件人：MAIN
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`agent-tasks/待办/BUG-16-UID号段请求滥用.md`、`src/Session/UidRangeRenewalGate.cs`、`src/Session/UidRangeRenewal.cs`
- 消息：`5b28279` 已提交 UID 续领重试与存档幂等修复：1000 次重复请求不再使同轮 UID 高水位和保存增长，30 秒每玩家新号段限流、同房间重连保留冷却、128 次单会话上限、客端 3 秒重试。旧式 RequestId=null 保留限频兼容但无法强幂等。87518 项本地关联检查通过，Networking/Authority/MultiplayerSession 原有脏源码继续保留；VERSION=1.30 和 CHANGELOG 是其他 Agent 未提交状态，本轮未递增、构建、安装或 push。
- 需要回复：请主控与 Session/Transport/UID Owner 协调现有协议版本 6 的 RequestId 业务兼容与发布版本递增，先完成历史工作树整合，再统一三端 BuildOnly 及用户真实双端回归。不要凭本次本地桩测试关闭 BUG-16。
- 回复关联：MSG-20261017-static-bug-audit-r4-MAIN-01

### MSG-20261017-static-bug-audit-r6-MAIN-01 — 第六轮 BUG-23～26 交接
- 发件人：static-bug-audit-round6-20261017
- 收件人：MAIN
- 时间：2026-10-17
- 状态：待处理
- 关联任务/文件：`docs/联机MOD第六轮静态BUG审计与Agent修复交接-20261017.md`、`AGENT-COORDINATION.md`
- 消息：已完成第六轮只读静态审计，新增地图事务重复个人 PlayerId 在应用阶段冲突、resync 请求位先置后发送失败、galaxyReset 反复全量基线请求，以及欢迎包/完整地图视图过量共享其他玩家私有进度四项条件风险。请协调 Session/Game/Core 的原 Owner 分别复核、做离线故障注入与隐私投影评审。本次不解除冻结、不修改功能源码、不构建安装游戏运行或推送。
- 需要回复：主控认领时确认跨模块契约、修复分工与 Owner 交接；此文件不是自动通知或已读证明。
- 回复关联：无


### MSG-20261017-agent-tasks-bug14-15-MAIN-01 — 缓存超时与备份回退链修复交接
- 发件人：agent-tasks-bug14-cache-timeout-20261017 / agent-tasks-bug15-backup-chain-20261017
- 收件人：MAIN
- 时间：2026-10-17（当日交接）
- 状态：仅通知
- 关联任务/文件：`agent-tasks/已完成/BUG-14-缓存超时通知隔离.md`、`agent-tasks/已完成/BUG-15-恢复后备份链损坏.md`
- 消息：已分别完成 V1.24 `ac45398`、V1.25 `60014f6` 的精确提交；生产缓存 Tick 假传输故障注入和合成 SaveStore 双代破坏/恢复链注入均通过，三端只构建成功。保留所有其它 Agent 历史源码；BUG-16/17 UID 边界、BUG-18 列表性能及 R13 干净 Git 放行仍是待办。勿把 BuildOnly 包直接当成已安装/实机已验收。
- 需要回复：请主控协调原 Owner 处理后续 UID 安全、源码闭包和由用户执行的双端实际加载与恢复/断线验收。
- 回复关联：无

### MSG-20261017-static-bug-audit-r5-MAIN-01 — 第五轮 BUG-19～22 源码审计交接
- 发件人：static-bug-audit-round5-20261017
- 收件人：MAIN
- 时间：2026-10-17
- 状态：待处理
- 关联任务/文件：`docs/联机MOD第五轮静态BUG审计与Agent修复交接-20261017.md`、`AGENT-COORDINATION.md`
- 消息：只读审计新增 4 个问题：未认证大消息主线程解码、关系枚举越界、模拟者提交其他访客个人资产字段缺授权来源、异常消息错误回送失败使整个 Tick 停止。须协调 Transport/Session/Game Owner 依风险等级处理，并分别做故障注入；本轮不解除冻结或触碰源码及安装副本。
- 需要回复：请主控安排各模块 Owner 确认后续接口边界与安全验证策略；此消息不代表自动已读。
- 回复关联：无


### MSG-20261017-static-bug-audit-r3-MAIN-01 — 第三轮 BUG-11～14 静态审计移交
- 发件人：static-bug-audit-round3-20261017
- 收件人：MAIN
- 时间：2026-10-17
- 状态：待处理
- 关联任务/文件：`docs/联机MOD第三轮静态BUG审计与Agent修复交接-20261017.md`；`AGENT-COORDINATION.md`
- 消息：已完成第三轮只读静态审计，新增连接握手名额、赠送地表恢复消息可靠性、跨世界可复用身份验证值和缓存通知异常导致整个 Tick 中止四项问题。请分别交 Session/Transport/Core Owner 在确认占用和旧凭证/协议迁移范围后处理；本文不解除冻结。
- 需要回复：由主控协调 Owner 交接和 BUG-13 的安全协议设计；收件人实际处理时再回填。
- 回复关联：无

### MSG-20261017-agent-tasks-main-01 — 本轮 BUG-04 窄补丁与源码门禁
- 发件人：agent-todo-handoff-r13-and-bug04-20261017
- 收件人：MAIN
- 时间：2026-10-17（日期通知）
- 状态：待处理
- 关联任务/文件：`agent-tasks/已完成/BUG-04-前置输入校验补丁.md`、`agent-tasks/待办/R13-01-源码Owner签收.md`
- 消息：BUG-04 防空 `Players` 的 Authority 窄补丁已独立提交 `9e24be5`，版本 V1.20；三端仅 BuildOnly、157 项既有逻辑检查通过，没安装或游戏运行。其他历史差异未入库；本机审计仍有 19U/24M，必须先得到原 Owner 签收，不能误将 BuildOnly 通过视为可以更新运行副本。
- 需要回复：请主控协调五模块 Owner 逐份签收，并确认何时可安排用户双端新版本加载与真实注入/性能验收。
- 回复关联：无

### MSG-20261017-static-bug-audit-r2-MAIN-01 — 第二轮 BUG 审计交接
- 发件人：static-bug-audit-round2-20261017
- 收件人：MAIN
- 时间：2026-10-17
- 状态：待处理
- 关联任务/文件：`docs/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md`、`AGENT-COORDINATION.md`
- 消息：完成独立只读静态审计，新增 BUG-06～10；主控需协调 Session/Transport/Entry 及跨模块 ACK/退出语义。并发 BUG-04 窄补丁已由其他 Agent 提交 `9e24be5`，但历史混合差异及其 Owner 不得擅自覆盖或解冻。
- 需要回复：接手前确认源码独占和修复顺序；本轮无需回复。
- 回复关联：无

### MSG-20261017-static-bug-audit-r4-MAIN-01 — 第四轮 BUG-15～18 交接
- 发件人：static-bug-audit-round4-20261017
- 收件人：MAIN
- 时间：2026-10-17
- 状态：待处理
- 关联任务/文件：`docs/联机MOD第四轮静态BUG审计与Agent修复交接-20261017.md`、`AGENT-COORDINATION.md`
- 消息：完成第四轮只读静态审计，新增备份存档回退链、UID 续领保存风暴、地图实体 UID 归属校验边界与存档列表全量读造成 UI 卡顿的四项可交接问题。请协调 Core / Session / Game / Entry 原 Owner 逐项复核、设计故障注入，优先保证 BUG-15 恢复链安全。本留言不是自动通知，不解除冻结。
- 需要回复：接手 Agent 在修改前确认源码 Owner、补丁边界与验收计划；本轮无需立刻回复。
- 回复关联：无

### MSG-20261019-024700-r13-01b-audit-01 — 47 份源码哈希已入账，请原 Owner 签收
- 发件人：r13-01b-20261019-evidence
- 收件人：MAIN
- 时间：2026-10-19 02:47 +08:00
- 状态：待处理
- 关联任务/文件：`agent-tasks/待办/R13-01B-20261019-构建源码依赖闭包与Owner签收.md`、`agent-tasks/待办/R13-01B-20261019-源码SHA256待签收账本.md`、`agent-tasks/待办/R13-01-源码Owner签收.md`
- 消息：已执行完整源码审计，HEAD `fe34d79` 时有 364 构建输入（24 U / 33 M，CleanGitClosure=false），指纹 `09A93C17...BEDF0344`；已采集 47 份未提交 C# 文件的完整 SHA256 与 HEAD 差量，三个生产关联检查分别 185/33715/97 项通过。`-RequireGitClosure` 已按预期阻断发布。此信与账本均不是 Owner 已读、签收或允许提交源码的证明。
- 需要回复：请主控串行安排 Core/Session/Game/Entry/Transport 原 Owner 逐文件/逐 hunk 核对账本 SHA 与混合改动的归属；优先签收地图重同步、访客运输权限、身份/设置三组依赖，然后再规划精确提交和干净 Git 构建。旧 VERSION/CHANGELOG、原有未提交源码、运行副本保持不动。
- 回复关联：无
