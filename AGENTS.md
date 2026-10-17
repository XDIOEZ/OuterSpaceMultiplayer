# FreeWorldMultiplayer

- 开始或继续任务时先读并更新 [AGENT-COORDINATION.md](AGENT-COORDINATION.md)，登记分工、准备修改的文件、接口依赖和阻塞；重叠文件先协调，完成后更新结果并释放占用。统一构建前查看其中的未完成依赖，只维护自己的记录，不覆盖其他 Agent 的内容。
- Agent 之间的非实时消息统一投递到 [agent-mailboxes/README.md](agent-mailboxes/README.md) 所列邮箱：开始和结束任务时主动读取 `ALL.md` 与自己的邮箱；任意 Agent 可向其他邮箱留言或维护自己邮箱的状态。邮箱不是即时通知或写锁，跨模块分工和冻结以 `AGENT-COORDINATION.md` 为准，不得因发信就认定对方已读或同意。
- 开发前必须先读 [Agent.md](Agent.md)，以其中的唯一主仓库、P3/Steam 运行副本关系和统一同步入口为准。
- 现行需求仅以 `联机方案 V1.md` 为准，完整 V1 同次交付，旧构想仅供历史参考。
- 用户于 2026-10-15 已授权开始实现；不再等待重复的开工确认。
- 仅修改本 MOD；`Project3` 是源码参考，Steam 的游戏程序集、运行库和工坊共享依赖不得替换。修改本体配置及重打包须单独取得用户授权。
- 每位玩家的母舰、舰队、资金、科技和任务独立；联机存档与单机隔离。不能以本地多份各自模拟的世界冒充共享世界。
- 只实现接入真实调用链的逻辑；不得用未调用的接口、空实现、固定成功回执或仅同步快照替代 V1 实时同步与可靠结算。
- 用户已于 2026-10-15 明确要求继续 MOD 实现与构建，Project3 严格只读。当前 Project3 的 Assets/link.xml 已声明完整保留 mscorlib 等程序集；旧 Steam DLL 的兼容性问题单独记录，不阻止源代码开发，也不得据此要求先修改 Project3。
- 用户要求主代理只做主控、接口协调、代码整合及构建；实际功能由子智能体实现。网络、会话/持久化、游戏适配按目录分工，跨模块契约变动先通知相关代理。
- 用户已明确主仓库为 `C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer`，P3 和 Steam 的 `mods/FreeWorldMultiplayer` 均为运行副本。兼容适配必须合入主仓库，通过 `Sync-ModCopies.ps1` 从同一源码分别构建并同步；历史迁移目录不再是开发或构建入口。只有这两个本 MOD 安装目录获准写入；游戏本体源码、资源、工程设置和其他 MOD 始终只读。
- 用户随后明确授权 `C:\TapTap\PC Games\242783` 的 Harmony 与联机 MOD 安装，并要求 TapTap/Steam 日志分开。TapTap 已纳入统一脚本的独立构建与同步目标；后续只更新其 `mods/FreeWorldMultiplayer`，不替换本体或既有共享 Harmony。此追加授权优先于上条的两目录旧范围。
- 引用以实际文件为准；若使用 Project3 现有程序集构建开发参考包，须明确标记，与面向当前 Steam DLL 的兼容构建区分。不能把开发参考包当成 Steam 运行已通过。
- 遵守用户验证边界，不启动游戏或代跑 Play Mode、跨机联机验收。编译、打包、安装与运行结果分别说明。
- 用户已持续授权每次修改创建本地 Git 提交。必须遵循 [Agent Git 提交规则](GIT-COMMIT-POLICY.md)；这取代此前默认不执行 commit 的要求。未经另行授权仍不得 push 或发布工坊，必须保留无关的工作区改动。

