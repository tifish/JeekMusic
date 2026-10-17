# JeekMusic 技术设计文档

> 状态：草案 v0.2 · 2026-10-16 · 对应需求见 [requirements.md](requirements.md)

## 1. 技术栈

| 领域 | 选型 | 说明 |
|---|---|---|
| 语言 | Kotlin 2.x | |
| UI | Jetpack Compose + Material 3 | 支持动态取色（minSdk 31 原生支持） |
| 导航 | **Navigation 3**（`NavDisplay` + 自管理的返回栈） | 每个标签页一个 `NavDisplay` 和独立的返回栈；`rememberViewModelStoreNavEntryDecorator` 让每个页面有自己的 ViewModel，见 §10 |
| 播放 | **Media3**：ExoPlayer + `MediaLibraryService` | 后台播放、通知栏、媒体按键、音频焦点、系统恢复播放都由官方支持；支持浮点输出和音频 offload；以后加 Android Auto 时不用重构 |
| 数据库 | Room | 曲库缓存、播放列表、标签栏、智能标签、歌词缓存 |
| 配置 | DataStore (Preferences) | 设置项、启动快照 |
| 后台任务 | WorkManager（按需初始化） | 智能标签分析，受充电条件约束 |
| 依赖注入 | Hilt | 重对象一律用 `Lazy<T>` 注入，避免拖慢启动 |
| 异步 | Coroutines + Flow | |
| 图片 | Coil 3 + 自定义 Fetcher | 加载内嵌封面和文件夹封面，内存和磁盘两级缓存 |
| 标签解析 | **TagLib (JNI)**：`io.github.kyant0:taglib` | M0 已验证，见 §4.3 |
| 音频分析 | MediaCodec 解码 + 自写 FFT 和特征提取 | 不引入额外依赖，见 §6 |
| 网络 | 系统自带的 `HttpURLConnection` + `org.json` | 只用于 LRCLIB；请求量很小，不值得引入 OkHttp |
| 启动优化 | Baseline Profile + ProfileInstaller；Macrobenchmark 测量 | 见 §9 |
| 构建 | Gradle Kotlin DSL + Version Catalog | |
| CI | GitHub Actions | 构建、lint、单元测试、Release APK |

## 2. 整体架构

主体是一个 `app` 模块，按包分层。另外有一个 `baselineprofile` 模块，专门用于生成 Baseline Profile 和跑启动基准测试，这是 Android 工具链的要求。代码量增长后再考虑拆分业务模块。

```
┌──────────────────────────────── UI 层 (Compose) ─────────────────────────────────┐
│  tabs/  folders/  artists/  albums/  moods/  playlists/  search/  nowplaying/  …  │
│                       ViewModel (StateFlow<UiState>)                             │
└───────────────┬───────────────────────────────────────────────┬──────────────────┘
                │                                               │
┌───────────────▼──────────────── 数据层 ──────────────────┐   ┌───▼──── 播放层 ──────────────┐
│ LibraryRepository  PlaylistRepository  TabRepository     │   │ PlayerConnection (UI 侧)     │
│ TagRepository      LyricsRepository    SettingsRepository│   │   └ MediaController          │
│ ┌──────────┐ ┌─────────────┐ ┌─────────┐ ┌────────────┐  │   │ PlaybackService (独立组件)  │
│ │ Scanner  │ │ Analysis    │ │ Room DB │ │ LrcLibApi  │  │   │   ├ ExoPlayer                │
│ │MediaStore│ │ Decoder     │ │         │ │ (可选联网) │  │   │   ├ MediaLibrarySession      │
│ │TagReader │ │ Features    │ │         │ │            │  │   │   ├ QueuePersistence         │
│ │          │ │ Classifier  │ │         │ │            │  │   │   ├ SleepTimer               │
│ └──────────┘ └─────────────┘ └─────────┘ └────────────┘  │   │   └ OffloadController        │
└──────────────────────────────────────────────────────────┘   └──────────────────────────────┘
```

- UI 只通过 ViewModel 访问 Repository，不直接接触 DB 或 MediaStore。
- **播放层与 UI 进程内解耦**：UI 通过 `MediaController` 与 `PlaybackService` 通信，不直接持有 ExoPlayer。系统通知、蓝牙、锁屏走的也是同一条通道，状态天然一致。

### 包结构

```
com.tinyfish.jeekmusic
├── JeekApp.kt                 // @HiltAndroidApp；onCreate 里只做异步预热（§9）
├── MainActivity.kt
├── startup/                   // StartupSnapshot、DeferredStartupTasks
├── data
│   ├── db/                    // Room: entities, DAOs, JeekDatabase
│   ├── library/               // LibraryRepository, 文件夹树构建, 专辑聚合
│   ├── scanner/               // MediaStoreSource, TagReader, ArtistSplitter, FileNameInferrer, CueResolver, CueParser, LibraryScanner
│   ├── backup/                // BackupManager（导出 / 恢复 .jeekmusic/backup.json）
│   ├── tabs/                  // TabRepository（标签栏）
│   ├── lyrics/                // LrcParser, LyricsRepository, LyricsWriter, LrcLibClient, CharsetDetector
│   ├── cover/                 // CoverStore（封面查找、缓存、保存）, CoverClient（iTunes / MusicBrainz）, CoverMatcher, CoverFetchWorker
│   ├── playlist/
│   └── settings/              // SettingsRepository (DataStore)
├── analysis                   // 智能标签
│   ├── PcmDecoder.kt          // MediaExtractor + MediaCodec → 单声道 16kHz PCM
│   ├── AudioTagger.kt         // YAMNet（LiteRT）→ 模型得分
│   ├── Fft.kt
│   ├── FeatureExtractor.kt    // BPM、响度等手工特征
│   ├── TagClassifier.kt       // 三个维度的分类规则 + 旧版本的后备规则
│   ├── TagRepository.kt
│   └── AnalysisWorker.kt
├── playback
│   ├── PlaybackService.kt     // MediaLibraryService
│   ├── PlayerFactory.kt       // ExoPlayer 音质配置（§7.1）
│   ├── OffloadController.kt   // 熄屏 offload（§7.2）
│   ├── MediaItemMapper.kt
│   ├── QueuePersistence.kt
│   ├── SleepTimer.kt
│   ├── ArtworkProvider.kt     // 封面 ContentProvider，实际工作在 data/cover/CoverStore
│   ├── ArtworkVersions.kt     // 换过封面的歌曲，封面 URI 加版本号让 Coil 重新加载
│   └── PlayerConnection.kt    // UI 侧对 MediaController 的封装，暴露 StateFlow
└── ui
    ├── theme/  components/    // TrackRow 等通用组件
    ├── main/                  // MainScreen: 标签栏 + Pager + 迷你播放条
    ├── folders/  artists/  albums/  moods/  playlists/  search/
    ├── nowplaying/            // 播放页（歌词页）、队列
    └── settings/
```

## 3. 数据模型（Room）

```
library_folder
  id              INTEGER PK
  tree_uri        TEXT UNIQUE    -- SAF 树 URI（已持久化读写授权）
  volume_name     TEXT           -- MediaStore 卷名："external_primary" 或 SD 卡 UUID
  relative_path   TEXT           -- 以 "/" 结尾，如 "Music/Pop/"
  display_name    TEXT
  added_at        INTEGER

track
  id              INTEGER PK
  track_key       TEXT UNIQUE    -- 稳定键：file_key；CUE 虚拟歌曲为 file_key + "#" + 两位音轨号
  file_key        TEXT           -- volume_name + ":" + relative_path + file_name（实际音频文件）
  mediastore_id   INTEGER
  folder_id       INTEGER FK → library_folder (ON DELETE CASCADE)
  relative_path   TEXT           -- 所在目录，用于构建文件夹树
  file_name       TEXT
  title_tag       TEXT?          -- 原始标题标签
  title           TEXT           -- 展示用：标题标签 → 文件名推断 → 去掉扩展名的文件名
  album           TEXT?
  album_artist    TEXT?
  album_key       TEXT?          -- 专辑归属键，见 §5.4；无专辑标签时为 NULL
  artist_raw      TEXT?          -- 原始艺术家标签，多值以 \u0000 连接；NULL 表示无标签
  artist_display  TEXT           -- 展示用：artist_raw 或文件名推断结果，都没有时为空串（UI 显示"未知艺术家"）
  artist_inferred BOOLEAN        -- artist_display 是否推断自文件名
  track_no / disc_no / year      INTEGER?
  genre           TEXT?
  duration_ms     INTEGER
  mime_type / bitrate / sample_rate / bits_per_sample
  date_modified   INTEGER        -- 来自 MediaStore，用于增量判断
  size            INTEGER
  has_embedded_lyrics  BOOLEAN
  has_embedded_cover   BOOLEAN
  cue_track_no    INTEGER?       -- 以下三项只有 CUE 虚拟歌曲才有，见 §4.6
  start_ms        INTEGER?
  end_ms          INTEGER?       -- NULL 表示播到文件末尾
  INDEX(folder_id, relative_path)
  INDEX(album_key)
  INDEX(file_key)

artist
  id              INTEGER PK
  name            TEXT           -- 首次出现时的写法
  name_key        TEXT UNIQUE    -- 归一化：trim + lowercase，用于合并
  sort_key        TEXT           -- 中文转拼音后的排序键
  index_letter    TEXT           -- A–Z 或 "#"
  track_count     INTEGER        -- 冗余字段，扫描结束时更新；艺术家列表不用 GROUP BY，启动更快
  INDEX(sort_key)

track_artist      -- 多对多
  track_id        FK → track (CASCADE)
  artist_id       FK → artist (CASCADE)
  PK(track_id, artist_id)

pinned_tab        -- 标签栏
  id              INTEGER PK
  type            TEXT           -- FOLDERS | ARTISTS | PLAYLISTS | ARTIST | MOOD
  ref             TEXT?          -- ARTIST: artist.name_key；MOOD: "SPORT" / "QUIET"；内置标签为 NULL
  position        INTEGER
  UNIQUE(type, ref)

track_features    -- 音频分析结果（§6）
  track_key       TEXT PK
  signature       TEXT           -- date_modified + ":" + size；文件变了就要重新分析
  analyzer_version INTEGER       -- 特征算法升级时整体重新分析
  bpm             REAL?
  bpm_confidence  REAL
  loudness        REAL           -- 近似 LUFS
  loudness_range  REAL           -- 动态范围，单位 LU
  onset_rate      REAL           -- 每秒鼓点/起音数
  centroid_hz     REAL           -- 平均频谱质心（明亮度）
  low_ratio       REAL           -- 150Hz 以下能量占比
  analyzed_at     INTEGER
  failed          BOOLEAN        -- 解码失败；不再重试，除非文件签名变了
  vocal_score, classical_score, calm_level, energy_level, orchestra_level
                  REAL?          -- YAMNet 模型得分（§6.3）；旧版本分析的为 NULL

tag_override      -- 手动修正（需求 M-4）
  track_key       TEXT
  dimension       TEXT           -- MOOD | VOICE | GENRE
  value           TEXT?          -- SPORT / QUIET / VOCAL / INSTRUMENTAL / CLASSICAL / POP；NULL 表示"这个维度都不是"
  PRIMARY KEY(track_key, dimension)

track_tag         -- 物化结果 = 每个维度手动修正优先，否则取自动分类结果
  track_key       TEXT
  dimension       TEXT
  value           TEXT
  source          TEXT           -- AUTO | MANUAL
  PRIMARY KEY(track_key, dimension)
  INDEX(dimension, value)

playlist
  id, name, created_at, updated_at

playlist_item
  id              INTEGER PK
  playlist_id     FK → playlist (CASCADE)
  position        INTEGER
  track_key       TEXT           -- ⚠ 引用稳定键，不引用 track.id

queue_item        -- 续播队列（§7.5）
  position        INTEGER PK
  track_key       TEXT

lyrics_cache      -- 在线歌词无法写回音乐目录时的兜底，以及"未找到"的负缓存
  track_key       TEXT PK
  source          TEXT           -- "lrclib"
  remote_id       TEXT?
  synced          BOOLEAN
  content         TEXT
  fetched_at      INTEGER

lyrics_offset
  track_key       TEXT PK
  offset_ms       INTEGER
```

