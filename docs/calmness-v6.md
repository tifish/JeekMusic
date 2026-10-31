# 独立安静标签：规则 v6

> 本文保留 v6 引入第四维时的契约和历史验证；当前窗口规则已由 [v7](calmness-v7.md) 改进。维度、同步及手工优先兼容语义不变。

## 定义与边界

新增第四维 `CALMNESS`，值为 `CALM`（安静 / Quiet）或 `NON_CALM`（非安静 / Not quiet）。它描述音乐的平静听感，与运动 `MOOD`、人声 `VOICE`、古典 `GENRE` 独立组合；同一维最多一个标签。它不是系统音量、录音响度、纯音乐或某种曲风，也不是 `NON_SPORT` 的别名。

分类只使用已有 v5 `analysis_evidence` 的有效窗口。继续使用 v5 的 `ScoredSegments`：追加采样有一段失败时，整轮追加证据不参与分类，保留首轮结果。每个窗口满足以下三个条件才提供正向安静证据：

- 模型舒缓类分组 `calm >= 0.02`；
- 动感分组 `energy <= 0.08`；
- `energy <= 1.5 * calm`。

全体窗口正向比例至少 70%，每个片段至少 50%，且 `energy > 0.15` 的强动感窗口不超过 20%，才输出安静。完整可用证据未达到这些条件时输出非安静。舒缓分组含 sad/tender/lullaby 等代理类别，本身不是平静程度的直接标注；需要持续证据、绝对动感上限和片段约束共同判断。

缺完整 v5 证据、少于 3 窗、出现负/非有限分组分数，或所有窗 `calm + energy < 0.01` 时，新维保持无结果（null，不物化）。失败、旧版/未分析、签名过期记录同样不会默认变成非安静。最后一种情况表示模型没有足够可用的氛围信号，并不证明音频是静音。无结果不匹配任一新标签筛选；仍可人工选择标签。

这些阈值是保守、可解释、待校准的启发式，不是经过独立人工标注验证的概率或准确率。响度、BPM、曲风、人声和标题不参与此维；原运动三档 2.75/3.25/3.75 不变。`ClassicalTitles` 曲名/专辑名强制自动古典的优先级保持，所有维度最终由手工覆盖优先。

## 缓存、兼容与同步

- AnalyzerVersion 和证据 contract 继续为 **5**，仅 RulesVersion 升为 **6**。没有新模型、额外采样、音频上传或解码改造。Windows 启动重算物化标签，Android 规则版本检查重算；同签名的成功 v5 缓存直接生成第四维。既有终止失败的 3 次标记和重排条件保持。
- `CALM` / `NON_CALM` 特意不复用历史 `QUIET`。`Tag.of(MOOD, QUIET)` 始终返回 `NON_SPORT`；`Tag.of(CALMNESS, QUIET)` 无效。历史 `POP` 仍解释为 `NON_CLASSICAL`。旧的无维度 Windows 筛选 `QUIET`、Android `mood_filter` / `filter_mood=QUIET` 仍映射到非运动。
- 不重写旧覆盖值/时间戳，也不将旧“安静”迁为新维人工真值。Windows 在读取业务结果时按维度归一化旧值；原始同步记录保留。新筛选使用 Windows `CALM` / `NON_CALM` 列表项和 Android `filter_calmness` 独立偏好键。
- 现有数据库的标签/覆盖表以 `(track_key, dimension)` 为键，维度和值都是字符串，无需新增表列或修改历史迁移。已有特征和审核 JSON 同样无需迁移。
- 同步 format 2 增加可选 `extended_tag_overrides`，结构与 `tag_overrides` 相同。文件和 LAN 输出只把 MOOD/VOICE/GENRE 放在原字段，新维及未来未知维放在扩展字段；两端读入时恢复统一映射，按每项时间戳取新值。也接受早期把 CALMNESS 放在原字段的输入。扩展字段不能夹带原三维；非法值/时间戳拒绝整份输入。缺第四维的旧文件不删除已有新值；新维恢复自动的 null 同步记录能覆盖旧人工值。Android 本地恢复自动仍删除覆盖行，由同步 journal 生成 null，并确保变更时间大于已有该项时间戳（含快于本机时钟的对端）。未变更项保留时间戳。
- 旧客户端忽略扩展字段，不会把 CALMNESS 传入旧 Android 信息页的 `enum.valueOf`。新客户端仍保有自己的新维状态及同步文件，和旧端交换原三维时不会删除它；旧端自身不能显示、编辑或转存新维，完整新维同步需两端升级。新 Android 信息页同样忽略内部未知维度而保留其存储/同步数据。
- Android 修正、物化标签和审核记录现在在同一互斥锁/Room 事务内提交，任何失败回滚整次操作；Windows 继续复用原原子批量服务。AUTO 不重解码、不写音乐文件。审核历史仅为纠正记录，不自动当独立标注。

## 两端和 MCP

