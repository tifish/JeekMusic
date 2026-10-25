# 局域网同步候选验收记录

基线 `154408ff724228d561d85fba46a6c9ed43c652ac`，测试数据全部为隔离合成文件。实现契约和限制见 [lan-sync.md](lan-sync.md)。本轮不 push、不发布，由父任务独立审查后决定发布。

## 已核验

| 项目 | 实际证据 |
|---|---|
| Windows 真实 TLS 服务授权 | 未配对、错误邀请、单次邀请重放、越根、未共享根、未计划文件和撤销设备均拒绝；`desktop/tools/test-lan-sync.py` |
| Android 实际 UI 配对/SAF | 在独占 JeekLanSync35、API 35/x86_64 上操作配对框、根列表、Karajan/Celtic 选择、估算、系统目标选择器和系统授权；截图 01–04 |
| 选择与下载 | 原始 6 个选择文件约 50 MB，逐文件 SHA-256、大小独立一致；未选 Unselected/Private.wav 不存在；`baseline.json` |
| 断线续传 | 主动停用 Windows 服务，手机保留 1,835,008 字节 partial；重试后该大文件完整 hash 一致；`partial-after-disconnect.txt`、截图 06–08 |
| 内容/时间独立变化 | PC WAV 改一个 PCM 字节、保持 64044 字节和原 mtime，下一轮检测并更新；另一 WAV 仅 mtime 减少一小时，hash 不变且目标时间更新；`mutations.json`、`after-content-and-time-update.json` |
| 本地冲突 | 手机 WAV 内容被外部改动，仍保留同 size/mtime；同步保留外部 hash 并报告冲突，PC 源未变；`conflict-before.json`、`conflict-preserved.json`、截图 11 |
| 时间精度及权限 | 4 个音频最初微秒可表示时间精确一致；CUE/LRC 的 400 ns 尾数差异可见。关闭特殊时间权限后，SAF 下载仍成功、显示时间错误；恢复权限重试补正，读回与请求差 700 ns；`saf-only-download.json`、`mtime-after-permission-retry.json`、截图 19 |
| CUE/附属文件 | 本地 CUE 展开两首；另一个选中子目录的 CUE 引用根内未选 SharedNew/Disc.wav，连同 cover.png、歌词下载；邻近 NotRequired.wav 未下载。明确绑定在正常扫描中显示 Sibling One/Two，8 条实际曲库记录；`android-identity-and-tags.json` |
| 两个同名根/同一 SAF 父目录 | 两个 Music 根、同名相对 WAV 分别落入不同 rootId 命名空间，目标 hash 各自匹配且互不相同；`two-roots-no-overwrite.json`、截图 13–15 |
| 用户数据往返/部分曲库引用 | PC 新建含两个根同名歌曲的播放列表、手动标签；手机只有第一根时另一项保持不可用；下载第二根后两条引用分别可用，标签对应正确根。手机通过 UI 重命名为 Phone_Merged，PC 正常合并收到；`user-data-roundtrip.json`、`android-identity-and-tags.json`、截图 12、15 |
| 播放/预加载避让 | 播放 CUE One 时修改 PC 整轨 mtime；手机拒绝触碰 PlayingFiles 当前/预加载文件；媒体会话保持 CUE One、PLAYING、位置继续前进；`playback-before-sync.txt`、`playback-after-sync.txt`、截图 16 |
| 取消与清理 | 实际点击取消得到“Cancelled/保留断点”；明确确认清理第二根后仅删除本任务下载、PC 源仍存在；重新启用可重新下载；`cleanup-owned.json`、截图 17–18 |
| 中英文 | 实际 Android 英文交互及中文映射选择界面截图检查，目标 URI、命名空间和保存/取消可见；截图 20–21 |

证据目录为仓库未跟踪的 `desktop/artifacts/lan-sync/`，不提交合成音频、AVD、设备数据库或配对凭证。配对邀请只直接送入当时聚焦的手机输入框，截图均在凭证清空之后。部分早期错误截图保留作为诊断，不能视为成功证据；最终逐文件 JSON及当前界面截图用于结论。

## 自动检查

- Core：90 通过（含 Windows junction、越根 CUE、跨选择依赖、路径/Unicode 碰撞、same size/mtime hash、root 引用严格匹配）。
- Windows App：544 通过、1 个既有物理音频端点测试跳过；最后可选服务惰性证书初始化改动后，LAN 两个相关测试及真实 TLS 授权回归再次通过。
- Android JVM：23 个套件、87 通过；Debug 构建通过。Release 的 R8、lintVital 及本地签名构建通过；Release APK 运行交互不能据 Debug 验收推断。
- MCP 兼容扩展至 24 个工具，严格 Schema/描述测试通过；旧工具输入未改变。NativeAOT 另作本地构建/真实服务核验，最终结果在候选交接中注明。

## 阻断与未验收

- Windows 原生 Computer Use 实测 `native pipe ... os error 2`，真实 Windows UI 交互及截图仍未验收。没有用 Headless 或 MCP 冒充此项。
- 模拟器 Qt 宿主窗口两次报 `Failed to load opengl32sw`，使用无宿主窗口的完整 Android 系统，通过实际 App UI、系统文件选择器和截图验收；宿主原生窗口本身未验收。
- 模拟器蜂窝旧栈出现 TLS 记录损坏；启用 WiFiPacketStream 并真正连接 AndroidWifi、停用模拟蜂窝后完成传输，未关闭证书校验。
- ARM64/OEM、物理 SD 卡、真实目标满盘、杀进程覆盖每个提交点、真实外部引擎并发写入均未验收。故障恢复、空间检查和冲突保护已实现，但不能把相应代码路径等同这些实测。
- 自动发现、后台调度、公网、通用双向音频合并和块差分不在本候选能力内；首版为手动局域网连接、前台发起同步。

日常用户曲库、恢复 mtime 的音乐和恢复备份没有用于本功能源/目标或测试初始化。日常客户端重启前通过其即时 MCP 读取最新队列/来源/位置并备份当时 settings；不从旧快照覆盖应用数据。重启实际结果保存在同目录的日常状态保护证据中。
