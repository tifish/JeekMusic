# 手机同步：已知变化驱动与手动刷新

手机默认假设音频以外的外部文件变化很少发生。自动处理系统媒体索引报告的音频变化，以及 JeekMusic 自己写入、删除、移动或改名的文件；外部软件修改歌词、封面和 CUE 后，使用“设置 → 同步 → 刷新外部文件”。该按钮完整检查一次所选目录并同步；原有曲库“重新扫描”也会使同步文件索引失效并安排检查。

## 日常执行

- 保存当前完整文件索引、媒体索引检查点和未确认变化路径，前后台切换、进程重启均保留。不保存修改历史，不再每 15 分钟使手机索引过期，也不递归建立手机目录监听。
- 前台进入、网络恢复、软件内编辑和 Android 后台调度只安排静默预检。系统媒体 generation 没有变化时不查询音频列表；变化时仅读取系统数据库中的 ID、路径和 generation，定位新增、改名、修改及消失的记录，然后核实对应文件。系统索引延迟允许延后发现外部音频变化。
- 软件内的 SAF 写入、删除、改名、移动和音频标签原子提交直接持久记录受影响路径；同步下载及删除也在操作前后记录路径。不依赖 MediaStore 及时更新旁文件。
- 预检只读取已知变化路径的精确大小和 mtime，与已确认基线比较。迟到的系统通知和本软件刚完成的同步写入如果已经与基线一致，不再启动文件对账任务。读取候选不等于确认同步：未确认提示在崩溃后仍保留。
- 电脑变化独立使用原有认证版本轮询。电脑进程重启、根版本或冲突变化可能安排一次静默对账，但仍复用手机索引。前台轮询为 30 秒，后台检查时间由 Android WorkManager 决定。
- 日常曲库读取也缓存系统媒体结果；generation 未变时复用结果，变化时只为变更音频补取精确时间，避免每次启动逐首打开音频。

## 完整检查与文件安全

首次建立索引、映射目标身份或选择范围改变、索引缺失，以及明确的手动刷新才重新枚举手机所选目录。外部旁文件编辑、新建或删除目录后如果需要立即发现，使用手动刷新。

权限、存储卷和目标目录身份在访问时仍由原来的 SAF/描述符检查验证；不可访问不能解释为文件删除。不完整读取不发布快照。覆盖、传输、删除和确认前仍实时检查对应文件版本，沿用原有大小、精确修改时间及最后软件编辑时间策略。

自动预检和没有文件动作的自动对账不显示同步进度；确定处理文件时才显示进度。手动“立即同步”和“刷新外部文件”仍显示准备和执行状态，错误与冲突仍在设置中可查。

## MCP 兼容

Windows `lan_sync_status` 工具描述同步说明以上手机策略；输入 Schema 和工具参数不变。既有手机调度报告的 `state=checking` 表示轻量预检（`running=false`），`transferring` 表示进入文件动作。`nextReconcile=0` 表示没有定期手机全量补查，`watchDirectories=0` 在新策略下是正常状态。现有 `lastLocalIndexHits/Misses` 等计数仍描述上次完成的对账，不能把未启动的预检算成新同步。开发版、正式版共用实现。

## 验证入口

Android 单元测试覆盖索引复用、单路径变化、删除、扫描期间并发变化、未确认提示及手动失效。`LocalFileIndexDeviceTest.durableInventoryAndPendingPathsSurviveRecreation` 使用实际文件和 SQLite 检查索引重建对象后的复用及中断恢复。真实跨端与 UI 验收使用隔离模拟器：

```powershell
python desktop/tools/test-lan-files.py --exe bin/JeekMusic.exe --android emulator-5584 --public-saf --auto --case unchangedChecksStayQuietAndExternalSidecarsRequireManualRefresh
python desktop/tools/test-lan-files.py --exe bin/JeekMusic.exe --android emulator-5584 --public-saf --auto --case actualEventsReconcileBothDirectionsAndRespectCancelAndPause
```

UI 用例实际操作新版设置页，截图覆盖无变化、前后台切换、手动刷新进行中和刷新完成；图片保存在模拟器应用缓存 `sync-quiet-ui/`，提取后的本机证据位于 `desktop/artifacts/sync-quiet-ui/`。不使用实体手机。

2026-11-15 验收：138 项 Android 单元测试、16 项相关桌面回归通过；debug、androidTest、正式签名 release 和 Windows 日常版本构建成功。最终模拟器持久索引测试 2 项、独立双向自动同步全流程及真实 UI 用例均通过，后台交接用例也已通过。实际验证无变化预检不启动对账、不新增文件元数据读取，前后台切换不全量扫描，软件内歌词编辑增量上传，外部歌词经手动刷新上传，刷新过程有可见进度。截图已逐项查看。Windows 生产 MCP 已验证新策略描述和不变的输入 Schema。

最终证据：`desktop/artifacts/sync-calm-delivery-build.log`、`sync-calm-final-auto.log`、`sync-calm-final-index.log`、`sync-calm-final-ui.log`、`sync-calm-desktop-tests.log`、`sync-calm-mcp-verification.json` 及 `sync-quiet-ui/`。回归断言已改为检查“空闲时没有新增任务和读取”，不再要求空任务覆盖上次有效传输统计；文件替换期间的读取轮询按未完成状态重试。

自动调度夹具按模拟器已有同步设备身份配对，仅读取停止后的合成 debug 数据库副本，不修改设备日志。最终 UI 用例还要求手动刷新结束时问题列表为空且显示“同步完成”；已查看对应截图，排除了随机文件测试身份造成的错误“配对失效”提示。
