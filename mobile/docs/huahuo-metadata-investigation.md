# 《花火（周深）》Android 元数据调查（2026-10-25）

## 状态与范围

用户已明确问题在歌名、歌手或专辑文字，不是歌词。本轮基于 `2153a59`，在独立分支 `t3/android-huohua-garbled-text` 调查；T3 返回的配置为 `codex / gpt-6.1-sol / reasoningEffort=high`。

**已确认本机样本的标签内容本身已是问号，尚未确认用户手机上的文件是否与样本一致。未修改产品编码规则，没有声称修复或通过手机验收。**

## 原文件证据（只读）

父会话提供的文件：`D:\Mobile\Song\散曲\周深 - 花火 (Live版).mp3`。

- 大小：11,887,069 字节。
- SHA-256：`92de4f14a7500838d6cfa3c9c002e0917508896b49ce7b5d25fd02d31fbbd840`；调查开始和结束相同。
- ID3v2.4，标签头 flags 为 0。以下三个文本帧 flags 均为 0，编码字节均为 `00`（Latin-1）。表中的字节从编码字节之后开始，包括结尾的 `00`。

| 帧 | 原始文本字节（十六进制） | 按声明解码 |
| --- | --- | --- |
| TIT2 | `3f3f20284c6976653f2900` | `?? (Live?)` |
| TPE1 | `3f3f00` | `??` |
| TALB | `3f3f3f3f3f32203f363f00` | `?????2 ?6?` |

ID3v1 的标题、歌手、专辑也保存相同问号。逐帧检查没有找到另一组完整的标题、歌手或专辑；文件尾没有 APE 标签 footer。独立执行以下命令也得到表中相同结果：

```powershell
ffprobe -v error -show_entries format_tags=title,artist,album,encoder -of json 'D:/Mobile/Song/散曲/周深 - 花火 (Live版).mp3'
```

`0x3f` 就是 ASCII 问号，不是待恢复的 GBK / UTF-8 中文字节；仅改变解码无法还原丢失的信息。父会话告知桌面库 track 1602 保存的是「花火 (Live版) / 周深 / 时光音乐会2 第6期」，但本轮未连接或操作桌面客户端，也未自行读取桌面数据库。库中的正确值不等于当前文件仍含完整标签，损坏发生的时间和写入方尚未确认。

机器可读证据位于工作树内忽略的 `mobile/build/huahuo-investigation/source-metadata.json`；没有复制或上传原音乐，没有修改音乐或歌词文件。

## Android 生产代码链路

1. `MediaStoreSource.query` 只查询位置、文件名、大小、修改时间和音频属性，不使用 MediaStore 的 TITLE / ARTIST / ALBUM。
2. `TagReader.read` 使用 TagLib 1.0.6 的 `propertyMap`，对 TITLE、ARTIST、ALBUM、ALBUMARTIST 等调用 `CharsetFixer.fix`。
3. `CharsetFixer` 对纯 ASCII 原样返回；这份样本的问号标签不可能通过现有 GBK 修复获得中文。含 Latin-1 以外字符的合法 Unicode 也原样返回，包括中外文混排。没有理由增加全局启发式转码。
4. `TrackFactory.build` 优先采用非空标题和歌手标签，因此 `??` 仍会被视作有效标签。文件名推断只在缺少歌手时生效；它也无法恢复未知专辑。此行为由代码确认，未在 Android 原生 TagLib 上执行验证。
5. Room 数据库为 `jeek.db`（schema 6）。`title_tag`、`artist_raw` 已是 TagReader 处理后的字符串，保留不了文件的编码字节；`album` 也是直接存解析结果。不能拿这些字段做有依据的再次转码。
6. `MediaItemMapper` 从 Room 的 `title`、`artistDisplay`、`album` 创建播放元数据；列表也直接显示这些字段。主题使用系统 Material 字体。本轮没有截图，未排除用户设备字体问题；本机文件中的问号则已独立于字体存在。

## 现有数据刷新边界

- `LibraryScanner.scan` 的增量判断仅比较文件修改时间和大小，普通重扫不会重新解析未变文件的标签。未来若证实需要修改解析器，仅升级代码不能保证刷新已入库内容。
- `requestScan(full = true)` 会重新读取标签，并复用已有 track id / trackKey，更新艺术家关联；它不是清空用户数据库。旧标签编码设置变化时，`ArtistSettingsViewModel.apply` 已使用这条路径。
- `rebuildArtists` 只用库中已有字段重算，无法恢复原始标签。本样本即使全量扫描也只能再次读到问号，不应把重扫或清库当作修复。
- 播放队列的 MediaItem 与启动快照还保存标题 / 歌手副本；未来确实修正标签时，应同时验证列表、播放页、通知和进程恢复，不只检查新导入结果。
- 未清库、清数据或更改包名 / 签名；未新增无关 Windows MCP 接口。

## 验证与设备阻断

在 `mobile/` 执行成功：

```powershell
.\gradlew.bat :app:testDebugUnitTest --tests 'com.tinyfish.jeekmusic.data.scanner.CharsetFixerTest' --tests 'com.tinyfish.jeekmusic.data.lyrics.LrcParserTest' --console=plain
.\gradlew.bat :app:assembleDebug --console=plain
```

两个测试类共 4 个 JVM 测试方法，0 失败 / 错误 / 跳过。它们使用既有共享用例，不是实际歌曲或 Android JNI 验证。歌词测试发生在位置澄清之前。测试 XML 位于 `mobile/app/build/test-results/testDebugUnitTest/`，Debug APK 位于 `mobile/app/build/outputs/apk/debug/app-debug.apk`。这是未修改产品代码的基线包，不是修复包。

T3 `device_list` 返回 `Agent device access is turned off for this environment.`；最初 `adb devices -l` 无设备。本机存在 `AppLimiterOwnerSimplify`、`JeekMusicAcceptance`、`Pixel_10` 三个 AVD。

仅尝试 `JeekMusicAcceptance`，固定 serial `emulator-5570`，以 `-read-only -no-window -no-audio -no-snapshot-load -no-snapshot-save` 启动 Android 16。第一轮基线 Debug 安装返回 Success，创建临时 Android 用户 10 后，`am start-user -w 10` 和独立 shell / exec-out 探测均卡住。结束本任务模拟器后，第二轮只读冷启动确认只有原 user 0（上一轮数据未保留），未再创建用户；安装及后续 shell 仍卡住。每次探测有 8 秒超时；定向 `adb reconnect` 后返回 `device offline`，安装报 `failed to run abb_exec. Error: closed`。

两轮均未成功启动客户端或取得原生截图，未把文件传入模拟器，未操作用户手机、Windows 鼠标键盘或桌面 JeekMusic。只结束了经 PID、父 PID 和 port 5570 校验属于本任务的模拟器进程，没有停止共享 adb server。原生读取、实际 UI、用户手机同文件比较均未验收。模拟器日志位于 `mobile/build/huahuo-investigation/emulator*.log`。

## 交回父会话

当前不宜合入转码修补，也不宜覆盖安装到用户手机。需要手机实际显示的错误原文或截图，以及手机对应文件的标签证据 / SHA-256，确认是否同一份损坏数据。如果手机也显示本样本的问号，后续应另行决定数据恢复来源或明确的损坏标签降级策略；不能靠换字符集恢复，也不能猜测专辑。保留正常 Unicode、多语文字、用户原文件和现有曲库数据。
