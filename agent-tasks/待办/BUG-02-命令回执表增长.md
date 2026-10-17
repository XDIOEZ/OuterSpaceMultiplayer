# BUG-02 / P2 · commandReplyPeers 生命周期泄漏

- **来源：** [静态 BUG 交接 BUG-02](../../docs/联机MOD静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待修复，不可归档；依赖 [BUG-01 命令终态](BUG-01-远程地图提交时序.md) 的语义稳定。
- **证据：** `src/Session/Networking.cs` 处理 `command` 先写映射，同步或拒绝命令不经过 `Commands.cs/AcceptExecution` 的唯一释放路径；`RemovePeer` 也未释放该表。
- **需要：** 同步完成、即时拒绝、重试、断线、超时统一释放映射；异步 pending / 已执行未提交的命令保留有效路由，不能提前丢最终回执。
- **验收：** 数千次即时和失败命令不产生不可回收增长；peer 断开后无残余；代理异步命令确认一次且不丢失。
