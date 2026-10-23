# JeekMusic 桌面端技术设计文档

> 状态：草案 v0.1 · 2026-10-20 · 需求见 `requirements.md`，技术验证见 `tech-spike.md`

> 2026-10-21：D1–D6 的主要实现已接入。本文保留原设计与验证计划；实际已验证范围、性能数据和未验收项以 [validation.md](validation.md) 为准。

和移动端相同的规则（标签解析、艺术家拆分、CUE、歌词、排序、智能标签分类、队列）只写桌面端的做法和区别，规则本身见移动端设计文档（`mobile/docs/design.md`，下文简称"移动端设计"）。

## 1. 技术栈

| 层 | 选型 | 说明 |
|---|---|---|
| 语言 / 运行时 | C# 14、.NET 10，**Native AOT** | 启动快、不需要安装运行时；代码里不能依赖运行时反射（序列化、绑定、COM 都用源生成器） |
| 界面 | **Avalonia 12** + Fluent 主题 + Avalonia.Controls.DataGrid | 验证见 `tech-spike.md` 第一部分 |
| MVVM | CommunityToolkit.Mvvm（源生成器） | `[ObservableProperty]`、`[RelayCommand]`；绑定全部用编译绑定（`x:DataType`） |
| 播放 | **libmpv**（P/Invoke，验证工程里的 `Mpv.cs` 发展而来） | 验证见 `tech-spike.md` 第二部分；正式发布用自己编的只含音频的 LGPL 版（§15.3） |
| 系统集成 | CsWinRT 投影（SMTC）；`[GeneratedComInterface]`（任务栏 `ITaskbarList3`）；`LibraryImport`（Win32） | 都兼容 AOT |
| 数据库 | SQLite：Microsoft.Data.Sqlite + SQLitePCLRaw（e_sqlite3） | 手写 SQL 和映射，不用 EF Core（启动开销大、AOT 支持有限） |
| 标签读取 | **ATL.NET**（MIT），备选 TagLib#（LGPL） | 要支持 APE、WMA、TAK、WavPack；D1 用和移动端相同的测试素材验证（§5.3） |
| 智能标签 | ONNX Runtime（MIT）+ 从 `yamnet.tflite` 转换的 `yamnet.onnx` | 只在分析时加载（§10） |
| 序列化 | System.Text.Json 源生成（`JsonSerializerContext`） | 设置、同步文件、在线接口 |
| 网络 | `HttpClient` | 在线歌词、封面、检查更新；第一次用到时才创建 |
| 测试 | xUnit + Avalonia.Headless | §16 |
| 构建 | `dotnet` CLI、GitHub Actions（`windows-latest`） | §15 |

## 2. 整体架构

```
┌─────────────────────────── 界面（Avalonia，UI 线程）───────────────────────────┐
│ MainWindow：标题栏（搜索、筛选）│ 导航栏 │ 艺术家栏 │ 歌曲表格 │ 歌词面板 │ 播放栏 │
│ MiniPlayerWindow │ DesktopLyricsWindow │ SettingsWindow │ TrayIcon            │
│        ViewModel（CommunityToolkit.Mvvm）——只读 LibrarySnapshot，向服务发命令          │
└──────────────┬──────────────────────────────┬──────────────────────────────────┘
               │                              │
┌──────────────▼───────────────┐  ┌───────────▼───────────────────────────────────┐
│ 曲库                         │  │ 播放                                          │
│  LibraryStore（SQLite）       │  │  PlayerService：QueueController（移植）        │
│  LibrarySnapshot（内存，只读）│  │   └ MpvEngine：libmpv（事件线程）              │
│  Scanner + FolderWatcher     │  │  OutputDevices / 独占模式 / 音量              │
│  TagReader（ATL）+ 规则（Core）│  │  SmtcBridge、TaskbarBridge、TrayBridge        │
│  SyncService（§11）          │  │  SleepTimer、QueuePersistence                  │
│  AnalysisService（§10）      │  └───────────────────────────────────────────────┘
│  Lyrics / Covers / Online    │
└──────────────────────────────┘
┌──────────────────── 平台（Win32）────────────────────┐
│ 全局快捷键、窗口消息钩子（WM_HOTKEY / WM_DEVICECHANGE / │
│ WM_POWERBROADCAST / 任务栏按钮）、单实例、回收站、电源 │
└──────────────────────────────────────────────────────┘
```

- **单进程**。UI 线程只做界面；mpv 有自己的事件线程，事件转到 UI 线程（`Dispatcher.UIThread.Post`）再更新 ViewModel；扫描、分析、同步在后台线程，最低优先级
- **不用依赖注入容器**：`AppServices` 在启动时按需创建各个服务（启动快、AOT 友好）。重对象（mpv、ONNX Runtime、HttpClient）都是第一次用到时才创建
- **曲库数据的流向**：SQLite 是持久化；启动后把整个曲库读进内存成为不可变的 `LibrarySnapshot`（5 万首约 15–20MB），界面上所有的列表（文件夹、艺术家、搜索、筛选、排序）都在内存里算，不查数据库。扫描、同步改了数据库后，生成新的快照替换旧的（写时复制），界面收到通知后重算当前列表

### 工程结构

```
desktop/
├── JeekMusic.slnx
├── src/
│   ├── JeekMusic.Core/        与平台无关的规则和模型（net10.0，不引用 Avalonia 和 Windows API）
│   │   ├── Rules/             CharsetFixer、ArtistSplitter、FileNameInferrer、CueParser、LrcParser、
│   │   │                      SortKeys、SongSorter、TagClassifier、ClassicalTitles（从 Kotlin 移植）
│   │   ├── Queue/             QueueController（队列的两部分、随机、重新生成，移植）
│   │   └── Sync/              同步文件格式和合并算法（§11）
│   └── JeekMusic.App/         WinExe：界面、数据库、扫描、播放、平台功能
│       ├── UI/                Views（.axaml）、ViewModels
│       ├── Library/           LibraryStore、LibrarySnapshot、Scanner、FolderWatcher、TagReader
│       ├── Playback/          MpvEngine、PlayerService、OutputDevices、SleepTimer、QueuePersistence
│       ├── Platform/          Smtc、Taskbar、Tray、Hotkeys、SingleInstance、Power、RecycleBin
│       ├── Analysis/          AnalysisService、PcmSource、AudioTagger（ONNX）
│       ├── Online/            歌词、封面来源（移植移动端 §8.4）
│       └── Update/            检查更新、下载、替换
├── tests/
│   ├── JeekMusic.Core.Tests/  规则测试，读 ../shared/testcases/ 的共用用例
│   └── JeekMusic.App.Tests/   Avalonia.Headless：ViewModel 和界面交互
├── tools/                     libmpv 编译脚本、YAMNet 转换脚本
├── spike/ spike-audio/        技术验证工程（D2 之后删掉）
└── docs/
shared/                        （仓库根目录）两端共用
├── testcases/                 规则的测试用例（JSON），移动端和桌面端的单元测试都读
└── pinyin/                    拼音表（§6.4）
```

