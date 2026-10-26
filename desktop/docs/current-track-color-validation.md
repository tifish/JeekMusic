# 播放色与选中色分离（2026-10-26）

在 `0f74a86e09e2fc6ac06581afeca4c0e04d928295` 基础上修正用户反馈：此前播放底色与普通选中都来自 `SystemAccentColor`，同为蓝色，只改变透明度不够清楚。

## 当前方案

播放状态使用专用 `CurrentTrackBackgroundBrush`、`CurrentTrackHoveredBackgroundBrush`、`CurrentTrackBorderBrush` 和 `CurrentTrackMarkerBrush`。全局强调色及其他控件的颜色不变；普通选中仍跟随系统主题强调色，目前为蓝色。

| 主题 | 播放整行底色 | 悬浮底色 | 播放边框/侧线 | 状态标签底色 |
| --- | --- | --- | --- | --- |
| 浅色 | `#FFF1D6` 浅金 | `#FFE6B5` | `#A5650D` | `#805000` |
| 深色 | `#3F321E` 暗棕金 | `#514027` | `#DBA650` | `#95600D` |

当前曲同时选中时继续使用原主题选中底色/透明度，叠加 3 DIP 金色边框与琥珀标签。普通当前曲边框仍为 1 DIP。跨列高亮、暂停身份、切歌清除、选择/多选/悬浮/键盘焦点和行高/列宽规则保持。队列底色及侧线、列表侧线、播放栏状态标签同步采用播放色系。

## 验证

`dotnet test desktop/tests/JeekMusic.App.Tests --filter 'FullyQualifiedName~PlayingAppearanceTests|FullyQualifiedName~PlayingVisibilityTests'`：**22 项通过，0 失败**，构建成功。

覆盖浅/深色主题中当前曲与其他普通选中行并列、当前曲本身选中、播放/暂停、悬浮、焦点、跨列像素一致、隐藏标题列、切歌/清空/虚拟化旧行清除。队列测试扩展为浅/深色两种，确认使用相同播放底色。暖色断言与默认蓝色选择断言保证不是同色透明度变化；正文对比度大于 4.5:1，标签白字对比度为浅色约 6.85:1、深色约 5.31:1。

已目检以下 **Headless/Skia 补充渲染，不是真实客户端截图**。前四张均在同一画面中显示当前曲及其他普通选中行：

- `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-amber/light-playing-unselected.png`
- `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-amber/dark-playing-unselected.png`
- `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-amber/light-playing-selected.png`
- `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-amber/dark-playing-selected.png`
- 同目录 `light-queue-playing.png`、`dark-queue-playing.png` 及暂停/隐藏标题列等组合。

## 重启及原生验收阻断

本轮初始化并尝试 `@oai/sky` 应用查询、`cua_repl` 桌面发现；新版本启动后再次执行 `sky.list_windows()`，仍全部返回：

`Computer Use native pipe is unavailable: failed to connect native pipe: The system cannot find the file specified. (os error 2)`

因此没有真实 Windows 客户端截图，未完成原生鼠标/键盘、Mica/DPI 下的视觉验收，不能用补充渲染或 MCP 读取代替。需通道恢复后补验。

旧 PID 20528 已结束。新构建 `D:/MyOpenSource/JeekMusic/output/JeekMusic.exe` **PID 22988** 已启动并调用 `show_window`，保留运行。生产 MCP 确认歌曲 ID 465、1602 项队列及约 5.74 秒进度恢复（误差小于 1 秒），保持重启前暂停状态，`lastPlaybackError=null`，未调整音量或输出策略。证据为同一忽略目录内 `after-restart-paused.json` 和 `after-restart-diagnostics.json`。

纯视觉修改，无 MCP 工具/字段/Schema/描述变更；`docs/mcp.md` 已同步最终配色及选中叠加规则，开发版与正式版共用实现。
