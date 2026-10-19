# BUG-25 / P2 · galaxyReset 可重复强制全量银河传输

- **来源：** [第六轮静态 BUG 审计 BUG-25](../../docs/audits/联机MOD第六轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：待 Session + Game 银河同步 Owner 交接和定量基准。**
- **证据：** 已认证 peer 的每次 `galaxyReset` 当前可触发 `bridge.ResetGalaxyRecipient`，未区分已经安排的重同步与重复请求；约 0.5 秒银河采样周期内可能不断失去压缩基线。
- **完成条件：** 单个玩家的重同步请求有界/幂等、带完成水位与退避，不会重置其他玩家共享采样缓存。对合成 1/10/100 次 reset 的发送字节、10 秒 Full frame 数量和正常客人的 Tick 影响进行对照，再完成双端回归。

## 2026-10-18 / per-player 银河重同步退避子补丁（真实双端验收仍待办）

- **生产源码提交：** `4300276`。新 `src/Session/GalaxyResetRequestGate.cs` 按已认证 PlayerId 隔离每个玩家的重同步状态；`Networking.cs` 的 galaxyReset 不再无条件执行 Reset，收到首次请求立即重置，重复/退避内的请求合并为唯一待执行请求；冷却 2/4/8/16 秒，30 秒无重置后恢复最短冷却。请求进入退避队列后即使客户端不再重复发送，房主在正常 0.5 秒银河 Tick 中也会自动执行，到发送 full frame 并完成 `ConfirmGalaxySent` 才释放“等待完整基线入队”标记。
- **双发送路径：** 周期广播和舰战入场共用 `SendAndConfirmGalaxy`，不再漏掉舰战入场时的 full 入队水位。网络 Send 或桥接确认失败不把 full 标为完成；缺包或客户端 full 原生应用失败后重发 galaxyReset 可以进入下次受限重同步，不会永久屏蔽合法请求。
- **缓存独立性：** `GameBridge.ResetGalaxyRecipient` 只清该玩家的 `galaxyRecipients` 发送状态，不再将 `fleetSampleFrame=-1` 或 `nextSharedCapture=0` 作用于所有玩家。断链 RemovePeer 移除该玩家排队状态，重连不会继承旧冷却。
- **测试：** `dotnet run -c Release --project tools/GalaxyResetGateChecks/GalaxyResetGateChecks.csproj`，真实生产 gate 与发送 partial 编译链接共 67924 项通过；舰队采样缓存 374473 项、FleetDeltaChecks 20 项、FleetBattleTickChecks 22 项通过。合成一名扰动者在 10 秒里发送 1/10/100 次 Reset，20 个正常银河发送周期中 full 帧分别 1/3/3，普通玩家保持 20 个增量帧。合成字节假设 full 131072 B、delta 512 B：100 次请求产生 401920 B；这是模型假设，不是 Unity 网络实测，不代表真实带宽或 CPU/P95。
- **仍待补齐：** Steam/TCP 真实双端注入重复 reset、全量帧原生解码失败、ACK/发送队列故障、舰战入场切换与重连，并核对真实日志和旧档。现行协议消息种类、版本/存档没有更改；仓库现存 VERSION/CHANGELOG 的其他 Agent 1.30 未提交历史内容无法在本子补丁内安全单独递增，主控统一集成后按仓库版本政策收口。不得将本卡移到已完成或声称已安装。
