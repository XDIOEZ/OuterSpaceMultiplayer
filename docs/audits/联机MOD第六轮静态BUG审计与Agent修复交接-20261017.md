# 联机 MOD 第六轮静态 BUG 审计与 Agent 修复交接

> 日期：2026-10-17
> 唯一主仓库：`C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer`
> 起始代码基准：`HEAD=96a77c5`，`VERSION=1.25`，实际读取的是**包含其他 Agent 未提交/未跟踪源码的工作区**；审计过程中并发 Agent 推进到 `410c340`／V1.26，已提交 BUG-22 的异常回送隔离窄补丁。文中证据均需接手者以当时最新源码重新核对，**不是对三端已安装 DLL 的结论**。
> 工作性质：**仅读业务源码；新增本交接文档与协作/邮箱记录，不改功能代码、不构建安装、不启动游戏、不同步运行副本、不推送、不对真实玩家数据做攻击/破坏测试。**

本文件只新增 **BUG-23～26**；既有 [第一轮 BUG-01～05](联机MOD静态BUG审计与Agent修复交接-20261017.md)、[第二轮 BUG-06～10](联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)、[第三轮 BUG-11～14](联机MOD第三轮静态BUG审计与Agent修复交接-20261017.md)、[第四轮 BUG-15～18](联机MOD第四轮静态BUG审计与Agent修复交接-20261017.md)、[第五轮 BUG-19～22](联机MOD第五轮静态BUG审计与Agent修复交接-20261017.md) 均**不重复计数**。

## 发现一览及证据分级

| ID | 优先级 | 类别 | 静态结论 | 验证边界 |
| --- | --- | --- | --- | --- |
| **BUG-23** | **P1** | 地图/个人事务原子性 | 同一 `MapUpdate.Players` 可含相同 `PlayerId` 的多条差量；预检全部通过后，顺序应用时可能发生部分写入再失败 | 需要构造带重复人员的异常事务，常规客户端不会主动生成 |
| **BUG-24** | **P1** | 地图缺包重同步恢复 | `requestedMapResyncs` 在 `Send("resync")` 前锁存；发送失败后不清标志，后续恢复请求不会重发 | 需要模拟客户端发送失败而房主连接未被断开 |
| **BUG-25** | P2 | 银河全量重传 / 资源放大 | 已认证客户端反复 `galaxyReset` 可不断清除本人的同步基准，并触发房主重新编码全量舰队/共享世界 | 资源开销有上界帧节奏，但无请求级速率控制；需压测 |
| **BUG-26** | P2 | 多玩家数据最小化 / 隐私 | `welcome.PublicWorld` 广播给每个来宾的世界副本仍携带**其他玩家**的原生存档快照；访客地图视图还含其他在场玩家的完整个人白名单字段 | 是否视为越权泄露取决于明确的隐私需求；过量数据确实在负载里 |

**证据标准：**“源码路径成立”表示已核对相关方法与调用次序，**不等于已经在游戏内复现故障或发生安全事件**。P1 是潜在权威完整性/恢复性级别，P2 是需要量化或补充隐私威胁模型的风险。本轮没有断言所有条件都可在未修改的正常客户端 UI 中发生。

## BUG-23：重复 `PlayerUpdate` 在部分写入后才发现冲突

**主责建议：Session/Authority + Game/NativePersonalSync。状态：待修复与故障注入。**

### 直接证据

- `src/Core/Contracts.cs:136-151`：`MapUpdate.Players` 是 `List<PlayerUpdate>`，无键唯一性约束。
- `src/Session/Authority.cs:403-415`：房主在 `AcceptMapUpdate` 内逐条检查该条更新的 `PlayerId`、访问者关系及 `BaseRevision/Revision`；并没有检查列表中 `PlayerId` 不重复。
- `src/Game/GameBridge.cs:365-391`：原生桥先 `replicator.Validate(update)`，随后 `update.Players.Select(ValidatePersonal).ToList()` **全部基于应用前的玩家状态预检**；之后先 `replicator.ApplyDelta`，再逐个 `ApplyPersonal`。
- `src/Game/NativePersonalSync.cs:73-94,96-116`：`ValidatePersonal` 校验 `Before/After` 与当前值一致；`ApplyPersonal` 在设置字段前再次检查当前值，并用反射 `field.Field.SetValue(player,field.After)` 修改原生玩家状态。
- `src/Session/Authority.cs:422-444`：一旦 `bridge.ApplyMapUpdate` 返回非 `NativeUnchanged` 错误，房主会 `LockNativeFault`，因为地图/人物可能已经部分变更。

