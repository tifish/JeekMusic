# 多 PC 连接（P1）

Android 可以保存最多 32 台 PC 的独立配对，在同步页面选择要查看的电脑及其目录。新增配对不覆盖旧连接；相同 TLS 指纹的重新配对更新该连接，保留原映射身份。每个文件任务按映射保存的 peerId 获取凭证，切换界面、读取目录和冲突查询不改变正在执行的传输目标。

本阶段支持多台独立 PC 的文件同步连接。歌单、标注和学习数据仍只与原主电脑交换；尚未提供个人数据同步组或 Windows 出站文件客户端，完整路线见[多设备计划](multi-device-sync-plan.md)。不能将本阶段描述为所有设备已共享同一逻辑曲库。

## 迁移与数据保留

`lan-sync.json` 使用 schemaVersion=2。原 connection 成为第一条 Peer，兼容 connection 字段只指向原主电脑，不随页面选择变化。旧配置原文保存在应用私有目录 `lan-sync-v1.json`；迁移和后续写入使用 AtomicFile、fsync 和提交后核验，成功后才更新内存。配置解析失败或版本过新时拒绝覆盖，不按空配置继续写入。

原 token、TLS pin、mapping key、scope、选择范围、目录身份、文件基线和事务都保留。缺少原连接的历史映射标记为 unbound，不自动附到下一台新配对的 PC。不同 PC 的映射按 peerId/rootId 区分；相同根 ID 不会覆盖彼此。不同 PC 的目标也不能相同、大小写等价或父子重叠。

原主电脑的歌曲引用继续使用 root:rootId/path，其他 PC 使用带 peerId 的独立命名空间。Windows／Android 的引用匹配器都禁止对 root、peer、collection 引用执行文件名后缀匹配；不同设备的同名歌曲不会因此绑定错误。

忘记电脑移除其连接和映射，保留本机音乐；需要撤销远端访问时仍须在 PC 撤销手机。忘记主电脑后不自动把个人数据改发到另一台电脑。关闭目录、取消当前电脑的文件同步及全局取消具有独立作用域；重新启用该电脑的映射或点击立即同步可恢复它。

## 调度、恢复与 MCP

一个手机仍串行执行文件任务，复用原条件提交、备份、事务恢复和增量快照。失败与退避按连接持久保存，一台离线 PC 的文件错误不终止其他 PC 的处理；同一失败连接本轮后续映射延期。局部错误不启动全局退避，也不会被报告为全部同步成功。目录刷新恢复保留未完成回执，某个不可用本地目录不阻断其他目录的恢复。

目录树、预估等 UI 查询使用独立连接；取消只关闭当前任务所属的传输连接。冲突按 mapping/scope 对应的 PC 交换，不使用当前页面选择的凭证。

Windows capabilities 和 `lan_sync_status` 增加 peerScheduleProtocol=1。协商后认证的 `file_schedule` 可携带 peerId、pairedComputerCount（1–32）和 metadataPeer。仍限定原凭证所属 scope，旧客户端可以省略；新手机连旧 PC 不发送这些字段。它们是手机上报的连接诊断，可能陈旧；last* 传输计数仍为手机轮次聚合，不能据此归因单台 PC。已有 MCP 工具及参数 Schema 不变，运行时描述和[使用文档](../desktop/docs/mcp.md)同步更新；开发与正式版共用代码。

## 验证

- Android 同步／引用单元测试 41 项通过，覆盖迁移幂等、凭证和基线保留、同根隔离、重叠目录拒绝、兼容别名、主电脑移除、未知格式拒绝和无凭证映射保留。
- `LanSyncStoreTest` 在模拟器通过 4 项持久存储测试，含真实文件迁移备份和重启恢复、两台 PC 同根映射共存、损坏配置不被覆盖。
- Windows 同步存储和 MCP 回归 75 项通过；共享曲库／引用回归 28 项通过。
- `test-multi-peer.py` 启动两个独立数据目录的真实 Windows TLS 客户端。模拟手机真实配对、读取各自根、在私有合成目录双向同步同名文件，验证凭证保持、显式映射路由、离线隔离及实际页面选择；两张截图已实际查看。该专项使用私有文件 IO，不代替 SAF 目录授权验收。
- 原 `actualEventsReconcileBothDirectionsAndRespectCancelAndPause` 在 emulator-5584 使用真实 SAF、生产调度器及 Windows 客户端通过，覆盖事件、双向修改、暂停、取消、恢复；主机 TLS 条件提交、冲突选择和本地备份恢复也通过。未操作实体手机。

证据位于忽略的 `desktop/artifacts/multi-peer-p1/`（result.json、first-selected.png、second-offline.png）。测试夹具使用临时 PC 和合成文件，邀请／凭证不写入证据；主机侧另保存测试前调试客户端配置，成功或崩溃后都恢复它，避免只依赖测试进程的 finally。早期夹具的路径别名和显示字段错误均已修复，最终专项通过；未放宽生产的 Wi-Fi、无链接或目录校验。

```powershell
cd mobile
.\gradlew.bat :app:testDebugUnitTest --tests 'com.tinyfish.jeekmusic.data.sync.*' --tests com.tinyfish.jeekmusic.data.backup.BackupRefsTest :app:assembleDebug :app:assembleDebugAndroidTest
cd ..
python desktop/tools/test-multi-peer.py --exe bin/JeekMusic.exe --android emulator-5584 --evidence desktop/artifacts/multi-peer-p1
```

上述验证使用单台主机的两个独立 Windows 进程和一个 Android 模拟器，尚不包含跨真实主机、两个手机、同步组传播或 PC 出站文件同步验收；这些仍是 P2/P3 的交付项。

Windows 日常 ReadyToRun 和 Android debug／release 构建通过；日常版保存状态后重启，模拟器重新安装并启动调试版。未向实体手机安装测试包。
