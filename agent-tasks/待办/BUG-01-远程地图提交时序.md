# BUG-01 / P1 · 远程命令的地图提交与成功回执顺序

- **来源：** [静态 BUG 交接 BUG-01](../../docs/audits/联机MOD静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待修复，需 Session + 地图权威负责人协调，不得归档。
- **证据路径：** `src/Session/Commands.cs` 的 `ExecuteOnSimulator` 先送 `execution` 后 `PublishLocalMaps`；房主 `AcceptExecution` 提前标终态。`MapUpdate` 提交可失败。静态证据不是双端数据丢失已复现。
- **必须完成：** 以命令 ID / Map Revision / CommitId 建立可靠待提交状态；房主地图权威真实完成才回 `Applied=true`，覆盖断包、超时、模拟权切换、重发、重启 Save/Load，不执行两次原生命令。
- **验收：** 故障注入断开发送；已执行未提交不能成功 ACK；提交后仅一次确认；旧任期/重连/持久化不重复资产副作用；两端相同地图与存档。
- **阻塞：** 当前 43 份历史源码尚无原 Owner 交接和可信实时故障证据；与 BUG-02 的映射生命周期需要同一方案，不用仅调整发包顺序冒充原子性。

## 2026-10-18 / 远程命令初次发送失败回滚与地图先行（局部代码补丁）

- **源码提交：** `3f21d34`。新增 `src/Session/RemoteCommandDispatch.cs` 并在 `src/Session/Commands.cs` 的两个生产调用点接入；源文件已经包含大量其他 Agent 未提交变更，因此只精准暂存自己的 8 行新增、4 行删除，不改变客户端原生命令接口、`CommandResult` DTO、`MapUpdate` Kind、协议号或存档。
- **房主初次 execute：** `RouteGameplayCommand` 先保存本次唯一 `GameCommand` 防御性拷贝，再尝试传输队列。若 `Send(peer, "execute", command)` 抛出任意异常，只撤销这次新入队的确切引用，不留下看似还在模拟者执行的幽灵 `pendingExecutions`；下一次同 ID 请求可以重新投递。如果同步回调已消费/替换该条目，则不会误删更新的事务。其它玩家的待执行命令保持原样。
- **模拟者结果投递：** 保留原有 `executedCommands` 对 CommandId 的 once-only 原生执行缓存，但发送顺序改为 `PublishLocalMaps()` 先于 `Send("host", "execution", result)`。若地图捕获/队列入队立即失败，异常向上报告且不会另外发送成功的执行声明；重复执行结果仍引用原本缓存的返回值。该顺序只保证本地调用和队列的先后，不能证明实际房主已接受地图或保存世界。
- **本地证据：** `dotnet run -c Release --project tools/RemoteCommandDispatchChecks/RemoteCommandDispatchChecks.csproj` 直接编译生产 helper 与 `src/Core/Contracts.cs`、`src/Core/PlayerFleetBattles.cs` 的真实类型，242845 项通过（初次 execute 入队失败、重试同 ID、同步回调重入、其它玩家命令隔离、地图上传失败后绝不发送执行声明、2.2 万轮随机注入）。`PeerDisconnectCleanupChecks` 210502 项、`FinalCheckpointGateChecks` 53972 项同时通过。本地桩并非实际 Unity `Commands` 全流程或跨机联机。
- **尚未完成（本卡必须继续待办）：** `AcceptExecution` 仍可能依据模拟者提供的结果在地图 `CommitId/MapRevision` 完成权威提交之前记录 `Applied=true`；已有待发送地图差量或没有可捕获变化时，简单的 Publish-first 不构成因果证明。必须由 Session/Authority/Transport Owner 联合设计命令 ↔ 地图提交编号与水位的持久状态，并覆盖房主拒绝地图、回执丢包、旧任期/连接重放、保存失败与重开旧档不重复执行。BUG-02 的 deferred reply 生命周期也需联测。当前用户其他 Agent 的 `VERSION=1.30`、CHANGELOG 未提交内容未覆盖，暂不执行三端构建安装或宣称实机通过。

### 2026-10-18 补充：原生拒绝操作时无需等待地图差量

- **补充代码提交：** `5a20738`。模拟端 `RemoteCommandDispatch.PublishBeforeReceipt(result.Applied,...)` 现在只要求 `Applied=true` 的执行声明先提交本地地图；对于 `Applied=false` 的真实拒绝/失败结果，直接入队回执，即使此时地图捕获异常也不会将失败通知一起堵住。
- **补充验证：** 新增生产 helper 本地回归用例，确认未应用结果不会触发可能失败的地图捕获，`RemoteCommandDispatchChecks` 从 242845 增至 **242846 项通过**；断线权威 210502 与最终检查点 53972 项分别通过。本轮共 **507320 项相关本地检查通过**。这只区分声明是否已应用，不表示成功执行已得到房主权威 MapRevision/CommitId 确认，原卡仍待办。
