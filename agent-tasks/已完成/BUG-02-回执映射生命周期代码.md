# BUG-02 · 命令回执映射生命周期代码修复

- **来源：** [静态 BUG 交接 BUG-02](../../docs/联机MOD静态BUG审计与Agent修复交接-20261017.md)，由 [原待办](../待办/BUG-02-命令回执表增长.md) 拆出的可独立检验代码任务。
- **状态：已完成代码修复（2026-10-17）**；最终异步 ACK/游戏验收受 BUG-01 提交时序影响，仍在原待办。
- **提交与版本：** `5d83a38` / V1.23，联机协议 6、存档 1 不变。

## 已实施

1. `Networking.cs` 的 `command` 收包路径不再为每条客户端命令无条件登记 `commandReplyPeers`；只在 `ExecuteHostCommand` 返回已排队、且 `pendingExecutions` 真正包含该键后才登记回执目的地。
2. 对即时完成/拒绝/发送失败结果移除旧映射；`RemovePeer` 在检查认证表前释放该 peer 对应的所有映射，包括未完成握手的边界。
3. 新 `DeferredCommandReplyMap.cs` 是直接接入上述生产路径的纯逻辑策略，原 `AcceptExecution` 的一次性 `Remove` 路径不变；不会错误地将排队 ACK 当成最终地图提交。

## 实际检查

- 新 `tools/OptimizationChecks/DeferredCommandReplyChecks.cs` 8 项：共 10k 即时完成 + 10k 立即失败均不留映射、1k 延迟命令断线清理、重复 ID 复用映射、多个 peer 独立清理及失败提交移除。
- `dotnet run --project tools/OptimizationChecks/OptimizationChecks.csproj -c Release` → **PASS: 97 optimization logic checks**（原 89 + 新 8）。
- `Sync-ModCopies.ps1 -Target All -BuildOnly` → **P3/Steam/TapTap 三端成功**，编译包版本 `1.23.0.0`、源码指纹 `005824B656221C54AFA5F8018888FDB3A4BC262E41B291A75E44FECB80B57625`。
- 提交只含 `Networking.cs` 的两处指定 hunk、一个新生产纯逻辑文件、两份检查工程改动及 VERSION/CHANGELOG；其它旧 `Networking.cs` 混合工作区源码没有被暂存。

**不能宣称：** BUG-01 的「远端 execution → 地图真正 commit → 最终 result」确认协议尚未修好。游戏内一对多真实回执、断线与重连结果需用户/Owner 复测；未部署游戏运行副本。
