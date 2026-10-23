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


## 方向键进度与音量回归（2026-10-23）

- `dotnet test desktop/JeekMusic.slnx --verbosity minimal`：177 项通过（Core 63、App 114），包含 38 项 `PlaybackShortcutTests`。使用独立 `JEEKMUSIC_DATA`；音量操作用模拟端点，不调节真实系统音量。
- 覆盖主窗口/迷你播放器真实构造与事件接入、歌曲表格隧道路由、左右 5 秒、上下 5 个百分点、Z/X、编辑/菜单/列表/树/滑块焦点保护、修饰键及 Alt 导航、连续 seek、CUE 偏移、暂停/队列/输出门控、设备外部音量更新、0/100 和静音保留。
- `dotnet build desktop/src/JeekMusic.App -c Release --verbosity minimal`：0 警告、0 错误；清理混用输出产物后，`python desktop/tools/test-mcp.py` 通过真正 `output/JeekMusic.exe` 完成冷启动、多客户端、播放/队列、输入校验、正常退出与重启持久化回归。新增曲尾/起点 seek 检查，等待 2.2 秒后确认真实暂停位置及队列不变。
- 真实 mpv/WASAPI 静音 WAV 测试发现曲尾前 100ms 仍可能提前换歌，终点因此统一保护到 `max(0, duration - 1s)`；正常播放自然到尾仍继续队列。另以独立 6 分钟静音 WAV/CUE 验证第二分轨的 0/20 秒请求落在分轨相对 0/19 秒，保留暂停及当前曲目。没有解除系统或用户客户端静音。
- MCP 保持 21 个工具，复用已有 `playback seek` 和 `set_volume`；没有增加相对参数或键盘专用工具。seek 描述、seconds Schema 说明和使用文档同步；未加载、不可 seek 或输出保护中返回业务错误。
- 限制：本轮未重新发布 Native AOT，也未完成 Windows 原生按键/截图目测（Computer Use 返回 `native pipe is unavailable`）；主/迷你窗口已由 Avalonia Headless 验证。构建目录中旧的 app-local `hostfxr.dll` 曾导致框架依赖 EXE 错认 .NET 根目录，已备份旧输出并干净重建恢复；长期产物隔离另项处理。

## 构建产物隔离与旧运行时迁移（2026-10-23）

### 根因和目录规则

- 旧配置取消 TFM/RID 输出后缀，将 App 普通 build、自包含 RID build 和 publish 的编译阶段写入同一个根 `output/`；旧 `IntermediateOutputPath` 同为 `obj/Release/`。系统实际已安装 x64 .NET 10.0.13，错误不是缺少 .NET。
- 在原配置真实复现：`dotnet build desktop/src/JeekMusic.App -c Release -r win-x64 --self-contained true -p:PublishAot=false`，随后普通 `dotnet build desktop/src/JeekMusic.App -c Release`。后者保留前者的 `output/hostfxr.dll`。真实 EXE 返回 **2147516566**，host trace 显示将 `output/` 视为 .NET 根目录、找不到框架。负向子进程单独设置 `DOTNET_DISABLE_GUI_ERRORS=1` 和 host trace，没有为诊断弹出安装提示。证据：`%TEMP%/jeek-build-isolation-20261023/legacy-*.log`、`legacy-exe.txt`。
- App 的 `bin/` 与 `obj/`（包含 NuGet assets）现按 build/publish、RID/portable、显式 PublishAot/SelfContained/PublishSelfContained 参数分开，之后由 SDK 追加配置、TFM/RID。配置在 SDK 导入前确定。Core 是 RID 无关类库，其项目引用构建会移除 RID/SelfContained，故 Core 和测试项目保留 SDK 默认目录。
- 只有普通无 RID 框架依赖 build 成功后自动同步完整运行文件到根 `output/`，Debug/Release 增量往返也会覆盖正确版本。RID、自包含 build 及 publish 不覆盖该入口。`-o output`、`PublishDir=output` 及其子目录会在编译/发布复制之前报错，包括 publish `--no-build`。
- 自动迁移仅将明确名单中的旧核心运行时文件移至 `desktop/artifacts/legacy-output-runtime-<唯一ID>/`，不整目录删除、不按 DLL 通配删除；验证源/目标绝对路径并拒绝重解析点，曲库、正式用户数据和未知文件不迁移。原完整备份 `desktop/artifacts/shortcut-output-backup-20261023-2253/` 仍保留。