Windows 和 Android 的中英文标签、信息页、纠正/恢复自动、筛选组合/互斥/清除/持久化均接入四维。Windows 列表与导航计数继续复用 `ListBuilder`；Android 文件夹、子目录和艺术家 SQL 计数补齐第四维 AND 条件，列表则复用 allowedKeys 交集。播放高亮、选中色、行内容和截断浮层未改。

生产与开发 MCP 共用 `set_smart_tags.calmness`（`CALM` / `NON_CALM` / `AUTO`），返回 `tags.calmness` / `tagSources.calmness`。`explain_analysis` 返回第四维自动/最终/来源及 `calmness` 阈值、窗数、全局/片段支持和强动感比例。`search_tracks.tags` 可只读查询四维组合，不修改用户筛选。Schema、使用文档、真实客户端契约脚本已同步。

审核导出新增待标注 `CALMNESS: "?"`；独立分组评估遍历统一的四维定义。旧三维输入继续可用，新维计为 unknown，不推造人工真值。共享合成窗口用例覆盖持续/孤立信号、阈值边界、片段冲突、无信号和短采样；它们验证行为一致性，不能证明真实音乐准确率提高。

## 验证记录（2026-10-30）

- Windows Core 98 项、受影响 App 78 项通过；Android JVM 103 项通过。共享分类/同步用例由两端运行，包含旧 QUIET、新 CALMNESS 独立值和 AUTO 合并；新增同步协议用例验证扩展字段往返、旧三维隔离、未来维度、时间戳冲突、null 重置和畸形输入。Windows LAN 收件箱也经过同一协议解析器，专项测试覆盖扩展还原、坏输入拒绝及特征文件隔离。Python 分组评估 4 项通过。
- Android API35 / x86_64 模拟器原生 Room 11 项通过（DAO 8、历史迁移 3），覆盖规则升级复用缓存、手工覆盖/AUTO、物化写入失败回滚、审核、失败不重排、筛选计数/组合/清除和偏好持久化；不是 ARM 实机验证。
- 日常托管 EXE 和 Release NativeAOT 均通过 `test-calmness-mcp.py` 与既有 `test-smart-tags-mcp.py` 的真实客户端隔离曲库契约。两种构建的完整 MCP Catalog/Schema 逐项相同。新维测试包括旧 QUIET、缓存不变、无结果、组合计数、参数拒绝、手工纠正、AUTO、失败回滚、未变项时间戳；不播放音频。AOT 保留第三方 DataGrid 的既有 trim/AOT 警告。
- 实际 Windows 曲库 1939 首，在启动规则重算后物化 253 首安静、1655 首非安静；23 首完整 v5 因缺可用氛围信号保持无结果，另外 8 首失败/不完整不标为非安静。重启前后全部 `track_features` 内容散列及手工覆盖逐行相同：没有为新维重解码或改人工标签。原 `SPORT` 用户筛选不变。这是行为记录，不是人工质量成绩。
- Windows 原生 UIAutomation / Win32 实际操作隔离正式客户端：查看四组筛选菜单、安静与非运动组合（1 首）、清除、纠正为非安静（手动）、再次进入查看来源并恢复自动。已截图检查菜单、四维面板和来源。另有 headless 控件断言作为补充，不代替这些原生操作。
- Android 真实模拟器客户端通过 ADB/UIAutomator 实际操作：四组英文筛选、Quiet 与 Not workout 组合、清除、隔离 WAV 的手工 Quiet、信息页的手动来源、Auto (None) 恢复自动。组合筛选由 1 首变为 0，确认无结果不作为非安静；面板能滚动，现有选中样式保留。首次尝试时模拟器 system_server/InputManagerService 超时，UI 树为空且输入 ANR；保存日志后冷启动同一隔离 AVD（保留数据），重新安装最终 APK、重跑 11 项原生测试并完成上述 UI 操作。此阻断已解除，未用 headless 或 MCP 冒充手机 UI。

每次重启日常 Windows 客户端前重新读取实际状态（未沿用任务开始时的歌曲）：歌曲 1146，队列 15 首，暂停于约 138.78 秒。最终重启后位置为 138.776 秒，顺序/手工队列标志/当前曲/暂停状态保持，单次重启的持久化舍入差小于 2ms。最终进程 PID 30688；没有把测试用人工纠正写入实际曲库，测试用 Windows 客户端已退出，日常客户端留运行。

最终交付构建另复验了 Windows 安静与非运动组合（列表与导航均为 1 首）和四维纠正面板，截图 `calmness-windows-combined-delivery.png`、`calmness-windows-editor-delivery.png`。Android 最终 APK 重启后保留 Not workout + Not quiet，再由 UI 清除后恢复 1 首，截图 `calmness-android-persisted-final.png`、`calmness-android-cleared-final.png`。没有遗留 UI 验收阻断；Android 仍仅覆盖模拟器。

本地证据位于 `desktop/artifacts/calmness-*`（不提交包含用户路径的诊断）：构建/测试日志、两种构建 Catalog、隔离数据库、Windows 原生截图以及实际曲库只读重启前后快照。
