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
| `get_track` | `trackId`。详细元数据、音频文件路径、稳定键、已提交的智能标签和缓存修改时间，包括 CUE 分轨；原 `tags` 保持兼容，新增 `tagSources`（各维度 `AUTO` / `MANUAL`，无结果为 null） |
| `set_smart_tags` | `trackIds`（1–1000 项）和至少一个 `mood` / `voice` / `genre`。运动为 `SPORT` / `NON_SPORT`，人声为 `VOCAL` / `INSTRUMENTAL`，曲风为 `CLASSICAL` / `NON_CLASSICAL`；每类也可用 `AUTO` 恢复自动。省略的维度及其同步时间戳保持不变。支持曲库中的离线歌曲，不要求文件在线；先验证全部歌曲和输入，再一次事务写入覆盖及重算结果，失败全部回滚。等待提交和同步通知后返回去重歌曲的 `count`、`items`（包含 `tags` 和 `tagSources`），不播放、分析音频或修改文件元数据 |
| `play_tracks` | `trackIds` 数组（1–1000 项）、可选 `startIndex`（默认 0）、`shuffle`。先校验全部歌曲可用，再按指定列表播放；与界面一样保留待播的手动队列 |
| `playback` | `action`：`resume` / `pause` / `toggle` / `next` / `previous` / `seek`；`seek` 另需 `seconds`，相对于当前分轨起点（绝对位置，不是增量）；需已加载可 seek 的歌曲，或显式暂停释放后保留的歌曲（只改保存位置，不开启音频），否则返回业务错误。显式 pause/toggle 暂停先关闭流并恢复本客户端临时格式，保留歌曲/队列/位置；resume 先重新匹配共享自动格式，再从原位播放，内部切歌/重建暂停不恢复。保留播放/暂停；终点限制在曲尾前 1s（不足 1s 的歌曲为 0），避免直接 seek 到 EOF；超出时长仍返回参数错误。`next` / `previous` 成功选曲或回到开头后直接播放，包括原先暂停的情况；上一首仍遵循超过 3 秒先回到当前分轨开头的规则。空队列无操作；加载或 seek 失败不会恢复旧曲播放。切歌仍受输出安全门控约束，不会代替物理音量确认 |
| `get_queue` | 支持分页，返回实际播放顺序、手动条目标记、当前位置 |
| `edit_queue` | `action`：`add` / `next` 另需 `trackIds`，`remove` / `jump` 另需 `index`，`move` 另需 `index` 与 `toIndex`，`clear` 清空。空队列添加歌曲会开始播放，与界面一致 |
| `set_play_mode` | 至少提供 `shuffle` 或 `repeat`（`off` / `all` / `one`） |
| `set_volume` | 至少提供 `volume`（0–100）或 `muted`。两种模式都设置当前实际设备端点主音量，共享下影响同设备其他程序；不持久化音量。`muted` 只静音播放器。不可调/写入失败返回业务错误 |
| `get_output` | 无参数。设备列表、共享/独占、音量安全、兼容的采样率摘要，以及完整 `audioFormat`（源、客户端流、真实端点、混音、精确组合能力与转换/失败原因） |
| `set_output` | 至少提供 `device`、`exclusive` 或 `automaticFormat`。`device` 来自设备列表，`auto` 跟随系统默认端点。`automaticFormat` 为共享自动匹配完整歌曲格式的布尔开关，false 不改动系统当前格式；独占始终自动协商，不另设格式选项。已在独占时修改共享开关需先切共享，或同时给 exclusive=false。设置持久化，重建保留 CUE 位置与播放/暂停意图 |
| `list_playlists` | 支持分页。播放列表 ID、UID、名称和时间戳 |
| `get_playlist` | `playlistId`，支持分页。按手动顺序返回条目，保留不可用条目 |
| `edit_playlist` | `action`：`create` 需 `name`；其余需 `playlistId`。`rename` 需 `name`，`delete` 删除列表，`add` 需 `trackIds`，`remove` 需 `positions`，`move` 需 `positions` 和 `target`（移动前的目标位置，可等于条目数）。触发正常持久化及同步事件，不删除音频文件 |
| `list_folders` | 无参数。曲库文件夹 ID、名称、完整路径和可用状态 |
| `add_folder` | `path` 为存在的绝对目录；如果包含已添加的子目录，需显式提供 `replaceChildren: true` 才会替换。添加后排队扫描 |
| `remove_folder` | `folderId`。排队移出曲库，保留音频文件 |
| `rescan_library` | 可选 `full`，默认增量扫描；`true` 重建曲库 |
| `get_diagnostics` | 无参数（input Schema 仍为空对象，拒绝额外参数）。应用/协议版本、进程 ID、实际 EXE 路径 `processPath`、运行框架 `frameworkDescription`、架构 `processArchitecture`、是否支持动态代码 `dynamicCodeSupported`，以及原有数据/程序/日志目录、引擎状态、最近一次播放错误、状态、音频输出详情、`lyricsPanel` 跟随诊断、`artistList` 分组/可见位置诊断、`folderNavigation` 文件夹展开策略/收起偏好统计和 `mainLayout` 实际面板宽度/可见性/自动增宽状态 |
| `get_logs` | 可选 `kind`（`app` / `playback`）、`lines`（默认 50、最大 200）；读取日志尾部最多 64 KiB，不接受任意文件路径 |
| `show_window` | 无参数。显示并激活主窗口 |

