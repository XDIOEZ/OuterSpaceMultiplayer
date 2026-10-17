# BUG-01 / P1 · 远程命令的地图提交与成功回执顺序

- **来源：** [静态 BUG 交接 BUG-01](../../docs/联机MOD静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待修复，需 Session + 地图权威负责人协调，不得归档。
- **证据路径：** `src/Session/Commands.cs` 的 `ExecuteOnSimulator` 先送 `execution` 后 `PublishLocalMaps`；房主 `AcceptExecution` 提前标终态。`MapUpdate` 提交可失败。静态证据不是双端数据丢失已复现。
- **必须完成：** 以命令 ID / Map Revision / CommitId 建立可靠待提交状态；房主地图权威真实完成才回 `Applied=true`，覆盖断包、超时、模拟权切换、重发、重启 Save/Load，不执行两次原生命令。
- **验收：** 故障注入断开发送；已执行未提交不能成功 ACK；提交后仅一次确认；旧任期/重连/持久化不重复资产副作用；两端相同地图与存档。
- **阻塞：** 当前 43 份历史源码尚无原 Owner 交接和可信实时故障证据；与 BUG-02 的映射生命周期需要同一方案，不用仅调整发包顺序冒充原子性。
