# 0.4.8 选卡、商店与观察接入

适配游戏 0.31.0。原有完整 `observe` 默认格式保留；操作能力以运行时 `capabilities.actions/features` 为准。所有修改操作需要同一连接先 `attach`。保持 `local_decisions_enabled=false` 和 `in_game_panel_enabled=false`。

## 准备阶段与铲子

`choose_card`、`deselect_card`、`replace_card`、`start_battle` 均要求原生选卡页已完成入场、未退出，且游戏没有树暂停或可见模态弹窗。不能用这些接口穿过暂停。

```json
{"op":"deselect_card","battle_id":"实际ID","content_id":"实际已选内容ID"}
{"op":"replace_card","battle_id":"实际ID","content_id":"实际已选内容ID","replacement_content_id":"实际解锁未选内容ID"}
```

强制锁定卡不可取消。替换先原生取消再原生选择，不是原子事务；`selection_partial` 必须按返回的真实 `state.chosen_cards` 继续决策，不重放旧替换。

`shovel` 绑定原生选中的具体目标，只在目标死亡/删除/离场时返回 `shoveled`。原生伤害或坑洞阶段变化返回 `shovel_effect_confirmed`，这不表示格子已经空了。没有即时证据则 `ok=false,outcome=unknown_observe_before_retry`；观察后判断，不自动重铲。普通原生铲子不执行的目标类型会被拒绝。

## 卡牌信息与传送带

`card_info(content_ids=[...])` 每次接受 1–32 个当前可玩或原生已解锁的内容 ID。返回 `base_cost/base_cooldown/starting_cooldown`、`placement_types`、`provides_terrain/is_support`、`upgrade_bases/direct_upgrade_allowed`、`requires_plant_target/can_target_zombies`，以及实际 `live_slots` 的 `card_id/effective_cost/cooldown_remaining/available/source`。

基础配置费用不能代替当前槽位报价。相同 content_id 可以有多个实际卡槽；`card_id` 是不透明的有符号 ID，保留负 ID，不能按名称合并。`observe.state.executable_card_ids` 提供本次可执行槽位；可选 `executable_cards_only=true` 只返回 `available=true` 的 cards，并明确附 `cards_view=executable_only`、`unavailable_cards_omitted`。省略不可用卡不是该卡被移出阵容，也不代表未来不会再出现。

## 商店操作

| op（MCP 为 pvz_ 前缀） | 参数 | 语义 |
| --- | --- | --- |
| open_shop | 无 | 从原生主菜单或就绪选卡页进入；要求自然解锁 |
| shop_observe | 无 | 只读商店 ID、金币、分类/页码、当前可见商品、弹窗及交易状态 |
| shop_navigate | shop_id, action | Total/Item/Packet/Custom/Garden/next/previous |
| shop_budget | budget_id, max_total, allowed_effect_keys, 可选 ttl_seconds | 显式累计金币上限和商品效果白名单；默认 600 秒，范围 10–3600 秒 |
| shop_prepare | shop_id, item_id, expected_stage, expected_price, max_spend, transaction_id, budget_id | 保留预算、绑定报价、打开原生确认；不花钱 |
| shop_confirm | shop_id, transaction_id | 一次原生确认；核对扣币和实际效果 |
| shop_cancel | transaction_id | 取消尚未提交的交易及其弹窗，释放预留；不撤销已购买项 |
| shop_dismiss | shop_id | 关闭唯一原生购买确认或金币不足提示；拒绝其他弹窗 |
| shop_receipt | transaction_id | 只读持久交易记录；不存在返回 null |
| close_shop | shop_id | 取消/完成准备交易后原生退出 |

`shop_id/item_id/stage/price` 全部从当前原生页面取得。分类切换和翻页有原生动画，返回后以 `shop_observe` 的可见条目为准；短暂空列表不表示商品全部售完。商品有 `price/currency/available/description/effect_type/effect_key/effect_amount/value_before`；售完条目的 stage 可能为 -1、效果字段为 null。金币为游戏 Coin，不是战斗阳光。

只有人类明确要求的商品/用途与总预算才能变成 `shop_budget`。能力开发、打开商店和“怎么购买”都不意味着授权花金币。参数示例中的 ID、价格和效果键必须替换为本次实际观察值：