### 已实际完成的验证

- 中断前的真实 EXE/MCP 记录在 `desktop/artifacts/build-isolation-20261023-230711/`：步骤 01–12 完成，覆盖 Debug、自包含 win-x64、普通 Release、RID 框架依赖、NativeAOT 发布及各自产物的真正 EXE 冷启动。每个已完成的 smoke 都通过 21 工具、库/队列/播放、多客户端、重启持久化、GUI 正常退出以及 stdio 桥接退出码 **0**、stdout/stderr 空。负向污染重现返回 **2147516566**，后续普通 Release 自动备份迁移并恢复 EXE；根目录测试标记文件保留。步骤 12 的回到 Debug 构建及文件哈希比对完成；**步骤 13 的再次 Debug EXE smoke 中断，后续 Release EXE smoke 未完成**。
- NativeAOT 命令：`dotnet publish desktop/src/JeekMusic.App -c Release -r win-x64 -o desktop/artifacts/build-isolation-publish`，当前进程 PATH 补入 Visual Studio Installer。首次实际生成 native code 成功，保留既有 DataGrid **IL2104 / IL3053** 警告，未扩展修复范围。该真正 NativeAOT EXE 的 MCP 完整回归在中断前通过；诊断显示实际发布 EXE 路径、X64、动态代码不支持。测试子进程使用不存在的 DOTNET_ROOT/DOTNET_ROOT_X64 且禁用多级搜索，没有生成 host trace，发布目录没有 runtimeconfig/hostfxr/coreclr。未在卸载 .NET 的干净 VM 复测。
- 游戏限制下另行运行了**只含 dotnet build/publish/msbuild 的静态流程**，没有重跑已中断的客户端测试批处理。证据：`desktop/artifacts/build-isolation-static-20261023-231828/`，`summary.json` 记录 `clientLaunches=0`：Debug → 自包含 RID build → 托管自包含 publish → Release → RID 框架依赖 build → NativeAOT publish → Debug → Release 全通过；RID/发布期间根入口四个主要文件 SHA-256 不变，Debug/Release 往返恢复各自原哈希。静态检查模型、原生 DLL、许可证目录及不同部署类型的运行时文件清单通过。
- 最终静态发布：`desktop/artifacts/build-isolation-static-20261023-231828/native-publish/`，未启动。根 `output/JeekMusic.exe` 为最后的普通 Release（186368 字节），没有上述四个核心本地运行时文件。
- `dotnet build ... -o output`、`dotnet publish ... -o output`、`dotnet publish ... --no-build -o output`、`dotnet publish ... -o output/forbidden-publish` 均预期退出 **1**，提示保留目录，根入口哈希不变。直接调用 staging 脚本的目录越界源、自包含源也均退出 **1** 且不改变输出。
- `dotnet test desktop/tests/JeekMusic.App.Tests -c Release --filter "FullyQualifiedName~McpSessionTests|FullyQualifiedName~ExclusiveAudioFormatTests|FullyQualifiedName~PlaybackShortcutTests|(FullyQualifiedName~OutputModeSwitchTests&FullyQualifiedName!~RealDecoderReloadsSilentAudioAtTheSavedPositionWithoutUsingAudioHardware)"`：**99 项通过**。已检查其端点/引擎注入为模拟实现；显式排除真实解码器测试。使用临时 JEEKMUSIC_DATA，不访问真实 WASAPI。
- `dotnet test desktop/tests/JeekMusic.Core.Tests -c Release`：**63 项通过**（平台无关规则）。本轮没有在游戏限制后运行 App/solution 全集。
- `python desktop/tools/test_build_workflow.py`：**5 项通过**，客户端、进程和音频均模拟，覆盖默认只读启动流程、独占必须显式选择、模式冲突启动前拒绝、构建脚本默认显式 startup-only 以及 build-only 不调用进程启动器。Python 语法检查、`git diff --check` 通过。

