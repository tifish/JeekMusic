# 移动端验证记录

## 2026-10-24：显式上一首 / 下一首后直接播放

基于 `58214b87369a3f7e0a4fead74b47845fc5643961`，对应需求 P-1、P-11。

### 实现与入口核查

- 播放页、迷你播放条均使用 `PlayerViewModel` → `PlayerConnection` → MediaController；上一首调用 `seekToPrevious()`，下一首调用 `seekToNextMediaItem()`。
- `PlaybackService` 只给 MediaLibrarySession 包装 `NextAlwaysPlayer`。四个标准跳曲入口（含系统使用的 `seekToNext()` / `seekToPreviousMediaItem()`）共享成功导航后播放的行为。
- 上一首阈值仍由 Media3 执行；下一首末尾回第一首、单曲回开头规则保留。目标按歌曲 ID、播放配置及 CUE 区间确认，允许同步封面元数据刷新。
- 暂停后的播放请求经过本会话的 MediaController，沿用 Media3 1.11.1 的 `onPlayRequested` 平台检查，尤其是 Android 12/12L 的前台服务拒绝路径；没有直接播放的后备路径。音频焦点和耳机拔出仍使用原 `PlayerFactory` 的 ExoPlayer 配置。
- 自动曲尾、队列控制、错误恢复仍持有原播放器；普通 seek 不附带播放。未改 Windows MCP；移动端现有通用 UI 点击脚本没有独立跳曲语义，无接口或参数变更。

### 自动验证

在 `mobile/` 执行（均不连接设备、不输出音频）：

```powershell
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug :app:lintDebug --console=plain
```

- 完整 JVM 单元测试：72 个，0 失败 / 错误 / 跳过；其中 `NextAlwaysPlayerTest` 15 个（新增 12 个）。
- 跳曲用例以纯 JVM Player 替身覆盖：暂停 / 播放、四个共享命令、上一首阈值重播、首曲重播、单曲与末曲回绕、IDLE / ENDED 状态请求播放、底层随机 / 循环目标顺序、CUE clipping 和队列保留、同步元数据刷新、空队列 / 无上一目标 / 命令不可用、忽略与异常 seek、目标替换 / 错误、播放拒绝 / 焦点抑制、等待连接期间暂停 / 停止 / 释放 / 队列变化 / 耳机拔出事件使请求失效、普通 seek 和原播放器导航不自动起播。
- Debug APK：`app/build/outputs/apk/debug/app-debug.apk`，仅构建，未安装或启动。
- Debug lint：0 错误，10 项位于未改文件的警告（依赖更新提示、既有复数字符串和 KTX 建议）。Gradle 另提示既有弃用配置。

### 延期的设备验收

本轮按用户限制未启动模拟器，未连接、安装或启动手机客户端，未真实播放；也未操作桌面 JeekMusic、根 `output/`、游戏或系统音量 / 静音 / 音频模式。

JVM 替身验证的是业务决策，没有模拟 MediaSession 的 prepare / 曲尾恢复实现；未在设备上验证 MediaController 连接、Binder / MediaSession 分派、通知 / 锁屏 / 有线及蓝牙按键、Android 12/12L 前台服务拒绝、真实焦点拒绝 / 来电 / 耳机拔出、offload 和 CUE 听感。后续在允许真实音频时验收：暂停或恢复的未准备队列在各入口跳曲后应直接播放；超过上一首阈值应从本曲开头播放；系统拒绝播放、空队列及失败时不意外起播旧曲；确认普通 seek、自动曲尾及 CUE 区间不变。
