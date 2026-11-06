# 0.4.10 原生掉落卡契约

适配原生游戏 0.31.0.0，DLL SHA256 `820E9E8F91F58062FCFC7548BDB9E5588EC8F822F5DDC71F4B4D83B09A912D95`。本轮未连接游戏 TCP，未停止 Runner、未部署或实机种植。历史诊断样本来自开发环境（不随仓库分发），其中 Adventure:2:4 的普通正 ID 均不可用；历史 ID 不能拿到当前关卡重放。

## 原生来源

`TowerDefenseBattleFeatureRainMode.SpawnPacket` 使用 `TowerDefenseManager.SpawnPacket`。`SpawnPacketCore` 创建独立 `TowerDefenseInGamePacketShow`，直接加入当前 `GetCharacterNode()`，设置 plantOnce=true，按关卡设置 useCost，再由 `BindSpawnedPacketPicker` 绑定原生 `PacketPickControl.PickPacket`。免费卡不设置 start=true，而通用 `CanUseFromMod` 要求 start=true，故原有普通卡服务会返回不可用。

桥接只枚举当前 TowerDefense 地图角色容器的直接卡片子节点，要求原生有效、在树中、未排队删除、有效 config、!onlyDraw、plantOnce 和本地阳光账户。不会从整棵 UI 树按名字猜卡，也不会将牌库、图鉴或商店预览当成掉落。此来源包含原生卡片雨和同一 SpawnPacket 链创建的弹出/掉落卡；source 并不声称该卡当前仍在物理下落。

证据源码：`research/TowerDefenseManager.cs` 的 SpawnPacketCore/BindSpawnedPacketPicker；`research/TowerDefenseInGamePacketShow.cs` 的 CanUseFromMod/Pressed/CompleteUse/PacketPickRelease 关联；`research/falling-cards-0.31.0/TowerDefenseBattleFeatureRainMode.decompiled.cs`。反编译仅为只读诊断证据，不属于 Mod 源码或游戏修改。

## 观察字段

能力：`capabilities.features.falling_cards=true`、`one_use_card_identity=true`、`card_selection_release_receipt=true`。

同一 observe 的 `state.cards[]` 保留普通 native 和 free_conveyor 兼容视图，并新增：

```json
{
  "id": -37,
  "content_id": "PlantPeaLilyPad",
  "source": "falling_card",
  "native_object_id": "原生实例ID字符串",
  "native_generation": 3,
  "cost": 0,
  "cost_status": "known",
  "use_cost": false,
  "single_use": true,
  "available": true,
  "selected": false,
  "remaining_lifetime_seconds": 19.5,
  "pending_use": false,
  "id_lifetime": "battle_native_instance_generation_until_consumption_expiry_or_removal"
}
```

示例仅说明字段，不是实机样本。card_id 为不透明的有符号整数；负 ID 同时用于 free_conveyor 和 falling_card，必须看 source。桥接 ID 按 battle、native instance、原生 `_asyncLifetimeVersion`、config instance、ModUseSequence 和 source 绑定，普通观察保留活卡 ID；Init/Clear/重新入树、配置替换、消费、离场/过期或换 battle 会退役旧 ID，数值不复用。同内容的不同原生实例始终各自有 ID，不能合并。

免费卡实际 cost=0。付费卡通过原生 UpdateItemCostLabel 的有效报价读取，包含原生费用变更/Mod 报价；无法取得有效报价则 cost=null、cost_status=unknown、available=false，绝不以配置价格或 0 冒充。card_info.live_slots.effective_cost 和 placement_check.cost 保持相同语义。remaining_lifetime_seconds=null 表示原生无期限；被原生选中时，剩余寿命为 0 也可能仍可用，这是原生持卡规则，Mod 不延长计时。

available 还要求原生 alive、未锁、未冷却、无 pending、可支付、可按压及非整列模式。存在独立的外来原生卡选择时，操作拒绝接管该选择；不要通过尝试其他卡绕过。

