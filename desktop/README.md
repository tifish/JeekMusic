# Jeek Music 桌面端

Windows 本地音乐播放器，与 [Android 端](../mobile/)共用曲库规则和同步协议。界面使用 Avalonia 12，发布为免安装的 Native AOT 程序；播放使用自行编译的音频版 LGPL libmpv。

界面默认使用简体中文。Windows 文本统一优先使用系统安装的 **Microsoft YaHei（微软雅黑）**，包括菜单、对话框和桌面歌词；缺少该字体时回退到 Segoe UI 及系统缺字回退。不下载或随包分发字体，图标继续使用 Segoe Fluent Icons / Segoe MDL2 Assets。英文界面沿用同一可读字体链。

已实现文件夹曲库、艺术家和专辑浏览、播放列表、搜索和智能标签筛选、WASAPI 输出和独占模式、队列、托盘与快捷键、迷你播放器、桌面歌词、在线歌词与封面、多设备同步、自动更新及中英文界面。在线歌词和封面默认关闭。

同步文件位于音乐目录的 `.jeekmusic/sync/`，每台设备仅写自己的 JSON 文件。同步工具负责传输目录；播放器负责按修改时间合并数据。旧 `backup.json` 可以导入，新版本不再覆盖它。

常听的艺术家可以用艺术家页标题旁的图钉固定到左侧导航栏，也可以右键艺术家或单首歌曲选择“固定到导航栏”（合作歌曲可分别选择艺术家）。固定项支持拖动排序、右键取消固定，重启后保留，并随导航顺序同步到其他设备。

歌曲列表的标题旁显示封面缩略图，支持内嵌封面、同名图片、同专辑共用封面和目录中的 `cover.jpg` 等图片；没有封面时显示音符占位。列表只加载屏幕内的封面，手动更换或下载封面后会刷新。列表标题栏的“更多”菜单提供“获取全部封面”“获取全部歌词”，检查当前来源与筛选后的完整歌曲集合（含滚动未呈现项，搜索沿用前 200 首上限），已有本地/缓存资源优先，不强制重新下载或覆盖。选中项的右键获取功能保留；封面按专辑去重，并按现有保存设置补齐资源。任务支持转后台、取消及重复启动保护，开始后切换列表不改变目标。

需求：[requirements.md](docs/requirements.md)。设计：[design.md](docs/design.md)。当前验证结果及未验收项：[validation.md](docs/validation.md)。性能目标和设备兼容性仍以验证报告为准。

## 窗口方向键

歌曲列表获得焦点时，Ctrl+C 复制选中歌曲的完整歌名；多选按当前列表排序逐行复制，包含未滚动显示的选中项，不带艺术家、专辑或其他列。没有选中项时保留剪贴板；搜索框等文本输入控件仍复制选中的文字。

主窗口和迷你播放器：← / → 快退 / 快进 5 秒，↑ / ↓ 设备主音量增减 5 个百分点，Z / X 上一首 / 下一首。仅无修饰键生效；Alt+← / → 导航、Ctrl+Alt+← / → 全局切歌保持不变。

歌曲表格普通选中时左右键调整进度，上下键仍选择歌曲。输入/编辑控件与菜单不触发这些快捷键；导航树保留方向键，其他列表保留上下键。进度滑块左右键统一为 5 秒跳转，上下键调音量；音量滑块上下键统一为 5 个百分点，左右键保留滑块原有步进。每次事件只执行一次。

跳转使用当前分轨位置，暂停时仍保持暂停。不可 seek、未加载或输出保护中无操作；终点最多到曲尾前 1s，避免直接跳至 EOF，正常播放到尾仍按队列规则继续。音量先读取设备实时值，限制 0–100；不可调时无操作，不解除应用或系统静音。

In both windows, Left/Right seek by 5 seconds and Up/Down adjust the current endpoint volume by 5 percentage points. Z/X select previous/next. Editing, menus and list/tree navigation keep their own keys. The progress slider uses Left/Right to seek and Up/Down for volume; the volume slider keeps its normal Left/Right step and uses Up/Down for 5-point changes. Seeking preserves pause and CUE boundaries; volume changes preserve mute and output safety gates.

## 音量与独占输出

共享与独占都使用当前播放设备的端点主音量，共享下也影响同设备其他程序。外部系统音量会同步到滑块；启动、切换设备/模式和退出都不恢复旧音量。静音按钮只静音 Jeek Music，系统静音独立显示。

Windows 合成器的 Jeek Music 衰减/静音会保留；请在合成器恢复 100% 并取消静音后再进入独占。设备无硬件音量时，独占滑块不可调：先使用硬件旋钮确认安全音量，再按播放继续。设备或音量读取失败时保持暂停，修复后按播放重试。

Both output modes control the current endpoint master volume; in shared mode this affects other apps on that device. External volume changes are reflected in the slider. Startup, switching and exit never restore historical volume. Mute affects Jeek Music only. Windows mixer attenuation/mute is preserved and must be cleared in the mixer before entering exclusive mode. Without hardware volume control, set a safe level with the physical knob, then press Play. Unreadable controls keep playback paused until a successful retry.