## 3. 两端共用的东西

| 内容 | 位置 | 说明 |
|---|---|---|
| 规则的测试用例 | `shared/testcases/*.json` | 自然排序、排序键和字母索引、拼音、繁简、乱码修复和编码识别、艺术家拆分、文件名推断、专辑键、CUE、LRC、目录树、m3u、备份引用、在线结果匹配、曲名补救、智能标签分类，共 16 个文件，覆盖移动端原有单元测试的全部断言（说明见 `shared/testcases/README.md`）。移动端的 JUnit（`SharedCases.kt`，路径由 `testOptions` 的系统属性 `jeek.testcases` 传入，用 org.json 解析）和桌面端的 xUnit（`SharedCases.cs`）都读这些文件，之后两端只维护一份。`desktopOnly` 的用例（反斜杠路径）只在桌面端跑 |
| 拼音、繁简表 | `shared/pinyin/` | §6.4 |
| 同步文件格式 | §11 | 两端都按这里实现，格式变化要同时改两端 |
| YAMNet 类别和分类阈值 | `tag-classifier.json` 的用例；类别分组在 Core 的 `YamnetScores`（移植移动端 `AudioTagger.scores`） | 模型文件各端放自己的格式（tflite / onnx） |

**正则的差别**：移动端在手机上运行时 `java.util.regex` 由 ICU 实现，`\s`、`\d`、`\b` 按 Unicode 匹配，和 .NET 的默认行为一致，所以移植时原样保留这些写法；但移动端在 JVM 上跑单元测试时 `\s`、`\d` 只匹配 ASCII，共用用例不依赖这个差别（不放全角空格、全角数字这类输入）。

CI：`mobile.yml` 和 `desktop.yml` 的触发路径都加上 `shared/**`，改了共用用例两端都要跑测试。

## 4. 数据模型（SQLite）

数据库文件 `%LOCALAPPDATA%\JeekMusic\library.db`，WAL 模式。结构和移动端（移动端设计 §3）基本一致，区别在路径和同步字段：

```
library_folder
  id              INTEGER PK
  volume_id       TEXT           -- 本地盘："{卷 GUID}"（GetVolumeNameForVolumeMountPoint）；网络路径："unc"
  path_in_volume  TEXT           -- 卷内路径，如 "\Music\"；网络路径为完整 UNC 路径
  last_root       TEXT           -- 上次解析到的完整路径，如 "E:\Music\"（盘符变了会更新）
  display_name    TEXT
  available       BOOLEAN        -- 所在的盘当前是否可用（需求 L-8）
  added_at        INTEGER

track
  id              INTEGER PK
  track_key       TEXT UNIQUE    -- volume_id + "|" + 卷内路径（小写）；CUE 虚拟歌曲再加 "#" + 两位音轨号
  folder_id       INTEGER FK → library_folder
  dir             TEXT           -- 相对于音乐文件夹的目录，"\" 分隔，以 "\" 结尾；根目录为 ""
  file_name       TEXT
  -- 标签相关的列同移动端：title_tag、title、album、album_artist、album_key、artist_raw、artist_display、
  --   artist_inferred、track_no、disc_no、year、genre、duration_ms、codec、bitrate、sample_rate、
  --   bits_per_sample、has_embedded_lyrics、has_embedded_cover、cue_track_no、start_ms、end_ms
  mtime           INTEGER        -- 文件的修改时间（UTC ticks），增量扫描用
  size            INTEGER
  sort_title      TEXT           -- 排序键在扫描时算好存起来（拼音转写较慢），启动时不用再算
  sort_file       TEXT
  sort_artist     TEXT
  INDEX(folder_id, dir)

artist、track_artist、track_features、track_tag   -- 同移动端

-- 以下是用户数据，参与多设备同步（§11），每行有 updated_at（UTC 毫秒）
playlist
  id              INTEGER PK
  uid             TEXT UNIQUE    -- 同步用的稳定 ID（新建时生成 UUID；从旧备份读入的见 §11.5）
  name            TEXT
  updated_at      INTEGER
playlist_item     -- 同移动端：playlist_id、position、track_key
tag_override      -- 同移动端，加 updated_at；value 为 NULL 的行表示"改回自动"，也要同步
lyrics_offset     -- 同移动端，加 updated_at
nav_item          -- 导航栏顺序（对应移动端的 pinned_tab）：type、ref、position
sync_meta         -- 键值：nav_updated_at、rules_updated_at
tombstone         -- 删除记录：kind（PLAYLIST）、uid、deleted_at；保留 180 天
sync_history      -- 被同步覆盖的旧版本（需求 SY-5）：kind、uid、content（JSON）、replaced_at、from_device；保留 30 天

queue_item、lyrics_cache                          -- 同移动端
```

- **`track_key` 用卷 GUID 而不是盘符**：移动硬盘、U 盘每次插上的盘符可能不同，用卷 GUID 时播放列表、分析结果、手动修正都不受影响。启动和收到 `WM_DEVICECHANGE` 时，用 `GetVolumePathNamesForVolumeName` 把卷 GUID 解析成当前的盘符，更新 `last_root` 和 `available`
- 路径比较一律不区分大小写（Windows 的文件系统不区分），`track_key` 里存小写；显示用 `dir` + `file_name` 的原始大小写
- 孤儿清理、`track_key` 的取舍同移动端设计 §3
- 结构迁移：`PRAGMA user_version` 记版本，每个版本一段迁移 SQL；从第一个公开版本起不能用"删库重建"代替迁移

### 4.1 设置和其他文件