### 触发与后果

当前地图模拟者发送一个其他字段均合法的异常 `MapUpdate`，其中含**两条相同 `PlayerId`、相同 `BaseRevision/Revision`、但对同一字段设置不同 `After` 的 `PlayerUpdate`。房主每条版本检查都可能通过；原生批量预检均针对初始 `Before` 通过。随后地图差量已应用，第一条个人差量将值从 `V0` 改为 `V1`，第二条再执行时发现当前值 `V1` 既非自己的 `Before=V0` 也非 `After=V2`，于是报错并使房主故障锁定。这个失配是**同一接收事务内部自相矛盾**，与第一轮 BUG-04 的空 `Players`、第五轮 BUG-21 的跨玩家授权属于不同检查维度。

### 修复建议与验收

在**所有原生应用之前**要求每个 `PlayerId` 在 `MapUpdate.Players` 中最多出现一次；若业务确需同一人多组字段，先严格合并到一条拥有唯一修订号的事务并验证无字段冲突。不要为了避免故障锁而把部分应用后的错误当作 `NativeUnchanged`。

验收用合成独立世界构造：①正常单个人员差量；②两个不同玩家各一条；③同一玩家重复且相同字段 `After` 冲突；④相同玩家重复、不同字段；⑤不同版本/空项。③④⑤ 应在**地图/资金/事务版本的任何原生写入前**明确拒绝。正常①②的 ACK/Revision 逻辑不能回退。

## BUG-24：地图重同步“已请求”状态先于发送成功

**主责建议：Session / MapViewLoading + Transport。状态：待修复与故障注入。**

### 直接证据

- `src/Session/MapViewLoading.cs:202-206`：`RequestMapResync` 使用 `requestedMapResyncs.Add(mapId)` 成功就立即标记为已请求，再调用 `Send("host","resync",...)`；没有 `catch`／清理回退。
- `src/Session/MapViewLoading.cs:207-225`：`WaitForMapResync` 建立 `loadingResyncs` 队列、累计要求的地图/玩家修订，然后调用 `RequestMapResync`；其后再次请求会因 `Add(mapId)==false` 直接返回。
- `src/Session/MapViewLoading.cs:232-245`：等待完整地图超过 `MapLoadingTimeoutSeconds=60` 时进入错误/收口路径。
- `src/Transport/TcpTransport.cs:222-232`、`src/Transport/SteamTransport.cs:155-161`：客户端发包可能因连接状态或发送队列满抛异常；一次 `Send` 的抛错并不等于房主已经得到请求。
- `src/Session/MapViewLoading.cs:120,332-337`：`requestedMapResyncs` 的清理主要在已成功应用完整视图或手动清理加载状态，不能处理这次“标记已入表但未发送”的即时错误。

### 触发与后果

客户端丢失某一地图差量基准 → `WaitForMapResync` 认为需要 `resync` → 请求 ID 先放进 `requestedMapResyncs` → 客户端向房主发送 `resync` 时恰好抛 `IOException` → 标记仍在，下一份同地图更新即使再次发现版本缺包，`RequestMapResync` 也不会发送第二次。最终可能等待到 60 秒超时、进入地图加载故障锁。**这是客户端局部重同步请求状态机缺陷，不是第三轮 BUG-12 的 `gift.resume` 权威恢复消息故障。**

### 修复建议与验收

采用“排队成功后才持久标记 + 明确 timeout/重试窗口”，区分未排队、已排队但未收到 view、收到不覆盖最新修订的旧 view；重试必须幂等，重连与任期变化清旧请求。不要用无上限循环发送 `resync` 导致另一侧全量地图风暴。

