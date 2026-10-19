# BUG-32 / P1 · 个人进度差量首发失败、回执身份与基准关联

> **状态：局部生产源码修复及本机生产 partial 故障注入已通过；完整事务/重连/双端游戏验收仍待办。**
>
> **建档时间：** 2026-10-19。审查起点 HEAD `4776799`，代码子提交 `7101fe256e8191389569bda56666e6defb440a63`。
>
> **建议 Owner：** Session / 个人进度提交；Game / NativePersonalSync 比较基准；Transport / 发送队列拒绝合同。版本、R13 干净源码、三端构建与发布由主控统筹。本卡不解除其它 Agent 源码冻结或替其签收。

## 一、首次发现的真实生产调用链

| 源码位置 | 已确认行为 | 风险或必要条件 |
| --- | --- | --- |
| `src/Game/NativePersonalSync.cs::CapturePersonalDeltas` | 比较 `personalBaselines` 和实时 Native 字段时，直接执行 `previous[field] = after`；构造 `PlayerUpdate` 时以房主上次已确认的 `Context.GetPlayerRevision` 作为 BaseRevision | **即使尚未发送，Native 比较基准也已前移。** 简单清掉 in-flight 并重新 Capture 不一定产生同一差量，不能把“还没入队”错当成“已 ACK” |
| `src/Session/Authority.cs::PublishPersonalUpdatesCore`（修复前） | 客端先 `personalInFlight.Add(update.PlayerId)` 再 `Send("host","personalUpdate",update)` | TCP/Steam 在队列满/关闭时可同步抛 `IOException/InvalidOperationException`；原始待发差量未保存，门禁却被占用 |
| `src/Session/MultiplayerSession.cs` | `PublishLocalMaps`、`PublishPersonalUpdates`、检查点命令、舰战及退出保存流程用 `personalInFlight.Count` 判断能否继续 | 出站拥塞的临时故障可能演变成此客户端持续等待，且不能按新版本安全补做个人增量 |
| `src/Session/Networking.cs::HandleClientMessage("personalAck")`（修复前） | 用 ACK 携带的 `PlayerId` 直接索引 `World.Players`，无条件赋 `Revision` 并 `personalInFlight.Remove` | 未比对本机玩家、当前待确认的 BaseRevision/Revision；晚到/重复旧回执可能释放新事务或回退水位 |
| `Networking.cs::personalReset`（修复前） | Apply 白名单 Native 基准成功后清空个人等待项，但未与新的待发已编码帧联动 | 加入发送重试后必须同时使旧帧失效；原生恢复失败时不能提前释放等待项 |
| `Authority.cs::AcceptPersonalUpdate` | 房主校验 sender/PlayerId、BaseRevision 和连续 Revision；只有 Native 应用完成后发 `personalAck`，再广播其他观察者 | 该逻辑不能被改为“收到就 ACK”；本卡只收口客户端发送和 ACK 门禁，**不改变房主权威应用/保存定义** |

**条件性风险，不是已复现的玩家丢档：** 真实 TCP/Steam 满队列时，客户端捕获基准可能已经前移，但首次 `personalUpdate` 没有入队；若直接从下一帧重新捕获，变化可能丢失。其它 ACK 顺序、重复包和跨版本场景尚需真实网络故障注入，不从静态调用链推断已发生。

## 二、与既有任务的界限

- [BUG-08 · 广播失败阻断 ACK](BUG-08-广播失败阻断ACK.md) 是**房主已提交个人/地图差量之后**，旁观者发送失败妨碍提交者 ACK 的路径。现有 `6efdf27` 子补丁已将 ACK 前置；BUG-32 是**客户端首次入队失败及客户端 ACK 对应关系**，不重复更改房主扇出。
- [BUG-10 · 退出最终检查点](BUG-10-退出最终检查点.md) 需要真实 ACK 而非队列空才能退出。本卡保留 `personalInFlight` 门禁直到 ACK/经验证的 Native reset；60 秒无法入队时 **fail-closed**，绝不伪造退出前已保存。
- [BUG-21 · 跨玩家个人权限](BUG-21-跨玩家个人字段权限.md) 涉及他人个人资产来源授权，不以本卡的 `LocalPlayerId` 回执检查代替完整 Host 身份/字段权限验证。
- [BUG-27 · 缓存协商发送失败](BUG-27-地图缓存协商发送失败与重试收口.md) 只覆盖 map.cache-* 协商；本卡处理有权威后果的 `personalUpdate`，不能将它误当成可丢弃的派生缓存消息。
- [R13-01 源码签收](R13-01-源码Owner签收.md) / [R13-01B](R13-01B-20261019-构建源码依赖闭包与Owner签收.md) 的旧混合未提交源文件仍需原 Owner 逐 hunk 签收，测试通过不能代替统一三端构建或用户实机。

