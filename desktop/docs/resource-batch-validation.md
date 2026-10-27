# 当前列表全部封面 / 歌词：实现与验收记录

日期：2026-10-27。任务：在 Windows 歌曲列表菜单提供“获取全部封面”“获取全部歌词”。

## 菜单与范围

- 位置：歌曲列表标题栏的“更多”菜单，保留“播放全部”“全选”等现有项目。两个新增项目有当前列表歌曲数及已有资源优先的 tooltip。原歌曲右键“获取缺失歌词 / 封面”继续作用于选中项。
- 目标取 `MainViewModel.Rows` 的完整稳定结果，不枚举已实现的 DataGridRow，不取 SelectedItems，也不自动扩成整个曲库。遵循文件夹/包含子文件夹、艺术家/固定艺术家、专辑、播放列表、搜索及智能标签筛选。搜索保留原先“前 200 首”列表上限；无法匹配本机歌曲的播放列表条目不参与。
- 菜单创建时捕获歌曲数组及来源标题，进入批量入口时在任何确认/异步工作之前再固定数组；确认等待中或执行后切换列表不会改动任务目标。列表正在重算、空列表或只有缺失条目时禁用，已有批量任务时禁用两个入口并显示“取消批量获取”。

## 业务与 MCP

- `ResourceBatch` 只负责范围、去重、单任务保护、进度与取消。顺序调用原 `OnlineFeature.Fetch(ignoreMiss: true)`，后台准备去重集合，每 32 个同步命中项至少让出一次 UI。选中项、设置页全库及 MCP 共用保护，包括等待启用/批量确认期间。旧进度窗口关闭只取消自己的任务，不能误取消后继任务。
- “全部”扩大目标范围，不强制下载/覆盖。封面先查本地/缓存，按专辑去重，无专辑按歌曲；沿用现有隐藏目录/同名图片/嵌入保存和播放文件避让。既有补齐流程可能作用于同专辑、当前列表外的成员。找到本地/缓存歌词则跳过，不因本次获取迁移或嵌入本地歌词。保存配置及后台迁移规则未改变。
- 进度分开显示目标歌曲数和去重后单位数：封面是专辑/无专辑歌曲单位，歌词是歌曲单位。已处理只计完成项；跳过/未匹配不算异常失败。转后台、取消/关闭、完成提示沿用现有交互。取消不回滚已完成写入，不可中断写入完成前保持忙碌。
- 新增 `fetch_resources`：start/status/cancel；start 使用 covers/lyrics 与 current_list/track_ids，后者支持 1–1000 ID；current_list 不受分页截断（搜索自身的 200 首列表语义除外）。在线未开启时 MCP 报错，不弹框、不启用、不改保存偏好。status/cancel 只接受 action，状态/来源/范围/单位计数与当前列表诊断可读。`get_diagnostics.resourceBatch` 共用状态，原接口参数兼容。UI 选中项和设置全库任务的诊断 scope 为 selected/library。
- 中英文菜单/提示、MCP 工具描述/Schema/注解、使用文档、现有工具目录计数及真实 MCP 回归脚本均已同步；Debug/Release/NativeAOT 使用同一实现，无开发专用接口。

## 验证结果

- `ResourceBatchTests` 使用真实 Headless 菜单和虚拟化 DataGrid、内存数据库、fake 引擎/端点。验证两个菜单点击均捕获全部 260 行而非一项选中或少量已实现行；确认期间切源不改范围；选中项获取仍是一首；搜索上限、智能标签筛选、文件夹递归、艺术家、专辑、播放列表成员/重复/缺失、空/重算列表禁用；UI/MCP 单任务、取消、进度、专辑去重和 3000 项任务让出 UI；旧窗口取消不干扰后继任务。
- 隔离 FLAC fixture 及本地 JPEG/LRC 验证：本地歌词获取不嵌入/修改音频；封面隐藏目录、同名图、内嵌三种保存策略；同专辑两首仅一个获取单位；已有资源不记网络未命中；刷新快照后的再次获取不改文件。未启用任何网络来源，未访问用户媒体。
- 相关回归 145 项通过，补充歌词面板目录兼容检查 1 项通过；最终专项 63 项通过（与前述有重叠）。覆盖 MCP 会话/Schema、本地化格式参数、播放来源、当前曲可见性/配色、歌曲与导航截断浮层、字体和现有诊断等路径。未把总数重复相加。
- 普通 Debug build：0 警告/错误。Release win-x64 NativeAOT publish 成功，Avalonia.Controls.DataGrid 12.1.2 报 IL2104 / IL3053 裁剪与 AOT 分析告警。
- `test-resource-batch-mcp.py` 对开发与 NativeAOT 实际 EXE 均通过：临时数据目录、离线歌曲及预置歌词缓存，验证 stdio/命名管道、去重/跳过、状态/完成后取消、无效范围/空列表/在线关闭/Schema、缓存/保存偏好/空队列未变。
- `test-mcp.py --startup-only --catalog ...` 对两种 EXE 均通过；25 工具的完整目录、描述、Schema 与 annotations 完全一致。无播放/输出切换命令。
- `git diff --check` 通过。原始结果留在本机忽略目录 `D:/MyOpenSource/JeekMusic/desktop/artifacts/resource-batch-validation/`：`resource-batch-final.trx`、`resource-catalog-compatibility.trx`、`resource-batch-targeted-final.trx`、两种 catalog 与 `catalog-comparison.json`。早期失败结果保留供诊断，不作为最终通过依据。

## 真实 UI 验收阻断与重启

本轮读取 computer-use 技能后初始化 `@oai/sky`，开始时及新版运行后分别调用 `sky.list_apps()`，均返回：

```text
Computer Use native pipe is unavailable: failed to connect native pipe: The system cannot find the file specified. (os error 2)
```

无法取得真实窗口对象，因此没有继续使用猜测坐标/句柄。**真实客户端菜单呈现、禁用视觉、鼠标/键盘交互及截图均未验收；没有真实截图文件。** Headless/MCP 仅为补充验证，不能据此宣称真实 UI 通过，也没有把功能测试交给用户。

旧客户端 PID 29256 已结束，新版 `D:/MyOpenSource/JeekMusic/output/JeekMusic.exe` PID **5012** 保留运行并通过 MCP 显示主窗口。重启前后读取完整状态/队列：当前歌曲 ID 1174、258.83 秒（差值 0）、暂停、1612 项队列逐项歌曲 ID/manual 标记、队列索引、随机/循环均一致。新实例 resourceBatch 为 idle/busy=false。证据为同目录 `before-restart.json`、`after-restart.json`、`restart-verification.json`；没有对用户当前列表/曲库发起批量获取，也未更改其保存选项。

主要改动：`UI/MainWindow.Online.cs`、`MainWindow.Menus.cs`、`ViewModels/MainViewModel.cs`、`Online/ResourceBatch.cs`、`OnlineFeature.cs`/`.Worker.cs`、`AppServices.cs`、`Automation/McpTools.cs`、`Strings/en.json`、专项测试/脚本与 MCP 文档。
