# 封面 / 歌词写入编码调查（2026-10-25）

## 已确认的缺陷

基线 `2153a59` 的桌面生产 `TagWriter.Write` 会在只嵌入封面、只嵌入歌词或同时嵌入时，把原本正确的 UTF-8 / UTF-16 中文 ID3 标签改成问号。**这是 JeekMusic 可复现的破坏性写入缺陷，不能把“文件已损坏”作为排除软件责任的理由。** 用户已确认手机的文字与本机《花火》样本的问号相同。

原因是 `TagReader.Configure` 为读取旧标签设置了全局 `ATL.Settings.DefaultTextEncoding = Encoding.Latin1`；同一进程的 ATL 7.18.0 保存会使用这个设置重写 ID3v2 所有文本，不只新增的封面 / 歌词。实际依赖程序集反编译确认：

- ID3v2 读取编码字节 `0` 时使用固定的 `Utils.Latin1Encoding`，与 `DefaultTextEncoding` 无关。
- ID3v2 写入直接选取 `Settings.DefaultTextEncoding`。Latin-1 无法编码中文，默认替换回退写出 `0x3f`，并把编码字节设为 `00`。
- `Track.Save` 同步现有 ID3v1；该格式的写入也固定使用 Latin-1，所以只修好 ID3v2 默认编码还不足以保护现有 ID3v1。

依赖 NuGet `z440.atl.core/7.18.0` 声明的源提交为 `61fbf286d99d04f6d668e944bc9c57c007ffd2c2`。核实使用的是本机实际 ATL.dll，不以当前上游分支代替安装版本。

## 对照实验与原始字节

先用 FFmpeg 生成无标签的 0.12 秒静音 MP3，再手工加入已知原始标签。独立小程序链接工作树的实际 `TagWriter.cs`、`TagReader.cs`、`Log.cs`、`AppPaths.cs` 和实际 ATL.dll，仅用常量代替数据库类型的 `MultiValueSeparator`，不启动客户端或数据库。后续正式 xUnit 测试直接引用完整生产应用程序集。

在改动生产代码前，对 UTF-8 v2.4、UTF-16 v2.3、误标 Latin-1 的 GBK v2.3 各做三种嵌入，共 9 次。所有写入返回成功。UTF-8 / UTF-16 的结果相同：

| 字段 | 正确输入 | 输出编码字节后的原始字节 | 输出文字 |
| --- | --- | --- | --- |
| TIT2 | 花火 (Live版) | `3f3f20284c6976653f2900` | `?? (Live?)` |
| TPE1 | 周深 | `3f3f00` | `??` |
| TALB | 时光音乐会2 第6期 | `3f3f3f3f3f32203f363f00` | `?????2 ?6?` |

全部输出变为 ID3v2.4、文本编码 `00`，与原歌曲同三个帧的内容完全相同；写入的新中文歌词同样变成问号。GBK 测试的既有 Latin-1 字节串可表示，原字段没有丢字，但新中文歌词仍损坏。

对输出重新按 ID3 头和尾部 ID3v1 边界提取完整 MPEG 音频负载。9 次均与原始负载逐字节相同，SHA-256 为 `367b552ca656d360adf45f4f20c164d29a103a3ad2c2bafe6b36d9183b0bfd52`。这是标签损坏，没有在实验中发现音频负载损坏。

## 最小预防修复

- 全局默认保存编码设为 UTF-8，不增加任何中文猜测转码；ID3 的 Latin-1 读取仍按其编码标记，现有 GBK 显示修复保持有效。
- 在暂存副本中保存前读取原有 ID3v1，保存成功后恢复其全部 128 字节。封面 / 歌词不属于 ID3v1，不应借机同步改写它的文本。
- ID3v1 尾部恢复只适用于 MP3、APE、WavPack、TAK、Musepack；不对 WAV、FLAC、MP4、Ogg 等容器追加尾标签。ATL 7.18.0 还声明 WAV 支持 ID3v1，实际可能把 RIFF 内最后 128 字节的 `TAG` PCM 误认成标签；容器检测到这种歧义时，在保存前取消写入并记录原因，源文件完整不变，沿用已有封面 / 歌词回退保存流程。
- 保留原有同卷暂存、变更检查、mtime / 创建时间恢复及最后替换流程；保存或恢复失败时丢弃暂存文件，不替换源文件。
- 成功操作新增 `[tagwriter]` 日志，记录路径、封面 / 歌词操作及 ID3v1 保留状态。既有 MCP `get_logs(kind=app)` 读取相同日志，接口 / Schema 不变。
- 不修改已有问号标签，不触发批量重扫或数据库迁移，不用坏文件标签覆盖桌面缓存中的正确文字。

