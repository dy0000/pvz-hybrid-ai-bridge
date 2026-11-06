# 安装参数与维护

根目录 `install.ps1` 支持：

| 参数 | 用途 |
| --- | --- |
| `-GamePath` | 游戏目录或 EXE；省略时优先复用本安装副本，再查找唯一运行的 C# 游戏 |
| `-PythonPath` | Python 3.12 x64 的绝对路径；省略时查找或通过 winget 安装 |
| `-Client Auto` | 有 Codex CLI 时注册，否则只生成配置 |
| `-Client Codex` | 要求注册独立 pvz_hybrid 服务 |
| `-Client Generic` | 只生成配置，由用户合并到其他 MCP 客户端 |
| `-NonInteractive` | 自动安装入口，不弹交互选择 |

`bootstrap.ps1` 另支持 `-InstallDir`，默认 `%LOCALAPPDATA%\PVZHybridAIBridge`，从仓库 main 下载完整项目后调用根安装器。它不自动覆盖已存在的源码安装；更新按 README 的停止与备份流程执行。

`scripts/setup_sandbox.ps1 -SourceDirectory <目录>` 校验完整 C# 游戏及支持的 DLL 哈希，把游戏复制到本项目 `game-sandbox/`。存档名由安装根目录的 SHA256 前 12 位生成，为 `%APPDATA%\PVZ-Hybrid-AI-<安装标识>`。复制后核对 EXE/PCK/游戏 DLL 哈希，记录到 `sandbox.local.json`。新副本默认关闭本地种植计划与游戏内调试面板，MCP 仍支持实时单次动作。

`scripts/install.ps1` 是底层插件安装器，与根安装器不同。它读取 `packages/build-info.json`，校验目标隔离存档、游戏 DLL 与插件包，并拒绝替换运行中的目标。`-UpgradeWhenStopped` 只备份本项目旧包，不改变玩家的 Mod 启用列表；初次需要在原生 Mod 管理中启用。`-CheckOnly` 仅检查兼容性和当前是否可安装。

`start.cmd` 只启动本安装记录对应的副本，核对隔离存档覆盖与游戏 DLL；已运行则返回已有 PID，端口被其他桥接占用时拒绝再启动。它不接管、不自动选卡或开始战斗。

`generated/` 和本机 JSON 均不提交。`doctor.cmd` 从本机记录找到 Python，校验包与已关联游戏的构建并完成 MCP 握手，不读取连接 token、不连接游戏。

源码路径改变时，先按正常结束流程交还并停止，再在新目录重新安装。旧存档不会自动迁移；不要复制旧 `sandbox.local.json` 来绕过安装记录检查。
