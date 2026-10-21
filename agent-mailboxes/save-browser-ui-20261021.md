# save-browser-ui-20261021 · Entry 交付

### MSG-20261021-save-browser-ui-01 — 继续世界异步目录与删除 UI 已冻结
- 发件人：/root/save_browser_ui
- 收件人：MAIN / save-browser-20261021
- 时间：2026-10-21 +08:00
- 状态：已完成（源码交付，用户游戏验收未执行）
- 关联任务/文件：src/Entry/MultiplayerLobbyPages.cs；MultiplayerRoomDialog.cs；MultiplayerVisualTheme.cs 的继续页提示和提交门禁；ModPluginLauncher.cs 的 QueueStart/create 两处调用；新增 MultiplayerSaveBrowser.cs、MultiplayerWorldStorageMutation.cs。
- 消息：继续页立即显现，下一 UI update 才启动后台 ListWorldHeaders；不全档枚举反序列化，目录页只对当前选中存档后台 ValidateWorld，校验完成之前显示待校验/正在校验且不能继续，HostLoad 仍走原完整加载。所有 Unity/Runtime/列表操作在主线程 Pump；后台只持有字符串、summary、取消 token 和 lease。目录 epoch、选择 revision、WorldId/CatalogToken 隔离刷新、快速切选、关闭、切页及会话变化的过期结果。每窗口只一个 worker，并以静态 SemaphoreSlim 限制关闭重开窗口仍仅一个目录/验证/删除任务实际读写；取消只丢弃结果，不破坏正在读取的存档。不持有 WorldState 缓存。
- 删除：详情右下新增“删除所选世界”，与继续按钮分行，原生 FairyUIDispatcher.showTip 二次确认展示名称、保存时间、完整稳定 WorldId；损坏档可删。确认与执行前复核原目录、选择、token、离线状态；Core 内锁再次核对 token。后台 DeleteWorld 只回收，当前任务没有删除任何用户世界。删除时禁用创建/加入，Runtime lease 同时拦截外部邀请等 QueueStart 以及延后 create，lease 在 worker finally 线程安全释放；提交 Task.Run 失败也释放。Begin 门禁覆盖会话、启动、关闭、待启动/返菜单/退出态；关闭窗口不遗留门禁。
- UI 生命周期：重建旧行前清除 captions、disabledCaptions、rowHighlights、selectionLines 与旧 selection 引用；目录变化才重建一次，周期刷新只更新状态。原生 ProceduralImage/Modifier 及现有ScrollRect模板保留。
- 依赖：Core 的 WorldSummary.ValidationRequired/CatalogToken；SaveStore.ListWorldHeaders、ValidateWorld、DeleteWorld(root,id,expectedCatalogToken)、WorldDeleteResult.RecoveryPath，已与 save_catalog Agent 一致。
- 验证边界：未构建、未执行测试、未启动游戏/Play Mode、未安装、未操作 Git index；符合主控分工及用户验证边界。继续页面响应、快速切选/刷新/关闭重开、坏档备份恢复、删除确认及双会话入口互斥由用户游戏验收。
- 需要回复：主控统一整合和三端 BuildOnly。
- 回复关联：无
- 处理记录：本 Agent 源码已冻结，后续仅应主控明确编译修正请求再改。