## 开发与验证

日常运行入口是仓库根目录的 `bin/JeekMusic.exe`。双击根目录 `build-and-run.cmd`：先退出本仓库新旧日常客户端，再构建 Windows x64 自包含版本，最后启动新版本。编译需要 .NET 10 SDK；运行和复制该版本无需安装 .NET，也无需安装软件或管理员权限。失败时保留错误信息，不启动旧产物。

`bin/` 是完整绿色软件目录，可以整体复制到其他可写目录：

| 路径 | 用途 |
| --- | --- |
| `JeekMusic.exe` | 唯一启动入口，可从任意工作目录启动 |
| `runtime/` | 应用程序集、托管及原生依赖、随包 .NET 运行时；不存 PDB、链接库、编译缓存或测试报告 |
| `Models/` | YAMNet 模型及已有的附加模型 |
| `Preferences/Runtime/` | 随包分析脚本与组件描述 |
| `licenses/`、`LICENSE.txt` | 第三方许可与项目许可证 |
| `portable.json` | 绿色包布局标记，随包保留 |
| `data/` | 用户设置、曲库、队列、学习组件与模型、封面/歌词缓存、运行日志和更新下载；后续构建不会覆盖 |

首次执行脚本时，旧 `output/` 中的 `Models/`、`Preferences/`、`licenses/` 迁入新目录；其余内容连同完整旧目录保存在 `desktop/artifacts/legacy-output-<唯一ID>/`。如果 `bin/data/` 尚不存在，会复制 `%LOCALAPPDATA%/JeekMusic` 的现有状态，原数据保留作备份；之后只使用 `bin/data/`。迁移保留学习模型与向量，但不复制带本机绝对路径的 Python 虚拟环境、临时训练任务及其就绪标记；可选学习环境会按需重新准备。已有 `bin/data/` 不会被旧数据覆盖。复制整个绿色目录会同时携带用户数据；只分发程序时应在客户端退出后去掉 `data/`，下次启动会创建新数据。音乐文件仍在用户选择的曲库文件夹，不自动打包；跨电脑需重新指定曲库位置。Windows 的文件关联、防火墙授权和受用户密钥保护的配对信息不能随目录自动迁移。

`JEEKMUSIC_DATA` 始终优先于绿色包的数据目录，用于隔离测试。旧版没有 `portable.json` 的包继续使用 `%LOCALAPPDATA%/JeekMusic`。单实例和 MCP 按实际数据目录隔离。

日常构建的等价命令（先退出客户端）：

```powershell
dotnet build desktop/src/JeekMusic.App -r win-x64 --self-contained true -p:PublishAot=false -p:JeekStageDailyBuild=true -p:JeekMigrateLegacyData=true
./bin/JeekMusic.exe
```

普通 `dotnet build`、`dotnet test`、带 RID 的其他构建和所有 publish 都不会更新根 `bin/`；只有显式 `JeekStageDailyBuild=true` 的 Windows x64 自包含 build 会同步完整日常目录，包括增量构建。直接将 `-o`、`OutputPath` 或 `PublishDir` 指向根 `bin/` 或其子目录会被拒绝。

编译和还原的中间产物继续保存在各工程自己的 `obj/<build或publish>/<RID或portable>/aot-<PublishAot>_sc-<SelfContained>_psc-<PublishSelfContained>/`，编译结果保存在对应工程的 `bin/` 下；它们与仓库根目录的绿色 `bin/` 完全分离。开发日志、截图、测试夹具和迁移备份统一放 `desktop/artifacts/`，不放日常运行目录。同步脚本拒绝重解析点，过期运行依赖仅按上次清单移到 artifacts 备份，不通配删除 DLL，不修改用户数据或未知文件。

正式发布仍使用 NativeAOT，需 Git Bash 和 Visual Studio C++ 链接工具，发布目录与日常目录分离。正式包使用同一绿色数据规则和同一套 MCP；NativeAOT 无需托管运行时，其原生 DLL 保留发布器要求的相对位置：

```powershell
& 'C:/Program Files/Git/bin/bash.exe' desktop/tools/get-libmpv.sh --lgpl
dotnet test desktop/JeekMusic.slnx
dotnet publish desktop/src/JeekMusic.App -c Release -r win-x64 -o desktop/artifacts/publish
$assets = dotnet msbuild desktop/src/JeekMusic.App -nologo -p:_IsPublishing=true -p:Configuration=Release -p:RuntimeIdentifier=win-x64 -getProperty:ProjectAssetsFile
python desktop/tools/collect-licenses.py desktop/artifacts/publish --assets $assets
```

