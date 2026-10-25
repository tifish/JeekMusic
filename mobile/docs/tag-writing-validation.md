# Android 原生标签写入与验证

本文区分曲库读取/播放能力、可验证的原生写入包络和外部文件回退。写入只在用户请求封面/歌词时发生，不扫描转换曲库、不修复或猜测重编码已有文本。原有 `?` 是合法字符，原始非目标 GBK、Latin-1、UTF-16 和 UTF-8 标签均保留。

策略对齐共享 policy2（`a12b73e03d2dd40d55083d24bb514759f1cf58ef`）；该提交没有改共享 corpus，测试资产仍固定到 `167d97ef` 的 manifest。Android 未复制 PC 的两步 rename/恢复协议。

`7c439e2` 独立审查的两项 P1、一项 P2 已另行复现修复；本轮候选竞争、嵌套 MP4 定位及 ID3 状态标志的逐项证据见 [审查修复验证](tag-writing-review-validation.md)。下文 252/132 数量与原 `tag-writing-api35.json` 属于首轮记录，不冒充本轮重跑。

## 支持矩阵

曲库 `MediaStoreSource` 接受 `mp3 flac m4a mp4 aac ogg oga opus wav mka amr`；读取使用固定的 TagLib 及系统媒体组件，具体音频播放还受设备解码器支持限制。扩展名不是写入能力声明。

| 实际包络 | 原生封面/歌词写入 | 编码与保护 | 明确回退边界 |
|---|---|---|---|
| MPEG MP3，无 ID3v2 | write | 新建一个 ID3v2.4，目标 UTF-8；整个音频及原有 ID3v1/尾部原样复制 | 非 MPEG 起点、叠放标签 |
| MP3 ID3v2.3 | write | 保留 v2.3，APIC/USLT 使用 UTF-16/BOM；不迁移 TYER/TDAT/TIME/TORY/IPLS 等帧 | v2.2、非零 tag flags/扩展头/CRC/全局 unsync/footer；编码或只读目标帧；CHAP/CTOC/ASPI/SEEK 位置引用；任何帧声明 tag-alter invalidation（v2.3 status 0x80 / v2.4 0x40） |
| MP3 ID3v2.4 | write | 目标 UTF-8；所有非目标帧（含未知/私有帧、日期精度、多值）保持原始字节 | 同上；损坏边界 |
| 原生 FLAC | write | 原生 PICTURE block、UTF-8 Vorbis comment；STREAMINFO、APPLICATION、未知块、原 vendor/非目标 comment entries 保留 | ID3 前缀 FLAC、重复 comment blocks、坏边界 |
| Ogg Vorbis / Opus 单逻辑流 | write | 原生 UTF-8 comment/`METADATA_BLOCK_PICTURE`；保留 vendor、重复/空值、未知字段与非目标图片；Opus comment 尾部扩展原样保留 | chained/multiplexed、header/audio 混合页、CRC/边界错误、旧 COVERART 字段歧义 |
| MP4 / M4A | write（有界包络） | 原生 `covr` typed image 和 `©lyr` UTF-8 data atom；原有歌词 locale 保留；安全包络内非目标 typed/私有/未知叶 atom 原样保留 | fragmented/compressed/protected/indexed movie、多 metadata/歌词槽；CENC/protected sample entries、sinf/tenc/pssh、saio/saiz/senc、结构树中的 iloc 及未分类 uuid 拒绝（包括顶层、moov/udta/meta）；fast-start 增长超出既有 padding 时回退，不重定位 mdat/stco/co64 |
| AAC/ADTS、WAV/BWF、MKA、AMR | refuse embed | 保留既有读取/播放路径；使用隐藏目录、同名文件或歌词缓存 | 无已验证的原生安全编辑器；不强套 UTF-8/ID3 |
| APE/WavPack、ASF/WMA、AIFF | 不新增手机支持；refuse embed | 不在当前曲库 advertised extensions；合成样本用于拒绝及原字节保护回归 | 不因底层库可识别就宣称支持原生写入 |

MP4 在所有其他顶层 atoms 后的 moov 可以增长；否则只能在原 moov/相邻 free/skip 空间内更新，其他顶层 atoms 的绝对位置不变。所有格式的 metadata 分配有 32 MiB 上限；MP4 当前拒绝单个 extended-size atom 超过 32 位无符号长度的包络。音频流式复制，没有沿用 PC 的 256 MiB 全文件分配上限，但未用多 GiB 文件实测；源/副本/验证输出同时存在时需要足够临时磁盘空间。超出边界回退，原件不变。

## 目标与事务语义

