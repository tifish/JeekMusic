# 当前歌曲标签移除与空闲输出格式显示验证

日期：2026-10-26；基于 `b715147b13e3e3a4a76dd5d6b716dabd0a7e0d31`。

## 可见变化

- 主列表、队列、底部播放栏移除占据歌名布局的播放/暂停文字标签及装饰图示，删除 `CurrentTrackMarker` 与未引用的专用画刷/样式，取消额外行或固定标签高度。列表标题居中，队列只有标题/艺术家两行；保留完整标题 tooltip、底栏 16 DIP 歌名和真正的播放/暂停操作按钮。
- 琥珀跨列整行底色/边框、蓝色普通选择、多选/悬浮/焦点保持；当前曲同时选中仍以蓝底及琥珀边框区分。状态通过 AutomationProperties.HelpText 提供，播放/暂停、切歌与清空时更新。
- 显式暂停由既有 `ReleasePlaybackOutput("pause")` 释放流并恢复写入前快照。UI 现在按 `OutputPending` / `IsIdle` 区分重建、空闲和已加载输出，不以 `!IsPlaying` 猜测。空闲摘要显示源率及“输出空闲”，取消源→输出箭头；tooltip/格式菜单明确列出设备默认格式，解释没有活动音频输出。播放时的实际转换和真实恢复失败/安全警告继续显示。状态事件同步刷新摘要和提示。没有修改音频策略或设备恢复逻辑。

## MCP

`get_output` / `get_diagnostics` 共用的 audioFormat、sampleRate 增加 `outputState`（idle/pending/active）。空闲时 actualDevice/actualRate 为 null、applied/resampled 为 false，设备真实静态配置保留在 windowsDeviceDefault/windowsMix。active 指已加载流，不保证正在播放。工具描述及 mcp.md 同步；无新增控制工具，输入 Schema 与工具数不变，开发版和正式版共用实现。旧调用方读取空闲设备配置须使用 windowsDeviceDefault，不能将它当作播放输出。

## 针对性自动验证

- 标签与列表行为回归：PlayingAppearanceTests、PlayingVisibilityTests、LocalizationTests，23 通过。
- 最终格式/布局回归：DeviceSampleRateTests、TemporaryDeviceFormatTests、LocalizationTests、PlayingAppearanceTests（排除 RealEndpoint），48 通过、0 失败。两次集合有重叠，不累计为独立测试数。
- 覆盖浅深主题、长标题、当前曲与其他普通选中行同屏、当前曲被选中、播放/暂停标签不占布局、真实操作按钮保留、队列、虚拟化/切歌旧高亮清除；共享/独占暂停恢复 48 kHz 默认后无误导箭头、续播重新匹配 44.1 kHz、切歌 96 kHz、实际不匹配、恢复失败警告和仍持有输出流的内核暂停。
- 最终 `dotnet build desktop/src/JeekMusic.App --no-restore --verbosity minimal` 成功，0 警告/0 错误，并已 staging 到 output。

## 补充渲染（不是原生客户端验收）

以下真实 Avalonia 控件的 Headless 渲染已目检，绝不能替代原生 UI 验收。音频测试宿主未加载图标字体，部分操作图标显示方框；其截图只用于格式文本及布局补充，按钮保留另有布局/命令断言。

目录：`D:\MyOpenSource\JeekMusic\desktop\artifacts\current-track-no-label\`

- `light-playing-unselected.png`：长歌名琥珀当前行与蓝色普通多选同屏，无状态标签。
- `dark-paused-selected.png`：长歌名当前曲被选中，蓝底/金边，无标签留下的空位。
- `light-queue-playing.png`、`dark-queue-playing.png`：队列标题/艺术家布局及琥珀当前行。
- `shared-playing-mismatch.png`：44.1→48 kHz 实际播放不匹配继续显示。
- `shared-output-idle.png`、`exclusive-output-idle.png`：44.1 kHz 源在输出空闲时没有转换箭头。
- `shared-resumed-matched.png`：恢复后 44.1→44.1 kHz。

## 新版实际进程与 MCP 回归

旧客户端 PID 18256 已在捕获状态、通过业务 pause 释放输出后结束。新 `D:\MyOpenSource\JeekMusic\output\JeekMusic.exe` PID **19976** 已启动并留运行；未 push。

真实生产 MCP 检查当前歌曲 ID 1602、队列 1602 首。续播实际输出匹配 44.1 kHz，随后 pause 恢复原 48 kHz 设备默认/混音格式，restoreStatus=restored；空闲 actualDevice/actualRate=null、resampled=false、hasWarning=false，摘要无转换箭头。最终恢复原暂停状态及 **9.781311 秒**位置，设备选择、共享自动匹配策略、音量未修改。证据同目录 `runtime-resumed-output.json`、`final-status.json`、`final-output.json`、`final-diagnostics.json`；原状态在 `paused-before-restart.json`。

## 真实客户端 UI 阻断

本轮使用 computer-use 技能，启动新版本后再次调用 `sky.list_windows()`，仍返回：

```text
Computer Use native pipe is unavailable: failed to connect native pipe: The system cannot find the file specified. (os error 2)
```

因此没有本轮真实客户端截图，鼠标/键盘操作、真实字体/DPI 下长标题与浅深主题视觉验收、原生格式 tooltip/菜单交互仍未验收。MCP 实际进程验证和补充渲染均不冒充原生验收。
