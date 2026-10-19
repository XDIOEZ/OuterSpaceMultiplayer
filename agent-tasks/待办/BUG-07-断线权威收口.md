# BUG-07 / P1 · 断线清理异常导致地图模拟权残留

- **来源：** [第二轮静态审计 BUG-07](../../docs/audits/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Session / GameBridge Owner 联合接手，未修复。
- **证据：** `Networking.RemovePeer` 解绑后先尝试原生 `SetPlayerOnline(false)` 等操作；一旦抛出异常，会跳过 `Visitors` 清除、`ReassignMap` 和待执行命令收尾。
- **完成条件：** 隔离可回滚的会话权威清理和原生表现失败；对通信、舰战、离线标记各项异常注入，离线玩家不保留已失效模拟任期，另一合法模拟者从已确认快照接管，不重放原生命令。提交和游戏回归分别记录。

## 2026-10-18 / 断线清理异常隔离与地图租约强制撤销（源码子补丁）

- **源码提交：** `dba3e64`。新增 `src/Session/PeerDisconnectCleanup.cs`，将 `Networking.RemovePeer` 原有串行收口委托到 `RemovePeerAuthorityFirst`。长期混合脏 `Networking.cs` 只精确提交这一个入口替换（1 行新增、15 行删除）；已有未提交 `rejectedPeers` 握手清理继续留在当前工作树边界，由原 Owner 后续集成。
- **失败独立性：** 对缓存清理、聊天/参观通知、舰战中断、原生玩家舰队离线和房间政策广播逐段故障隔离；任一步异常也继续移除旧玩家的所有地图访客引用、尝试各图 `ReassignMap` 及清除该玩家 `pendingExecutions`。只清本次 peer/playerId 精确双向路由，其他玩家在线标记、地图和待执行命令保持不变；重复 Disconnect 不产生第二次清理或新的 UID/资产副作用。
- **防止模拟权悬挂：** 任一地图移交抛异常（包括原生检查点捕获前或新任期已发布之后），强制清除 `SimulatorPlayerId` 与 `AwaitingAuthorityReady`、提高 `AuthorityEpoch`、转 Frozen 并要求 `bridge.SetMapAuthority(Frozen)`，且继续处理后续其它地图。舰战、原生舰队冻结或广播权威政策失败会锁定 `nativeFault`、暂停房主推进/保存；清理过程失败不会被伪装成成功移交。通知错误不涉及权威状态时可记录错误并继续房间。
- **本地证据：** `dotnet run -c Release --project tools/PeerDisconnectCleanupChecks/PeerDisconnectCleanupChecks.csproj` 真实生产 helper/partial 和 `Core/Contracts.cs` / `PlayerFleetBattles.cs` DTO 联编，210502 项断线清理顺序、原生失败、聊天、舰战、无/多地图、旧租约撤销、半次分配、15k 随机交错及二次故障检查通过；原登录回滚 36149、最终检查点 53972、舰战 Tick 22、通讯/参观 Tick 90、赠送恢复 60147 项通过。本轮相关 6 组共 360882 项本地逻辑检查，**不是 Unity/真实网游双端验证**。
- **未完成：** 需要 Session/Transport/Game Owner 在真实 P3、Steam、TapTap 断线/续开、游客地图接管、离线舰队、正在进行的舰战与跨图运输中验证旧任期失效后另一设备从房主最后完整已确认检查点接管；补测桥接原生冻结失败后手动恢复的正确流程、网络回执及 BUG-01/10 的关键事务保存门禁。不得把失败原生状态静默存档或误称跨机通过。VERSION/CHANGELOG 原 Owner 尚未提交的 1.30 工作未修改，未安装运行副本或推送。**BUG-07 主卡保持待办，不移动到已完成。**
