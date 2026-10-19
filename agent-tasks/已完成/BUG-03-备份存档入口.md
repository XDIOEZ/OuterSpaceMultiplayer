# BUG-03 / P2 · 备份代有效但继续游戏列表不可选

- **来源：** [静态 BUG 交接 BUG-03](../../docs/audits/联机MOD静态BUG审计与Agent修复交接-20261017.md)。
- **状态：已完成代码修复与合成存档验收（2026-10-17）**。未进行游戏内 UI/跨机实测，不代表整个 V1 已验收。
- **证据：** `SaveStore.ListWorlds` 只看 `active.json`，但 `SaveStore.Load` 可尝试 `active.json.bak`；Entry 对 `WorldSummary.Error` 禁止 `HostLoad`。
- **需要：** 列表与真实 `Load` 共用**只读可恢复性判断**，显式说明回退代；没有合法备份时不可虚报“可恢复”，浏览列表不得自动修复或覆盖损坏索引。
- **验收：** 临时隔离数据模拟主/备份均好、主索引坏但备份与代完整、主/备份均坏；列表状态和实际可加载性完全一致。用户真实存档不可读写用于注入。

## 实际交付记录

- 修复提交：`af09940`（`fix: list recoverable backup multiplayer saves`），版本 **V1.21**，协议 6 / 存档 1 未变。
- `src/Core/Storage.cs`：`ListWorlds` 现在调用真实 `Load` 的只读指针、SHA 和存档代校验。当前代坏或缺失而备份可读时，列表展示备份内容的正确代次与「备份可恢复」，且 `Error` 为空，现有 Entry 的「继续世界」按钮因此允许选择；无可读代时列表仍显示错误并阻止进入。
- `tools/IdentityStorageChecks/Program.cs`：直接编译生产存储源码，在隔离临时目录下新增主档有效、当前代损坏、主索引损坏、主索引缺失、双档损坏及不修改磁盘现场的回归断言。
- 实际命令：`dotnet run --project tools/IdentityStorageChecks/IdentityStorageChecks.csproj -c Release` → **PASS: 59 production IdentityStore / ModSettingsStore / SaveStore checks**；`Sync-ModCopies.ps1 -Target All -BuildOnly` → **Project3 / Steam / TapTap 三端成功**，包程序集 **1.21.0.0**，本轮源码指纹 `DF9DAC4782FB16C31903DA262118A15A8F364974D935553EDA08090106DBE99D`。
- Git 仅暂存本轮四个文件的精确变更；`Storage.cs` 原有历史删除 `IdentityStore` 的旧未提交差异**未合入**。未改 Entry 的历史未跟踪分页文件、未碰玩家存档、未部署运行副本或运行游戏。

**归档依据：** 本卡要求的隔离数据主/备份恢复一致性已验证；后续实际游戏 UI 交互属于整体 [R13 运行验收](../待办/R13-02-07-运行与发布门禁.md)，仍需用户执行。
