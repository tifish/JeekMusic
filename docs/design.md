# JeekMusic 技术设计文档

> 状态：草案 v0.1 · 2026-10-16 · 对应需求见 [requirements.md](requirements.md)

## 1. 技术栈

| 领域 | 选型 | 说明 |
|---|---|---|
| 语言 | Kotlin 2.x | |
| UI | Jetpack Compose + Material 3 | 支持动态取色（minSdk 31 原生支持） |
| 导航 | Navigation Compose（类型安全路由） | |
| 播放 | **Media3**：ExoPlayer + `MediaLibraryService` | 后台播放、通知栏、媒体按键、音频焦点、系统恢复播放都由官方支持；以后加 Android Auto 时不用重构 |
| 数据库 | Room | 曲库缓存、播放列表、歌词缓存 |
| 配置 | DataStore (Preferences) | 设置项、播放状态 |
| 依赖注入 | Hilt | |
| 异步 | Coroutines + Flow | |
| 图片 | Coil 3 + 自定义 Fetcher | 加载内嵌封面和文件夹封面 |
| 标签解析 | **TagLib (JNI)**，候选：jaudiotagger | 见 §4.3，M0 阶段做技术验证 |
| 网络 | OkHttp + kotlinx.serialization | 只用于 LRCLIB |
| 构建 | Gradle Kotlin DSL + Version Catalog | |
| CI | GitHub Actions | 构建、lint、单元测试、Release APK |

## 2. 整体架构

单 `app` 模块，按包分层。代码量增长后再考虑拆模块，对个人项目来说过早拆分多模块弊大于利。

```
┌──────────────────────────── UI 层 (Compose) ────────────────────────────┐
│  folders/   artists/   playlists/   search/   nowplaying/   settings/   │
│                     ViewModel (StateFlow<UiState>)                      │
└───────────────┬─────────────────────────────────────┬───────────────────┘
                │                                     │
┌───────────────▼──────────── 数据层 ─────────┐   ┌───▼──── 播放层 ──────────────┐
│ LibraryRepository   PlaylistRepository      │   │ PlayerConnection (UI 侧)     │
│ LyricsRepository    SettingsRepository      │   │   └ MediaController          │
│ ┌────────────┐ ┌───────────┐ ┌────────────┐ │   │ PlaybackService (独立组件)  │
│ │ Scanner    │ │ Room DB   │ │ LrcLibApi  │ │   │   ├ ExoPlayer                │
│ │ MediaStore │ │           │ │ (可选联网) │ │   │   ├ MediaLibrarySession      │
│ │ TagReader  │ │           │ │            │ │   │   ├ QueuePersistence         │
│ └────────────┘ └───────────┘ └────────────┘ │   │   └ SleepTimer               │
└─────────────────────────────────────────────┘   └──────────────────────────────┘
```

- UI 只通过 ViewModel 访问 Repository，不直接接触 DB 或 MediaStore。
- **播放层与 UI 进程内解耦**：UI 通过 `MediaController` 与 `PlaybackService` 通信，不直接持有 ExoPlayer。系统通知、蓝牙、锁屏走的也是同一条通道，状态天然一致。

### 包结构

```
jeekmusic.tinyfish.com
├── JeekApp.kt                 // @HiltAndroidApp
├── MainActivity.kt
├── data
│   ├── db/                    // Room: entities, DAOs, JeekDatabase
│   ├── library/               // LibraryRepository, FolderRepository, 文件夹树构建
│   ├── scanner/               // MediaStoreSource, TagReader, ArtistSplitter, LibraryScanner
│   ├── lyrics/                // LrcParser, LyricsRepository, LrcLibClient, CharsetDetector
│   ├── playlist/
│   └── settings/              // SettingsRepository (DataStore)
├── playback
│   ├── PlaybackService.kt     // MediaLibraryService
│   ├── MediaItemMapper.kt     // Track → MediaItem
│   ├── QueuePersistence.kt
│   ├── SleepTimer.kt
│   └── PlayerConnection.kt    // UI 侧对 MediaController 的封装，暴露 StateFlow
└── ui
    ├── theme/  navigation/  components/
    ├── folders/  artists/  playlists/  search/
    ├── nowplaying/            // 迷你播放条、全屏播放页、队列、歌词
    └── settings/
```

