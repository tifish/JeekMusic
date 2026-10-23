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
| `playback` | `action`：`resume` / `pause` / `toggle` / `next` / `previous` / `seek`；`seek` 另需 `seconds`，相对于当前分轨起点。上一首遵循播放超过 3 秒先回到开头的规则 |
| `get_queue` | 支持分页，返回实际播放顺序、手动条目标记、当前位置 |
| `edit_queue` | `action`：`add` / `next` 另需 `trackIds`，`remove` / `jump` 另需 `index`，`move` 另需 `index` 与 `toIndex`，`clear` 清空。空队列添加歌曲会开始播放，与界面一致 |
| `set_play_mode` | 至少提供 `shuffle` 或 `repeat`（`off` / `all` / `one`） |
| `set_volume` | 至少提供 `volume`（0–100）或 `muted`。共用共享/独占音量持久化；独占模式可能改变 Windows 设备音量 |
| `get_output` | 无参数。WASAPI 设备列表、当前设备/独占模式、文件与设备的音频格式 |
| `set_output` | 至少提供 `device` 或 `exclusive`。`device` 来自设备列表；`auto` 跟随系统默认设备。设置持久化，模式切换可能重建音频输出 |
| `list_playlists` | 支持分页。播放列表 ID、UID、名称和时间戳 |
| `get_playlist` | `playlistId`，支持分页。按手动顺序返回条目，保留不可用条目 |
| `edit_playlist` | `action`：`create` 需 `name`；其余需 `playlistId`。`rename` 需 `name`，`delete` 删除列表，`add` 需 `trackIds`，`remove` 需 `positions`，`move` 需 `positions` 和 `target`（移动前的目标位置，可等于条目数）。触发正常持久化及同步事件，不删除音频文件 |
| `list_folders` | 无参数。曲库文件夹 ID、名称、完整路径和可用状态 |
| `add_folder` | `path` 为存在的绝对目录；如果包含已添加的子目录，需显式提供 `replaceChildren: true` 才会替换。添加后排队扫描 |
| `remove_folder` | `folderId`。排队移出曲库，保留音频文件 |
| `rescan_library` | 可选 `full`，默认增量扫描；`true` 重建曲库 |
| `get_diagnostics` | 无参数。应用/协议版本、进程 ID、数据/程序/日志目录、引擎状态、最近一次播放错误、状态和音频输出详情 |
| `get_logs` | 可选 `kind`（`app` / `playback`）、`lines`（默认 50、最大 200）；读取日志尾部最多 64 KiB，不接受任意文件路径 |
| `show_window` | 无参数。显示并激活主窗口 |

播放、seek 和音频输出由 libmpv 异步完成；返回的状态是命令执行后的即时快照，应再次调用 `get_status` 确认实际播放状态。扫描、添加/移除文件夹通过现有后台曲库队列执行，`queued: true` 只表示已入队。通过快照版本、扫描状态、搜索结果和日志确认结果；扫描失败原因也进入正常应用日志。

成功的工具调用同时返回 `structuredContent` 对象和对应 JSON 文本 `content`，便于程序及通用 MCP 客户端消费。工具参数/业务错误返回 `isError: true`；未知工具或 JSON-RPC 方法、协议格式错误返回 JSON-RPC 错误，不会因此终止正常会话。

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

# 对正式 Native AOT 包运行同样的端到端检查
python desktop/tools/test-mcp.py --exe desktop/artifacts/publish/JeekMusic.exe --sdk
```

端到端脚本自动使用临时数据目录和静音 WAV，验证冷启动、stdio 关闭、多客户端、中文/分页、曲库、播放列表、播放、队列、日志、错误校验、保留活动连接时正常退出和重启后设置恢复；结束后只清理它自己的测试播放器。脚本不激活窗口，也不使用正式曲库。`show_window` 的激活行为不在后台测试中调用。
