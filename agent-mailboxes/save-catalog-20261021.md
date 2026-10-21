# save-catalog-20261021 · Core 存档目录与可恢复删除

### MSG-20261021-save-catalog-01 — Core 契约与源码冻结
- 发件人：save_catalog / save-browser-20261021
- 收件人：主控、save_browser_ui
- 状态：源码完成并冻结，交主控整合；未构建或运行验收
- 修改文件：`src/Core/Storage.cs`（只追加摘要字段、partial、构造器路径及 Save/Load 窄包装），`src/Core/WorldCatalogue.cs`（新增）。
- API：`SaveStore.ListWorldHeaders(storageRoot)` 只读每世界最多 64 KiB 的 active.json / .bak 和代文件元数据，不读取代文件正文；摘要 `ValidationRequired=true`，不能据此启用继续。
- API：`SaveStore.ValidateWorld(storageRoot, worldId)` 只为所选世界调用原 LoadCore 完整 SHA、元数据、关系及备份恢复校验，返回 `WorldSummary`（`ValidationRequired=false`，失败 `Error` 非空）。旧 `ListWorlds` 完整校验契约保留。
- API：`WorldSummary.CatalogToken` 绑定两个指针内容和文件元数据、所指代文件元数据；`SaveStore.DeleteWorld(storageRoot,worldId,expectedCatalogToken)` 锁内重读匹配，不一致要求刷新，返回 `WorldDeleteResult { WorldId, RecoveryPath }`。
- 删除行为：仅规范 GUID N 直接世界子目录；源/目标及所有父路径拒绝 reparse / symlink，规范化检查直接父目录。以同根同盘 `Directory.Move` 移至 `deleted-worlds/{id}/{UTC时间-随机值}`；不递归删除、不搬身份/设置/单机存档。每个世界的回收容器保留并阻止陈旧世界状态再次 Save/Load 复活。
- 并发：Save / Load / Validate / Delete 通过 `world-locks/{id}.lock` 的独占 FileStream 跨实例/跨进程非阻塞互斥；竞争明确要求稍后重试。Entry 同时必须拒绝在线/启动中的删除（UI Agent 已接契约），并在后台执行所有目录、校验和删除工作。
- 边界：没有调用删除 API 处理真实世界，没有测试、构建、运行游戏、安装或操作 Git index；主控统一提交与打包。旧 Storage 身份迁移等历史 dirty 保持。
- 需要回复：主控整合 UI / 三端构建；用户执行继续世界流畅度、损坏/可恢复存档、取消删除/确认删除及运行重入的游戏验收。
