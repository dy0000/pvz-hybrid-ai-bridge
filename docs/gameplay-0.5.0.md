# 单人玩法支持与接入（0.5.0）

0.5.0 对照 0.31.0.0 游戏原生 feature、菜单和输入订阅补齐以下接入。源码兼容、离线测试和实际关卡验收分别记录；本版新增玩法尚未实战验收。运行中的 0.4.10 不会因为文件构建完成而自动升级。

## 覆盖清单

| 原生玩法/功能 | 接口与状态 | 本版处理 |
|---|---|---|
| 冒险、挑战、生存、益智、迷你游戏、每日挑战 | 原有 menu / flow | 沿用原生开放关卡和有界续关 |
| 我是僵尸 2、杂交乐园 | menu 的 reverse_role / hybrid_park | 增加缺失入口；反向阵营仍由原生卡牌与目标规则决定 |
| 选卡、取消/换卡、开始 | 原有接口 | 查看战场期间禁止更改卡组/开战 |
| 查看战场与返回选卡 | menu 的 view_map / continue_preview | 等原生镜头已进入等待后才发送 ViewBack；不丢失过早发送的返回事件 |
| 砸罐与卡片雨 | break_vase / plant | 走原生鼠标组件与开罐动画；只显示原生已经揭示的罐内内容 |
| 普通/免费传送带、独立地面卡 | 原有 plant；负 ID 精确绑定 | 保留实体、代次、真实报价、消费与选择释放证明 |
| 一次性实体目标卡 | use_target | 接入负 card_id；精确原生卡槽，不按同名卡猜目标卡 |
| 整列种植 | plant；source=column_card 或原掉落来源 | 普通整列卡增加精确负 ID；旧正 ID 标不可执行。原生决定整列地形、实体目标、限种、费用、实际消费 |
| 手套搬植物 | move_plant | 精确实体和铲子层；原生承载、可铲/可移和目标合法性；finally 释放自己的选择 |
| 锤僵尸/修坑 | hammer_hit | 原生锤击和动画，下一物理帧结算；不调用直接伤害、死亡或改生命 |
| 拉老虎机 | slot_spin | 原生阳光费、随机数、冷却、奖励动画；精确 feature_id 和 spin_count 防旧状态重放 |
| 三消 | gem_action | 交换、补洞、刷新、升级；原生费用与消除状态机。绑定棋盘版本 |
| 植物/道具点击技能、双击机器人、瞄准技能 | entity_interact | 精确实体的原生组件输入；保留原生生命期、冷却、点击区域、技能条件 |
| 弹簧菇类按钮 | entity_interact(action=button) | 仅原生可见启用按钮 |
| 原生可拖动单位 | entity_interact(action=drag) | 组件自己处理拖动与落点；结束释放本次选择 |
| 可滑动整格植物栈 | slide_cell | 原生 CanMowerMove/CanMoveToCell/MoveToCell；不同目标格、目标空、地形相同，无相邻限制 |
| 金币、钻石拾取 | collect_coins / auto_collect_coins | 原生拾取动画与入账；与阳光拾取分开，无购物行为 |
| 竞猜玩法 | quiz_* | 原生行押注、换行、确认弹窗；入场扣币受持久预算与回执约束 |
| 星星完成度奖励 | menu 的 star_rewards / claim_star:* | 原生达标后解锁；原生不扣星星，报告为完成阈值而不是假造星星价格 |
| 每日累计奖励 | menu 的 daily_rewards / claim_daily:* | 仅原生可领取按钮，不修改完成天数 |
| 试玩关 | 商店 try_levels；星星页 try_levels；start_trial | 原生组别/条目选择；开关消耗同一 flow 额度 |
| 已解锁铲子/小推车装备 | menu 的 almanac / almanac_props / prop_select:* / equip_prop | 原生图鉴与装备按钮；不能装备未解锁配置 |
| 原生加速按钮 | set_native_speedup | 显式布尔开关；实际倍率由游戏决定，不提供任意倍速。无自动加速 |
| 暂停菜单、重开确认、难度/版本/提示窗 | open_pause_menu / menu | 绑定最上层原生对话框；重开仍计入 flow 重试；版本只关闭提示，不打开链接 |
| 传送门、存活脑、星形布阵目标、生存轮次 | state.mechanics | 新增真实观察；这些机制自身由原生游戏推进，无本地隐藏决策 |
| 雾、地形、墓碑、冰道、坑洞、承载/护甲/升级、教学、火炮、胜败领奖 | 原有观察及接口 | 保留原生行为，无新绕过规则 |

账号登录、创建/删除玩家、联机对战、创意编辑器、联网投稿及任意第三方 Mod 不属于本单人游玩桥。未知回调的通用 DialogBoxChoose 仍只报告内容，两个按钮都不泛化执行：其中“取消”也可能退出关卡或恢复存档。已绑定的竞猜弹窗必须用 quiz_confirm/quiz_cancel。损坏进度恢复等尚不能绑定到确切语义的弹窗仍停在报告阶段，不能靠文字猜按钮、改档或电脑操作绕过。

## 观察与 ID

`observe.state.mechanics` 包含 features、column_planting、reverse_role、finish_method、coins、glove、hammer、slot_machine、gem_match、quiz、formation_goal、portals、brain_rows、survival、speedup。未出现的机制为 null 或 enabled=false，不能由 capability=true 推断当前已启用。