`state.executable_card_ids` 从本次 available=true 条目派生，`executable_cards_only=true` 可显式过滤不可用条目。原生 native 服务可能同时暴露同一付费一次性卡的正 ID 别名，尚未提供公开的跨命名空间映射；保持原样，不按名称/费用猜对应或删除普通卡。掉落卡操作使用明确 source=falling_card 的精确 ID，不能把同名 native 和桥接条目当作两张独立库存。一次消费后两种入口都受原生失效检查约束，但调用者仍不得换别名重试未知动作。

## 操作、消费和释放

仍调用 `plant(battle_id,card_id,column,row)`，坐标为 1 基。无需新增桌面拾卡步骤或独立选择请求。桥接一次绑定精确原生卡，按原生 `Pressed → PickPacket → ProcessPacketPickCore(modConfirm=true) → Plant/CompleteUse` 执行；普通地形、承载、升级、格子目标、费用、冷却和消费规则不变。不写 start、useCost、alive、冷却、阳光或存档字段。

进入操作前拒绝别的原生卡选择。finally 只释放本次选中的卡，调用 PacketPickRelease，再验证 picker 没有持卡且本次卡不再 selected。没有确认释放则返回 unknown，不能宣称动作成功。

成功回执必须含：`ok=true,outcome=Committed,source=falling_card,card_id,native_object_id,native_generation,native_commit_confirmed=true,selection_released=true,native_use_sequence_before,native_use_sequence_after`。after 必须大于 before；这与原生 TryUseModCard 的 Committed 判定一致。使用后附带正常新 state，已消费旧 ID 不再可执行。

同一份实际响应中的八项证明字段（source、card_id、native_object_id、native_generation、native_commit_confirmed、selection_released、native_use_sequence_before/after）会按白名单原样复制到 `stream_status.status.receipt_history[]` 和 `plant_committed` 事件的 `data`。原子 status_file 使用相同 status 对象，也保留这些字段；事件外层 source 仍为 game_observation，卡片来源在 data.source。缺失字段不从请求或 Committed 推断，false/null 也原样保留；不转发整份 state 或凭据。接收方只在 GameRecovery 解释证明，不需要各层重复核验。

这些是有界流/状态文件证据，不是跨进程的永久动作账本：receipt_history 仍保留最近 12 项、事件最近 256 项，实例/游标域和旧证据淘汰规则不变。直接响应丢失时可用匹配 request_id/op/battle 域的完整证明恢复；若证据缺失或被淘汰，继续保留 unknown，不能只凭 Committed 解锁或重放。

`state.card_selection={status:"clear"|"selected"|"unknown",native_object_id:string|null}` 提供本次观察的原生卡片选择状态，不代表炮/铲子等所有工具状态。它和 selection_released 是维护边界的卡片选择证据，不能代替唯一控制器/在途动作核对。

确定没有消费且已释放才返回 rejected。消费结果不可读取、存在 pending、调用异常或未确认释放均为 unknown_observe_before_retry；先观察，不重放原请求或改别名重放。整列模式当前不可用；`use_target` 的负 ID 不在本次扩展范围，普通 plant 可走原生格子目标逻辑。本地种植计划仍禁用，本次未扩展其调度。

## 接收方验证

由已有唯一 Runner 取得新进程 capabilities 和本关 observe，保留原始请求/响应：先核对 source/ID/费用/生命周期；模型选择一张真实可用掉落卡和原生合法格子，串行调用一次 plant；核对上述消费及释放回执、对应植物/原生效果、消费旧 ID 已退役及新 state.card_selection.status=clear。不同实体同内容应有不同 ID；不制造卡片、改进度或重复破坏性种植来补测试。

离线身份/可用性测试与原生裁剪兼容性检查只证明对应代码边界，不能替代游戏加载、实际费用扣减、实际掉落种植、移动端布局或通关证据。部署由接收方协调安全边界，具体版本、包哈希、测试结果和状态见 `artifacts/falling-card-handoff-2026-10-28.md`。
