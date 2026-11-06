# 植物大战僵尸杂交重制版 AI 控制桥

让 AI 通过原生游戏状态和 MCP 工具，实时游玩 **植物大战僵尸杂交重制版 0.31.0（Windows C# 版）**。

AI 负责观察、选卡和战术，本项目负责把动作交给游戏。桥接采用游戏的 `.pmod` 插件机制，不使用 OCR 或模拟鼠标；种植、拾取和特殊操作保留原生费用、冷却与放置规则。默认实时运行，AI 思考时游戏继续进行。

这是杂交版的独立项目；恐怖版请使用 [PVZ Scared AI Bridge](https://github.com/dy0000/pvz-scared-ai-bridge)。两者的游戏、引擎与接口不同。

## 能做什么

| 能力 | 内容 |
| --- | --- |
| 观察战场 | 阳光、卡槽与费用/冷却、植物/僵尸血量、地形、障碍、波次和特殊玩法实体 |
| 实时操作 | 选卡、开战、种植、拾取、铲除、加农炮和部分植物主动能力 |
| 原生流程 | 菜单导航、开放关卡、每日挑战历史日期、教程、领奖、结算与有限续关 |
| 特殊玩法 | 传送带、掉落卡、砸罐、三消、手套、技能等，以当前 capabilities 为准 |
| AI 接入 | 本机 stdio MCP、持久 Python 客户端、增量观察与事实事件 |
| 玩家接管 | F8 停止并暂停；F9 交还手动控制；玩家按 F10 重新允许 AI |

当前版本 **0.5.16**。AI 的水平取决于模型和策略；接口接通、示例脚本可运行不等于能够通关。联网账户、联机、编辑器和玩家档案创建不在接入范围。

## 快速开始

需要 **Windows x64** 和支持本机 stdio MCP 的 AI 客户端。请自行准备完整解压的**杂交重制版 0.31.0 C# 版**游戏；本项目不分发游戏本体。

### 1. 安装

先打开游戏，让安装器识别路径，再在 PowerShell 粘贴：

```powershell
irm https://raw.githubusercontent.com/dy0000/pvz-hybrid-ai-bridge/main/bootstrap.ps1 | iex
```

也可从 [Releases](https://github.com/dy0000/pvz-hybrid-ai-bridge/releases) 下载安装包，解压后双击 **`install.cmd`**。安装器自动准备 Python 和 MCP 配置，用户端不需要 .NET SDK、Node.js、Frida 或模型 API Key。

没有打开游戏时，也能先完成 MCP 安装。之后在安装目录执行，代入游戏文件夹或 EXE 路径：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -GamePath "D:\Games\PVZ-Hybrid"
```

安装器复制游戏到项目内的 `game-sandbox/`，并创建**独立新存档**；原游戏目录及原存档不变。请预留一份完整游戏副本的磁盘空间。已有本安装的副本会保留，不合并其他副本或导入旧进度。

### 2. 启动并启用插件

双击 **`start.cmd`** 打开隔离副本。在游戏的 **Mod 管理**中启用 **AI 玩家接口**，按原生提示重新加载。首次进入游戏需要手动创建本副本的玩家档案。

默认安装目录是 `%LOCALAPPDATA%\PVZHybridAIBridge`。每个安装位置使用自己的 `%APPDATA%\PVZ-Hybrid-AI-<安装标识>` 存档目录，实际路径见本机 `sandbox.local.json`。

### 3. 让 AI 开始玩

重新加载 AI 客户端的 MCP，把 **`generated/START_HERE.txt`** 发给 AI，并说明想玩的关卡、关数或时间范围。服务名是 **`pvz_hybrid`**，工具前缀是 **`pvz_`**。

同一游戏只用一个控制器。结束时让 AI 领奖返回并交还控制；任何时候都可以在游戏窗口获得焦点时按 **F8/F9** 停止。

## 交给大肥鱼安装

把这句话发给能操作你电脑的 AI：

> 帮我安装这个项目：https://github.com/dy0000/pvz-hybrid-ai-bridge ，按 INSTALL_FOR_AI.md 自动安装并接入 MCP。

安装指引见 [INSTALL_FOR_AI.md](INSTALL_FOR_AI.md)。AI 需要本机文件、终端和 MCP 能力。

## 接入其他 AI 客户端

安装器检测到 Codex CLI 时，会注册独立的 `pvz_hybrid` 服务；使用 `-Client Generic` 可只生成配置。导入 **`generated/mcp.json`**，或者添加 stdio 服务：

- 命令：安装器生成的 Python 绝对路径。
- 参数：`-X`、`utf8`、项目 `scripts/run_mcp.py` 的绝对路径，分别作为数组项。
- 服务名：`pvz_hybrid`。

Codex 的配置片段在 `generated/codex.toml`。请合并单个服务条目，保留其他配置。`mcp.example.json` 仅展示结构，不能直接使用其中占位路径。

技能随项目提供，默认让 AI 读取 `skills/pvz-mod-player/SKILL.md`，不会覆盖已有全局技能。Python 工具只使用标准库，不向系统 Python 安装第三方依赖。

## 停止、更新和卸载

**停止 AI：**游戏内按 F8/F9，或让 AI 调用 `pvz_release_control`。F8/F9 后只有玩家按 F10 才重新允许 AI 接管。正常释放须以回包中的控制、自动拾取和原生选择状态为准，详见 [API](API.md)。

**更新：**先让唯一控制器交还，确认没有结果未知的在途动作，然后正常关闭隔离游戏并退出使用本项目的 MCP 服务。备份 `sandbox.local.json` 中的存档目录，再把新源码覆盖到**原安装目录**并运行 `install.cmd`。保留 `sandbox.local.json`、`game-sandbox/` 和存档；购买收据也在存档内。安装器拒绝替换运行中游戏的插件，旧插件会备份到 `artifacts/`。不要在游戏或控制器运行中覆盖代码。

**卸载：**交还并关闭隔离游戏，移除 AI 客户端的 `pvz_hybrid` MCP 条目，再删除项目目录。Codex CLI 可用 `codex mcp remove pvz_hybrid`。存档单独保留，是否删除由你决定。移动安装目录会改变隔离标识，请在新位置重新安装，不直接搬用旧安装记录。

## 常见问题

| 现象 | 处理 |
| --- | --- |
| 没找到游戏 / 多个候选路径 | 用 `install.ps1 -GamePath` 指定完整解压的游戏目录或 EXE |
| 提示游戏构建不兼容 | 当前仅适配 0.31.0 Windows C# 构建；游戏 DLL 校验不能跳过 |
| 找不到 Python / winget | 手动安装 Python 3.12 x64，再重试；也可传 `-PythonPath` |
| AI 看不到工具 | 导入 generated 配置并重新加载客户端；工具名为 `pvz_*` |
| `connection.json` 不存在 | 启动隔离副本，启用 AI 玩家接口并按提示重新加载 |
| 47632 端口被占用 | 另一个桥接副本仍在运行，正常交还并关闭它后再启动 |
| 玩家锁阻止接管 | 玩家在游戏中按 F10；AI 不能自行解除停止锁 |
| 动作被拒绝 | 依据当前费用、冷却、地形、教程和实体状态重新决策 |
| 超时后不知是否成功 | 观察原回执和现场；结果未知的动作不自动重放，购买先查收据 |
| 能看状态但打不好 | 检查模型和战术；先从一关、有限时长开始 |

双击 **`doctor.cmd`** 可检查桥接包哈希、游戏构建和 MCP 握手；自检不连接游戏、不接管、不输出 token。反馈时请提供错误消息和必要日志片段，不公开存档、`connection.json` 或整份运行目录。

## 开发

```text
pvz-hybrid-ai-bridge/
├─ install.cmd / install.ps1      # 玩家安装
├─ start.cmd / doctor.cmd         # 启动 / 离线自检
├─ src/PvzAiBridge/               # C# 原生 .pmod 桥接
├─ python/pvz_ai/                 # Python 客户端与 MCP
├─ mod/                          # 插件清单
├─ packages/                     # 本项目编译插件及目标构建哈希
├─ scripts/                      # 构建、安装、发布与维护入口
├─ skills/pvz-mod-player/         # 游玩流程及战术
└─ tests/                        # 隔离测试
```

只有开发者重新构建 Mod 才需要 **.NET 9 SDK** 和自行获取的兼容游戏 DLL：

```powershell
.\scripts\build.ps1 -GameDirectory "D:\Games\PVZ-Hybrid"
```

构建更新 `packages/`。`-Verify` 另运行桥接离线与实际裁剪运行库兼容性检查，仅在相关边界需要时使用。针对安装、打包或默认连接路径的修改：

```powershell
python -X utf8 -m unittest discover -s tests -p test_distribution.py -v
python -X utf8 -m unittest discover -s tests -p test_client.py -v
python -X utf8 scripts/package_release.py
```

发布包在 `dist/`，附逐文件 SHA256 清单和 ZIP 校验文件。游戏、本机配置、存档、日志和 token 不进入 Git 或安装包。

- [AI 安装指南](INSTALL_FOR_AI.md) / [安装参数](docs/installation.md)
- [接口与回执](API.md) / [游玩技能](skills/pvz-mod-player/SKILL.md)
- [架构](docs/architecture.md) / [兼容性与验证范围](docs/compatibility.md)
- [贡献指南](CONTRIBUTING.md) / [更新记录](CHANGELOG.md)

## 许可与致谢

本项目桥接代码及文档采用 [MIT License](LICENSE)。游戏本体、美术、音乐、剧情、Godot 和游戏运行库各自遵循其权利人的许可，不属于本项目 MIT 授权范围，详见 [NOTICE](NOTICE.md)。

感谢植物大战僵尸杂交重制版作者及游戏原生 Mod 接口维护者。
