# BUG-08 / P1 · 其他接收方队列故障阻断提交者 ACK

- **来源：** [第二轮静态审计 BUG-08](../../docs/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Session / Authority / Transport Owner 协调，未修复。
- **证据：** `AcceptPersonalUpdate` 在已提交后先广播给其他人再给提交者 `personalAck`；地图 `AcceptMapUpdate` 同样依赖访客扇出循环不抛异常，而 transport 队列满会明确失败。
- **完成条件：** 注入第三方队列满/断链，提交者收到房主真实提交后的确定 ACK 或明确拒绝（重发幂等）；故障接收方隔离并恢复，不通过丢可靠权威消息伪装成功。不能破坏 BUG-01 的终态确认门禁。编译和真实双端验证分别报告。
