# AI 接入协议（0.5.16）

推荐使用 `scripts/run_mcp.py`（MCP stdio）或 `python/pvz_ai/client.py`。游戏进程内运行原生 Mod，不需要截图、OCR、系统鼠标键盘、内存地址偏移或注入。

0.4.0 提供可选的游戏视口 PNG 导出，仅用游戏渲染数据，不调用桌面控制。当前副本 `local_decisions_enabled=false`，新增直播能力不会自行打开本地种植计划。

## 目标与连接

本项目只使用 `game-sandbox` 副本。默认连接文件为 `%APPDATA%/PVZ-Hybrid-AI-<安装标识>/AiBridge/connection.json`，默认端口 47632。游戏加载 Mod 时生成本次进程的新令牌、PID、实例名、EXE 和实际存档路径。Python 默认检查 `instance=sandbox` 且 `user_data_dir` 与连接文件所在实例目录一致，文件缺失时不回退到原游戏。

显式指定 `PvzClient(config=...)` 或环境变量 `PVZ_AI_CONNECTION` 可配置另一个明确的实例。调用者承担目标确认，不能把旧路径当成自动回退。不要将令牌放进模型提示、聊天或公共日志。

底层是仅监听 loopback 的 UTF-8 JSON Lines TCP。每条请求格式：

```json
{"id":"唯一请求ID","token":"从连接文件读取","op":"observe"}
```

一条完整响应之后才发下一条，不支持网络流水线。最大请求 64 KiB，单实例只服务一个连接。一个 AI 会话保持一条连接，避免每个动作都重新接管。

## 控制语义

`attach` 默认 `mode=realtime, background=true`：持续运行，不因观察、思考、种植而暂停。后台设置只为当前控制会话临时修改，释放/断开恢复。实时断线不会由 Mod 自动冻结战斗，调用者必须考虑失联期间游戏仍推进。游戏自己的模态暂停不能被战斗动作绕过，先查询菜单执行返回的 `resume`。

用户明确要求按帧测试时才能设置 `mode=turn_based`。此时由 Mod 暂停，`step` 放行 1–600 个物理帧后暂停。实时模式拒绝 `step`。F8 是玩家紧急停止并暂停，F9 交还控制；这两项会锁住再次接管，只能由玩家在游戏窗口按 F10 解锁。

## 操作

所有战斗坐标为从 1 开始的 `column,row`。关卡目录返回的索引是游戏内部索引，应原样使用返回的菜单动作 ID，不自行拼接。

