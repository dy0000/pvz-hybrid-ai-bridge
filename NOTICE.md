# 第三方与分发范围

本仓库包含 AI Bridge 的 C#、Python、安装脚本、文档和游玩技能。`packages/*.pmod` 只包含本项目的 `mod.json` 和 `Runtime/ModAssembly.dll`，不打包游戏程序集。

游戏《植物大战僵尸杂交重制版》及其素材归各自权利人所有，请从作者提供的渠道另行获取。源码通过游戏提供的 Mod API、GodotSharp 和游戏类型进行编译引用；`PlantsVsZombies.dll`、`GodotSharp.dll`、EXE、PCK 和游戏运行库不在仓库或 Release 中。

桥接离线兼容性测试开发依赖 Mono.Cecil 0.11.6（MIT），由 NuGet 获取，不随玩家包分发。Python 运行端只使用标准库。安装入口与发布布局沿用本作者的 MIT 项目 [PVZ Scared AI Bridge](https://github.com/dy0000/pvz-scared-ai-bridge)，其版权声明保留在 LICENSE 中。

文档中的历史实机记录来自开发环境；证据目录、研究材料和反编译游戏源码不随本项目分发。它们不构成游戏或第三方素材的再分发授权。
