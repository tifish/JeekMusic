# 实现与验证记录

更新：2026-10-21。当前为 Windows x64 本地预览构建（版本 1.0.0），不是所有验收指标均已达标的正式版本。对应需求见 [requirements.md](requirements.md)，实现设计见 [design.md](design.md)。

## 实现范围

D1–D6 的主要功能已接入：文件夹曲库、文件变更监听、艺术家与专辑、播放列表、搜索与智能筛选、libmpv 播放、输出设备与独占设置、SMTC、队列、文件操作、托盘与快捷键、迷你播放器、桌面歌词、在线歌词与封面、智能分析、同步、自动更新、中英文界面。

Android 同时加入每设备同步文件、事务合并、删除记录、冲突历史和共享分析结果；Room 数据库升级到版本 6。旧 `backup.json` 只作为导入来源，新同步不再覆盖它。

这份记录区分代码实现、自动化测试和设备验收。下面未验证的项目不能视为通过。需求中明确标为后续的 ARM64、DSD、NAS 等 P2 项不作为本预览版的完成承诺。

## 已完成的检查

| 项目 | 结果与边界 |
|---|---|
| 桌面自动化测试 | `dotnet test desktop/JeekMusic.slnx -c Release`：77 项通过（Core 63，App 14） |
| Android 自动化测试 | 54 项单元测试通过；模拟器上 13 项设备测试通过；Debug APK 构建成功 |
| 发布构建 | Windows x64 Native AOT 构建成功；Avalonia DataGrid 的 IL2104 / IL3053 裁剪与 AOT 警告仍存在，不能仅凭编译成功认定所有反射路径都正确 |
| 中英文界面 | 两种语言均通过后台启动、截图检查和正常退出；英文资源键无缺失，格式占位符测试通过 |
| 焦点 | 后台测试使用独立数据目录和 `--background`，PrintWindow 截图；最终中文测试 0/59 次焦点采样属于测试程序，正常退出成功 |
| 在线来源 | 实际检测网易、QQ、iTunes、MusicBrainz、archive.org、LRCLIB 连接成功；公开歌曲查询从网易、QQ、LRCLIB 返回结果。这是当时的连通性记录，不保证第三方接口一直可用 |
| 音乐文件写入 | 合并写入封面与歌词后，逐样本对比解码 PCM 不变；标题、艺术家、专辑、注释和修改时间保持；锁定目标时写入失败且原文件字节不变 |
| 分析签名 | 标签写入改变文件大小后，仅更新此前有效的分析签名，不使已有过期分析重新有效 |

写入封面和歌词先在同卷隐藏目录复制文件，ATL 修改副本，确认原文件未变后原子替换。失败时保留原文件。歌词成功保存到文件后清理重复的应用缓存；取消退出中的缓存清理不会抛出未处理的取消异常。

## 统一端点音量验证（2026-10-23）

- `dotnet test desktop/JeekMusic.slnx --verbosity minimal`：151 项通过（Core 63、App 88）。输出测试使用可注入 Core Audio 设备，覆盖共享/独占切换、CUE 位置、快速重建与旧事件隔离、外部音量无反馈写回、真实设备 ID 绑定、默认设备变更、指定设备断开/恢复、旧 JSON 迁移、局部静音、合成器衰减/静音/未知读数、无硬件音量明确继续、写入/读回失败及读取恢复后的显式重试。
- `python desktop/tools/test-mcp.py --sdk`：开发/框架运行版通过；21 个工具和官方 SDK 的 Schema/调用验证通过。静音 WAV 验证实际 WASAPI 共享播放、同端点显式设备/auto 重建、会话静音阻止独占、应用静音不改变端点静音及重启读取当前端点值。测试不写端点音量，不用固定的 40% 覆盖用户设置。
- `$env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH` 后执行 `dotnet publish desktop/src/JeekMusic.App -c Release -r win-x64 -o desktop/artifacts/publish --verbosity minimal`：Native AOT 发布成功，仅保留既有 Avalonia DataGrid IL2104/IL3053 警告。首次未补 PATH 时链接失败，按仓库现有说明修复环境后通过。
- `python desktop/tools/test-mcp.py --exe desktop/artifacts/publish/JeekMusic.exe --sdk`：正式 Native AOT 包相同 MCP/SDK smoke 通过，真实 WASAPI **共享→独占→共享** 重建通过；生成音频全为静音 WAV，应用静音保持开启，端点音量/静音未改变。发布路径下的新会话为 100% 且未静音。
- 当前真实默认端点读数约 20%，驱动报告硬件 volume/mute 均支持；`output/JeekMusic.exe` 的既有 Windows 合成器会话已静音。开发路径测试保留这个外部设置，验证拒绝绕过；没有为测试强行取消用户静音。
- `pwsh -NoProfile -File desktop/tools/test-app.ps1 ... -Seconds 5 -Shot ...`：中英文播放栏截图复核通过，提示不挤掉队列/歌词/迷你播放器按钮，两轮后台启动均为 0/25 次前台焦点采样；英文使用 `-GracefulExit` 正常退出。
- 外部系统音量调整、默认设备跨物理设备变化、拔插、无硬件控制设备及异常 HRESULT 的主要覆盖来自可注入测试；尚不能代替不同 USB DAC、蓝牙、驱动及实际响度的人工验收。250ms 只读轮询属于近实时同步；主线程被其他工作阻塞时可能延迟。
- 正常新会话由 Windows 初始化为 100%；既有合成器衰减/静音保留。迁移不会自动恢复历史端点音量，也不会自动清除旧版本可能留下的会话静音，需要用户在 Windows 合成器自行解除。

