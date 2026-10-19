# BUG-10 / P1 · 最终检查点未等待刚发出的提交 ACK

- **来源：** [第二轮静态审计 BUG-10](../../docs/audits/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Session / Authority Owner 协调，未修复。
- **证据：** `MultiplayerSession` 先检查 in-flight 为空，然后 `PublishLocalMaps/PublishPersonalUpdates` 可能新增事务，未复查便发 `finalDone`；房主收到后直接移出 `finalCheckpointPeers`。
- **完成条件：** 最后新发 map、personal、拒绝、超时和正常 ACK 分别注入；只在房主可验证全部提交水位后才报告最终完成；保留有界退出等待与失败告警，前一完整存档代仍安全。与 BUG-08 的 ACK 恢复机制协调并在真实双方关闭/重开中验收。

## 2026-10-18 / 客端结束通知的局部时序修复（原卡仍待办）

- **源码提交：** `b72e385`（`src/Session/MultiplayerSession.cs` 的唯一 finalDone 分支、`src/Session/FinalCheckpointGate.cs`、`tools/FinalCheckpointGateChecks/`）。
- **已覆盖：** 客端先复核旧 in-flight/排队命令，再依原顺序执行 `PublishLocalMaps` 和 `PublishPersonalUpdates`，之后重新检查；刚生成的 map/personal 事务及待执行 checkpoint 命令都会延迟 `finalDone`，等待后续真实 ACK。只有 `Send("host", "finalDone", null)` 正常返回才清掉 `finalCheckpointRequested`；发布/发送失败保留请求。
- **本机证据：** .NET8 编译链接真实 `FinalCheckpointGate` 的 53972 项故障/交错/顺序回归通过，已有优化逻辑检查 185 项通过。生产 `MultiplayerSession.cs` 仅本任务窄 hunk 提交，其他 Agent 的混合未提交源文件保留。
- **尚未验收：** 房主收到 finalDone 的可信提交修订水位与持久化先后、BUG-08 可靠 ACK 故障补偿，以及客端拒绝/超时/断链和双端实际退出重开。未执行 Unity/Steam/TapTap 三端正式构建安装、真实联机或发布版本升级；当地 VERSION/CHANGELOG 既有未提交 1.30 内容由主控接收后按版本规则处理。**不能据此移动到已完成目录。**