| op / MCP 工具 | 主要参数 | 结果语义 |
|---|---|---|
| `capabilities` / `pvz_capabilities` | 无 | 版本、默认模式、支持操作、限制 |
| `observe` / `pvz_observe` | 可选 view、executable_cards_only | 当前状态；默认完整 JSON，compact 须解码 |
| `observe_delta` / `pvz_observe_delta` | base_snapshot_id，可选相同 view、executable_cards_only | 显式基线的顶层变化；失效时返回 full/reset |
| `card_info` / `pvz_card_info` | content_ids（1–32 项） | 原生结构化卡牌配置及实际槽位报价 |
| `attach` / `pvz_take_control` | 可选 mode、background | 独占当前连接的控制权 |
| `detach` / `pvz_release_control` | 可选 resume | 实时默认交还并运行；按帧默认留下暂停 |
| `stop` / `pvz_stop` | 无 | 明确请求停止并暂停 |
| `menu` / `pvz_menu` | 无 | 当前 menu_id、阶段、可用动作和关卡数据 |
| `menu_action` / `pvz_menu_action` | menu_id、action_id | `navigation_requested`，之后重新观察 |
| `catalog` / `pvz_catalog` | 无 | 当前卡牌/可选卡牌的游戏说明 |
| `choose_card` / `pvz_choose_card` | battle_id、content_id | 发起游戏选卡动画；观察 chosen_cards 确认 |
| `deselect_card` / `pvz_deselect_card` | battle_id、content_id | 原生取消已选卡；强制锁定卡不可取消 |
| `replace_card` / `pvz_replace_card` | battle_id、content_id、replacement_content_id | 顺序取消再选择；可能 selection_partial，不能盲目重放 |
| `start_battle` / `pvz_start_battle` | battle_id | 发起当前已选阵容开局；观察 running 确认，不将请求受理当作开战完成 |
| `plant` / `pvz_plant` | battle_id、card_id、column、row | 仅原生 `Committed` 才返回 ok=true；正常扣费/冷却 |
| `legal_cells` / `pvz_legal_cells` | battle_id、card_id | 当前时刻放置提示，不是未来成功承诺 |
| `shovel` / `pvz_shovel` | battle_id、column、row，可选 layer_fraction | 原生铲子及层选择；范围 0–1，默认 0.5 |
| `fire` / `pvz_fire` | battle_id、entity_id、column、row | 已就绪炮的正常发射；entity_id 不是 card_id |
| `collect` / `pvz_collect` | battle_id、sun_ids 数组 | `collection_started`；动画完成后入账 |
| `auto_collect` / `pvz_auto_collect` | battle_id、enabled 布尔值 | 本关实时自动拾取开关；每 100 毫秒走原生 Collection 动画及入账 |
| `events` / `pvz_events` | after_event_id、limit、narration_only（均可选） | 增量战况及事实解说事件 |
| `inspect_cell` / `pvz_inspect_cell` | battle_id、column、row | 地形、承载/护甲层、实体 ID |
| `placement_check` / `pvz_placement_check` | battle_id、card_id、column、row | 正常放置提示、结构化受限原因、升级前置与实体目标 |
| `use_target` / `pvz_use_target` | battle_id、card_id、entity_id，可选 intent_id | 特殊卡作用于精确实体；保留原生目标限制、扣费和冷却 |
| `capture_frame` / `pvz_capture_frame` | 可选 max_width，320–1920，默认 1280 | 游戏渲染视口 PNG；返回图片、时间、场景和保存路径 |
| `stream_update` / `pvz_stream_update` | thinking、goal、reason、ttl_ms、visible、intents | 仅发布直播意图与显示状态，不执行种植 |
| `stream_status` / `pvz_stream_status` | 无 | 意图、已确认回执、事件、控制和流程状态 |
| `flow_start` / `pvz_flow_start` | max_levels，可选 max_retries、max_seconds | 授权有上限的连续游玩；不自动开始关卡 |
| `flow_status` / `pvz_flow_status` | 无 | 当前累计进入关数、重试次数、截止时间及待确认跳转 |
| `flow_stop` / `pvz_flow_stop` | 无 | 停止后续开关授权，当前战斗继续运行 |
| `set_plant_plan` / `pvz_set_plant_plan` | battle_id、plan_id、jobs、sun_budget，及下述可选参数 | 受预算和次数约束的本地种植计划；接受不等于已经种植 |
| `plant_plan_status` / `pvz_plant_plan_status` | 可选 after_event_id（默认 0） | 读取当前/最近计划及游标之后的执行事件；不取得控制 |
| `cancel_plant_plan` / `pvz_cancel_plant_plan` | battle_id、plan_id | 停止后续调度；已提交原生动作不能撤回，需等 in_flight=false |
| `step` / `pvz_step` | battle_id、frames | 仅按帧模式，返回实际完成帧数与新状态 |

0.4.6 起 `observe` 在初始化期间也返回真实选卡状态：`selecting_cards`、`available_choices`、`chosen_cards`、`selection_can_add`、`selection_can_start`。选卡须等原生面板入场动画完成，离场时不可操作；节点存在或可见不代表选卡已开放。普通教学对话仍由菜单流程推进，不能提前选卡或发起开战。请求受理后观察后继，不自动重放。

