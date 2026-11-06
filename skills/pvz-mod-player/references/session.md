# 会话与关卡流程

## 连接

工作室复用 Runner，旁观只读状态文件、不另连 TCP。独立游玩保持一个会话到收尾；MCP 别名 attach → pvz_take_control、detach → pvz_release_control，其余查 schema。

终端入口 项目内的 `scripts/play_session.py`：同一进程 stdin 逐行 JSON，收全回执再继续。保留子进程 APPDATA，每次启动用独立 artifact 目录，不输出 connection.json/令牌。

先读 capabilities/observe/menu；已有战斗续接，已有 flow 读 flow_status，不覆盖预算。确认无人控制才 attach(realtime,background=true)。游戏未运行且任务含游玩时，用 scripts/launch_sandbox.ps1 启动隔离副本。

## 模式与玩法

模式/开放章节按 menu 的动作和索引选择，不拼固定按钮坐标。

普通布阵用 plant/shovel/fire；传送带/卡片雨见 [掉落卡](falling-cards.md)，砸罐/三消等见 [特殊玩法](gameplay.md)，玉米大炮见 [复杂操作](advanced-and-recovery.md#玉米大炮与多格升级)。

## 范围

level:*、start_daily、start_trial、next_level、retry_level 受 flow 限额约束。flow_start 不自动开局，flow_stop 不暂停当前战斗。

- 试玩一关：max_levels=1、max_retries=0。
- 打通一关：max_levels=1，有限重试；多关按用户范围。

已有 battle 占第1关，不反复 stop/start 扩大授权。

## 阶段推进

| 状态 | 操作 |
|---|---|
| loading/动画 | 等实际状态变化，有界查询，不重复请求 |
| enter_game | 执行后读后继菜单 |
| dialogue | 有 continue_dialogue 才继续 |
| tutorial | 按当前条件正常种植、铲除、收集，不跳过步骤 |
| selecting_cards | 读候选与地形，choose/deselect/replace；等真实 chosen_cards |
| viewing_map | 镜头就绪后 continue_preview，返回选卡 |
| 阵容就绪 | start_battle，确认 running |
| playing | 开拾取并立即布防 |
| paused/modal_pause | 用 menu 的 resume；玩家锁不接管 |
| lost | 在授权内 retry_level 或 finish_level |
| victory_reward_pending | collect_victory_reward |
| AwardSettlement | finish_level，或有续关授权时 next_level |
| 未知弹窗 | 报告类型，不猜按钮 |

菜单动作一次、观察后继一次；再操作取新 menu。transition_unknown/长期无进展查回执、错误和阶段，不因超时重发。

## 结果收尾

领奖一次后等动画：钱袋可直接回 LevelChoose，新植物可进 AwardSettlement，按阶段表收尾。返回约定菜单后 detach，确认交还/无意外暂停；不在领奖动画中断连，避免恢复后台配置触发失焦暂停。

无僵尸/wave.final/结算页不替完整结果链；回选关后不因 level_completed=false 重打。保留关卡、实际结果、领奖返回与交还证据。

## 终端状态

play_session 解码 compact/delta 后输出 terminal_summary；cards 为 `[id,content_id,cost,cooldown_remaining,available,source]`。mechanics、可操作实体ID和省略字段分别保留，省略不等于空场；细节读同会话原 record，不另连补观察。ready=true 仅命令进程就绪。
