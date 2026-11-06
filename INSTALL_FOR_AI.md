# 给 AI 的安装指南

目标仓库：https://github.com/dy0000/pvz-hybrid-ai-bridge

先读 README.md。操作用户电脑时，复用已有安装，不启动第二个游戏控制器，不停止健康 Runner。安装与游玩是分开的步骤，用户只要求安装时不要接管或开战。

1. 下载仓库或 Release，解压到稳定目录；默认可用 `%LOCALAPPDATA%\PVZHybridAIBridge`。不要混入另一个项目目录。
2. 如果已有健康安装或游戏运行中，保留运行现场；源码升级等待控制交还、在途动作结束、隔离游戏和对应 MCP 退出后进行。
3. 在项目根目录运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -NonInteractive`。安装器自动查找唯一的已运行杂交重制版；多个候选时用 `-GamePath` 明确指定目录或 EXE。
4. 安装器需要 Windows x64、Python 3.12 x64；可通过 winget 自动安装。无第三方 Python 依赖，无需编译器或模型 API Key。未找到游戏时先完成 MCP 安装，待获得游戏路径再运行安装器；游戏不随仓库提供。
5. 游戏须是完整的 0.31.0 Windows C# 兼容构建。安装器复制到 `game-sandbox/` 并创建新的隔离存档，不导入原存档，不写原游戏目录；不跳过游戏 DLL 哈希校验。
6. 有 Codex CLI 时自动注册 `pvz_hybrid`；其他客户端用 `-Client Generic`，合并 `generated/mcp.json` 中的单个服务，保留已有 MCP 配置。重新加载客户端。
7. 运行 `doctor.cmd` 或 `scripts/doctor.ps1`。MCP 握手与包哈希通过只说明本地安装检查通过，不代表已经连上游戏或实玩。
8. 告知用户安装位置、是否已关联游戏，以及下一步：`start.cmd` → 游戏 Mod 管理中启用 AI 玩家接口 → 按原生提示重新加载 → 首次手动创建玩家档案。需要用户点击这些游戏界面时如实说明。
9. 用户明确要游玩时，再读 `skills/pvz-mod-player/SKILL.md` 和 API.md，并按 `generated/START_HERE.txt` 建立唯一会话。购物与竞猜按用户明确范围和预算执行。

不要公开连接 token、用户存档、整份运行目录或客户端配置。不要为了安装验证去接管现有开发副本。
