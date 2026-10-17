# BUG-15 / P1 · 备份恢复后首次保存破坏最后的完整代

- **来源：** [第四轮静态 BUG 审计 BUG-15](../../docs/联机MOD第四轮静态BUG审计与Agent修复交接-20261017.md)。
- **任务目标：** 旧 `active.json` 或其最新代损坏、但 `active.json.bak` 仍完整时，经过 `Load` 回退并首次 `Save`，旧有效指针与对应完整代应继续保留；新代后来再次损坏仍可恢复。
- **限制：** 不能只检查 JSON 解析；必须验证哈希、真实世界与代次；不得读取或破坏真实玩家存档，不替换旧未提交 Core 逻辑。
- **验收标准：** 合成目录分别覆盖损坏索引、损坏新代、缺失主索引、恢复源变化和写入失败，确认原子提交、备份、生成代和最终 `Load` 的实际结果；三端真实引用 BuildOnly，单独 Git 修复提交与版本记录。游戏内验收另建后续待办。
- **状态：已完成代码与合成存档故障注入（2026-10-17），本卡已从待办迁入已完成。** 游戏内真实退出/续开仍有独立待办。

## 代码交付

- **修复提交：** `60014f6`，MOD **V1.25**，协议 6 / 存档结构 1 未变。
- **修改：** `SaveStore.Save` 发现该 `SaveStore` 的上一 `Load` 从 `active.json.bak` 完整验证并恢复同世界同代时，先重新验证备份指针与存档代 SHA256。若验证通过，采用已有 `AtomicFile.Write` 将旧有效备份原子地恢复为主指针，再执行原有新代提交与自动备份，避免旧的坏指针被 File.Replace 写进唯一有效备份。若备份或旧代已再次损坏，写新代之前拒绝，不误报成功或删除现场。
- **历史内容保护：** `src/Core/Storage.cs` 仍有旧 Agent 的未提交 IdentityStore 移除差异，Git 只精确暂存本轮 `Save` 接线及新 helper 两个 hunk；其他未交接改动没有混进修复提交。

## 可复核验收

- 新增 `tools/IdentityStorageChecks/RecoveryPointerChecks.cs`，**直接链接生产 `Storage.cs`**，仅生成 `artifacts/identity-storage-checks/` 下的合成世界，不访问任何玩家真实文件。
- 原有 59 项存储/身份检查加 **19 项**恢复链故障注入，运行 `dotnet run --project tools/IdentityStorageChecks/IdentityStorageChecks.csproj -c Release`：**78 项通过**。
- 场景：主指针损坏、最新代 SHA 错误、主指针丢失，三者恢复后首次 Save 均保存旧完整代和可解析的回退指针；随后人为再次损坏新代，`Load` 能回退旧代。恢复后备份再次损坏、旧代 SHA 变化或原子指针恢复写入失败都拒绝新代提交，旧文件保持可追溯。
- `Sync-ModCopies.ps1 -Target All -BuildOnly` 三端独立编译成功：Project3 / Steam / TapTap，版本 `1.25.0.0`，同源指纹 `642BAFB690936DD3D32BC9AD0381A7995A63C8E4DBA73425A7A73DEA7A839E46`。
- **未执行：** 安装游戏 MOD、Unity/游戏实际加载、双端退出重连与真实玩家存档实验。不能以模拟目录测试冒充游戏已验收；后续见 [BUG-15 实机验收](../待办/BUG-15-真实存档恢复回归.md)。