旧 GBK 的 Latin-1 字节串经 UTF-8 序列化后仍可无损恢复，JeekMusic 现有 `CharsetFixer` 显示结果不变；这不是把所有旧标签自动识别并改写成中文。ID3v2 的编码 / 序列化形式会变化，不声称所有标签原始字节不变。原有 USLT 在 ATL 保存时可能增加外围换行，回归比较歌词文字时仅归一化这些换行。

## 修复验证

`TagWriterEncodingTests` 24 个组合：UTF-8、UTF-16、误标 Latin-1 的 GBK、合法西文重音 × 仅封面、仅歌词、联合 × 有 / 无 ID3v1。检查生产读取值、独立解码的原始文本帧、中文 / 希腊文 / 阿拉伯文 / 扩展汉字 / emoji、歌词、封面、完整音频负载、ID3v1 原字节、mtime、暂存清理和成功日志。另跑原有 WAV 联合保存 / 无声 PCM 解码验证、锁定文件失败保护，共 26 项通过，0 失败 / 跳过。

另执行 Core.Tests 的 `--filter 'FullyQualifiedName~Text'`，既有编码 / 文本共享回归 10 项通过，0 失败 / 跳过；记录为 `test-results/text-regression.trx`。当前 ADB 无连接设备，没有运行中的本任务模拟器。

```powershell
$env:JEEKMUSIC_DATA = Join-Path (Get-Location) 'mobile/build/huahuo-investigation/test-app-data'
$env:TEMP = Join-Path (Get-Location) 'mobile/build/huahuo-investigation/test-temp'
$env:TMP = $env:TEMP
dotnet test desktop/tests/JeekMusic.App.Tests/JeekMusic.App.Tests.csproj -p:_IsPublishing=true --filter 'FullyQualifiedName~TagWriter' --logger 'trx;LogFileName=tag-writer-fixed.trx' --results-directory mobile/build/huahuo-investigation/test-results
```

`_IsPublishing=true` 只用于跳过桌面日常输出目录同步；未发布、启动、连接或重启桌面客户端。首次测试发现隔离 worktree 缺 libmpv，后下载仓库已有 LGPL Release 的依赖到本 worktree，原有无声 PCM 测试也通过，没有使用当前桌面进程。

对实际歌曲的本地副本另做修复后的三种写入。完整音频负载哈希三次均为 `8f86b470849cfbdc83dd6a69ddd65e6a0a8993568a0331649ebd69e3aa3c51af`，ID3v1 字节不变；问号不会凭空恢复。原路径未被写入，整文件 SHA-256 仍为 `92de4f14a7500838d6cfa3c9c002e0917508896b49ce7b5d25fd02d31fbbd840`。

本地证据均在忽略的 `mobile/build/huahuo-investigation/`：`writer-probe/before-fix-bytes.json`、`writer-probe/after-fix-bytes.json`、`after-fix-writer.jsonl`、`test-results/tag-writer-fixed.trx` 和实际依赖反编译文件。真实歌曲副本未提交或上传；提交的 MP3 fixture 只有合成静音。

## 主分支整合验证

整合时补充 WAV PCM 尾部恰好为 `TAG` 的安全取消回归，以及 WAV、FLAC、AAC/M4A、Ogg Vorbis 的中文、重音、阿拉伯文、扩展汉字和 emoji 读写兼容回归。FLAC / M4A / WAV 比较真实生产无声 PCM 解码；Ogg 独立解析全部页和分段，除 Vorbis 注释包外逐包比较 codec / 音频原字节。全部 fixture 为合成静音。

短 Ogg 的 mpv 命名管道截窗出现长度不稳定，未将它当作写入损坏结论：单独用旧 Latin-1 和新 UTF-8 设置各保存一次后，45 个完整音频包均与输入完全一致；独立 FFmpeg 全长解码的 175888 字节 PCM 三者 SHA-256 均为 `c3f5549bb70dd9bb8479a8b22a48dd69dd2ece63e46d7b9661bbdc966328bf54`。自动回归改用确定性的全部 Ogg 包比较，没有降低音频覆盖范围。

