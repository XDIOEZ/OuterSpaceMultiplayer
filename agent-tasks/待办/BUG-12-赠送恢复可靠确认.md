# BUG-12 / P1 · 赠送恢复通知失败导致地图冻结

- **来源：** [第三轮静态审计 BUG-12](../../docs/联机MOD第三轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Session / Gift / Authority 原 Owner 接管，未修复。
- **证据：** `ClearGiftBarrier` 移除房主屏障后不检查 `gift.resume` 返回值；客端只有收到 resume 才能移除 `giftPausedMaps`，冻结后的异常收口也不完整。
- **完成条件：** 依照 `TransferId+MapId+AuthorityEpoch` 设计幂等恢复确认或与完整权威视图对账；发送失败、超时、重复、取消、换任期和 `SetMapAuthority` 异常都明确重试/锁定/重同步，不提前解除未确认的原生冻结。
- **依赖：** BUG-08 广播失败与 BUG-10 最终检查点可靠 ACK 语义；不能通过无条件 `Clear`/忽略失败伪装修复。
