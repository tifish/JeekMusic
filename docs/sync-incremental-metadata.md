# 只交换变化的文件信息

首次对账建立完整状态。之后每个有效映射保留最近一次确认的元数据检查点，只交换变化路径：新增、删除、大小或精确 mtime 改变、最后一次成功软件编辑时间改变，以及必要的已确认基线更新。未变化轮次返回 0 条文件信息，不再逐页重传全目录基线和清单。原文件比较优先级不变：mtime 较新者优先；同 mtime、同大小认为一致；同 mtime、大小不同才按最后编辑时间决策。

检查点对应成功对账的边界，使用双方确认的不透明游标，而不是筛选 `文件 mtime > 上次成功时间`。复制旧文件可能保留旧 mtime，删除也没有现存文件可读时间；比较当前状态与已确认状态才能发现这些变化。这里只保存当前状态、一个已确认快照和一个进行中的快照，不保存歌词/封面操作历史。

## 获取和确认

PC 在现有健康目录监听清单缓存上比较快照，目录事件立即使清单失效，15 分钟兜底重扫。最后编辑时间在本地批量读出，不逐文件请求或读取音频内容。快照逐行写入已有受限 SQLite；网络每页最多 200 个变化路径，删除明确携带空 remote 状态。

手机保留电脑状态和已确认记录，并复用完整的本地文件索引。目录监听连续有效时，只刷新事件涉及的文件，日常曲库扫描已经读取的精确大小/mtime 可直接共享；最后编辑时间仍批量加入。首次建索引、目录结构变化、监听中断或失效、进程重启以及 15 分钟补查才完整枚举。MediaStore 歌曲列表不用于证明文件不存在，歌词、封面和 CUE 仍包含在完整索引中；传输和提交前的逐文件实时核验不变。详见[手机本地索引复用](sync-local-file-index.md)。

一轮开始时固定 PC 快照，期间发生的变化由下一轮补齐，包括本轮提交产生的新基线，可能保守地多返回一次该路径的元数据。只有映射完成且无冲突、延期时确认游标；失败、取消和冲突均不确认。手机在覆盖缓存前先持久标记未完成，中断重试或游标丢失时重新获取完整状态，防止半页缓存、状态回退或确认丢失造成漏同步。后续稳定轮次恢复纯增量。

## 协议和 MCP

TLS capabilities 增加 `fileDeltaProtocol=1`；文件协议仍为 3。新手机连接旧 PC 时继续原完整清单流程；旧手机连接新 PC 的接口保留。新增操作复用既有认证、scope/凭证/选择范围绑定、文件工作许可、请求截止和 SQLite 容量限制。

- `file_delta {fileProtocol:3, scope, plan, cursor}`：plan 必须属于当前设备、根和相同选择范围。返回 cursor、reset、total、next、hasMore、rows。首次或游标不匹配时 reset=true，手机先清理旧快照。每行包含 path、remote、baseline；空值表示该路径对应状态已不存在。
- `file_delta_rows {fileProtocol:3, scope, cursor, offset}`：读取同一个固定快照的后续页，最多 200 行；已被替代的游标返回 `delta_checkpoint_changed`。
- `file_delta_commit {fileProtocol:3, scope, cursor}`：原子确认快照，可幂等重放；不写音乐或推进文件基线，文件本身仍须走原来的条件提交与确认。

`lan_sync_status.fileSync.metadataDelta` 返回 protocol、policy=confirmed_snapshot_delta、history=false，及当前进程 rounds/fullRounds/rowsSent 计数。手机认证调度报告新增 lastMetadataRows（已完成映射接收的路径数之和）与 lastIncremental（所有已完成映射是否使用已有检查点）；reading_delta 对应手机的“正在读取变更文件信息”。报告可能陈旧，以 reportedAt 为准；中断时未完成映射不计入完整轮次统计。MCP 工具描述已同步，参数和 Schema 不变，开发版与正式版共用实现。

## 验证入口

```powershell
dotnet test desktop/tests/JeekMusic.App.Tests -c Release --filter "FullyQualifiedName~Lan|FullyQualifiedName~FileEditTimeStore"
cd mobile
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug :app:assembleDebugAndroidTest :app:assembleRelease
cd ..
python desktop/tools/test-lan-files.py --exe desktop/artifacts/delta-publish/JeekMusic.exe --android emulator-5584
```

新增桌面回归覆盖 401 行分页、持久检查点重启、无变化零条目、旧日期新增、删除、时间/大小/软件时间变化、未确认重试、游标被替代及同步期间的基线变化。Android 真实协调器用例验证稳定轮次零元数据和零文件传输、本地软件时间变化、不完整缓存重新初始化、旧日期新增和双向删除；原有断点/冲突/时间规则同时回归。

## 交付结果

桌面 LAN/最后编辑时间回归 118 项、Android 单元测试 133 项全部通过，debug、androidTest 和签名 release 构建成功。9 项真实 Android/Windows 整组回归通过；公开 SAF 目录增量专项及新增的范围外 CUE 音频恢复专项均通过。CUE 专项还暴露并修复了原计划 `verify` 分支被入口白名单误拒绝的问题，核验仍限定当前设备、有效计划与计划内精确源版本，不放宽文件写删边界。既有语种缓存/覆盖/交换 Room 回归单独通过。

PC 日常客户端已保存状态、部署并重启；实体手机同签名覆盖安装 1.0.272 并重启，未清理配对、文件或数据。实际进入同步页面、等待检查点稳定后点击“立即同步”，两个映射均正常完成。生产 MCP 前后对照确认 lastIncremental=true、lastMetadataRows=0、lastTransferBytes=0、pendingConflicts=0、backoffUntil=0；PC rowsSent 和 transferBytes 均未增长。PC 重启后仍可复用手机与电脑的持久检查点，无需重新全量下发。

已实际查看最终完成界面 `desktop/artifacts/sync-expired-physical/delta-manual-complete.png`；前后原始报告为同目录 `delta-manual-before.json`、`delta-manual-after.json`。构建、回归及实际交互记录在忽略的 `desktop/artifacts/delta-*` 中，实体设备原始状态、歌曲路径及凭据不进入提交。首次整组运行曾在 Android 用例开始前的夹具删除断言中止，补充具体返回值诊断后，最终整组与专项通过；未放宽生产重试或保护条件。

语种结果属于独立元数据扩展，由立即同步及前台元数据轮询交换；后台文件任务自身只处理文件。识别任务空闲或某次任务完成，不表示当前全库均有有效缓存。手机的 LanguageExchange 日志新增 receivedTracks/matchedTracks/savedRows/metadataMismatchRows/validCachedTracks/pendingTracks 数量诊断，不包含路径、音频或凭据。本次实体设备未能读到这组日志，因此未将手机实际匹配数量标为已核实。语种识别的计算与有效性规则没有改变。
