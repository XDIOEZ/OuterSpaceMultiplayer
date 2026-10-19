# R13-01A · Core 五份文件只读盘点

- **来源：** [第十三轮执行案 §八第一张工单](../../docs/plans/联机插件第十三轮优化开发案-源码签收与实测攻坚-20261017.md)，步骤 1。
- **当前状态：已完成并归档（2026-10-17）。** 原文档 R13-01A 的只读步骤 1 已完全核实；不是原 Owner 签收。
- **范围：** 只读核对 `IdentityStore.cs`、`ModSettings.cs`、`Contracts.cs`、`Protocol.cs`、`Storage.cs` 的当前 SHA256、Git U/M、HEAD 差异，并执行现有 53 项身份/设置/存档测试。**不包含取得原 Owner 授权、暂存源码或实机验证。**

## 当前证据

审计报告：`artifacts/agent-todo-20261017/source-audit.json`（本机被忽略目录）。执行 `tools/Audit-SourceClosure.ps1` 后：138 输入、19U/24M、`CleanGitClosure=false`；整体文件内容指纹 `82FBFEC96ED3F397398EEFA778D5477860F9DFB3E7AD65F07044D4838065BC3A`。

| 文件（`src/Core/`） | 状态 | SHA256 | HEAD 差异 |
| --- | --- | --- | --- |
| `IdentityStore.cs` | U | `39C0283D344C1C41BE5D179DDAB8F7D1B3C221F04510D1ADD0BB8444540178E5` | 未跟踪，HEAD 无文件 |
| `ModSettings.cs` | U | `454082A159DEF1F272F8BC4268FF655FF6E9C57722B650D22FEC098B2DB8C00E` | 未跟踪，HEAD 无文件 |
| `Contracts.cs` | M | `76E8E2CD2F32DC6299D27A336771828F5508CF14A6ECCAE09A88BD828204462A` | +70/-5 行 |
| `Protocol.cs` | M | `A685798C71CA6F53EF38D02046B562ACAA24FACCC09BB9CDC3CDDA021E1189EE` | +27/-4 行 |
| `Storage.cs` | M | `61BBA65BEDE8731235D20EBF09CF6957D447FD722DFB4188EDE18BBED77F2C7C` | +1/-20 行 |

执行：`dotnet run --project tools/IdentityStorageChecks/IdentityStorageChecks.csproj -c Release`。

结果：`PASS: 53 production IdentityStore / ModSettingsStore / SaveStore checks`。

**本任务完成边界：** 仅完成上述静态盘点与生产纯逻辑测试；5 份文件**仍未得到原 Owner 签收**，后续必须由 [R13-01 源码签收任务](../待办/R13-01-源码Owner签收.md) 独立处理。未暂存、提交或更改以上 Core 文件。