都在 `%LOCALAPPDATA%\JeekMusic\`：

| 文件 | 内容 |
|---|---|
| `settings.json` | 所有设置（System.Text.Json 源生成）。曲库规则那一组参与同步（§11） |
| `snapshot.json` | 启动快照（§12）：窗口位置和大小、导航栏选中项和展开状态、表格列宽、滚动位置、正在播放的歌 |
| `device.json` | 本机的设备 ID（安装时生成）和设备名（默认是计算机名） |
| `covers\` | 封面缩略图缓存（按专辑） |
| `logs\` | `app.log`、`playback.log`（同移动端 P-14 ③的播放日志），各保留最近 5MB |

## 5. 曲库扫描

### 5.1 扫描流程

```
1. 解析音乐文件夹的当前路径（卷 GUID → 盘符），不可用的跳过并标记 available = false
2. 枚举文件：Directory.EnumerateFiles + EnumerationOptions { RecurseSubdirectories, IgnoreInaccessible,
   AttributesToSkip = Hidden | System }，跳过 .jeekmusic 目录；只要支持的扩展名和 .cue
3. 和数据库比较 (track_key → mtime, size)：新增、变化的进入解析队列；不再存在的删除
4. 解析：Parallel.ForEachAsync，并发数 = 核心数 / 2，线程优先级 BelowNormal
   TagReader 读标签 → CharsetFixer → ArtistSplitter / FileNameInferrer → 算排序键
   .cue 交给 CueResolver（规则同移动端设计 §4.6）
5. 每 500 首一个事务写入数据库；全部完成后重建 artist 表、生成新的 LibrarySnapshot
```

- 估算（5 万首，SSD）：枚举 1–2 秒；解析每首 2–5ms，8 核开 4 个线程约 30–60 秒，满足 3 分钟的目标。增量扫描只有第 2、3 步，几秒内完成
- 进度（已解析数 / 总数）显示在设置的曲库页和状态栏（需求 L-7）

### 5.2 监听文件变化（FolderWatcher）

- 每个可用的音乐文件夹一个 `FileSystemWatcher`（`IncludeSubdirectories = true`，`InternalBufferSize = 64KB`）
- 事件先合并：同一路径 2 秒内的多次变化只处理一次（复制大文件时会连续触发 Changed）；之后对涉及的目录做增量扫描
- 缓冲区溢出（`Error` 事件）时，整个文件夹做一次增量扫描
- `.jeekmusic\sync\` 下的变化不进曲库，转给 SyncService（§11）
- 网络共享文件夹的监听不一定可靠（需求 L-9）：监听出错时不重试，只靠启动时和手动的扫描
- 盘被拔掉时 watcher 报错 → 把文件夹标为不可用（`WM_DEVICECHANGE` 也会通知），歌曲行保留；插回后恢复监听并做一次增量扫描

### 5.3 标签读取（TagReader）

- 首选 ATL.NET：MIT 许可，纯托管代码，支持 APE、WMA、TAK、WavPack、DSF；只读不写（写封面、歌词时也用它，见 §9）
- **D1 验证**（和移动端 M0 一样）：用 `mobile/tools/pick_library_sample.py` 挑出的真实曲库子集和 `push-testdata.sh` 生成的合成素材，对比 ATL 和移动端 TagLib 读出的结果（标题、艺术家多值、专辑艺术家、音轨号、内嵌歌词、内嵌封面、CUESHEET、时长、采样率、位深），并测 5 万首的解析耗时。不满足就换 TagLib#
- GBK 乱码：ID3v2.3 里按 ISO-8859-1 存的 GBK 字节，读出来的字符串每个字符就是一个字节（Latin-1 解码是无损的），`CharsetFixer`（移植）还原成字节再按 GBK 解码。.NET 里要先 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 才有 GBK / Big5 / Shift-JIS
- 多值艺术家：ATL 把多值字段用分隔符连接成一个字符串（可以配置分隔符），设成 `\u0000`，和移动端的 `artist_raw` 一致

### 5.4 文件操作

规则同移动端设计 §4.7 和需求 F-10。Windows 上：

- 删除：`SHFileOperation`（`FO_DELETE` + `FOF_ALLOWUNDO`）移到回收站，同名 `.lrc`、图片一起
- 改名、移动：`File.Move`；跨盘移动时先复制再删除；保持修改时间（复制后 `File.SetLastWriteTimeUtc`）
- 操作前暂停 FolderWatcher 对这些路径的处理，操作完直接更新数据库（播放列表等按 `track_key` 一起改），不等监听事件
- "在资源管理器中显示"：`explorer.exe /select,"<路径>"`

## 6. 浏览与界面

### 6.1 主窗口结构

```
MainWindow（Mica，标题栏扩展到客户区）
├── 标题栏：后退 / 前进、搜索框（Ctrl+F）、筛选按钮（智能标签，同移动端 M-6）、窗口按钮
├── NavPane（左，可以折叠成图标）
│   ├── 文件夹：TreeView，根一级是音乐文件夹（需求 W-3）
│   ├── 艺术家
│   ├── 播放列表（可以展开，列出所有播放列表；可以作为拖放目标）
│   └── 固定的艺术家（可以拖动排序）
├── ArtistPane（只在选中"艺术家"时出现）：ListBox + 字母索引
├── 内容区：TrackTable（DataGrid）/ 专辑页 / 搜索结果
├── LyricsPanel（右，可以拉出、拉宽、切换成整页）
└── PlayerBar（底）
```

- **导航历史**：内容区的每次切换（选文件夹、艺术家、专辑、搜索）压入历史栈，标题栏的后退 / 前进、鼠标侧键、Alt+← / → 都可以用；历史不持久化，只记住最后的位置（快照）
- 窗口位置和大小在显示之前就从快照恢复，避免先在默认位置出现再跳过去；保存的位置不在任何一块显示器上时（显示器拔掉了）放回主显示器

### 6.2 列表的计算

每个视图的歌曲列表都是同一条管线，全部在内存里做，结果交给 DataGrid：

```
来源（文件夹 / 含子文件夹 / 艺术家 / 专辑 / 播放列表 / 搜索）从 LibrarySnapshot 取出歌曲
 → 智能标签筛选（同移动端设计 §5.5）
 → 排序（SongSorter，规则同移动端设计 §5.8；排序键已存在数据库里，§4）
 → List<TrackRow>
