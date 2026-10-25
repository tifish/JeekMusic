# 7c439e2 独立审查修复验证

配置：codex / gpt-6.1-sol / high。仅修改独立 worktree 的 mobile；未操作主目录、PC 客户端、用户手机、真实音频或备份。原始 `tag-writing-api35.json` 保留首轮证据，本轮独立清单是 `validation/tag-writing-review-api35.json`。

## 逐项关闭

| 审查项 | 真实 API35 旧实现复现 | 修复及验收 |
|---|---|---|
| P1：候选验证后可被更换/改写 | `NativeTagReviewTest` 在 validate 回调后或 canCommit 内截断、等长改写（恢复 mtime）、同内容换 inode，旧实现六项全部返回 true；原件被替换，模拟成功清理删除 sidecar | 在验证前打开候选 fd，记录身份及 SHA；验证后核对同一身份/内容；属性保存后再次绑定快照；持有源及候选锁，使用原 channel 在 rename 前复核候选 fd/path stat、SHA。九项确定性竞争均拒绝、源与 sidecar 不变，其中三项由独立 app_process 实际持锁改写/换 inode。额外进程 probe 证实 canCommit/最终检查区候选锁有效。真实 CoverStore.embedDownloaded、LyricsRepository.embedDownloaded、CoverStore.save 联合路径在候选被另进程持锁时均拒绝嵌入，保留外部 JPG/LRC，不记 alsoDone/hasEmbeddedLyrics |
| P1：嵌套 MP4 iloc 失效 | 对合成 moov/udta/meta/iloc 的三种操作，旧生产 write/validate 全部接受；独立解析显示 item 偏移仍是 1314，数据已移动到 863。音频包与 PCM 仍相同，不能证明 metadata 引用有效 | 按真正的 moov/udta/meta FullBox、ilst item、音频 sample-entry 层级递归，拒绝 iloc 和未分类 UUID；也检查顶层 meta/udta/uuid。三个层级 × 两类 atom × 三操作共 18 项原件全字节不变；真实仓库再覆盖 cover/lyrics/combined/serial 回退。opaque 叶数据含 `ilocsaioencauuid` 仍能安全写入，明确没有任意字节搜索 |
| P2：未知 ID3 帧 tag-alter 标志失效 | v2.3 XPOS status=0x80、v2.4=0x40 保存后仍保留缓存 MPEG 起点 24，但实际音频起点已变化；六项旧生产 write/validate 均接受。独立原始帧和音频包/PCM 对照确认 | 遇到该版本对应 tag-alter-preservation 位即拒绝整次编辑，不删除未知帧后宣称成功。两版 × 三操作六项拒绝且原文件逐字节不变；真实仓库 cover/lyrics/combined/serial 均回退。未自动迁移、转码或修补旧字段 |

旧实现基线为 `7c439e2385fec5e9cb2d7218b1cd17d4e9ddd7e2`，先增加回归、构建并安装原生产实现；`review-baseline-instrumentation.txt` 为 **3 tests / 3 failures**。复现输出在 `review-before-files/`；`validate_tag_review_baseline.py` 独立检查九个旧输出的原始定位/状态字节及 FFmpeg 音频/PCM。顶层 meta 的部分旧用例已有验证拒绝，不将其宣称为新增复现。

## 本轮运行

私有 `JeekTaglibNative35` / `emulator-5582`，Android 15 API35 / x86_64，google_apis r09；沿用已有 user0、userdata 与持久 SAF grant。Emulator 37.1.11、ADB 37.0.1；实际读取依赖仍为固定 TagLib 1.0.6 原生 x86_64 库，SHA 与首轮相同。

- **85** JVM tests，0 failures/errors/skips；Debug、AndroidTest、Release 构建及 Debug lint 通过。
- **9** 实际设备方法：5 个审查方法 + 4 个现有合约/事务方法。5 个审查方法含 37 项候选/守卫/仓库/格式结果；4 个既有方法含 **152** 个生产仓库/editor/SAF 用例及源竞争、序列提交、EACCES、持久 SAF / MediaStore URI、独立源进程锁。
- **152** 个本轮生产输出全部通过独立主机非目标字节/原始帧、完整歌词、私有标记、音频包及 PCM 检查；拒绝文件必须与合成输入全字节相同，含 iloc、未知标志帧和原有 ID3v1。有效 Ogg 仍检查 CRC / granule=-1。没有用 PCM 相同替代位置语义验证。
- 83/252/132、60 个 raw JNI characterization 和 permission-only 方法属于首轮，不计为本轮重跑。候选事务修改影响所有容器，因此重跑完整 152 项生产合约；未无意义重复未修改的 JNI setter 特征。
- 本轮新 Debug APK 经 ADB force-stop/start 后，在真实 Android 客户端打开保留的合成 Showcase 和 Song info，保存并查看 `review2/client-showcase.png`、`review2/client-info.png`。文字/封面显示正常；这些是合成样本读取界面，写入验证由上述生产路径 instrumentation 完成，并非联网 UI 下载操作或用户《花火》真文件验收。

在 mobile 下复现（模拟器及原授权准备见主验证文档）：

```powershell
.\gradlew.bat :app:assembleDebug :app:assembleDebugAndroidTest :app:testDebugUnitTest :app:lintDebug :app:assembleRelease --console=plain
adb -s emulator-5582 install -r app/build/outputs/apk/debug/app-debug.apk
adb -s emulator-5582 install -r app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
python tools/validate_native_tags.py --serial emulator-5582 --contract-only --output build/android-taglib-validation/review2
# 已运行的 Android 输出可独立复核，避免重新执行设备测试：
python tools/validate_native_tags.py --serial emulator-5582 --contract-only --collect-only --output build/android-taglib-validation/review2
# 保留旧实现输出时：
python tools/validate_tag_review_baseline.py --captured build/android-taglib-validation/review-before-files
```

`generate_tag_review_fixtures.py` 只从已提交合成 corpus 生成九个本轮夹具。canonical corpus、原 boundaries 均未改；新 manifest 单独记录 SHA。实际构建、设备、独立验证日志及截图位于 `mobile/build/android-taglib-validation/`，不入 Git；哈希在本轮 evidence JSON。

## 支持及剩余限制

主 [格式矩阵](tag-writing-validation.md) 延续 MP3 v2.3/v2.4、FLAC、单流 Vorbis/Opus、有界 MP4 写入。新增安全拒绝范围仅为标签自声明失效的 ID3 帧，以及无法证明重定位安全的 iloc/未分类 UUID。`boundaries/private.m4a` 含电影层未知 UUID，现改为安全外部回退；其 typed 私有字段、共享未知 typed atom、非索引 opaque 叶仍有成功回归。其他格式/复杂包络继续原外部回退，不变更读取或曲库 advertised extensions。

候选验证到锁前的改写/换 inode 窗口已经修复并实测，不能归入一般不遵锁限制。最终区间现在保护源和候选两个 inode，检查之后仍不是内核 pathname CAS：POSIX advisory 锁不能强制阻止完全忽略锁的写入或目录项替换，同进程关闭另一同 inode fd 也可能影响经典 POSIX 锁；生产双锁区避免自己这样做。恶意或不协调的最终检查后写入、OEM/可移除存储/ACL、断电/kill 与真机 ARM64 尚未验收，不能将 API35/x86_64 结果外推。用户原文件历史损坏仍不能具体归因。

测试结束只关闭本任务私有 AVD，保留 userdata，不关闭共享 ADB server。仅本地提交，由父会话独立复审、整合、重建签名包及发布。
