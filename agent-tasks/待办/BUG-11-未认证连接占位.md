# BUG-11 / P2 · 未认证连接长期占据房间名额

- **来源：** [第三轮静态审计 BUG-11](../../docs/联机MOD第三轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Transport + Session 原 Owner 接管，未修复。
- **证据：** TCP/Steam 都在完成 `hello/auth` 前就占用 transport peer；Session 只清理已拒绝的 peer，缺全体未认证连接的 deadline。TCP 有 90 秒读超时，Steam 超时行为需实际测。
- **完成条件：** transport 连接后采用非游戏暂停时间驱动的限期，认证成功即清理待认证状态；超时释放 transport/challenge 并允许合法玩家加入。分别测试不发 hello、hello 后不 auth、有效慢速握手、Steam/TCP 并存；不得踢掉已认证 peer 或绕过人数上限。
- **依赖：** 与 BUG-06 握手半成功回滚协调；未经原 Owner 交接不得重写混合 `Networking.cs`、`TcpTransport.cs` 或 `SteamTransport.cs`。

## 2026-10-18 / 未认证连接绝对期限与名额保护子补丁（原卡仍待办）

- **生产提交：** `b3488d0`。新建 `src/Session/UnauthenticatedPeerDeadline.cs` 的会话握手计时器并接入真实 `Networking.HandleTransportCore` Connected/Disconnected、hello/auth 过期拒绝、`AuthenticatePeer` Welcome 后 transport admission 完成边界，以及 `MultiplayerSession.Tick` 的无缩放时间。两个长期脏文件采用单独 Git index 精确 hunk，只提交自己新增入口；不触碰其它 Agent 的 `MapViewLoading`、`OptimizationChecks` 正在处理的 BUG-24。
- **语义：** 从 transport 的 Connected 事件起每个未认证连接最多有 30 秒（无游戏倍速/暂停影响）完成验证；重复 Connected/hello/challenge 不能续期。至多 32 个同时待验证连接，第 33 个当场断开。完成 `welcome` 发送及 `ITransportPeerAdmission.MarkAuthenticated` 后才移除过期计时；接收 Disconnect 时释放记录；超时使旧挑战失效并实际断开传输，不只是变更 UI。TCP/Steam 断开若暂时报 IOException/InvalidOperationException，则保留过期标识、每 2 秒重试，不能在此期间再次接受迟到 hello/auth；异常信息独立记录。
- **回归：** `dotnet run -c Release --project tools/UnauthenticatedPeerDeadlineChecks/UnauthenticatedPeerDeadlineChecks.csproj` 生产 partial/helper 链接 30071 项通过，覆盖静默、只发送 hello、慢速合法握手、32/33 并发名额、认证后永不被此 gate 踢出、断开失败/重试、异常边界及 15000 轮随机连接交错。原有 `tools/UnauthenticatedTcpChecks/` 的 7 项生产 TCP localhost 入站检查和 `tools/OptimizationChecks/` 的 185 项通过。只证明本地行为及入口源码接线，不代表 Steam API/Unity MOD 全量编译或真实房间表现。
- **尚未验收：** 需 Session/Transport Owner 在两台真实设备上分别用 TCP、Steam 故意保持不发 hello、发 hello 后不 auth、慢速正常握手、欢迎包入队失败、断线重连与 Steam peer 回调顺序做实机日志验证；BUG-06 登录半成功回滚仍是独立风险。已存在其他 Agent 的未提交 `VERSION=1.30` 与 CHANGELOG，未更改或抢占版本升级/三端安装；**保持本卡在待办**，完成测试验收后再决定归档。