智能标签修正与歌曲右键一级「修正智能标签…」使用同一个事务服务。界面三个分类同时显示当前值和来源，默认保持不变；只有应用才保存，取消／Esc／关闭不保存。`set_smart_tags` 不打开编辑器，也不需要额外确认。

```json
{"trackIds":[12,34],"voice":"INSTRUMENTAL","genre":"AUTO"}
```

上例把两首歌设为手动纯音乐、曲风恢复自动，运动维度及其同步时间戳不变。`tags` 结构兼容旧调用；`tagSources` 的三个小写键分别为 `AUTO`、`MANUAL` 或 null。`get_track` 和修正响应直接读取已提交结果；搜索／队列等批量结果沿用曲库快照，通常在 2 秒内刷新。

`python desktop/tools/test-smart-tags-mcp.py --exe <JeekMusic.exe>` 使用临时数据库、离线虚拟歌曲和缓存模型分数验证真实异步 MCP、单／批量修正、同值转手动、恢复自动清除记录、省略维度时间戳、无效输入及重算写入失败回滚，不播放音频。CI 对日常构建和 NativeAOT 发布包都执行它；原启动诊断脚本仍默认只读，无新增音频动作。

播放、seek 和音频输出由 libmpv 异步完成；返回的状态是命令执行后的即时快照，应再次调用 `get_status` 确认实际播放状态。扫描、添加/移除文件夹通过现有后台曲库队列执行，`queued: true` 只表示已入队。通过快照版本、扫描状态、搜索结果和日志确认结果；扫描失败原因也进入正常应用日志。

成功的工具调用同时返回 `structuredContent` 对象和对应 JSON 文本 `content`，便于程序及通用 MCP 客户端消费。工具参数/业务错误返回 `isError: true`；未知工具或 JSON-RPC 方法、协议格式错误返回 JSON-RPC 错误，不会因此终止正常会话。

### Windows 字体诊断

`get_diagnostics.typography` 提供只读字体信息：`defaultFamilyChain` 是配置的 `Microsoft YaHei, Segoe UI`，`initialized` 表示已有 Avalonia 应用。初始化后另有 `effectiveDefaultFamily`、`microsoftYaHeiAvailable`（系统是否安装准确的 Microsoft YaHei 家族）、`sample`、`normalFamilies` 和 `boldFamilies`。后两个数组从代表性中文实际排版后的 GlyphRun 读取家族，不是把请求字体名当成命中结果。未安装微软雅黑时正常使用后续字体及系统缺字回退，诊断如实报告。

查询不创建应用或窗口、不滚动、不播放。开发和正式版共用实现；工具仍为 22 个，`get_diagnostics` 仍无参数、空对象 Schema 并拒绝额外参数。

English: `typography` reports the configured/effective default chain, exact Microsoft YaHei availability, and the families actually used to shape the Chinese sample in regular and bold weights. Missing fonts use normal fallback. This read-only addition creates no window and adds no tool or argument.

### 空格播放/暂停与焦点

`playback {"action":"toggle"}` 与主窗口、迷你播放器的无修饰空格共用 `PlayerService.PlayPause()`：切换播放/暂停，保留曲目、队列及位置；暂停释放流并恢复本客户端拥有的临时端点格式，续播自动重新匹配并从保留位置继续。空队列仍为空操作，输出保护仍有效。沿用现有工具及参数，无新增键盘工具。

