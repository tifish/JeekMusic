# 手机后续队列跟随可见歌曲列表（2026-10-26）

## 行为

- 原因：此前仅 `PLAY_SOURCE` 设置来源，显式切换标签或详情页不通知播放服务；原队列继续观察旧艺术家来源。
- 显式切换歌曲视图由 `MainViewModel` 发送应用内 `FOLLOW_SOURCE`，统一用于文件夹、艺术家（含固定标签）、专辑、保存列表和搜索。文件夹/艺术家/播放列表的索引页没有歌曲，不改来源。启动恢复、预组合的隐藏页、行选择、设置和普通重组不产生切源意图。
- 服务立即撤掉旧来源的后续条目，保留当前 MediaItem、位置、暂停/播放意图和手动待播。新来源由现有 `QueueSourceRepository` 异步提供筛选/排序后的列表；当前曲存在则从新位置接续，不存在则从新列表首曲/新随机顺序接续。重复列表条目保留，手动待播优先。
- 新列表未到达时显式 Next/Previous 等待同一代次；快速切换及重新 Play 使旧读取失效，暂停/停止/显式 seek 撤销待决跳曲。自然 EOF 先到时，在新列表就绪后经原会话导航/播放许可路径接续，不在音频回调阻塞查库。
- 不属于新来源的旧当前曲仅暂存到离开它时，然后退出队列；新列表的前向/循环不再选择它。空新列表没有待播目标时，Next 不重播旧曲。切源待决状态和旧当前曲标记随来源保存，恢复缺失音频时不误排除新源首曲。
- 原有同源内容刷新保持既有算法。新增命令只开放给本 App；Windows 已有对应行为，本次未改 Windows MCP 接口。

## 验证

- JVM：95 项通过，0 失败/跳过。包括新增跨源顺序/随机/重复条目计划，以及待决导航取消、旧当前曲退出后暂停 Next 的播放请求有效性。
- Android 设备测试 7 项通过，使用 API35/x86_64、真实 ExoPlayer 与 Android 主 Looper；合成媒体/静音，未用用户音频。覆盖当前条目及 24.340 秒位置保留、手动待播、随机新源、重复条目、九次循环、空结果、A→B→C 晚到、显式 Play 覆盖、取消待决 Next、待决恢复、真实短音频自然 EOF 后接续请求和旧当前曲文件缺失。
- `testDebugUnitTest`、Debug/AndroidTest APK、签名 Release/R8、完整 lint 构建通过；最终设备测试和发布结果在本地证据目录记录。
- 真实 Release UI 先在旧 1.0.179 复现：Source A 随机播放，切到已显示 B1/B2 的文件夹 B，Next 仍播放 A2。候选同路径验证后，切换时当前 A1 继续；Next 播放 B2，随后 B1/B2 循环不回 A。暂停后切源仍保持 A1 暂停，Next 才播放 B。搜索 `Source A` 后通过 Android 标准媒体 Next，实际播放搜索结果 A1。
- UI 操作和截图均使用既有 JeekLanSync35 / emulator-5582，生产包 `com.tinyfish.jeekmusic`；未触碰用户手机、Windows 客户端、真实曲库或恢复备份。新增四首 Source A/B 静音 FLAC 仅在既有模拟器合成测试根内。测试输入法子类型已恢复。

本地证据：`output/mobile-source-follow-20261026/` 中的 JVM/构建日志、`device-tests-final.log`、`01-old-folder-next-still-artist.png`、`02-fixed-switch-current-still-artist.png`、`03-fixed-folder-next.png`、`04-fixed-wrap-stays-folder.png`、`05-paused-switch-keeps-current.png`、`06-paused-next-starts-folder.png`、`07-search-system-next.png` 及对应 UI XML。原生仅此模拟器；ARM/OEM、蓝牙硬件及所有异步交错未宣称穷举。
