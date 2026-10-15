# 依赖与来源

- 原版游戏 `Assembly-CSharp`、Unity、TextMeshPro、Steamworks.NET、Newtonsoft.Json：仅以实际安装文件或 Project3 已生成程序集为编译引用；不随 MOD 分发，不复制或修改本体文件。实际版本与 SHA-256 见生成包内 `BUILD-INFO.json`。
- Harmony `0Harmony`：原生 MOD 依赖 `com.harmony`；构建从本游戏实际安装的工坊/本地 MOD 提供者解析，仅引用，不捆绑共享 DLL。Harmony 为 MIT 许可，来源 <https://github.com/pardeike/Harmony>。
- Unity .NET 4.8 API：仅编译时参考，不随包分发；完整参考 API 不代表发行版运行库保留了相同 API。

本包包含自身 `FreeWorldMultiplayer.dll` 与调试符号。Steam 元数据检查只覆盖已知 Harmony 直接引用，不能证明 MOD 加载或联机运行通过。
