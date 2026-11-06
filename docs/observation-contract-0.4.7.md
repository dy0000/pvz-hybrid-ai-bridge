# 0.4.7 原生环境与地图观察契约

适配游戏 0.31.0。新增字段均为只读，不选卡、不补种、不修改地形或游戏规则。已有教学、免费传送带、选卡和开始关卡接口保留。

## 入口与域

仍调用同一控制连接的 `observe`，无需新增工具。`capabilities.features` 新增 `native_environment`、`map_preview`、`obstacle_observation`、`cell_layers_in_observe`。

`map_preview`、`obstacles` 与 `cells` 属于本次 `state.battle_id/observation_id`。`snapshot_id` 形如 `<battle>:observe:<observation>`，层和障碍 ID 只在该快照有效，用于关联信息，不能作为游戏动作实体 ID。真正的 `entity_id` 仍为原生 ID，不能用 layer_id 或 obstacle_id 替代。单格 `inspect_cell` 使用独立 `<battle>:inspect:<counter>` 域。

## 昼夜

`state.environment`：

| 字段 | 语义 |
| --- | --- |
| status | known / unknown |
| day_phase | day / night / unknown |
| is_night | 当前原生布尔值；未知为 null |
| source | 已知时固定为 TowerDefenseBattleFeatureMap.config.isNight |
| map_resource_path | 当前资源路径，运行时创建资源可能为空字符串 |
| transitioning | 原生 isChange；转换中应避免沿用之前地图的落点 |

使用当前 `GetMapIsNight` 所读取的原生 config 字段，不按章节、植物或贴图名称猜测。没有有效原生 config 时不采用游戏 getter 的 false 兜底，以免将未知说成白天。环境与格子 element_flags 分开，后者保留原生元素标记，不能覆盖原生昼夜值。

## 初始化预览与完整性

`state.map_preview` 含 `status`、`terrain_status`、`layers_status`（ready / partial / unknown）、`captured_cells`、`expected_cells` 和来源。

只有原生 `_gridReady=true`、当前地图存在、未转换且网格尺寸匹配时才读取格子。此检查允许在选卡阶段、正常游戏阶段和已就绪教学阶段使用，不访问 HP、护甲或不完整的行为组件。

未就绪时：`map_preview.status=unknown`，`state.cells=null`，`obstacles.status=unknown,items=null,gravestones_present=null`。部分格子、配置或冰道信息尚未就绪时标 partial；未列出的对象/格子不可推断不存在。`ready` 是本次结构化观察的覆盖状态，不是动作承诺，也不是未来刷出障碍的预测。

## 格子与承载

`state.cells[]` 保留 row、column、water、lane_enabled、terrain，并新增：

| 字段 | 语义 |
| --- | --- |
| element_flags | 原生格子的元素位标记 |
| layers_status / layers | 分层覆盖状态与原生对象列表，包含没有 Mod 注册句柄的对象 |
| support_layer_ids | 原生 POT / LILYPAD 或原生承载体型标记认定的底座 |
| upper_plant_layer_ids | 已观察到的非底座、非周围护甲、非虚影植物 |
| has_upper_plant | true / false / null；部分观察且没有看到上层时为 null |
| obstacle_ids | 引用同一快照 obstacles.items |
| pending_gravestone | 原生待绑定随机墓碑占位 |
| support_requirements | 普通地面植物的承载提示；card_specific=true，实际仍由卡牌规则决定 |

每个 layer 包含 layer_id、可取得时的 entity_id、content_id、native_type、status、slots、provides_terrain、accepts_terrain、is_support、native_slot_link、surround、ladder、shield、grid_phantom、hypnosed、preview_only、runtime_ready。缺配置的属性为 null，而不是假造 NOONE 或“无底座”。

`native_slot_link` 是原生 characterSlotDictionary 的原始关联方向；不把它统一改名为“父”或“子”。通常底座会关联上层，特殊覆盖/修复可有不同方向。模型结合 terrain、slots 和 upper_plant_layer_ids 判断，再由原生提交决定合法性。

