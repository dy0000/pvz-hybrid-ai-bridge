# 复杂操作与恢复

## 地形、分层与升级

plant 支持承载、护甲、升级和扩展格；地形/占位/前置不明才 placement_check，必要时 inspect_cell。

inspect_cell 看 terrain/water/lane_enabled/layers，层含 entity_id/slots/provides_terrain/surround；placement_check 看实价、可用性、目标、upgrade_bases/受限原因，不保证稍后提交成功。

| 原因 | 处理 |
|---|---|
| cooldown | 做其他动作或等冷却 |
| insufficient_sun | 等真实入账或调整预算 |
| missing_lilypad / missing_pot | 正常种承载，确认 Committed 再放上层 |
| missing_upgrade_base | 按 upgrade_bases 补前置 |
| planting_blocker | 用本关正常处理手段 |
| entity_target | 非整列用返回的 entity_id 调 use_target；整列仍用 plant |
| native_placement_restricted | 查分层、占地与特殊规则，不编造缺件 |
| card_unavailable | 检查卡槽与阶段 |

## 实体卡、铲子与火炮

实体目标用 use_target(battle_id,card_id,entity_id)，整列仍用 plant。正 ID 同名目标槽无法唯一绑定会拒绝，不猜替代槽/别名；一次性消费见 [掉落卡](falling-cards.md)。

shovel 的 layer_fraction 为0–1，默认0.5；只铲指定层，不为重排队列清空。shoveled 才表示移除；shovel_effect_confirmed 可仅是伤害/坑洞变化，格未必空。

fire 用 can_fire=true 的炮实体ID，目标为地图内1基格；受理不代表击杀。

## 玉米大炮与多格升级

按当前 card_info/placement_check 核前置、占地、承载与费用，不套原版价格/基础植物/坐标。各前置 Committed 后再 plant 升级，不在同批预支底座；种炮成功取真实实体ID，再按上节 fire。

## 回执与恢复

| 返回 | 处理 |
|---|---|
| Committed | 已确认种植，按回传状态继续 |
| selection_requested / battle_start_requested / navigation_requested | 等真实阶段后继 |
| collection_started / 技能触发 / completion_pending | 等原生动画或效果，不宣称完成 |
| rejected | 根据原因调整；条件不变不重复 |
| Submitted / unknown / interrupted / 超时 / 断线 | 查原请求回执、现场与事件，不重放 |
| stale ID | 刷新对应作用域再决策，不只换 ID 重放业务 |

未知无证据就保持未确认，历史缺失不能补演。只核当前动作的直接证据，不让各层重复证明同一状态。

断线先确认所有者释放，再建一个会话观察；玩家锁不靠重连/配置/注入解除。切关 pending 等新 battle，模态按菜单。重启/部署须唯一控制器交还、无在途未知动作且输入释放，不打断健康 Runner。
