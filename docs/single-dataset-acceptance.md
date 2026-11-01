# 单一学习数据集交付验证

2026-11-01。此次验证使用隔离数据目录和专属 Android 模拟器；清除、旧库升级、离线并发及重放场景均在 fixture 中完成。生产升级只允许修改个人标签学习专用数据库及其派生产物，不删除普通曲库或设备数据。

## 自动化与实际 MCP

- Windows App：672 通过、2 跳过（实际音频端点读写测试需专门环境），合计 674；Core：107 通过；Python 数值协议：9 通过。
- Android：113 项 JVM 测试通过，Debug 构建和 lint 通过；API 35 x86_64 模拟器中 5 项真实 SQLite/Room instrumentation 测试通过；Release R8 构建通过。
- 真实 managed / NativeAOT 客户端均通过 `desktop/tools/test-single-learning-client.py`。覆盖首次进入、唯一默认数据集、弃用多数据集调用拒绝、四维材料不足、学习专用清除后重新使用、普通库不变、日常修正到学习的修订与撤回、已手工标注筛选、列控制与标题必显、播放状态不变。
- 两种客户端的 38 个 MCP 工具描述与 Schema 逐项相同。NativeAOT 实际诊断 `dynamicCodeSupported=false`。发布只保留既有 DataGrid IL2104/IL3053 警告。
- 数据层测试覆盖 schema 1/2 一次升级、新数据重开保留、边界并集、离线两端初始化、并发冲突、重复事件、撤回、清除后旧离线事件不复活、日常来源与独立答案隔离、四维隔离，以及普通手工覆盖/库表保全。
- 固定宽松覆盖旧 NORMAL/STRICT、追加采样、重算和解释。规则版本 9 复用有效分析缓存；人工覆盖、其他三维和 ClassicalTitles 保持原业务规则。
- 手工筛选覆盖任一维 MANUAL、混合 AUTO/MANUAL、手工值等于自动、撤回最后一维、批量和来源/搜索组合；筛选发生在完整集合分页/虚拟化之前。独立盲测答案不进入普通列表。

## 最少真实交互与双端同步

Windows NativeAOT 实际鼠标连续切换表头两列，原菜单始终打开、勾选与列同步；Esc 和点击外部均关闭。歌曲右键实际连续修正两维、恢复 AUTO 后列表因筛选变空，再修正仍保存到打开菜单时的原两首；同一个菜单保持打开，第一列为单行当前值/混合值，Esc 关闭。截图已实际查看。

证据在 ignored 目录 `desktop/artifacts/single-dataset-acceptance/`：`header-continuous-native.png`、`quick-continuous-native.png`、对应 UI 树及 MCP 交互报告。初期首屏、保存、切维、材料不足和清除确认截图也保留；设置页后来折叠补充标注区域，不能用初期截图声称最终管理布局全部视觉验收。

真实 Windows AOT 服务与 Android 前台主动 LAN 请求已完成协议 3 交换；读取两端专用数据库确认清除边界和 6 个日常修订/撤回当前头一致。`real-active-sync-report.json` 保存结果；另外实际 MCP `lan_sync_status.bytesServed=0`，没有为本次学习验证传输音频。测试使用既有配对和一个映射，临时连接端口在交付前恢复。

## 限制与交付证据

Android 实际设备是 `emulator-5584 / JeekLanEmpty35`，不是实体手机；未验证 ARM/OEM 特有行为。自动化业务验证不冒称完整真实 UI 验收，依用户后续要求优先 MCP，仅对菜单保持打开等必要行为做最小实际操作。未进行全库音频提取、下载模型或训练用户数据；个人模型仍仅候选，自选反馈不声明独立准确率。

实际版本、提交、包 SHA256/签名、生产客户端动态 checkpoint 与保全重启结果写入新交付目录的 `handoff.json`。旧归档不覆盖。生产重启前重新读取最新队列、位置、播放状态、筛选和输出状态，不能恢复早先测试快照。私有数据库备份与配对令牌不提交到 Git。
