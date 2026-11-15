# 自动判断标签传输与完整文件传输

按用户选择自动判断，无需每次切换模式。先沿用现有 mtime、大小及必要时最后编辑时间的规则确定文件是否需要同步、同步方向，再对需要传输且双方均存在的支持格式解析媒体范围。媒体长度一致且 SHA256 完全相同，才传送媒体范围之外的全部字节；接收端从原文件复制媒体字节到私有暂存文件，组合成与源文件逐字节一致的结果，再走原有条件提交、备份与确认。

元数据包括标签和容器结构，不统一截取固定长度文件头，也不通过标签对象重新编码字段。MP4 的源索引、WAV 长度字段和首尾标签均按源文件原始字节传送，媒体位置改变也不会保留旧偏移。节省的是网络传输，接收端仍需读取原媒体并写入完整临时文件。

普通扫描和无变化轮次不解析、读取或散列音频；新增文件没有可复用目标，直接完整传输。媒体不同、解析不支持/有歧义、媒体小于 256 KiB、元数据超过 32 MiB及旧对端均自动走完整文件，不因优化不适用而跳过正常同步。物理文件上限、暂存、备份、每日上传预约、空间余量及权限检查不变，预约仍按完整候选大小计。相同 mtime 与大小仍按用户先前指定规则认为一致，不为发现隐藏变化重新读取内容；最后软件编辑时间规则也不改变。

## 当前解析范围

- MP3：开头 ID3v2 包络与可选 ID3v1 尾部，媒体区域为中间连续字节。APEv2 尾部等不能明确处理的布局完整传输。
- FLAC：有合法首个 STREAMINFO 的原生 FLAC 元数据块链，最后一个元数据块之后的连续帧区域。
- WAV：标准 RIFF/WAVE，正确处理块长度与偶数对齐，仅复用单个 data 块的有效字节。
- M4A/MP4：正确解析普通、64 位扩展及到文件末尾的 box 大小，仅复用单个 mdat 的有效字节。

Ogg/Opus、MKA、AAC、AMR、多个 data/mdat、RF64 等未支持的布局当前使用完整传输，不宣称这些格式也已实现标签优化。两端使用 `shared/testcases/audio-envelope.json` 的相同包络样本；不在扫描阶段遍历压缩音频帧。