`selection_capacity: integer|null` 是当前已初始化原生选卡栏总容量，包含已选及锁定卡占用的槽位，不是剩余槽位。仅 `selecting_cards=true` 且原生 SeedBank 初始化完成、总数非负且不少于已选数量时返回数字，其余返回 `null`。读取 `TowerDefenseManager.Instance.seedbankPacketMax`；原生 `TowerDefenseInGameSeedBank.InitializeReferences` 通过 `GetPacketSlotNum(slotConfig)` 初始化此值，`CanAddPacket()` 也使用它。观察不调用容量计算或购买标记，不修改容量。`capabilities.features.selection_capacity=true` 表示支持此字段。完整、compact、delta 和终端摘要均保留此字段，离开选卡后以 `null` 清除旧容量。选卡布阵须保留 `chosen_card_details[].locked` 卡，用总容量决定完整卡组数量；未知容量不得猜成默认槽数。字段契约与样例见 [选卡容量](docs/selection-capacity.md)。

0.4.7 增加 `environment`（原生当前昼夜及来源）、`map_preview`（初始化也可读的就绪地形）、`obstacles`（墓碑、坑洞与非 character 冰道）及同快照的 `cells.layers/support_layer_ids/upper_plant_layer_ids/obstacle_ids`。未就绪为 unknown/null，不把空落点当作无墓碑。底座身份按原生承载标记确定，与攻击能力无关；原生放置仍是最终判定。完整字段、快照域、partial/unknown 语义和接入示例见 [观察契约](docs/observation-contract-0.4.7.md)。

0.4.8 增加暂停/模态门禁、取消/替换选卡、真实铲除效果确认、结构化卡牌信息、显式可执行卡视图、无损 compact/delta，以及原生商店全流程。完整操作参数、购买预算/防重放/收据、压缩解码和覆盖采集见 [0.4.8 接入契约](docs/bridge-0.4.8.md)。商店操作为 `open_shop/close_shop/shop_observe/shop_navigate/shop_budget/shop_prepare/shop_confirm/shop_cancel/shop_dismiss/shop_receipt`，MCP 名均为 pvz_ 前缀。准备和目录查询不花钱，只有明确的人类商品/预算授权才可确认购买。

0.4.9 选卡观察新增 `selection_edit_enabled` 和 `chosen_card_details[{content_id,locked}]`；直播工作室据此允许取消/换卡，满槽也可替换未锁定卡。购物准备会正常弹出原生确认，由 AI 调 `shop_confirm`，已有商品范围和总预算后不要求用户逐单手动确认；同一弹窗的手动确认/取消也走持久交易同步。备用 play_session 先解码 compact/delta，再输出明确标识的终端摘要，原始回执不变。

0.4.10 增加原生掉落卡 `source=falling_card`，每个实例使用独立负 card_id，按实际 cost/available 使用现有 plant。付费报价未知时 cost=null 且不可用；不把不可用 native 卡槽当作掉落卡，不按内容名合并。一次性卡成功必须确认原生消费与 `selection_released=true`，状态新增 `card_selection`；完整身份生命周期、别名边界及接收方验收见 [掉落卡契约](docs/falling-cards-0.4.10.md)。

0.5.0 补齐单人特殊玩法：原生砸罐/锤击、手套、老虎机、三消、实体点击/双击/瞄准/拖动技能、整格滑动、金币收集、竞猜预算确认、整列种植与一次性实体目标卡；新增原生模式/试玩/奖励/装备及提示窗入口。`state.mechanics` 和 `entities[].interaction` 提供对应真实状态。完整参数、成功/待完成语义和已知边界见 [0.5.0 玩法接入](docs/gameplay-0.5.0.md)。所有新 MCP 工具仍为 pvz_ 前缀。任何已实现接口都不等于该场景已经实战通过。

`menu` 覆盖标题、单人模式、正常章节/关卡、每日挑战及累计奖励、星星解锁、试玩、图鉴/已解锁装备、战场预览返回、普通 NPC 对话、暂停/重开和明确类型的提示窗。动作绑定当前最上层弹窗与原生对象，关卡来自实际开放选项。通用二选一回调不按文字猜测：两个按钮都可能有副作用，使用已绑定的专用语义操作；未知弹窗报告类型，不绕过。