## MCP 接口验证（2026-10-23）

Windows 开发版和 Native AOT 发布包均内置 `--mcp` stdio 入口，提供同一组 21 个工具，复用客户端服务。接入配置及工具参数见 [mcp.md](mcp.md)。

- `dotnet test desktop/JeekMusic.slnx --no-restore`：150 项通过（Core 63、App 87），其中 28 项 MCP 协议测试覆盖版本协商、初始化顺序、请求格式、工具校验、错误恢复和消息长度限制。
- `python desktop/tools/test-mcp.py --sdk`：开发版完成冷启动、多客户端共享状态、中文搜索与分页、播放列表、播放与跳转、队列操作、曲库增删、错误参数、日志、保留活动连接时正常退出和设置恢复验证。生成的音频为静音 WAV；不验证听感或设备音质。
- Windows x64 Native AOT 发布成功，对 `desktop/artifacts/mcp-aot-check/JeekMusic.exe` 运行相同端到端脚本及官方 Python MCP SDK 验证通过。MCP 代码没有新增裁剪/AOT 警告；现有 Avalonia DataGrid 的 IL2104 / IL3053 警告仍存在。
- Native AOT 后台窗口启动焦点检查为 0/39 次采样属于测试程序。`show_window` 会主动激活窗口，后台端到端检查不调用它。
- 测试使用独立临时数据和音乐目录。文件夹/播放列表移除后，生成的原音频文件仍保留；结束后只清理测试实例。

## 同步集成

在独立临时曲库里使用两首合成 FLAC、一份 LRC 和模拟手机同步文件验证：

- 较新的远端播放列表覆盖本机版本，被覆盖版本进入 30 天历史。
- 上级目录与子目录之间按路径后缀匹配；本机不存在的歌曲保留为不可用条目。
- 手动 `MOOD=SPORT` 与 250ms 歌词偏移正确导入。
- 后续删除记录删除播放列表，历史版本保留。
- 损坏的其他设备文件不阻断合法设备的合并；桌面端未改写手机文件。
- 对内存整数和 JSON 解析整数的处理已统一，并增加回归测试。

Android SAF 的同步与分析 JSON 读取在分配完整内容前限制为 32MB，避免异常文件无限分配。真实手机与 PC 经同步工具传输的端到端流程尚待设备验收。

### Android 模拟器验收（2026-10-21）

独立 AVD `JeekMusicAcceptance`，Android 16 / API 36.1、Google Play x86_64 镜像；测试包名 `com.tinyfish.jeekmusic.debug`。模拟器以无窗口、无扬声器输出方式运行，未使用或清除已有手机数据。桌面继续使用独立临时曲库。

