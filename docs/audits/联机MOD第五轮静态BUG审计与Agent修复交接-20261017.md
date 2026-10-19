# 联机 MOD 第五轮静态 BUG 审计与 Agent 修复交接

> 审计日期：2026-10-17
> 唯一开发仓库：`C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer`
> 审计起点：`HEAD=5ebd284`、`VERSION=1.23`，基于**包含原有未提交与未跟踪代码的实际工作区**。不是干净提交，不代表 Project3/Steam/TapTap 已安装的 DLL。
> 本轮只读源码与文档交接；**未修改功能代码、未构建/安装、未运行游戏、未针对真实玩家发包或做攻击验证**。

> **并发版本提示：**本轮记录期间其他 Agent 先将 HEAD 推进到 `0cbc2aa`、工作区 `VERSION=1.25`（BUG-14 缓存通知隔离修复与任务归档），随后又提交了 BUG-15 回退指针修复 `60014f6`。这些属于其他独立任务，不代表本轮 BUG-19～22 已修复或通过实机验证。接手者必须以最新源码重新核对。

这是 [第一轮 BUG-01～05](联机MOD静态BUG审计与Agent修复交接-20261017.md)、[第二轮 BUG-06～10](联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)、[第三轮 BUG-11～14](联机MOD第三轮静态BUG审计与Agent修复交接-20261017.md)、[第四轮 BUG-15～18](联机MOD第四轮静态BUG审计与Agent修复交接-20261017.md) 的**增量报告**，新编号仅为 **BUG-19～22**。

## 阅读方式

- **源码确认**：实际调用链与校验缺口可由所列工作区源码定位；**条件触发待测**：尚无游戏内、网络故障注入或真实对抗性复现记录。
- 本轮含授权/安全边界问题。其风险**不是**声称普通客户端的正常操作一定会触发；相关情况需要已认证的异常/改动客户端，或会话层未认证的异常报文。
- P1 表示若触发可能影响全房间可用性或跨玩家权威；P2 表示可能导致业务状态不一致。严重度不等于发生频率。
- 接手前读 `AGENTS.md`、`Agent.md`、`docs/plans/联机方案 V1.md`、`AGENT-COORDINATION.md`、`GIT-COMMIT-POLICY.md` 和角色邮箱，并重新核对最新源码及文件 Owner。**本报告不解除并行 Agent 的冻结或授予功能文件写入权**。

| 编号 | 优先级 | 模块 | 新发现 | 证据等级 |
| --- | --- | --- | --- | --- |
| **BUG-19** | **P1** | Transport / Protocol / Session | 未认证 peer 可以发送单帧最高 128 MiB 的 JSON，主线程在鉴权前完整解码与分配 | 先解码后认证的代码顺序明确；耗时待测试 |
| **BUG-20** | P2 | Session / Relation / Game | 关系命令未拒绝非法 enum 值，可能令 UI、权限和原生敌对判断互相矛盾 | 缺枚举值校验直接可见；畸形报文待测 |
| **BUG-21** | **P1** | Session / Authority / NativePersonalSync | 客户端地图模拟者可提交**其他在场玩家**的个人资金/科技等字段；仅校验字段与版本，未校验变更来源 | 权威边界确认；受控伪造差量影响待测 |
| **BUG-22** | **P1** | Session / Transport / Runtime | 消息处理异常的错误回送若再次抛异常，会使整个 `Session.Tick` 退出，并由 Runtime 长期停帧 | 异常传播链直接可见；故障注入待测 |

## BUG-19：未认证大消息进入主线程 JSON 解码

**建议主责：Transport + Protocol + Session。状态：待修复与压测。**

### 源码证据

