# BUG-20 · 非法关系枚举的生产代码防护

- **来源：** [第五轮 BUG-20 静态审计](../../docs/audits/联机MOD第五轮静态BUG审计与Agent修复交接-20261017.md)。
- **范围：已完成（2026-10-17）**；只记录已验证的代码修复与离线合成存档检查。Unity 游戏 UI、房主/客人实机回归仍由 [原 BUG-20 待办](../待办/BUG-20-非法玩家关系枚举.md) 跟踪。
- **代码提交：** `807ac9d`；版本 **V1.27**；联机协议仍为 6、存档 Schema 为 1。

## 改动与回滚

- 新增 `src/Core/RelationValidation.cs`：显式只允许 `Neutral / Friendly / Hostile`，不使用可能随枚举扩展而放宽的数值转换规则。
- `src/Session/Commands.cs` 的 `ChangeRelation` 在 `FindRelationship(create:true)`、原生 `ApplyRelationship` 和 `BroadcastPolicy` **之前**验证；非法整型关系无法新建或修改权威关系记录。
- `src/Core/Storage.cs` 的 `Save` 与 `Load` 均验证关系表。加载旧档时如果最新代具有非法关系，会按原有安全策略尝试完整有效备份；没有合法备份则拒绝加载，不偷偷解释为中立或改变原档字节。保存非法关系时在建目录/写代次之前拒绝。
- 保留其他 Agent 的 `Commands.cs`、`Storage.cs` 历史混合修改，Git 仅提交两处新生产验证 hunk；回滚可由主控精确回退提交 `807ac9d`。

## 验证证据

- `tools/IdentityStorageChecks/RelationValidationChecks.cs` 直接编译生产 `RelationValidation.cs` 和实际 `SaveStore`；合成中立/友好双向同意/敌对存档正常往返；`-1/3/99`、缺关系表、空关系记录、旧档恶意 enum 值（**故意重算合法 SHA**）均拒绝，合法旧代回退生效，损坏字节保留。
- `dotnet run --project tools/IdentityStorageChecks/IdentityStorageChecks.csproj -c Release`：**97 项通过**（此前 78 + 本轮 19）。
- `Sync-ModCopies.ps1 -Target All -BuildOnly`：Project3 / Steam / TapTap 真实目标引用编译打包**全部通过**，程序集 `1.27.0.0`，构建输入 `SourceRevision=05C39F9F75573ABD6C3DCD97ED54E0E3A4B620F26ECA928DCF911EA664EC8C2D`；没有更新三处运行副本。
- **未宣称：** 真实游戏按钮、友好申请、原生仇恨、游戏中旧档恢复及多人实测已经通过。这些保留在原待办。