窗口内普通按钮、复选框、列表、滑块聚焦时，空格的 KeyDown/KeyUp 在隧道路由中接管，一次按压只切换一次，长按不重复，不点击焦点控件。按住期间切换焦点仍属于原按压；窗口失活时清理状态。文本/搜索/重命名与 IME、编辑控件、菜单/弹出层和对话框保持原有输入语义，Ctrl/Alt/Shift/Meta+Space 不触发应用内播放快捷键；原方向键、音量、Z/X 和 Alt 导航保持原规则。

English: `playback` with `action: "toggle"` shares the main/mini player's unmodified Space service, retaining track, queue and position and the existing pause/release/rematch lifecycle. A held Space triggers once without clicking the focused control. Editing, IME, menus, dialogs and modified Space keep their normal behavior. No keyboard-specific tool or schema field is added.

### 播放时当前歌曲可见性

`play_tracks`、`playback` 的恢复/切换播放/上一首/下一首，以及 `edit_queue` 的跳转或空队列开始播放，与双击、菜单、快捷键共用 `PlayerService.PlaybackStarted`。成功开始、恢复、当前曲重播或自然换歌后，只发出一次定位请求；内部输出重建不会新增请求，也不改变显式暂停释放音频、续播保留位置的行为。

当前可见歌曲表格含目标歌曲时，先记录定位请求起点的实际可见状态。该行原本已完整可见时只更新当前曲标记，不改纵向或横向滚动；原本屏外或部分露出时，先复用 DataGrid 虚拟化实现目标行，再在布局后将行中心尽量对齐实际数据行视口中心（排除表头）。首尾按合法滚动边界停靠，不添加留白。居中决策在请求期间保持，不因中间 ScrollIntoView 已将行带进视口而提前结束。异步替换列表时，等待新列表的可靠布局再判断。打开的队列弹层同样遵循已可见则保持、屏外则居中的规则。不会改排序、筛选、多选、焦点或文件夹展开状态，也不会自动导航离开当前视图。当前视图不含歌曲（包括筛选排除、搜索结果上限等情况）时保留原视图。普通进度、队列编辑、输出状态刷新不发起定位，完成定位后用户可继续浏览其他歌曲。

歌曲表格的选择背景、悬浮和键盘焦点沿用主题；当前曲以封面上的独立强调色播放/暂停标记和标题加粗表示，两种状态可同时显示，标记不改变行高或已有列的显示偏好。队列复用同一标记。暂停时当前曲仍保留标记并显示暂停图标与“当前歌曲（已暂停）/ Current track (paused)”提示；真实播放状态仍读取现有 `get_status.playing`，当前曲读取 `get_status.track`，不新增视觉样式控制工具或参数。

`get_diagnostics.songVisibility` 和 `queueVisibility` 是兼容性新增，只读取既有控件状态；未创建或已关闭时返回 `created: false`。开发版与正式版同样提供，查询不创建窗口、计算列表、滚动或启动音频。

- `visible`、`currentTrackId`、`containsCurrent`、`currentFullyVisible`：当前控件状态；完整可见按真实行与数据行视口的纵向边界判断，0.5 逻辑像素容差，不要求歌曲表格横向展示所有列。
- `requestedTrackId`、`requestVersion`、`positionPending`、`layoutPasses`、`rowsReady`：最近请求、代次、是否等待异步列表/布局、有限校正次数（最多 6 次）和列表是否就绪。快速切歌只处理最新请求，视图重算/切换、用户输入、关闭窗口会取消旧请求。
- `alignment` 为 `none`、`awaiting-layout`、`keep` 或 `center`；`initiallyFullyVisible` 在可靠布局前为 null，之后记录本次请求的原始判断，虚拟化实现行后也不改写；`centerErrorY` 为当前行中心相对实际数据视口中心的纵向差（逻辑像素），无行布局时为 null。首尾无法居中时可以非零，判断是否完整可见仍以 `currentFullyVisible` 为准。
- `result`：`no-request`、`pending`、`already-visible`、`centered`、`clamped`，或边界/取消原因 `not-in-current-view`、`view-hidden`、`view-changed`、`track-changed`、`user-input`、`queue-changed`、`closed`、`layout-limit`、`row-taller-than-viewport`。原 `revealed` 结果随居中规则改为 `centered` 或 `clamped`。超过有限布局校正或单行高于视口时不保证完整显示，不会无限重试。`result` 是最近请求的结果，`currentFullyVisible` 是查询时的实际状态；用户随后滚走时两者可以不同。