## 三、本轮已提交的局部源码修复

**本地 Git：** `7101fe2`（`fix: retain personal deltas across outbound enqueue failure`）。

1. 新增 `src/Session/PersonalOutbound.cs`（真实 `MultiplayerSession` partial）：捕获后的 `PlayerUpdate` **只编码一次**，在触发 TCP/Steam 同步队列拒绝前记录原消息、SessionId、PlayerId 和独占等待标记。仅针对确认未入队的 `IOException/InvalidOperationException` 每两秒重发**同一 `byte[]`**；绝不重新调用 Native 差量捕获或产生第二个提交编号。成功进入传输队列后停止自动重发，等待权威 ACK。
2. `Authority.cs`：客端原先“先 Add 再直接 Send”替换为 `SendCapturedPersonalUpdate`。房主 `AcceptPersonalUpdate`、广播、协议 Kind 和个人存档结构不变。
3. `Networking.cs`：`personalAck` 现在必须匹配本机 `LocalPlayerId` 和原待发消息的 `BaseRevision/Revision` 才能解除门禁；忽略旧/重复 ACK，匹配但本地 Revision 已更高时不会降级。`personalReset` 先验证本机身份并成功应用真实 `bridge.ApplyPlayerFields`，再撤销旧出站帧和门禁。
4. `MultiplayerSession.cs`：在未缩放时间 Tick 中驱动有界重试；真正离开 Session 时清掉旧帧，不向下一房间跨会话投递。编解码失败不会当作队列拥塞而静默跳过；首次待发保留并锁本机 Native 故障。出站队列持续拒绝 **60 秒** 时保留现场并设置 `nativeFault/Paused`，不宣称资产已发送、应用或保存。
5. 三个已有混合未提交源码 `Authority.cs`、`Networking.cs`、`MultiplayerSession.cs` **仅精确暂存自己的 1、2、2 处窄修改**，新 helper 和测试单独入 Git；未修改 NativePersonalSync、Transport、Core DTO、协议版本、存档格式、VERSION/CHANGELOG、游戏运行副本。

## 四、已执行的故障注入与结果

新增 `tools/PersonalOutboundChecks/`：通过 .NET8 **直接编译生产 `src/Session/PersonalOutbound.cs`**，外层 Transport、Bridge/World、Encode 方法以可控桩提供。故障注入包含：

| 编号 | 本轮局部验证 | 状态 |
| --- | --- | --- |
| C32-01 | 先 IOException 满队列，再 InvalidOperationException 关闭队列；两秒后恢复，直到成功入队保持相同原始已编码帧、一次 Capture 身份和唯一 in-flight | **S1 通过** |
| C32-02 | 旧 ACK/重复 ACK/BaseRevision 不匹配/错误 PlayerId；只有当前版本匹配的 ACK 才释放等待，不降低已有更高 Revision | **S1 通过** |
| C32-03 | 假传输在 `Send` 内同步调用 ACK，后续 Send 返回不复活已释放租约 | **S1 通过** |
| C32-04 | Host personalReset 的 Native apply 失败、外玩家 reset 拒绝、正确本机 reset 成功后清除旧待发帧 | **S1 通过** |
| C32-05 | Native 比较基准已前移后编码发生格式错误，保留租约并 fail-closed；UI 状态提示二次异常仍不解除故障锁 | **S1 通过** |
| C32-06 | 退出房间清理、同一个 Session 对象更换 SessionId 后不重发旧帧、重连后仅发新编码帧 | **S1 通过** |
| C32-07 | 并存第二个个人差量、外玩家提交、null ACK/消息被拒绝；60 秒持续满队列保留事务并暂停而不无限打满队列 | **S1 通过** |
| C32-08 | 12,000 轮确定性随机入队失败次数、分配版本、成功后旧 ACK 和真实匹配 ACK | **S1 通过** |
| C32-09 | 真正整套 `PublishPersonalUpdatesCore` → NativePersonalSync 比较基准 → TCP/Steam 入队和 ACK → 客端/房主资产及 SaveGeneration | **未执行** |
| C32-10 | 真实双端/3 人用户游戏：暂停、地图载入、临时拥塞、断网重连、保存与退出；验证舰队/个人进度/任务/存档没有重复或丢失 | **未执行** |