0.2.2 支持 NPC 手持道具展示对话；`dialogue_text` 返回本地化正文，`dialogue_type` 标明对话类型。内嵌教程运行期间不会提供跳过动作，只有原游戏确认条件完成后才允许继续相应对话。对话动作绑定观察时的具体对话行，行变化后拒绝旧动作。

`observe.state.tutorial` 和当前对话的 `menu.tutorial` 提供教程步骤（从 0 开始）、总步骤数、提示正文与条件进度：`character_count` 包含植物/角色内容 ID、比较符、目标数和当前数；`sun_collected` 包含目标阳光量和实际收集量。没有活动教程时为 null。观察不调用教程的 Enter/Step/Exit，也不修改完成状态；仍通过正常种植、收集等动作满足条件。未知条件会标记 `supported=false`，不会自动跳过。

0.4.4：嵌入式教学可能处于 `initializing=true,running=false`。当前原生步骤已进入且地图完整时，observe 返回 `phase=tutorial` 和完整 `entities/cells/cards/suns`；普通初始化仍早退，不读取未就绪实体。`tutorial.actionable=true` 表示至少一项所需动作此刻可提交，`tutorial.allowed_ops` 只包含 `plant`、`shovel`、`collect` 中当前未满足条件需要的动作；空列表表示等待原生动画/步骤切换。该标记不代替 attach、battle_id、玩家锁和暂停门禁；教学操作仅支持 realtime。

`character_count.required_action` 是 `plant`、`shovel` 或 null，取决于实际数量及原生比较符。教学种植只允许条件指定的角色，铲除也只允许当前条件需要减少的角色；不开放 fire、use_target、step 等其他战斗动作。自动拾阳光可预先启用，但非运行阶段只在未满足的收集教学中执行原生收集。

`state.shovel_enabled` 反映原生铲子就绪、配置、`CanUseShovel` 权限和阶段门禁：原生允许 `shovelShow || isGameRunning`，战斗运行时不强求教学显示标志。`state.shovel_status` 提供 `enabled,reason,manager_ready,config_ready,shovel_show,battle_running,native_can_use`，缺少可读原生对象时相应证据为 null；`native_permission_denied` 仍遵守原生解锁/IZM/IZM2 限制。能力标志为 `shovel_gate_diagnostics=true`。这只是工具可用性，不代替控制权、玩家锁、battle_id、暂停/模态及教学目标授权。详见 [铲子门禁](docs/shovel-gate.md)。`tutorial.shovel_targets` 仅列出本教学允许铲除的当前原生目标，每项含 `row,column,layer_fraction,content_id,character_name,entity_id,slots,surround`；`inspect_cell.shovel_targets` 提供该格的普通原生分层候选。entity_id 不可取得时为 null。候选不预先铲除上层，也不保证隐藏下层已可选；完成一次后按新状态决定下一次，不能跨步骤重放旧列表。仍用现有 `shovel` 参数和 `outcome=shoveled` 回执。

免费传送带卡：`cards.source=free_conveyor` 的卡用负整数 `id` 唯一绑定原生卡槽，跨观察保持稳定；消费、离场或换关后失效。`card_id` 始终是不透明的有符号整数，不能按数组索引、正数假设或内容名重新生成。`cost=0` 是原生 `useCost=false` 的实际费用，卡牌仍须满足原生可用、冷却、锁、账户与放置规则。使用现有 `plant`；只在原生 ModUseSequence 增加后返回 Committed，正常消耗该一次性卡槽，不改 start/useCost 标记。原生 API 自身可能另列同内容的 `source=native,available=false` 项；只按本次观察的可用卡 ID 决策，不能按内容名合并卡槽或把旧 ID 替换成新 ID。本地计划不使用这些卡；只有运行实例实际报告 0.5.0 的 one_use_target_cards/column_planting 能力后，才使用对应目标/整列扩展。

