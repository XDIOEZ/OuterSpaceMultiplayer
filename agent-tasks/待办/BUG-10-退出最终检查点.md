# BUG-10 / P1 · 最终检查点未等待刚发出的提交 ACK

- **来源：** [第二轮静态审计 BUG-10](../../docs/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Session / Authority Owner 协调，未修复。
- **证据：** `MultiplayerSession` 先检查 in-flight 为空，然后 `PublishLocalMaps/PublishPersonalUpdates` 可能新增事务，未复查便发 `finalDone`；房主收到后直接移出 `finalCheckpointPeers`。
- **完成条件：** 最后新发 map、personal、拒绝、超时和正常 ACK 分别注入；只在房主可验证全部提交水位后才报告最终完成；保留有界退出等待与失败告警，前一完整存档代仍安全。与 BUG-08 的 ACK 恢复机制协调并在真实双方关闭/重开中验收。
