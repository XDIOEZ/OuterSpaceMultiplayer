# 依赖与来源

- 原版游戏 `Assembly-CSharp`、Unity、TextMeshPro、Steamworks.NET：分别引用目标环境真实程序集，不随 MOD 分发，不修改本体文件。版本及指纹记录在包内 `BUILD-INFO.json`。
- 舰战副本控制门禁引用目标环境的 `AstarPathfindingProject`、`NodeCanvas` 及其基类所在的 `PackageTools`、`ParadoxNotion`、`Drawing`，用于关闭副本的原生避障和行为树；仅引用现有程序集并记录指纹，不随 MOD 分发或替换。
- Project3 JSON：引用 Editor 环境已有 `Newtonsoft.Json.dll`，不随 Project3 包分发。
- Steam JSON：随包提供不同程序集名的 `FreeWorldMultiplayer.Newtonsoft.Json.dll`，不替换游戏自带 JSON。基于官方 Json.NET 13.0.2 源码，固定归档为主仓库 `third_party/Newtonsoft.Json/source-13.0.2.zip`，SHA-256 为 `3FE7BBF46E2745E7742ECFA6A18E276999512597B017FB4AC5ED86655FF2FCBA`。MIT 许可在同目录 `LICENSE.md`，并随 Steam 包提供 `Newtonsoft.Json.LICENSE.md`。使用上游 net45 开关，关闭本 MOD 未使用且被目标裁剪的可选集成，仅给 DEBUG 断言加预处理保护。开关及直接引用检查记录在 `PRIVATE-JSON-BUILD.json`。来源 <https://github.com/JamesNK/Newtonsoft.Json/tree/13.0.2>。
- Harmony `0Harmony`：原生 MOD 依赖 `com.harmony`；构建从本游戏实际安装的工坊/本地 MOD 提供者解析，仅引用，不捆绑共享 DLL。Harmony 为 MIT 许可，来源 <https://github.com/pardeike/Harmony>。
- Unity .NET 4.8 API：仅编译时参考，不随包分发；完整参考 API 不代表发行版运行库保留了相同 API。

第三方源码归档、工具和适配均由主仓库管理；构建缓存位于 `artifacts`，不能在缓存中继续开发。编译和元数据检查不代表游戏加载、反射路径或联机运行验收通过。
