# LAN 同步审查修复与 Release 补验

针对候选 b7e905d 的四项独立审查，合并处理 Android 中英文可用性收尾。所有路径为 `desktop/artifacts/lan-sync/` 合成目录和 JeekLanSync35 API35/x86_64 独占 AVD，正式包与 debug 并存。没有操作用户曲库或 Syncthing。

## 修复

1. PC `library_folder.identity` 迁移为每次新增独立生成的随机身份，重定位保留，删除后不可继承。共享绑定 identity，根删除永久撤销、清空计划；旧候选无 identity 的共享默认停用，需重新明确授权。真实 NativeAOT 服务复现 A→删除→B 复用 ID=1、同 Same.wav 路径：旧 root/tree/read/plan 均拒绝，B 初始不可见，显式共享后新 rootId 与旧值不同。证据 `release-runtime/root-reuse.log` 与 `root-reuse/*/proof.json`。
2. Android 持久 pending 未恢复前停止整个同步，不允许 B 的提交覆盖 A 日志；日志包含原映射/旧记录，启动及取消后独立执行本地恢复。目标/备份的 hash、实际时间均核对，外部冲突保留副本和日志。定向扫描暂缓 pending 单文件，避免临时缺席导致 track ID 丢失。
3. 替换单个 CUE 的绑定，保留其他 CUE 的边，强制刷新新旧音频并集。Release 真实源 Album.cue 从 Shared/A.wav 改为 B.wav，A 留作整轨且旧 #01/#02 消失，B 展开两首。证据 `release-runtime/cue-before.json`、`cue-after.json`；多 CUE 共享边另有 JVM 回归。
4. 提交前将待刷新路径及 CUE 图变更写入 AtomicFile，正常曲库读取成功才清除。Release A 的 INFO 标题同 size/mtime 更新，B 大文件期间点击取消，磁盘队列保留 A；独立恢复后标题由 BeforeAA 变为 AfterAAA，ID=6 保留。证据 `cancel-before.json`、`cancel-durable.json`、`cancel-after.json`。进程重启同样执行本地恢复/强制读取，不依赖重新下载或远端在线。

## 原版签名 Release

原始 APK SHA256 `70a34d664d8e1fee15fdb41b9de6878a45a8dd20c902b73148917068218e0ded`，R8 正式包实际 UI 配对 NativeAOT、选择 Celtic 和 Karajan/Nested、系统目录选择并授权到 Documents/JeekLanReleaseAcceptance。7 文件 11,648,516 字节，独立 hash 全部一致，mtime 误差 <1000ns，5 首正常曲库歌曲含跨目录 CUE。手机实际 UI 将 PC 播放列表改为 Release_Original_Verified，PC SyncMerge 收到。证据 `original-release-files.json`、`original-release-roundtrip.json` 和截图 01–06（均在 `release-runtime/`）。

## 故障验证

借用隔离 userdebug AVD 的系统 strace，仅针对合成 A.wav 重命名 syscall 做延迟/失败，没有新增产品调试接口或修改 SELinux。注入 stage→A 的 EIO 后，B 未继续，旧 A 恢复；系统 seccomp 同时终止 DocumentsProvider，记录保留该测试副作用，不能推断为普通 OEM EIO 行为。证据 `injected-rename.log`、`transaction-failure-recovered.json`、截图 09。

在 old→backup 成功、stage→A 延迟窗口真实 force-stop Release，并终止这次隔离 DocumentsProvider 以避免仍在进行的 Binder 请求完成提交。重启前在原路径放入测试专用外部阻挡文件，重启保留它、旧备份及持久日志；移除这个已核对内容的阻挡文件后再次启动，无需 PC 请求即可恢复旧 hash、清空日志并读取曲库。证据 `process-killed-pending.json`、`restart-external-conflict.json`、`restart-restored.json`、`process-kill-rename.log`。

空间不足没有现成可控注入入口，没有填满保存其他证据的分区，也没有为补测新增架构；真实满盘/ENOSPC 未验收。ARM64/OEM、物理 SD 卡及未覆盖的提交点仍未测。Windows 原生 Computer Use 既有 native pipe os error2 阻断未重试，Windows UI 不能宣称通过。

## UI 和回归

主同步页显示根名、可读目标目录及简短下载子目录；长 URI、完整 ID、请求/实际时间放在展开详情。精度限制用中英文普通色汇总，真正时间设置失败仍报错；使用“包含子文件夹”和易懂的其他同步工具约束。普通曲库路径标签缩短生成的根 ID，文件详情仍保留精确路径。

App 回归：545 通过、1 个既有物理音频端点测试跳过。使用 portable 测试输出；一次 RID 测试运行的 9 个失败来自现有探针固定 portable 路径，随后正确 portable 完整运行全部通过。Android JVM 88 项通过，签名 Release/R8/lintVital 构建通过。最终新提交 APK 运行结果记录在候选 manifest 与 `release-runtime/`，不能由原版 Release 结果代替。


扫描避让补丁在同一签名 Release 再次实际中断提交、放入外部阻挡、启动后点击“Rescan library”：pending 原路径不被删除，恢复前后 track ID=16 及全部其他行一致，见 `protected-normal-scan.json`、`protected-final-recovery.json`、`protected-rename.log`。随后另一次 A 同 size/mtime 更新在 B 下载时真实终止进程，重启不下载即从磁盘待刷新队列读到 AfterAAC，ID=16 保留，见 `refresh-restart-pending.json`、`refresh-restart-after.json`。
