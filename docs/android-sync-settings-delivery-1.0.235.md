# Android 同步设置精简：1.0.235

2026-11-03。源码提交 `1bc6359f6e1d50507cea8936904fd170574e5243`，基于 `3144bff`（上轮正式软件 `8dacc51` / 1.0.233）。全部直接完成，未委派。后续证据提交只更新文档，不重建软件。

## 实际移除与合并

| 原位置 | 当前行为 |
|---|---|
| 设置首页“设备同步 / 连接电脑”与“数据同步与备份”双入口 | 首页“设备”分组只保留“同步 / Sync”，进入同一页 |
| LanSyncSettings 与 SyncSettings 分属两页 | 同页按“电脑连接”“同步目录”“自动同步与状态”分组；高级兼容位于页尾 |
| 立即备份、上次备份、从任意备份文件恢复、旧备份恢复提示 | 移除入口、文件选择器、BackupManager / RestorePrompt 专用代码及中英文资源、失效 baseline profile 引用 |
| 同步内独立“文件恢复备份”列表及恢复/清除按钮 | 移除独立管理 UI，不执行清理；文件事务与恢复 API 保留 |
| 数据页另一组“立即同步” | 移除重复命令，同页只保留 LAN 服务的“立即同步 / Sync now”以及取消 |
| 目录同步兼容（Syncthing / format 2） | 收到默认折叠的“高级 → 旧版目录交换”；协议、现有开关与持久化方式保留，不自动关闭配置 |

电脑配对状态、扫码/手工配对、共享根刷新、目标目录映射、每映射启停、立即同步/取消、状态和问题提示均在同页。选定目录就是目标；旧映射仍需重选，不能重新启用旧布局。停用映射即暂停且保留文件，取消持久抑制自动任务；立即同步或重新启用映射沿用现有恢复语义。取消按钮在自动映射空闲时也可达，不必等待传输开始才能停止后续自动任务。

全权限配对、永久凭证、size+mtime 调解、前台监控、WorkManager 和后台限制未改。原数据同步状态仍在同页显示。实际被覆盖的播放列表才出现历史恢复动作，具体文件冲突仍在对应文件下处理。没有重写文件同步或训练算法。

`BackupRefs` 仍支撑普通播放列表和同步的路径匹配，保留。启动改为直接调用原幂等 `SyncService.start()`；添加音乐目录仍等扫描空闲再 `syncNow()`。没有新增页面启动同步的副作用。替换/删除恢复槽、回滚、持久事务与启动恢复完整保留，不删除历史备份文件、标签、学习、配对、映射或音乐。

保留 `settings_group_devices`、`lan_start`、`lan_cancel` 等原 resource-id，移除的 `settings_group_data` 不再可点击；旧 `SettingsPage.DATA` 保存状态归入同一同步页。`TopRoute.Settings` 不变，MainActivity 没有设置子页外部深链。返回、滚动、展开和偏好持久化有真实 UI 覆盖；配对及共享目录弹窗补齐 resource-id 导出。

## 验证范围

