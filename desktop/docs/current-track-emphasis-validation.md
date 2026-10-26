# 当前歌曲辨识度改进（2026-10-26）

## 范围与原因

代码检查发现当前曲仅以封面右下角的 18 DIP 圆形图标和半粗标题表示，扫视时不易区分；队列也只有小图标与字重变化。播放栏歌名没有单独字号或可见播放状态。实际旧客户端正在运行，但本轮 Windows 控制通道不可用，未声称完成旧 UI 的现场视觉检查。

- 列表：当前曲标题下显示强调色“正在播放 / 已暂停”图标文字标签，左侧增加细线；保留封面、加粗歌名及既有行高，选中/多选/悬浮/键盘焦点继续使用主题背景。
- 队列：复用状态标签，当前行增加淡强调色背景和侧边线；艺术家保留在第二行，移除和拖动行为不变。
- 播放栏：复用状态标签，歌名改为 16 DIP；列表及播放栏歌名支持完整标题提示。无曲目时不显示播放状态，暂停与续播复用实际业务状态。
- 中文及英文标签均有资源。没有调整业务播放、自动定位或队列规则。

## 已完成验证

`dotnet test desktop/tests/JeekMusic.App.Tests --filter 'FullyQualifiedName~PlayingAppearanceTests|FullyQualifiedName~PlayingVisibilityTests|FullyQualifiedName~LocalizationTests'`：22 项通过，0 失败。随后仅调整补充队列截图的捕获根控件，重跑 `PlayingAppearanceTests`，3 项通过。构建成功并同步到 `output/JeekMusic.exe`。

覆盖浅色/深色、当前曲选中/未选中与其他多选行并存、悬浮及键盘焦点、播放/暂停标签同步、队列切歌、播放栏曲名更新、清空队列后隐藏标签、行高/列宽稳定，以及原有虚拟化自动定位与 MCP 诊断/Schema 回归。这些控件测试使用内存曲库与 fake 音频，不能代替原生客户端验收。

已目检的 **Headless/Skia 补充渲染图（不是真实客户端截图）** 保存在忽略目录 `D:/MyOpenSource/JeekMusic/desktop/artifacts/current-track-emphasis/`：

- `light-playing-unselected.png`
- `dark-paused-selected.png`
- `queue-playing.png`、`queue-paused.png`
- 同目录另有深浅主题的播放/暂停、选择组合及屏外定位图。

真实进程与 MCP 验证：先使用旧客户端业务暂停保存进度，再结束旧发布版 PID 1724；启动 `D:/MyOpenSource/JeekMusic/output/JeekMusic.exe`，PID **26896**，调用 `show_window` 显示主窗口。新进程恢复同一歌曲 ID 1602、153 项队列及约 172.79 秒进度（误差小于 1 秒）；生产 MCP `resume` 成功，恢复重启前的播放状态，未调整音量或输出策略。`after-restart-diagnostics.json`、`after-restart-paused.json`、`after-restart-playing.json` 为本地证据，位于上述忽略目录。

## 原生 UI 验收阻断

computer-use 技能的 `@oai/sky` 查询应用、重试窗口查询，以及 `cua_repl` 的独立桌面发现均返回：

`Computer Use native pipe is unavailable: failed to connect native pipe: The system cannot find the file specified. (os error 2)`

因此本轮没有可交付的真实 Windows 客户端截图，未完成实际点击/键盘操作、多选与队列交互、播放/暂停/切歌时的原生视觉状态及 Mica/DPI 现场验收。上述生产 MCP 状态检查与补充渲染不能视为这些项目通过。需在主对话恢复 Windows 控制通道后补验；客户端已启动并保留运行。

## MCP 维护结论

这是纯 UI 呈现改进。继续读取现有 `get_status.track` / `playing`，使用既有播放工具与当前行可见性诊断；不新增工具、字段或参数，不改变描述或 Schema。`docs/mcp.md` 同步更新视觉状态说明。开发版与正式版共用相同组件及业务服务。
