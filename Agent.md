# 开发前必读：主仓库与运行副本

## 唯一开发源

`C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer` 是本 MOD 唯一开发主目录，由本目录的 Git 管理。

功能源码、兼容适配、构建与同步脚本、分发配置和开发文档都在这里维护。必须保留未提交改动；用户没有明确要求时，不执行 commit、push、reset 或覆盖本地改动。

开始开发前依次阅读本目录的 `AGENTS.md`、本文件和当前需求 `docs/plans/联机方案 V1.md`。不要到历史迁移目录或运行副本中继续开发。

## 主仓库与运行副本

| 目录 | 职责 | 允许的修改 |
| --- | --- | --- |
| `C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer` | 唯一源码与 Git 仓库 | 在用户授权范围内开发、整合、构建 |
| `C:\Users\CatStudio\Desktop\Project3` | 游戏源码参考与 Unity Editor 运行环境 | 本体源码、资源、设置和共享依赖只读；本 MOD 仅同步到 `mods\FreeWorldMultiplayer` |
| `C:\Program Files (x86)\Steam\steamapps\common\Outer Space` | Steam 游戏实际运行环境 | 本体 DLL、运行库及其他 MOD 只读；本 MOD 仅同步到 `mods\FreeWorldMultiplayer` |
| `C:\TapTap\PC Games\242783` | TapTap 游戏实际运行环境 | 本体只读；用户已授权安装 Harmony 和本 MOD，后续仅同步 `mods\FreeWorldMultiplayer`，不替换共享 Harmony |

P3、Steam 与 TapTap 的 `mods\FreeWorldMultiplayer` 都是可重新生成的安装副本，不是新的开发仓库。`artifacts\migrations` 下的旧工作目录只留作历史依据，不再作为构建入口。

若发现副本或历史目录有独有修复，先核对差异、合入主仓库，再从主仓库构建；不能直接用副本覆盖主仓库。

## 构建和频繁同步

同一份源码分别针对 Project3、Steam 和 TapTap 的真实程序集构建。三种目标的依赖和 DLL 可以不同；应以 `BUILD-INFO.json` 中相同的 `SourceRevision` 判断是否来自同一开发状态，不能要求 DLL 字节相同。

统一入口在主仓库：

```powershell
# 修改完成后，构建并更新三个运行副本
& 'C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer\Sync-ModCopies.ps1'

# 仅更新一个目标
& 'C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer\Sync-ModCopies.ps1' -Target Project3
& 'C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer\Sync-ModCopies.ps1' -Target Steam
& 'C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer\Sync-ModCopies.ps1' -Target TapTap
```

这里的“拉取”指从主仓库最新工作区构建、安装，包括用户尚未提交的源码改动；不需要在运行副本中执行 Git pull。不要擅自增加定时任务、后台监视或自动重启。

用户已授权维护三个安装副本：后续完成源码、依赖或分发配置修改后，默认通过上述入口同步三端，除非用户要求只调查、只修改文档、只打包或暂不安装。若目标运行中、构建失败或依赖不匹配，停止该次同步并说明原因；不强杀进程或复制不兼容包。

## BUG 修复版本

从本轮 `V1.1` 起，每完成一个独立 BUG 修复，在主仓库 `VERSION` 中递增一次小版本；两个部分都是整数，顺序为 `V1.1`、`V1.2`、……、`V1.9`、`V1.10`。不能按小数处理或把 `1.10` 化为 `1.1`。

同一项修复的重复构建、重试安装及 P3/Steam/TapTap 三端同步共用同一版本，不重复加号。版本来源、修复说明、面板显示、程序集和构建/安装回执须一致；在 `CHANGELOG.md` 记录本轮修复。业务修复版本不替代游戏匹配目录 `v_common`、网络协议版本或存档格式版本。

## 必须保留的边界

- 只覆盖本 MOD 的已知分发文件，并保留安装备份、来源版本和回执；不删除玩家存档、身份数据、自定义文件、其他 MOD 或启用配置。
- Steam 适配也在主仓库维护，不另开长期维护的迁移源码副本。独立 JSON 库随 Steam 包分发，不替换游戏自带库；Harmony 使用目标环境已有提供者。
- 编译、文件同步和运行验收分开说明。遵守用户当前验证要求；未实际执行游戏内验收，不宣称联机或 Play Mode 通过。
- 更新 DLL 后由用户按原版要求重新加载或重启。不要把磁盘副本更新等同于运行中的旧程序集已更新。