### 音频干扰、测试入口修正与延期项

- 旧通用 `test-mcp.py` 默认会真实 `set_output(exclusive=true)`。本轮中断前的隔离 smoke 确实执行过共享→独占→共享；**静音 WAV 和应用静音不能阻止占用物理端点，独占会令其他程序失声**。主对话读到 `%TEMP%/jeekmusic-mcp-mp1olw1h/data/logs/playback.log` 在 23:10:43.260 打开耳机独占、23:10:44.147 切回共享，用户游戏受到声音干扰。已停止编排及仅属于本轮的测试进程，确认没有 JeekMusic.exe/test-mcp.py/test-build-isolation.py 残留；未调整设备音量、静音或操作游戏进程。
- `test-mcp.py` 和构建隔离回归现在默认只做启动/只读诊断与退出，不播放、不切换设备。真实共享音频需要 `--audio`，真实独占还需要 `--exclusive`；说明和 CI 同步。`--startup-only` 仍会启动真实客户端、读取系统音频状态，不能在用户禁止启动期间使用；独立数据目录不隔离物理设备。`--build-only` 跳过所有客户端启动，明确报告跳过项。
- MCP 仍为 **21 工具**；`get_diagnostics` 兼容性新增 `processPath`、`frameworkDescription`、`processArchitecture`、`dynamicCodeSupported`，描述/文档和验证同步，无参数 Schema 保持空对象、拒绝额外参数。动态代码支持为 false 不单独证明 NativeAOT（AOT 兼容托管构建也可为 false）。没有新增工具或仅开发版开关。
- **尚未运行**：最终版本的默认 startup-only 流程及跨产物实际工具描述/Schema 全量比较、修改显式音频选项后的实机流程、最终 Release EXE 再次冷启动及正式客户端状态复核、GitHub Actions 本身。中断前的已完成日志不能代替这些延期检查。
- **按用户游戏期间约束未重启客户端**。旧正式 PID17140 在开始构建前已停止；此前只读快照保存于 `%TEMP%/jeek-build-isolation-20261023/user-before.json`：暂停、145 首队列、索引 94、设备音量约 26%、应用/端点/会话均未静音。测试使用独立数据，不操作正式队列；禁止启动后未通过 MCP 再读正式状态。后续会话必须继续禁止客户端启动/MCP 冷启动和真实音频测试，直至用户或主对话明确解除。

## 歌词滚动与跟随恢复（2026-10-23）