现共 22 个工具（新增智能标签修正）；播放工具的参数与范围保持兼容；`get_diagnostics` 输入 Schema 仍为空对象并拒绝额外参数，不新增 UI 专用控制工具。Headless 回归在 `PlayingVisibilityTests`，使用真实 DataGrid/队列弹层、跨屏虚拟化、内存曲库和 fake 引擎/端点（含真实发出 `FileStarted` 的模拟自然换歌），不连接 MCP 桥接、不启动客户端、不解码音频。

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

`get_diagnostics.mainLayout` 为兼容性新增，开发和正式版同一实现。窗口未创建或已关闭时仅返回 `created: false`；读取既有窗口的布局状态，不创建窗口、触发布局/展开/导航、访问目录或启动音频。工具总数为 22（含既有智能标签工具），输入 Schema 仍为空对象并拒绝额外参数，无新增 UI 控制工具或参数。

- `windowWidth`、`renderScaling`：主内容区逻辑宽度与窗口实际渲染缩放；宽度单位均为 DIP。
- `navigationWidth`、`navigationVisible`、`navigationCollapsed`、`preferredNavigationWidth`、`navigationMinWidth`、`navigationMaxWidth`：导航实际宽度、可见性、图标折叠状态、记忆的展开宽度及当前预算范围。
- `contentWidth`、`songPanelWidth`、`songPanelVisible`、`songMinimumWidth`、`artistPanelWidth`、`artistPanelVisible`、`lyricsPanelWidth`、`lyricsPanelVisible`：右侧内容及各面板实际布局；隐藏控件的旧 Bounds 不代表当前占用，须结合 visible 字段读取。
- `splitterWidth`、`splitterVisible`、`splitterDragging`：导航分割条实际命中宽度、可见性和拖动状态。
- `automaticWidthPending`、`lastMeasuredContentWidth`、`lastMeasuredFolderCount`、`automaticWidthResult`：最近一次用户展开后的可见目录测量，不包含目录名或路径；结果为 `none` / `cancelled` / `stale-expansion` / `grown` / `grown-to-limit` / `at-limit` / `already-fits`。计数只含祖先已展开的文件夹节点，包含分组，与 folderNavigation 的全树统计不同。
- `navigationOverflow`：原位浮显的兼容追加诊断，不含目录名或内容。`visible` / `pending: false` / `reason`、`naturalWidth` / `visibleTextWidth`、`wrapped`、`overlayWidth` / `overlayHeight`、`hitTestVisible: false`、`delayMs: 0`、`automaticReading` / `readingOffset`。兼容追加 `theme` / `background` / `foreground` / `contrastRatio` / `foregroundAdjusted`，记录最近一次实际主题和不透明合成后的配色及对比度，不含文字。只读取既有状态，不触发测量或浮显；查询不会显示或创建窗口。

导航内文件夹、播放列表、固定艺术家和内置分组共用名称/计数模板。展开导航中，仅被实际裁剪的名称在鼠标移入的同次 PointerMove 内立即原位浮显（无计时器延迟）；判定同时检查完整字形宽度、文本分配区和各祖先视口/裁剪区，深层缩进或整个名称宽度为 0 时可由悬停原行/图标触发。计数自身被截断且鼠标位于计数上时显示该计数。浮显只复制对应文本，图标及其他字段留在原行；字体链（含微软雅黑）、字号、字重、颜色、字距、有效透明度保持，背景按原控件 ActualThemeVariant 显式解析资源，再将实际祖先背景及选中/悬停色合成为不透明底色；Mica 使用对应主题的实色近似，避免混用默认浅色资源。原前景合成后对比度低于 3 时才选用可读的深/浅前景保护，字体不变。完整可见的文字不显示浮层；图标折叠模式保留原图标名称提示。