用假传输模拟首次 `Send(resync)` 抛 `IOException`、第二次成功、房主返回完整视图、房主返回过期视图及连续失败。首次失败后必须还允许后续有界重试；成功覆盖后清理 `requestedMapResyncs/loadingResyncs`；超时提示保留但不在一次临时队列拥塞时直接永久阻断。

## BUG-25：`galaxyReset` 无限重建全量基线的请求级门禁

**主责建议：Session / Networking + Game / NativeFleetSynchronization。状态：代码控制缺口确认；严重度待基准。**

### 直接证据

- `src/Session/Networking.cs:148-153`：对**已认证**客户端的 `"galaxyReset"` 直接调用 `bridge.ResetGalaxyRecipient(playerId)`，没有按玩家限流、待恢复标志或冷却门禁。
- `src/Game/NativeFleetSynchronization.cs:157-165,210-211`：`ResetGalaxyRecipient` 删除该玩家 `galaxyRecipients` 中的序号/已知内容、强制 `fleetSampleFrame=-1`、`nextSharedCapture=0`，下一次 `CaptureFleetFrame` 将新建 `GalaxyRecipient` 且 `Full=true`。
- `src/Session/MultiplayerSession.cs:399-416`：房主约每 0.5 秒对所有 peer 调用 `bridge.CaptureGalaxy(peer.Value)` 并发送 `galaxy`；重置的玩家会再次收到完整基线，重建采样状态还可能增加与其他 peer 共用的 CPU 成本。
- `src/Session/Networking.cs:350-363`：正常客户端只在同步错误时请求 `galaxyReset`，但房主无法从当前 `galaxyReset` 入口区分正常缺包和反复请求者。

### 触发与影响

已认证但异常的客人反复发送 `galaxyReset`，房主不断放弃对该客人的增量基线，下一周期重新编码完整舰队/共享世界；当共享世界大、玩家多时可能显著增加序列化时间和带宽。**房主有 0.5 秒的银河广播节奏，不能声称单一客户端能无限次每帧发送完整包；问题是每周期持续被迫发送较大的全量包及反复失去基线。** 这不同于第四轮 BUG-16 的 UID 号段+存档 IO 风暴。

### 修复建议与验收

只允许每用户单个有界的“待全量重同步”状态，增加请求级频率限制和正常基线修复的 ACK/完成水位；已经安排全量传输时重复请求应幂等，不得再重置跨所有接收者共享的采样缓存。测试连续发送 1、10、100 次 reset，在相同模拟世界规模下记录 10 秒内 full frame 个数、字节数、房主最长 `Tick` 和其他玩家的延迟；正常一次缺包恢复必须能成功。

## BUG-26：`welcome` 与完整地图视图携带其他玩家的私有进度快照

**主责建议：Core/Session 协议投影 + Game/NativePersonalSync。状态：负载过量共享成立；是否违反隐私规则待产品/用户定界。**

### 直接证据

- `src/Core/Contracts.cs:74-105,429-449`：`WorldState` 包含所有 `Players`、`Snapshot` 和完整地图/世界状态；每个 `PlayerRecord.Snapshot.NativeJson` 存在独立 `FWGameData` 原生序列化内容。
- `src/Game/GameBridge.cs:256-276`：`CaptureWorld` 将其他玩家的完整 `PackPlayerData` 保存在 `SavedWorld.Players` 中；`CapturePlayer` 也会把该玩家原生数据写入 `PlayerSnapshot.NativeJson`。
- `src/Session/Networking.cs:268-278`：`PublicWorld(recipient)` 从 `JsonData.Clone(World)` 开始，仅清理 `Transactions` 和每名玩家的凭证字段、UID 号段；**没有清理其他玩家的 `Snapshot` 或 `World.Snapshot`**。
- `src/Session/Networking.cs:256,298-313`：房主经 `welcome` 将整个 `PublicWorld` 交给新加入者，其客户端随后解包并遍历玩家记录、应用全部原生玩家数据。
- `src/Session/Authority.cs:244-265`：给某张地图的访客发完整视图时，附带该图访客及拥有者的全部 `PlayerFieldState`，不是仅该接收者本人。
- `src/Game/NativePersonalSync.cs:29-31,120-126`：这份 `NativeFieldsJson` 包括 `creditPoint`、`techonologys`、任务、虚拟商店/库存等白名单字段；目前没有对不同接收者的字段做最小化投影。

