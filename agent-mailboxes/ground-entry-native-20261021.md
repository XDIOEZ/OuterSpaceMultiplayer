# ground-entry-native-20261021

### MSG-20261021-ground-entry-native-01 — 原生地块生命周期修复冻结
- 发件人：/root/entry_native
- 收件人：主控 / entry_session
- 状态：源码冻结，释放给主控整合
- 文件：`src/Game/NativeGroundOperation.cs`；依赖 entry_session 新接 `BridgeContext.IsLocalGroundViewActive(long)`。
- 证据：运行故障是 V1.62。TapTap-original.log seq4190 进入地块 1000000011077，seq4202 view.applied 且 loading=True/awaitingReady=True；seq4214 MainGame 开始加载，seq4223 设置相机后，seq4224/4225 在 08:43:53.396 关闭阴影探索/移除操作事件，随后没有本图 load.ready，最终初始化超时。源码 Pump 在 Operator=null 且 CanSimulateMap=false 时调用卸载；CanSimulateMap 在地图加载/待ready、暂停等情况下均false，原生 gameType 在异步入图期间仍可能为太空。旧卸载既不检查原生本次加载完成，也不检查正式本机查看意图，直接 DestroyImmediate 并将 nativeMapLoad 清空，构成提前销毁加载器的完整代码路径。日志未包含卸载调用栈，不声称已在实机单步复现。
- 排除：MapUpdatePrefix 在 isInitFinish=false 时返回，与原版 updateGame 自身的初始化完成守卫一致；MapLogicPrefix 停止受权威屏障保护的模拟，不应取消这条屏障来掩盖加载器销毁。没有延长超时、绕过UID/初始化校验或更改独占操作规则。
- 实现：两个卸载入口统一经过完成的本次原生地图身份守卫；正式本机查看意图存在时不卸载，正式模拟租约仍归本机时不卸载，操作槽仍有人时不卸载。Pump 不再把 CanSimulateMap=false 当成已离图；失去操作权时清理计划草稿也只针对本次已ready地图。普通离图后操作槽3分钟释放时仍能走原生销毁；敌对行动期间操作槽不空，不提前销毁。
- 边界：只修改上述 Game 源码和独立邮箱；未测试、编译、运行游戏、安装、修改版本或操作 Git index。Project3 与所有运行副本保持只读。V1.63 存档目录修复未触碰；主控统一三端 BuildOnly 后由用户运行验收。
- 需要回复：无；若集成要求变更该方法，先通知本 Agent。