承载身份独立于攻击能力与名称。普通花盆、豌豆花盆、小盆菇、猫砂盆只要原生声明 POT/承载标记，均会作为底座报告；不因有攻击、经济或特殊能力排除它们。这里不改变它们可适配哪些上层。

## 墓碑与其他障碍

`state.obstacles` 含 status、coverage、gravestones_present、items。

- gravestones_present=true：已从原生位置/格子引用确认至少一个墓碑。
- false：本次支持的原生来源完整且没有墓碑。
- null：覆盖不完整且未确认墓碑。空 legal_cells、未知位置或泛化放置拒绝均不能证明墓碑存在或不存在。

items 合并原生格子 list、slot、层级引用、当前角色树、Gravestone/Crater 专用组与冰道表，按原生对象去重，不依赖 Mod 的 character 查询注册表。coverage 明确限定为原生墓碑、坑洞、冰道和格子引用；不声称识别任意第三方脚本的新障碍种类。

每项有 obstacle_id、kind（gravestone / crater / ice_trail）、native_type、row、column、occupied_cells、status、source、blocks_planting、restricts_planting、blocking_rule、terrain_exceptions。墓碑/坑洞还含 position_status、cell_bound、grid_phantom、card_dependent、preview_only、runtime_ready。未绑定且坐标未确认时 row/column/occupied_cells 为 null；不会输出 0,0 冒充有效格子。

`blocks_planting` 对墓碑精确表示原生 config.blocksPlanting；其他类型没有该原生通用布尔字段，为 null。不能把 false 解读成任何卡都能种：普通墓碑仍可能占据原生槽位。`restricts_planting=true` 只说明已绑定对象具有条件性限制；是否影响特定卡须结合地形例外、阵营、虚影与原生 placement_check。preview_only 或未绑定对象不会被声称为已生效的限制。

冰道来源是原生 iceCapList 的前沿 Sprite 和原生坐标转换，不属于 character。坑洞与普通墓碑报告原生 AIR/CRATER/GRAVESTONE 等地形例外；config.blocksPlanting=true 的墓碑没有普通植物地形例外。

## 既有诊断接口

- inspect_cell 复用相同 CellFact 分层构建，额外返回该格原生障碍描述；地图未就绪时返回 status=unknown、terrain/layers=null，不尝试读取不完整实例。
- placement_check 保留 planting_blocker 原有错误码；仅在原生 FindPlantingBlocker 返回真实对象时附 blocking_obstacle，能确认目标格或扩展格的墓碑，不从 native_placement_restricted 猜测。
- 原生规则不能安全读取时 placement_check.native_cell_hint=null、can_submit_cell=false，并给出 cell_not_ready。
- legal_cells 保留“只在卡可用时枚举”的行为，新增 card_available、enumerated、status、skipped_unready_cells。not_enumerated 或 partial 的空结果不能当作“没有合法地形”，更不能作为障碍物探测器。

## 字段示例（说明用，不是实机证据）

```json
{
  "environment": {"status":"known","day_phase":"night","is_night":true,"source":"TowerDefenseBattleFeatureMap.config.isNight","transitioning":false,"map_resource_path":""},
  "map_preview": {"status":"partial","terrain_status":"ready","layers_status":"partial","snapshot_id":"battle:observe:42","captured_cells":45,"expected_cells":45},
  "obstacles": {"status":"partial","gravestones_present":null,"items":[]},
  "cells": [{"row":2,"column":3,"terrain":["BRICK","AIR"],"support_layer_ids":["battle:observe:42:123"],"upper_plant_layer_ids":[],"has_upper_plant":false}]
}
```

## 验收边界

离线构建、无反射 JSON 测试和导入兼容性检查不能代替原游戏 NativeAOT 验证。必须由现有唯一 Runner 串行取得一次选卡预览与一次对局快照，核对真实昼夜/原生来源、墓碑正例或明确的未知/完整缺席、底座与上层数据，以及选卡/开战未退化。特殊场景未出现时保留未覆盖记录，不为制造用例改存档、刷障碍或中断健康关卡。
