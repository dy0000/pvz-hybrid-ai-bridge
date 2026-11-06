# 每日挑战日历导航（0.5.16）

每日挑战原生支持历史日期。此前接口只能打开日历当前选中日期的关卡，缺少翻月和选日期操作；本次通过现有 menu/menu_action 补齐，无需更改系统日期。

## 接口

`capabilities.features.daily_calendar_navigation=true`。日历菜单的 `phase=daily_calendar`，包含：

- `date`：原生当前选择的日期。
- `calendar_year/calendar_month`：当前显示月份。
- `calendar_dates`：原生日期按钮对应的 `date/available/selected/completed`。
- `daily_prev_month/daily_next_month`：调用原生日历翻月回调，保留原生月份数据下载和刷新。
- `daily_date:YYYY-MM-DD`：选择当前月份原生已开放的日期，更新原生日期按钮及选择回调。
- `daily_levels`：原生开始按钮可用时，列出所选日期的关卡。

每次操作后读取新菜单。日期按钮重建后，旧按钮及旧菜单不能继续使用；翻月按钮也绑定观察时的月份。禁止选择原生禁用日期，不直接修改 year/month/day、月数据或存档。`daily_levels` 绑定观察时的所选日期，避免玩家切换日期后误开另一日目录。实际 `start_daily` 仍遵守原有 flow 限额。

## 验证及安装

2026-11-06 完成：

- Release 构建：0 警告、0 错误；裁剪后的实际游戏运行库引用解析通过。
- Python 玩法接口定向测试：4/4 通过。
- 实机加载 0.5.16，确认日历能力标志；2026-11 → 2026-10 → 2026-11 翻月成功。
- 选择 2026-11-01，确认选中状态并列出该日 2 关挑战。
- 日期可用性与菜单选择动作逐项对应，旧菜单请求被拒绝。
- 恢复原选中日期、返回主菜单并交还控制；未开战、未购买或领取奖励。

证据在 `artifacts/daily-calendar-20261106-145135/`，包括 `live-result.json`、`live-trace.json` 和本次验收脚本。升级前游戏在主菜单且没有 AI 控制连接；正常关闭、完整备份存档后安装，再正常启动加载。存档备份及逐文件哈希核验结果为该目录的 `save-before-upgrade.zip`、`save-backup.json`。

本次验证覆盖日历导航和历史关卡目录，不代表已验证这些关卡的战斗通关。