```json
{"op":"shop_budget","budget_id":"human-budget-unique","max_total":500,"allowed_effect_keys":["实际effect_key"],"ttl_seconds":600}
{"op":"shop_prepare","shop_id":"当前商店ID","item_id":"当前商品ID","expected_stage":0,"expected_price":500,"max_spend":500,"transaction_id":"purchase-unique","budget_id":"human-budget-unique"}
{"op":"shop_confirm","shop_id":"当前商店ID","transaction_id":"purchase-unique"}
{"op":"shop_receipt","transaction_id":"purchase-unique"}
```

`purchase_prepared` 尚未花钱。0.4.9 起不能绑定唯一原生确认时，会关闭本次匹配的确认并取消预留，返回 `confirmation_not_ready` 及实际交易状态；剩余金币不足提示可 `shop_dismiss`，不能直接重复 prepare。确认时重新核对商品对象、阶段、价格、原有效果值、余额和预算有效期。

原生弹窗正常展示；商品范围和总预算已授权后，由 AI 连续执行 prepare → confirm，无须用户逐单手动点击。0.4.9 为这份原生弹窗替换其购买事件回调，再在持久 committing 之后调用原来绑定的原生 Sale 回调一次，所以 API 确认和玩家手动确认不会绕过记账。关闭事件仅取消未提交的预留，不能将 committed/unknown 改成 cancelled。此同步不接管玩家从其他入口独立发起的原生购买。

`user://AiBridge/purchases.json` 原子持久化预算和交易，确认前先刷盘 `committing`，之后记录 `committed` 或 `unknown`。同一预算的 prepared/committing/unknown/committed 累计占用额度；cancelled 释放占用。事务和预算 ID 不能复用，格式为 1–64 位 ASCII 字母/数字/横线/下划线。最多 256 交易与 128 预算，达到上限拒绝新建；不自动清理历史防重记录。

重连后查询旧收据，不重新买；重复 confirm 只返回原记录并标记 `replayed=true`。进程在提交期间退出后，旧 committing 读作 unknown，阻止进一步购买。损坏账本同样阻止购买，但目录仍可只读；不要删除账本或用新 ID 绕过未知交易。`Purchased/committed` 仅在金币扣减和原生升级/解锁效果同时核对成功时成立。此版本没有自动解除未知交易的接口。

## 无损观察与增量

`observe(view="compact")` 返回 `encoding=shared-json-v1`，state 是 `{encoding,root,definitions}`。重复子树使用 `$shared_ref`，原生同名键用 `$shared_literal` 转义。definitions 保存未编码原子树；先解码才能读取状态。压缩不保证每种场景都变小。

`observe_delta(base_snapshot_id=...,view=...,executable_cards_only=...)` 基于该连接最后一次显式 observe/observe_delta 的相同视图、战斗和场景。根响应 `snapshot_id` 是增量基线令牌，不是 state.map_preview 的分层关联 ID。换连接/视图/战斗/场景或传错基线会返回 `kind=full,reset=true`。动作回执中的 state 不会更新显式基线。

`kind=delta` 时先删除 `removed_fields`，再将 changes 的顶层字段整体替换；不能递归合并数组/对象。changes 中 null 是真实新值，不是删除指令。每次观察仍会刷新阳光 ID。Python 可直接使用：

```python
from pvz_ai.observation import apply_observation
response = client.request("observe", view="compact")
state, snapshot_id = apply_observation(response)
response = client.request("observe_delta", base_snapshot_id=snapshot_id, view="compact")
state, snapshot_id = apply_observation(response, state, snapshot_id)
```

## 特殊场景覆盖

`scripts/collect_scenario_coverage.py` 离线读取 Mod 完整回执、play_session 记录或 Studio 原生快照，可按输入顺序重建 compact/delta。不会连接游戏或发送动作：

```powershell
python scripts/collect_scenario_coverage.py artifacts/run/selection.json artifacts/run/battle.json --output artifacts/run/coverage.json
```

报告保存源文件绝对路径、SHA256、battle/observation ID，以及水面、屋顶 BRICK、POT/LILYPAD、底座有/无上层、冰道/坑洞/墓碑、选卡、实际可用重试菜单、植物/僵尸目标卡信息的 observed/not_observed。observed 仅证明快照中出现事实，不代表种植/铲除/特殊卡或重试流程成功。完整实战验证仍须保存原生动作回执与后继状态；不要为补覆盖修改存档或中断健康直播。