- 安装和覆盖安装成功；通过系统文件夹选择器授权 `Music/JeekAcceptance`，扫描出两首合成 FLAC，读取标题、艺术家、专辑和时长。点击播放后系统媒体会话显示对应歌曲，并自动进入下一首；这不验证真实扬声器或蓝牙音质。
- 设备测试 13 项通过，新增 5→6 迁移覆盖播放列表、不可用歌曲、固定艺术家、歌词偏移、手动标签和队列保留，验证同步日志与播放列表能在同一事务中回滚。
- 将桌面实际导出的设备 JSON 通过 ADB 传入，手机合并播放列表；存在的歌匹配本机路径，`Desktop only.flac` 保留为不可用，750ms 歌词偏移和 SPORT 手动修正正确入库。
- 在手机 UI 将列表重命名为 `PhoneEdited`，将手机实际导出的 JSON 传回桌面；桌面名称更新，旧版本进入历史。桌面再修改并传回手机，手机历史中出现 `PhoneEdited`，点击恢复后再次导出该版本。
- 在手机 UI 删除列表，手机导出删除记录；桌面导入后删除对应列表，两端音频文件仍存在。手机读取旧桌面文件时也未复活已经删除的列表。
- 将模拟器设为未充电，离线清空它的测试分析记录（事前保存数据库备份），重新启动后从桌面分析文件导入两行；人声、古典、平静、能量、管弦乐和响度字段与桌面输出完全一致。验证的是共享结果直接复用，不是两种解码器自行分析得到相同分数。
- 模拟器完整重启后，SAF 授权、两首歌曲、设备 ID 和删除记录保留；再次同步成功，已删除列表未被旧远端文件复活。同步前后对远端设备文件逐字节比较不变。

本轮发现并修复：不完整的远端导航会隐藏 Android 内置页面，两端现在补齐内置项、去重并统一艺术家键；不可用条目不再暴露内部 `foreign:` 前缀；手机备份说明改为真实使用的 `.jeekmusic/sync/`。导航规范化在两端均有回归测试。

传输由 ADB 复制实际设备文件完成，**不等同于 Syncthing / 网盘自身的传输与冲突验收**。本机截图、数据库快照与分析导入断言结果在 `%TEMP%/JeekMusic-acceptance-20261021/`。设备测试可通过以下命令复现（仅对测试模拟器）：

```powershell
# 在 mobile/ 下构建设备测试包
.\gradlew.bat :app:assembleDebug :app:assembleDebugAndroidTest :app:testDebugUnitTest
adb -s emulator-5580 install -r app/build/outputs/apk/debug/app-debug.apk
adb -s emulator-5580 install -r app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb -s emulator-5580 shell am instrument -w com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner
```

## 分析模型一致性

使用 109 首歌曲、3270 个分析帧对比 TFLite 和 ONNX 管线，分数平均绝对误差为 **0.0002110815357971087**。其中 **2 首歌曲在 SPORT 阈值附近分类不同**；不能宣称两端逐位一致或所有分类完全相同。

复现工具为 `tools/verify-yamnet.py` 和 `tools/yamnet-to-onnx.py`。本机完整报告保存在 `%TEMP%/JeekMusic-review-20261021/model-parity.json`。共享分析结果使用版本、文件大小和时长校验，导出前同时排除本机签名已失效的分析行。

## 五万首性能

测试曲库为 500 个目录、每目录 100 个短 WAV 硬链接。它测量文件系统枚举、元数据处理和表格装载开销，**不代表五万首真实混合格式音乐的扫描或解码性能**。准备工具为 `tools/prepare-benchmark.py`，运行工具为 `tools/test-app.ps1`。

测试机：AMD Ryzen 9 5900X、Windows 11 Pro for Workstations（build 26300），构建 SDK 为 .NET 10.0.300。

上一轮在 SDK 10.0.300 下、无并行编译的 Native AOT 基线测量：

| 指标 | 实测 | 要求与结论 |
|---|---:|---|
| 窗口打开 | 266ms | <500ms；该次通过，未测清空系统文件缓存后的启动 |
| 全部 50,000 行就绪 | 649ms | <800ms；该次通过 |
| 首次扫描 | 约 7.9s | <3min；仅合成文件集 |
| 增量枚举与比较 | 约 0.1–0.2s | <5s；未改动的合成文件集 |
| 空闲进程私有内存 | 195.1MiB | 低于 200MiB；脚本以 1024² 字节换算，若目标严格采用十进制 200MB，则约 204.6MB 仍超出 |
| 最后 5s 的 CPU 时间增量 | 0ms | 该次观测为空闲；不表示任何时刻都没有后台任务 |
| 焦点采样 | 0/89 | 未抢占前台；正常退出成功 |

