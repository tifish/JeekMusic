# Jeek Music 桌面端

Windows 本地音乐播放器，与 [Android 端](../mobile/)共用曲库规则和同步协议。界面使用 Avalonia 12，发布为免安装的 Native AOT 程序；播放使用自行编译的音频版 LGPL libmpv。

界面默认使用简体中文。Windows 文本统一优先使用系统安装的 **Microsoft YaHei（微软雅黑）**，包括菜单、对话框和桌面歌词；缺少该字体时回退到 Segoe UI 及系统缺字回退。不下载或随包分发字体，图标继续使用 Segoe Fluent Icons / Segoe MDL2 Assets。英文界面沿用同一可读字体链。

已实现文件夹曲库、艺术家和专辑浏览、播放列表、搜索和智能标签筛选、WASAPI 输出和独占模式、队列、托盘与快捷键、迷你播放器、桌面歌词、在线歌词与封面、多设备同步、自动更新及中英文界面。在线歌词和封面默认关闭。

同步文件位于音乐目录的 `.jeekmusic/sync/`，每台设备仅写自己的 JSON 文件。同步工具负责传输目录；播放器负责按修改时间合并数据。旧 `backup.json` 可以导入，新版本不再覆盖它。

常听的艺术家可以用艺术家页标题旁的图钉固定到左侧导航栏，也可以右键艺术家或单首歌曲选择“固定到导航栏”（合作歌曲可分别选择艺术家）。固定项支持拖动排序、右键取消固定，重启后保留，并随导航顺序同步到其他设备。

歌曲列表的标题旁显示封面缩略图，支持内嵌封面、同名图片、同专辑共用封面和目录中的 `cover.jpg` 等图片；没有封面时显示音符占位。列表只加载屏幕内的封面，手动更换或下载封面后会刷新。

需求：[requirements.md](docs/requirements.md)。设计：[design.md](docs/design.md)。当前验证结果及未验收项：[validation.md](docs/validation.md)。性能目标和设备兼容性仍以验证报告为准。

## 窗口方向键

主窗口和迷你播放器：← / → 快退 / 快进 5 秒，↑ / ↓ 设备主音量增减 5 个百分点，Z / X 上一首 / 下一首。仅无修饰键生效；Alt+← / → 导航、Ctrl+Alt+← / → 全局切歌保持不变。

歌曲表格普通选中时左右键调整进度，上下键仍选择歌曲。输入/编辑控件与菜单不触发这些快捷键；导航树保留方向键，其他列表保留上下键。进度滑块左右键统一为 5 秒跳转，上下键调音量；音量滑块上下键统一为 5 个百分点，左右键保留滑块原有步进。每次事件只执行一次。

跳转使用当前分轨位置，暂停时仍保持暂停。不可 seek、未加载或输出保护中无操作；终点最多到曲尾前 1s，避免直接跳至 EOF，正常播放到尾仍按队列规则继续。音量先读取设备实时值，限制 0–100；不可调时无操作，不解除应用或系统静音。

In both windows, Left/Right seek by 5 seconds and Up/Down adjust the current endpoint volume by 5 percentage points. Z/X select previous/next. Editing, menus and list/tree navigation keep their own keys. The progress slider uses Left/Right to seek and Up/Down for volume; the volume slider keeps its normal Left/Right step and uses Up/Down for 5-point changes. Seeking preserves pause and CUE boundaries; volume changes preserve mute and output safety gates.

## 音量与独占输出

共享与独占都使用当前播放设备的端点主音量，共享下也影响同设备其他程序。外部系统音量会同步到滑块；启动、切换设备/模式和退出都不恢复旧音量。静音按钮只静音 Jeek Music，系统静音独立显示。

Windows 合成器的 Jeek Music 衰减/静音会保留；请在合成器恢复 100% 并取消静音后再进入独占。设备无硬件音量时，独占滑块不可调：先使用硬件旋钮确认安全音量，再按播放继续。设备或音量读取失败时保持暂停，修复后按播放重试。

Both output modes control the current endpoint master volume; in shared mode this affects other apps on that device. External volume changes are reflected in the slider. Startup, switching and exit never restore historical volume. Mute affects Jeek Music only. Windows mixer attenuation/mute is preserved and must be cleared in the mixer before entering exclusive mode. Without hardware volume control, set a safe level with the physical knob, then press Play. Unreadable controls keep playback paused until a successful retry.

## 开发与验证