```

- 5 万首的筛选 + 排序约 20–50ms，在后台线程算，算完回到 UI 线程替换 `ItemsSource`；新的计算开始时取消上一次
- 播放队列的"来自列表的部分"（移动端 P-15）用的是同一条管线，保证队列和界面上看到的顺序一样
- 文件夹树和每个文件夹的歌曲数（含子文件夹、按筛选）在生成快照和筛选变化时一次算好

### 6.3 歌曲表格（TrackTable）

- DataGrid，`IsReadOnly`，行虚拟化；列定义（显示哪些、宽度、顺序）存在快照里
- **排序由我们自己做**：处理 DataGrid 的 `Sorting` 事件并设 `Handled = true`，改全局排序设置后重算列表；表头的排序箭头自己画（DataGrid 自带的箭头只反映它自己的排序）
- 正在播放的行：TrackRow 有 `IsPlaying` 属性，行样式绑定它（同移动端 U-11）
- 拖放：从表格拖出时数据是 `track_key` 列表（同时提供文件路径，拖到资源管理器就是复制文件）；播放列表视图里可以拖动调整顺序（DataGrid 不自带行拖动，自己处理指针事件，显示插入线）
- 右键菜单：内容同移动端 U-2；作用于所有选中的行

### 6.4 拼音和繁简

移动端的排序键、拼音首字母搜索用 Android ICU 的 `Transliterator("Han-Latin; Latin-ASCII")`（`SortKeys.kt`），在线结果比较用 `Traditional-Simplified` / `Simplified-Traditional`（`ChineseText.kt`）。.NET 没有转写功能，所以在 Core 里复现（`Text/Pinyin.cs`、`Text/ChineseText.cs`），需要的表由 `shared/pinyin/Generate.java` 用 ICU4J 76.1（和 Android 16 的 ICU 同版本）导出，`shared/pinyin/generate.sh` 下载 jar 并运行。生成时会核对 ICU 规则的结构，升级 ICU 版本后规则变了会直接报错。

**ICU 的拼音转写实际是这几轮**（从 ICU 的规则源码 `toRules()` 读出来的）：

1. `Han-Spacedhan` 前两轮：对 CJK 标点、表意文字、汉字做 NFKC，然后**全角转半角**（对整个字符串，片假名也会转成半角片假名，"シャルル" → "ｼｬﾙﾙ"）。逐字查表 `spacedhan-pre.tsv`（1455 条）
2. `Han-Spacedhan` 第三轮：19 条中文标点映射（`。` → `.`、`、` → `,`、`「` → `‘`、`《` → `«`、`々` → `⓶` 等，`spacedhan-punct.tsv`），和两条插空格的规则：
   - 前一个字符是汉字、句读标点（`. , : ; ? !` 及全角形式）或收尾括号、引号，**后一个是字母（汉字也算字母）**时插入空格
   - 前面是字母（可以跟附加符号），**后一个是汉字或开括号、开引号**时插入空格
   同一轮里标点映射排在前面：字符有映射时直接替换，不在它前面插空格（所以 "a《b》" 不插空格）；判断时前文看已经输出的结果，后文看原字符。所以 "周杰伦" → "zhou jie lun"，"周杰伦Jay" → "zhou jie lun Jay"，"1周" → "1zhou"，"晴天.mp3" → "qing tian. mp3"，"G.E.M." → "G. E. M."（**西文里的点号后面也会插空格**，排序键就是这样，两端一致）
3. `Han-Latin`：4 条多音字上下文规则（"藏文" zàng、"重庆" chóng、"沈阳" shěn、"秘鲁" bì，`han-latin-context.tsv`），然后逐字查带声调的拼音（`han-latin.tsv`，44437 个字，含扩展 B 等补充平面）
4. `Latin-ASCII`：只处理拉丁、通用、继承文字（`sets.tsv` 的 `LatinAsciiFilter`，其余字符原样保留并把文本分段）：NFD，去掉紧跟在拉丁字母或数字后的非间距附加符号，NFC，再逐字查表（`latin-ascii.tsv`，846 条：ō → o、ß → ss、Æ → AE、« → <<、© → (C) 等）

判断"字母""附加符号""表意文字"等用的字符集合也从 ICU 导出（`sets.tsv`），不依赖 .NET 自己的 Unicode 数据版本。另有 `Han` 集合（文字属性），代替移动端的 `Character.UnicodeScript.HAN`（`CharsetFixer`、`CoverMatcher.hasCjk`）。

**繁简**：`traditional-simplified.tsv`（4177 条）、`simplified-traditional.tsv`（3845 条）按 ICU 的规则顺序列出词组和单字，少数带上下文（数字后面的"只"转"隻"、"周"后面跟数字转"週"等 7 条）。匹配同 ICU：在每个位置按顺序尝试以这个字开头的规则，第一条原文、前文、后文都对得上的生效。移动端的 `toSimplified` 在没有汉字（按 UTF-16 码元判断）时直接返回原文，桌面端照搬，所以只含扩展区汉字的文本不转换。

**验证**：语料是作者曲库（`D:\Mobile\Song`，1609 个文件）的文件名、目录名、歌名、艺术家、专辑共 6646 条，加上 11 万条随机混排串（汉字含扩展区和兼容区、假名和半角片假名、谚文、带附加符号的拉丁字母、分解形式的组合符号、全角字符、中英文标点、各种空白、多音字和繁简词组）。在共 117011 条上，C# 的结果和 ICU4J **完全一致**（拼音、繁转简、简转繁都是 0 条差异）。代表性的 442 条进了 `shared/testcases/pinyin.json`、`chinese-text.json`（`shared/pinyin/build-testcases.py` 生成），两端测试都跑；完整语料不进仓库，桌面端的 `PinyinCorpusTests` 在设置了环境变量 `JEEK_PINYIN_CORPUS` 时对比。

**体积和耗时**：表一共约 600KB（嵌入 Core 程序集），第一次使用时加载：拼音表约 25ms、繁简表约 10ms（JIT 版）；转写一次约 10 微秒。排序键还是在扫描时算好存进数据库（§4），启动时不用加载表。

### 6.5 智能标签筛选、搜索、专辑页

规则同移动端设计 §5.4–5.6，都在内存里算。搜索：每首歌预先算好"标题 / 艺术家 / 专辑 / 文件名"的小写和拼音首字母，存在快照里；5 万首逐个匹配约 10ms，输入时防抖 150ms。

## 7. 播放层

### 7.1 MpvEngine

- 一个 libmpv 实例，**首帧之后**才创建（加载 libmpv 和初始化约几十毫秒，不放在启动的关键路径上）
- 选项：验证工程的 `Mpv.BaseOptions()`（`config=no`、`video=no`、`volume=100`、`replaygain=no` 等，见 `tech-spike.md`），加上 `ao=wasapi`、`gapless-audio=weak`、`prefetch-playlist=yes`、`idle=yes`
- 观察的属性：`pause`、`idle-active`、`duration`、`playlist-pos`、`audio-params/*`、`audio-out-params/*`、`current-ao`、`audio-device-list`。**不观察 `time-pos`**：它每秒变几十次，界面需要进度时每 250ms 读一次，窗口不可见时不读（验证时观察它让 CPU 到了 12%）

### 7.2 队列和 mpv 的播放列表

队列的逻辑（两部分、随机、跟着来源列表重新生成，移动端设计 §7.5）由 `QueueController`（移植到 Core）负责，**mpv 的播放列表里只放"当前这首 + 下一首"**：

- 开始播放一首：`loadfile <当前> replace`，再 `loadfile <下一首> append`；mpv 会提前打开下一首（`prefetch-playlist`），自动换歌时无缝衔接
- 收到 `playlist-pos` 变化（自动播到了下一首）：通知 QueueController 前进一首，删掉 mpv 播放列表里已经播完的那条，再追加新的"下一首"
- 队列重新生成、用户插入"下一首播放"时，如果"下一首"变了，就替换 mpv 播放列表里的第二条（`playlist-remove 1` + `loadfile ... append`），当前这首不受影响
- 这样队列可以很长（整个曲库），mpv 里始终只有两条；随机、循环也都在 QueueController 里实现（单曲循环用 mpv 的 `loop-file=inf`）

### 7.3 CUE 虚拟歌曲

- `loadfile <整轨文件> append -1 start=<秒>,end=<秒>`
- **结束时间比分轨点早 1 微秒**，否则分轨点上的那个样本会在前后两首里各播一次（验证结论）；开始时间用分轨点本身
- CUE 的时间是 1/75 秒的帧，换算成秒时用 `decimal` 算，不用 `double` 累加

### 7.4 输出设备、独占模式、音量

- **设备列表**：mpv 的 `audio-device-list` 属性（WASAPI 设备 ID 和名称），设备增减时属性会变；选中的设备存设备 ID，设成 `audio-device`。默认值 `auto` 就是跟随 Windows 的默认设备，mpv 自己会在默认设备改变时切过去（验证日志里的 `Monitoring changes in device`）
- **指定的设备断开**：`audio-device-list` 里没有它了 → 暂停并提示（需求 O-2）
- **独占格式选择**：对已知采样率和声道数的单 / 双声道歌曲，先用 `IAudioClient::IsFormatSupported(EXCLUSIVE)` 查询原采样率下可用的样本格式，再把 `audio-format` / `audio-samplerate` 作为 `loadfile` 的文件级选项传入；当前歌曲和预加载下一首各自协商，切回共享时不附加这些选项。16bit 源文件优先保持 16bit；其他音频优先选设备在原采样率支持的浮点或高位深 PCM，仅在原采样率不支持高位深时选择 16bit。例如设备支持 44.1kHz/16bit、48/96kHz/24bit 时，44.1kHz 的 MP3 使用 44.1kHz/16bit，避免 mpv 自动协商到 96kHz/24bit。查询失败、元数据不全、多声道或原采样率不支持时仍由内核协商，播放栏继续显示实际输出和重采样状态。
- **独占模式**（需求 O-3）：切换时记录当前歌曲内的位置和播放状态，先静音、暂停并销毁旧引擎，按新的 `audio-exclusive` 设置重建引擎，再用 `loadfile` 的 `start` / `end` 重新解码原歌曲（保留 CUE 偏移和边界）。新引擎始终以暂停、静音状态启动；文件加载完成后恢复 `ao-volume` 并读回验证，才恢复原来的静音和播放状态。恢复失败保持暂停、静音并提示；旧引擎或旧条目的延迟事件不允许解除保护。
- **音量**（需求 O-4）：用 mpv 的 `ao-volume` 属性，它直接调输出端的音量：共享模式下是本程序在 Windows 音量合成器里的会话音量，独占模式下是设备的端点音量。mpv 自己的 `volume` 始终是 100，不做软件音量。两种模式的音量分别持久化为 `sharedVolume` / `exclusiveVolume`，切换时恢复目标模式上次的设置，滑块随之同步；离开模式时保留外部合成器调低后的实际音量。尚无记录的模式（含旧版本迁移）以原音量为参考，不高于新输出的既有音量，确认成功后才建立记录；重载中的后端值不能覆盖已保存的记录。新输出的音量不可用、写入失败或读回不符时，保持暂停和静音，不按默认 100% 出声。睡眠定时的渐弱值随引擎重建保留
- **系统主音量恢复**：独占模式的设备音量也会改变 Windows 系统主音量，因此仅恢复共享会话音量不能恢复原来的响度。在独占输出首次修改每个设备前，通过 `IAudioEndpointVolume` 保存该设备的原系统主音量；独占引擎完全关闭后，按原设备 ID 写回并读回验证，再启动共享播放。待恢复的记录持久化为 `sharedDeviceVolumes`，重启独占输出不能覆盖旧记录；恢复成功后移除，下一次进入独占时重新捕获用户最新设置。正常退出软件时也在关闭引擎后恢复，最后写入设置。恢复失败保留记录并暂停播放，不修改程序会话音量、系统静音或声道平衡。
- 播放栏显示的格式：`audio-codec-name`、`audio-params/samplerate`、`audio-params/format` → `audio-out-params/samplerate`、`audio-out-params/format`；两边采样率不同时提示"已重采样（系统混音）"

### 7.5 系统集成

- **SMTC**（`SmtcBridge`）：用主窗口的句柄取得（`SystemMediaTransportControlsInterop.GetForWindow`，验证过）。歌名、艺术家、专辑、封面（`DisplayUpdater.Thumbnail`，用封面缓存文件）、播放状态，以及时间线（`UpdateTimelineProperties`，只在换歌、跳转、暂停时更新，不随进度刷新）。按钮事件转给 PlayerService
- **任务栏**（`TaskbarBridge`）：`ITaskbarList3` 的缩略图按钮（上一首 / 播放暂停 / 下一首，图标是嵌入的 .ico）和进度（`SetProgressValue`，每 2 秒更新一次，窗口最小化时也要更新，暂停时显示为暂停状态）。按钮点击通过窗口消息钩子（`Win32Properties.AddWndProcHookCallback`）收到 `WM_COMMAND`
- **托盘**：Avalonia 自带的 `TrayIcon` + `NativeMenu`（需求 PC-1）；悬停提示在换歌时更新

### 7.6 续播、睡眠定时、出错

- **续播**：同移动端设计 §7.5，队列存 `queue_item` 表，当前位置每 10 秒和暂停、退出时写入。启动后 mpv 创建好时，以暂停状态加载当前歌曲并跳到上次的位置（`loadfile ... start=<秒>` + `pause=yes`），SMTC 显示为暂停，按播放键就接着放
- **睡眠定时**：同移动端设计 §7.6；渐弱是在最后 30 秒线性降低 mpv 的 `volume`（这是唯一允许软件音量的情况），停止后恢复 100
- **出错**：`end-file` 的原因是错误时记日志、提示，跳到下一首；连续 3 首都失败时停止（需求 O-7）

## 8. PC 专属功能

### 8.1 关闭到托盘、单实例

- 主窗口的 `Closing` 事件里：设置为"关闭到托盘"时取消关闭并隐藏窗口；隐藏后停止所有界面刷新（§13）
- **单实例**：启动时创建命名互斥量 `Local\JeekMusic-<用户 SID>`。已经存在时，把命令行参数（要打开的文件）通过命名管道 `\\.\pipe\JeekMusic-<SID>` 发给正在运行的实例，然后退出；发送前调用 `AllowSetForegroundWindow(ASFW_ANY)`，这样正在运行的那个可以把自己的窗口带到前台（这是用户自己的操作，不算抢焦点）

### 8.2 全局快捷键

- `RegisterHotKey`（`MOD_NOREPEAT`），注册在主窗口上（窗口隐藏到托盘时句柄还在，照样收到 `WM_HOTKEY`）
- 注册失败（被别的程序占用）时记下来，设置页里标红"已被其他程序占用"
- 修改设置后先 `UnregisterHotKey` 再重新注册

### 8.3 迷你播放器（MiniPlayerWindow）

- 独立的小窗口：封面、歌名、艺术家、进度条、上一首 / 播放暂停 / 下一首、回到主窗口按钮；可以设为总在最前（`Topmost`）
- 和主窗口二选一：切换时隐藏主窗口；位置存在快照里
- 用同一个 PlayerViewModel，所以状态天然一致

### 8.4 桌面歌词（DesktopLyricsWindow）

- 无边框（`SystemDecorations = None`）、透明背景（`TransparencyLevelHint = Transparent`）、`Topmost`、不进任务栏、显示时不激活（`ShowActivated = false`）
- 文字带描边，在任何背景上都看得清；双语歌词两行；没有歌词时隐藏窗口
- **锁定**：给窗口加上 `WS_EX_TRANSPARENT | WS_EX_LAYERED`（`SetWindowLongPtr`），鼠标点击穿透到下面的窗口；解锁从托盘菜单或全局快捷键（锁定后窗口本身点不到）。未锁定时鼠标悬停显示工具条（字号、锁定、关闭），可以拖动位置
- **只在换行时重绘**：根据 LRC 的时间点算出下一行的时刻，用一次性定时器等到那时再换，不按帧刷新；暂停时取消定时器，跳转后重新计算
- 窗口位置按显示器记（拖到另一块显示器后按那块的缩放显示）

## 9. 歌词与封面

规则同移动端设计 §8 和封面相关的部分，代码从 Kotlin 移植：

- LrcParser、CharsetDetector（BOM → 严格 UTF-8 → GBK → Big5 → UTF-8 替换）移植到 Core，用共用用例测试
- 在线歌词、封面的来源和匹配规则移植移动端设计 §8.4；HTTP 用 `HttpClient`，JSON 用源生成
- 写入音乐文件（用户选择的保存位置）用 ATL；**正在播放和已经预加载的歌不写**（同移动端 P-14 ④，先存隐藏目录，播放器不再用这个文件后再写）。在同一文件系统的 `.jeekmusic/tag-writes/` 创建副本，封面和歌词合并保存成功后原子替换；保留修改时间。音频内容不变时同步更新现有分析结果的文件签名，避免重复分析
- 歌词面板（LyricsPanel）：规则同移动端设计 §8.6（当前行居中、手动滚动后暂停跟随、拖动出现定位线）；面板不可见时不跟随进度

## 10. 智能标签

### 10.1 解码（PcmSource）

和移动端设计 §6.2 相同的取样方式（30%、50%、70% 处各 10 秒，短于 40 秒的整首）和同样的下混、抽取代码（移植，保证两端得到同样的 16kHz 输入）。解码用 libmpv：

- **方案 A**：每个分析线程一个单独的 mpv 实例，`ao=pcm`、`ao-pcm-file=\\.\pipe\jeekmusic-pcm-<n>`（我们创建命名管道的服务端，mpv 把样本写进来）、`audio-format=float`，`loadfile ... start=,end=` 只解码需要的片段。不写临时文件（5 万首的临时文件会有几百 GB 的写入）
- **方案 B**（方案 A 不行时）：自己编 libmpv 时把 FFmpeg 编成单独的 DLL（§15.3），分析直接调 libavformat / libavcodec
- **D5 验证（2026-10-21）**：已采用方案 A。管道输出原始采样率和声道的 float WAV，由 C# 使用与移动端一致的等功率下混和 box 抽取；44.1kHz 双声道逐样本测试通过。详细模型对比见 [validation.md](validation.md)

### 10.2 模型（AudioTagger）

- `yamnet.tflite` 用 tf2onnx 转成 `yamnet.onnx`，转换脚本在 `desktop/tools/`；用校准集检查转换前后的得分一致
- ONNX Runtime 只在分析时加载，当前批次结束或暂停后立即释放（`InferenceSession.Dispose`），推理线程不空转
- 得分之后的分类规则（`TagClassifier`、`ClassicalTitles`）在 Core 里，和移动端共用测试用例

### 10.3 调度（AnalysisService）

- 有待分析的歌、并且满足条件时才运行；条件不满足时等待通知，**不轮询**
- **电源**：`RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE)`，接上 / 拔掉电源时收到通知；台式机没有电池（`GetSystemPowerStatus` 的 `BatteryFlag` 为 128），视为一直接着电源
- **全屏时暂停**：`SHQueryUserNotificationState` 返回 `QUNS_BUSY`、`QUNS_RUNNING_D3D_FULL_SCREEN`、`QUNS_PRESENTATION_MODE` 时暂停；分析运行中每处理完一首检查一次
- 并发：一个最低优先级工作线程，ONNX 内外部并发均为 1；线程用 `THREAD_MODE_BACKGROUND_BEGIN`（CPU 和磁盘 IO 都是最低优先级）
- 分析结果写数据库，并按 §11.4 共享给其他设备；开始分析一首之前先看其他设备有没有同一版本的结果

## 11. 多设备同步（SyncService）

需求 SY-1 至 SY-9。移动端要按同样的格式改（移动端 B-7）。

### 11.1 文件

- 位置：**每个**音乐文件夹下的 `.jeekmusic\sync\`（和移动端的备份一样，每个文件夹都写一份全量数据，拷走哪个文件夹都能恢复）
- 本机的文件：`<设备 ID>.json`（用户数据）和 `<设备 ID>.features.json`（分析结果）。只写自己的，读所有的
- 写入：先写 `<名字>.tmp`，再 `File.Replace` 替换（同时留下 `.prev`，需求 B-6）。同步工具（Syncthing 等）都是写临时文件再改名，所以读到的总是完整的文件；万一解析失败，跳过，等下一次变化

```json
{
  "format": 2,
  "device": { "id": "6f0c…", "name": "书房电脑", "platform": "windows", "app_version": "1.0.120" },
  "written_at": 1792000000000,
  "rules": { "updated_at": 1791990000000, "value": { "artist_separators": [";", "/"], "artist_exceptions": ["AC/DC"],
             "infer_from_file_name": true, "file_name_format": "ARTIST_TITLE", "legacy_charset": null } },
  "nav": { "updated_at": 1791990000000, "items": [ { "type": "FOLDERS" }, { "type": "ARTIST", "ref": "周杰伦" },
           { "type": "ARTISTS" }, { "type": "PLAYLISTS" } ] },
  "playlists": [ { "uid": "b1d2…", "name": "跑步", "updated_at": 1791990000000,
                   "items": ["周杰伦/叶惠美/03. 晴天.flac", "Live/2004.flac#05"] } ],
  "deleted_playlists": [ { "uid": "a9e3…", "deleted_at": 1791980000000 } ],
  "tag_overrides": { "周杰伦/叶惠美/03. 晴天.flac": { "MOOD": { "value": "SPORT", "updated_at": 1791990000000 } } },
  "lyrics_offsets": { "周杰伦/叶惠美/03. 晴天.flac": { "offset_ms": -500, "updated_at": 1791990000000 } }
}
```

- 歌曲引用：相对于音乐文件夹的路径，`/` 分隔（两端一致；Windows 上读写时转换），CUE 虚拟歌曲加 `#NN`。同移动端设计 §3.1
- 时间：UTC 毫秒

### 11.2 合并

```
读入其他设备的文件（同一个设备在几个音乐文件夹里都有时，取 written_at 最新的）：
  播放列表：按 uid 对应
    本机没有：本机没有它的删除记录、或删除记录早于对方的 updated_at → 新建
    对方的 updated_at 更新 → 本机的旧版本存进 sync_history，用对方的名字和歌曲替换，updated_at 取对方的
  删除记录：本机有这个播放列表、并且它的 updated_at 早于 deleted_at → 存进 sync_history 后删除，记下删除记录
  手动修正、歌词偏移：按（歌曲, 维度）对应，updated_at 新的为准；value 为 null 表示改回自动
  曲库规则、导航顺序：整组比较 updated_at
合并后本机的数据有变化 → 写自己的文件；曲库规则变了 → 用新规则重新整理艺术家（不用重读文件，数据库里有原始标签）
```

- 本机修改任何一项时，把它的 `updated_at` 设为当前时间；自己的文件防抖 5 秒后写入（移动端 30 秒）
- 两台设备的时钟一般都和网络时间同步，差几秒不影响"以后改的为准"；对方的时间比本机快一天以上时记日志提醒，照常合并
- 删除记录保留 180 天：比这更久没同步的设备，删掉的播放列表可能会回来（可以接受）
- 歌曲对不上（只在别的设备上有的歌）：条目原样保留在播放列表里（存的是对方的相对路径），本机显示为不可用，写回自己的文件时也原样写出，不会因为同步被删掉（需求 SY-6）。对应规则同移动端的恢复：完全匹配 → 路径后缀匹配（唯一命中才采用）

### 11.3 什么时候合并

- 启动时，曲库快照生成之后（不在启动的关键路径上）
- 运行中：FolderWatcher 发现 `.jeekmusic\sync\` 下别的设备的文件变了，防抖 2 秒后合并
- 移动端：启动时和回到前台时检查各文件的修改时间，变了才读
- 合并有变化时提示"已同步来自「设备名」的 N 项改动"，设置的同步页列出各设备和它们的 `written_at`

### 11.4 分析结果

- `<设备 ID>.features.json`：`{ "format": 2, "analyzer_version": 3, "items": { "<相对路径>": { "size": …, "duration_ms": …, "scores": { … }, "bpm": …, … } } }`
- 5 万首约 7–8MB。分析进行中最多每 10 分钟写一次，分析完写一次（避免大文件频繁同步）
- 使用：本机要分析一首歌之前，先在其他设备的结果里找同一路径、同样的大小和时长、同一 `analyzer_version` 的，有就直接用
- 两端解码器不同，得分会有细微差异（移动端设计 §6.6：阈值附近的几首可能判得不一样），可以接受

### 11.5 兼容旧的 backup.json

- 文件夹里只有移动端 1.0 写的 `.jeekmusic/backup.json`、没有 `sync\` 时，把它当作一台"旧设备"读入一次
- 旧备份里的播放列表没有 uid：用 `"legacy-" + 名字的 SHA-1` 作为 uid，两端算出来的一样，不会因为都从旧备份读入而重复
- 新版本不再写 `backup.json`

## 12. 启动性能

目标：冷启动到窗口显示内容 < 500ms，到表格内容完整 < 800ms（5 万首）。

```
Main
 ├ 单实例检查（互斥量，< 1ms）
 ├ 读 snapshot.json、settings.json（几 KB）
 ├ 打开 SQLite，在后台线程查"当前视图"需要的那部分（上次选中的文件夹里的歌 / 艺术家列表）
 └ 创建主窗口：位置、大小、导航栏状态、列宽来自快照；播放栏显示快照里的当前歌曲
首帧之后（按顺序）：
 ├ 当前视图的数据到达 → 填表格，恢复滚动位置（"内容完整"）
 ├ 300ms：创建 mpv、SMTC、任务栏按钮、托盘，以暂停状态恢复上次的歌（§7.6）
 ├ 读入整个曲库生成 LibrarySnapshot（5 万首约 200–400ms，后台线程）
 ├ 注册全局快捷键、电源通知
 ├ 2s：增量扫描（后台、低优先级）→ 之后启动 FolderWatcher
 ├ 同步合并（§11.3）
 └ 检查更新（每天最多一次）、分析调度
```

- 不加载自定义字体，用系统字体（Segoe UI Variable / 微软雅黑）；图标是嵌入的少量矢量路径
- 每个里程碑测一次冷启动（验证工程里的 `--measure-startup` 方式），记录在本节

## 13. 省电

- **不播放时 CPU 为 0**：没有任何周期性定时器。进度刷新的定时器只在播放中并且有可见的进度显示时运行
- **窗口不可见时不刷新界面**：主窗口最小化或隐藏到托盘、迷你播放器也没显示时，停止进度、歌词面板的刷新；SMTC 的时间线只在换歌、跳转、暂停时更新，任务栏进度每 2 秒一次
- 桌面歌词只在换行时重绘（§8.4）
- mpv 只观察不常变的属性（§7.1）
- 后台任务（扫描、分析）都是最低优先级；分析受电源和全屏的约束（§10.3）
- 验证工程测得：空闲 0.1%，播放时最小化约 1.7%、窗口显示约 2.9%（`tech-spike.md`）。正式工程以此为上限，每个里程碑复测

## 14. 设置

需求 §3.11。`settings.json` 由 `SettingsStore` 读写（源生成序列化，防抖 1 秒写入）。ViewModel 订阅变化。曲库规则那一组改动时更新 `rules_updated_at` 并触发同步写入。

## 15. 构建与发布

### 15.1 CI（`.github/workflows/desktop.yml`）

- 触发：推送到 main 且改动了 `desktop/**`、`shared/**` 或这个工作流本身（只改 `desktop/docs/**` 和 `*.md` 的不发布）；PR 只构建和测试
- `windows-latest`：`dotnet test` → `dotnet publish -c Release -r win-x64`（Native AOT）→ 放入 libmpv → 打包 `JeekMusic-desktop-1.0.<提交数>-win-x64.zip`
- 发布：标签 `desktop-v1.0.<提交数>`，**`make_latest: false`**（移动端的应用内更新取的是 latest，移动端设计 §13.2）；更新说明是上个桌面端版本之后改动了 `desktop/`、`shared/` 的提交标题
- `mobile.yml` 的触发路径加上 `shared/**`
- 发布前收集 NuGet、模型及原生库的完整许可文件；发布包的 `licenses/libmpv/SOURCE.txt` 指向与 DLL 对应的源码 Release。LGPL 包下载失败会终止构建

### 15.2 程序内更新（Updater）

- 检查：`GET /repos/tifish/JeekMusic/releases?per_page=20`，取标签以 `desktop-v` 开头的最新一个，比较末尾的提交数；每天最多一次，可以关闭（需求 UP-2）
- 下载 zip 到 `%LOCALAPPDATA%\JeekMusic\update\`，用 Release 资源的 `digest`（SHA-256）校验，解压到 `update\new\`
- 替换：用户确认后退出前启动 `update\new\JeekMusic.exe --apply-update <程序目录> <进程 ID>`：它等原进程退出，把自己目录里的文件复制到程序目录（覆盖），启动程序目录里的新版本，然后退出；新版本启动时删掉 `update\`
- 程序目录不可写（比如放在 Program Files）时提示用户手动更新

### 15.3 libmpv

- 开发阶段（D1–D5）：`get-libmpv.sh` 下载 shinchiro 的现成版本（GPL、121MB），不随正式版发布
- 正式发布（D6）：在 CI 里用 mpv-winbuild-cmake（或 MSYS2）自己编：mpv `-Dgpl=false`，FFmpeg 不开 `--enable-gpl` / `--enable-nonfree`，只保留音频的解封装器、解码器和 WASAPI 输出，关掉视频输出、libass、libplacebo、Vulkan 等；结果缓存起来，不是每次都编。目标体积 15MB 以内。FFmpeg 可以编成单独的 DLL，供分析直接用（§10.1 方案 B）
- 第三方许可证列在"关于 → 开源许可"：libmpv、FFmpeg（LGPL）、Avalonia、SkiaSharp、HarfBuzz（MIT）、ATL（MIT）、ONNX Runtime（MIT）、YAMNet（Apache 2.0）

## 16. 测试

| 类型 | 内容 |
|---|---|
| 规则（Core.Tests） | 读 `shared/testcases/` 的共用用例；QueueController 的队列行为（移植移动端的 `QueueControllerTest`）；同步合并（§11.2 的各种情况：新建、覆盖、删除记录、对不上的歌、旧 backup.json） |
| 界面（App.Tests） | Avalonia.Headless：ViewModel 的列表计算、排序、筛选，导航历史，主要的窗口交互；不打开真实窗口 |
| 播放 | 验证工程的自动测试（`spike-audio` 的 `--test-*`）整理成集成测试，需要真实声卡，只在本地跑 |
| 性能 | 冷启动、5 万首的滚动帧时间、扫描耗时、空闲和播放时的 CPU；每个里程碑测一次 |
| 测试素材 | 合成素材（移动端 `tools/push-testdata.sh` 的生成部分）、真实曲库子集（`pick_library_sample.py`）、`spike-audio/make-testdata.py` 的无缝 / CUE / 格式素材 |

**在作者的电脑上测试不能打扰正在用电脑的人**：能不开窗口就不开（Headless）；必须开窗口时用 `--background`（不激活、不进任务栏、放到所有窗口后面）并以 `CreateNoWindow` 启动；截图用 `PrintWindow`；不要反复启动窗口（`tech-spike.md` 的"用法"一节）。

## 17. 风险

| 风险 | 应对 |
|---|---|
| Avalonia DataGrid 的限制：自定义排序箭头、行拖动、5 万行以上的性能 | 验证过 2 万行 60fps；排序箭头和行拖动自己做（§6.3）；实在不行换成 ListBox + 自己的表头 |
| 拼音排序和移动端不一致 | 用 ICU4J 生成的表（§6.4），D1 用整个曲库对比 |
| ATL 的标签读取和 TagLib 有差异 | D1 对比验证（§5.3），差异大就换 TagLib# |
| 自己编 libmpv 的工作量 | 开发阶段用现成的；D6 之前单独做，CI 缓存结果 |
| 同步的边界情况（时钟偏差、同步工具改名、部分同步） | 合并算法在 Core 里，单元测试覆盖；读不到完整文件就跳过等下一次 |
| AOT 下的 COM（任务栏）和 WinRT（SMTC） | SMTC 验证过；任务栏用 `[GeneratedComInterface]`，D3 验证 |
| 没有代码签名，SmartScreen 拦截 | 说明里写清楚"仍要运行"；以后再考虑签名 |
| 独占模式下音量不可调 | `ao-volume` 不行时音量条不可用并提示（需求 O-4） |
