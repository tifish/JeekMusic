# 两端共用的规则测试用例

移动端（`mobile/app/src/test/`，JUnit）和桌面端（`desktop/tests/JeekMusic.Core.Tests/`，xUnit）的单元测试都读这里的 JSON，
保证两端的曲库规则结果一致（`desktop/docs/design.md` §3）。改规则时先改这里的用例，两端的测试都要通过。

每个文件是 `{ "description": "...", "cases": [ ... ] }`，每条用例都有 `description`，其余字段的含义见各文件开头的 `description`。

| 文件 | 规则 | 移动端 | 桌面端 |
|---|---|---|---|
| `natural-order.json` | 自然排序 | `NaturalOrderComparator` | `NaturalOrderComparer` |
| `sort-keys.json` | 字母索引 | `SortKeys.indexLetter` | `SortKeys.IndexLetter` |
| `pinyin.json` | 拼音转写（ICU "Han-Latin; Latin-ASCII" 的结果） | ICU4J（测试依赖） | `Pinyin.Transliterate` |
| `chinese-text.json` | 繁简转换（ICU 的结果） | ICU4J（测试依赖） | `ChineseText` |
| `charset.json` | 乱码修复、文本编码识别 | `CharsetFixer`、`CharsetDetector` | 同名 |
| `artist-splitter.json` | 艺术家拆分 | `ArtistSplitter` | 同名 |
| `file-name-inferrer.json` | 从文件名推断艺术家 | `FileNameInferrer` | 同名 |
| `album-keys.json` | 专辑归属键 | `AlbumKeys` | 同名 |
| `cue-parser.json` | CUE 解析 | `CueParser` | 同名 |
| `lrc-parser.json` | LRC 解析 | `LrcParser` | 同名 |
| `folder-tree.json` | 子文件夹折叠 | `FolderTree` | 同名 |
| `m3u.json` | m3u 读写 | `M3u` | 同名 |
| `backup-refs.json` | 备份 / 同步的歌曲引用 | `BackupRefs` | 同名 |
| `cover-matcher.json` | 在线结果匹配 | `CoverMatcher` | 同名 |
| `classical-titles.json` | 曲名补救古典 | `ClassicalTitles` | 同名 |
| `tag-classifier.json` | 智能标签分类 | `TagClassifier` | 同名 |
| `workout-v8.json` | 持续强窗与音乐周期的合成契约、三档/失败兼容 | `WorkoutEvidenceTest` | `WorkoutEvidenceTests` |

`workout-feedback.json` 单独保存九个具体录音的人工反馈及核对身份（含新增《简单爱》SPORT与同艺人《东风破》NON_SPORT配对），全部为调参/回归样本，不是可分发音频或生产手工覆盖表。生产分类不得读取这个文件，也不能把这些样本报告为独立准确率。

`workout-representation-protocol.json` 是推理前冻结的研究协议，不是分类用例或产品规则；中间表示稳定性、邻域/身份偏差、新配对与停止条件见[表征评估](../../docs/workout-representation-evaluation.md)。新增录音沿用相同协议，不能据结果回改它后宣称预注册成功。

`pinyin.json`、`chinese-text.json` 的期望值由 `shared/pinyin/generate.sh expect` 用 ICU4J 生成。
更大的对比语料（真实曲库的文件名、标签和随机混排串，约 12 万条）不进仓库，桌面端通过环境变量 `JEEK_PINYIN_CORPUS` 指定后由 `PinyinCorpusTests` 对比。