### 风险与限定

一个**合法加入房间**的玩家可在已接收的协议负载/本地内存中拿到其他玩家的部分或全部原生进度，而不仅是进入同一地图所必须显示的实时角色/建筑信息。这不等同于凭证泄露：`CredentialId/Hash` 已被清理；也不是第五轮 BUG-21 的**修改**他人个人状态漏洞。本问题是数据最小化/保密范围与联网带宽的设计风险。

现有原生 `GameBridge.LoadWorld` 需要共享世界及多名玩家容器以恢复玩家舰队/人物投影；**不可**直接把 `World.Snapshot` 或所有其他 `PlayerSnapshot` 置空，否则很可能导致已有加载链路崩溃。需先和用户确认是否要求玩家资金、任务、仓库、科技等仅自己/房主可见，再做协议投影设计和旧档/协议版本兼容。

### 修复建议与验收

以“房主真实档保留全量、网络最小必需投影”分离：仅给来宾传共享模拟/显示必需的玩家副本，私有经济/任务/技术细节按本人授权发送；跨玩家工作所需的计算输入考虑经房主验证的派生状态而非原始全文。对现有 `FWGameData` 序列化/反序列化依赖做字段级差异审计和安全兼容策略。

验收需同时确认：①正常新来宾可以加载、看到其他舰队和人物；②非本人未经授权看不到其他玩家私人 `creditPoint`、任务和仓库值（包含 `welcome`/地图视图两条通道）；③本人仍能加载完整个人进度；④已有人物的参观图实时更新正常；⑤不同 Steam/TapTap/P3 组合的协议一致。仅使用生成的虚拟账号和合成数据，不在报告附真实个人记录。

## 接手顺序、独占与回填

1. **BUG-23 优先**：与第一轮 BUG-04 输入校验、第五轮 BUG-21 来源授权一起做前置幂等/唯一性审查，但修复点独立，防止“半应用后仅锁住”的异常事务。
2. **BUG-24 第二**：恢复路径属于双端地块一致性的关键控制流；与地图缓存容错和有界重试协同，先做本机失败注入。
3. **BUG-25 第三**：结合房主银河数据实测采样，建立真正的请求限流，不得影响正常缺包补救与当前 Steam/TapTap 联机。
4. **BUG-26 单独设计评审**：先确定玩家隐私协议和最低加载字段集合，再改投影；不以“单方隐藏 UI 显示”代替网络层数据控制。
5. `src/Session/Authority.cs`、`MapViewLoading.cs`、`Networking.cs`、`GameBridge.cs`、`NativePersonalSync.cs`、`NativeFleetSynchronization.cs` 等源码与主仓库其他 Agent 原历史修改可能重叠。**本次只占用新报告/协作行/MAIN 留言。原 Owner 未交接之前不允许抢写或解除冻结。**
6. 逐项记录独立 Git commit、VERSION/协议/存档影响、离线假传输与预检测试、P3/Steam/TapTap BuildOnly、磁盘安装与真实运行 SourceRevision、由用户执行的游戏/双端验收。**静态发现与构建通过不能宣称实机已修复。**

### 未来 Agent 修复回填

```text
BUG-ID / 代码最新版本 / 原 Owner 签收：
根因复核和是否可达（含反证）：
最小合成测试输入 / 预期 / 实际：
修复文件、独立 Git 提交、版本与兼容：
异常/重复/断网用例、逻辑回归：
Project3 / Steam / TapTap BuildOnly：
真实磁盘 SourceRevision / 游戏运行 SourceRevision：
用户双端验收记录：
剩余风险 / 回滚 / 依赖：
```

**总结：**新增 4 项与既有 BUG-01～22 不重复的静态问题，其中 2 项影响事务/恢复、1 项影响全量同步资源、1 项属于私人进度数据暴露与最小化风险。本轮未进行游戏、PlayMode、跨机联机或真实玩家资料测试，也未修复上述问题。