需要 Windows、.NET 10 SDK、Git Bash，以及 Native AOT 链接所需的 Visual Studio C++ 工具。

日常测试直接编译并运行仓库根目录的 `output/JeekMusic.exe`，需要系统已安装 x64 .NET 10 运行时。普通无 RID 的框架依赖 build 成功后会自动把完整运行文件同步到这里；Debug 和 Release 最后一次编译覆盖测试入口，包括增量构建。复制测试版本时请复制整个 `output/`。正在运行时，先退出客户端再重新编译。

```powershell
dotnet build desktop/src/JeekMusic.App
./output/JeekMusic.exe
```

```powershell
& 'C:/Program Files/Git/bin/bash.exe' desktop/tools/get-libmpv.sh --lgpl
dotnet test desktop/JeekMusic.slnx
dotnet publish desktop/src/JeekMusic.App -c Release -r win-x64 -o desktop/artifacts/publish
$assets = dotnet msbuild desktop/src/JeekMusic.App -nologo -p:_IsPublishing=true -p:Configuration=Release -p:RuntimeIdentifier=win-x64 -getProperty:ProjectAssetsFile
python desktop/tools/collect-licenses.py desktop/artifacts/publish --assets $assets
```

编译与还原不直接使用 `output/`。App 工程的中间产物与 NuGet assets 位于 `obj/<build或publish>/<RID或portable>/aot-<PublishAot>_sc-<SelfContained>_psc-<PublishSelfContained>/<Configuration>/<TFM>/`（SDK 可再追加 RID）；`bin/` 使用相同层级。空开关值表示 SDK 默认值；NuGet assets 位于配置子目录之前。操作、RID 和显式部署开关在 SDK 导入前隔离，避免还原缓存和编译文件混用。Core 是 RID 无关的类库，SDK 构建项目引用时会移除其 RID/SelfContained，故 Core 和测试工程保留 SDK 默认 `obj/`、`bin/<Configuration>/<TFM>/`；单元测试从各自 `bin/` 加载引用。

带 RID、自包含 build 和所有 publish 均保留在自己的目录，不覆盖日常入口。NativeAOT 发布仍默认启用，正式包自包含、不依赖机器上的 .NET；默认 publish 目录在隔离的 `bin/` 下，也可使用上例的 `desktop/artifacts/publish`。`-o output`、`PublishDir=output` 及其子目录被拒绝，不能将发布包直接写进日常入口；测试发布包应运行发布目录内的 EXE。AOT 链接找不到 `vswhere` 时，仅为当前终端补入 `$env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'`。

build 和 publish 的缓存也相互隔离，发布请使用完整 `dotnet publish`，不能在普通 build 后用 `publish --no-build` 复用另一种操作的缓存。

从旧版混用的 `output/` 迁移会自动发生：普通 build 按明确名单将 `hostfxr.dll`、`hostpolicy.dll`、`coreclr.dll`、`System.Private.CoreLib.dll` 等核心 .NET 运行时文件移至 `desktop/artifacts/legacy-output-runtime-<唯一ID>/`，然后同步框架依赖产物。脚本验证绝对源/目标路径并拒绝重解析点；不删除整个目录，不扫描曲库或正式数据目录，不按 `*.dll` 通配删除。旧版遗留的其他无害文件保留，完整旧目录备份也保留。不要手工把运行时或发布包合并进 `output/`。

构建回归（先退出日常客户端，需要 AOT 工具链；默认仍会多次启动客户端）：

```powershell
python desktop/tools/test-build-isolation.py
```

它验证 Debug → 自包含 RID build → 普通 Release → RID 框架依赖 build → NativeAOT publish → Debug → Release，默认仅用各自 EXE/MCP 做启动/只读诊断、22 个工具描述和 Schema 比较、退出检查，不播放音频或切换输出。它还重建本地 hostfxr 污染并验证自动迁移、未知文件保留和禁止路径覆盖。负向 EXE 子进程单独禁用 .NET 错误弹窗并记录 host trace、stdout/stderr 与退出码。临时数据与用户曲库隔离，日志保存在 `desktop/artifacts/build-isolation-<时间>/`。CI 使用 `--startup-only --published-exe publish/JeekMusic/JeekMusic.exe`，发布包路径仍为 `publish/JeekMusic`。

