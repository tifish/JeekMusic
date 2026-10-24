# Windows MCP 接口

Windows 客户端内置 MCP 服务，Debug、Release 和 Native AOT 发布包使用相同的工具、参数校验和业务服务，没有开发专用开关。播放器的窗口、托盘和 MCP 操作共享同一份播放状态、队列和曲库。

## 连接

向支持 MCP stdio 的软件添加以下配置，把 `command` 改为本机的实际程序路径：

```json
{
  "mcpServers": {
    "jeekmusic": {
      "command": "D:\\MyOpenSource\\JeekMusic\\output\\JeekMusic.exe",
      "args": ["--mcp"]
    }
  }
}
```

发布包使用解压后的 `JeekMusic.exe` 路径；保留其旁边的 DLL、模型等完整文件。接口不需要 Python、Node.js、管理员权限或额外安装服务。

日常 `output/JeekMusic.exe` 由普通无 RID build 自动同步，为依赖系统 x64 .NET 10 的版本；RID/自包含构建和 NativeAOT 发布不会覆盖它。验证发布版本时显式指定发布目录的 EXE，不合并目录。产物隔离和旧 hostfxr 自动迁移规则见 [开发说明](../README.md#开发与验证)。

`--mcp` 启动轻量 stdio 转发进程，通过命名管道连接当前 Windows 用户的播放器。播放器没有启动时，会自动以 `--tray --background` 启动；默认不激活窗口。曲库及上次队列恢复后接受连接，连接超时为 30 秒。多个软件可同时连接，命令在播放器 UI 线程串行执行。关闭 MCP 的 stdin 会结束转发进程。若希望断开后持续播放，建议先正常启动播放器：部分 MCP 宿主在结束连接时会终止它启动的整个进程树，包括自动启动的播放器。

管道按 Windows 用户和数据目录隔离，限制为当前用户，不监听 TCP 端口。同一用户启动的 MCP 客户端能够读取曲库路径、日志并修改播放器数据。使用独立数据目录测试时，在配置中增加环境变量，且所有需要连接这个实例的软件使用相同目录：

```json
"env": {
  "JEEKMUSIC_DATA": "D:\\Temp\\JeekMusicMcpTest"
}
```

## 工具

实际名称、描述、JSON Schema 和只读/破坏性提示由 `tools/list` 提供。所有工具接受对象参数，拒绝未知参数和类型、枚举、范围错误。列表的默认页大小为 50，最大为 200；使用 `offset`、`limit` 分页，返回 `total`、`items`、`nextOffset`。队列和播放列表的位置均从 **0** 开始；歌曲、文件夹和播放列表的数字 ID 仅在当前数据目录内有效，歌曲另有稳定的 `trackKey`。

| 工具 | 参数与行为 |
|---|---|
| `get_status` | 无参数。当前歌曲、播放/暂停、秒数、音量、随机/循环、队列位置、曲库数量、扫描状态和快照版本 |
| `search_tracks` | `query` 可省略或留空浏览全库；沿用 UI 搜索和排序服务，可按 `folderId`、`artistKey`、`albumKey` 同时筛选；默认使用已保存排序，无偏好时文件修改时间倒序。可选 `sort`（`FileName` / `Modified` / `Title` / `Artist` / `Album` / `Duration` / `Format`）、`reverse` 仅覆盖本次查询，不保存设置；先排序再分页。结果包含 `artistKeys`、`albumKey` 和下述修改时间字段 |
| `get_track` | `trackId`。详细元数据、音频文件路径、稳定键、智能标签和缓存修改时间，包括 CUE 分轨 |
| `play_tracks` | `trackIds` 数组（1–1000 项）、可选 `startIndex`（默认 0）、`shuffle`。先校验全部歌曲可用，再按指定列表播放；与界面一样保留待播的手动队列 |
| `playback` | `action`：`resume` / `pause` / `toggle` / `next` / `previous` / `seek`；`seek` 另需 `seconds`，相对于当前分轨起点（绝对位置，不是增量）；需已加载可 seek 的歌曲且输出已就绪，否则返回业务错误。保留播放/暂停；终点限制在曲尾前 1s（不足 1s 的歌曲为 0），避免直接 seek 到 EOF；超出时长仍返回参数错误。`next` / `previous` 成功选曲或回到开头后直接播放，包括原先暂停的情况；上一首仍遵循超过 3 秒先回到当前分轨开头的规则。空队列无操作；加载或 seek 失败不会恢复旧曲播放。切歌仍受输出安全门控约束，不会代替物理音量确认 |
| `get_queue` | 支持分页，返回实际播放顺序、手动条目标记、当前位置 |
| `edit_queue` | `action`：`add` / `next` 另需 `trackIds`，`remove` / `jump` 另需 `index`，`move` 另需 `index` 与 `toIndex`，`clear` 清空。空队列添加歌曲会开始播放，与界面一致 |
| `set_play_mode` | 至少提供 `shuffle` 或 `repeat`（`off` / `all` / `one`） |
| `set_volume` | 至少提供 `volume`（0–100）或 `muted`。两种模式都设置当前实际设备端点主音量，共享下影响同设备其他程序；不持久化音量。`muted` 只静音播放器。不可调/写入失败返回业务错误 |
| `get_output` | 无参数。WASAPI 设备列表、所选设备/独占模式、文件与设备的音频格式，以及下述 `volumeState` |
| `set_output` | 至少提供 `device` 或 `exclusive`。`device` 来自设备列表；`auto` 跟随系统默认设备。设置持久化，模式切换可能重建音频输出 |
| `list_playlists` | 支持分页。播放列表 ID、UID、名称和时间戳 |
| `get_playlist` | `playlistId`，支持分页。按手动顺序返回条目，保留不可用条目 |
| `edit_playlist` | `action`：`create` 需 `name`；其余需 `playlistId`。`rename` 需 `name`，`delete` 删除列表，`add` 需 `trackIds`，`remove` 需 `positions`，`move` 需 `positions` 和 `target`（移动前的目标位置，可等于条目数）。触发正常持久化及同步事件，不删除音频文件 |
| `list_folders` | 无参数。曲库文件夹 ID、名称、完整路径和可用状态 |
| `add_folder` | `path` 为存在的绝对目录；如果包含已添加的子目录，需显式提供 `replaceChildren: true` 才会替换。添加后排队扫描 |
| `remove_folder` | `folderId`。排队移出曲库，保留音频文件 |
| `rescan_library` | 可选 `full`，默认增量扫描；`true` 重建曲库 |
| `get_diagnostics` | 无参数（input Schema 仍为空对象，拒绝额外参数）。应用/协议版本、进程 ID、实际 EXE 路径 `processPath`、运行框架 `frameworkDescription`、架构 `processArchitecture`、是否支持动态代码 `dynamicCodeSupported`，以及原有数据/程序/日志目录、引擎状态、最近一次播放错误、状态、音频输出详情、`lyricsPanel` 跟随诊断、`artistList` 分组/可见位置诊断和 `folderNavigation` 文件夹展开策略/收起偏好统计 |
| `get_logs` | 可选 `kind`（`app` / `playback`）、`lines`（默认 50、最大 200）；读取日志尾部最多 64 KiB，不接受任意文件路径 |
| `show_window` | 无参数。显示并激活主窗口 |

播放、seek 和音频输出由 libmpv 异步完成；返回的状态是命令执行后的即时快照，应再次调用 `get_status` 确认实际播放状态。扫描、添加/移除文件夹通过现有后台曲库队列执行，`queued: true` 只表示已入队。通过快照版本、扫描状态、搜索结果和日志确认结果；扫描失败原因也进入正常应用日志。

成功的工具调用同时返回 `structuredContent` 对象和对应 JSON 文本 `content`，便于程序及通用 MCP 客户端消费。工具参数/业务错误返回 `isError: true`；未知工具或 JSON-RPC 方法、协议格式错误返回 JSON-RPC 错误，不会因此终止正常会话。

### 歌曲修改时间与排序

所有歌曲对象（包括 `get_track`、`search_tracks`、队列和播放列表中的歌曲）兼容新增：`mtimeUtcTicks` 为缓存音频文件 mtime 的 .NET UTC ticks 原值；`modifiedUtc` 为带 `Z` 的 ISO 8601 UTC 字符串，未知为 null；`modifiedLocal` 为客户端本地 `yyyy-MM-dd HH:mm`，未知为“—”。CUE 分轨共享源音频 mtime，不使用 CUE 文件、扫描或入库时间。查询不重新读取文件元数据、不扫描、不创建窗口、不启动音频。

`search_tracks` 沿用搜索视图排序，即使带 `albumKey` 也作为搜索结果排序；无偏好默认 Modified（新到旧）。其他基准排序为升序。`reverse` 反转基准顺序，Modified 因此变为旧到新；无效日期双向排末，同时间仍按文件/CUE 分轨号稳定排序。未给 `sort` 时默认沿用保存的方向，显式给 `sort` 时方向默认 false。示例：`{"sort":"Modified","reverse":false,"limit":50}`。现有调用无需新参数，返回分页结构和工具数量（21）不变；搜索顺序现在明确与 UI 服务一致，而非数据库行序。

客户端无排序偏好时，普通曲库采用新默认，手工播放列表与专辑保留曲序；已有保存值（包括旧版无条件写入的 FileName）一律保留。MCP 本次查询排序不改设置、播放队列或手工列表顺序，`get_playlist` 继续按手动顺序返回。

上述四个运行诊断字段为兼容性新增，开发版与正式版同时提供；NativeAOT 的 `dynamicCodeSupported` 为 `false`，启用 AOT 兼容开关的托管构建也可能为 `false`，不能单凭此字段判断发布类型。它们用于核对连接到的实际程序，不能将框架描述字符串当成部署类型枚举。工具数量仍为 21，参数 Schema 无变化。

### 歌词跟随诊断

`get_diagnostics.lyricsPanel` 为兼容性新增，同样适用于开发与正式版本，无额外开关。尚未创建歌词面板时仅返回 `created: false`，查询不会打开窗口、创建面板或读取歌词文件。创建后返回：

- `created`、`visible`、`trackKey`、`loaded`、`synced`、`lineCount`、`offsetMs`。
- `currentIndex`：已显示的零基高亮行，首句之前或纯文本为 -1；面板隐藏期间不刷新此 UI 值，实际进度请读取同次结果的 `status.positionSeconds`。
- `following`、`manualBrowsing`、`pointerHeld`、`locatorVisible`；`resumeAfterMs` 为最后手动交互后的剩余等待时间，按住时即使为 0 也不回中。
- `scrollOffsetY`、`viewportHeight`：Avalonia 逻辑像素；`centerLineTimeMs` 为视口中央最近的歌词行时间（未加歌曲偏移），纯文本/空态为 null。

现有 `playback {"action":"seek","seconds":...}` 即为定位能力：与拖动进度条及左右 ±5 秒共用 PlayerService，显式 seek 立即退出手动浏览、同步可见的时间轴歌词，即使仍在同一句；保留原播放/暂停及 CUE 语义。无需新增歌词 seek 工具。纯文本不自动定位。输入 Schema 的字段、范围及空对象约束保持不变，seek 工具与 `seconds` 参数描述同步说明歌词行为。

用户禁止启动客户端或正在进行游戏隔离验证时，**不要调用 MCP 客户端脚本，包括只读查询和 `--startup-only`**：stdio 桥接仍可能冷启动客户端。此时只运行明确注入模拟引擎/端点的测试；Headless 的内存业务调用不经过 stdio/管道，不启动客户端。

### 艺术家分组诊断

`get_diagnostics.artistList` 是兼容性新增，开发/正式版本同样提供。窗口未创建或已关闭时仅返回 `created: false`；查询不创建窗口或控件。窗口已创建后返回：

- `created`、`visible`、`artistCount`；`groups` 按显示顺序给出 `letter`、`firstIndex`、`count`，索引均为过滤后的零基艺术家位置，不计标题。
- `currentLetter`：视口顶部第一条仍可见的艺术家（含其组标题）的现有 `IndexLetter`，空表/尚无布局为 null。仅余 0.5 个逻辑像素及以下的行不计入顶部；末组无法顶齐时仍以实际顶部为准，不代表所选艺术家或上次点击目标。
- `firstVisibleIndex`、`lastVisibleIndex`：实际已布局且与视口相交的艺术家范围；无布局/空表为 -1。`realizedCount` 是虚拟化实现的容器数量，不是全库数量。
- `scrollOffsetY`、`viewportHeight`：Avalonia 逻辑像素；`positionPending` 表示索引点击后的有限布局校正尚未完成。隐藏期间不主动滚动/刷新可见范围，这些位置字段保留上次布局值；源变化清空旧位置，重新显示后按新布局更新。

工具数量仍为 21，`get_diagnostics` 输入 Schema 仍为空对象并拒绝额外参数，没有新增控制工具。Headless 测试直接调用注入模拟服务的内存 McpTools，不连接 stdio 或命名管道；游戏期间仍禁止任何可能冷启动的 MCP 桥接。

使用：艺术家栏按 A–Z / # 显示组标题；滚轮、触摸、拖动滚动条或键盘导航时查看顶部“当前字母 / Current letter”和右侧高亮。点击右侧字母尽量将组标题置顶；末尾内容不足一屏时，高亮显示真实顶部的组。索引跳转和普通滚动不会改变当前选择或播放。数字、符号与未知艺术家遵循现有规则归 #；搜索仍使用原搜索页，智能筛选会刷新艺术家及索引。

### 文件夹展开诊断

`get_diagnostics.folderNavigation` 是兼容性新增，开发/正式版本同一实现，无额外开关、无新增工具；21 个工具和空对象输入 Schema 不变。查询只读取当前内存导航状态，不创建窗口、恢复选中、展开目录或启动音频。窗口尚未创建/已关闭时 `created: false`，策略和已载入的偏好统计仍可读取，树节点计数为 0。

- `defaultExpanded: true`、`persistence: "device-local-ui-snapshot"`、`keyVersion: 1`。
- `automaticNavigation: "preserve-collapsed"`：恢复选中项、普通导航/历史、刷新/扫描不打开已主动收起的文件夹祖先。
- `explicitLocate: "temporary-reveal"`：显式“定位正在播放”临时打开所需祖先，不删除收起偏好；下次树重建/重启重新应用。
- `collapsedPreferenceCount`：所有已记录收起的节点数，包含文件夹分组，以及因筛选/无歌曲/离线暂时没有显示的目录；不因隐藏丢失偏好。
- `nodeCount`、`expandedNodeCount`：当前过滤后的文件夹树逻辑节点总数/展开数，包含分组及叶节点，**包括被收起祖先遮住的子节点**；不是屏幕可见或已实现容器数。
- `temporarilyExpandedCount`：有收起记录但当前临时展开的节点数。输出不含目录路径或额外音乐身份。

使用：文件夹分组和各层目录默认展开，新扫描目录同样展开；点击箭头、标题双击或键盘展开/收起只更新操作的节点。主动收起后，正常退出并重启仍保留。先前旧版 `expandedNav` 未记录明确收起意图，不据其缺项推断收起；旧目录采用新的默认展开。偏好保存在本机 `snapshot.json.collapsedFolders`，不写音乐目录或 Android 同步数据；不新增开关或 UI 提示字符串。

English: Folders expand by default, including newly discovered directories. Your explicit collapses are remembered on this device across refresh, filtering and normal restarts. Parent and child preferences are independent. Locate playing temporarily reveals ancestors without clearing those preferences; the next tree rebuild restores them. Artist and playlist groups keep their existing behavior.

Headless 模拟验证直接调用内存 McpTools；禁止客户端启动期间，不执行 stdio/命名管道桥接，即使只是 `get_diagnostics`。

上一首/下一首的限定模拟回归位于 `TrackNavigationTests`：使用注入的 FakeEngine、FakeEndpointVolume、临时空文件及 Headless 窗口，内存调用 MCP，不启动客户端或 MCP 服务，不解码音频、不访问真实音频设备。覆盖暂停/播放中切歌、3 秒阈值、CUE、随机/循环和边界、加载/seek 失败、连续切歌事件及输出门控。全局快捷键和 SMTC 的真实系统分发仍需在允许客户端及音频测试时验收。

```powershell
# RID 构建避免向正在使用的根 output/ 执行日常 staging；只运行指定模拟类。
$env:JEEKMUSIC_DATA = Join-Path $env:TEMP ('jeek-navigation-tests-' + [guid]::NewGuid().ToString('N'))
dotnet build desktop/tests/JeekMusic.App.Tests/JeekMusic.App.Tests.csproj -r win-x64 -p:SelfContained=false -p:PublishAot=false
dotnet test desktop/tests/JeekMusic.App.Tests/JeekMusic.App.Tests.csproj --no-build --no-restore -r win-x64 -p:SelfContained=false -p:PublishAot=false --filter FullyQualifiedName~JeekMusic.App.Tests.TrackNavigationTests
```

### 音量输出语义

原 `get_status.volume` 保留数值字段，表示端点读数（不可读时为 0 占位，**不能把它当作真实值**）；`muted` 只表示应用静音。`get_status.output` 和 `get_output.volumeState` 提供同样的详细对象：

- `semantics: "endpoint-master"`、`muteScope: "player"`。
- `actualDeviceId`：绑定给 WASAPI 的物理端点 ID；`device: "auto"` 仍表示用户选择跟随默认。
- `endpointVolume` / `endpointMuted`：设备当前读数，不可读为 `null`；不等于应用静音。
- `volumeAvailable`：当前能否写端点音量。`hardwareVolume` / `hardwareMute`：硬件支持，未知为 `null`。独占无硬件音量时端点软件音量不影响实际响度，不能以滑块数值声称独占响度。
- `sessionVolume` / `sessionMuted`：本进程默认会话的 Windows 合成器状态，未知为 `null`；`sessionPolicy: "preserve-external-block-exclusive"`。正常新会话为 100%，已有用户衰减/静音保留；请在合成器中恢复后再进入独占。
- `hardwareVolumeConfirmationRequired`：独占会绕过软件音量或软件静音时，用户需先用物理旋钮确认安全音量，再调用 `playback` 的 `resume` 或点击播放。切换设备/模式会重新要求确认；此确认不能绕过读取错误或会话衰减。`next` / `previous`（包括上一首回到开头）不能完成此确认。
- `outputPending`、`blockReason`、`diagnostic`：保护门控和读取/写入错误，诊断包含 Core Audio 操作及 HRESULT。修复设备后显式 `resume` 重试，不自动恢复播放。`endpoint-write-failed`、`endpoint-read-failed`、`hardware-support-unreadable`、`session-attenuated-or-unreadable`、`hardware-volume-confirmation` 等原因可用于诊断。

`set_output` 若进入独占被会话策略拒绝，返回 `isError`，原模式保留。已接受的异步重建仍需轮询输出状态。音量、静音和默认设备每 250ms 只读同步；窗口在托盘时同样更新。启动、模式/设备切换和退出均不写历史端点音量。旧设置中的 `volume` / `sharedVolume` / `exclusiveVolume` / `sharedDeviceVolumes` 被忽略并在保存时移除。

## 日常调试

仓库工具仅需 Python 3 标准库；从仓库根目录执行：

```powershell
python desktop/tools/mcp_client.py --list
python desktop/tools/mcp_client.py get_status
python desktop/tools/mcp_client.py search_tracks '{"query":"zjl","limit":10}'
python desktop/tools/mcp_client.py play_tracks '{"trackIds":[123,456]}'
python desktop/tools/mcp_client.py playback '{"action":"pause"}'
python desktop/tools/mcp_client.py get_logs '{"kind":"playback","lines":30}'
python desktop/tools/mcp_client.py --data "$env:TEMP/jeekmusic-mcp-debug" get_diagnostics
python desktop/tools/mcp_client.py --exe desktop/artifacts/publish/JeekMusic.exe get_status
```

`123`、`456` 应替换为搜索得到的真实 ID。不加参数默认调用 `get_status`。工具失败时脚本退出码为 1；正常为 0。需要连续调用时，可从 `desktop/tools/mcp_client.py` 导入 `McpClient`，在一个 `with` 会话里调用 `client.call(name, arguments)`。

## 协议与验证

遵循 [MCP stdio 传输](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)、[初始化生命周期](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle)及 [tools 协议](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)。支持协商 `2025-11-25`、`2025-06-18`、`2025-03-26` 和 `2024-11-05`；不支持的版本返回 `2025-11-25` 供客户端决定是否继续。必须先 `initialize`，再发送 `notifications/initialized`；支持 `ping`、`tools/list`、`tools/call`。未声明 resources、prompts、订阅或服务端主动请求能力。

消息使用 UTF-8 单行 JSON-RPC；stdout 仅输出协议消息，转发错误写 stderr，播放器日志仍写文件。单条消息上限为 1 MiB 字符；超限或损坏的 UTF-8 连接被关闭。无需 SDK 反射或动态类型生成，兼容 Native AOT。

```powershell
dotnet test desktop/JeekMusic.slnx
python desktop/tools/test-mcp.py

# 可选：同时使用官方 Python SDK 验证握手、工具 schema 和调用
python -m pip install mcp
python desktop/tools/test-mcp.py --sdk

# 对正式 Native AOT 包运行相同的启动诊断（仍会启动真实客户端）
python desktop/tools/test-mcp.py --exe desktop/artifacts/publish/JeekMusic.exe --sdk

# 构建往返、旧运行时污染迁移及真正 EXE/发布包冷启动
python desktop/tools/test-build-isolation.py

# 显式选择真实共享音频/播放/队列验证（静音 WAV 也会占用设备）
python desktop/tools/test-mcp.py --audio

# 仅在允许中断其他程序声音时，额外验证真实独占切换
python desktop/tools/test-mcp.py --audio --exclusive

# 完全不启动客户端的构建与静态产物检查
python desktop/tools/test-build-isolation.py --build-only
```

`test-mcp.py` 默认只验证真实 EXE 路径/运行诊断、21 个工具、冷启动、stdio 关闭退出码及输出和正常退出，不发播放或输出切换命令。`--startup-only` 保留为默认行为的显式别名；`--sdk` 可叠加验证协议与参数 Schema。`--audio` 才额外验证静音 WAV、多客户端、中文/分页、曲库、播放列表、播放、队列、日志、错误校验、重启后应用静音恢复及共享输出；`--exclusive` 必须同时指定 `--audio` 才能打开真实独占设备。构建隔离脚本使用同样的显式选项规则，默认传入 `--startup-only`。

**静音 WAV / 应用静音仍会打开 WASAPI 设备，独占能中断游戏等其他程序声音。** 独立数据目录只隔离曲库和单实例，不隔离物理音频设备。默认启动诊断也会启动 GUI 并读取系统音频状态；用户要求禁止启动或正在处理游戏失声时，不运行任何 EXE/MCP（包括 `--startup-only`、`mcp_client.py` 的只读命令）。仅可编译/静态检查和确认使用模拟端点的测试。测试结束后只清理自己的播放器，不激活窗口或使用正式曲库；`show_window` 的激活行为不在后台测试中调用。
