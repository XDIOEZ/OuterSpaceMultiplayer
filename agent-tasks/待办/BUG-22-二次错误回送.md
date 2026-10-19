# BUG-22 / P1 · 消息拒绝后的错误回送故障

- **来源：** [第五轮审计 BUG-22](../../docs/audits/联机MOD第五轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：代码已修复，待完整 Session Tick / 真实双端注入与验收；不得提前整体归档。**
- **已完成子任务：** [错误回送代码与生产辅助策略](../已完成/BUG-22-错误通知隔离代码.md)，`7b73a80` / V1.26，116 项逻辑检查、三端 BuildOnly 成功。
- **仍需验收：** 使用本机假 `ITransport` 将已认证 B 的非法命令拒绝、B 的 `error` 回送队列满/断开错误和 A 的后续合法消息组合注入**完整会话主循环**；确保真实 `Tick` 后续依然继续，B 被隔离且不会有“拒绝即确认成功”的假回执。正常错误回报、peer 主动断开与未知原生变更后的致命错误不能被吞没。
- **部署门禁：** 源码历史 Owner 签收、干净 Git 同源构建后才允许由用户完成双端游戏实际加载和回归，不直接替换现有运行 MOD 或共享 Harmony。

## 2026-10-19 / 拒绝消息时真实断线失败的隔离与重试子补丁

- **源码提交：** `082ee44`。新增 `src/Session/RejectedPeerEvictionGate.cs`，在房主 `MultiplayerSession.Tick` 的已认证非法消息错误通知分支记录 `OptionalPeerNotice` 是否真正断链；失败或异常时将故障 peer 隔离，并在不受游戏暂停/倍速影响的 Tick 中按 2 秒间隔重试 `transport.Disconnect`。在 `Networking` 运输事件入口先拦截隔离 peer 的新消息，只有真实 `Disconnected` 事件才清除隔离记录。
- **未认证连接：** 现行工作树中正在由其他 Agent 整合的 `RejectUnauthenticatedPeer` 与 `TickRejectedPeers` 分支，也已将错误发送失败和到期断开失败接入同一个可重复清理入口；这部分依赖该 Owner **尚未提交的完整认证代码**，本轮 Git 仅按 committed baseline 精确提交共用 Tick/Networking 入口窄修改，没有顺带 stage 那些旧变更。待主控签收时必须核对真实工作树和提交版闭包。
- **故障注入：** `dotnet run -c Release --project tools/RejectedPeerEvictionChecks/RejectedPeerEvictionChecks.csproj` 直接链接生产 gate、会话 partial 及原生产 `OptionalPeerNotice`，180035 项通过：已认证正常错误通知、发送队列满、断开多次失败、坏玩家继续隔离、好玩家不受影响、空/重复/复用 peer id、同步断链回调、二次日志异常、1.8 万轮随机拒绝/重试。既有 `OptimizationChecks` 185 项通过。均为 .NET8 本地桩与逻辑故障注入，**没有启动 Unity、没有实际构建完整 MOD 或 TCP/Steam 双人房间注入**。
- **仍待完成：** 用真实 `ITransport`/全量 `Session.Tick` 在同一房间注入 B 非法命令后错误回送失败、B 断开也失败，验证 A 继续正常提交且 B 没有第二次权威副作用；确认异常原生状态仍被 `nativeFault` 锁住；审核 BUG-08 的观众可靠广播队列和 BUG-11 的握手期限，不把不可靠错误消息伪装为已确认 ACK。BUG-22 **仍留在待办**，正式三端构建、原 Owner 签收和双端验收完成后才能考虑归档。