已通过减少重复搜索字符串、复用快照字符串、避免无变化时重建快照和一次性回收大批临时对象降低内存。同步路径映射改为可回收缓存后，空闲内存从 203.1MiB 降到 195.1MiB；实际同步期间仍持有强引用，不会因回收丢失当前操作的数据。回收不使用周期性空闲轮询。

独立列表构建的 Release/JIT 检查中，部分排序仍超过 100ms。**G-7 的全部操作 <100ms 和滚动 60fps 尚未验收**；不能将列表计算耗时等同于完整 UI 交互耗时。正式性能验收应补充真实曲库、多次独立进程启动和 UI 帧时间记录。

本轮验收期间，本机 Visual Studio / .NET 更新将 SDK 从 10.0.300 换成 10.0.402（中间短暂仅能发现 10.0.113）。更新完成后 Native AOT 重建成功；关闭模拟器、停止并行编译后两次启动测得：

| 本轮启动 | 窗口 | 全部行就绪 | 空闲私有内存 | 最后 5s CPU |
|---|---:|---:|---:|---:|
| 第一次 | 398ms | **1486ms** | 195.7MiB | 0ms |
| 第二次 | 294ms | 764ms | 195.9MiB | 0ms |

两次均正常退出，均为 0/89 次前台焦点采样。第一次表格装载超过 800ms，不能仅凭后一次通过宣称冷启动指标稳定达标；未控制系统文件缓存、安装器后台活动等因素，因此也不能把差异直接归因于 SDK。

同轮 Release/JIT 列表计算检查：每列预热 3 次，再测 10 次。文件名、修改时间、标题、艺术家、专辑、时长、格式排序中位数分别为 54.5、27.7、76.1、31.8、56.7、48.3、58.3ms；最大值分别为 56.4、43.4、97.4、47.7、62.2、57.1、74.4ms。预热后的列表计算通过 100ms 检查，首次计算和完整 UI 渲染仍不在这组结果的覆盖范围内。原始样本在 `%TEMP%/JeekMusic-acceptance-20261021/sorting.json`。

## libmpv 与许可证

当前原生 DLL 使用自行构建的音频版 LGPL libmpv 0.41.0，来自 [libmpv-lgpl-0.41.0-20261021-3](https://github.com/tifish/JeekMusic/releases/tag/libmpv-lgpl-0.41.0-20261021-3)。对应构建 [39602172287](https://github.com/tifish/JeekMusic/actions/runs/39602172287) 成功，Release 同时提供源码归档 `sources-lgpl.tar.xz` 与构建信息。

桌面 CI 必须取得该 LGPL 构建，下载失败不会回退到 GPL DLL。`tools/collect-licenses.py` 收集 NuGet 依赖许可证、NOTICE、包清单及补充的上游许可证；发布目录包含根 LICENSE、`licenses/`、LGPL 源码地址和 DLL 替换说明。许可收集成功不替代未来新增依赖时的许可审核。

## 尚待完成的验收

- 真实五万首曲库的严格内存目标、稳定的冷启动装载时间、所有排序/切换/筛选延迟及 60fps 滚动。
- WASAPI 独占模式在实际设备上的采样率、位深、硬件音量；默认设备切换、蓝牙和指定设备拔出。
- 真实输出上的无缝播放、CUE 相邻音轨衔接，以及不同格式切换。
- Windows 10 1809、不同显卡、100%–200% 和不同 DPI 的多显示器切换。
- Android 真机 SAF、同步工具传输、双端并发修改、离线与重新连接的完整流程。
- 自动更新下载后退出替换、重新启动的真实版本升级；目前只完成实现与构建检查。
- 全功能键盘与辅助技术验收，以及长时间播放与大曲库连续文件变更压力测试。

本机生成物位于 `desktop/artifacts/JeekMusic-desktop-preview-win-x64.zip`，解压后运行 `JeekMusic/JeekMusic.exe`。Android 调试 APK 位于 `mobile/app/build/outputs/apk/debug/`。生成物不纳入源码提交。
