# BUG-09 / P2 · 房间 UI 忽略 Submit 同步拒绝

- **来源：** [第二轮静态审计 BUG-09](../../docs/联机MOD第二轮静态BUG审计与Agent修复交接-20261017.md)。
- **状态：** 待 UI / Entry Owner 签收和独立回归，未修复。
- **证据：** `MultiplayerRuntime.Submit` 在 `Perform(Action)` 内调用 `Session.Submit(command)` 却丢弃结果；`CommandResult.Fail` 早期拒绝并不抛异常或触发 `CommandCompleted`。
- **完成条件：** 分别模拟同步失败、已排队、最终完成状态：失败显示准确原因；排队只告知等待，不能当已成功；成功和失败回执不会出现重复提示。精确修改已签收的 Entry 文件并执行逻辑与游戏 UI 验收，提交记录独立于其它 BUG。