## 3. 数据模型（Room）

```
library_folder
  id              INTEGER PK
  tree_uri        TEXT UNIQUE    -- SAF 树 URI（已持久化授权）
  volume_name     TEXT           -- MediaStore 卷名："external_primary" 或 SD 卡 UUID
  relative_path   TEXT           -- 以 "/" 结尾，如 "Music/Pop/"
  display_name    TEXT
  added_at        INTEGER

track
  id              INTEGER PK
  track_key       TEXT UNIQUE    -- 稳定键：volume_name + ":" + relative_path + file_name
  mediastore_id   INTEGER
  folder_id       INTEGER FK → library_folder (ON DELETE CASCADE)
  relative_path   TEXT           -- 所在目录，用于构建文件夹树
  file_name       TEXT
  title_tag       TEXT?          -- 原始标题标签
  title           TEXT           -- 展示用：标题标签 → 文件名推断 → 去掉扩展名的文件名
  album           TEXT?
  album_artist    TEXT?
  album_key       TEXT?          -- 专辑归属键，见 §5.3；无专辑标签时为 NULL
  artist_raw      TEXT?          -- 原始艺术家标签，多值以 \u0000 连接；NULL 表示无标签
  artist_display  TEXT           -- 展示用：artist_raw 或文件名推断结果，都没有时为空串（UI 显示"未知艺术家"）
  artist_inferred BOOLEAN        -- artist_display 是否推断自文件名
  track_no / disc_no / year      INTEGER?
  genre           TEXT?
  duration_ms     INTEGER
  mime_type / bitrate / sample_rate
  date_modified   INTEGER        -- 来自 MediaStore，用于增量判断
  size            INTEGER
  has_embedded_lyrics  BOOLEAN
  has_embedded_cover   BOOLEAN
  INDEX(folder_id, relative_path)
  INDEX(album_key)

artist
  id              INTEGER PK
  name            TEXT           -- 首次出现时的写法
  name_key        TEXT UNIQUE    -- 归一化：trim + lowercase，用于合并
  sort_key        TEXT           -- 中文转拼音后的排序键
  index_letter    TEXT           -- A–Z 或 "#"

track_artist      -- 多对多
  track_id        FK → track (CASCADE)
  artist_id       FK → artist (CASCADE)
  PK(track_id, artist_id)

playlist
  id, name, created_at, updated_at

playlist_item
  id              INTEGER PK
  playlist_id     FK → playlist (CASCADE)
  position        INTEGER
  track_key       TEXT           -- ⚠ 引用稳定键，不引用 track.id

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

**关键决策：用 `track_key` 而不是 `mediastore_id` 作为跨表引用。** MediaStore 的 `_ID` 在重建索引或卷重新挂载时可能变化，`track.id` 在"重建曲库"时也会重新生成。基于路径的 `track_key` 能保证播放列表、歌词缓存、续播队列在重扫后依然有效（需求 PL-3）。代价是文件移动或重命名后会失联，对于以文件夹组织曲库的用户来说可以接受。

**艺术家孤儿清理**：每次扫描事务结束时执行 `DELETE FROM artist WHERE id NOT IN (SELECT artist_id FROM track_artist)`。

## 4. 曲库扫描

### 4.1 存储访问策略：SAF + MediaStore 组合使用

| 用途 | 方式 | 原因 |
|---|---|---|
| 用户选择文件夹 | SAF `ACTION_OPEN_DOCUMENT_TREE` + `takePersistableUriPermission`（读 + 写） | 用户体验自然，支持 SD 卡 / U 盘；同时获取写权限，用于保存歌词 |
| 发现音频文件 | **MediaStore** 查询 | 很快（直接查系统索引），而且可以监听变化 |
| 读取音频内容（标签、播放） | MediaStore content URI | 有 `READ_MEDIA_AUDIO` 权限就能访问 |
| 读取 `.lrc` 和封面图片 | **SAF 树 URI** | 分区存储下 `READ_MEDIA_AUDIO` 读不了非媒体文件，但用户授权的树 URI 可以 |
| 写入在线歌词 `.lrc` | **SAF 树 URI**（`DocumentsContract.createDocument`） | 见 §7.5 |

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
 3. 生成 track_key，与 DB 现有记录做 diff：
      新增                                  → 解析标签
      date_modified 或 size 变了（或 Full） → 重新解析标签
      DB 里有但 MediaStore 里没有           → 删除
 4. 用有限并发（Dispatchers.IO，limitedParallelism(4)）解析标签，进度通过 StateFlow 暴露给 UI
 5. 计算派生字段：文件名推断（§4.5）、艺术家拆分（§4.4）、album_key（§5.3）
 6. 分批（每 500 条一个事务）写入 track、artist、track_artist
 7. 清理孤儿 artist
```