`--build-only` 跳过全部客户端启动（包括负向 apphost 诊断），仅编译/发布及静态检查，输出明确列出跳过项。`--audio` 才启用真实共享 WASAPI 静音 WAV 播放/设备切换，`--audio --exclusive` 才额外打开独占端点。**无声音、静音 WAV 和应用静音不等于不占音频设备；独占测试能让游戏和其他程序失声。** 游戏期间或要求禁止启动客户端时，连默认模式、`--startup-only`、`mcp_client.py` 都不要运行；它们仍启动真实客户端并读取系统音频状态。不能把“只读 MCP”当成“不会冷启动”。

`python desktop/tools/test_build_workflow.py` 使用模拟客户端和模拟进程验证这些选项边界，不启动 Jeek Music 或访问真实音频。

发布包需包含 `licenses/`、`Models/yamnet.onnx` 和原生 DLL。`collect-licenses.py` 从已还原的 NuGet 包收集许可证，不发送网络请求。LGPL DLL 的对应源码和编译参数随 [libmpv Release](https://github.com/tifish/JeekMusic/releases/tag/libmpv-lgpl-0.41.0-20261021-3) 提供；桌面 CI 下载失败时会停止，不能回退到 GPL 内核。

后台运行测试使用独立的临时数据目录：

```powershell
pwsh desktop/tools/test-app.ps1 -Data "$env:TEMP/jeekmusic-smoke" -Seconds 15 -GracefulExit
```

测试脚本默认运行 `output/JeekMusic.exe`；验证发布包时可用 `-Exe desktop/artifacts/publish/JeekMusic.exe` 指定程序。

`--background` 不激活窗口，截图通过 PrintWindow 获取；测试脚本检测到抢占焦点会失败。`JEEKMUSIC_DATA` 覆盖数据目录，单实例通信也按此目录隔离。正式数据默认放在 `%LOCALAPPDATA%/JeekMusic`。

## 修正智能标签

歌曲右键一级「修正智能标签…」打开同一个编辑面板，运动、人声、曲风及其选项全部直接可见；每类显示当前值和自动／手动来源，多选会标明混合值或混合来源。默认保持不变，只在「应用」时批量保存明确选择的维度。取消、Esc 或关闭窗口不保存；方向键选择同类选项，Tab 切换焦点，Enter 应用。选择具体值会设为手动覆盖，即使与自动结果相同；「恢复自动」保留同步所需的清除记录。不修改音乐文件标签，也不重新分析音频。

## MCP 接口

开发版和正式发布包均内置相同的 MCP 接口，使用 `JeekMusic.exe --mcp` 通过标准 stdio 连接。支持播放控制、曲库搜索、队列、播放列表、智能标签修正、扫描、音频输出和日志诊断，共 22 个工具。连接配置、参数说明和调试示例见 [MCP 使用文档](docs/mcp.md)。

仓库提供只依赖 Python 标准库的调试客户端：

```powershell
python desktop/tools/mcp_client.py --list
python desktop/tools/mcp_client.py get_status
python desktop/tools/mcp_client.py search_tracks '{"query":"周杰伦"}'
```

`tools/prepare-benchmark.py` 可生成独立的五万首合成曲库。`spike/` 和 `spike-audio/` 保留早期技术验证工程，不是正式播放器。


共享模式可开启“自动匹配歌曲格式”，将歌曲采样率、合理有效精度及可靠声道布局作为完整组合匹配到真实设备；关闭时不立即改格式。显式暂停/结束/退出时，关闭流并在确认未被外部改动后恢复自动匹配前的完整设备配置；续播先重新匹配，再从原位置播放，内部切歌不恢复。独占仅一个选项，继续自动协商，保留共享开关偏好。播放栏保留文件格式、共享/独占、重采样与警告信息，采样率部分显示“歌曲采样率 → 实际设备采样率”，左键、右键及键盘激活均打开同一格式菜单，查看源/设备位深和声道、选择模式及共享自动开关，无设备选择或手动格式列表；切换设备使用独立输出设备按钮，其完整菜单保持原有顺序。失败和必要转换明确显示，不用客户端 float 冒充设备位深。详见 [自动匹配音频格式](docs/mcp.md#自动匹配音频格式)。

许可证收集使用与 publish 配置一致的 `ProjectAssetsFile`，不再读取旧的 `obj/project.assets.json`。自定义 RID / AOT / SelfContained 时，查询 assets 路径须传入与 publish 相同的开关；未传 `--assets` 时只接受唯一的隔离 publish graph，多个配置时明确报错，不猜测旧缓存。
