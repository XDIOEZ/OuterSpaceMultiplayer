# BUG-22 · 消息拒绝后的非权威通知故障隔离

- **来源：** [第五轮审计 BUG-22](../../docs/audits/联机MOD第五轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：代码与纯逻辑注入完成（2026-10-17）**；真实会话 Tick/双端故障注入仍在 [BUG-22 待办](../待办/BUG-22-二次错误回送.md)，没有宣称游戏已验收。
- **Git：** `7b73a80` / V1.26，协议 6 / 存档 1 不变。

## 已实施

- 将原 BUG-14 的非权威 `Send/Disconnect` 最小策略提取为独立生产 `src/Session/OptionalPeerNotice.cs`，旧 `MapCacheExpiry.cs` 直接复用。仅捕获 TCP/Steam 通常使用的 `IOException` 和 `InvalidOperationException` 发送故障，尝试断开故障 peer；断开也失败时不再让可选提示影响其它玩家。
- `src/Session/MultiplayerSession.cs` 接收主循环的 `catch` 中，**仅**把向已认证异常消息发送者回送 `error` 的旧直接 Send 包装进该策略。原始拒绝/异常和原生故障锁仍由原路径处理；不将拒绝标成成功，不用于权威地图/个人 ACK、完整存档代或真实命令确认。
- `MultiplayerSession.cs` 原有大量旧 Agent 未提交代码，Git 仅提交当前发送通知的精确窄 hunk；它们的日志细节净化与完整网络循环仍在工作区，未混进本任务的提交。

## 验证

- `tools/OptimizationChecks/OptionalPeerNoticeChecks.cs` 直接编译生产通知策略；新 7 项包括正常错误反馈、发送队列满、连接已经不存在、断开再失败、继续处理其他 peer、其他程序异常不误吞及非法参数拒绝。原 BUG-14 的生产缓存 Tick 注入同时继续通过。
- `dotnet run --project tools/OptimizationChecks/OptimizationChecks.csproj -c Release`：**116 项通过**（原 109 + 7）；`Sync-ModCopies.ps1 -Target All -BuildOnly`：Project3 / Steam / TapTap 均成功，程序版本 `1.26.0.0`，同源指纹 `BFB05B29ED6261D26015286FF1EC48058C48E896A6A7492D49A7A2EDAAE83A8E`。
- **未做：** 真实 `MultiplayerSession.Tick` 的双端网络队列故障注入、用户加载测试、游戏存档或运行 MOD 安装。原 BUG-22 的最终验收不能仅凭纯逻辑和 BuildOnly 通过。