触发时机：

- App 启动后在后台跑一次增量扫描（UI 先展示 DB 缓存，不等扫描结束）
- 注册 `ContentObserver` 监听 `MediaStore.Audio.Media.EXTERNAL_CONTENT_URI`，防抖 3s 后增量扫描（只在 App 进程存活时生效）
- 添加或移除文件夹后
- 用户手动操作

扫描在 Application 级 `CoroutineScope` 中执行，同时只允许一个扫描任务；新的请求会合并进当前任务。

### 4.3 标签解析（TagReader）

接口：

```kotlin
interface TagReader {
    suspend fun read(uri: Uri): TrackTags   // 多值字段用 List<String>
    suspend fun readCover(uri: Uri): ByteArray?
    suspend fun readLyrics(uri: Uri): String?
}
```

**选型：TagLib（通过 JNI 封装）为首选。** 它对 mp3、flac、m4a、ogg/opus、ape、wav 的标签支持最全，能正确读出多值字段，可以通过文件描述符（`ParcelFileDescriptor`）读取，不需要真实路径。M0 阶段的验证内容：

1. 选定一个现成的 Android TagLib 封装库，或自己写薄 JNI 层；确认许可证兼容（TagLib 本身是 LGPL/MPL）
2. 用包含中文、多值、各种格式的测试曲库验证正确性，并测出 1 万首的解析耗时
3. 如果不可行，备选 jaudiotagger（纯 Java，在 Android 上需要处理部分兼容问题）

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

- 默认分隔符：`;`、`、`、` feat. `、` ft. `、` featuring `
- 可选分隔符（默认关闭）：`/`、`&`、`,`、` x `
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

## 5. 浏览视图实现

### 5.1 文件夹视图

文件夹树**不扫描文件系统**，而是根据 `track.relative_path` 在内存中构建：

```kotlin
// 查询：某个 root 下、以 path 为前缀的所有不同 relative_path
// → 取 path 之后的第一段作为直接子文件夹，同时统计歌曲数
data class FolderNode(val folderId: Long, val path: String, val name: String, val trackCount: Int)
```

- 根级 = `library_folder` 列表，每个根显示 `display_name` 和其下的总歌曲数
- 子级 = `SELECT relative_path, COUNT(*) FROM track WHERE folder_id=? AND relative_path LIKE ? GROUP BY relative_path`，再在内存中折叠出直接子目录，并按名称自然排序
- 这样空目录自然不会出现（需求 F-3）
- 导航路由：`Folder(folderId: Long, path: String)`

### 5.2 艺术家视图

- 列表：`SELECT artist.*, COUNT(track_id) ... GROUP BY artist.id ORDER BY sort_key`，作为 Flow 输出。1 万首的规模下艺术家通常只有几千个，直接用 `LazyColumn` 即可，不需要 Paging
- 字母索引：按 `index_letter` 分组生成 sticky header，右侧快速滚动条跳到对应分组
- 详情：`SELECT track.* FROM track JOIN track_artist ... WHERE artist_id=?`，在内存中按 `album_key` 分组（为空的归入"未知专辑"），组内按 `disc_no, track_no, file_name` 排序

### 5.3 专辑页

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