**运行证据：**

- `dotnet run -c Release --project tools/PersonalOutboundChecks/PersonalOutboundChecks.csproj --no-restore`：**77,839 项通过**。
- 从代码提交的 `git archive HEAD src/Session/PersonalOutbound.cs tools/PersonalOutboundChecks` 导出至被忽略目录 `artifacts/bug32-clean-head/`，独立构建/运行同一测试：**77,839 项通过**。
- `tools/ReliableCommitFanoutChecks`：**30,027 项通过**（已有房主 ACK/旁观者扇出 helper 关联回归）。
- `tools/FinalCheckpointGateChecks`：**53,972 项通过**（退出 ACK 等待门禁）。
- `tools/OptimizationChecks`：**此次未通过编译**，报告缺少 `offeredMapTickSnapshot` / `waitingMapTickSnapshot` 测试依赖字段；是已有地图缓存独立工程与其它 Agent 工作树接线问题，**不得记为通过**，也未为本轮越权修改那些测试源码。

以上 S1 测试是真实新增出站 partial + 假业务/传输，不证明整个 `Authority.cs`、`Networking.cs`、`NativePersonalSync.cs` 或 Unity 游戏已联编并验证。未执行三端官方 `BuildOnly` / 游戏安装或运行。

## 五、尚需原 Owner 收尾与安全边界

- [ ] **T32-1 / Session + Transport：** 验证 TCP/Steam `Send` 的失败保证确实为“同步拒绝前尚未入队”，尤其旧连接、异步关停、同步回调与错误类型。若出站失败可能已入队，不能不带幂等提交标识重复发送，需先设计协议兼容收口。
- [ ] **T32-2 / Session + Game：** 生产链直接故障注入 `CapturePersonalDeltas`：Native 捕获阶段本身可能在更新部分 `personalBaselines` 后抛错，此前还未形成可保留的 `PlayerUpdate`，本补丁**不能恢复这种半捕获状态**；应由 Game Owner 审核全量先验证后提交本地比较基准的可能性。
- [ ] **T32-3 / 端到端可靠 ACK：** **成功入队后**房主执行、ACK 丢失、重连、个人 Reset 与 MapRevision/PersonalRevision 的关联及重复发送幂等仍未解决；现行客户端只对“已证明未入队”的队列失败重试，不能把 ACK 丢失后的再次业务应用当作安全。
- [ ] **T32-4 / 授权与一致性：** 核对旧个人 ACK 及 policy/地图视图改变 Revision 的先后、非本机 `personal` 广播和异常 reset 是否会污染 Native 白名单字段；同步断链不能重复赠送/扣款。严禁弱化 `AcceptPersonalUpdate` 的来源认证、连续 Revision 和真正桥接应用。
- [ ] **T32-5 / 编译与集成：** 原 Owner 签收 `Authority/Networking/MultiplayerSession` 混合 hunk；修好独立 `OptimizationChecks` 缺失快照桩时不得覆盖其 Owner 差异；完成 R13 源码 0U/0M、版本日志一致与三端 `Sync-ModCopies.ps1 -BuildOnly`。只打包，未经明确授权不安装。
- [ ] **T32-6 / 用户运行验收：** 执行 C32-09～10 的 Project3、Steam 与 TapTap 合法联机/跨版本受控负向测试。记录房主/客端同一 PlayerId 的个人 Revision、存档 SaveGeneration、真实 Native 字段及失败前后状态，不能靠 UI Toast 或 .NET 桩标记整套联机通过。

**本轮真实边界：** 仅主仓库内独立子补丁及任务文档；未运行游戏、Play Mode、真实 TCP/Steam 双端，不复制/安装三端 MOD，不更改房主存档、协议、游戏本体、VERSION 或 CHANGELOG，不 push。没有取得原 Owner 的明确签收，**BUG-32 保留在 `agent-tasks/待办/`，不得归档**。