普通名称保持一行、对齐原文字起点；超长内容在客户端与当前显示器工作区的交集内换行，字体不缩小。只有完整文本换行后仍超过可用高度时，以原字号在透明命中的阅读视口内缓慢循环滚动，首尾留停顿，使任意长播放列表名也可读完。浮层无焦点、无鼠标捕获且不参与命中，点击/双击/右键/滚轮仍作用于原控件；鼠标移出原行、滚动、拖分割条、折叠、刷新、文字/容器变化、窗口 resize/移动/失活/关闭会清理浮层和阅读计时器。悬停不调整面板宽度、不改变选择或展开偏好。

使用：导航与右内容之间有细线和六 DIP 拖动区，悬停/键盘焦点有反馈，可左右反复调整。展开目录后按实际字体、图标、计数和层级适度增宽，只增长且受窗口 45% 及歌曲空间预算限制；收起不强制缩窄，手工拖动取消待处理请求，普通布局更新不会重新抢占宽度。临时定位展开与恢复不修改收起偏好、不自动增宽。设置按钮位于主窗口顶栏右侧，系统窗口按钮保留独立预留区。

导航展开最小宽度 160，歌曲区域至少 260（扣除左右边距后可用 244）；艺术家和歌词侧栏在小窗口分别可缩至 120 / 180，防止固定辅助宽度把主列表挤没。折叠为图标时仍保存展开宽度；0、负值、非有限值或非法 JSON 导航字段单独恢复为 230，过大值按窗口预算校正，不重置其余快照。有效自定义宽度保留；临时缩小窗口不覆盖记忆宽度，扩大后重新应用。

English: The visible six-DIP navigation splitter resizes both ways. Expanding a folder can grow the pane once using the current font and only expanded visible branches, bounded by the window and song-area budget. Collapsing does not shrink it; manual resizing takes priority. Settings is in the main top-right toolbar. Clipped navigation names reveal in place immediately in the same pointer move using the original typography and row colour; fully visible names do not. The backdrop resolves the source's actual theme explicitly and composites its ancestor/row colours onto an opaque theme base, including a solid approximation for native Mica. A contrast guard changes only an unreadable foreground. The overlay is click-through, never takes focus or resizes the pane, and closes on input/layout/lifecycle changes. Very long names wrap within the client/monitor work area; exceptional names taller than that area slowly scroll at the original font size. `mainLayout.navigationOverflow` reports existing state and last composited palette/contrast without names or side effects; `delayMs` is 0 and `pending` remains false for compatibility. The 22-tool catalog and empty diagnostics input schema remain compatible.

`MainWindowLayoutTests` uses actual Headless mouse down/move/up, real window scaling at 100/125/150/200%, JSON round trips, memory library/fake audio, and Skia renders in light/dark themes. No decoder or endpoint write is used. Set `JEEKMUSIC_TEST_ARTIFACTS` to save the four rendered layout PNGs; the test substrate substitutes for Windows Mica only during Headless rendering.

`NavigationOverflowTests` adds actual Headless hover/click/double-click/right-click/wheel, clipped ancestor viewports and zero-width text, Chinese/nested folders and English playlists, short/long names, count opacity, selected/unselected rows, 100–200% render scaling, lifecycle cancellation, text/container refresh, scroll and resizing, exceptionally long reading, and Skia visual captures. Tests await the complete async dispatcher operation and use memory library/fake audio only; no native playback or MCP bridge is required. `JEEKMUSIC_TEST_ARTIFACTS` also saves the overflow captures.

`get_diagnostics.artistList` 是兼容性新增，开发/正式版本同样提供。窗口未创建或已关闭时仅返回 `created: false`；查询不创建窗口或控件。窗口已创建后返回：

- `created`、`visible`、`artistCount`；`groups` 按显示顺序给出 `letter`、`firstIndex`、`count`，索引均为过滤后的零基艺术家位置，不计标题。
- `currentLetter`：视口顶部第一条仍可见的艺术家（含其组标题）的现有 `IndexLetter`，空表/尚无布局为 null。仅余 0.5 个逻辑像素及以下的行不计入顶部；末组无法顶齐时仍以实际顶部为准，不代表所选艺术家或上次点击目标。
- `firstVisibleIndex`、`lastVisibleIndex`：实际已布局且与视口相交的艺术家范围；无布局/空表为 -1。`realizedCount` 是虚拟化实现的容器数量，不是全库数量。
- `scrollOffsetY`、`viewportHeight`：Avalonia 逻辑像素；`positionPending` 表示索引点击后的有限布局校正尚未完成。隐藏期间不主动滚动/刷新可见范围，这些位置字段保留上次布局值；源变化清空旧位置，重新显示后按新布局更新。

