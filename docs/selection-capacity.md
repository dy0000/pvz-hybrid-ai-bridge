# 选卡容量契约

`observe.state.selection_capacity: integer | null`，能力标志 `capabilities.features.selection_capacity=true`。

数字为当前关卡原生选卡栏的**总容量**，包含已选和锁定卡占用的槽位；不要再加 `chosen_cards.length` 或锁定卡数量。它不代表可选候选总数、剩余槽数或必须满槽才可开战。

来源为 `TowerDefenseManager.Instance.seedbankPacketMax`。原生 `TowerDefenseInGameSeedBank.InitializeReferences` 调用 `GetPacketSlotNum(slotConfig)`，根据实际关卡配置初始化此值后设置 `_referencesInitialized=true`；原生 `CanAddPacket()` 直接比较 `packetNum < seedbankPacketMax`。Mod 只读已初始化值，不自行计算商店扩槽，也不在观察中调用 `GetPacketSlotNum`。

以下任一条件不满足时为 `null`：原生选卡入场完成且未离场、战斗未开始、SeedBank 有效且在场景树内未等待删除、原生引用初始化完成、容量非负且不少于已选数量。非选卡阶段也显式返回 `null`，防止 delta 保留上一关容量。`0` 是原生数值，不代表未知；不要用 truthy/falsy 判断未知。

原有 `chosen_cards`、`chosen_card_details`、`selection_edit_enabled`、`selection_can_add`、`selection_can_start` 语义保留。`selection_can_start` 仍是原生已选数量大于零，不意味着必须满槽。完整 JSON、compact 解码、delta 合并和 Python 终端摘要都保留新字段及锁定卡详情。

协议样例（合成示例，不是实机验收）：

```json
{
  "selecting_cards": true,
  "phase": "selecting_cards",
  "selection_capacity": 10,
  "chosen_cards": ["PlantSunflower"],
  "chosen_card_details": [{"content_id": "PlantSunflower", "locked": true}],
  "selection_edit_enabled": true,
  "selection_can_add": true,
  "selection_can_start": true
}
```

此例完整构筑应为 10 张，其中保留这张固定卡，再选 9 张。候选不足、锁定卡歧义或容量未知时由唯一 Runner 观察后决定是否等待或报告，不凭默认容量组建投票方案。

离开选卡后的 delta 包含 `changes.selection_capacity=null`，并通过 `removed_fields` 清除不再适用的选卡列表/状态（原有逻辑）。不增加选卡动作，不增加投票/竞猜接口，不改变游戏速度、暂停或控制权。

## 安装和验收

本功能从 0.5.1 起提供，当前插件在 `packages/`，项目安装器为 `scripts/install.ps1`。先执行 `./scripts/install.ps1 -CheckOnly` 检查哈希和目标进程；安装器拒绝在该副本运行时替换文件。由唯一 Runner 协调交还控制、确认无在途/未知动作且选择已释放，正常退出副本后备份最新存档，再执行 `./scripts/install.ps1 -UpgradeWhenStopped`。不要用第二个 TCP 客户端验证或抢控制器。

上线验证由原有 Runner 在下次安全启动时完成：核对 `bridge_version=0.5.1`、能力标志；进入原生选卡后检查容量为整数、锁定卡仍在、`selection_can_add` 与原生总数一致；离开后容量为 `null`。离线构建、协议桩和原生 DLL 绑定检查不等于已部署或已在本机选卡实测。
