---
name: pvz-mod-player
description: 通过本机植物大战僵尸杂交重制版 AI Bridge Mod 实时游玩、挑战关、选卡布阵、特殊玩法、领奖续关及授权购物。
---

# 杂交重制版游玩

依据最新原生现场决策；危险时先行动、后简短解说。只问功能不接管、不启停游戏。

## 先判运行方式

- **工作室运行中**：复用唯一 Runner，只通过既有管理接口修改战术或读取状态；不另起 MCP/PvzClient、种植脚本或控制连接，不重启健康 Runner。
- **独立游玩且无人控制**：保持一个 pvz_* MCP/PvzClient 会话，先读 capabilities，再 attach(realtime,background=true)。先核已有 battle/flow，接续而不覆盖预算；完成后确认交还。
- 项目以本仓库根目录为准，副本 `game-sandbox`，存档路径取本机 `sandbox.local.json`。MCP 服务名 `pvz_hybrid`，工具前缀 `pvz_`；连接参数取当前 capabilities/配置，参数定义见 [API](../../API.md)。

## 不变边界

保持 `local_decisions_enabled=false`、`in_game_panel_enabled=false`，stream_update 用 `visible=false`。不用 computer-use、OCR、桌面输入、改档或第二控制器；不暂停/step 等思考，不改速度或解除玩家锁。用户停止、F8/F9 优先；技能不扩展工作室动作白名单或直播授权。

行列从1开始，菜单索引原样用。content_id 是种类、card_id 是卡槽、entity_id 是实体，不能互换；负 card_id 有效，同名槽不合并。battle/card/entity ID 不跨战斗，sun/coin ID 用最新观察。

Committed 才确认种植；受理、动画、技能触发不等于效果。unknown/超时/断线先查原回执与现场，不重放；确定拒绝按原因、条件变化再决策。保费用、冷却、地形、升级和真实胜败规则。

## 战术与维护

用户最新明确要求优先，总纲定阶段，同关专项只覆盖明确差异，地形/敌种预案按条件启用。技能战术补空缺，不把历史卡组、旧坐标或胜败当固定打法。接入其他工作室时沿用其唯一 Runner 和有效管理员规则。

选卡和战斗读 [战术](references/tactics.md)，其他机制按需查下面的参考。技能文件随项目提供，无需覆盖全局技能。离线更新不代表当前会话已重新读取，不为纯战术更新中断健康战斗。

## 游玩闭环

1. 独立游玩新开关用 flow_start 限关数/重试/时间，试玩默认一次；已有战斗占本次第1关，不反复 stop/start 扩范围。
2. 按新 menu_id/action_id 进入开放关卡，核真实 chosen_cards；start_battle 后确认 running。教学按原生条件完成，不跳过。
3. 新 battle 开 auto_collect，金币按需拾取。普通落点直接 plant，回包够用即继续；歧义/拒绝才诊断。承载先 Committed、下轮叠上层，不预支费用/冷却。
4. 胜后领奖并确认返回；续关/重试遵授权，独立会话完成后 detach，确认交还与无意外暂停。未完成记中断，不冒称胜利。

## 购物与竞猜

购物须已有商品范围、总金币预算；竞猜另需明确授权。授权持续有效，按触发/间隔/件数执行，AI prepare → confirm，原生确认窗正常显示，无需逐单询问。余额为上限时取购买前原生余额，不另加比例/金额限制；预算不因重连重授，未知查收据。详见对应参考。

## 按需参考

- 连接、模式、教学、领奖：[会话流程](references/session.md)。
- 选卡与即时决策：[战术](references/tactics.md)。
- 多格升级、玉米大炮、铲子、恢复：[复杂操作](references/advanced-and-recovery.md)。
- 换卡、信息压缩、购物：[选卡与购物](references/selection-and-shopping.md)。
- 传送带、卡片雨、独立卡：[掉落卡](references/falling-cards.md)。
- 砸罐、三消、手套、技能、竞猜等：[特殊玩法](references/gameplay.md)。
- 队列、事件、解说与视口：[直播](references/live.md)。

只读当前需要的参考；结果按真实回执报告。
