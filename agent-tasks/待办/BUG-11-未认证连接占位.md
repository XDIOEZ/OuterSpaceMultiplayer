# BUG-11 / P2 · 未认证连接长期占据房间名额

- **来源：** [第三轮静态审计 BUG-11](../../docs/联机MOD第三轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Transport + Session 原 Owner 接管，未修复。
- **证据：** TCP/Steam 都在完成 `hello/auth` 前就占用 transport peer；Session 只清理已拒绝的 peer，缺全体未认证连接的 deadline。TCP 有 90 秒读超时，Steam 超时行为需实际测。
- **完成条件：** transport 连接后采用非游戏暂停时间驱动的限期，认证成功即清理待认证状态；超时释放 transport/challenge 并允许合法玩家加入。分别测试不发 hello、hello 后不 auth、有效慢速握手、Steam/TCP 并存；不得踢掉已认证 peer 或绕过人数上限。
- **依赖：** 与 BUG-06 握手半成功回滚协调；未经原 Owner 交接不得重写混合 `Networking.cs`、`TcpTransport.cs` 或 `SteamTransport.cs`。