工具数量仍为 21，`get_diagnostics` 输入 Schema 仍为空对象并拒绝额外参数，没有新增控制工具。Headless 测试直接调用注入模拟服务的内存 McpTools，不连接 stdio 或命名管道；游戏期间仍禁止任何可能冷启动的 MCP 桥接。

使用：艺术家栏按 A–Z / # 显示组标题；滚轮、触摸、拖动滚动条或键盘导航时查看顶部“当前字母 / Current letter”和右侧高亮。点击右侧字母尽量将组标题置顶；末尾内容不足一屏时，高亮显示真实顶部的组。索引跳转和普通滚动不会改变当前选择或播放。数字、符号与未知艺术家遵循现有规则归 #；搜索仍使用原搜索页，智能筛选会刷新艺术家及索引。

### 文件夹展开诊断

`get_diagnostics.folderNavigation` 是兼容性新增，开发/正式版本同一实现，无额外开关、无新增工具；当前共 22 个工具；该诊断的空对象输入 Schema 不变。查询只读取当前内存导航状态，不创建窗口、恢复选中、展开目录或启动音频。窗口尚未创建/已关闭时 `created: false`，策略和已载入的偏好统计仍可读取，树节点计数为 0。

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

`test-mcp.py` 默认只验证真实 EXE 路径/运行诊断、22 个工具、冷启动、stdio 关闭退出码及输出和正常退出，不发播放或输出切换命令。`--startup-only` 保留为默认行为的显式别名；`--sdk` 可叠加验证协议与参数 Schema。`--audio` 才额外验证静音 WAV、多客户端、中文/分页、曲库、播放列表、播放、队列、日志、错误校验、重启后应用静音恢复及共享输出；`--exclusive` 必须同时指定 `--audio` 才能打开真实独占设备。构建隔离脚本使用同样的显式选项规则，默认传入 `--startup-only`。

**静音 WAV / 应用静音仍会打开 WASAPI 设备，独占能中断游戏等其他程序声音。** 独立数据目录只隔离曲库和单实例，不隔离物理音频设备。默认启动诊断也会启动 GUI 并读取系统音频状态；用户要求禁止启动或正在处理游戏失声时，不运行任何 EXE/MCP（包括 `--startup-only`、`mcp_client.py` 的只读命令）。仅可编译/静态检查和确认使用模拟端点的测试。测试结束后只清理自己的播放器，不激活窗口或使用正式曲库；`show_window` 的激活行为不在后台测试中调用。


### 自动匹配音频格式

最终交互仅共享/独占模式及“共享模式自动匹配歌曲格式”开关；菜单中该开关缩进归于可直接选择的共享模式，独占单列，独占时共享开关保留选中偏好但禁用，不提供手动采样率、位深、声道列表。播放栏保留文件格式、共享/独占、已重采样和警告标记，其中采样率统一为“源采样率 → 实际设备采样率”，相同也显示两端；摘要区左键、右键及键盘激活均打开同一菜单，本身不更改输出；菜单只读列出歌曲和设备的采样率、有效位深/容器位深、声道与可靠布局，格式摘要菜单不含设备选择；输出设备按钮的完整菜单仍以系统默认设备和具体设备项开头，保留原有顺序和选择行为。模式/设备只在选择对应菜单项时改变。输出重建和不可读状态使用占位，不用请求值充当实际值。

推荐控制：

```json
{"automaticFormat":true}
{"automaticFormat":false}
{"exclusive":true}
{"exclusive":false}
```

开关开启后，共享模式按完整元组（采样率、样本类型、容器/有效位、声道数量、声道掩码）探测并选择，绝不将逐字段“支持”拼成未经驱动验证的组合。优先源采样率，再优先已知源布局，在可用组合中保留源精度：16 位整型有可行 16 位则使用；24/32 位整型优先不降低有效精度，24 有效位装在 32 位容器与真实 32 有效位明确区分。未知/有损/浮点源不编造原始 PCM 位深，默认优先设备可行的 24 位有效精度，必要时再转换并报告。

