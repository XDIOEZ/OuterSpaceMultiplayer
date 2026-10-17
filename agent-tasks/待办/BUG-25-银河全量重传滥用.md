# BUG-25 / P2 · galaxyReset 可重复强制全量银河传输

- **来源：** [第六轮静态 BUG 审计 BUG-25](../../docs/联机MOD第六轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：待 Session + Game 银河同步 Owner 交接和定量基准。**
- **证据：** 已认证 peer 的每次 `galaxyReset` 当前可触发 `bridge.ResetGalaxyRecipient`，未区分已经安排的重同步与重复请求；约 0.5 秒银河采样周期内可能不断失去压缩基线。
- **完成条件：** 单个玩家的重同步请求有界/幂等、带完成水位与退避，不会重置其他玩家共享采样缓存。对合成 1/10/100 次 reset 的发送字节、10 秒 Full frame 数量和正常客人的 Tick 影响进行对照，再完成双端回归。