- 根因：原 `ScrollChanged` 不区分程序滚动/布局通知与用户输入，同步 `_programmaticScroll` 标志无法保护延迟通知；3 秒计时器只隐藏定位线，同句进度提前返回，因而暂停/同句不会主动回中；固定 160/240 留白不能适配视口，异步加载只比较 Track 对象也不能阻止同歌旧重载覆盖。
- 修复：真实输入暂停跟随，最后输入/触摸惯性后 3 秒主动居中，按住拖动不抢回；程序/布局滚动不暂停也不延长等待。触摸识别器吞掉 PointerReleased 时以手势结束/惯性事件释放按住状态；滚动条捕获取消也可恢复。显式 seek（含同句、连续 ±5 秒、MCP、定位线）立即恢复跟随。定位回调合并并读取最新行，隐藏/最小化时停止，重开和布局变化重新定位；首尾句、原译文和长行作为一组居中。首个时间戳之前回到开头但不高亮。纯文本保持手动浏览。无持续动画或 60fps 计时器；定位线按已布局行中心二分查找。
- 新增 **18 项 LyricsPanelTests**，只使用 Avalonia Headless、合成 LRC、内存 SQLite、显式注入的 FakeEngine/FakeEndpointVolume；没有默认 AppServices 构造、真实解码器、音频文件播放、真实端点或客户端启动。覆盖自动换行及延迟程序 ScrollChanged、暂停同句 3 秒真实 DispatcherTimer、最后交互计时、滚轮/触摸/滚动条拖动及捕获取消、外部 seek、隐藏/重开、侧栏/整页、尺寸和 100%–200% LayoutTransform 缩放、首尾句/双语长行、纯文本/空态/搜索开关、旧歌曲/同歌异步结果、同尺寸纯文本/时间轴重载留白、延迟解码器重启不覆盖新手动交互、真实 LyricsService 缓存/偏移后台重载及 MCP 诊断/seek。1500 行歌词经过 100 次同句进度刷新没有新增 ScrollChanged 或重建行；这不等同于实机帧率基准。
- 最终限定回归使用临时 `JEEKMUSIC_DATA`：`dotnet test desktop/tests/JeekMusic.App.Tests/JeekMusic.App.Tests.csproj -c Release --filter "FullyQualifiedName~LyricsPanelTests|FullyQualifiedName~PlaybackShortcutTests|FullyQualifiedName~McpSessionTests|FullyQualifiedName~LocalizationTests" --verbosity minimal`：**85 项通过，0 失败/跳过**。已核查该过滤集的播放引擎及端点均为模拟；未跑 solution/App 全集或 RealDecoder 测试。
- `dotnet build desktop/src/JeekMusic.App/JeekMusic.App.csproj -c Release --no-restore --verbosity minimal`：**0 警告、0 错误**；普通 build 按既有机制同步根 `output/`，未修改产物隔离，也未运行生成的 EXE。`git diff --check` 通过。
- MCP 保持 **21 工具**，兼容性新增 `get_diagnostics.lyricsPanel`（未创建面板时仅 `created: false`；查询不创建控件/读歌词）；报告可见性、歌词索引/偏移、跟随/手动/按住、恢复倒计时、定位线和逻辑像素布局。复用 `playback seek`，工具说明、seconds Schema 描述、中英文提示、需求/设计/使用文档和内存业务验证同步；输入字段/约束无破坏性变化，开发/正式同套代码，无测试专用开关。
- **延期**：真实 Windows 窗口截图/交互及实际 DPI/多屏、正常播放观感、最终 EXE 冷启动/MCP 管道验证、NativeAOT 发布验证和真实音频设备测试。Headless 验证几何与事件，不能代替上述实机验收；本轮没有发布 AOT，也没有用既往日志冒充新版本运行结果。
- **遵守游戏期间限制，未启动客户端/未访问真实音频设备/未重启**；没有改变用户音量、静音、输出模式或操作游戏。构建前后均未发现 JeekMusic 客户端进程，保持停止；须等待主对话明确解除后才可补充启动/UI/音频验收。


## 艺术家分组与可见字母（2026-10-24）

