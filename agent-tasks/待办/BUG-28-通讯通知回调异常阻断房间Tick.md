# BUG-28 / P1 · 通讯 / 参观通知回调异常可能阻断整房 Tick

> **状态：待执行 / 仅生产调用链静态审查，尚无故障注入与实机复现**
> **建卡：** 2026-10-19，仓库 `codex/multiplayer-v1`，浏览时 HEAD `fe2c82116a1373a525678c67c9b4e718e9f5c245`。
> **建议 Owner：** Session 通讯 / 参观模块；Entry UI Owner 协助核对事件订阅/注销；主控负责混合源码 hunk 签收与完整发布门禁。
> **需求：** [联机方案 V1.md](../../联机方案%20V1.md) 的玩家聊天、参观同意、5 分钟赠送授权、10 秒内送达回执、断线及房主唯一世界推进。**本卡不新增 V1 功能。**

## 1. 源码观察与风险限定

已经确认以下**条件性异常传播路径**，但**没有实际注入会抛错的订阅者，也没有运行 Unity / Play Mode / 双端游戏**。不可写成用户已经遇到此 BUG：

| 调用位置 | 生产代码行为 | 可疑传播路径 |
| --- | --- | --- |
| `src/Session/PlayerVisits.cs::TickVisits` | 本机申请 / 赠送授权到期时先设置 `Expired`、清空 `Grant`，然后直接 `VisitChanged?.Invoke(JsonData.Clone(update))` | 任何事件订阅者抛错都可能在本帧越过 `TickVisits`；现有 `finally` 只负责归还 `VisitTickSnapshot`，**不负责隔离回调** |
| `src/Session/PlayerVisits.cs::EndVisit → SendVisitUpdate` | 房主结束申请后，向两位参与者顺序发送状态；`TrySendCommunication` 对 **LocalPlayerId** 调用 `HandleLocalCommunication("visit.update") → ReceiveVisit → VisitChanged` | 如果首先调用的本机订阅者抛错，可能打断给另一位参与者的后续通知；授权已在房主状态中结束，不应让 UI 回调错误改变业务事实 |
| `src/Session/PlayerChat.cs::TickCommunication → CompleteLocalChat → NotifyChat` | 等待送达结果超时后移除 `pendingChats`，标为 `Failed`，直接调用 `ChatChanged?.Invoke` | UI 订阅者的非业务异常可能从“仅提示消息发送失败”升级为**整个房间帧失败** |
| `src/Session/PlayerChat.cs::TrySendCommunication` | 网络发送异常只在 **远端路径** 捕获 `IOException` / `InvalidOperationException`；发送给本人时直接调用 `HandleLocalCommunication` | 不能以“`TrySendCommunication` 已有 catch”断言本机通知安全；`ChatChanged / VisitChanged` 不属于远端网络发送错误 |
| `src/Session/MultiplayerSession.cs::Tick` | 在收包和房主世界推进之前直接调用 `TickCommunication(seconds)`，此调用不在专门的异常隔离区内 | 通知回调抛出的异常可能让房主 **本帧收包、地图同步、世界 Tick、定期保存均无法继续** |
| `src/Entry/ModPluginLauncher.cs::Update` | `Session.Tick(...)` 抛错进入 `catch`，将 `tickFailed = true`；后续 `if (Session != null && !tickFailed)` 跳过 Tick | 一次本应仅影响 UI 的通知异常，在当前 Session 内可能让联机推进长期停用，需退出/重建会话才能解除 |
| `src/Entry/MultiplayerCommunicationUi.cs` | 当前已订阅 `ChatChanged`、`VisitChanged`，回调调用 `MultiplayerChatDialog.MarkDirty` 并维护弹窗通知数据，没有局部异常隔离 | **不能推定现有 UI 必然抛错**；但关闭/重建界面、其它 MOD 回调或未来 UI 变更可能触发异常，应将事件边界作为可注入的防护点 |

