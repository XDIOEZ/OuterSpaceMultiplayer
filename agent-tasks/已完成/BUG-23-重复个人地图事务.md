# BUG-23 / P1 · 重复玩家个人差量导致地图事务半提交

- **来源：** [第六轮静态 BUG 审计](../../docs/audits/联机MOD第六轮静态BUG审计与Agent修复交接-20261017.md)。
- **代码任务状态：已完成并归档（2026-10-17）。** 双端实机事务验收仍在 [BUG-23-实际双端事务验收](../待办/BUG-23-实际双端事务验收.md)。
- **目标：** 接收 `MapUpdate.Players` 时，缺列表、空项、无效 ID、重复 `PlayerId` 必须在任何原生地图或个人修改前拒绝，合法空列表、单人更新和不同人员的多行更新不受影响。
- **修复范围：** `src/Session/Authority.cs` 中 `AcceptMapUpdate` 前置唯一性检验，`src/Session/MapTransactionPlayerValidation.cs`、`tools/OptimizationChecks/`、版本及日志。完整 Map/Player apply、ACK/重连与真实游戏验收不在本卡宣称通过。

## 真实代码与测试交付

- **代码提交：** `edfb4a2`；V1.29（程序集 1.29.0.0），协议 6、存档 Schema 1 保持不变。
- 新 `src/Session/MapTransactionPlayerValidation.cs` 使用 `HashSet<string>(StringComparer.Ordinal)` 线性验证 `PlayerUpdate` 的唯一身份、非空对象和非空 `PlayerId`。
- `AcceptMapUpdate` 进入权威原生应用前的 `try` 预检中，紧邻 `Players != null` 检查处调用生产验证方法，**任何重复玩家都在 `bridge.ApplyMapUpdate` 或 `RegisterOwner` 前拒绝**；不清空玩家状态，也不把原生写入后的失败伪装成 `NativeUnchanged`。
- `tools/OptimizationChecks/MapTransactionPlayerChecks.cs` **直接编译生产 helper**，新增 9 项：空列表/单人/不同玩家两行合法；重复同值、重复不同字段/版本、空对象、空 ID/缺列表均被拒绝；10,000 个不同玩家的列表允许，并用调用顺序断言被拒绝的事务不会进入下游写入阶段。
- `dotnet run --project tools/OptimizationChecks/OptimizationChecks.csproj -c Release`：**PASS: 133 optimization logic checks**（旧 124 + 新 9）。
- `Sync-ModCopies.ps1 -Target All -BuildOnly`：Project3、Steam、TapTap 各使用目标真实程序集成功编译并打包 V1.29；三端源码指纹 `7B7FF9ECDC0B004520CA55BDB6A89B18CB8F72B495A2B73B379EF2F76F1EF8D0`；**只有 BuildOnly，没有安装/游戏运行**。
- `src/Session/Authority.cs` 存在其它 Agent 的大型历史未提交差异；只选择性暂存本次一处两行窄补丁，其他历史内容均未合入 commit。

## 尚有边界

本修复阻止“同一列表重复身份”的内部自相矛盾事务；第五轮 BUG-21 中跨玩家个人资产变更的**来源授权**仍未修复。真实双端地图、个人版本、ACK 与地图模拟任期需独立用户验收后再确认整条业务链。
