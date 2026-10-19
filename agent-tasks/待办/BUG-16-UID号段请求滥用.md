# BUG-16 / P1 · 已认证客户端重复领取 UID 号段

- **来源：** [第四轮静态 BUG 审计 BUG-16](../../docs/audits/联机MOD第四轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：待修复及跨 Session / UID Owner 交接。**
- **证据：** 房主处理客户端 `uids` 直接新增 100 万跨度、加入 `IssuedUidRanges` 并全量持久化，没有对应的剩余号段需求、请求幂等或速率验证。
- **必须完成：** 保护已发行号段和真实 UID 高水位，限单人重复/并发领取与全量 Save 频率；客户端接近耗尽时可以合法续领且重试不会重复分配。使用合成客户端压力检查已认证恶意重复 1k 次与正常边界、断线重连、保存失败，不修改用户真实身份及存档；经正式三端编译和原 Owner/用户的运行验收再归档。

## 2026-10-18 / UID 续领关联确认与抗保存洪泛子补丁（实机验收仍待办）

- **源码提交：** `5b28279`。新增 `src/Session/UidRangeRenewalGate.cs` 与 `src/Session/UidRangeRenewal.cs`，仅替换 `Networking.cs` 中 host/client `uids` 两个分支，`Authority.cs` 中 AllocateUid 低水位与耗尽请求的两处调用、`MultiplayerSession.cs` 无倍速 Tick 一行；三个历史混合脏文件使用精确索引替换，其余 Agent 代码未暂存。
- **严格去重（新旧相同 MOD 客户端）：** 继续使用既有 `uids` 消息种类、`UidRange` JSON 字段和 `WireMessage.RequestId`，客户端以当前旧范围 `SessionId/First/Last` 生成稳定 RequestId；重复请求只能重发上次已保存范围，不能重复扩展 `UidHighWater`。房主先将新范围与 `IssuedUidRanges` 登记为待保存，只有 `SaveOrThrow` 成功才回发，同一请求的磁盘保存失败或 Send 失败均不会重新 Reserve/追加；发送回复时回显 RequestId，客户端只接收同会话、与所申请旧号段对应、非重叠且 Next=First 的新范围。旧包、乱序包或重复回执不会倒退客户端已使用的 Next。
- **限流与真实耗尽：** 同一身份新领取最短间隔 30 秒、最多 128 次/会话，断开同一会话再连接仍保留服务端发放节流；保存失败和回复丢失的相同申请每 2 秒最多重试一次，客户端临近耗尽/耗尽后使用未缩放房间时钟每 3 秒重发稳定键。旧协议版本 6 但无 RequestId 的同 Kind 请求保留兼容，每 2 秒最多重复回执、每 30 秒最多新发范围；**旧请求无法证明是否已消费之前的范围，因此只能限频、不能保证超过冷却后的完全幂等**。需要主控发布/协议兼容审查，不能假称混版已验证。
- **本地证据：** `tools/UidRangeRenewalChecks/` 直接联编生产 Session partial 和 grant/client 双 gate，21201 项检查通过（含 1000 次重复、1000 次持久化失败短间隔重试、真实 Session 假网络掉包及旧 ACK 乱序、同会话重连抗冷却绕过、旧客户端兼容、单玩家上限、合法第二次续领）；`AuthenticatedJoinRollbackChecks` 36149、`UnauthenticatedPeerDeadlineChecks` 30071、`IdentityStorageChecks` 97 项，本轮总计 87518 项通过。没有操作玩家真实身份或 SaveStore 世界档；测试 Save/Transport 为桩，不是原版 Unity 双端编译或真实磁盘崩溃恢复。
- **剩余风险 / 验收：** 房主未掌握客端实际已消耗的 UID Next，高频恶意客户端仍可在 30 秒窗口后谎称耗尽；同一会话持续重连也会由认证流程另行发新基础号段，需联合 BUG-06/17 原 Owner 继续限制。需要在 P3、Steam、TapTap 用同版本及混合版本测试响应丢失、真实身份/地图/原生 UID 验证、合法耗尽与 Save 失败、多人号段不重叠以及协议迁移边界。现有 `VERSION=1.30` 与 CHANGELOG 是其他 Agent 未提交内容，本轮不覆盖、不执行三端构建安装或游戏运行。**BUG-16 保持待办，不能视为完全修复。**
