# BUG-14 / P1 · 缓存超时通知异常使房主 Tick 全局停止

- **来源：** [第三轮静态审计 BUG-14](../../docs/联机MOD第三轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 Session/Transport 原 Owner 接手和注入测试，未修复。
- **证据：** `TickMapCache` 删除 60 秒未回复的 offer 后给对应 peer 发送普通 `error`，队列满时 `Send` 可抛异常；外层 `MultiplayerRuntime.Update` 发生 `Tick` 异常后将 `tickFailed=true`，停止后续推进。
- **完成条件：** 非权威的缓存超时提示发送失败不应停止整个房主 Tick，失败方应明确断开或隔离并可通过权威重同步恢复；使用假 transport 注入通知发送异常，确保其它 peer 时钟/收包仍推进且不忽略真正地图提交错误。
- **依赖：** 与 BUG-07 的断线权威收口保持一致，不能用广泛 catch 掩盖原生应用后的半提交。