入口（需求 AL-1）：`TrackRow` 的"更多"菜单、全屏播放页的专辑名（`MediaItem` 的 extras 里带上 `album_key`）、艺术家详情页的专辑分组标题。路由：`Album(albumKey: String)`。

### 5.4 搜索

M2 先用 `LIKE '%q%'` 查询 `title / artist_display / album / file_name`，在 1 万首规模下足够快。如果性能不够或需要拼音搜索（S-2），再加 FTS4 表，并预存拼音全拼和首字母列。

## 6. 播放层

### 6.1 PlaybackService

```kotlin
class PlaybackService : MediaLibraryService() {
    // ExoPlayer 配置：
    //   setAudioAttributes(USAGE_MEDIA, CONTENT_TYPE_MUSIC, handleAudioFocus = true)
    //   setHandleAudioBecomingNoisy(settings.pauseOnUnplug)
    //   setWakeMode(C.WAKE_MODE_LOCAL)
    // MediaLibrarySession.Callback：
    //   onAddMediaItems     → 根据 mediaId 从 DB 补全 MediaItem（Controller 端只传 id）
    //   onGetLibraryRoot / onGetChildren → 浏览树（为 Android Auto 预留，M1 只实现根和最近队列）
    //   onPlaybackResumption → 从 QueuePersistence 恢复（需求 P-7 系统恢复播放）
    //   onCustomCommand     → 睡眠定时等自定义命令
}
```

- 通知栏使用 Media3 的 `DefaultMediaNotificationProvider`，零额外代码
- `onTaskRemoved` 时如果没有在播放就 `stopSelf()`
- 播放出错（`onPlayerError`）时跳到下一首，并通过自定义事件通知 UI 弹出提示

### 6.2 MediaItem 映射

```
mediaId     = "track:" + track.id
uri         = MediaStore content URI
metadata    = title, artist(artist_display), album, artworkUri, durationMs
artworkUri  = content://jeekmusic.tinyfish.com.artwork/<track_key 的哈希>
```

封面通过自建的 `ArtworkProvider`（ContentProvider）提供，按需提取内嵌封面或文件夹封面，并缩放缓存到 `cacheDir`。通知栏、锁屏、蓝牙车机和 Coil 共用这一个入口，避免重复解码。

### 6.3 队列与续播（QueuePersistence）

- 持久化内容：`List<track_key>`、当前索引、播放进度、循环模式、随机开关（随机顺序本身不保存，恢复时重新生成）
- 存储：一个 Room 表 `queue_item(position, track_key)` 加上 DataStore 中的标量
- 写入时机：队列变化时（防抖 1s）；`onIsPlayingChanged(false)` 时；播放中每 10s 写一次进度；`onTaskRemoved` / `onDestroy` 时
- 恢复：把 `track_key` 映射回 track，找不到的跳过；整个队列都失效时为空队列

### 6.4 睡眠定时（SleepTimer）

- 位于 Service 内，状态（剩余时间、模式）通过 `MediaSession` 的 session extras 同步给 UI
- 模式 A（N 分钟）：协程 delay，最后 30s 线性降低 `player.volume` 后暂停，然后恢复音量
- 模式 B（播完当前这首）：监听 `onMediaItemTransition` 或播放结束，在切换时暂停；单曲循环下同样生效

### 6.5 PlayerConnection（UI 侧）

在 `@Singleton` 中用 `MediaController.Builder(...).buildAsync()` 建立连接，把播放器状态转换为 `StateFlow<PlayerUiState>`（当前歌曲、是否播放、进度、队列、模式）。进度只在全屏播放页可见时以 200ms 间隔轮询，避免无谓刷新。

## 7. 歌词

### 7.1 来源与优先级

```
1. 同目录同名 .lrc（通过 SAF 树 URI 查找，大小写不敏感匹配）
2. 内嵌歌词（TagReader.readLyrics）
3. lyrics_cache（在线获取了但没能写回音乐目录的）
4. 在线获取（仅当开关打开时）：自动模式（仅 Wi-Fi）直接取最佳匹配，否则显示"搜索歌词"按钮
```

