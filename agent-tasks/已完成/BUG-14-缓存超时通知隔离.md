# BUG-14 / P1 · 缓存超时通知异常使房主 Tick 全局停止

- **来源：** [第三轮静态审计 BUG-14](../../docs/联机MOD第三轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：已完成代码修复与生产方法故障注入（2026-10-17）。** 用户实际多机运行回归单独保留在 [待办/BUG-14-跨机运行回归](../待办/BUG-14-跨机运行回归.md)，不把 BuildOnly 当成已安装或实机通过。
- **证据：** `TickMapCache` 删除 60 秒未回复的 offer 后给对应 peer 发送普通 `error`，队列满时 `Send` 可抛异常；外层 `MultiplayerRuntime.Update` 发生 `Tick` 异常后将 `tickFailed=true`，停止后续推进。
- **完成条件：** 非权威的缓存超时提示发送失败不应停止整个房主 Tick，失败方应明确断开或隔离并可通过权威重同步恢复；使用假 transport 注入通知发送异常，确保其它 peer 时钟/收包仍推进且不忽略真正地图提交错误。
- **依赖：** 与 BUG-07 的断线权威收口保持一致，不能用广泛 catch 掩盖原生应用后的半提交。

## 实际完成的代码与验收

- **提交：** `ac45398`，V1.24 / 联机协议 6 / 存档 1；生产 `TickMapCache` 移至 `src/Session/MapCacheExpiry.cs`，原 `MapSnapshotCache.cs` 仅移除重复函数定义，不更改地图编码、Apply、ACK、原生状态或模拟权算法。
- **修复：** 仅当可选 `error` 通知因 `IOException`（队列满/连接关闭）或 `InvalidOperationException`（对端已不存在）失败时，尝试对该 peer 调用真实 `transport.Disconnect`；缓存 offer 和内存额度照常释放，剩余 peer 继续执行；断开本身失败会报告警告且不继续向外抛出。非法格式等非传输异常仍传播，客户端等待真实地图的超时仍沿原 `FailMapLoading` 路径报错，不吞掉任何权威提交错误。
- **生产方法直接注入：** `tools/OptimizationChecks/MapCacheExpiryChecks.cs` 与 `MapCacheExpiryStubs.cs` 在 .NET8 测试工程里**直接编译生产 `src/Session/MapCacheExpiry.cs`**；模拟 60s 前、队列拥塞、失效连接、断开再失败、其他 peer 同 Tick 正常通知、已释放内存不重复操作、断开失败的日志观察者再次抛错、非传输程序错误仍传播以及实际本地地图等待超时。
- **实测工具结果：** `dotnet run --project tools/OptimizationChecks/OptimizationChecks.csproj -c Release` → **109 项通过**（此前 97 + 新增 12）；`Sync-ModCopies.ps1 -Target All -BuildOnly` → P3 / Steam / TapTap 均成功，V1.24 / 1.24.0.0，三端源码指纹 `97E9AB824A22C1F9843432F0C77B869613751E63190928C5910C3C064C081542`。
- **交付限制：** 原 Session/Transport 历史混合修改未触碰；未运行 Unity/双机游戏，未修改游戏本体、玩家存档或运行 MOD 安装副本，未 `push`。真实 `RemovePeer` 异常下的模拟权恢复仍属于 [BUG-07](../待办/BUG-07-断线权威收口.md) 的独立未完成范围。
