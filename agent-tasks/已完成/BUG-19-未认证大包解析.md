# BUG-19 / P1 · 未认证连接的大型 JSON 主线程解析

- **来源：** [第五轮审计 BUG-19](../../docs/联机MOD第五轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：代码、真实 TCP 回环帧门禁与三端编译已完成并归档（2026-10-17）。** 真正游戏内的 Steam/TCP 双端连接和地表/银河大地图表现，另见 [待办/BUG-19-双端真实握手与地图回归](../待办/BUG-19-双端真实握手与地图回归.md)；没有冒认已实机通过。
- **证据：** `DecodeMessage` 在检查 peer 认证前解码最高可达 128MiB 的包；全局队列容量不等于匿名消息的解析预算。
- **需要：** 在完整 UTF-8/Json.NET 对象解析前对未认证 peer 做小尺寸严格门禁及握手消息白名单，保留已认证大地图的可靠分块；与 BUG-11 待认证过期、BUG-13 身份协议一并审查。
- **验收：** 只用隔离本机 fake peer，发送合法小 hello/auth 与 1/8/32MiB 未认证包，证明大包在解码前被拒绝且不占主线程巨量分配；已认证地图仍可正常同步。禁止向真实玩家网络攻击测试。

## 修复实现与验收证据

- **修复 Git：** `9a7f3d6`；**测试强化 Git：** `83176da`；MOD V1.28，协议 6 / 存档 Schema 1 均未变。
- `src/Transport/UnauthenticatedFramePolicy.cs` 的生产门禁：房主未认证连接每帧最多 **16 KiB**；已认证连接与游戏客户端接收房主数据继续使用原来的 128 MiB 总上限。未认证状态仅允许 `hello/auth` 通过会话阶段白名单。
- `src/Transport/TcpTransport.cs` 在只读完 4 字节帧头后、`TryReserve` 和整块负载分配前拒绝超限长度；`SteamTransport.cs` 在分片 `total` 字段验证后、`new byte[total]` 之前拒绝；`RoomTransport` 显式转发两种渠道的 `MarkAuthenticated`，只有房主成功发送 `welcome` 后才提升这个 peer 的接收权限。
- `src/Session/Networking.cs` 在完整 UTF-8 / Json.NET `JToken` 解析之前复检大小；未认证连接的非 `hello/auth` 消息在进入地图/业务消息分发前拒绝。协议包字段和可靠 FIFO 不变。
- `dotnet run --project tools/OptimizationChecks/OptimizationChecks.csproj -c Release` → **PASS: 124**（原 116 + 新 8 项），覆盖帧边界、匿名 1/8/32/128 MiB 拒绝、合法 128 MiB 已认证和握手 Kind 白名单。
- `dotnet run --project tools/UnauthenticatedTcpChecks/UnauthenticatedTcpChecks.csproj -c Release` → **PASS: 7 production TCP pre-auth admission checks (localhost only)**：只发 4 字节长度头声明 1/8/32 MiB，生产 TCP 立即拒绝并产生断开事件；真正 JSON 格式的小 `hello`、`auth` 能通过；显式认证后真实传输 1 MiB 数据，仍原样收到。没有接入游戏或真实玩家房间。
- `Sync-ModCopies.ps1 -Target All -BuildOnly` → Project3 / Steam / TapTap 三端独立引用均成功，程序版本 1.28.0.0，三端 `SourceRevision=126ED1BE686EBC15D026B4F86E5A5B6AA5414D7D3A24D5B95E19630036575E74`。**仅构建包，未安装运行副本、未启动 Unity/Steam/TapTap 或推送**。
- 既有 `Networking.cs`、TCP/Steam 的其它 Owner 混合工作树代码均保留；提交只含上述门禁与独立测试范围。BUG-11 未认证连接期限、BUG-13 Verifier 密钥设计均继续独立待办，本门禁不宣称能解决两者。