`.lrc` 查找优化：扫描时不处理 `.lrc`。打开歌词页时，用 `DocumentsContract` 列出歌曲所在目录（结果按目录缓存在内存 LRU 中）并匹配文件名。

### 7.2 LrcParser

- 支持 `[mm:ss.xx]`、`[mm:ss.xxx]`、`[mm:ss]`，以及一行多个时间戳（`[00:12.00][01:30.00]歌词`）
- 支持 `[offset:+/-ms]` 标签，以及 `[ti:]`、`[ar:]` 等元信息标签（忽略）
- 同一时间戳出现两行时，合并为 `LyricLine(time, text, translation)`（需求 LY-5）
- 没有任何时间戳时判定为纯文本歌词
- 逐字歌词（Enhanced LRC `<mm:ss.xx>`）：先去掉逐字时间标记，按行显示；逐字高亮作为 P2

### 7.3 编码识别（CharsetDetector）

识别顺序：BOM 检测 → 严格 UTF-8 解码成功则采用 → 依次尝试 GBK / Big5 严格解码 → 失败时使用 UTF-8 替换模式。同一个工具也用于 §4.3 的标签修复。

### 7.4 在线歌词（LRCLIB）

- 精确查询：`GET https://lrclib.net/api/get?track_name=&artist_name=&album_name=&duration=`
- 搜索：`GET https://lrclib.net/api/search?track_name=&artist_name=`，结果列表展示标题、艺术家、专辑、时长差和"同步/纯文本"标记，由用户选择
- 设置 `User-Agent: JeekMusic/<version> (https://github.com/<owner>/JeekMusic)`，这是 LRCLIB 的要求
- 获取结果按 §7.5 保存；"未找到"写入 `lyrics_cache` 作为负缓存，7 天内不再重复请求
- 网络模块只在开关打开时才会初始化，代码路径与本地功能隔离

### 7.5 歌词写回音乐目录（LyricsWriter）

需求 LY-10 / LY-11。设置为"音乐目录"（默认）时：

```
 1. 通过 track.folder_id 找到 library_folder.tree_uri，再按 relative_path 逐级定位到歌曲所在目录的 document
 2. 再次确认目录里没有同名 .lrc（大小写不敏感）；如果有，放弃写入，直接读取已有文件
 3. DocumentsContract.createDocument(dirUri, "application/octet-stream", "<歌曲文件名去扩展名>.lrc")
    mime 不用 text/plain：部分 DocumentsProvider 会自作主张地追加 ".txt" 扩展名
 4. 检查返回文档的实际文件名，不一致（被自动改名）时删除这个文件，当作失败处理
 5. 以 UTF-8（无 BOM）、LF 换行写入
 6. 使该目录在 §7.1 的 LRU 目录列表缓存中失效
 失败（只读、没有写授权、空间不足等）→ 写入 lyrics_cache，并提示"歌词已缓存在 App 内，未能保存到音乐目录"
```

- 写授权在添加文件夹时一并申请。如果某个文件夹只有读授权（比如用户在系统设置里撤销了写权限），第一次写入失败时提示重新授权
- 新建的 `.lrc` 不是音频文件，不会触发 MediaStore 音频变化，也就不会引起重扫
- 设置为"仅 App 内缓存"时，直接写入 `lyrics_cache`

### 7.6 歌词 UI

`LazyColumn` 加上当前行索引（根据播放进度二分查找）。自动滚动用 `animateScrollToItem` 让当前行居中；用户拖动后 3s 内暂停跟随；点击某一行时执行 `seekTo(line.time)`。

## 8. UI 结构

```
MainActivity
└── JeekNavHost
    ├── 首次引导 Onboarding（权限 → 添加文件夹）
    └── MainScaffold
        ├── 内容区（底部标签页）
        │   ├── 文件夹：FolderRoot → Folder(folderId, path)*
        │   ├── 艺术家：ArtistList → ArtistDetail(artistId)
        │   │   （任一标签页的栈内都可以压入 Album(albumKey)）
        │   └── 播放列表：PlaylistList → PlaylistDetail(id)
        ├── 搜索（顶栏入口）
        ├── 设置（顶栏菜单）
        ├── MiniPlayer（底部导航上方）
        └── NowPlaying（全屏 BottomSheet / 独立页面）
            ├── 封面页 ⇄ 歌词页（HorizontalPager）
            └── 队列（ModalBottomSheet，支持拖动排序）
```