- `src/Transport/TransportQueue.cs:16-17,26-31`：单条消息上限为 `128 * 1024 * 1024` 字节，接收队列上限为 256 MiB；**队列有全局容量门禁，但不是未认证消息的小尺寸门禁**。
- `src/Transport/TcpTransport.cs:165-182`：TCP 收到帧头后，只按上述单帧上限/队列容量预留并读入 `byte[] payload`，随后推入会话队列。
- `src/Transport/SteamTransport.cs:229-247`：Steam 分片也按同样的 `MaxMessageBytes` 重组；入口需是已建立的 P2P peer／允许的 Steam 房间成员，并非公开任意外网用户。
- `src/Session/Networking.cs:90-113,120-150`：`HandleTransportCore` **先** `DecodeMessage(item.Data)`，**后**进入 `HandleHostMessage`，而未认证限制位于该方法的 `hello`／`auth` 和后续 `peers.TryGetValue(peer,...)`。
- `src/Core/Protocol.cs:30-37`：`Decode` 将整帧 `Encoding.UTF8.GetString` 后，用 `JsonData.Read<WireMessage>` 对整个 JSON 构建对象及 `JToken Payload`；`src/Core/Storage.cs:13-30` 的序列化设置只设 `MaxDepth=128`，没有与鉴权状态相关的字节上限。
- `src/Session/MultiplayerSession.cs:281-289`：解码由 Unity 主线程的会话 `Tick` 处理。

### 触发与影响

一个已连接、**尚未通过 `auth` 的**测试 peer 可以发送协议版本正确、Payload 体积很大的 JSON，即便 `Kind` 对它无权限，房主也必须先解码整个报文才会拒绝。128 MiB 输入可能造成大块字节、UTF-16 文本与 JSON 树的多份分配及主线程暂停；不能将 256 MiB 队列上限理解为解码后的内存上限。**这不同于第三轮 BUG-11 的“未认证连接占满人数槽”，本问题是认证前主线程 CPU/内存成本。**

### 修复约束

将未认证消息与已认证的地图大包使用不同上限和解码预算。建议在进入完整 JSON/`JToken` 解析前按 peer 握手状态限制接收帧字节数（小尺寸白名单仅允许 `hello/auth`），握手完成后才开放必要的大帧；与第三轮 BUG-11 的认证期限及 BUG-13 的凭证协议改造协调。仍须保证合法地图视图的完整性和可靠 FIFO；不能全局降低 `MaxMessageBytes` 以破坏正常大地图。

### 验收

使用**隔离的本机合成 peer、非玩家真实房间**发送 1 KiB 的合法 `hello/auth` 以及 1/8/32 MiB 的未认证无关 Payload；记录完整 JSON 解析次数、峰值分配、最长主线程占用和拒绝耗时。未经认证的大消息必须在完整 JSON 解析前拒绝/断开，其他正常已认证 peer 的合法大地图分片仍可通过。

## BUG-20：非法玩家关系 enum 被保存，权限判定出现不一致

**建议主责：Session（Relation）+ Game（Hostility）。状态：源码校验缺口确认。**

### 源码证据

- `src/Core/Contracts.cs:7`：`Relation` 只有 `Neutral/Friendly/Hostile` 三个合法枚举值。
- `src/Session/Commands.cs:100-102`：房主读取客户端的 `RelationRequest` 后调用 `ChangeRelation`。
- `src/Core/Storage.cs:13-29`：使用普通 Newtonsoft `JsonConvert.DeserializeObject<T>`，未见拒绝越界数值枚举的自定义转换器。
- `src/Session/Commands.cs:239-253`：`ChangeRelation` 只对 `Friendly` 特殊处理，其余**任何**枚举底层值均直接赋给 `pair.Relation=desired`，随后应用原生关系并广播；此处没有 `Enum.IsDefined` 或三态白名单检查。
- `src/Game/GameHooks.cs:223-229`：原生敌对判断只在 `GetRelation(a,b)==Relation.Hostile` 时为真；`src/Session/Authority.cs:94-103` 的 `Permission.Attack` 则对 `GetRelation(...) != Relation.Friendly` 走可攻击分支（其余地图前置条件仍需成立）。`src/Entry/MultiplayerRoomPages.cs:~120` UI 对未知值回退显示“中立”。

### 触发与影响