格式依据：[ID3v2.4 结构](https://id3.org/id3v2.4.0-structure)、[FLAC RFC 9639](https://www.rfc-editor.org/rfc/rfc9639.html)、[Microsoft RIFF](https://learn.microsoft.com/en-us/windows/win32/xaudio2/resource-interchange-file-format--riff-)、[Apple box/atom 大小](https://developer.apple.com/documentation/quicktime-file-format/atoms)。

## 协议与恢复

能力 `fileAudioReuse=1` 独立协商，不改变文件协议 3 或元数据比较策略。新增操作复用已有配对、scope、根与设备授权、串行文件工作许可和 15 分钟任务期限，正式/开发实现相同。

- `file_audio_layout {fileProtocol:3, scope, path, expectedVersion, mediaBytes}`：只读固定版本，异步任务返回 supported、format、start、length、sourceVersion；仅在媒体长度相符时计算 digest。长度不相符不做无用媒体散列。不适用返回 supported=false。
- `file_reuse_audio {fileProtocol:3, scope, operation, offset, sourceOffset, length, digest}`：仅从该上传操作的现存目标文件、已解析媒体范围复制，追加到已确认的私有暂存偏移。输入范围必须在原媒体及完整候选大小内；不接受其他路径。不一致返回 reused=false 并恢复原私有前缀，手机继续完整数据块；源版本改变则延期，不覆盖并发修改。
- `file_read` 可选 count 1–262144，避免读取元数据边界后的音频；旧输入保持默认 256 KiB。

下载本地复制在失败/取消时撤回未确认媒体部分，已确认检查点仍可重试。上传本地复制在失败/取消时恢复原确认前缀；进程意外退出留下的未确认复制标记，下次恢复只清理身份匹配的私有暂存并重新开始。替换过的私有对象不截断；prepared/committed/acknowledged 仍保持原恢复/幂等路径。公开音乐文件直到原条件提交完成前始终不变。

## MCP 与界面

MCP 的工具描述和使用文档同步更新，工具输入 Schema 不变。`fileSync.audioReuse` 单独统计 PC hashBytes/reusedBytes/copies，原元数据比较 hashBytes=0/hashPurpose=none 仍只描述版本判断；不能据此把变化文件的媒体验证也声称为零读取。手机调度可带 lastAudioReusedBytes/lastAudioReusedFiles（完成映射的双向总计），旧 PC 不发送这些新增字段。

同步页说明自动行为，成功复用后以“最近一次音频复用”显示文件数和节省的字节数，按当前语言使用千分位分隔符。该非零结果只在本次客户端进程内保留，后续无变化轮次不清除，也不新增编辑历史；MCP 调度字段仍独立表示最近完成轮次的实际计数。诊断只含计数和策略，不返回音频、歌词、路径或凭据。

## 验证入口

```powershell
dotnet test desktop/tests/JeekMusic.Core.Tests -c Release --filter AudioEnvelope
dotnet test desktop/tests/JeekMusic.App.Tests -c Release --filter "FullyQualifiedName~Lan|FullyQualifiedName~FileEditTimeStore"
cd mobile
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug :app:assembleDebugAndroidTest :app:assembleRelease
cd ..
python desktop/tools/test-lan-files.py --exe desktop/artifacts/audio-reuse-final-publish/JeekMusic.exe --android emulator-5584 --public-saf --case changedTagsReuseExactAudioBothWaysAndChangedAudioFallsBackToFullTransfer
python desktop/tools/test-lan-audio-reuse-ui.py --exe desktop/artifacts/audio-reuse-final-publish/JeekMusic.exe --android emulator-5584 --out desktop/artifacts/audio-reuse-ui
```

## 本轮验证与交付（2026-11-15）

- Core 格式测试通过，覆盖共享样本的 10 种有效/拒绝布局；桌面 LAN 与最后编辑时间回归 123 项通过，Android 单元测试 135 项通过。
- 真实 Android public SAF 与 Windows TLS 对账全部 12 项通过，包括 MP3 首尾标签双向变更、WAV 双向复用、音频变化完整回退、无变化轮次不增加音频散列，以及原有冲突、删除、断点恢复。
- 合成 WAV 标签下载只传 1,076 字节、上传只传 2,100 字节；各复用 1,048,576 字节音频，落地完整文件均与源逐字节一致。测试文件位于隔离模拟器，不修改用户歌曲。
- 最终 APK 的真实 UI 手动操作验收通过，截图人工核对自动行为说明、“最近一次音频复用：1 个文件，节省 1,048,576 字节传输。”及千分位。早期截图存在列表未滚到摘要、后续空轮次覆盖摘要的问题，未计为视觉通过；修正摘要保留并更新模拟器 APK 后，以最终截图验收。
- Windows 日常发布与 Android Release 构建成功，PC 客户端已替换并重启；真实手机安装并重启 1.0.273，保留现有数据及配对。生产 MCP 工具描述已包含 audioReuse、输入 Schema 不变；真实手机无变化自动轮次为零文件传输、零变化信息行，PC 音频 hashBytes/reusedBytes/copies 均为 0。
- 真实手机点击“立即同步”复验通过：增量信息行、文件传输及 PC 音频散列增量均为 0，无冲突及退避；完成状态截图已人工检查。产物为 `desktop/artifacts/sync-expired-physical/audio-reuse-manual-{before,after}.json` 和 `audio-reuse-manual-complete.png`。

本机忽略的验证产物：`desktop/artifacts/audio-reuse-core-tests.log`、`audio-reuse-desktop-tests.log`、`audio-reuse-delivery-debug.log`、`audio-reuse-delivery-release.log`、`audio-reuse-target-integration.log`、`audio-reuse-all-integration.log`；最终 UI 为 `desktop/artifacts/audio-reuse-ui-accepted/android-reused-audio.png` 与同目录 `result.json`。这些本机产物不随 Git 提交。
