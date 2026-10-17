# BUG-17 / P1 · 地图差量新实体归属缺少 UID 来源校验

- **来源：** [第四轮静态 BUG 审计 BUG-17](../../docs/联机MOD第四轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：待 Session/Authority 与 Game 原 Owner 联合确认。**
- **证据：** `owner` 显式注册消息对发行 UID 号段做验证，但 `MapUpdate.EntityOwners` 中新 UID 只验证玩家存在/无已有归属冲突，未经过相同的来源校验便可能进入 `bridge.ApplyMapUpdate`。
- **必须完成：** 在原生地图事务写入之前，区分已知实体、合法客户端分配 UID、房主 NPC/原生保留 UID，拒绝未知新增 UID、任期过期/跨世界冲突；不得重判老玩家实体归属，也不能阻断真实地图合法生产。与 BUG-16 UID 发行规则先交叉审，完成畸形差量故障注入、三端构建和实际多人验收后再归档。
