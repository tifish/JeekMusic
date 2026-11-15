# 手机日常扫描与同步复用本地文件信息

当前策略见[已知变化驱动与手动刷新](sync-event-driven.md)：索引持久保存，前后台切换、进程重启和每 15 分钟不再触发手机完整枚举。自动处理系统媒体与软件内编辑，外部旁文件变化由手动刷新发现。

## 早期实现与验证记录

以下内容记录此前依赖连续目录监听的实现；当前行为与验收以新策略文档为准。

手机日常曲库扫描通过 MediaStore 找歌曲，同步原本又独立遍历一次文件系统，结束后还重建全部目录监听。现在同步保留一个完整的本地文件索引：监听连续有效且索引未超过 15 分钟时，无变化轮次直接读取索引，不重新列目录或逐文件读取大小、mtime。

文件新增、删除、写入、属性变化或改名的事件只标记相应路径，下次同步只更新这些路径。日常曲库扫描为纳秒精度打开文件时，把同一描述符的精确大小和 mtime 共享给索引；如果对应事件后已有这一观察，同步直接复用，不再打开文件。以描述符实际路径绑定，不从 MediaStore 猜测物理路径。MediaStore 行即使尚未更新，现存文件的精确 fstat 也不会与旧行的大小/时间混合。

MediaStore 的歌曲列表不等于完整目录：可能延迟，也不完整覆盖 CUE、歌词、封面。因此不直接用歌曲列表推断删除。完整索引由原来的描述符相对、安全路径枚举建立，包含支持的歌曲和旁文件；大小/mtime、最后一次软件编辑时间的比较优先级不变，不读取音频或增加编辑历史。

## 有效性与回退

- 首次使用、进程重启、切换根/选择范围/目录身份、15 分钟补查：完整枚举一次。
- 前台目录监听在映射或目录拓扑变化时重建，不再每轮重建；普通文件事件仅更新相应路径。所选子目录尚不存在时也监听已有祖先目录。新的子目录、目录移动/删除、队列溢出或意外失去监听使索引失效。
- 退到后台立即丢弃索引；后台 WorkManager 和返回前台后的首次对账重新枚举，不能拿未被监听的时间段作无变化证据。
- 枚举中并发变化、读取不完整、权限或存储不可用时不发布可复用快照；不完整索引不进入删除收尾。构建监听期间发生目录拓扑变化也不能发布健康状态。
- 本应用同步写入/删除在操作前同步标记路径失效，避免依赖异步监听的到达时机。新文件的完整下载、删除条件检查、恢复和基线确认仍走原服务。
- 缓存仅在进程内保留完整当前状态和变更路径，最多 8 个快照、32 MiB 快照文本、每份最多 100,000 个文件；变更与共享观察各限制 100,000 个路径。不是修改历史数据库。传输、覆盖、删除及确认前仍执行原来的实时版本/句柄检查。

Android 监听事件含义以 [FileObserver 官方文档](https://developer.android.com/reference/android/os/FileObserver)及 [inotify 手册](https://man7.org/linux/man-pages/man7/inotify.7.html)为准；监听只提供缓存失效和同步调度提示，不替代提交前检查。

## MCP 诊断

文件协议仍为 3。PC 宣告 `localFileIndex=1` 后，手机可报告 `lastLocalIndexHits`、`lastLocalIndexMisses`、`lastLocalIndexRefreshedFiles`、`lastLibraryMetadataReusedFiles`；定义见[桌面 MCP 文档](../desktop/docs/mcp.md)。旧 PC 不接收新增字段；两种构建使用同一套实现，MCP 工具输入 Schema 无变化。

无变化的稳定轮次应有本地索引命中、`lastLocalIndexRefreshedFiles=0`、`lastMetadataObservations=0`、`lastMetadataRows=0`、`lastTransferBytes=0`。如果存在 CUE 范围外依赖、待恢复事务、冲突或传输动作，仍可有必要的逐文件元数据观察。

## 验证

所有 Android 验证使用隔离模拟器，实体手机不参与本轮测试或部署。

```powershell
cd mobile
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug :app:assembleDebugAndroidTest :app:assembleRelease
cd ..
adb -s emulator-5584 shell am instrument -w -e class com.tinyfish.jeekmusic.data.sync.LocalFileIndexDeviceTest com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner
python desktop/tools/test-lan-files.py --exe desktop/artifacts/local-index-publish/JeekMusic.exe --android emulator-5584 --public-saf --case watchedLocalInventoryAvoidsRescanAndSharesLibraryMetadataWithoutLosingDeletes
python desktop/tools/test-lan-audio-reuse-ui.py --exe desktop/artifacts/local-index-publish/JeekMusic.exe --android emulator-5584 --check-local-index --out desktop/artifacts/local-index-ui
```

单元测试覆盖无变化复用、只刷新变化路径、旁文件删除、定期回退、枚举期间变化、监听中断、拓扑变化和不完整目录。模拟器原生测试核验实际 FileObserver 事件、描述符共享、最后编辑时间和封面删除；跨端 TLS 测试核验无变化零观察、日常元数据复用、双向新增和删除。

2026-11-15 验证：Android 单元回归 137 项、桌面 LAN/最后编辑时间回归 123 项通过；debug、androidTest、签名 release 及 Windows 日常发布构建成功。模拟器实际日常扫描读取器、原生监听与精确时间测试 3 项通过；跨端本地索引专项通过。PC 新进程已经启动，生产 MCP 工具描述包含新增诊断，输入 Schema 保持不变。

本机忽略产物：`desktop/artifacts/local-index-accepted-build.log`、`local-index-desktop-tests.log`、`local-index-device-accepted.log`、`local-index-integration-final.log`、`local-index-deploy.log`。早期自动调度测试的固定未来时间与系统时间混用、对瞬时上传摘要的依赖已修正；Windows 仪器输出显式按 UTF-8 解码。被安装测试包打断的自动用例不计为通过，以串行最终验收记录为准。

最终生产调度验收 `local-index-auto-serial.log` 通过，包含连续手动轮次索引命中且零逐文件观察、零元数据行、零文件传输，以及目录变化、突发写入、暂停/取消恢复、双向删除和离线返回前台补查。`local-index-handoff.log` 的后台交接验证通过，等待前台任务清理后再由 WorkManager 接手。

最终真实 UI 验收 `local-index-ui-verified.log` 通过：实际点击取消/立即同步，先完成完整下载和标签音频复用，再完成连续无变化手动轮次。`desktop/artifacts/local-index-ui-verified/result.json` 确认本地索引命中 1、未命中 0、重新观察 0、元数据观察 0、接收变化行 0、传输 0，无退避。已人工查看同目录 `android-local-index-complete.png`，确认“同步完成”、正常可用的按钮及保留的音频复用摘要。夹具按客户端现有同步设备 ID 配对，仅读取停止后的合成 debug 数据库副本；通过界面暂时暂停自动任务后再点击已启用的按钮，避免对瞬时状态或无效点击作错误判断。