首次新存档的玩家档案创建、账户登录、联机、编辑器不在接口范围。当前不提供修改系统日期、自动续关或修改关卡结果。每日挑战支持原生日历中的历史日期选择，不要求更改系统日期。

0.5.16 起，`capabilities.features.daily_calendar_navigation=true`。每日挑战日历的 `menu.phase=daily_calendar`，返回所选 `date`、显示月份 `calendar_year/calendar_month` 及 `calendar_dates`（各日期的 `date/available/selected/completed`）。通过本次菜单的 `daily_prev_month` / `daily_next_month` 翻月、`daily_date:YYYY-MM-DD` 选择当前月可点击的日期，然后重新读取菜单，用 `daily_levels` 列出所选日期的挑战。原生月份数据可能异步加载并重建日期按钮；新数据就绪后重新读菜单，不能重放旧按钮。禁用日期不提供选择动作，`daily_levels` 也遵守原生开始按钮的禁用状态。不更改日期数据、存档或解锁条件；实际 `start_daily` 仍受现有 flow 额度约束。

## 直播事件与解说

`capabilities.features` 是布尔值 object，键为 `live_events`、`narration_events`、`placement_diagnostics`、`target_card_use`、`capture_frame`、`bounded_flow`、`stream_status`、`stream_update`、`status_file`。按能力探测接入，不能只判断版本。

`events` 默认 after_event_id=0、limit=64（1–128）、narration_only=false。返回 `{ok:true,feed:{stream_id,latest_event_id,next_event_id,reset_required,events_truncated,events}}`。每条事件含 event_id、kind、battle_id、at_unix_ms、speech、source、data。游标属于 stream_id，换实例后重置为 0；最多保留 256 条，超出会标明丢失。过滤无解说的事件时游标仍向前推进，不会卡在空页。建议现有唯一控制器每 100–500 毫秒取增量，不为解说另建连接。

战况每 100 毫秒采样，事件包括 battle_entered、scene_changed、wave_changed、zombie_type_seen、plant_removed、lane_danger、card_ready、pause_changed、battle_lost、reward_pending。plant_removed 只表示已观察植物离场，不推断一定被吃掉。card_ready 表示从不可用变为可用，可能同时涉及冷却和阳光。lane_danger 指实际位置到达左侧第 3 列及以内，仅发出信息，不自动改阵。

操作事件包括 plant_committed、plant_submitted、action_failed、action_result 和控制权变化。speech 可用于字幕/解说素材，但不代表已发送到直播平台。奖励出现只报告“尚未领取和完成结算”。AI 的部署理由属于声明意图，不能冒充游戏事实。

## 完整放置和特殊卡

普通 `plant` 原本就经过原生放置服务，支持合法的水面承载、花盆、护甲叠放和升级覆盖；0.4.0 补上诊断及精确目标接口，不修改游戏规则。`inspect_cell` 返回 terrain、water、lane_enabled、layers；每层含 entity_id、content_id、name、slots、provides_terrain、surround。

`placement_check.placement` 包含 card_available、cost、cooldown_remaining、native_cell_hint、can_submit_cell、requires_target、targets、upgrade_bases、cell、reasons。原因码包括 cooldown、insufficient_sun、missing_lilypad、missing_pot、missing_upgrade_base、planting_blocker、native_placement_restricted、entity_target、card_unavailable。只在实际条件支持时给出具体原因，否则保留原生规则受限，不能把提示当成未来种植保证。普通 plant 的不可用拒绝也会附 placement。

`use_target` 仅对支持该实体种类的卡牌开放，目标 ID 来自 observe、inspect_cell 或 placement_check。使用原生 PlantOnPlant/PlantOnZombie 处理，确认 ModUseSequence 增加后才报告 Committed。重复同内容的可用实体目标卡槽无法唯一绑定时拒绝，不猜卡槽；普通格子操作仍可使用精确 card_id。没有自动补睡莲、补花盆、铲除或免除升级前置。普通层叠铲除继续用 shovel 的 layer_fraction；就绪加农炮继续用 fire。

