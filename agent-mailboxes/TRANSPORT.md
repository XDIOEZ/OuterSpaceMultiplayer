# TRANSPORT · 网络 Agent 邮箱

> 用于连接、封包、传输层和协议相关留言。具体文件占用以 `../AGENT-COORDINATION.md` 为准。

## 消息

### MSG-20261017-agent-tasks-bug19-transport-01 — BUG-19 认证前超大帧修复交接
- 发件人：agent-tasks-bug19-preauth-frames-20261017
- 收件人：TRANSPORT
- 时间：2026-10-17（本轮）
- 状态：待处理
- 关联任务/文件：`agent-tasks/已完成/BUG-19-未认证大包解析.md`、`agent-tasks/待办/BUG-19-双端真实握手与地图回归.md`
- 消息：V1.28 `9a7f3d6` / `83176da`：TCP 接收帧头与 Steam 重组前限制未认证帧 16 KiB，完成 welcome 后经 RoomTransport 将合法 peer 升级正常 128MiB 地图帧额度；124 项逻辑检查和 7 项真实 localhost TCP 回环通过，P3/Steam/TapTap BuildOnly 通过。历史网络混合差异原样保留，未安装/运行游戏。
- 需要回复：原 Transport/Session Owner 在干净 Git 源码闭包后，请由用户做真实 Steam/TCP 双端连接与大地图恢复；BUG-11 认证超时和 BUG-13 凭证安全仍必须独立处理。
- 回复关联：无

### MSG-20261018-bug11-preauth-guard-transport-01 — 未认证连接超时与名额保护代码交接
- 发件人：bug11-preauth-deadline-20261018
- 收件人：TRANSPORT
- 时间：2026-10-18
- 状态：仅通知
- 关联任务/文件：`agent-tasks/待办/BUG-11-未认证连接占位.md`、`src/Session/UnauthenticatedPeerDeadline.cs`、`src/Session/Networking.cs`
- 消息：提交 `b3488d0` 已增加同房主的所有 Connected peer 认证前 30 秒绝对期限和 32 个待验证连接名额上限；重复 hello 不续期，完成 Welcome 和 MarkAuthenticated 才释放计时。实际调用 transport.Disconnect，IO/状态异常两秒重试；新 .NET8 生产 partial/helper 检查 30071 项通过，TCP 真实 localhost 入站检查 7 项通过。未改 TCP/Steam 源码、wire 版本、存档或游戏运行副本，其他 Agent 混合代码保留。
- 需要回复：请 Transport Owner 协调在 Steam/TCP 各测不发 hello、hello 后不 auth、欢迎包队列错误、有效慢速连接、30 秒后恢复名额及 32/33 连接压力，并联动 BUG-06 认证半成功回滚；本次非真实双端验收。
- 回复关联：MSG-20261017-agent-tasks-bug19-transport-01
