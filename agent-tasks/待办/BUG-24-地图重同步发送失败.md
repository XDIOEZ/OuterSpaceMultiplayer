# BUG-24 / P1 · 地图重同步请求发送失败后无法重试

- **来源：** [第六轮静态 BUG 审计 BUG-24](../../docs/联机MOD第六轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：待 Session/MapViewLoading 原 Owner 接手与假传输注入。**
- **证据：** `RequestMapResync` 先把地图加入 `requestedMapResyncs` 再发出 `Send("host","resync")`；如果队列满发送异常，没有撤销标志，后续请求被视为已发送，直到 60 秒加载超时。
- **完成条件：** 发送失败立即恢复可重试状态，发送成功后有有限、有退避的等待与超时，重复请求幂等且不引起全量地图风暴。使用假 transport 测第一次失败后重试、旧视图、重复任期与连续失败；真实游戏两端同一地图/个人进度可重同步。
- **边界：** 未经原 Owner 签收不能覆盖 `MapViewLoading.cs` 的相关未提交部分；BUG-01 权威 ACK 不可通过忽略发送错误绕过。
