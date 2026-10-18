# BUG-17 / P1 · 地图差量新实体归属缺少 UID 来源校验

- **来源：** [第四轮静态 BUG 审计 BUG-17](../../docs/联机MOD第四轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：待 Session/Authority 与 Game 原 Owner 联合确认。**
- **证据：** `owner` 显式注册消息对发行 UID 号段做验证，但 `MapUpdate.EntityOwners` 中新 UID 只验证玩家存在/无已有归属冲突，未经过相同的来源校验便可能进入 `bridge.ApplyMapUpdate`。
- **必须完成：** 在原生地图事务写入之前，区分已知实体、合法客户端分配 UID、房主 NPC/原生保留 UID，拒绝未知新增 UID、任期过期/跨世界冲突；不得重判老玩家实体归属，也不能阻断真实地图合法生产。与 BUG-16 UID 发行规则先交叉审，完成畸形差量故障注入、三端构建和实际多人验收后再归档。

## 2026-10-18 / 房主提交前校验远端模拟者的 UID 来源（源码子补丁）

- **代码提交：** `99ac673`。新增 `src/Session/MapEntityUidValidation.cs`，仅在 `Authority.AcceptMapUpdate` 中原有 EntityOwners 冲突检查之后、设置 `applyingOwners` / 执行 `bridge.ApplyMapUpdate` 之前插入 8 行调用。对房主从远端 peer 接收的地图增量执行；房主自己模拟的地图和客人接收房主已确认的更新仍使用原流程。已有 Authority 其他 Agent 的混合未提交代码没有进入 Git 暂存。
- **可信旧身份：** 已由房主当前 `World.EntityOwners` 或该地图先前已确认的 `Checkpoint.EntityOwners` 记录，且新包提交的原 owner 完全一致的 UID，可继续随地图运输/访问，不用用当前新模拟者 UID 范围重判。二者记录互相矛盾也会拒绝。未登记的新 UID 必须属于本次模拟者 `IssuedUidRanges` 中 `SessionId` 与房主当前会话相同的有效范围；归属本身可以是合法基地所有者或访客，但不能用其他玩家已发的陌生号段凭空创建未登记资产。
- **原生前拒绝：** 负数、未知 owner、未授权房主/访客新 UID、跨房间过期号段、原 owner 篡改在原生 MapUpdate 写入之前一律拒绝，不登记到房主的世界 EntityOwners。原有地图模拟者/任期与 Revision 门禁、重复提交回执、个人进度版本验证、NativeDeltaJson 和协议/存档格式均保持不变。注意这只是 UID 来源防护，不能凭此证明全部地图增量经济行为不可作弊。
- **本地回归证据：** `dotnet run -c Release --project tools/MapEntityUidValidationChecks/MapEntityUidValidationChecks.csproj` 真正链接生产验证器和 `src/Core/Contracts.cs`/`PlayerFleetBattles.cs` DTO，26,959 项通过，包含 4,000 个合法 owner 后恶意外来 UID 的整批拒绝、14,000 个随机混合地表、旧检查点/房主已知访客、最大 64 位 UID、会话隔离和合法建筑归属。已有 `tools/UidRangeRenewalChecks` 21,201 项、`tools/OptimizationChecks` 185 项通过；**均为 .NET8 本地验证，非完整原生 Unity/Steam/TapTap 编译和双端运行**。
- **尚待验证（卡仍待办）：** Game Owner 需在真实地表建造、协作访客造物、雇员运输、房主生成/注册 NPC、旧存档已知 UID 进入新模拟任期时确认 host owner 注册事件不会遗漏；Session Owner 需覆盖 BUG-16 的新旧会话 UID 号段与 `owner` 认证包在真实可靠流中的先后顺序、地图拒绝与正确重同步，以及跨机 TCP/Steam 实际恶意增量。VERSION/CHANGELOG 现有其它 Agent 未提交 1.30 状态不动，本轮未安装三端、不推送；真实验收后才能考虑本卡归档。