## 游戏画面

`capture_frame` 只读取当前游戏视口，可在后台工作。返回 `{ok,path,mime_type,width,height,captured_at_unix_ms,scene,battle_id,source:"game_viewport",png_base64}`；MCP 会把 png_base64 转成原生 image 内容块，文本仅保留元数据。上限每 500 毫秒一次、压缩后 4 MiB，过大时请降低宽度。图片保存在该隔离存档的 `AiBridge/captures/`，不接受任意输出路径，也不截取其他程序。画面来自已渲染帧，不等于某个异步操作已经完成。

## 直播状态面板与只读文件

`stream_update` 需要当前控制权，全量替换声明意图。参数 thinking 默认 false；goal 最长 200 字符；reason 最长 300 字符；visible 默认 false，且需本机 pvz_ai/in_game_panel_enabled=true 才能显示（当前为 false；客户端不能自行打开）；ttl_ms 为 500–30000，默认 5000；intents 最多 32 项。每项必需 id、content_id、column、row，可选 reason、status（只允许 pending/waiting/submitting）。所有条目使用本次统一 TTL，重复 id 或地图外坐标会拒绝。此接口完全不执行种植，本地决策关闭时仍可使用。

plant、use_target、shovel、fire 可带 intent_id 关联声明。Committed 才会将对应意图标成 committed；Submitted 或普通接受标成 submitted；确定拒绝为 failed，结果未知为 unknown。待执行展示应只保留 pending/waiting/submitting，成功历史从 receipt_history 获取。TTL 过期、失去控制或换关后 intentions 清空。停止不会撤回已发生操作。

`stream_status` 返回 `{ok:true,status:{...}}`。同一个 status 对象每 250 毫秒原子写入 `capabilities.status_file`（文件没有 ok/status 包装）。默认是 `%APPDATA%/PVZ-Hybrid-AI-<安装标识>/AiBridge/live/status.json`；外部 overlay 只读该文件，**不要新建第二条 TCP 连接**。`generated_at_unix_ms` 超过 `stale_after_ms=2000` 应隐藏实时意图，不能继续展示陈旧操作。文件不包含连接令牌。退出后文件可能仍存在，新鲜度校验不可省略。

status schema_version=1：顶层含 stream_id、generated_at_unix_ms、stale_after_ms、expires_at_unix_ms、battle_id（无关卡时 null）、scene、mode、ai_attached、control_locked_by_player、paused、local_decisions_enabled、thinking、intentions_expired、goal、reason、overlay_visible、intentions、queue_semantics、receipt_history、recent_events、last_action、last_failure、telemetry_error、wave、sun、flow。queue_semantics 为 declared_intent_order_not_execution_guarantee；数组是声明顺序，不承诺实际执行顺序。

intention 含 id、content_id、name（本地化）、column、row、reason、status、battle_id、expires_at_unix_ms、source="ai_intent"。receipt_history 保留最近 12 个操作结果，含 request_id、op、outcome、ok、error、intent_id、content_id/name（已知时）、column/row、action_id、at_unix_ms。recent_events 为最近 8 条事实事件；stream_id 变化时清空消费者游标。界面是只读、不截获鼠标，visible=false 可隐藏内置面板而继续文件导出。

## 有界连续游玩

**0.4.0 开始关卡前必须先 flow_start**：max_levels 1–100，max_retries 0–20（默认 0），max_seconds 10–14400（默认 1800）。启动流程时已有战斗计为第 1 关；level:*、start_daily、next_level、retry_level 都受此授权约束。达到限额或时间到期后仍可领奖和返回，但不能再开启关卡。流程不自动选卡、不自动游玩，也不启用本地种植决策。

