# 传送带与掉落卡

按 cards 的 source、精确 signed card_id、实际 cost 和 available 操作。free_conveyor 是免费带卡，falling_card 是独立地面卡，column_card 是普通整列卡；负号不代表来源，同名实体不合并。

掉落卡有 native_object_id、native_generation、single_use、use_cost、剩余寿命和 pending_use。消费、重建、过期、离场或换 battle 后 ID 失效；正常刷新保留仍存活的 ID。报价未知为 null 且不可用，不能当零费。

plant 在一次操作内完成原生选卡、放置、消费及释放，无需另发拾卡。整列模式一律用 plant，由原生选择整列目标并统一结算；不逐行重放，也不调用 use_target。非整列模式的实体目标才用 use_target。

一次性卡确认需要 ok=true、Committed、native_commit_confirmed=true、消费序号 after>before 及 selection_released=true。unknown 或未确认释放先核对，不换别名重试。同一原生卡的多个显示条目不能当作独立库存。

按实际掉落和来敌及时使用，不等待固定常规阵容；保留承载、升级、费用与冷却规则。
