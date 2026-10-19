# BUG-08 / P1 · 其他接收方队列故障阻断提交者 ACK

- **来源：** [第二轮静态审计 BUG-08](../../docs/audits/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Session / Authority / Transport Owner 协调，未修复。
- **证据：** `AcceptPersonalUpdate` 在已提交后先广播给其他人再给提交者 `personalAck`；地图 `AcceptMapUpdate` 同样依赖访客扇出循环不抛异常，而 transport 队列满会明确失败。
- **完成条件：** 注入第三方队列满/断链，提交者收到房主真实提交后的确定 ACK 或明确拒绝（重发幂等）；故障接收方隔离并恢复，不通过丢可靠权威消息伪装成功。不能破坏 BUG-01 的终态确认门禁。编译和真实双端验证分别报告。

## 2026-10-18 / 已提交 ACK 先行与旁观者隔离子补丁（主卡仍待办）

- **源码提交：** `6efdf27`。新增 `src/Session/ReliableCommitFanout.cs`、`tools/ReliableCommitFanoutChecks/`，并仅对 `src/Session/Authority.cs` 的 `AcceptMapUpdate` 和 `AcceptPersonalUpdate` 已提交后通知阶段进行精准 Git 变更（15 行新增、4 行删除）；旧的 200+ 行未提交混合修改仍留工作区，未覆盖。
- **地图：** 房主先完成原生应用与 MapRevision/LastCommitId/PersonalRevision 更新，再将包含同一 SessionId/MapId/AuthorityEpoch/CommitId/版本及个人水位的紧凑 `map` ACK 发给提交者；之后用原有 `map`/ `personal` kind 广播给其他需要该状态的玩家。故障旁观者的可靠队列无法接收时断开该连接，不把失去的地图事务当作已同步，其他目标继续收到可靠更新。保留重复 CommitId 对应 ACK 的既有门禁。
- **个人：** `AcceptPersonalUpdate` 原生/版本更新完成后先向提交者发送 `personalAck`，再按旧 Broadcast 顺序和一次编码给其余玩家发 `personal`。旁观者队列满（IOException/InvalidOperationException）执行断开与重连恢复。发送方 ACK 失败仍抛出，不虚构成功；断开也失败则显式传播异常以免留下貌似同步成功的旁观者。
- **测试：** `tools/ReliableCommitFanoutChecks/` 链接生产 helper/partial 与虚构传输边界，30,027 项检查通过：异常类型、ACK 先行的假房间消息流、1 万轮随机单故障旁观者、其它接收者继续投递、紧凑 map receipt 的版本水位及编码共享。已有 `tools/OptimizationChecks/` 185 项通过。此测试不等于编译全部 Unity MOD，也未启动双端网络。
- **待收尾：** 模拟者自身队列拥塞、ACK 丢失/重发后的权威幂等对账、不同端真实 TCP/Steam 断线与重连、地图/个人同步可靠终态（含 BUG-01/10）仍须集成故障注入，未达原卡所有完成条件；**保持本卡在待办目录**。当前 VERSION/CHANGELOG 为其他 Owner 未提交 V1.30 内容，待主控整合后再按仓库规则递增/发布，不将本窄补丁冒充已安装或已实机验收。