声道布局来自已加载解码器的明确 `audio-params/channels`，mono/stereo 可安全识别；5.1 与 5.1(side) 区分。首次加载保持静音/暂停，拿到可靠布局后若有必要，再切换设备并重开流；确认完成才继续播放。只知道“6 声道”等数量时不推断通用掩码，保留当前扬声器布局，标明布局未知和必要声道转换。已知源布局直接输出不可行时，使用设备当前布局并报告转换；原采样率不可行时保留可用率并明确报告重采样/精度降低。独占仍交给播放内核自动协商，多声道未知布局不强套固定掩码。

关闭开关仅停止后续自动匹配，不立即改变设备格式；本客户端已持有的临时格式仍在随后显式暂停/结束/释放时按所有权保护恢复。暂停时开启仅保存偏好，续播才匹配；进出独占不改写保存的共享开关。旧版无偏好默认为关闭；本轮未发布预览配置中的 auto 迁移为开启，manual/system 迁移为关闭，丢弃旧手动率，绝不据旧手动值写端点。本轮早先的 Desktop 工作流均未发布成功，因此新增的 sampleRateMode/sampleRate 手动控制参数未对外正式发行，最终 Schema 删除这些预览参数并拒绝它们；此前正式版 device/exclusive 调用保持兼容，这些输出参数变更不增加工具；当前随智能标签修正共 22 个工具。

共享自动格式是临时配置：每个物理端点首次实际写入前，保存当时真实 Windows 设备默认格式与混音格式的完整原始结构，不使用 factory default、硬编码 48k/16/2 或其他端点快照。显式暂停（界面、媒体键、快捷键与 MCP 共用业务）先静音并关闭流，恢复基线，保留歌曲/队列/CUE 精确位置；暂停 seek 只更新保存位置。续播重新读取当前基线并匹配，确认真实输出后继续。切歌和内部重建不恢复；自然队列结束、清空、正常退出、释放旧设备和进入独占恢复。独占本身不改共享格式，暂停仅释放其流。

恢复前设备与混音完整格式必须都等于本客户端最近成功写入值，否则保留外部修改并放弃旧基线；暂停期间的系统编辑成为下一次播放的新基线。每端点协作互斥租约阻止其他新版 Jeek Music 实例相互写入/回滚。恢复失败保留可重试基线并显示诊断，进程退出释放租约；异常崩溃/强制结束无法保证恢复。旧版本已经改过的历史原配置无法追溯，升级只从新功能首次写入前的实时配置捕获，不猜测旧默认。系统独立程序不遵循租约，检查与写入之间仍受 Windows 非公开接口的并发限制。

`get_output.audioFormat` 与 `get_diagnostics.output.audioFormat` 使用同一业务状态，开发与 NativeAOT 正式版一致：

- `displaySummary` 与播放栏完整文本一致；`resampled` 同时检查解码到客户端流及源到实际端点的采样率变化，输出重建时不提前宣称重采样；`hasWarning` 覆盖格式失败、转换/回退及输出安全门控，提示和右键保留详情。以上为兼容新增的查询字段，控制参数与工具数不变。
- `temporaryFormat` 包含 pausedOutputReleased、restoreStatus（none/active/restored/external-change/failed）、restoreReason、diagnostic、ownershipPolicy，以及 owners 中每个端点的 baselineDevice/baselineMix/lastAppliedDevice/lastAppliedMix。`get_status.idle` 现在也可表示已释放输出但仍保留歌曲/队列/位置的暂停状态。
- `automaticFormat` 为保存的共享偏好；`activePolicy` 为 windows-current/shared-automatic/exclusive-native。
- `source` 包括 sampleRate、bits（未知为 null）、precision、channels、channelMask、layout。bits 来自可靠整型元数据；precision 对已知有损编码为 lossy-not-applicable，已确认的 PCM 浮点源为 float32/float64，无可靠整数位深为 unknown，整数源为 integer-metadata。MP3 源显示“位深不适用（有损编码）”，无损/PCM 读取不到位深才显示“位深未知”；解码器 floatp 不会被冒充为原始浮点源或设备位深。
- `clientStream` 为 mpv 实际客户端流 rate/sampleFormat/channels，仅用于流诊断；float 不构成设备为 32 位的证据。
- `actualDevice`：共享使用独立 `IPropertyStore.PKEY_AudioEngine_DeviceFormat` 读回的真实端点 sampleRate/containerBits/validBits/sampleKind/channels/channelMask/layout；独占实际流率与声道可读，但播放内核不公开其最终有效位深/布局时为 null，UI 明确显示未知，不冒用共享默认值。
- `windowsDeviceDefault` 为 Windows 端点持久格式，`windowsMix` 为新 IAudioClient.GetMixFormat 读取的当前混音格式，二者与客户端流分开。
- `selected` 为自动策略选中且经过完整组合探测的目标；`supportedFormats` 为完整元组数组。范围是标准 8–768 kHz，另加源/当前率，只探测已知源布局和设备现有布局；Windows 没有通用的所有自定义采样率穷举接口。
- `applied` 表示目标元组已通过实际端点及混音率/布局读回、流重建完成且无格式失败；`conversion` 说明重采样、精度或布局回退，`diagnostic` 为写入/读回失败，`capabilityDiagnostic` 为接口/设备查询限制。不能把开关已选中当成已生效。
- 兼容摘要 `sampleRate` 保留 mode(auto/system/native)、requestedRate、actualRate、mixRate、applied 及能力率列表；完整位深与布局判断请使用 audioFormat。

