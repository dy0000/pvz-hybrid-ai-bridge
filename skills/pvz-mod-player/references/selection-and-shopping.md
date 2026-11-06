# 选卡、状态与购物

## 选卡

deselect_card 取消未锁定卡；replace_card 先取消再选，replacement_content_id 取当前候选。selection_partial 按真实 chosen_cards 继续，不原样重放；暂停/模态未结束不换卡/开战。

card_info 每次查1–32个可玩/已解锁内容ID；base_cost 是配置价，live_slots.effective_cost 才是实时槽价，同内容槽分别保留。

## 状态与信息量

compact 用 python/pvz_ai/observation.py 解码。observe_delta 绑定同连接最后显式观察的视图/battle/场景；full/reset 换基线，delta 先删 removed_fields 再替换 changes 顶层字段，null 是真实值。

动作 state 不更新显式增量基线；sun/coin ID 用最新观察。executable_cards_only 省略不可用卡，不等于阵容缺卡；现场够用即行动。

## 商店

open_shop 从主菜单/就绪选卡页进入自然解锁商店。shop_observe 读金币、商品、stage/price/effect_key；shop_navigate 后有界等待同商店/分类/页加载，不把短暂空 items 当售罄、不重发导航。花 Coin，不花阳光。

在已有的人类商品范围、总预算、触发/间隔/件数内：

1. 每份授权只建一次 shop_budget(budget_id,max_total,allowed_effect_keys)，后续复用、累计扣已花费/保留额；不因换商品、重连或到期重授总额。
2. shop_prepare 绑定该预算、实际 shop_id/item_id、expected_stage/expected_price、max_spend 和新 transaction_id。
3. 原生确认窗正常显示，AI shop_confirm 一次；确认精确扣币及实际所得才报成功，不买 shop_cancel。

授权边界见主文；仅问方法不授权花费。

prepared 占预算、cancelled 释放；committed/unknown 不复用ID。超时查 shop_receipt，不换ID/预算或删账本绕过。confirmation_not_ready 查是否已取消，再 shop_dismiss 残留提示，不叠 prepare。
