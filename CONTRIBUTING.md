# 贡献指南

修改前读 AGENTS.md、README.md 和相关 API 契约。正常游戏规则、玩家停止锁、单控制器和未知结果不重放均是接口边界。

- 改哪里，验证哪里。安装/发布修改运行 `test_distribution.py`，默认连接或 MCP 契约修改运行 `test_client.py`；观察和玩法另有对应 Python 测试。
- 修改 C# 时使用 .NET 9 和自行获取的目标游戏构建。`scripts/build.ps1 -GameDirectory <目录>` 只编译插件并更新 packages；相关原生 API 兼容风险可加 `-Verify` 检查裁剪运行库。
- 需要实机验收时，先核对唯一控制者、用户授权和隔离目标。`verify_*_live.py` 会实际操作游戏，不能当成普通离线测试运行。购买、竞猜另需明确预算；不知道结果时先查收据。
- 不热替换运行中的 DLL，不为离线改动停掉健康 Runner；升级在控制交还、无在途动作并正常退出游戏后进行。
- 提交 API 或行为变更时，同步修改技能和文档，说明测试覆盖及未实测边界。不要重复维护只验证实现字面的测试。
- 不提交游戏 DLL、PCK、EXE、反编译游戏代码、存档、运行记录、生成配置、用户路径或凭据。发布只能通过 `scripts/package_release.py` 的公开文件列表。

修改桥接版本时同步 `mod/mod.json`、`Entry.cs`、MCP `serverInfo`、packages 构建元数据及 CHANGELOG。保留原 Mod ID `local.pvz-ai-bridge`，避免破坏已有启用配置。