- `TagEdit` 的 null 目标完全不动。封面只替换 role 0/3，其他 role（包括未知角色）、描述和图片二进制保持原字节；MP4 covr 视为显式整个字段。
- 歌词全文不规范化换行、不合并双语时间戳、不截断空行/offset。单个 USLT 保留语言/描述身份，单个 comment 保留 key 拼写，MP4 保留 locale；多个歌词槽、SYLT、自定义歌词别名歧义拒绝。封面单独操作不解析或改写歌词。
- `CoverStore` 的联合操作一次提交。任一目标失败即不提交；`alsoDone` 仅在最终提交成功后记录，`LyricsRepository.onEmbedded` 才能删除外部歌词/记已嵌入。拒绝返回真实隐藏目录/缓存位置，不能误报为已嵌入。
- `AtomicAudioFile`：SAF `rw` 授权 → 本地路径与 fd 的 dev/inode/stat 比较 → 同目录副本与源 SHA → 目标编辑 → 打开候选 fd 并记录验证前 SHA/身份 → 独立重解析目标/非目标字段及音频原始边界 → 核对验证前后候选身份/SHA → 文件权限/所有者和 mtime 校验 → 获得源及候选 OS advisory 写锁 → 在同一双锁区间查播放状态、源 SHA/stat、已验证候选 SHA/fd/path stat 并执行 `Os.rename`。候选锁在验证器/属性 API 关闭额外 fd 后取得；双锁区只用已打开的 channel 读取 SHA，不另开关同 inode fd。源 fd 从不用于写入，不在失败后用旧 SHA 重试替换。
- 保留旧 ID3v1 的所有 128 字节；原本没有就不新增。MP3 以外的音频尾部即使出现 `TAG` 也不按 ID3v1 处理。
- OS 锁已用独立 `app_process` 进程实测竞争/释放：遵守 POSIX advisory 锁协议的写入者被覆盖整个最终检查/替换阶段。它不是 Windows 强制共享锁，也不是内核 compare-and-swap；不遵守锁协议的外部写入者，以及同进程其他 fd 关闭对 POSIX 锁的影响仍不能由 Android 公共 API 完全排除。此限制不以模拟测试掩盖。设备/文件系统不允许锁、保留 mtime、mode/uid/gid 或原子重命名时使用外部回退。未声称验证所有 OEM/可移除存储及 ACL 实现。

## 精确依赖与已确认缺陷

依赖仍为 `io.github.kyant0:taglib:1.0.6`，没有改包名、签名配置或清用户数据。

| 对象 | SHA-256 / commit |
|---|---|
| Maven AAR | `fe29a3ced5cab0ba21659d9ca88a2079ceaaa01487166f3286baa8bd45c1f157` |
| 实际 x86_64 libtaglib.so | `12489f9f7a3829bd944721c6ec68f6f5a5269e705efb3d84bb30d7d5e2478991` |
| AAR arm64-v8a libtaglib.so（仅校验文件，未运行） | `3e3d66d9b27957616c2f44b8f8c8564e1775b6ce11895f27662fd090a6ae311f` |
| 匹配 wrapper tag 1.0.6 | `957fd7c2794457a14c1484fa3e3b58382aca4202` |
| 该 wrapper 所指 TagLib 子模块 | `1cbd910c3da4818e835ee6d6e467157366d5f1bb`，服务器返回 not our ref；没有用当前上游代替匹配版本 |

匹配 wrapper 的 `src/main/cpp/utils.h` 属性字符串桥使用 `GetStringUTFChars` + 显式 UTF8，图片描述却没有显式 UTF8；`taglib.cpp` 保存走默认 native save。在实际 API35/x86_64 AAR 上确认：属性写回会清空已有 ID3v1 文字，缺少 ID3v1 时保存会新增空 footer；图片整表保存会把非 ASCII 的保留图片描述改坏。中文、重音西文、Greek、Arabic、emoji 和扩展汉字在该设备的属性 JNI 中能往返，不能用 Modified UTF-8 理论推断它们必坏。

生产封面/歌词不再调用这两个全量 setters，改用上述原生目标记录编辑器；TagLib 继续用于实际读取。原始 JNI 特征测试明确保留并断言已知缺陷集合，它们通过不代表原始 setters 安全。此次没有证明历史《花火》文件具体由哪个版本/操作写坏，不能由当前损坏字节编造历史归因。

## 可复现验证

私有 AVD `JeekTaglibNative35`，Android 15 / API35 / x86_64，google_apis r09，Emulator 37.1.11，ADB 37.0.1；普通 user 0，通过真实 DocumentsUI 选择 `Music/JeekTaglibNative` 获持久读写 grant。测试 Debug 包与正式包隔离。没有用户音频、真实用户设备或 Windows 客户端操作。