失败弹窗的 menu 提供 retry_level 和 finish_level；新植物奖励结算页提供 next_level 和 finish_level；钱袋领奖原生返回选关后按新 menu 选已开放的下一关。每次仍由 AI 用新 menu_id 明确选择。接受导航请求只标记 pending_action，实际出现新的 battle_id 才增加关数/重试数；未确认转换 20 秒后变为 transition_unknown，不自动重发。flow_stop、断线或交还控制后需重新授权。正常菜单返回与 unsupported_dialog 行为不变。

## 种植计划

临时总开关：副本 `override.cfg` 中 `[pvz_ai] local_decisions_enabled=false`，重启加载后禁止新的本地种植计划并停止计划调度。`capabilities.local_decisions_enabled` 和 `observe.state.local_decisions_enabled` 返回实际状态。关闭时仍可使用单次 plant 与独立的 auto_collect；恢复必须由用户明确要求后将配置改为 true 并重启。

仅实时模式。`plan_id` 是调用方生成的 1–64 字符唯一 ID，本关不得重放；同一请求 ID 的原有回执仍可查询。每关最多提交 128 个不同计划。活跃计划必须先取消，再提交新的计划；验证失败不会替换原计划。手工 plant/shovel/fire 与活跃计划不能并行。

顶层参数：`sun_budget` 为累计种植花费上限；`reserve_sun` 为每次种植后至少保留的阳光（默认 0）；`ttl_seconds` 为墙钟有效期 1–600 秒（默认 180，暂停时仍计时）；`emergency_priority` 默认 true；`threat_column` 为逼近家门的列阈值（默认 4，必须在地图内）。紧急任务同样受预算和保留阳光约束。

`jobs` 每项包含唯一 `id`、从本关卡槽观察到的 `content_id`、`cells=[{"column":1,"row":3},...]`。可选 `role` 为 attack/block/economy（默认 attack），`priority` 为 0–100（默认 0），`max_uses` 为 1–32（默认 1），`threatened_only` 默认 false。最多 32 项，每项 32 个候选，全计划最多 128 个候选和 128 次种植。角色由 AI 根据图鉴明确分类，执行器不猜植物功能。

每 50 毫秒检查一次本关现场，生成当前可用卡牌、足够阳光和合法空格的候选。只从这些候选中选择：正常按 priority；有僵尸的行缺少本计划标注的 attack 植物时优先补火力；逼近 threat_column 时优先该行的 attack/block 任务。同分按任务与格子输入顺序。`max_uses>1` 允许填满多个候选，或在植物消失后补种，始终受剩余次数和预算限制。没有新的自动铲除、叠放或整列种植能力；整列种植模式会拒绝计划。

`observe.state.plant_plan` 包含状态、花费、剩余时长、任务次数、等待原因和事件游标，但不重复返回历史事件。`plant_plan_status(after_event_id=...)` 返回新事件，游标属于当前 plan_id；换计划后从 0 开始。最多保留 64 条，`events_truncated=true` 表示请求的旧历史已丢弃。`plant_committed` 才算成功，含格子、花费、优先原因、最近僵尸列、是否已有火力和耗时。`decision_ms` 是本地选取候选的计算耗时，`native_commit_ms` 是调用原生卡牌到确认结果的耗时，`scan_gap_ms` 是本次与上次调度检查间隔；这些不是端到端模型耗时，也不单独证明条件刚满足的准确时刻。

等待原因包括 `cooldown`、`sun_or_reserve`、`budget_limit`、`cells_blocked`、`waiting_for_threat`、`game_restriction`、`card_missing`。换关、胜败、断线、detach/stop、F8/F9、切换按帧模式和非 resume 的菜单导航会停止后续调度。取消不能撤回已经提交的原生动作，晚到的 Committed 仍会记录与计费。原生拒绝停止计划供 AI 重新规划；Submitted、失败、异常或超时不会盲目重试，未知结果会锁住自动种植，直到换关。

提交示例（必须先用实际战场替换 battle_id 并确认候选格）：