- 艺术家栏按现有 `ArtistInfo.IndexLetter` 排为 A–Z / #，组内稳定沿用快照排序；将数字、符号和未知艺术家合成末尾 #。首行附字母标题与分隔，不添加选择项；头像/数量/右键/固定艺术家仍使用原 ArtistInfo 和导航业务。保留 VirtualizingStackPanel。
- 顶部固定“当前字母 / Current letter”与右侧索引同步。索引与滚动条分列、索引只含存在的组。当前字母读取 ScrollContentPresenter 中顶部实际可见容器（含组标题），剩余不超过 0.5 个逻辑像素的上一行不计；与选中项及滚动百分比无关。索引点击实现目标后按真实坐标最多校正 4 次；末组不能顶齐时高亮实际顶部的组。直接输入/源刷新取消旧目标，正常滚动不改选择或播放。
- 新增 **10 项 ArtistListTests**：内存 SQLite、模拟 FakeEndpointVolume、显式注入 FakeEngine 工厂、Avalonia Headless 实际 MainWindow XAML/主题/控件布局。每项断言没有创建任何解码引擎且没有模拟端点音量写入；没有调用默认 AppServices、Program、Shell、MCP 管道或音频文件。覆盖中文拼音/英文/数字符号/未知、标题非选择项、键盘选择和导航、240 A / 2 B / 75 M / 极短 Z/#、首中尾索引真实点击及几何、末组夹紧、滚轮/滚动条 Value/实际 Thumb 拖动/触摸手势、边界部分行、选择独立性、快速连续点击/旧回调取消、筛选/刷新/清空/单组、搜索隐藏/重开，以及全部 27 组在深浅主题、760/1300 窗宽、480/900 窗高、100%/150%/200% LayoutTransform 和 Headless DPI 变化下的提示/索引/滚动条几何。未实现行不参与可见范围，测试验证实际容器数量远小于艺术家总数。
- 已核查限定回归夹具：PlaybackShortcutTests 使用模拟引擎/端点，McpSessionTests 只使用内存协议委托，LocalizationTests 仅检查资源。最终命令（设置临时 `JEEKMUSIC_DATA`）：`dotnet test desktop/tests/JeekMusic.App.Tests/JeekMusic.App.Tests.csproj -c Release --filter "FullyQualifiedName~ArtistListTests|FullyQualifiedName~PlaybackShortcutTests|FullyQualifiedName~McpSessionTests|FullyQualifiedName~LocalizationTests" --no-restore --verbosity minimal`：**77 项通过，0 失败/跳过**。未运行 solution/App 全集或 RealDecoder。
- `dotnet build desktop/src/JeekMusic.App/JeekMusic.App.csproj -c Release --no-restore`：**0 警告、0 错误**；普通 build 按现有流程同步 `output/`，未运行 EXE、未额外发布 AOT。`git diff --check` 通过。
- MCP 兼容性新增 `get_diagnostics.artistList`：实际分组及组首位置/数量、当前字母、可见艺术家范围、滚动偏移/视口高度、已实现容器数量与有限定位状态。窗口未创建/关闭为 `created: false`，查询不创建窗口；隐藏期间可见位置保留上次布局值，源变化清空旧值，重开重新读取。仍为 **21 工具**，input Schema 仍为空对象且拒绝额外参数；工具描述、使用文档、内存 McpTools 验证同步，开发/正式使用相同代码，无新增控制工具或开发开关。
- **延期实机项**：真实 Windows 原生视觉截图、鼠标/触屏实际体验、系统 DPI 与多屏、最终 EXE 冷启动及 MCP stdio/管道验收。Headless DPI/几何/手势测试不能代替原生视觉与真实硬件验收。本任务未触及音频功能，无需真实音频验证；游戏限制解除前仍禁止所有启动/桥接与真实设备测试。
- **本轮未启动客户端、未访问真实音频设备、未重启**，未改变设备音量/静音/输出模式、未操作游戏。进程只读检查确认无 JeekMusic 客户端，保持关闭；按用户指示延期 AGENTS.md 的重启惯例，等待主对话明确解除。

## 文件夹默认展开与主动收起记忆（2026-10-24）