已认证、能修改关系的测试客户端在 `RelationRequest` 里提交如数值 `99` 的非法枚举，Json.NET 的普通数值枚举反序列化可产生未定义值。关系表被写为 99；界面可能显示“中立”，原生战斗钩子不视为“敌对”，而特定指令权限检查又不把它当“友好”。各模块对同一关系语义不一致，并可能被保存至房主世界。**这需要畸形客户端报文，不能宣称正常 UI 会发出非法值。**

### 修复约束与验收

在房主变更关系前拒绝未定义 enum；旧档若已经存在非三态值，应明确拒绝加载或受控迁移，不默默改为友好或敌对。用本机纯逻辑用例覆盖三种合法关系及 `-1/3/99`；非法值不得修改 `World.Relationships`、原生仇恨或触发权威广播，并验证 UI、`HostilityPrefix`、`HasPermission(Attack)` 对三态含义一致。

## BUG-21：地图模拟者可以提交其他玩家任意“合法字段”的个人进度

**建议主责：Session / Authority + Game / NativePersonalSync。状态：授权来源缺口确认，真实影响需安全测试。**

### 源码证据

- `src/Session/Authority.cs:249-262`：房主给地图访问者发送 `MapView` 时可包含该地图的访问者及拥有者的 `PlayerFieldState`；模拟者能够获得用于对照的个人字段版本与编码值。
- `src/Session/Authority.cs:381-417`：`AcceptMapUpdate` 确认发送者是当前地图模拟者，但允许 `update.Players` 的 `PlayerId` 为**发送者本人、地图所有者或任意 `map.Visitors`**；只进一步核对 `PersonalRevision` 连续性，未验证每个字段的实际操作/收益来源或本人授权。
- `src/Game/NativePersonalSync.cs:29-31`：字段白名单包含 `creditPoint`、`techonologys`、任务、虚拟仓库等不应无因改变的个人资产/进度字段。
- `src/Game/NativePersonalSync.cs:73-94`：`ValidatePersonal` 只检查字段在白名单、去重、`Before` 与当前值匹配以及 `After` 可按类型解码；没有验证变更是来自该个人持有者，或由房主确认的地图业务事件引起。
- `src/Game/GameBridge.cs:365-391`：`ApplyMapUpdate` 将预检通过的地图差量和个人差量组合执行；`src/Game/NativePersonalSync.cs:96-116` 最终将 `field.After` 写入指定玩家的 `FWGameData`；房主随后推进该个人 `PersonalRevision`（`src/Session/Authority.cs:443-447`）。

### 触发与影响

**条件是客户端确实拿到某地块的模拟权并使用修改过的报文。**例如 B 负责模拟自己的基地，A 正在该基地参观：B 可提交 `Players=[{PlayerId=A,...}]`，把 A 的 `creditPoint` 或科技字段设置成任意可解码 `After`，同时填写匹配房主当前值的 `Before`。若其他原生差量校验条件也成立，现有代码没有检查“B 是否被授权改变 A 的这些个人字段”，导致不同玩家资产独立性被破坏。**这是跨玩家个人字段来源认证缺口；和第四轮 BUG-17（新实体 UID 归属来源校验）不是同一个问题。未做对抗复现，不能声称现网已出现作弊。**

### 修复约束

不能简单禁止所有访客个人字段同步，否则合法的合作劳动、任务进度、随身资产变化可能失效。应按来源定义分层权威：本人可自主提交哪些个人字段、地图模拟可报告哪些可验证的工作事件、涉及货币/科技/任务时房主如何验证事件与奖励；或把这些个人状态更新限定为本人/房主确认的独立提交。跨用户字段校验务必在任何原生写入**之前**完成，并与既有 `PersonalRevision` 和 BUG-01 事务提交时序结合。

### 验收

在独立测试世界模拟 B 为地块模拟者、A 为访客的情况：①正常合作导致可解释的 A 任务进度；②B 尝试给 A 额外资金；③B 伪造 A 的科技解锁；④A 本人提交合法个人变化；⑤旧个人版本；⑥模拟任期已失效。只有有权限且可验证的业务变更应被接受；②③应在原生地图与个人状态写入前拒绝，且不会污染 A 的存档或使真正合法变更被静默覆盖。

