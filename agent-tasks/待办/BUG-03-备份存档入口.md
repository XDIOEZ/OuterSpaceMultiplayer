# BUG-03 / P2 · 备份代有效但继续游戏列表不可选

- **来源：** [静态 BUG 交接 BUG-03](../../docs/联机MOD静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待修复，不可归档；需要 Core / Entry 原 Owner 的范围交接。
- **证据：** `SaveStore.ListWorlds` 只看 `active.json`，但 `SaveStore.Load` 可尝试 `active.json.bak`；Entry 对 `WorldSummary.Error` 禁止 `HostLoad`。
- **需要：** 列表与真实 `Load` 共用**只读可恢复性判断**，显式说明回退代；没有合法备份时不可虚报“可恢复”，浏览列表不得自动修复或覆盖损坏索引。
- **验收：** 临时隔离数据模拟主/备份均好、主索引坏但备份与代完整、主/备份均坏；列表状态和实际可加载性完全一致。用户真实存档不可读写用于注入。