- 文件夹导航分组及所有目录无偏好时默认展开，新目录也展开；艺术家/播放列表分组沿用原逻辑。主动收起只保存该节点，父目录隐藏子项不产生子目录偏好。主动展开清除该节点的收起记录。普通刷新、重新扫描结果替换、过滤隐藏/恢复、导航树/主题/窗口重建及正常重启保留偏好；恢复/普通导航到选中歌曲所在目录不打开已收起祖先。
- 显式“定位正在播放”临时打开路径中的祖先，保留收起集合；下次树重建/刷新/重启恢复偏好。该行为对跨视图定位和歌曲已在当前表格内的定位一致。无新增设置开关、快捷键或 UI 字符串；中英文使用说明见 `mcp.md`。
- 旧 `UiState.ExpandedNav` 经检索确认只有属性定义，没有持久化/恢复调用；旧字段未出现的节点不代表主动收起，全部按新默认处理。新增 `collapsedFolders` 通过原 `SaveWindow → SaveLayout → UiState.Save` 写入本设备 `snapshot.json`；Key 使用卷身份 + 规范化卷内完整目录，统一大小写、正反斜杠和末尾分隔符，不依赖显示名、盘符或数据库 ID。不写音乐文件、曲库业务身份或 Android 同步数据；点击只更新内存 HashSet，不增加磁盘保存次数。暂时隐藏/无歌曲的 Key 保留，偏好不会凭空生成不存在的节点。
- 新增 **16 项 FolderNavigationTests**：内存 SQLite、临时 settings/序列化快照文件、注入 FakeEngine 工厂和 FakeEndpointVolume、Avalonia Headless 实际 MainWindow/TreeView/双向绑定。覆盖首次全展开及新目录、箭头鼠标点击/键盘左右/标题双击、主动收起再展开、父子独立、文件夹分组收起及选中、模拟扫描结果替换/刷新/筛选、目录暂时消失后恢复、同名不同根及路径、正式 source-generated JSON 序列化和布局保存/恢复钩子的重启序列、旧空设置及旧 expandedNav、Key 的 ID/盘符/大小写/末尾分隔符兼容、主题及窗口重建/隐藏选中路径、模型和控件程序赋值不改偏好、显式定位两种路径、非文件夹分组/导航顺序/固定艺术家业务身份，以及 10,000 个偏好节点只保存 3 个明确例外。该规模测试只验证状态算法，不代表原生大目录渲染帧率验收。
- MCP 兼容新增 `get_diagnostics.folderNavigation` 的默认策略、本地保存、自动导航/显式定位策略及偏好/树节点/临时展开计数。查询复用现有集合和当前树；窗口未创建/关闭为 `created: false`，不创建窗口、导航、展开或启动音频，不输出目录路径。工具描述/使用文档/内存调用验证同步；仍为 **21 工具**、空对象输入 Schema（拒绝额外参数），无额外控制工具或开发开关。模拟验证确认重复查询不改选择、当前视图、队列、偏好或引擎状态。
- 最终限定回归：设置 `$env:JEEKMUSIC_DATA = Join-Path $env:TEMP 'jeek-folder-navigation-verification'` 后执行 `dotnet test desktop/tests/JeekMusic.App.Tests/JeekMusic.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FolderNavigationTests|FullyQualifiedName~ArtistListTests|FullyQualifiedName~PlaybackShortcutTests|FullyQualifiedName~McpSessionTests|FullyQualifiedName~LocalizationTests" --verbosity minimal`，**93 项通过，0 失败/跳过**（含全部 16 项文件夹专项）。已核对这些夹具仅使用模拟引擎/端点、内存协议或资源检查；专项夹具逐项断言未创建任何解码引擎且模拟端点无音量写入。没有运行 App/solution 全集、RealDecoder、真实 scanner、音频文件或 MCP stdio/管道桥接。
- 普通 `dotnet build desktop/src/JeekMusic.App/JeekMusic.App.csproj -c Release --no-restore`：**0 警告、0 错误**。现有 build 流程同步 `output/`，未运行产物、未重复 AOT/publish。`git diff --check` 通过。
- **延期实机项**：原生 Windows 窗口的展开视觉/鼠标键盘体验、真实曲库的大目录渲染与扫描后状态、真实正常退出/最终 EXE 冷启动恢复、发布包及 MCP stdio/管道诊断。Headless 重启序列与主题重建测试不代替原生窗口/最终启动验收；本功能无需新增真实音频测试。游戏期间禁止启动/后台/托盘/独立数据/桥接/`--startup-only` 的约束继续有效，等待主对话明确解除。
- **本轮未启动客户端、未访问真实音频、未重启**；未修改设备音量/静音/输出模式，未操作游戏。提交前只读进程检查为 **0 个 JeekMusic 客户端**，保持关闭；用户运行限制优先 AGENTS.md 自动重启惯例。