```json
{"op":"set_plant_plan","battle_id":"实际观察值","plan_id":"新生成的唯一ID","sun_budget":750,"reserve_sun":25,"ttl_seconds":180,"emergency_priority":true,"jobs":[{"id":"lane3","content_id":"PlantSunflowerPea","role":"attack","priority":10,"max_uses":6,"cells":[{"column":1,"row":3},{"column":2,"row":3},{"column":3,"row":3}]}]}
```

## 状态与时效

`observe.state` 包含 bridge_version、mode、scene、phase、observation_id、captured_at_unix_ms、physics_frame、paused、控制权、battle_id、simulation_seconds，以及实际可用时的 sun、wave、cards、entities、suns、cells。初始化过程中 `initializing=true`，只返回已存在的字段，不能假定所有战斗数组必有。

卡牌提供实际价格、剩余冷却、可用性。角色提供阵营、格子、世界坐标、血量、护甲、死亡/移除状态；带炮组件的角色还提供 can_fire、cannon_mode。`battle_awarded=true` / `phase=victory_reward_pending` 表示游戏已创建胜利奖励，尚未完成领取和结算；`lost=true` 表示失败。僵尸数组为空不等于获胜。

`auto_collect` 默认为 false，必须针对当前 battle_id 显式开启；换关、断线、detach、stop、F8/F9 后关闭。暂停期间不收集。`auto_collection_started` 是本关发起收集的数量，不是即时入账额，余额仍以动画结束后的 `sun` 为准。此功能不种植、不选择战略、不自动领取通关奖励。

胜利奖励由 `menu` 提供 `collect_victory_reward`，沿用原生奖励按钮处理，最多领取一次。领取后观察场景：钱袋原生流程直接回到 `LevelChoose`；植物奖励进入 `AwardSettlement`，`phase=settlement`、`level_completed=true` 表示当前结算页可见，再用 `finish_level` 返回选关。`level_completed` 不是跨场景的历史标记，返回选关后为 false；完整结束证据是同会话的领取请求及后继结算/选关状态。不要只凭 `battle_awarded` 提前结束、释放后台运行或宣称完整通关。

每个战斗动作必须带观察得到的当前 battle_id。卡牌/角色 ID 只在相应战斗会话内有效。阳光 ID 绑定最新一次观察及游戏对象池的 lease；新观察、消失、已收集或对象池复用后会拒绝旧 ID。菜单每次查询产生新 menu_id，动作执行后失效。实时状态会继续变化，应及时执行。

`snapshot_ms` 是游戏主线程快照耗时，`round_trip_ms` 是 Python 客户端本次通信耗时；两者不包含模型决策时间。现有实机菜单通信样本约 18–37 ms，不能据此保证模型有相同反应时间。

## 错误与恢复

正常拒绝返回 `ok=false, outcome=rejected, error=...`。发送动作后异常可能返回 `unknown_observe_before_retry`；被游戏阶段/模态中断返回 `interrupted_observe_before_retry`。网络超时或响应 ID 不匹配也视为结果可能未知。

不要自动重试已提交的种植、收集、选关。先读取当前状态确认结果，然后作出新决策。同连接最近 256 个请求 ID 缓存结果，同 ID 同参数返回原结果；同 ID 不同参数被拒绝。跨连接没有持久提交重放服务。客户端没有隐式重试。

## 推荐会话

1. 查询 capabilities，确认实例/版本；take_control 使用 realtime。
2. 查询 menu，执行明确选择的模式、章节、关卡；每步查询后继，不猜坐标或按钮。
3. 必要时继续普通对话、选卡并开始；用 catalog 理解卡牌。
4. 开启 auto_collect，尽早提交短期种植计划。按事件和重要波次检查，必要时取消后改计划；紧急处置优先于长篇解说。
5. 失败以 lost 判定；成功需领取奖励、确认结算/返回选关后再 release_control，避免奖励阶段失焦暂停。

Mod 提供观察、原生动作和有界计划执行。战略由 AI 选择，应急排序仅在本次授权的植物、候选格和预算内进行。其他 AI 可以复用接口，不需要复用本次对话策略。