本地定向同步/路径匹配单元测试 20 项通过；最终源码 `lintDebug` 与正式签名 release 构建通过，Lint 为 0 错误、89 警告。[本次 Mobile CI](https://github.com/tifish/JeekMusic/actions/runs/41644522179) 的完整单元测试 120 项通过，0 失败、0 跳过；Lint 和 release 构建成功。

使用已有隔离 `JeekLanEmpty35` / `emulator-5584`（API 35），未触碰实体手机。设备 MCP 返回 `Agent device access is turned off`，实际 UI 改用 ADB + UIAutomator 操作并截图目检，未将 MCP/单元断言冒充 UI 验收。

本地签名候选完成中英文首页 → 同一同步页 → 配对弹窗 → 目录/主要动作 → 高级 → 返回。兼容开关临时改变后，验证返回和进程重启仍持久，随后还原。遍历整页确认无旧备份产品控件、无第二组立即同步。返回时首页及同步页滚动位置、展开状态保持。

离线夹具只含虚假凭证、关闭的 Android 回环端口 9、无 SAF 授权且不存在的合成目标；未连接真实 PC/曲库。验证目录错误和重试、旧映射不可启用/需重选、当前映射恢复/暂停、立即同步错误提示、取消持久化与重启保持。完成后原配对配置逐字节还原，夹具映射没有遗留。截图中的 `Offline UI fixture PC` 和两个目录均为该夹具。

本轮没有重跑真实文件传输、冲突写删、训练全量矩阵、自然 15 分钟后台周期或 OEM/实体 SD 卡验收；底层相关实现未变，不将历史测试充当此次新验证。必要回归工具为 `mobile/tools/settings_ui.py` 的 `verify-sync` 与 `verify-sync-offline`，后者仅允许指定离线合成夹具。

PC 软件与 Windows MCP 没有改动，没有无关 Windows 构建、重启或 UI 验收。

## 正式交付及数据保全

[正式签名 APK 1.0.235](https://github.com/tifish/JeekMusic/releases/download/v1.0.235/JeekMusic-1.0.235.apk) 已下载核验并用 `install -r` 覆盖安装到同包名正式应用，没有卸载或清数据。

| 核验项 | 实际结果 |
|---|---|
| Mobile CI headSha | `1bc6359f6e1d50507cea8936904fd170574e5243` |
| `v1.0.235` 实际 Git tag 对象、Release target_commitish | 均为同一源码 SHA |
| APK 包名 / versionCode / versionName | `com.tinyfish.jeekmusic` / `235` / `1.0.235` |
| APK / GitHub 资产 digest / 设备实际 base.apk 的 SHA-256 | `df4d9fc443e6fccfe76a21c501d6b4c4933f5cef648bfc925b057ad12fb46d31`，三者一致 |
| 正式证书 SHA-256 | `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe`，与原版一致 |

下载的最终 CI APK 再次实际完成英文与中文首页 → 同一同步页 → 手工配对弹窗 → 主要动作/高级 → 页首返回。两个语言均截图目检，版本显示 1.0.235；页首返回与原系统返回路径均有覆盖。未配对时目录和同步命令正确禁用，提示先配对；兼容开关保持原值。离线动作的完整持久化验收使用同源码的本地签名候选（提交前 Git 计数为 234），公开截图明确区分阶段。

数据保全分两阶段复核：修改前至最终版本，`jeek.db` 的 18 个表逻辑内容一致，settings/startup 两个 DataStore 文件、原配对配置逐字节一致；最终 CI APK 覆盖前至覆盖及中英文复核后，另包括 `preferences.db` 7 表、evaluation 1 表、文件事务数据库 9 表，共 35 表全部逻辑一致（计数包含 SQLite/Room 元数据表）。首次安装时间及原安装回归标记不变。最后恢复中文及原配对配置，0 个有效/旧映射，无夹具遗留；正式 1.0.235 客户端已结束旧进程并重新启动，停在中文同步页。实体手机没有更新。

公开的[验证 JSON](evidence/android-sync-settings/verification.json) 保存本次来源、测试、两阶段保全结果和 17 张截图清单，不包含原始凭证、私有配置或数据库快照。主要截图：

- 正式 APK：[中文入口](evidence/android-sync-settings/zh-CN-home.png)、[中文同步页](evidence/android-sync-settings/zh-CN-connection.png)、[中文动作与状态](evidence/android-sync-settings/zh-CN-actions.png)、[中文返回](evidence/android-sync-settings/zh-CN-return.png)。
- 正式 APK：[英文入口](evidence/android-sync-settings/en-home.png)、[英文同步页](evidence/android-sync-settings/en-connection.png)、[英文高级兼容](evidence/android-sync-settings/en-advanced.png)、[英文返回](evidence/android-sync-settings/en-return.png)。
- 本地同源码离线候选：[目录错误/重试](evidence/android-sync-settings/offline-folder-error.png)、[旧映射重选](evidence/android-sync-settings/offline-old-mapping.png)、[具体问题提示](evidence/android-sync-settings/offline-sync-issue.png)、[取消保持](evidence/android-sync-settings/offline-cancelled.png)、[映射暂停](evidence/android-sync-settings/offline-paused.png)。