- 每个标签页有自己的返回栈，切换标签时保留各自的浏览位置
- 歌曲列表项使用统一的 `TrackRow` 组件：封面缩略图、标题、副标题、时长、更多菜单；当前播放的歌曲会高亮

## 9. 权限

| 权限 | 条件 | 用途 |
|---|---|---|
| `READ_MEDIA_AUDIO` | API 33+ | 读取 MediaStore 音频 |
| `READ_EXTERNAL_STORAGE` | `maxSdkVersion=32` | 在 Android 12 上读取 MediaStore 音频 |
| `POST_NOTIFICATIONS` | API 33+ | 媒体通知 |
| `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_MEDIA_PLAYBACK` | | 后台播放 |
| `INTERNET` | | 只用于在线歌词 |
| `ACCESS_NETWORK_STATE` | | 判断是否连着 Wi-Fi，用于"仅 Wi-Fi 自动获取" |

SAF 树授权不属于运行时权限，通过 `takePersistableUriPermission` 持久化。启动时检查 `persistedUriPermissions`，如果授权已失效（比如 SD 卡更换了），在文件夹列表中标红并提示重新授权。

## 10. 测试策略

| 层次 | 内容 |
|---|---|
| 单元测试（JVM） | `ArtistSplitter`、`FileNameInferrer`、album_key 计算、`LrcParser`、`CharsetDetector`、SAF documentId → volume/relative_path 转换、文件夹树折叠、自然排序比较器 |
| Room 测试 | DAO 查询（艺术家列表、文件夹统计、孤儿清理）、迁移测试 |
| 仪器测试 | 扫描器在真实 MediaStore 上的冒烟测试；播放服务的启动、通知、恢复 |
| 测试素材 | `testdata/` 下放一套小型合成音频（静音片段加各种标签：GBK 乱码、多值、feat.、无标签、内嵌歌词），用于回归测试 |

## 11. 构建与发布

- Version Catalog 统一管理依赖版本；开启 R8 和资源压缩
- GitHub Actions：PR 时跑 `lint` + `testDebugUnitTest` + `assembleDebug`；打 tag 时构建签名的 release APK 并上传到 Releases（签名密钥放在 Secrets 中）
- 许可证：MIT。依赖库的许可证要与之兼容：TagLib 是 LGPL/MPL，以动态链接库（.so）形式分发即可满足 LGPL 要求；在"关于 → 开源许可"中列出所有第三方许可证
- F-Droid：不引入任何闭源依赖（不使用 Firebase / GMS），保证可复现构建

## 12. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| TagLib JNI 集成的复杂度和 APK 体积（每个 ABI 约 1–2MB） | M0 延期 | 先验证；可以只打包 arm64-v8a 和 armeabi-v7a；备选 jaudiotagger |
| 部分 ROM 的 MediaStore 索引不及时或不完整 | 新歌不显示 | 提供手动重扫；设置页加"让系统重新索引"入口（`MediaScannerConnection.scanFile`） |
| SAF 授权在 SD 卡更换或系统清理后失效 | 无法读取 .lrc 和封面 | 启动时检查并提示重新授权；音频播放不依赖 SAF，不受影响 |
| 写入 .lrc 时各家 DocumentsProvider 行为不一致（自动改名、追加扩展名） | 生成的文件名不对 | 写入前后都校验文件名（§7.5），不一致时回退到 App 内缓存 |
| 文件名推断误判（如 "Live - 2019"） | 产生错误的艺术家 | 只在无艺术家标签时生效；可以在设置中关闭；在歌曲信息页标注"推断自文件名" |
| 文件移动或重命名后 track_key 失联 | 播放列表条目不可用 | 以后可以加按"标题 + 时长 + 大小"模糊重新关联 |