```powershell
cd mobile
.\gradlew.bat :app:assembleDebug :app:assembleDebugAndroidTest :app:testDebugUnitTest :app:lintDebug --console=plain
adb -s emulator-5582 install -r app/build/outputs/apk/debug/app-debug.apk
adb -s emulator-5582 install -r app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
# 初次运行在 Debug App 的系统文件夹选择器选择 Music/JeekTaglibNative；不创建 secondary user。
adb -s emulator-5582 shell appops set --uid com.tinyfish.jeekmusic.debug MANAGE_EXTERNAL_STORAGE allow
python tools/validate_native_tags.py --serial emulator-5582
```

测试层级分别记录：

1. `NativeTagWriteTest`：固定真实 .so 的 36 BMP + 24 supplementary 原始 setter 对照，JNI 多值/空值，以及 136 组生产 repo/editor 调用（120 MP3 + 16 容器/回退）。MP3/FLAC/M4A/Vorbis 使用实际 Android PCM 解码；特殊 WAV 在写入前就不被 Android extractor 接受，仅验证拒绝后文件不变与主机 PCM。
2. `NativeTagContractTest`：共享合成 corpus 与 mobile 边界资产 × cover-only/lyrics-only/combined/serial，共 116 组；私有帧/atom/block、非 ASCII 图片、多值/空值、日期、v2.3/2.4、SYLT、歧义槽、Opus 私有尾部、fast-start padding/拒绝、chained Ogg。新增有限 CHAP byte offsets/CTOC、真实 CENC 加密 M4A、saio 索引拒绝；Vorbis 和 Opus 都用 70,000 ASCII 歌词验证无完成 packet 页的 granule 为全 FF（-1）。可解码基线的受支持样本比较实际 Android PCM；提交后真实 JNI 读回目标。
3. 真正的 SAF fd/路径、mtime/mode/uid/gid、序列提交、持久 SAF URI、既有 MediaStore URI重新打开；部分写入抛错、错误输出被验证器拦下、提交前放弃、源 SHA/路径竞争、独立 OS 进程持锁、实际文件系统 EACCES 均验证原件不受本次失败写入损害。
4. `NativeTagPermissionTest`：单独拒绝 Debug 包 all-files app-op，验证 SAF-only 时真实隐藏目录回退和精确 LRC，原音频全 SHA 不变；脚本 finally 恢复原已授权的 allow。
5. `validate_native_tags.py` 独立 FFprobe/FFmpeg 对 132 组实际输出检查非目标标签、完整歌词、压缩音频包和 PCM；另用独立 Python 原始帧解析器比较 ID3 非目标帧、音频/尾部，比较私有二进制标记，并验证 Ogg 页 CRC/无完成包页 granule。CENC 合成夹具仅用公开固定零值测试 key 解码其主机基线，拒绝后整个密文文件保持不变。JVM 结构测试是补充，不替代 JNI/Android。

本机完整日志、JSON、独立输出和原生截图在 `mobile/build/android-taglib-validation/`（构建产物不入 Git）。运行脚本显式指定私有 emulator 并验证 AVD 名称；不会自行安装、清数据、启停 ADB/设备或读取真实曲库。共享 corpus 从正式提交 `167d97ef4e43df2ce2ed110fe2e3a737dbef33dd:testdata/tag-writing` 只读复制，manifest SHA-256 为 `46c2a637235dd9b2c97258061b2bb6c78f52e9ba2d41dea8c038f15bf074f745`（20 个静态资产），另有 mobile 专用 `generate_tag_boundary_fixtures.py`，各自 manifest 固定原始资产 SHA。

原生 UI 通过 ADB 后台通道重启 Debug App，进入合成 `Showcase` 目录，再长按多语歌曲打开 Song info。`client-showcase.png` 和 `client-info.png` 已由代理实际截图并查看：中文标题/周深/专辑、重音文字、Greek、Arabic、𠮷和 emoji 正常显示，绿色新封面可见，合法问号保留。两首样本是 0.25 秒合成音调，不是用户《花火》文件；写入由上述真实生产仓库/SAF instrumentation 执行，UI 截图用于确认最终客户端读显结果，没有通过联网下载来测试写入。

参考：[ID3v2.3](https://id3.org/id3v2.3.0)、[ID3v2.4 frames](https://id3.org/id3v2.4.0-frames)、[Vorbis comments](https://xiph.org/vorbis/doc/v-comment.html)、[Opus RFC7845 §5.2](https://www.rfc-editor.org/rfc/rfc7845.html#section-5.2)、[FLAC RFC9639 §8](https://www.rfc-editor.org/rfc/rfc9639.html#section-8)。
