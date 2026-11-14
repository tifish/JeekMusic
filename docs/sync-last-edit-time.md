# 文件时间、大小和最后编辑时间

2026-11-15，按用户指定的简化规则执行。每个文件只保留最后一次成功软件编辑的时间，不记录编辑历史、字段操作列表或历史内容。

两端文件都存在时：

1. 文件 mtime 不同，以时间较新的文件为准，即使另一端也有变化或软件编辑时间更晚。
2. mtime 相同，大小相同，认为一致，不读音频内容、不查软件时间、不传输。
3. mtime 相同，大小不同，才查询 `editedAtMs`，以时间较新的记录为准；缺失为 0，记录相同则保留冲突。

例如，修改歌词后文件大小变化但 mtime 保持不变，使用保存的最后编辑时间确定同步方向；若编辑后大小也没变，按用户指定策略视为一致。

首次只有一端存在时补齐文件；首次缺失不表示删除。已确认的删除、修改与删除相遇、未知/离线、扫描未完成等仍受原有基线和并发检查保护。复制后不同文件系统可能舍入时间，双方都没有再变的已确认落地对应关系直接复用，避免相互反复传输。

Windows 与 Android 的原生歌词/封面写入成功后覆盖该路径的最后时间，并保留原文件 mtime；失败/跳过不会推进。记录绑定实际 size/mtime，外部变化导致元数据不匹配时返回缺失。软件时间采用设备的 Unix 毫秒记录，无法补出启用前没有记录的操作。全文件上传/下载沿用源记录，不把接收时间当作编辑时间。新策略通过现有 TLS 能力协商，旧组合继续旧行为，无需清理原基线或重新配对。

Windows 使用独立 SQLite 单行键值表，Android 在现有私有同步数据库中增加 `latest_edit` 表，均按路径覆盖一行。普通扫描不查询该表；只有必要的大小决策或变更文件的校验/落地才访问。源文件元数据检查、条件替换、恢复备份、原子事务及断点续传保持生效。

## 验证入口

```powershell
dotnet test desktop/tests/JeekMusic.Core.Tests -c Release --filter FullyQualifiedName~FileReconciliation
dotnet test desktop/tests/JeekMusic.App.Tests -c Release --filter "FullyQualifiedName~Lan|FullyQualifiedName~FileEditTimeStore|FullyQualifiedName~TagWriter"
cd mobile
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug :app:assembleDebugAndroidTest
cd ..
python desktop/tools/test-lan-files.py --exe bin/JeekMusic.exe --android emulator-5584 --public-saf --case lastEditTimeBreaksOnlyEqualFileTimeAndDifferentSize
python desktop/tools/test-lan-files.py --exe bin/JeekMusic.exe --android emulator-5584
```

两端共用 `shared/testcases/file-reconciliation-last-edit.json`，覆盖规则优先级、缺失与相同记录、首次存在、舍入稳定性和删除边界。时间存储测试验证同一路径只剩一条记录、重启后保留、旧 size/mtime 不借用时间。真实跨端用例使用隔离 PC 服务和模拟器，核对字节结果、自动方向、两端传递、同元数据零传输；公开 SAF 用例还经生产 AudioFileEditor 连续内嵌歌词，确认保留 mtime 并覆盖保存时间。

## 本次结果

核心比较回归 3 项、桌面 LAN/最后时间存储/标签写入回归 250 项、Android 单元测试 130 项全部通过，Windows 与 Android 构建成功。8 项真实 Android/Windows 协调器回归全部通过；最终 Windows 构建再次通过公开 SAF 新规则及连续内嵌歌词测试。原始结果分别在 `desktop/artifacts/last-edit-core-tests.log`、`last-edit-desktop-tests.log`、`last-edit-android-build.log`、`last-edit-all-integration.log`、`last-edit-final-public-integration.log`。

实际操作新版客户端点击立即同步、按 Home、等待后台完成、返回前台并等待再次对账。32MiB 主音频字节一致、20 个小音频完整且 MediaStore 已入库；返回后传输字节为 0、没有取消/失败退避。已查看 `desktop/artifacts/last-edit-ui/android-preparation.png` 和 `android-returned.png`，确认新的判定规则说明、完成状态和控件显示正常；原始调度结果在同目录 `background-result.json`。UIAutomator 的短暂 null-root 返回经重试恢复，未跳过真实操作或截图。

PC 保存原状态后部署重启，生产 MCP 核对新进程、`fileComparisonPolicy`、`history=false` 及兼容的无参数 Schema 均生效。Android 夹具恢复原配置后重新启动。测试仅使用隔离模拟器和合成数据，未连接实体手机。
