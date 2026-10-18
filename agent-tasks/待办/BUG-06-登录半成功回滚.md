# BUG-06 / P1 · 登录半成功残留在线标记

- **来源：** [第二轮静态审计 BUG-06](../../docs/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待原 Session Owner 签收，未修复，未游戏验收。
- **证据：** `Networking.AuthenticatePeer` 在 `peers` / `playerPeers` 双向登记前设 `Online=true`，随后的原生恢复、广播、welcome 发送可抛异常。未注册的连接被踢时 `RemovePeer` 不能找到要回滚的玩家。
- **完成条件：** 同一身份在恢复舰队、广播、welcome 三种故障注入后都能重新登录，不重复初始资产、雇员或 UID；完成握手/在线状态幂等和异常回滚，保留已保存的首次发放内容。源码提交、编译和双端验证分别记录。

## 2026-10-18 / 登录失败后撤销半成功在线状态（代码子补丁）

- **源码提交：** `646d8fd`。在 `src/Session/Networking.cs` 的 `SaveOrThrow()` 后，将原有登录尾段交由真实 `src/Session/AuthenticatedJoinRollback.cs` 统一执行；该主文件已有别的 Agent 未提交混合修改，Git 仅精准替换原尾段 1+/13-，其它改动完整保留。
- **保留首次资产：** `SaveOrThrow` 仍先保存已建立的身份、首次雇员/舰队与 UID 号段，随后才尝试 `Online=true`、原生 `SetPlayerOnline`、`playerJoined` 广播、peer 双向登记、Welcome 入队、transport `MarkAuthenticated` 以及进入地图。失败后不撤销这些已保存的首次发放内容，也不重新建同 ID 的母舰或再次领取 UID。
- **失败收口：** 广播/欢迎前还未登记 peer 时，先设置 `Online=false`、恢复 native 离线状态并尝试发送纠正政策；双向 peer 已登记时复用现有 `RemovePeer` 的聊天/战场/访客/模拟权退出清理。无论业务清理成功与否，最后只删除与该次 peer 匹配的映射并尝试真实 Disconnect；异常传播保留首个登录错误。冻结失败锁定原生权威，断开失败重新加入未认证计时器等待后续驱逐。
- **本地回归：** `dotnet run -c Release --project tools/AuthenticatedJoinRollbackChecks/AuthenticatedJoinRollbackChecks.csproj`：直接编译生产方法及原生产未认证握手 gate，注入原生上线失败、其他玩家广播失败、welcome 发送失败、transport 认证升级失败、地图加载和状态通知失败，并覆盖 RemovePeer/原生冻结/断链/错误通知自身再次失败，含 12,000 轮随机入场交错，36,149 项通过。另 `UnauthenticatedPeerDeadlineChecks` 30,071 项、`IdentityStorageChecks` 97 项、`UnauthenticatedTcpChecks` localhost 7 项及 `OptimizationChecks` 185 项通过。身份/初始资产/UID 未重复的证据来自生产方法加测试替身，而非真实 Unity 雇员创建。
- **仍未闭环：** 需要原 Session/Transport Owner 在双端真实地图加载、原生启用舰队失败、`playerJoined` 旁观者满队列、welcome 丢包、断链清场和重连保存代上验证；若 `RemovePeer` 或恢复权威本身失败，原生锁定仍需人工恢复路径。当前 MOD 版本 `VERSION=1.30` 是其他 Agent 现有未提交修改，主控整合前不能擅自覆盖或宣布新安装。**本卡继续待办，不移动到已完成。**