`state.entities[].interaction` 报告 pointer_skill、aim_required、aiming、double_click_skill、button_skill、draggable、dragging、slidable_with_cell。`pointer_skill=true` 表示原生组件存在/活跃，不保证本刻没有冷却或一定产生技能效果；提交仍由组件验证。普通加农炮优先已有 fire 接口。

- battle_id、实体 handle、原生 feature_id 均不跨关/进程。
- coins 的 id 是本次观察加原生池化代次绑定。最新观察会使旧 coin_id 失效。
- gem_match.board_revision 配合 feature_id 一起传回；棋盘坐标从 1 开始，属于三消棋盘，不直接等于地图坐标。
- source=column_card 的 `single_use` 按实际原生卡槽报告，可为 false；该桥接 ID 在一次使用/代次变化后退役。所有卡 ID 都是不透明的有符号整数。
- 付费报价未知仍为 null 且不可用。整列费用、目标和消费由原生统一结算，不能逐行重放一次请求。

## 新操作参数

除 menu_action 与原有 shop_budget/shop_receipt 外，下列都要求当前 battle_id 和本连接 attach。

| op | 参数（除 battle_id） | 成功语义 |
|---|---|---|
| collect_coins | coin_ids[1..128] | collection_started，尚未保证入账 |
| auto_collect_coins | enabled | 本关实时开关，100ms 原生收集 |
| break_vase | entity_id | vase_opening_started，观察动画后内容 |
| move_plant | entity_id,column,row,layer_fraction? | plant_moved，具体实体位置已变，selection_released=true |
| hammer_hit | column,row,entity_id? | hammer_swing_started；指定实体还须处于指定格 |
| slot_spin | feature_id,expected_spin_count | spin_started，native_count_after=before+1；奖励仍在动画中 |
| gem_action | feature_id,expected_board_revision,action | gem_action_started，等待原生消除/回弹/落子 |
| entity_interact | entity_id,action,column?,row? | native_skill_triggered、entity_button_submitted 或 entity_moved |
| slide_cell | from_column,from_row,column,row | cell_slid；格子归属已确认，位移动画可能未结束 |
| open_pause_menu | 无 | pause_menu_requested，之后读取 menu |
| set_native_speedup | enabled | native_speedup_set；读 actual_time_scale，不猜“2X”标签倍率 |

gem_action.action 只允许 swap/fill_hole/refresh/upgrade。swap 必须给 from_column/from_row/to_column/to_row 两个相邻棋盘格；upgrade 必须给本次观察的 upgrade_key。操作期间或版本不符拒绝，不排队延后花费。

entity_interact.action 只允许 click/double_click/aim/drag/button。aim 和 drag 必须给 column,row。有原生瞄准状态时不接管它；本次新建的瞄准/拖动由同一操作在 finally 释放。输入只送到绑定实体的原生 `_Input`，没有桌面输入或全局 ParseInputEvent。`native_activation_confirmed=true` 只确认原生技能回调被触发，不能解释成伤害、击杀或动画完成。

新动作经过原有玩家锁、单连接、当前 battle、暂停/模态及本地计划门禁。auto_collect_coins 在 detach/断线/停止/换关时关闭；暂停和非运行状态不收。sun auto_collect 保持独立。

## 竞猜金币

开发此能力不授权任何实际下注。调用者需要人类明确同意的竞猜范围及总游戏金币上限。

1. 读 mechanics.quiz 的 feature_id、bets、multiple、price 和 native_minimum_balance。
2. quiz_configure(feature_id,bets=[{row,wager,choice}]) 修改原生 UI。choice 为 win/lose/skip，金额遵守原生 Min/Max/Step；先验证整批再逐项改控件，不扣金币。
3. 可选 quiz_refresh_row(feature_id,button_id,row)，只动精确原生换行按钮；之后重新读倍数和报价。不会替 AI 决定换哪行。
4. 用已有 shop_budget(budget_id,max_total,allowed_effect_keys=["quiz_entry"],ttl_seconds) 记下人类授权。与商店共用同一金币账本，既有保留/花费不能被竞猜绕过。
5. quiz_prepare(feature_id,transaction_id,budget_id,expected_price,max_spend) 打开正常原生确认窗，不扣费。
6. quiz_confirm(feature_id,transaction_id) 由 AI 确认；无需人类逐单点确认。原生弹窗的人类点击同样经过持久 committing 写入、完整押注/倍数/目标/预算复核和原生 Run。
7. 只在确切扣币且 native running 从 false→true 后记 committed。此证明是入场成功，不是竞猜获胜。丢回复查 shop_receipt(transaction_id)，unknown 不重放/换 ID；重启后未确认的 prepared 可 quiz_cancel 释放，原弹窗不存在不能继续确认。

原生 UI 的可用余额门槛可能高于实际入场扣费，接口两项都如实报告并保留该规则。没有直接改金币、存档、奖励、赔率或随机结果。

## 验证和部署

本版验证记录位于 `artifacts/gameplay-0.5.0/`，包括原生输入订阅扫描、离线/兼容测试日志及包指纹。扫描依据为当前游戏 DLL，不把 UI 存根、debug 快捷键或未订阅按钮冒充可玩的新能力。

发布前执行一次完整 C# 离线/原生兼容回归、Python 回归和 skill 验证；日常迭代只运行相应边界。未停止健康 Runner、未连接游戏 TCP、未用验收脚本换关。新 Mod 只能在唯一 Runner 交还、无在途/未知动作及输入释放有证据后，正常退出、备份最新存档再安装。不能热替换已加载 DLL。
