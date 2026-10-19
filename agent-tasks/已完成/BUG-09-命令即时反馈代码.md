# BUG-09 · 命令同步反馈代码与纯逻辑回归

- **来源：** [第二轮审计 BUG-09](../../docs/audits/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)，从原 [待办](../待办/BUG-09-UI同步拒绝提示.md) 拆出的独立可核验代码范围。
- **状态：已完成（2026-10-17）**，原游戏 UI 验收仍保持待办。
- **版本及提交：** V1.22，`85862c7`（`fix: report immediate multiplayer command refusals in UI`）；协议 6 / 存档 1 不变。

## 实际修复

1. 在 `MultiplayerRuntime.Submit` 的真实用户命令入口检查 `Session.Submit` 的即时返回值，不再丢弃同步拒绝。
2. 新增 `src/Entry/CommandSubmitStatus.cs` 作为无 Unity 依赖的状态分类逻辑：失败显示具体拒绝原因，`Success=true, Applied=false` 保留“等待”而非宣称成功；已经 `Applied` 的结果返回空，不重复覆盖 `CommandCompleted` 的最终反馈。
3. `tools/OptimizationChecks/CommandSubmitStatusChecks.cs` 直接编译上述生产文件，包含 null、明确拒绝、无说明拒绝、本地排队、远端排队和已应用六项断言。

## 验证与边界

- `dotnet run --project tools/OptimizationChecks/OptimizationChecks.csproj -c Release`：**PASS: 89 optimization logic checks**（原 83 + 6）。
- `Sync-ModCopies.ps1 -Target All -BuildOnly`：Project3 / Steam / TapTap 真实目标程序集编译、打包成功；包版本 `1.22.0.0`，三端本轮源指纹 `FC17DBF7DC17C2AB89F2A6D7DB88431EA9EFD9A4F4870D1685A3B9E2E5602C4B`。
- `ModPluginLauncher.cs` 仅提交 `Session.Submit` 所在的目标 hunk，旧的混合 UI 代码、诊断与其它 Agent 修改依然保留未提交；未触碰 `MultiplayerRoomPages` 等未签收页面。
- **未完成：** 游戏内的按钮刷新、Toast/状态文本唯一性和客端操作跨机体验需用户实际测试，不在本卡宣称通过；详见原待办。