## BUG-22：消息拒绝处理中的二次发送失败会停掉整个房间 Tick

**建议主责：Session / Networking + Transport。状态：异常传播链确认。**

### 源码证据

- `src/Session/MultiplayerSession.cs:281-305`：主循环以 `try { HandleTransport(item); } catch(Exception ex)` 处理每一条消息，但在该 **catch 内** 对已认证的发送者直接 `Send(peer,"error",publicReason)`，没有再次捕获 `Send` 抛出的异常。
- `src/Transport/TcpTransport.cs:222-232`、`src/Transport/SteamTransport.cs:155-161`：连接失效或发送队列拥塞会抛 `IOException`／`InvalidOperationException`，且 `Send` 不保证成功。
- `src/Entry/ModPluginLauncher.cs:76-87`：`MultiplayerRuntime.Update` 一旦接到 `Session.Tick` 抛出的异常，设置 `tickFailed=true`，后续主循环不再推进该会话。

### 触发与影响

已认证的客户端 B 发送一条能被房主正常拒绝的异常消息（如无效地块、旧任期或非法命令），与此同时房主对 B 的**发送队列已满**。房主正确捕获原消息错误，却在尝试回送 `error` 时再次抛出 `IOException`，二次异常逃出 `Session.Tick`，让包括 A 在内的其他正常玩家也停止世界推进和收包。**这和第三轮 BUG-14（地图缓存超时通知在 Tick 前置阶段抛错）是不同入口，但可共用错误通知隔离设计。**

### 修复约束与验收

对失败回报做非致命的逐 peer 隔离；明确区分“原始业务拒绝”“告知失败”“权威状态可能已部分改变”。错误通知发不出去时可隔离/断开故障 peer，不能吞掉真正的未知原生副作用，也不能使其他玩家停止推进。用本机 `ITransport` 测试替身模拟：B 的消息先触发 `MessageRejectionException`，再让回送 `error` 抛 `IOException`；房主 `Tick` 必须能继续处理后续来自 A 的合法消息，记录 B 的错误并进行确定性收口。再覆盖正常的失败回报、B 主动断开与权威原生异常，避免误判已成功提交。

## 交接与实施顺序

1. **BUG-21 与 BUG-19** 优先进行主控安全/架构评审：分别是客户端跨玩家字段写入与鉴权前大包主线程处理；涉及角色与协议信任边界，不能靠 UI 拦截。
2. **BUG-22** 可由 Session/Transport 做精确异常隔离；应联审第二轮 BUG-08 与第三轮 BUG-14，但独立复现和提交。
3. **BUG-20** 可单独增加小范围主机参数白名单和旧存档检查，不需要改原生仇恨算法。
4. 现有 `Networking.cs`、`MultiplayerSession.cs`、`Authority.cs`、`NativePersonalSync.cs` 等包含其他 Agent 原有工作区内容；先获文件 Owner 交接再改。此文档和留言不代表任何 Agent 已收到即时通知。
5. 修复后每项记录**独立 Git 提交、版本/协议/存档迁移影响、纯逻辑/故障注入、P3/Steam/TapTap BuildOnly、磁盘副本与真实运行 SourceRevision、由用户执行的双端游戏验收**；构建成功不能冒充实机验证。

## Agent 修复回填模板

```text
BUG 编号 / Owner / 开始处理时间：
旧源码与最新源码是否一致：
触发条件/日志/测试输入（不包含真实凭证、房间号、个人存档）：
修复策略与修改文件：
独立 commit、VERSION、协议和存档兼容：
逻辑及故障注入的预期/实际：
Project3/Steam/TapTap BuildOnly：
磁盘安装与运行 SourceRevision：
游戏内与双端用户验收：
剩余风险、阻塞与回滚：
```

**本轮结论：** 新增 4 条前四轮未登记的代码问题／条件性授权风险。证据是源码分析，**没有做运行时复现，也没有修改或发布功能修复**。
