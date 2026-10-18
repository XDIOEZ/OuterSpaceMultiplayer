# BUG-12 / P1 · 赠送恢复通知失败导致地图冻结

- **来源：** [第三轮静态审计 BUG-12](../../docs/联机MOD第三轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Session / Gift / Authority 原 Owner 接管，未修复。
- **证据：** `ClearGiftBarrier` 移除房主屏障后不检查 `gift.resume` 返回值；客端只有收到 resume 才能移除 `giftPausedMaps`，冻结后的异常收口也不完整。
- **完成条件：** 依照 `TransferId+MapId+AuthorityEpoch` 设计幂等恢复确认或与完整权威视图对账；发送失败、超时、重复、取消、换任期和 `SetMapAuthority` 异常都明确重试/锁定/重同步，不提前解除未确认的原生冻结。
- **依赖：** BUG-08 广播失败与 BUG-10 最终检查点可靠 ACK 语义；不能通过无条件 `Clear`/忽略失败伪装修复。

## 2026-10-18 / 发送队列与客户端冻结身份子补丁（主卡仍待办）

- **生产代码提交：** `0326221`。修改 `src/Session/PlayerGiftCheckpoint.cs`；在 `src/Session/MultiplayerSession.cs` 通用 Tick 仅接入定期重试，在 `src/Session/Authority.cs` 的地图视图接收处补清理当次冻结身份；这两个历史脏文件分别仅提交 7 行/1 行，不纳入其他 Agent 的未提交内容。
- **房主：** `ClearGiftBarrier` 在调用发送之前保存一次性原恢复请求；若发送队列拒绝或抛错，则保留等待，每 1 秒（非游戏倍速）尝试将 `gift.resume` 重新入队。同地图且原模拟者相同时，旧恢复通知未排队前不允许下一笔赠送；换模拟者或地图消失时丢弃无效旧通知。允许同模拟者在 RebaseMap 后任期增长，但原恢复消息仍引用当初冻结的授权。
- **客户端：** 独立记录当前真实冻结的 `MapId+TransferId+AuthorityEpoch`；忽略旧赠送/重复恢复通知，拒绝覆盖仍冻结的另一笔赠送；对原生 `SetMapAuthority` 先验证真实成功，成功后再从 `giftPausedMaps` 和待发 `gift.flushed` 清理。恢复失败保留原身份及冻结，以便后续重发；完整地图视图切代也清理旧身份。保持已发送 `gift.flushed` 后移除快照队列、实际恢复前继续冻结的原语义。
- **本机验证：** `tools/GiftResumeReliabilityChecks/` 直接编译生产 PlayerGiftCheckpoint 与 VisitTickSnapshot，使用同一套假原生/传输边界注入：60147 项通过（拒绝入队/异常、重试频率、重复/乱序旧通知、模拟者换人、原生恢复失败、地图任期升级、随机交错）；原 `tools/GiftFlushTickChecks/` 78612 项和 `tools/FinalCheckpointGateChecks/` 53972 项通过。未进行 Unity/三端正式编译、游戏场景或跨机联机。
- **尚未完成：** 发送队列接受消息只代表已排队，不等于客户端已成功恢复；仍需新的幂等端到端 `gift.resumed`（或带水位的权威状态对账）及版本/协议迁移评审、ACK 丢失和重连/超时补偿、BUG-08 队列隔离、多端真实运行验收。此卡维持 `待办/`，不得视为已完成。当前 `VERSION/CHANGELOG` 中他人未提交 1.30 不能直接覆盖，发布版本由主控完成源码交接后处理。