**关键决策：用 `track_key` 而不是 `mediastore_id` 作为跨表引用。** MediaStore 的 `_ID` 在重建索引或卷重新挂载时可能变化，`track.id` 在"重建曲库"时也会重新生成。基于路径的 `track_key` 能保证播放列表、手动标签、分析结果、歌词缓存和续播队列在重扫后依然有效（需求 PL-3、M-4）。代价是文件移动或重命名后会失联，对于以文件夹组织曲库的用户来说可以接受。同样的道理，固定艺术家引用的是 `artist.name_key`，而不是 `artist.id`。

**数据库版本**：1.0 发布前使用 `fallbackToDestructiveMigration`，结构变化时直接重建。**从 1.0（数据库版本 4）起**只在降级时重建（`fallbackToDestructiveMigrationOnDowngrade`），结构变化必须提供 `Migration`，以免丢失播放列表等用户数据；`app/schemas/` 中导出了每个版本的结构，可以用 `MigrationTestHelper` 测试迁移。

**孤儿清理**：每次扫描事务结束时删除没有歌曲的 artist。`track_features`、`tag_override`、`lyrics_*` 这些表中的孤儿行**不立即删除**，因为文件可能只是暂时不可用（比如 SD 卡拔出）；在"重建曲库"时，或孤儿行超过 30 天时再清理。

### 3.1 备份与恢复（BackupManager）

需求 B-1 至 B-6。

**备份文件格式**（`.jeekmusic/backup.json`，UTF-8）：

```json
{
  "format": 1,
  "app_version": "1.0.0",
  "exported_at": "2026-10-16T21:00:00+08:00",
  "settings": { "artist_separators": [";", "、"], "artist_exceptions": [], "...": "..." },
  "pinned_tabs": [ { "type": "FOLDERS" }, { "type": "ARTIST", "ref": "周杰伦" } ],
  "playlists": [ { "name": "跑步", "items": ["周杰伦/叶惠美/03. 晴天.flac", "Live/2004.flac#05"] } ],
  "tag_overrides": { "周杰伦/叶惠美/03. 晴天.flac": { "MOOD": "SPORT" }, "Classical/x.flac": { "GENRE": null } },   // 计划中（需求 B-5），尚未实现
  "lyrics_offsets": { "周杰伦/叶惠美/03. 晴天.flac": -500 },
  "features": { }
}
```

- **歌曲引用 = 相对于所属音乐文件夹的路径**（`relative_path` 去掉 `library_folder.relative_path` 前缀，再加上文件名；CUE 虚拟歌曲再加上 `#NN`）。这样音乐文件夹换了卷或换了上级目录，依然能对上
- `features`（P2）以同样的相对路径为键，附带文件 `size` 和 `duration_ms` 作为校验。拷贝文件后 `date_modified` 会变，所以不能用它校验

**写入**：

- 触发：备份范围内的数据（`playlist`、`playlist_item`、`pinned_tab` 表和设置）有变化后防抖 30s；用户点击"立即备份"
- **保护已有备份**：App 中还没有任何用户数据（没有播放列表、没有固定的艺术家）时，自动备份不写文件。否则刚安装或清除数据后，自动备份会用空数据覆盖音乐目录里的备份，用户就无法恢复了（M2 测试中实际出现过）。手动"立即备份"不受此限制
- 写入**每一个** `library_folder` 下的 `.jeekmusic/` 目录（通过 SAF 树 URI，不存在时创建），每个文件夹里的内容完全一样，都是全量数据
- 原子替换：先写 `backup.json.tmp`，然后把旧的 `backup.json` 重命名为 `backup.prev.json`（覆盖更早的那份），再把 tmp 重命名为 `backup.json`。如果 DocumentsProvider 不支持重命名，就退化为直接覆盖写入
- 后台优先级线程执行；只有在内容哈希变化时才真正写文件，避免无谓的 IO

**恢复**：

```
1. 添加文件夹时，检查 <folder>/.jeekmusic/backup.json 是否存在
   若存在，且（这是第一个文件夹，或 App 中没有任何播放列表、手动标签、固定艺术家）→ 弹窗提示
2. 用户同意 → 先完成对该文件夹的扫描
3. 建立索引：对所有 track 计算"相对于其音乐文件夹的路径" → track_key
4. 逐项映射：相对路径完全匹配 → 采用；匹配不到时按"后缀匹配"再找一次（路径段从右往左比对，唯一命中才采用）
5. 写入：同名播放列表合并（追加缺失的歌曲），手动标签和歌词偏移按 track_key 覆盖，固定标签追加缺失的项，设置项整体覆盖
6. 汇报："已恢复 N 个播放列表、M 条手动标签；K 首歌曲未找到"
```

"从备份恢复"手动入口：用 SAF `ACTION_OPEN_DOCUMENT` 选择任意 JSON 文件，之后同样执行第 3 至 6 步。

恢复提示由主界面统一显示（待恢复的备份保存在单例 `BackupManager` 中）：添加文件夹的引导页在曲库变为非空后就离开了屏幕，放在那里的对话框会跟着消失。

## 4. 曲库扫描

### 4.1 存储访问策略：SAF + MediaStore 组合使用

| 用途 | 方式 | 原因 |
|---|---|---|
| 用户选择文件夹 | SAF `ACTION_OPEN_DOCUMENT_TREE` + `takePersistableUriPermission`（读 + 写） | 用户体验自然，支持 SD 卡 / U 盘；同时获取写权限，用于保存歌词 |
| 发现音频文件 | **MediaStore** 查询 | 很快（直接查系统索引），而且可以监听变化 |
| 读取音频内容（标签、播放、分析） | MediaStore content URI | 有 `READ_MEDIA_AUDIO` 权限就能访问 |
| 读取 `.lrc`、`.cue`、封面图片和备份 | **SAF 树 URI** | 分区存储下 `READ_MEDIA_AUDIO` 读不了非媒体文件，但用户授权的树 URI 可以 |
| 写入在线歌词 `.lrc` 和备份文件 | **SAF 树 URI**（`DocumentsContract.createDocument`） | 见 §8.5、§3.1 |

SAF 树 URI 转换为 MediaStore 查询条件：

```
treeUri documentId:  "primary:Music/Pop"     → volume = "external_primary", relative_path = "Music/Pop/"
                     "1A2B-3C4D:Music"       → volume = "1a2b-3c4d",        relative_path = "Music/"
```

查询条件：`VOLUME_NAME = ? AND RELATIVE_PATH LIKE ? ESCAPE '\'`，参数为 `relative_path` 加 `%`，其中的 `%` 和 `_` 需要转义。不使用 `IS_MUSIC` 过滤，因为用户明确指定了文件夹，里面的音频都算。

> 已知限制：含有 `.nomedia` 的目录不会被 MediaStore 索引。在设置页的说明里注明。如果以后要支持，可以对这类目录退回 SAF 遍历。

### 4.2 扫描流程

```
LibraryScanner.scan(mode = Incremental | Full)
 1. 读取所有 library_folder
 2. 对每个文件夹查询 MediaStore → 得到 {mediastore_id, volume, relative_path, file_name, date_modified, size, duration, mime}
 3. 按扩展名和 mime 过滤掉不支持的格式（ape、wma、dsf/dff 等），只统计数量，存入 DataStore，供设置页显示
 4. 生成 file_key，与 DB 现有记录（按 file_key 分组）做 diff：
      新增                                  → 解析标签
      date_modified 或 size 变了（或 Full） → 重新解析标签
      DB 里有但 MediaStore 里没有           → 删除（包括该文件的所有 CUE 虚拟歌曲）
    对新增或变化的 FLAC / WAV 文件做 CUE 检测（§4.6），一个文件可能展开成多条 track
 5. 用有限并发（后台优先级的 IO 调度器，limitedParallelism(4)）解析标签，进度通过 StateFlow 暴露给 UI
 6. 计算派生字段：文件名推断（§4.5）、艺术家拆分（§4.4）、album_key（§5.4）
 7. 分批（每 500 条一个事务）写入 track、artist、track_artist
 8. 清理孤儿 artist，更新 artist.track_count
 9. 有新增或变化的歌曲时，通知 AnalysisScheduler（§6.5）
```

触发时机：

- App 启动后，**首帧显示 2s 后**在后台跑一次增量扫描（UI 先展示 DB 缓存，不等扫描结束，见 §9）
- 注册 `ContentObserver` 监听 `MediaStore.Audio.Media.EXTERNAL_CONTENT_URI`，防抖 3s 后增量扫描（只在 App 进程存活时生效，注册本身也推迟到首帧之后）
- 添加或移除文件夹后
- 用户手动操作

