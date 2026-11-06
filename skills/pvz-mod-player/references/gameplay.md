# 特殊玩法

按 state.mechanics 和 entities[].interaction 选择动作；参数查 [API](../../../API.md)。坐标从 1 开始，三消坐标属于棋盘。

| 机制 | 操作 |
|---|---|
| 砸罐 | mechanics.vases 的 entity_id → break_vase；vase_opening_started 后等开罐，隐藏内容不猜 |
| 锤击 | hammer_hit 瞄准格子或仍在该格的实体；hammer_swing_started 不等于命中/击杀 |
| 手套 | move_plant 用精确实体、正确 layer_fraction 和目标格；按原生规则搬移，确认到达及 selection_released |
| 三消 | gem_action 绑定 feature_id+expected_board_revision；swap 两格相邻，fill_hole/refresh/upgrade 用原生阳光费；等待消除/回弹/落子 |
| 原生老虎机 | slot_spin 绑定 feature_id+expected_spin_count；正常阳光费、随机结果和冷却；count+1 只证明开始，奖励等动画 |
| 主动技能 | entity_interact 的 click、机器人 double_click、带目标格的 aim 或原生 button；native_skill_triggered 是回调触发，不是后续效果完成 |
| 拖动 | draggable 实体用 entity_interact(action=drag)，结束确认本次选择释放 |
| 滑格 | slidable_with_cell 用 slide_cell；原生可直达合法空目标格、地形相同，不限相邻；旧 Bridge 若仍限相邻则按已装版本执行，不冒称已支持跨格 |
| 金币/钻石 | collect_coins 用本次观察 ID，auto_collect_coins 持续正常收集；与阳光拾取独立，动画开始不等于入账 |
| 阵型目标 | 读 formation_goal.targets 正常种植；完成状态由原生判定 |
| 传送门/生存/反向阵营 | 读 portals、survival、brain_rows、reverse_role；不改门位或跳波/跳轮 |
| 整列/一次性目标卡 | 整列用 plant；非整列实体目标用 use_target，均取精确卡 ID，见 [掉落卡](falling-cards.md) |

只操作当前原生启用的组件/按钮，保留冷却、地形、目标与生命周期规则；unknown 先查回执。

## 菜单与奖励

reverse_role/hybrid_park 进入对应模式。view_map 查看战场，镜头就绪后 continue_preview 返回选卡。start_trial 计入现有 flow 限额。

每日挑战可选原生日历中的历史日期。`daily_calendar_navigation=true` 时，menu 返回 `calendar_year/calendar_month/calendar_dates`；用 `daily_prev_month/daily_next_month` 翻月、当前菜单的 `daily_date:YYYY-MM-DD` 选可用日期，再读新 menu 用 `daily_levels` 列出所选日期关卡。月份数据异步加载时等原生按钮开放后重读，不改系统日期、不点击禁用日期、不复用换月前的动作；`start_daily` 沿用 flow 限额。

star_rewards、daily_rewards 领取原生达标项；星星奖励是完成阈值，不扣星星。almanac_props → prop_select:* → equip_prop 装备已解锁道具。

open_pause_menu 用于正常菜单操作，不为思考暂停。set_native_speedup 只在用户要求改速度时设置，实际倍率读 actual_time_scale。

提示窗用 acknowledge，更新提示用 dismiss_update。通用二选一可能涉及扣币、退出或恢复存档；只走对应专用操作，不猜按钮。

## 竞猜

已有明确竞猜范围和总金币预算后：读 mechanics.quiz → quiz_configure 行 wager/choice → 必要时 quiz_refresh_row 并重读报价 → 建立一次 shop_budget(allowed_effect_keys=["quiz_entry"])，已有预算直接复用 → quiz_prepare → quiz_confirm。后续入场沿用该授权的 budget_id，不重新授予总额。

原生确认窗由 AI 确认，无需逐单人工点击。竞猜与商店共用持久金币预算；保留原生余额门槛。精确扣币且 running false→true 的 committed 只证明入场，不是获胜。丢回复查 shop_receipt；unknown 不重放，未确认准备单可 quiz_cancel。
