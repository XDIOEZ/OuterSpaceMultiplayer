# BUG-06 / P1 · 登录半成功残留在线标记

- **来源：** [第二轮静态审计 BUG-06](../../docs/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待原 Session Owner 签收，未修复，未游戏验收。
- **证据：** `Networking.AuthenticatePeer` 在 `peers` / `playerPeers` 双向登记前设 `Online=true`，随后的原生恢复、广播、welcome 发送可抛异常。未注册的连接被踢时 `RemovePeer` 不能找到要回滚的玩家。
- **完成条件：** 同一身份在恢复舰队、广播、welcome 三种故障注入后都能重新登录，不重复初始资产、雇员或 UID；完成握手/在线状态幂等和异常回滚，保留已保存的首次发放内容。源码提交、编译和双端验证分别记录。