本轮全套 Release 回归：App 433 通过、1 跳过，Core 70 通过；跳过项是需要单独显式启用的真实端点完整格式测试，没有宣称通过。构建工作流约束测试 7 通过。证据位于忽略的 `desktop/artifacts/tag-write-validation/`，完整 TRX 在 `results/final/`。MCP `get_logs` 的工具描述同步明确成功日志字段、无歌词内容和旧日志边界，输入 Schema / 工具数不变。

新托管 Release 客户端 PID 13436 使用隔离数据目录 `managed/data`。代理通过真实 Windows 设置界面将歌词、封面两项改为「写入音乐文件」，然后经正式 MCP `add_folder` 加入仅含三首合成静音的目录；已有离线同名 JPEG / LRC 走生产 `EmbedPrevious`，分别完成封面、歌词、联合写入，日志明确记录三个操作组合和 `id3v1Preserved=True`。未调用 mock 写入接口。UI 实际双击联合样本播放无声内容、打开歌词面板并暂停，歌名、歌手、专辑、青色封面和多语歌词均正确。截图 `evidence/managed-embed-settings.jpg`、`evidence/managed-combined-result.jpg`，对应可访问性全文 `evidence/managed-combined-tree.txt`。

`managed/before.json` / `after.json` 由独立 Python 原始 ID3 解码生成：三个样本的完整 MPEG 负载 SHA-256 均保持 `18954b9008c4fdd983a6dd559fd8bfc727ba2b05e57182ab941c6cf3211abbff`，全部原 ID3v1 字节及 mtime 不变，TIT2 / TPE1 / TALB 原文字不变，APIC / USLT 与所选操作一致，成功后同名 JPEG / LRC 已按原业务规则清理。`managed/mcp-tagwriter-logs.json` 为真实 MCP `get_logs` 读回。既有 `TryDeleteLyrics` 在隐藏目录尚不存在时会输出清理警告，写入及同名歌词清理均已独立核实成功；本轮不作无关清理重构。

## Android 与历史归因边界

Android 的 `LyricsRepository.embedOperation` 读取并全量回写 propertyMap；`CoverStore.embed` 调用 `savePictures`，联合操作会继续写歌词。`AudioFileEditor` 原地写入，恢复 mtime 并更新大小 / 分析签名，不重新读取标题、艺术家、专辑。桌面 `MarkEmbedded` 同样更新大小 / mtime / 标志而不重读文本；因此桌面库仍有中文与文件损坏可以同时发生，缓存正确不能排除嵌入损坏。

已检出 Android 依赖 `Kyant0/taglib` 的 `1.0.6` 源提交 `957fd7c2794457a14c1484fa3e3b58382aca4202`。其 JNI 对属性值使用 `GetStringUTFChars` → `TagLib::String(..., UTF8)`，读回使用 `NewStringUTF`；封面路径写复杂属性后调用原生 `save()`。没有发现与桌面同一个 ATL 全局 Latin-1 设置，但这不能代替 Android 运行验证。JNI modified UTF-8 与非 BMP 字符的边界也未在设备上验收，不据此猜测修补。

依赖所指 TagLib 子模块提交 `1cbd910c3da4818e835ee6d6e467157366d5f1bb` 抓取返回 `not our ref`，未完整核实该精确原生版本的默认保存编码。本轮遵守限制，没有再次启动此前卡死的模拟器，也未用等价 mock 声称 JNI 安全；Android 仅封面 / 仅歌词 / 联合写入的真实原生验收与截图仍缺失。

有限只读检查本地 `app.log` 和 `playback.log`：该歌曲只有播放记录，未找到成功嵌入日志；旧 `TagWriter` 本来也只记录失败。原样本 TSSE 为 `Lavf58.76.100`、备注为 `Create by taurusxin/ncmdump.`，这些既有字段可在后续保存时保留，不能证明最后一次标签写入来自哪个程序。mtime 被两端代码主动恢复，不能用于推断写入时间。Git 历史显示全局 Latin-1 配置来自 `27efc47`，调用该配置的桌面写入器来自 `21836b5`；这些说明缺陷的代码来源，不是用户具体文件的操作记录。

目前可以明确回答：**JeekMusic 桌面写封面或歌词确实会触发这种损坏；这首具体文件的形态与复现完全一致，属于强相关线索，但没有足够历史证据断言是哪一次、哪个端实际写坏。** 本修复只预防以后再损坏，已有文件恢复仍由父会话另行协调授权和备份；不自动修改原音乐。