扫描在 Application 级 `CoroutineScope` 中执行，同时只允许一个扫描任务；新的请求会合并进当前任务。扫描线程设为 `THREAD_PRIORITY_BACKGROUND`，不和 UI、播放抢 CPU。

### 4.3 标签解析（TagReader）

接口：

```kotlin
interface TagReader {
    suspend fun read(uri: Uri): TrackTags   // 多值字段用 List<String>，包含采样率、位深等音频属性
    suspend fun readCover(uri: Uri): ByteArray?
    suspend fun readLyrics(uri: Uri): String?
}
```

**选型：TagLib（通过 JNI 封装）。** 它对 mp3、flac、m4a、ogg/opus、wav 的标签支持最全，能正确读出多值字段，可以通过文件描述符（`ParcelFileDescriptor`）读取，不需要真实路径。

**M0 验证结论（2026-10-16，已采用）**：使用 [Kyant0/taglib](https://github.com/Kyant0/taglib) `io.github.kyant0:taglib:1.0.6`（Apache-2.0 封装，内含 TagLib 原生库）。

| 项目 | 结果 |
|---|---|
| 正确性 | vivo X200 Ultra 上的真实曲库 1687 首，全部读取成功，失败 0 |
| 性能 | 4 路并发共 1248ms，平均 0.74ms/首（不读封面）。按此推算 1 万首约 7s，远低于 60s 的目标 |
| GBK 乱码 | TagLib 把 GBK 字节按 Latin-1 原样返回，`CharsetFixer` 能无损还原，真机上修复了 3 首（如 `ÉíÆï°×Âí → 身骑白马`） |
| fd 所有权 | 原生层的 `FileStream` 会关闭传入的 fd，所以每次调用都要传 `pfd.dup().detachFd()` |
| 不足 | `AudioProperties` 只有时长、码率、采样率和声道数，没有位深。位深需要另外获取（MediaExtractor 的 `KEY_PCM_ENCODING` 或解析 FLAC STREAMINFO），留到 M1 解决 |
| 体积 | `libtaglib.so` 随 AAR 分发，release 包按 ABI 拆分后再评估 |

备选方案 jaudiotagger 不再需要。

**中文乱码处理（CharsetFixer）**：在 ID3v2.3 / ID3v1 中，标签声明为 ISO-8859-1 但实际存的是 GBK 字节，这种情况非常常见。处理规则：

- 当字符串全部由 Latin-1 字符组成，且包含 0x80–0xFF 范围的字符时，取回原始字节，尝试用 GBK 严格解码
- 解码成功且结果包含 CJK 字符时，采用 GBK 解码结果
- 设置里可以强制指定旧标签编码（GBK / Big5 / Shift-JIS），这时跳过启发式判断

### 4.4 艺术家拆分（ArtistSplitter）

```
输入：artist 标签（List<String>，多值标签已经拆好）
对每个值：
  1. 如果在例外名单中（忽略大小写）→ 原样保留
  2. 否则按已启用的分隔符拆分（多字符分隔符按单词边界匹配，忽略大小写）
  3. trim，丢弃空串
去重（按 name_key）→ 结果为空则归入"未知艺术家"
```

- 默认分隔符：`;`、`/`、`、`、` feat. `、` ft. `、` featuring `
- 可选分隔符（默认关闭）：`&`、`,`、` x `
- 例外名单预置：`AC/DC`、`Earth, Wind & Fire`、`Simon & Garfunkel` 等（用户可增删）
- 依据：作者曲库（1578 首 MP3）的艺术家标签中，`/` 出现 187 次，几乎全是多艺术家（如"张惠妹/艾怡良/徐佳莹"）；`&` 13 次，其中既有合唱（"杨宗纬&李泉"）也有组合名（"小娟&山谷里的居民"），所以默认不拆
- 修改分隔符、例外名单或文件名推断设置后，提示"需要重建艺术家索引"。重建只需基于 DB 里的 `artist_raw`、`title_tag`、`file_name` 重算 `title`、`artist_display` 和 `track_artist`，**不需要重新解析文件**。所以多值标签在入库时要以 `\u0000` 连接存进 `artist_raw`，保留原始拆分信息。

只使用 `artist` 标签，不使用 `album_artist`（按需求，多艺术家要拆开，每个艺术家下都能找到这首歌）。

**拼音排序**：使用系统自带的 `android.icu.text.Transliterator.getInstance("Han-Latin; Latin-ASCII")` 生成 `sort_key`，首字母作为 `index_letter`，非字母开头的归入 `#`。在入库时计算一次，查询时直接 `ORDER BY sort_key`。

### 4.5 从文件名推断艺术家（FileNameInferrer）

只在 `artist_raw` 为空时生效（需求 A-9）。

```
输入：file_name（去掉扩展名）
 1. 去掉开头的音轨号：^\d{1,3}\s*[.\-_)、]\s*   例："01. "、"01 - "、"1_"、"03)"
    只有去掉后剩下的部分仍包含分隔符时才去掉，避免把 "1999 - Prince" 里的 1999 当成音轨号
 2. 按第一个分隔符切成两段：" - "、" – "、" — "、"－"（两侧空格可选）
    没有分隔符 → 推断失败
 3. 按设置中的格式取值：
      "艺术家 - 标题"（默认）→ 左段是艺术家，右段是标题
      "标题 - 艺术家"        → 反过来
 4. trim；任一段为空 → 推断失败
输出：artist（再交给 ArtistSplitter 拆分）、title（只在 title_tag 为空时使用）
```

| 文件名 | 推断艺术家 | 推断标题 |
|---|---|---|
| `周杰伦 - 晴天.mp3` | 周杰伦 | 晴天 |
| `01. Queen - Bohemian Rhapsody.flac` | Queen | Bohemian Rhapsody |
| `03 - 晴天.mp3` | （失败：去掉音轨号后没有分隔符） | |
| `A feat. B - Song.mp3` | A、B（经拆分） | Song |

推断结果写入 `artist_display`，同时设置 `artist_inferred = true`，在歌曲信息页标注"推断自文件名"。

推断出的艺术家除了用户设置的分隔符，**还额外按 `,` 拆分**：文件名里不能出现 `/`，网易云等下载的文件名用逗号分隔多个艺术家（如 `Panta.Q,周深 - 对不起.mp3`）。

### 4.6 CUE 整轨（CueResolver）

需求 CUE-1 至 CUE-8。

**发现 CUE**。`.cue` 不是媒体文件，MediaStore 里查不到，要通过 SAF 列目录：

```
对每个新增或变化的 FLAC / WAV 文件：
 1. 内嵌 cuesheet：TagLib 读取 Vorbis comment 中的 CUESHEET 字段（或 FLAC 的 CUESHEET 元数据块）
 2. 外部 .cue：通过 SAF 列出所在目录（与歌词共用 §8.1 的目录列表 LRU 缓存），按以下顺序匹配：
      a. <音频文件名>.cue，例如 "Album.flac.cue"
      b. <音频文件名去扩展名>.cue，例如 "Album.cue"
      c. 任意 .cue 中 FILE 行的文件名去掉扩展名后与音频文件同名（处理 CUE 中写 .wav、实际是 .flac 的情况）
      d. 目录中恰好只有一个 .cue 和一个 FLAC / WAV 文件 → 直接配对
 3. 都没有找到 → 当作普通单曲
```

- 只对 FLAC / WAV 做检测，并且只检查时长 ≥ 5 分钟的文件，这样大部分单曲目录不需要额外的 SAF 调用
- 一个 `.cue` 文件新增或修改时，不会触发 MediaStore 音频变化。所以设置页的"重新扫描"会对所有整轨候选文件重新检测 CUE（`requestScan(recheckCue = true)`）；自动的增量扫描只处理音频有变化的文件
- 编码识别：Big5 的字节往往也是合法的 GBK，所以先看所有双字节是否都在 GB2312 区（两个字节都 ≥ 0xA1），是则按 GBK，否则先试 Big5（§8.3）

**解析（CueParser）**：

- 编码：用 `CharsetDetector`（§8.3），GBK 编码的 CUE 很常见
- 顶层：`TITLE` → 专辑名，`PERFORMER` → 专辑艺术家，`REM DATE` → 年份，`REM GENRE` → 流派
- 每个 `TRACK nn AUDIO`：`TITLE`、`PERFORMER`（没有时继承顶层），`INDEX 01 mm:ss:ff`（每秒 75 帧）作为起点；下一首的 `INDEX 01` 作为终点，最后一首的终点为 NULL（播到文件末尾）。忽略 `INDEX 00`（曲间间隙归入上一首，这样连续播放时不会丢失音频）
- CUE 里有多个 `FILE` 且每个文件只有一个 TRACK（分轨 CUE，CUE-8）→ 忽略这个 CUE
- CUE 里缺少的字段（比如封面、年份）取自整轨文件本身的标签

**入库**：每个 TRACK 生成一条 `track`：`track_key = file_key + "#" + 两位音轨号`，填上 `cue_track_no`、`start_ms`、`end_ms`，并按 `end_ms - start_ms` 计算 `duration_ms`。整轨文件本身**不生成**普通的 track。

**在各模块中的表现**：

| 模块 | 处理方式 |
|---|---|
| 文件夹视图 | 虚拟歌曲显示在整轨文件所在目录，按 `cue_track_no` 排序，排在同目录普通歌曲的后面 |
| 播放 | `MediaItem.ClippingConfiguration(start_ms, end_ms)`，见 §7.4 |
| 智能标签 | `PcmDecoder` 的取样窗口按虚拟歌曲的起止时间计算 |
| 封面 | 整轨文件的内嵌封面，或目录里的封面图片 |
| 歌词 | 按 CUE-7 的文件名规则查找和保存 |

## 5. 浏览视图实现

### 5.1 标签栏（TabRepository）

- 标签列表来自 `pinned_tab ORDER BY position`。首次安装时写入默认值：文件夹、艺术家、播放列表（"运动 / 安静"已改为筛选，见 §5.5；启动时删除旧版本留下的 `MOOD` 类型标签）
- 固定艺术家时插入到"文件夹"标签之后（在同一个事务里把后面的标签 `position` 依次加一），而不是追加到末尾，否则标签多时新标签在屏幕外看不到
- 固定艺术家：插入 `(ARTIST, name_key)`，位置放在最后；如果已经存在就忽略
- 固定的艺术家在重扫后不存在了（name_key 查不到）：标签保留，页面显示"暂无歌曲"，并提供"取消固定"按钮。这样 SD 卡暂时拔出时，标签不会丢失
- 标签列表同时写入启动快照（§9.2），首帧不需要等数据库
- UI：`ScrollableTabRow` 加 `HorizontalPager`。`beyondViewportPageCount = 0`，也就是只组合当前页，避免固定标签很多时拖慢启动和滑动；首帧之后再把相邻页的预加载打开

### 5.2 文件夹视图

文件夹树**不扫描文件系统**，而是根据 `track.relative_path` 在内存中构建：

```kotlin
// 查询：某个 root 下、以 path 为前缀的所有不同 relative_path
// → 取 path 之后的第一段作为直接子文件夹，同时统计歌曲数
data class FolderNode(val folderId: Long, val path: String, val name: String, val trackCount: Int)
```

- 根级 = `library_folder` 列表，每个根显示 `display_name` 和其下的总歌曲数
- 子级 = `SELECT relative_path, COUNT(*) FROM track WHERE folder_id=? AND relative_path LIKE ? GROUP BY relative_path`，再在内存中折叠出直接子目录，并按名称自然排序
- 这样空目录自然不会出现（需求 F-3）
- 路由：`Folder(folderId: Long, path: String)`

### 5.3 艺术家视图

- 列表：`SELECT * FROM artist ORDER BY sort_key`（`track_count` 已经冗余存储，不需要 GROUP BY），作为 Flow 输出。1 万首的规模下艺术家通常只有几千个，直接用 `LazyColumn` 即可，不需要 Paging
- 字母索引：按 `index_letter` 分组生成 sticky header，右侧快速滚动条跳到对应分组
- 详情：`SELECT track.* FROM track JOIN track_artist ... WHERE artist_id=?`，在内存中按 `album_key` 分组（为空的归入"未知专辑"），组内按 `disc_no, track_no, file_name` 排序
- 路由：`ArtistDetail(nameKey: String)`。固定艺术家标签的根页面就是这个路由

### 5.4 专辑页

专辑不单独建表，而是由 `track.album_key` 动态聚合。

**album_key 计算**（需求 AL-4）：

```
album 为空          → NULL（不提供专辑入口）
album_artist 非空   → "a:" + norm(album) + "|" + norm(album_artist)
否则                → "d:" + norm(album) + "|" + volume + albumDir
norm     = trim + lowercase
albumDir = relative_path；如果最后一级目录名匹配 ^(cd|disc|disk)\s*\d+$（忽略大小写），取上一级目录
```

- 有专辑艺术家时，跨目录或多碟分目录（`CD1/`、`CD2/`）存放的同一张专辑都能正确合并
- 没有专辑艺术家时退回到按目录区分：既能避免不同艺术家的同名专辑（"Greatest Hits"）混在一起，也能避免合辑因为每首歌艺术家不同而被拆散

查询：`SELECT * FROM track WHERE album_key = ? ORDER BY disc_no, track_no, file_name`。页头信息在内存中汇总：专辑名取第一条；艺术家取 `album_artist`，没有时取所有歌曲艺术家去重；年份取最小值；封面取第一首有封面的歌。存在多个 `disc_no` 时按碟分组。

入口（需求 AL-1）：`TrackRow` 的"更多"菜单、播放页上的专辑名（`MediaItem` 的 extras 里带上 `album_key`）。路由：`Album(albumKey: String)`。

### 5.5 智能标签筛选

最初"运动"和"安静"是两个标签页；后来改为全局筛选（需求 M-6），在任何歌曲列表里都能用；加入人声和曲风后，三个维度可以组合筛选。

- 状态：`TagFilter(mood, voice, genre)`，每个维度不选或选一个，保存在设置 DataStore 里（`filter_mood` / `filter_voice` / `filter_genre`；兼容旧版本的 `mood_filter`），由 `TagFilterRepository` 提供 `filter` 和 `allowedKeys`（各个选中标签的 `SELECT track_key FROM track_tag WHERE dimension = ? AND value = ?` 取交集，没有筛选时为 null）。分析完成或手动修正后 `track_tag` 变化，所有列表自动刷新
- 只有歌曲列表在内存里按 `allowedKeys` 过滤（艺术家页、专辑页、文件夹内的歌曲、播放列表、搜索结果）；需要计数的地方在 SQL 里加条件：
  - 文件夹：`observeWithCounts(mood, voice, genre)`、`observeDirectoryCounts(..., mood, voice, genre)` 对每个维度加 `(:mood IS NULL OR t.track_key IN (SELECT track_key FROM track_tag WHERE dimension = 'MOOD' AND value = :mood))`（三个维度同样）。子文件夹由计数折叠得到，没有这类歌的子文件夹自然不出现；根级的音乐文件夹总是显示
  - 艺术家列表：`ArtistDao.observeCounts(mood, voice, genre)` 按艺术家统计符合的歌曲数，没有的艺术家去掉，`track_count` 换成筛选后的数量
- 播放列表在筛选时隐藏拖动手柄：只看到部分条目时，按可见条目重排会打乱看不到的条目
- 标题栏的筛选菜单里显示未分析数（`未分析数 = 曲库总数 - 已分析数`，排除 `failed`），并提供"立即分析"（触发一次不要求充电的任务）。未分析数只在菜单打开时查询，不影响启动

### 5.6 搜索

M2 先用 `LIKE '%q%'` 查询 `title / artist_display / album / file_name`，在 1 万首规模下足够快。如果性能不够或需要拼音搜索（S-2），再加 FTS4 表，并预存拼音全拼和首字母列。

### 5.7 歌曲列表（TrackRow）

歌曲列表是使用最频繁的界面，要保证滚动流畅、耗电低：

- `LazyColumn` 使用稳定的 `key = track.id`，并设置 `contentType`；行高固定
- `TrackRow` 只接收不可变的 `TrackUi`（`@Immutable`），避免多余的重组
- "正在播放"高亮：当前播放的 `track_key` 通过单独的 `State` 传入，切歌时只有两行会重组
- 缩略图：Coil 按 48dp 实际像素尺寸解码，磁盘缓存缩略图；快速滚动时先显示占位图
- "定位到正在播放"（需求 P-10）：在当前列表中查找 `track_key` 的索引，然后调用 `scrollToItem`

### 5.8 歌曲排序（SongSorter）

- 全局设置 `SongSort { DEFAULT, MODIFIED, FILE_NAME, TITLE, ARTIST }`（DataStore `song_sort`），在标题栏的"筛选和排序"菜单里选（需求 U-8）
- 在内存里排序（`flowOn(Dispatchers.Default)`），不改 SQL：各列表先按筛选过滤，再交给 `SongSorter.sorted()`；`DEFAULT` 时返回 null，调用方用自己的默认顺序
- 比较器：修改时间 = `date_modified` 降序；文件名 = 自然排序 + CUE 音轨号；歌名 / 歌手 = 拼音排序键（`SortKeys`，ICU Han-Latin）+ 自然排序，同名再按文件名；"未知艺术家"排在最后；播放列表中不可用的条目排在最后
- 拼音转写较慢，排序键按字符串缓存（`ConcurrentHashMap`），长列表反复排序也不卡
- 播放列表在非默认排序或筛选时隐藏拖动手柄

## 6. 智能标签：氛围、人声、曲风

### 6.1 总体思路

```
AnalysisWorker（WorkManager，默认要求充电）
  对每首待分析的歌曲：
    PcmDecoder        解码三个片段 → 单声道 16kHz PCM（只用于分析，与播放无关）
    AudioTagger       YAMNet 逐帧打分 → 521 类的平均分 → 几组得分（ModelScores）
    FeatureExtractor  抽取到 11025Hz → BPM、响度等手工特征（歌曲信息页显示；旧版本数据的后备）
    写入 track_features
  每 25 首和批次结束时：
    TagRepository.recomputeAll()
      → 每个维度：有 tag_override 时用手动结果，否则用 TagClassifier.classify(features, sensitivity)
      → 写入 track_tag
```

**得分和分类是分开的**：模型得分保存在 `track_features` 里，阈值在分类时才用。调整灵敏度或改阈值只需要重新分类（毫秒级），不需要重新解码音频（需求 M-8）；改了类别分组才需要提高 `ANALYZER_VERSION` 重新分析。

最初（v2）只用节奏、响度、低频占比等手工特征判断运动 / 安静，在人工标注的校准集上只有 58% 准确：它分不清"录音音量小的激昂古典"和"真正安静的歌"，也漏掉了大多数抒情流行。人声和曲风更是手工特征很难做的。v3 改用预训练的音频分类模型。

### 6.2 解码（PcmDecoder）

- 用 `MediaExtractor` + `MediaCodec`（系统解码器，大多是硬件加速）解码
- 取 3 个各 10 秒的片段，分别位于时长的 30%、50%、70% 处，以避开前奏和尾奏；短于 40 秒的歌曲整首分析。CUE 虚拟歌曲的时长和位置按 `start_ms` / `end_ms` 换算
- 下混为单声道（÷√声道数，功率守恒，与 BS.1770 和 ffmpeg 一致），盒式平均抽取到 16kHz（YAMNet 的输入）；手工特征再从 16kHz 抽取到 11025Hz
- 有些文件在 `MediaExtractor` 中跳转不可靠，按窗口解码得不到数据时退回到从头解码 30 秒
- 解码失败时把 `failed` 设为 true，不阻塞队列

### 6.3 YAMNet 模型（AudioTagger）

- 模型：Google 的 [YAMNet](https://www.kaggle.com/models/google/yamnet)（MobileNet v1，在 AudioSet 上训练，521 个声音类别，Apache 2.0），TFLite 分类版约 4MB，放在 `assets/yamnet.tflite`（不压缩，直接内存映射）
- 运行时：LiteRT（原 TensorFlow Lite，`com.google.ai.edge.litert:litert`），用经典的 `org.tensorflow.lite.Interpreter` 接口，CPU + XNNPACK，2 个线程；第一次分析时加载，任务结束时释放（约 20MB 内存）
- 输入 15600 个采样（0.975 秒），不重叠地切帧，三个 10 秒片段共约 30 帧；输出每帧 521 类的分数，取平均
- 只保存分类要用的几组得分（`track_features` 的 5 个 REAL 列）：

| 列 | 计算 | 用途 |
|---|---|---|
| `vocal_score` | ln(人声类之和 / 乐器类之和)。人声类：Singing、Choir、Rapping、Humming、Chant、Vocal music、A capella、Opera、Speech、Lullaby 等；乐器类：Musical instrument、Bowed string、Violin、Cello、Piano、Orchestra、Brass、Woodwind、Guitar 等 | 人声 / 纯音乐 |
| `classical_score` | ln(古典类之和 / 流行类之和)。古典类：Classical music、Opera、Orchestra、弦乐、钢琴、铜管、木管；流行类：Pop、R&B、Hip hop、Electronic、Dance、Rock、Music of Asia、Singing、Drum machine、Synthesizer 等 | 古典 / 流行 |
| `calm_level` | Tender、Sad、Lullaby、New-age、Ambient、Background music 之和 | 安静 |
| `energy_level` | Exciting、Happy、Dance、Electronic、Hip hop、Rock、Drum kit、Drum、Rapping、Disco、Pop、EDM 之和 | 运动 |
| `orchestra_level` | Orchestra | 排除大编制管弦乐段落 |

- 性能：模拟器（x86_64）上每首约 2.6 秒（含解码，后台低优先级），手机上应更快；1 万首在充电时分几次完成
- 体积：APK 从 10MB 增加到约 17MB（模型 4MB + LiteRT 原生库；原生库压缩打包，只打包 arm64-v8a 和 x86_64）

### 6.4 分类规则（TagClassifier）

```
人声：vocal_score ≥ -2.3 → 人声，否则纯音乐（总有一个）
曲风：classical_score ≥ 0.5 → 古典；≤ -0.5 → 流行；之间 → 都不是
氛围：sport = ln(energy + 0.003) - ln(calm + 0.003)
      quiet = ln(calm + 0.01) - ln(energy + 0.01)
      sport ≥ 3.25 → 运动
      否则 quiet ≥ 0.2 且 orchestra ≤ 0.1 → 安静
      否则都不是
灵敏度（只影响氛围）：运动阈值 宽松 2.75 / 标准 3.25 / 严格 3.75；安静阈值 0.0 / 0.2 / 0.5
```

- 人声阈值有很大余量：校准集里纯音乐的 `vocal_score` 最高 −2.83，人声最低 −1.72，−2.3 取在中间。只用"真正的人声类"，不把流行曲风当作人声证据，否则纯音乐的电子 / 流行会被误判成人声
- 曲风的"都不是"区间判断得不准：爵士钢琴三重奏、电影配乐在模型看来也很"古典"（乐器类得分高）。所以"古典"实际含义是"听起来像古典"；这点写进了需求 M-1
- 安静：激昂的古典（序曲、快板）和慢板在"舒缓 / 动感"上很接近（都没有鼓），区别在于前者 Orchestra 得分高、几乎没有 Tender / Sad；加上 `orchestra ≤ 0.1` 后排除了大部分
- 规则只依赖单首歌自己的得分，不依赖整个曲库的分布，新增歌曲不会改变已有歌曲的标签
- 还没用模型重新分析的歌（`analyzer_version` < 3，模型得分为 null）：氛围用旧的手工特征规则（`LegacyMoodClassifier`），人声和曲风为空。升级后在重新分析完成之前，"运动 / 安静"筛选不会突然变空

### 6.5 数据（track_tag、tag_override）

- `track_tag(track_key, dimension, value, source)`，主键 (track_key, dimension)，索引 (dimension, value)。dimension 为 MOOD / VOICE / GENRE，value 为 SPORT、QUIET、VOCAL、INSTRUMENTAL、CLASSICAL、POP，source 为 AUTO / MANUAL。筛选和计数都直接查这张表（§5.5）
- `tag_override(track_key, dimension, value)`：手动修正，value 为 null 表示"这个维度都不是"
- 数据库 4 → 5（`MIGRATION_4_5`）：`track_features` 增加 5 个得分列；`track_mood` / `mood_override` 的数据迁移到新表（dimension = MOOD）后删除。`MigrationTest` 用 `app/schemas/` 中的 v4 结构建库、写入数据、迁移并由 Room 校验结构
- 全部重算（`recomputeAll`）：1 万首规模下只要几十毫秒，所以分析结果、手动修正或灵敏度变化后直接全部重算

### 6.6 校准方法和结果

校准集：作者曲库中挑出的 109 首（`tools/pick_library_sample.py`，流行、古典、民谣、配乐混合，周深的歌较多）。人工标注在 `tools/calibration/labels.tsv`：人声 / 纯音乐和古典 / 流行比较客观；运动 / 安静带主观性，只把明显的标为运动或安静，介于中间的标为"都不是"，拿不准的标为"?"不参与评估。

```
tools/calibration-pcm.sh <音乐目录>          # ffmpeg 按与 App 相同的方式导出 16kHz PCM
python tools/calibration/yamnet_scores.py app/src/main/assets/yamnet.tflite build/calibration16 build/calibration16/scores.npz
python tools/calibration/evaluate.py build/calibration16/scores.npz --report
```

`evaluate.py` 用与 App 相同的类别分组和阈值，逐首打印得分、预测和错误，以及三个维度的准确度。调阈值时先在这里试，再同步到 `TagClassifier`。

| 维度 | 准确度 | 错误的歌 |
|---|---|---|
| 人声 / 纯音乐 | 106 / 106 | — |
| 古典 / 流行 | 95 / 98 | 小提琴翻奏的流行歌、弦乐编曲的流行抒情歌、周深的歌剧风格现场（都被判为古典） |
| 运动 / 安静 | 64 / 73（v2 手工特征：42 / 73） | 几首木吉他抒情歌没判为安静；几首慢速的古典（艺术歌曲、长笛奏鸣曲）被判为安静 |

对比过在 521 类分数上训练逻辑回归（留一法交叉验证）：人声 99%、古典 / 流行 97%，并不比上面的规则好，而规则更容易理解和调整，所以用规则。

**模拟器与 PC 的一致性**：模拟器上用系统解码器的分析结果，和 PC 上用 ffmpeg 导出的结果对比：纯音乐 33 / 33、古典 41 / 41 首完全一致；人声和流行各少 1 首（那首 Ogg 系统解码器打不开）；运动 6 / 5、安静 36 / 40 首（几首歌的得分正好在阈值附近，两种解码器的细微差异会改变结果）。

**已知问题**：极少数文件系统的 `MediaExtractor` 无法识别（实测 1604 个文件中有 1 个 Ogg Vorbis），分析失败后标记为 `failed`，不再重试；ExoPlayer 有自己的解析器，播放不受影响；这类歌可以在菜单里手动修正标签。

### 6.7 调度与耗电（AnalysisScheduler）

- 唯一任务名 `mood-analysis`，使用 `ExistingWorkPolicy.KEEP`
- 约束条件：默认 `requiresCharging + batteryNotLow`；设置为"随时"时只保留 `batteryNotLow`
- 每次运行最多处理 300 首或 8 分钟，到达上限后提交一个新任务接着做，避免超出 WorkManager 的单次执行时限；被系统停止时（`isStopped`）立即保存进度并退出
- 触发时机：扫描发现新歌或歌曲有变化时；`analyzer_version` 升级后；用户点击"立即分析"
- 解码和计算在 `THREAD_PRIORITY_BACKGROUND` 线程中进行

## 7. 播放层

### 7.1 音质配置（PlayerFactory）

目标：**App 内对音频不做任何有损处理**（需求 §4.1）。

```kotlin
val audioSink = DefaultAudioSink.Builder(context)
    .setEnableFloatOutput(true)          // 高位深音源以浮点 PCM 输出，不截断到 16-bit
    .build()
val renderersFactory = object : DefaultRenderersFactory(context) {
    override fun buildAudioSink(...) = audioSink
}
ExoPlayer.Builder(context, renderersFactory)
    .setAudioAttributes(musicAttributes, /* handleAudioFocus = */ true)
    .setHandleAudioBecomingNoisy(settings.pauseOnUnplug)
    .setWakeMode(C.WAKE_MODE_LOCAL)
    .setSkipSilenceEnabled(false)
    .build()
```

- 不调用 `setPlaybackParameters`（速度和音调保持 1.0），不挂任何 `AudioProcessor`。浮点输出会旁路 ExoPlayer 的音频处理链，正好符合要求
- 音量：`player.volume` 恒为 1.0，只有睡眠定时渐弱时例外；渐弱结束、暂停后恢复为 1.0
- 不做音量标准化（ReplayGain）；如果以后加，作为可选功能，默认关闭
- 系统混音器之后的重采样不在 App 的控制范围内。USB DAC 比特完美输出（Android 14+ 的 `AudioManager.setPreferredMixerAttributes`，`MIXER_BEHAVIOR_BIT_PERFECT`）列为 P2
- 具体 API 名称以实际使用的 Media3 版本为准，M0 阶段确认

### 7.2 低功耗播放（OffloadController）

音频 offload 把压缩数据直接交给音频 DSP 解码，CPU 可以休眠，长时间熄屏播放时省电明显。

设置项"低功耗播放"有三档：

| 档位 | 行为 |
|---|---|
| **仅后台 / 熄屏**（默认） | 熄屏或 App 退到后台时开启 offload；App 回到前台且亮屏时关闭，保证进度刷新及时、拖动进度条响应快 |
| 始终使用 | 前台也开启 offload。亮屏时屏幕和 UI 渲染的耗电占绝大部分，所以额外省的电很少；代价是部分机型上进度和歌词同步变粗，拖动进度条、暂停后继续和切歌的延迟变大。主要用于在自己的设备上实测对比 |
| 关闭 | 始终使用普通播放。某台设备在 offload 模式下出问题时，用户可以自行关闭 |

- 开启方式：`TrackSelectionParameters.audioOffloadPreferences` 设为 `AUDIO_OFFLOAD_MODE_ENABLED`，并要求支持无缝播放（`setIsGaplessSupportRequired(true)`），保证歌曲之间不会因为 offload 而出现间隙
- OffloadController 监听 `ProcessLifecycleOwner`（前台 / 后台）和屏幕开关（`ACTION_SCREEN_ON/OFF`），按档位切换 offload 偏好
- 设备或格式不支持 offload 时，ExoPlayer 会自动走普通播放路径
- 歌曲信息页显示当前是否处于 offload 模式，方便对比和排查
- 音质：无损格式在 DSP 上的解码同样是比特精确的；有损格式由标准解码器解码，结果与软件解码等价

### 7.3 PlaybackService

```kotlin
class PlaybackService : MediaLibraryService() {
    // player = PlayerFactory.create()
    // MediaLibrarySession.Callback：
    //   onAddMediaItems     → 根据 mediaId 从 DB 补全 MediaItem（Controller 端只传 id）
    //   onGetLibraryRoot / onGetChildren → 浏览树（为 Android Auto 预留，M1 只实现根和最近队列）
    //   onPlaybackResumption → 从 QueuePersistence 恢复（需求 P-7 系统恢复播放）
    //   onCustomCommand     → 睡眠定时等自定义命令
}
```

- 通知栏使用 Media3 的 `DefaultMediaNotificationProvider`，零额外代码
- `onTaskRemoved` 时如果没有在播放就 `stopSelf()`；暂停一段时间后，服务退出前台并允许系统回收
- 播放出错（`onPlayerError`）时跳到下一首，并通过自定义事件通知 UI 弹出提示

**"下一首"始终可用（需求 P-11）**：交给 `MediaLibrarySession` 的不是 ExoPlayer 本身，而是包了一层 `NextAlwaysPlayer`（`ForwardingPlayer`）：
- 队列不为空时 `getAvailableCommands()` / `isCommandAvailable()` 总是包含 `COMMAND_SEEK_TO_NEXT(_MEDIA_ITEM)`，所以系统媒体控件（通知、锁屏）总显示"下一首"
- `seekToNext()` / `seekToNextMediaItem()`：没有下一首时 `seekTo(0, 0)` 跳回第一首（只有一首时就是从头重播）
- 会话直接使用"可用命令变化"事件里的参数（不会再调 `getAvailableCommands()`，反编译 Media3 1.11 确认过），而这个参数来自 ExoPlayer、不含"下一首"。所以 `addListener` 时用 `CommandsListener` 包装监听器，转发前补上
- `CommandsListener` 逐个写出 `Player.Listener` 的全部回调：这些都是 Java 默认方法，Kotlin 的 `by` 委托不会转发它们（单元测试验证过），否则播放状态等事件会丢失。`NextAlwaysPlayerTest` 用反射检查是否覆盖了全部回调，升级 Media3 新增回调时会提醒
- 播放出错时自动跳下一首（`onPlayerError`）仍用 ExoPlayer 本身判断，最后一首出错不会绕回开头

### 7.4 MediaItem 映射

```
mediaId     = "track:" + track.id
uri         = MediaStore content URI
metadata    = title, artist(artist_display), album, artworkUri, durationMs
extras      = track_key, album_key, artist name_keys   // 播放页跳转用
artworkUri  = content://com.tinyfish.jeekmusic.artwork/<track_key 的哈希>
```

**CUE 虚拟歌曲**：`uri` 指向整轨文件，并设置 `ClippingConfiguration(startPositionMs = start_ms, endPositionMs = end_ms ?: C.TIME_END_OF_SOURCE)`。

同一整轨中相邻两首的衔接（需求 CUE-6）是一个技术风险：ExoPlayer 会把每个裁剪片段当作独立的媒体源来准备，切换时需要重新 seek 解码器，可能产生几十毫秒的停顿。M2 阶段先实测：

- 方案 A（默认）：直接使用 ClippingConfiguration，利用 ExoPlayer 预加载下一首。如果实测停顿听不出来，就采用这个方案
- 方案 B（备选）：当队列中连续几首都来自同一个整轨文件时，把它们合并成一个覆盖整段的 MediaItem，由 Service 根据播放位置换算出"当前是第几首"，再更新元数据、通知栏和歌词。这样衔接完全无缝，但队列和进度条的逻辑要复杂很多

封面通过自建的 `ArtworkProvider`（ContentProvider，`content://<authority>/track/<id>/<small|large>`）提供，通知栏、锁屏、蓝牙车机和 Coil 共用这一个入口。查找和缓存在 `CoverStore`：

- **查找顺序**：`.jeekmusic/covers/` 里的在线封面 → 歌曲旁边的同名图片（`歌曲名.jpg/png/webp`）→ 内嵌封面 → 同目录的 `cover.jpg` / `folder.jpg` 等。在线封面是用户为这首歌选的，所以最优先
- **缓存**：缩放到 144px（列表）和 720px（通知、播放页）的 JPEG，按专辑键（没有专辑时按歌曲键）的 SHA-1 存在 `cacheDir/artwork`，"没有封面"也记一个 `.none`。目录列表放在内存 LRU 里：所有歌曲在同一个目录时，每次都通过 SAF 列一遍上千个文件很慢
- **换封面后刷新**：保存时直接用新图片重写这张专辑的缓存，再通过 `ArtworkVersions` 给同专辑所有歌曲的封面 URI 加上 `?v=n`。`Artwork` 组件用 `derivedStateOf` 只读自己那首歌的版本号，所以只有换了封面的条目会重新加载；Provider 忽略查询参数

**在线封面（需求 C-3、C-4）**：

- **来源**：`CoverClient` 用 HttpURLConnection + org.json，不引入网络库
  - iTunes Search API（`entity=song`）：中文只有繁体能搜到，含汉字时用 ICU `Simplified-Traditional` 转成繁体、查台湾区，其余查美国区；`artworkUrl100` 换成 `1000x1000` 取大图。限速约 20 次 / 分钟，请求间隔 3s
  - MusicBrainz 录音搜索（`recording:"歌名" AND artist:"艺术家"`）：中文收录比 iTunes 全；要求 User-Agent、每秒不超过 1 次。封面取 Cover Art Archive 发行组的代表封面（`release-group/<id>/front-1200`，没有时 `front-500`），比按单个发行版查命中率高
  - 搜索艺术家只用第一位：多位艺术家拼在一起时两个来源往往都搜不到
- **匹配**（`CoverMatcher`）：NFKC、小写、繁转简（ICU `Traditional-Simplified`），去掉标点空白和括号里的附注、` - Remastered` 之类的后缀后比较。歌名必须完全相同（"Baby" 和 "Baby Shark" 不算）；艺术家拆分后任意一位与结果互相包含即可。手动搜索时对得上的排前面，括号附注也相同的（不是 Remix / Live）再往前；批量获取只采用对得上的，专辑名也对得上的优先
- **手动搜索**：两个来源分别请求、各张缩略图分别下载，谁先返回先显示谁，不用等最慢的 Cover Art Archive
- **获取方式**（`CoverFetchScheduler`）：手动时只在用户点"获取所有缺失的封面"时以 `CONNECTED` 约束、`REPLACE` 提交（顶掉在等 Wi-Fi 的自动任务）；自动 / 仅 Wi-Fi 时，App 启动的推迟任务里和扫描到新增或变化的歌曲后调用 `scheduleAuto()`，分别以 `CONNECTED` / `UNMETERED` 约束、`KEEP` 提交。重复提交很便宜：有封面的（读缓存）和 30 天内找不到的都直接跳过。改获取方式时先取消还在排队的任务再按新方式提交。注意 `UNMETERED` 要等系统验证过 Wi-Fi 能上网才会运行
- **批量获取**（`CoverFetchWorker`，WorkManager 唯一任务 `cover-fetch`）：按专辑键分组，用 `CoverStore.hasCover` 找出没有封面的组，逐组查询保存，通过 `setProgress` 报告进度，设置页显示"正在获取 n / m，已找到 k"。找不到的组记在 `filesDir/cover_misses.tsv`，30 天内不再查；网络出错的不记，下次重试。被系统停止（超过 10 分钟等）时返回 retry，已保存的封面下次会被跳过
- **保存**（`CoverStore.save`）：图片统一成 JPEG（不超过 1200px 的 JPEG 原样保存）
  - 隐藏目录：`<音乐文件夹>/.jeekmusic/covers/专辑艺术家 - 专辑.jpg`，没有专辑时 `艺术家 - 歌名.jpg`，非法字符换成 `_`。查找时两个名字都试
  - 写入文件：通过 SAF 文档 URI 以 `rw` 打开，TagLib `savePictures` 替换 Front Cover、保留其他图片。文件的修改时间和大小会变，所以会被重新扫描，智能标签的签名也变了、需要重新分析
  - 同名图片：`歌曲名.jpg`，已存在时覆盖
  - 写入文件或同名图片失败时改存到隐藏目录；存到别处时删掉这首歌在隐藏目录里原来的封面，否则新封面会被它挡住

### 7.5 队列与续播（QueuePersistence）

- 持久化内容：`List<track_key>`、当前索引、播放进度、循环模式、随机开关（随机顺序本身不保存，恢复时重新生成）
- 存储：`queue_item` 表加上 DataStore 中的标量；当前歌曲的标题、艺术家和封面缓存路径同时写入启动快照（§9.2），以便启动时迷你播放条立即显示
- 写入时机：队列变化时（防抖 1s）；`onIsPlayingChanged(false)` 时；播放中每 10s 写一次进度；`onTaskRemoved` / `onDestroy` 时
- 恢复：把 `track_key` 映射回 track，找不到的跳过；整个队列都失效时为空队列

### 7.6 睡眠定时（SleepTimer）

- 位于 Service 内，状态（剩余时间、模式）通过 `MediaSession` 的 session extras 同步给 UI
- 模式 A（N 分钟）：协程 delay，最后 30s 线性降低 `player.volume`，然后暂停并恢复音量
- 模式 A'（N 分钟 + 播完整首）：delay N 分钟后不渐弱，转为模式 B 且 N = 1
- 模式 B（播完 N 首）：监听 `onMediaItemTransition`，只有自然播完（`AUTO`）或单曲循环重新开始（`REPEAT`）才计数减一，手动切歌不计数；减到 0 时暂停。队列播完（`STATE_ENDED`）时取消
- session extras：`sleep_end_elapsed`（0 表示不按时间）、`sleep_songs_left`（0 表示不按歌曲数）、`sleep_finish_song`
- 对话框里的"播完整首"、自定义分钟数和歌曲数保存在设置（`SleepPrefs`）中，下次打开时沿用

### 7.7 PlayerConnection（UI 侧）

- `@Singleton`，**在首帧之后，或用户第一次操作播放控件时，才建立连接**（`MediaController.Builder(...).buildAsync()`），见 §9.3
- 把播放器状态转换为 `StateFlow<PlayerUiState>`（当前歌曲、是否播放、队列、模式）。连接建立前，用启动快照里的数据填充迷你播放条
- 跳转（`seekTo`）：重启后恢复的队列还没准备，此时 `COMMAND_SEEK_IN_CURRENT_MEDIA_ITEM` 不可用，`MediaController` 会直接忽略 `seekTo(position)`；这种情况下改用 `seekTo(currentMediaItemIndex, position)`（`COMMAND_SEEK_TO_MEDIA_ITEM` 在未准备时也可用），第一次播放就从拖到的位置开始
- 进度条松手后（`SeekState`）：继续显示目标位置，直到轮询到的播放位置与目标相差不到 1.5 秒（最多 3 秒）。否则会先显示轮询到的旧位置、下一次轮询才跳到新位置，看起来像"跳过去、跳回来、再跳过去"
- 迷你播放条的进度条加了 `Modifier.systemGestureExclusion()`：手势导航下从屏幕边缘开始拖动不会触发系统的返回手势。只排除这 28dp 高的一条（系统上限每边 200dp），屏幕其他位置的边缘返回不受影响
- 进度是单独的 Flow，只有在有订阅者时才轮询（`stateIn(WhileSubscribed)`）：播放页可见时每 200ms 一次；迷你播放条的细进度条每 1s 一次；App 在后台时完全不轮询

## 8. 歌词

### 8.1 来源与优先级

```
1. 同目录同名 .lrc（通过 SAF 树 URI 查找，大小写不敏感匹配）
2. 内嵌歌词（TagReader.readLyrics）
3. lyrics_cache（在线获取了但没能写回音乐目录的）
4. 在线获取（仅当开关打开时）：自动模式（仅 Wi-Fi）直接取最佳匹配，否则显示"搜索歌词"按钮
```

内嵌歌词读取 `LYRICS`（ID3 USLT、Vorbis LYRICS、MP4 ©lyr），也兼容一些工具写入的自定义字段 `UNSYNCEDLYRICS`、`USLT`（例如 ffmpeg 会把歌词写成 `TXXX:USLT`）。CUE 虚拟歌曲不读整轨文件的内嵌歌词。

`.lrc` 查找优化：扫描时不处理 `.lrc`。打开播放页时，用 `DocumentsContract` 列出歌曲所在目录（结果按目录缓存在内存 LRU 中）并匹配文件名。切到下一首时预先加载下一首的歌词。

### 8.2 LrcParser

- 支持 `[mm:ss.xx]`、`[mm:ss.xxx]`、`[mm:ss]`，以及一行多个时间戳（`[00:12.00][01:30.00]歌词`）
- 支持 `[offset:+/-ms]` 标签，以及 `[ti:]`、`[ar:]` 等元信息标签（忽略）
- 同一时间戳出现两行时，合并为 `LyricLine(time, text, translation)`（需求 LY-5）
- 没有任何时间戳时判定为纯文本歌词
- 逐字歌词（Enhanced LRC `<mm:ss.xx>`）：先去掉逐字时间标记，按行显示；逐字高亮作为 P2

### 8.3 编码识别（CharsetDetector）

识别顺序：BOM 检测 → 严格 UTF-8 解码成功则采用 → 依次尝试 GBK / Big5 严格解码 → 失败时使用 UTF-8 替换模式。同一个工具也用于 §4.3 的标签修复。

### 8.4 在线歌词（LRCLIB）

- 精确查询：`GET https://lrclib.net/api/get?track_name=&artist_name=&album_name=&duration=`
- 搜索：`GET https://lrclib.net/api/search?track_name=&artist_name=`，结果列表展示标题、艺术家、专辑、时长差和"同步/纯文本"标记，由用户选择
- 设置 `User-Agent: JeekMusic/<version> (https://github.com/tifish/JeekMusic)`，这是 LRCLIB 的要求
- 获取结果按 §8.5 保存；"未找到"写入 `lyrics_cache` 作为负缓存，7 天内不再重复请求
- 网络客户端（`LrcLibClient`）通过 `dagger.Lazy` 注入，只在开关打开后第一次使用时才创建，不影响启动

### 8.5 歌词写回音乐目录（LyricsWriter）

需求 LY-10 / LY-11。设置为"音乐目录"（默认）时：

```
 1. 通过 track.folder_id 找到 library_folder.tree_uri，再按 relative_path 逐级定位到歌曲所在目录的 document
 2. 再次确认目录里没有同名 .lrc（大小写不敏感）；如果有，放弃写入，直接读取已有文件
 3. DocumentsContract.createDocument(dirUri, "application/octet-stream", "<歌曲文件名去扩展名>.lrc")
    mime 不用 text/plain：部分 DocumentsProvider 会自作主张地追加 ".txt" 扩展名
 4. 检查返回文档的实际文件名，不一致（被自动改名）时删除这个文件，当作失败处理
 5. 以 UTF-8（无 BOM）、LF 换行写入
 6. 使该目录在 §8.1 的 LRU 目录列表缓存中失效
 失败（只读、没有写授权、空间不足等）→ 写入 lyrics_cache，并提示"歌词已缓存在 App 内，未能保存到音乐目录"
```

- 写授权在添加文件夹时一并申请。如果某个文件夹只有读授权（比如用户在系统设置里撤销了写权限），第一次写入失败时提示重新授权
- 新建的 `.lrc` 不是音频文件，不会触发 MediaStore 音频变化，也就不会引起重扫
- 设置为"仅 App 内缓存"时，直接写入 `lyrics_cache`

### 8.6 歌词 UI

- `LazyColumn` 显示歌词行。当前行索引通过 `derivedStateOf { 二分查找(position) }` 计算，**只有行号变化时才重组**，200ms 的进度轮询不会让整个列表重组
- 自动滚动用 `animateScrollToItem` 让当前行居中；用户拖动后 3s 内暂停跟随；点击某一行时执行 `seekTo(line.time)`
- 当前行高亮只改变颜色和字重，不做缩放或模糊动画

## 9. 启动性能

目标见需求 §4.1：冷启动 TTID < 400ms，TTFD < 700ms。

### 9.1 原则

**首帧只依赖"小而快"的数据，其他工作一律推迟到首帧之后，并放到后台线程。**

### 9.2 启动关键路径

```
进程启动
 └ JeekApp.onCreate                       （主线程，只做两件事，都是非阻塞的）
     ├ 在 IO 线程开始读 StartupSnapshot（DataStore，约 1–2KB）
     └ 在 IO 线程预热 Room（打开数据库，执行一次首屏查询）
 └ MainActivity.onCreate
     ├ installSplashScreen()；setKeepOnScreenCondition { 快照尚未读完 }（快照读取通常 < 30ms，最多等 200ms）
     └ setContent
         ├ 标签栏：来自快照（不用等数据库）
         ├ 当前页：恢复上次的标签和滚动位置（来自快照）；列表数据来自已在预热中的 Room 查询
         └ 迷你播放条：来自快照中的歌曲标题、艺术家和封面缓存路径（不用等 MediaController）
     └ 列表数据第一次到达 → ReportDrawnWhen → reportFullyDrawn()（TTFD）
```

`StartupSnapshot` 的内容：标签列表、上次选中的标签和滚动位置、主题设置、迷你播放条信息，以及"文件夹"标签的返回栈和每个打开的文件夹的滚动位置（需求 F-8；退出文件夹时丢掉它的位置）。它在对应数据变化时更新（标签栏修改、切歌、退出 App 时）。

### 9.3 推迟执行（DeferredStartupTasks）

在 `reportFullyDrawn()` 之后按顺序执行：

| 任务 | 时机 | 说明 |
|---|---|---|
| 连接 MediaController | 首帧后 500ms，或用户第一次点击播放控件时（以先到者为准） | 连接会创建 PlaybackService 和 ExoPlayer，有一定开销 |
| 打开 Pager 相邻页预加载 | 首帧后 | 让左右滑动切换标签更顺滑 |
| 注册 ContentObserver | 首帧后 1s | |
| 增量扫描 | 首帧后 2s，后台优先级 | |
| WorkManager 初始化 | 第一次需要时（按需初始化，移除默认的 Initializer） | 不在启动时初始化 |
| 网络组件 | 第一次使用在线歌词时 | |

### 9.4 其他手段

- **Baseline Profile + Startup Profile**：`baselineprofile` 模块覆盖以下路径：冷启动、滚动歌曲列表、切换标签、打开播放页。对 Compose 应用来说，这通常能让启动快约 30%
- R8 full mode；只保留必要的依赖（不使用 Firebase 等会在启动时自动初始化的 SDK）
- Hilt：`JeekApp` 中不直接注入重对象；Repository 和 Scanner 用 `Lazy<T>`
- App Startup 中只保留 `ProfileInstaller`
- 使用系统字体，不加载自定义字体；图标使用 Material Symbols 中的少量矢量图
- debug 构建开启 StrictMode，主线程出现磁盘或网络 IO 时直接报错
- **生成 Profile**：在模拟器上执行 `ANDROID_SERIAL=emulator-5554 ./gradlew :app:generateReleaseBaselineProfile`，结果写入 `app/src/release/generated/baselineProfiles/`，提交到仓库。Profile 与 CPU 架构无关，模拟器生成的可以直接用于真机
- **测量**：在真机上运行 `tools/measure-startup.sh`，对比"不编译"和"Baseline Profile"两种编译状态下的冷启动 TTID / TTFD 中位数。每个里程碑记录一次结果
- 模拟器的数据只用于和自身的历史数据对比，不能和真机比较
- **测量记录**：

  | 里程碑 | 设备 | 页面 | 不编译 TTID / TTFD | Baseline Profile TTID / TTFD |
  |---|---|---|---|---|
  | M0（2026-10-16） | X200 Ultra | M0 验证页，启动时查询全部 1687 首 MediaStore 音频 | 134 / 153 ms | **116 / 133 ms** |
  | M1（2026-10-16） | Pixel_10 模拟器 | 正式主界面：标签栏 + 文件夹列表（Room），173 首 | 366 / 447 ms | 395 / 445 ms |
  | M5（2026-10-17） | Pixel_10 模拟器 | 1.0 主界面：5 个标签 + 艺术家列表 + 迷你播放条，1604 首 | 547 / 492 ms（刚开机，波动大） | 408 / 412 ms |

  M0 的页面很简单，这组数据只是起点。之后每个里程碑都要对比：正式主界面加入数据库、标签栏和封面以后，仍然要满足 X200 Ultra 上的内部标准（TTID < 250ms，TTFD < 450ms）
- 模拟器必须使用硬件 GPU 渲染（`dumpsys SurfaceFlinger | grep GLES` 应显示宿主显卡）。从非交互会话（如 PowerShell `Start-Process`）启动时，模拟器会退回 SwiftShader 软件渲染，每帧要 100–400ms，冷启动会变成 700–1000ms，看起来像性能退化
- 没有用 Macrobenchmark 测启动：OriginOS 会拦截它发给 App 的 profileinstaller 广播（`DROP_SHADER_CACHE`、安装 profile），测试直接失败。`baselineprofile` 模块中的 `StartupBenchmarks` 保留着，可以在类原生系统的设备上使用

## 10. UI 结构

```
MainActivity
└── JeekNavHost（全局）
    ├── Onboarding（权限 → 添加文件夹）
    ├── Main
    │   ├── TopAppBar（标题 · 搜索 · 菜单）
    │   ├── ScrollableTabRow（内置标签 + 固定的艺术家和智能标签）
    │   ├── HorizontalPager（每页一个标签，每页内部有自己的 NavHost 和返回栈）
    │   │   ├── 文件夹：FolderRoot → Folder(folderId, path)*
    │   │   ├── 艺术家：ArtistList → ArtistDetail(nameKey)
    │   │   ├── 播放列表：PlaylistList → PlaylistDetail(id)
    │   │   ├── 固定艺术家：ArtistDetail(nameKey)（作为根）
    │   │   └── 智能标签：MoodTracks(mood)（作为根）
    │   │   （Album(albumKey)、ArtistDetail 可以压入任一标签的栈）
    │   └── MiniPlayer（封面 · 标题/艺术家 · ⏮ ⏯ ⏭ · 细进度条）
    ├── Search · Settings · TabManager · SongInfo
    └── NowPlaying（从迷你播放条向上展开的全屏页面）
        ├── 顶部：封面缩略图 + 标题 / 艺术家 / 专辑（可点击跳转）
        ├── 中部：歌词（无歌词时显示大封面；开启在线歌词时显示"搜索歌词"）
        ├── 底部：进度条 · ⏮ ⏯ ⏭ · 循环 / 随机 · 队列 · 更多
        └── 队列（ModalBottomSheet，支持拖动排序）
```

- 第一版只支持竖屏：`MainActivity` 设置 `android:screenOrientation="portrait"`（需求 U-5）。布局里不写死尺寸，以后做横屏和平板时可以按 `WindowSizeClass` 扩展
- 返回键：先在当前标签页的栈内后退；已经在标签页的根时，退出 App
- 从播放页点击艺术家或专辑时：关闭播放页，并把详情页压入当前标签的栈
- 歌曲列表项统一使用 `TrackRow`（§5.7）

## 11. 权限

| 权限 | 条件 | 用途 |
|---|---|---|
| `READ_MEDIA_AUDIO` | API 33+ | 读取 MediaStore 音频 |
| `READ_EXTERNAL_STORAGE` | `maxSdkVersion=32` | 在 Android 12 上读取 MediaStore 音频 |
| `POST_NOTIFICATIONS` | API 33+ | 媒体通知 |
| `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_MEDIA_PLAYBACK` | | 后台播放 |
| `INTERNET` | | 只用于在线歌词 |
| `ACCESS_NETWORK_STATE` | | 判断是否连着 Wi-Fi，用于"仅 Wi-Fi 自动获取" |

SAF 树授权不属于运行时权限，通过 `takePersistableUriPermission` 持久化。启动时（推迟到首帧之后）检查 `persistedUriPermissions`，如果授权已失效（比如 SD 卡更换了），在文件夹列表中标红并提示重新授权。

## 12. 测试策略

| 层次 | 内容 |
|---|---|
| 单元测试（JVM） | `ArtistSplitter`、`FileNameInferrer`、album_key 计算、`CueParser`（GBK 编码、INDEX 00/01、分轨 CUE）、CUE 与音频文件配对规则、备份导出与恢复映射（相对路径、后缀匹配）、`LrcParser`、`CharsetDetector`、SAF documentId → volume/relative_path 转换、文件夹树折叠、自然排序比较器 |
| 音频分析测试（JVM） | 用合成信号验证 `FeatureExtractor`：128 BPM 的节拍器音轨 → bpm ≈ 128；-23 LUFS 的粉红噪声 → loudness ≈ -23；纯低频正弦 → low_ratio ≈ 1。`TagClassifier` 用校准集中真实歌曲的模型得分做表驱动测试；`MigrationTest` 验证数据库迁移 |
| Room 测试 | DAO 查询（艺术家列表、文件夹统计、track_tag 筛选计数、孤儿清理）、迁移测试（4 → 5） |
| 仪器测试 | 扫描器在真实 MediaStore 上的冒烟测试；播放服务的启动、通知、恢复；`PcmDecoder` 对各种格式的解码 |
| 性能测试 | Macrobenchmark：冷启动 / 温启动耗时、歌曲列表滚动掉帧率（`FrameTimingMetric`） |
| 测试素材 | `testdata/` 下放一套小型合成音频（静音或合成片段，加各种标签：GBK 乱码、多值、feat.、无标签、内嵌歌词、24-bit FLAC），用于回归测试 |

## 13. 构建与发布

- Version Catalog 统一管理依赖版本；开启 R8 full mode 和资源压缩
- 版本号：`versionCode` = Git 提交数（`git rev-list --count HEAD`），`versionName` = `1.0.<提交数>`；没有 Git 时为 1
- 签名：本地从 `keystore.properties` 读取，CI 从环境变量（GitHub Secrets）读取，都没有时退回 debug 签名（PR 构建）。**本地和 CI 必须使用同一个签名文件**，App 内更新才能覆盖安装；签名文件和密码丢失后，已安装的 App 无法再升级，必须备份
- 发布：每次推送到 main（只改文档的除外），`.github/workflows/ci.yml` 跑 lint 和单元测试，构建签名的 release APK，以标签 `v1.0.<提交数>` 发布到 GitHub Releases，更新说明是上个版本之后的提交标题
- F-Droid：`fastlane/metadata/android/{en-US,zh-CN}/` 中有标题、简介、更新说明和截图（`tools/store-screenshots.sh` 截取）
- 多语言：`androidResources.generateLocaleConfig = true`，Android 13+ 可以在系统设置里单独设置 App 语言
- GitHub Actions：PR 时只跑 `lint` + `testDebugUnitTest` + `assembleRelease`（debug 签名），不发布。Baseline Profile 在本地生成后提交到仓库
- 许可证：MIT。依赖库的许可证要与之兼容：TagLib 是 LGPL/MPL，以动态链接库（.so）形式分发即可满足 LGPL 要求；在"关于 → 开源许可"中列出所有第三方许可证
- F-Droid：不引入任何闭源依赖（不使用 Firebase / GMS），保证可复现构建

### 13.2 App 内自动更新（AppUpdater）

- 检查：`GET https://api.github.com/repos/tifish/JeekMusic/releases/latest`，从 `tag_name` 末尾的数字得到 versionCode，大于 `BuildConfig.VERSION_CODE` 即为新版本；取第一个 `.apk` 资源的下载地址、大小和 `digest`（SHA-256）
- 时机：首帧之后的推迟任务最后再等 5 秒执行；开关打开、且距上次检查超过 24 小时才联网；网络错误静默忽略。设置里的"检查更新"忽略间隔和"跳过此版本"
- 提示：`UpdatePrompt` 放在最外层（设置页里手动检查时也能显示）："更新 / 跳过此版本 / 以后再说"，下载时显示进度
- 下载：`HttpURLConnection`（GitHub 的下载地址会重定向到另一个 https 域名，默认跟随），写到 `cacheDir/updates/`，边下载边算 SHA-256，和 GitHub 给出的不一致时放弃
- 安装：`PackageInstaller` 会话（`REQUEST_INSTALL_PACKAGES`），结果由 `InstallResultReceiver` 接收：`STATUS_PENDING_USER_ACTION` 时启动系统的确认界面（首次还会要求允许"安装未知应用"）；用户取消时回到"发现新版本"提示，失败时显示原因。签名不一致时系统会拒绝安装
- 免确认：会话设置 `USER_ACTION_NOT_REQUIRED`，并声明 `UPDATE_PACKAGES_WITHOUT_USER_ACTION`（普通权限）。第一次通过 App 内更新安装后，本 App 成为"安装来源"，之后的更新不再弹出系统确认；用 adb 或文件管理器安装的版本第一次更新仍需确认
- F-Droid 不允许应用自更新；以后上架 F-Droid 时需要单独的构建变体关闭这个功能

## 14. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| TagLib JNI 集成的复杂度和 APK 体积（每个 ABI 约 1–2MB） | M0 延期 | 先验证；可以只打包 arm64-v8a 和 armeabi-v7a；备选 jaudiotagger |
| 规则识别运动 / 安静的准确率有限（比如节奏快但很轻的歌） | 标签不准 | 手动修正优先；灵敏度可调；调试页辅助校准；分类器可替换成 AI 模型 |
| 部分设备的 offload 实现有问题（卡顿、进度不准） | 熄屏播放异常 | 只在熄屏和后台时启用；设置里可以关闭；记录问题机型，必要时内置黑名单 |
| 浮点输出在少数设备上不被支持 | 高位深音源退回 16-bit | ExoPlayer 会自动回退；在歌曲信息页显示实际的输出格式，方便排查 |
| 启动目标在低端机上达不到 | 启动慢 | 先保证中端机达标；用 Macrobenchmark 找瓶颈；低端机目标放宽到 TTFD < 1.2s |
| 部分 ROM 的 MediaStore 索引不及时或不完整 | 新歌不显示 | 提供手动重扫；设置页加"让系统重新索引"入口（`MediaScannerConnection.scanFile`） |
| SAF 授权在 SD 卡更换或系统清理后失效 | 无法读取 .lrc 和封面 | 启动后检查并提示重新授权；音频播放不依赖 SAF，不受影响 |
| 写入 .lrc 时各家 DocumentsProvider 行为不一致（自动改名、追加扩展名） | 生成的文件名不对 | 写入前后都校验文件名（§8.5），不一致时回退到 App 内缓存 |
| 文件名推断误判（如 "Live - 2019"） | 产生错误的艺术家 | 只在无艺术家标签时生效；可以在设置中关闭；在歌曲信息页标注"推断自文件名" |
| CUE 相邻两首切换时有停顿 | 现场专辑、古典乐的听感受影响 | M2 阶段实测；不行就改用方案 B（§7.4） |
| 备份写入 SAF 时 DocumentsProvider 不支持重命名，或者写到一半中断 | 备份损坏 | 保留 `backup.prev.json`；恢复时先校验 JSON，损坏则改用上一份 |
| 多个音乐文件夹的备份内容不一致（比如某张 SD 卡当时没有插着） | 恢复到旧数据 | 每份备份都带 `exported_at`；恢复时如果发现多份备份，用最新的那份 |
| 文件移动或重命名后 track_key 失联 | 播放列表、手动标签失效 | 以后可以加按"标题 + 时长 + 大小"模糊重新关联 |