发布须使用完整 `dotnet publish`，不要用 `publish --no-build` 复用普通 build 缓存。AOT 找不到 `vswhere` 时，仅为当前终端补入 `$env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'`。许可证收集使用与 publish 相同配置的 `ProjectAssetsFile`。发布包保留 `Models/`、`Preferences/`、`licenses/`、`portable.json` 和原生 DLL。LGPL DLL 的源码和参数随 [libmpv Release](https://github.com/tifish/JeekMusic/releases/tag/libmpv-lgpl-0.41.0-20261021-3) 提供。

构建与目录迁移回归（所有证据写入 `desktop/artifacts/`）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/tools/test-runtime-layout.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/tools/test-portable-package.ps1 -Relocate
python desktop/tools/test_build_workflow.py
python desktop/tools/test-build-isolation.py
```

`test-runtime-layout.ps1` 使用独立模拟仓库验证资源迁移、完整归档、数据保留、过期依赖和路径保护，不启动客户端。`test-portable-package.ps1 -Relocate` 将包复制到 artifacts 下带空格的路径，使用新数据启动真实客户端，通过 MCP 验证绿色路径，并对合成 WAV 做限时模型/原生解码诊断，不播放音频；`-Exe` 可指定正式包。构建回归默认实际启动各版本 EXE 并通过 MCP 比较工具与 Schema，不播放音频；`--build-only` 跳过全部客户端启动。仅 `--audio` 显式测试真实共享输出，`--audio --exclusive` 额外占用独占设备；静音不等于不占设备。CI 可用 `--startup-only --published-exe publish/JeekMusic/JeekMusic.exe` 复用 AOT 产物。

真实客户端截图验证使用独立测试数据，默认 EXE 为 `bin/JeekMusic.exe`：

```powershell
pwsh desktop/tools/test-app.ps1 -Data "$env:TEMP/jeekmusic-smoke" -Seconds 15 -GracefulExit
```

`--background` 不激活窗口，截图通过 PrintWindow 获取；该脚本需 PowerShell 7，发布包可用 `-Exe desktop/artifacts/publish/JeekMusic.exe` 指定。不要将截图、测试数据或构建日志写到根 `bin/`。

## 修正智能标签

歌曲右键一级「修正智能标签…」打开同一个编辑面板，运动、人声、曲风、平静程度及其选项全部直接可见；每类显示当前值和自动／手动来源，多选会标明混合值或混合来源。默认保持不变，只在「应用」时批量保存明确选择的维度。取消、Esc 或关闭窗口不保存；方向键选择同类选项，Tab 切换焦点，Enter 应用。选择具体值会设为手动覆盖，即使与自动结果相同；「恢复自动」保留同步所需的清除记录。不修改音乐文件标签，也不重新分析音频。

局域网音乐同步现已提供根共享、TLS 配对、手机子目录下载及独立目的地映射，详见 [首版契约与限制](../docs/lan-sync.md)。

## MCP 接口

开发版和正式发布包均内置相同的 MCP 接口，使用 `JeekMusic.exe --mcp` 通过标准 stdio 连接。支持播放控制、曲库搜索、队列、播放列表、智能标签修正、扫描、音频输出和日志诊断，共 28 个工具。连接配置、参数说明和调试示例见 [MCP 使用文档](docs/mcp.md)。

仓库提供只依赖 Python 标准库的调试客户端：

```powershell
python desktop/tools/mcp_client.py --list
python desktop/tools/mcp_client.py get_status
python desktop/tools/mcp_client.py search_tracks '{"query":"周杰伦"}'
```

`tools/prepare-benchmark.py` 可生成独立的五万首合成曲库。`spike/` 和 `spike-audio/` 保留早期技术验证工程，不是正式播放器。


共享模式可开启“自动匹配歌曲格式”，将歌曲采样率、合理有效精度及可靠声道布局作为完整组合匹配到真实设备；关闭时不立即改格式。显式暂停/结束/退出时，关闭流并在确认未被外部改动后恢复自动匹配前的完整设备配置；续播先重新匹配，再从原位置播放，内部切歌不恢复。独占仅一个选项，继续自动协商，保留共享开关偏好。播放栏保留文件格式、共享/独占、重采样与警告信息，有输出流时采样率部分显示“歌曲采样率 → 实际设备采样率”；暂停释放输出后只显示源采样率和“输出空闲”，不把恢复后的设备默认格式当作播放输出或重采样，tooltip/格式菜单另列设备默认格式，左键、右键及键盘激活均打开同一格式菜单，查看源/设备位深和声道、选择模式及共享自动开关，无设备选择或手动格式列表；切换设备使用独立输出设备按钮，其完整菜单保持原有顺序。失败和必要转换明确显示，不用客户端 float 冒充设备位深。详见 [自动匹配音频格式](docs/mcp.md#自动匹配音频格式)。

许可证收集使用与 publish 配置一致的 `ProjectAssetsFile`，不再读取旧的 `obj/project.assets.json`。自定义 RID / AOT / SelfContained 时，查询 assets 路径须传入与 publish 相同的开关；未传 `--assets` 时只接受唯一的隔离 publish graph，多个配置时明确报错，不猜测旧缓存。

运动分析采用 Analyzer 6 / Rules 8，保留原运动阈值并增加有条件的持续节奏证据；旧缓存渐进升级。实际修复范围、许可和验证见 [运动 v8](../docs/workout-music-v8.md)。
