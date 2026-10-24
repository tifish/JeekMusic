# 移动端验证记录

## 2026-10-24：默认文件修改时间排序与紧凑时间显示

基线 `51ffe2cf250c8207aef093972893b8e1192226ba`，对应 F-4、A-6、U-8。Settings 默认与 MainViewModel 初值均为 MODIFIED；DataStore 无键/未知值才用新默认，有效旧值及历史 DEFAULT→FILE_NAME 保留。只在用户 setSongSort 时写键；无偏好时播放列表保留手动顺序/拖动，专辑按碟号/音轨号，显式排序继续全局生效。来源队列复用同一管线，手动待播队列不变。

修改时间取 TrackFactory 已写入的音频 MediaStore DATE_MODIFIED（Unix 秒，CUE 共用源文件），不读每行磁盘或触发重扫。共享 TrackRow 在艺术家/专辑下面增加 labelSmall“修改：yyyy-MM-dd HH:mm / Modified: …”，使用系统时区；≤0 或超出安全日期范围为“—”并排末，同时间按文件与同源 CUE 分轨号稳定排序。子文件夹名称排序不变。

在 `mobile/` 执行：

```powershell
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug :app:lintDebug --console=plain
.\gradlew.bat :app:testDebugUnitTest --console=plain
```

- 75 项纯 JVM 测试，0 失败/错误/跳过；新增 SongSorterTest 3 项，覆盖无偏好/有效旧值/DEFAULT/未知值和 Flow 初值、显式排序、专辑默认曲序、有效/未知/溢出 mtime、秒与毫秒混淆、UTC/新加坡本地显示、相同显示分钟仍按秒排序、同时间/CUE 稳定顺序及不可用条目排末。DataStore 实际磁盘重开与 Android 进程恢复仍待设备验收；本轮旧值验证针对生产解析函数和排序 Flow。
- Debug APK 构建成功，`app/build/outputs/apk/debug/app-debug.apk`，未安装或启动。
- lint 0 错误、11 警告：依赖/Gradle 版本提示、原有复数字符串和 KTX 建议，均不位于本次改动行；另有既有 Gradle 弃用提示。

按游戏隔离限制，未启动模拟器、连接/安装/启动任何手机客户端或真实播放，未操作桌面客户端、根 `output/`、游戏或真实音频设备。真实手机上的小屏/字体缩放/深浅主题排版、系统时区切换后的动态刷新、DataStore 进程恢复与真实来源队列同步均延期，由主对话验收。

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