共享写入通过 Windows 非公开 IPolicyConfig.SetDeviceFormat 实际修改端点与混音格式，影响同端点其他应用；随后独立读取文档化 PKEY 设备格式与新客户端混音格式验证。失败尝试恢复完整旧格式，保留可用播放并显示实际状态。每首共享自动歌曲都核对完整格式，包括相同采样率但不同位深/布局，避免预加载绕过匹配；自动模式不跨曲预加载，因此可能有短暂间隔。外部更改只更新诊断，不在监视循环中无限抢写；下一首或显式重新开启/切换模式时重新匹配。

真实验收曾确认：Windows 设置在页面已打开时可显示旧采样率，重新打开后与 44.1 kHz 端点读回一致；另一次 48 kHz 偏离的真实原因是独立硬件测试恢复旧格式，不能混同为页面缓存。已增加真实测试的其他客户端运行拦截，并使普通单元测试默认写入临时数据/日志目录，避免模拟设备日志污染用户日志。

验证命令（先关闭所有其他 Jeek Music 客户端；隔离数据并不隔离物理端点）：

```powershell
# 独立只读诊断，不启动客户端、不改变格式：
python desktop/tools/read-audio-format.py
# 显式真实硬件验证：共享 48/16 → 48/24 → 44.1/24 源（按驱动确认精度回退）→ 48/24 → 44.1/16，含暂停恢复完整基线/原位续播、关闭开关及独占返回：
python desktop/tools/test-sample-rates.py --exe output/JeekMusic.exe --audio --exclusive
```

脚本使用零值 PCM 和临时曲库，客户端/MCP结果与独立 Python Core Audio/PKEY 读取交叉核验。结束先关闭自己的流，检查客户端已恢复完整原端点/混音快照；失败清理仅在没有其他 Jeek Music 客户端且格式仍等于测试最后一次成功应用值时恢复，未知外部变更与新客户端均拒绝覆盖。结果在 desktop/artifacts/sample-rate-verification/wasapi.json。静音仍占设备；禁止音频测试时不得运行。可另用 JEEKMUSIC_TEST_REAL_AUDIO=1 显式执行受相同其他客户端运行检查约束的真实端点单元测试。常规 dotnet test 不修改设备。


本机 STAX S5 实测：精确驱动组合接受 44.1 kHz/16 位、48 kHz/16 位、48 kHz/24 位及 96 kHz/24 位；当前精确查询不接受 44.1 kHz/24 位或 24 有效位/32 位容器的 44.1 kHz 组合。程序在源率优先策略下对 44.1/24 源采用可用 44.1/16，并显示精度转换；48/24 源实际端点为 24 位，已用独立 Python IPropertyStore 和 IAudioClient 读回验证。同采样率 48/16 → 48/24 的自然换曲也通过，未被采样率相同的早退逻辑跳过。上述是此端点当次探测结果，不硬编码为所有设备的能力。


显示名称统一为 **Jeek Music**。初始化的 `serverInfo.title` 使用该显示名称，稳定协议标识 `serverInfo.name` 仍为 `JeekMusic`；工具 ID、参数 Schema、管道名、数据目录、EXE 名称及已有连接配置不变。