此外，如果 `ReceiveChat` / `ReceiveVisit` 在**合法网络消息处理过程**中触发同类 UI 异常，消息级异常处理可能把本应有效的通讯包误判为“联机消息拒绝”。需单独验证，不把界面故障冒充玩家恶意输入。

## 2. 与现有任务的边界

- [BUG-14](BUG-14-跨机运行回归.md) / [已完成子卡](../已完成/BUG-14-缓存超时通知隔离.md) 对应 **地图缓存超时后的错误通知**，已使用 `OptionalPeerNotice` 收口；不是 `VisitChanged/ChatChanged` 的 UI 回调。
- [BUG-22](BUG-22-二次错误回送.md) 对应**收到非法输入之后**的错误回包与断链失败，不能靠隔离 `Session.Tick` 中任意异常解决本卡。
- [BUG-09](BUG-09-UI同步拒绝提示.md) 对应命令排队与拒绝反馈，不能因为本卡添加回调隔离就修改可靠事务的状态文案。
- [R13-01](R13-01-源码Owner签收.md) / [R13-01B](R13-01B-20261019-构建源码依赖闭包与Owner签收.md) 仍要求旧源码和混合 hunk 原 Owner 逐项签收。本卡不是解冻令，也不能混合提交别人的代码。

## 3. 必须保持的业务不变量

1. **先状态、后通知：** 房主 `HostVisit` 到期 / 拒绝 / 取消必须真正撤销 `Grant` 和一次性授权；本机 `PlayerVisitUpdate` 到期必须只改变原有状态一次。即使 UI 观察者失败，绝不延长 60 秒申请或 300 秒赠送授权。
2. **失败不能变已送达：** `ChatPending` 超时仍为 `Failed`，不可因为 UI 回调抛错伪造 `Delivered`，也不能重复发送私聊或生成另一条 `MessageId`。
3. **健康玩家不被连坐：** A 的通知处理失败不能停止房主处理 B/C 的有效地图更新、聊天及保存，也不能自动将健康客人视作坏连接断开。
4. **只隔离可选回调：** 保护边界应尽量收窄到 `ChatChanged` / `VisitChanged` 的 UI 事件通知。**不允许**把整段 `TickCommunication` 包在无条件 `catch` 里后继续原生更新，以免吞没真实授权、存档、地图事务或桥接错误。
5. **不吞异常证据：** 记录可诊断的阶段与异常类型，日志和状态反馈不能再二次抛错导致原故障复现；不记录聊天正文、用户凭证、密钥或私有资产。
6. **消息与观察者分离：** 来自合法已认证连接的包仍遵循原会话 / 玩家授权，UI 回调的异常不应被解释成网络违规或成功 ACK；多个观察者的行为与通知次数必须有明确约定。

## 4. 建议实施方式（需 Owner 签收）

- [ ] **T28-1 / Session：** 审核 `NotifyChat`、`ReceiveVisit`、`TickVisits` 和 `SendVisitUpdate` 的全部 `ChatChanged/VisitChanged` 发射点，判断可否统一成两个受控通知方法；同类回调的错误不得影响已完成的房主状态变化或另一个人的通知。
- [ ] **T28-2 / Session + Entry：** 为事件处理建立**受限诊断防护**。只捕获外部/可选观察者抛错，业务方法中的 `InvalidDataException / MessageRejectionException / nativeFault` 保持原 fail-closed 边界；诊断发送失败也不能再次使 `Tick` 抛错。决定是否按 `GetInvocationList` 隔离单个观察者，避免一个坏 UI 处理器阻断后面的正常处理器；不改变现有公开事件签名。
- [ ] **T28-3 / Entry：** 审核 `AttachCommunicationUi/DetachCommunicationUi`、窗口销毁与重连时的实际订阅生存期，避免失效监听器和重复订阅；不擅自接管历史冻结中的房间分页文件。
- [ ] **T28-4 / Test：** 新增链接真实生产代码的可控事件观察者注入，用 `Tick` 包装层 / 真实 `PlayerVisits` / `PlayerChat` 方法验证下面 C28 系列。优先避免为通过测试创建未被真实调用的空 helper。
- [ ] **T28-5 / 主控：** 跟踪原 Owner、当前源码 SHA、精确 diff/hunk、VERSION / CHANGELOG 归属；完成官方三端 `BuildOnly` 后交由用户按仓库边界测试 TCP / Steam，未验证前继续待办。

