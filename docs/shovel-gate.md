# 铲子可用性与原生门禁

`EnabledShovel` 原先硬要求 `shovelShow=true`，与当前原生 `ShovelManager.ShovelButtonPressed` 存在差异：原生只在 `!shovelShow && !isGameRunning` 时因显示/阶段条件拒绝。运行中 `shovelShow=false` 并不代表铲子被禁用。现改为原生的 `shovelShow || isGameRunning`，保留有效且在树内未等待删除的 ShovelManager、有效 shovelConfig 和原生 `CanUseShovel()` 检查；后者继续决定解锁及 IZM/IZM2 限制。

这修复了确定存在的代码差异。原现场只报告 `shovel_enabled=false`，没有拆分证据，不能据此断言这是现场唯一原因。新版本由唯一 Runner 的新鲜观察确认其他条件。

## 观察契约

`capabilities.features.shovel_gate_diagnostics=true` 表示支持此诊断。`observe.state.shovel_enabled` 保留布尔字段；`observe.state.shovel_status` 是同一次检查的结构化证据，完整 JSON、compact/delta 和 Python 终端摘要均保留。

以下是合成协议样例，不是已部署实测：

```json
{
  "shovel_enabled": true,
  "shovel_status": {
    "enabled": true,
    "reason": "ready",
    "manager_ready": true,
    "config_ready": true,
    "shovel_show": false,
    "battle_running": true,
    "native_can_use": true
  }
}
```

`reason` 按首个阻塞返回：

| 值 | 含义 |
| --- | --- |
| `manager_unavailable` | 原生管理器缺失、不在树内或等待删除 |
| `config_unavailable` | 原生铲子配置缺失 |
| `native_permission_unknown` | 原生权限未能确定，不能按允许处理 |
| `native_permission_denied` | 原生 `CanUseShovel` 拒绝，可能是解锁或反向玩法限制 |
| `hidden_outside_battle` | 非运行阶段且原生未显示铲子 |
| `ready` | 工具门禁通过 |

管理器不可读时 `shovel_show/native_can_use=null`，不伪造 false；`manager_ready/config_ready` 表示就绪判定，不声称对象永久不存在。无战斗时沿用原观察流程，不返回工具字段，delta 通过 removed_fields 清除旧字段。

## 动作与目标

工具可用性不代表当前请求已授权。控制权/玩家锁、单人模式、battle_id、真实战斗或特定教学步骤、暂停/模态、活跃种植计划冲突等门禁保持。动作仍用原生 `GetShovelCharacter(layer_fraction, shovelConfig)` 绑定一格的一层，再仅调用一次原生 `cell.Shovel`；保留目标类型、教学角色和效果确认。没有开启铲子光标，也没有新增持有输入。

`outcome=shoveled` 确认目标离场；`shovel_effect_confirmed` 只确认伤害或阶段变化，不等于移除。未知结果先观察现场和回执，不自动重放。处理屋顶图案任务时，只对新鲜目标中的冲突上层操作，保留底座、防线与已满足的杨桃；逐项确认离场后再种植，不能根据旧目标列表批量铲除。用当前原生 `formation_goal` 或胜利判断最终完成。

## 安全部署交接

本功能从 0.5.2 起提供，当前插件在 `packages/`，项目安装器 `scripts/install.ps1`，对应哈希在 `packages/build-info.json`。安装器 `-CheckOnly` 只读验证目标/包哈希和进程，运行中拒绝安装。

由唯一 Runner 协调交还控制，确认无在途/未知动作且选择释放后正常退出副本，备份最新 `%APPDATA%/PVZ-Hybrid-AI-<安装标识>` 存档，再执行 `./scripts/install.ps1 -UpgradeWhenStopped`。不停止健康 Runner、不另开游戏 TCP、不热替换。安装器备份的是旧 Mod 包，不代替存档备份。

下一次安全启动后由原 Runner 核对 `bridge_version=0.5.2`、能力标志、`shovel_status`。若仍不可用，按真实 reason 处理；不得强开解锁或模式权限。运行中 `shovel_show=false/native_can_use=true/config_ready=true` 时应返回 `shovel_enabled=true`，再对一个经授权的精确上层验证。离线矩阵和 DLL 绑定通过不等于本关已实机铲除、部署或通关。
