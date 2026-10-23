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
| `search_tracks` | `query` 可省略或留空以浏览全库；支持界面相同的标题、艺术家、专辑、文件名和拼音首字母搜索；可按 `folderId`、`artistKey`、`albumKey` 同时筛选；支持分页。结果包含 `artistKeys`、`albumKey` |
| `get_track` | `trackId`。详细元数据、音频文件路径、稳定键和智能标签，包括 CUE 分轨 |
| `play_tracks` | `trackIds` 数组（1–1000 项）、可选 `startIndex`（默认 0）、`shuffle`。先校验全部歌曲可用，再按指定列表播放；与界面一样保留待播的手动队列 |
| `playback` | `action`：`resume` / `pause` / `toggle` / `next` / `previous` / `seek`；`seek` 另需 `seconds`，相对于当前分轨起点（绝对位置，不是增量）；需已加载可 seek 的歌曲且输出已就绪，否则返回业务错误。保留播放/暂停；终点限制在曲尾前 1s（不足 1s 的歌曲为 0），避免直接 seek 到 EOF；超出时长仍返回参数错误。上一首遵循播放超过 3 秒先回到开头的规则 |
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
| `get_diagnostics` | 无参数（input Schema 仍为空对象，拒绝额外参数）。应用/协议版本、进程 ID、实际 EXE 路径 `processPath`、运行框架 `frameworkDescription`、架构 `processArchitecture`、是否支持动态代码 `dynamicCodeSupported`，以及原有数据/程序/日志目录、引擎状态、最近一次播放错误、状态和音频输出详情 |
| `get_logs` | 可选 `kind`（`app` / `playback`）、`lines`（默认 50、最大 200）；读取日志尾部最多 64 KiB，不接受任意文件路径 |
| `show_window` | 无参数。显示并激活主窗口 |

播放、seek 和音频输出由 libmpv 异步完成；返回的状态是命令执行后的即时快照，应再次调用 `get_status` 确认实际播放状态。扫描、添加/移除文件夹通过现有后台曲库队列执行，`queued: true` 只表示已入队。通过快照版本、扫描状态、搜索结果和日志确认结果；扫描失败原因也进入正常应用日志。

成功的工具调用同时返回 `structuredContent` 对象和对应 JSON 文本 `content`，便于程序及通用 MCP 客户端消费。工具参数/业务错误返回 `isError: true`；未知工具或 JSON-RPC 方法、协议格式错误返回 JSON-RPC 错误，不会因此终止正常会话。

上述四个运行诊断字段为兼容性新增，开发版与正式版同时提供；NativeAOT 的 `dynamicCodeSupported` 为 `false`，启用 AOT 兼容开关的托管构建也可能为 `false`，不能单凭此字段判断发布类型。它们用于核对连接到的实际程序，不能将框架描述字符串当成部署类型枚举。工具数量仍为 21，参数 Schema 无变化。

### 音量输出语义

原 `get_status.volume` 保留数值字段，表示端点读数（不可读时为 0 占位，**不能把它当作真实值**）；`muted` 只表示应用静音。`get_status.output` 和 `get_output.volumeState` 提供同样的详细对象：

- `semantics: "endpoint-master"`、`muteScope: "player"`。
- `actualDeviceId`：绑定给 WASAPI 的物理端点 ID；`device: "auto"` 仍表示用户选择跟随默认。
- `endpointVolume` / `endpointMuted`：设备当前读数，不可读为 `null`；不等于应用静音。
- `volumeAvailable`：当前能否写端点音量。`hardwareVolume` / `hardwareMute`：硬件支持，未知为 `null`。独占无硬件音量时端点软件音量不影响实际响度，不能以滑块数值声称独占响度。
- `sessionVolume` / `sessionMuted`：本进程默认会话的 Windows 合成器状态，未知为 `null`；`sessionPolicy: "preserve-external-block-exclusive"`。正常新会话为 100%，已有用户衰减/静音保留；请在合成器中恢复后再进入独占。
- `hardwareVolumeConfirmationRequired`：独占会绕过软件音量或软件静音时，用户需先用物理旋钮确认安全音量，再调用 `playback` 的 `resume` 或点击播放。切换设备/模式会重新要求确认；此确认不能绕过读取错误或会话衰减。
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