## 5. 故障注入与验收矩阵（本轮尚未执行）

| 用例 | 场景 / 人为注入 | 期望结果 |
| --- | --- | --- |
| C28-01 | 客端本地待同意申请在未缩放时间达到 60 秒时，`VisitChanged` 的第一个回调抛 `InvalidOperationException` | 申请只变成一次 `Expired`，授权撤销，下一帧仍正常执行 `Tick`，无房间整体 `tickFailed` |
| C28-02 | 房主正在处理 A→B 参观同意到期，A 的本机 UI 回调抛错，B 的通知链仍可用 | 房主状态已结束；不会保留可用赠送令牌；B 的合法状态通知不因 A 的 UI 异常漏发 |
| C28-03 | 客端私聊等待 10 秒送达回执后，`ChatChanged` 抛错 | 消息仅转成 `Failed`，不变 `Delivered`；下一帧继续有效通讯、心跳与地图同步 |
| C28-04 | 为同一事件挂接两个观察者：一个抛错、另一个正常；再次注册 / 注销 | 不重复通知、正常观察者仍可按设计收到状态；关闭窗口或重连后没有残留监听器 |
| C28-05 | 通知回调重入 `GetPlayerVisits` / 修改 UI 列表，或在超时回调中退出房间 | 可复用 `VisitTickSnapshot` 在 `finally` 中释放，未重复推进同一条记录，不发生集合修改异常或二次通知 |
| C28-06 | 收到一个**有效** `chat.message / visit.update`，只让 UI 订阅者抛错；另给非法会话消息负向用例 | 合法包不被记录为攻击或错误断链；真正的伪造会话、无效授权仍按原策略拒绝 |
| C28-07 | 注入真实会话内部桥接失败、原生写入不确定或存档失败，不注入 UI 事件 | 不会被“可选观察者失败保护”误吞；保持原生/保存故障锁和明确用户提示 |
| C28-08 | 用户双端或 3 人 Steam / TCP 测私聊超时、参观等待、取消/授权到期、关闭界面再重进，查看房主日志 | 房主世界仍推进、仍能保存并接收其他玩家合法操作；消息不重复且实际资产/授权一致 |

**证据级别**：本轮是静态源码调用链（S0），不是故障注入通过、干净 Git 闭包、三端 `BuildOnly` 或多人实机验收。测试结果必须按真实执行内容分层记录；不得拿 UI 提示正常冒充原生权威/存档一致。

## 6. 后续交接与关闭标准

**候选路径**：`src/Session/PlayerChat.cs`、`src/Session/PlayerVisits.cs`（访客个人权限、运输赠送敏感）、`src/Session/MultiplayerSession.cs`（**当前混合脏文件，仅接口只读**）、`src/Entry/MultiplayerCommunicationUi.cs` 和 `ModPluginLauncher.cs`（运行时全局 Tick 防护只读）；可新增隔离测试工具。与负责聊天/访问的原 Owner 沟通前，禁止重写现有访客权限或对话状态机。

关闭本卡必须同时满足：已验证原 Owner 文件/hunk 签收、生产源码真实接线与 C28-01～07 受控故障注入通过、干净 Git 依赖闭包和三端官方构建、用户实际 C28-08 TCP/Steam 验收及可复核双端日志。若只完成代码或本地逻辑检查，继续留在 `待办/`，并在卡中分别记录未通过 / 未执行的层级。

**本轮交付边界**：仅只读浏览、创建 MD、登记协作和通知有关 Agent；没有修改 MOD 功能源码、协议、存档、版本、运行副本或其它 Agent 的历史工作区差异，不构建安装、不运行游戏、不推送。
