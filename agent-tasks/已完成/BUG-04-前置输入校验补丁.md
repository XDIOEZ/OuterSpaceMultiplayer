# BUG-04 / P2 · 生产前置校验修复（代码范围）

- **来源：** [静态 BUG 交接 BUG-04](../../docs/audits/联机MOD静态BUG审计与Agent修复交接-20261017.md)。
- **状态：已完成并归档（2026-10-17）。** 代码补丁和静态构建验收通过；运行时消息注入仍需用户或对应 Owner 完成。
- **独立范围：** 只修 `AcceptMapUpdate` 缺失 `Players` 非空校验导致原生地图应用成功后在 `foreach` 抛空引用的确定性缺陷；**本卡不宣称完成游戏端故障注入与多人验收**，后者单列 [BUG-04 集成验收](../待办/BUG-04-畸形输入集成验收.md)。

## 当前修改

- `src/Session/Authority.cs`：在任何原生 `bridge.ApplyMapUpdate` 之前，和 `EntityOwners` 一起校验 `Players != null`。正常本机捕获/线协议始终填非空列表，空列表合法，`null` 不合法。
- `VERSION`：1.19 → 1.20；`CHANGELOG.md`、`src/Entry/ModVersion.cs` 注释同步更新。无协议或存档版本变动。
- **不包含：** 对 BUG-01、02、03、05 的事务语义修改、覆盖其他未提交源码、安装三端/游戏运行验收。

## 本卡关闭门槛

- [x] 补丁在 `bridge.ApplyMapUpdate` 前执行；空列表非 `null`，能通过这一输入校验；缺失列表直接拒绝。
- [x] Project3 / Steam / TapTap 真实目标引用的 `Sync-ModCopies.ps1 -Target All -BuildOnly` 均成功（V1.20 / Assembly 1.20.0.0，`SourceRevision=555515D61CAB2E0ED5B631FB00EA9C6EBD3EED98FAB2D9B4A28F08F0635967A2`）。**仅打包，未安装、未启动游戏**。
- [x] 现有 83 项优化、21 项地图缓存、53 项身份/保存共 157 项纯逻辑回归通过；本次无新增的真实空 `Players` 运行消息注入测试。
- [x] 精确 staged patch 核对仅含 Authority 3 加 1 删的目标 hunk，另有 VERSION / CHANGELOG / ModVersion 注释，共 4 文件独立提交 `9e24be5`；旧 Authority 混合差异仍保留为未提交，未连带暂存。

**归档边界：** 本卡只是可构建的输入校验修复，并非完整功能/联机验收；仍需由用户/Owner 在隔离地图注入 `Players=null`、`[]`、合法个人差量及旧消息，检查地图没有半提交。那是独立待办，不能当成已验收。三端已安装副本仍为旧源码，发布门禁不放行。
