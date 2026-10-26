# 当前歌曲整行高亮修正（2026-10-26）

在 `2e9272babfc76cbab97c7cef0ed74c3fe36c15c1` 的基础上，按用户要求将局部标记升级为跨所有列的整行高亮。此前播放栏改进保留。

## 最终视觉规则

- 当前曲未选中：在 DataGrid 已有整行背景层绘制 18% 强调色底色和 1 DIP 强调色边框；悬浮时底色增至 26%。浅色/深色主题均使用动态主题资源。
- 普通选中/多选：沿用 DataGrid 的选中色、悬浮变化及焦点状态。
- 当前曲同时选中：保留主题选中底色及其透明度，叠加 2 DIP 主题前景色整行边框；状态标签和标题加粗继续存在。单元格键盘焦点保持独立。
- 暂停：保留当前歌曲整行身份，标签变为“已暂停”；切歌、清空队列或虚拟化行复用时，旧行恢复普通状态。
- 高亮不依赖标题列，隐藏标题列后仍覆盖可见各列；没有改变行高、列宽、选择集合或自动定位规则。
- 队列当前行改用同一 `CurrentTrackBackgroundBrush`，保留侧线和状态标签。

实现仅调整 `UI/Styles.axaml` 与 `UI/QueueFlyout.cs`，复用已有 `current` 行类及业务事件，无需改写 DataGrid 模板或播放服务。样式依据当前 NuGet 12.1.2 的[上游 Fluent 模板](https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid/blob/1e12211eb0c52a09e314ca57a344147c37d0a26c/src/Avalonia.Controls.DataGrid/Themes/Fluent.xaml)，限定到主歌曲表格。

## 验证结果

`dotnet test desktop/tests/JeekMusic.App.Tests --filter 'FullyQualifiedName~PlayingAppearanceTests|FullyQualifiedName~PlayingVisibilityTests'`：**21 项通过，0 失败**，构建成功。

补充验证覆盖浅/深色、播放/暂停、当前曲选中/未选中、多选与悬浮、原单元格键盘焦点、切歌、清空队列、旧行虚拟化复用、隐藏标题列和原有自动定位。除检查背景层宽度外，还从实际 Skia 渲染的多个非标题列取样，确认整行底色一致且区别于普通行。选中时验证仍使用原主题选中底色。布局断言确认行高和列宽未改变。

已目检 **Headless/Skia 补充渲染（不是真实客户端截图）**：

- `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-full-row/light-playing-unselected.png`
- `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-full-row/light-playing-selected.png`
- `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-full-row/dark-playing-unselected.png`
- `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-full-row/dark-paused-selected.png`
- `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-full-row/dark-title-hidden.png`
- 同目录 `queue-playing.png` 及其他播放/暂停组合图。

## 客户端重启与真实 UI 阻断

本轮重新初始化并调用 computer-use 技能的 `@oai/sky` 应用查询，同时复查 `cua_repl` 桌面发现；新客户端启动后再调用 `sky.list_windows()`。三次均返回：

`Computer Use native pipe is unavailable: failed to connect native pipe: The system cannot find the file specified. (os error 2)`

因此本轮**没有真实 Windows 客户端截图，也未完成原生鼠标/键盘及 Mica/DPI 视觉验收**。上述补充渲染和业务状态验证不代替真实 UI 验收。需主对话在通道恢复后补验整行高亮、多选叠加及切歌视觉状态。

旧日常客户端 PID 26896 已通过业务暂停保存进度后结束。新构建 `D:/MyOpenSource/JeekMusic/output/JeekMusic.exe` 已启动，PID **20528**，生产 MCP 确认进程路径、同一歌曲 ID 1261、153 项队列及进度恢复（误差小于 1 秒）；恢复原来的播放状态，`engineReady=true`、`lastPlaybackError=null`。已调用 `show_window`，保留运行，未调整音量或输出策略。本地证据为同一忽略目录下 `after-restart-paused.json` 和 `after-restart-diagnostics.json`。

## MCP

纯视觉改进，无新接口、字段、描述或 Schema 变更。继续使用 `get_status.track` / `playing` 与已有可见性诊断，开发版和正式版共用实现。`docs/mcp.md` 已更新为最终整行及选中叠加规则。
