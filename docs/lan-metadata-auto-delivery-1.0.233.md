# 元数据变化检测与自动同步正式交付：1.0.233

2026-11-03 核验。正式软件源码为 `8dacc51e19e93e47b7cec22df50b7c358e8f3615`，本记录的后续文档提交不改变软件或重建同版本包。前一正式版本及配对/防火墙合同见 [1.0.229 记录](lan-paired-delivery-1.0.229.md)。本轮全部直接实现，未委派，也未新增外部自动调度任务。

## 交付与来源

| 平台 | 同一源码的成功 CI | 正式包 | 下载字节 SHA-256 |
|---|---|---|---|
| Windows x64 NativeAOT | [Desktop](https://github.com/tifish/JeekMusic/actions/runs/41636103631) | [1.0.233 ZIP](https://github.com/tifish/JeekMusic/releases/download/desktop-v1.0.233/JeekMusic-desktop-1.0.233-win-x64.zip) | `ac7c702c208127910b91c12d6df570950db9da68dc965ef7ff0e4d09c553d895` |
| Android 正式签名 | [Mobile](https://github.com/tifish/JeekMusic/actions/runs/41636103656) | [1.0.233 APK](https://github.com/tifish/JeekMusic/releases/download/v1.0.233/JeekMusic-1.0.233.apk) | `d3b9ac96f746a9b148bb390afb24f7c19c4466a6d29ec829f5391ec9763d9523` |

两个 CI 的 headSha、Release target_commitish、实际 `desktop-v1.0.233` / `v1.0.233` 标签均为上述完整源码 SHA。下载摘要与 GitHub 资产 digest 相符。Windows FileVersion 为 `1.0.233.0`，ProductVersion 为 `1.0.233+8dacc51e19e93e47b7cec22df50b7c358e8f3615`，Authenticode 仍为 **NotSigned**。APK 包名 `com.tinyfish.jeekmusic`、versionCode 233、versionName 1.0.233；其来源由同 SHA 的 CI、标签和资产共同核验。APK 签名验证通过，证书 SHA-256 `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe` 与原正式包一致。

实现分为 `9d36b40`（两端元数据基线与自动协调）、`4293461`（扩展 SAF 夹具复用 scope，遵守每凭证 8 scope 预算）、`8dacc51`（WorkManager 持有的同步退后台后继续运行及真实后台夹具）。最终软件 SHA 的两项 CI 均通过；本记录只补交付证据。

## 比较与迁移合同

变化判断使用相对路径、size、实际 mtime，版本为 `m2:size:modifiedNs`。仅 mtime 改变也算修改，不再计算内容摘要来排除。**同 size、同 mtime 的内容改变视为未变化**，这是明确采用的策略；不能检测所有内容修改。扫描、plan、scope/probe、确认均使用元数据，没有隐藏的全文件 file_probe 哈希。CUE 解析结果按精确元数据持久缓存；首次或改变的 CUE 仍需读取解析，单独计入 sidecarParseBytes。

基线分别保存远端版本与本地实际落地版本及精度信息。每端与自己的历史落地版本精确比较，不以宽容时间窗口吞修改。落地时间因 SAF/FUSE 舍入而与源时间不同时也能稳定确认；两个不同版本确实各自改变仍是冲突。双方相同元数据自动收敛；有明确基线的单边新增、修改、删除自动执行；双改、改删保留冲突。首次同路径元数据相同直接复用，即使字节不同也遵循用户策略；元数据不同且无基线时保留冲突，不按较新 mtime 覆盖。

原 baseline/scope/事务不清空，新增 metadata 信息。可信的原有双方版本直接复用；仅有旧 hash 的存活记录不足以推导 size/实际 mtime：双方当前元数据相同才建立新基线，双方缺失则确认 tombstone，双方不同或单边缺失进入 `legacy_metadata_unconfirmed`，不凭缺失误删或自动重复下载。已有 tombstone 历史保留。离线、未知、权限丢失、扫描失败、目录身份变化不解释成删除。旧布局映射仍须重选，不自动迁移，不操作旧子目录。

文件合同升级到 **2**，传输合同仍为 4。旧端文件操作明确返回 `upgrade_required`，需要两端升级；不发送可被旧端误解的空 hash，也不静默退回全库摘要扫描。配对永久凭证、全权限、TLS pin、标注、学习和 SAF 权限不清除。

## 自动同步行为

已配对且当前有效 enabled 映射自动工作。Windows 复用曲库目录 watcher，将变化转换为认证后的轻量 revision；Android 前台有界轮询远端 revision（30 秒），同时对获得 SAF 和所有文件访问授权的真实本地目录使用 FileObserver，不假定 Document URI 可监控。事件 debounce、等待写入稳定，客户端文件任务互斥；运行中事件置 dirty 并续一轮，自写后无变化轮次收敛。首启、回到前台、网络恢复、映射新增/恢复触发对账，900 秒低频元数据对账补漏事件。短请求保留既有认证、连接、限流及工作预算，不占用持久在线连接。

取消会停止当前任务并持久抑制自动文件同步，直到立即同步或重新启用映射；watch、重启或回前台不会立即重启取消的任务。暂停只影响对应文件映射，立即同步也跳过暂停映射，不关闭播放、配对或学习。冲突标识依映射/路径/基线/双方元数据稳定去重，只有相关版本变化或用户解决后重新决策，不循环弹窗或重复传输。自动轮次仅协调文件，已有用户数据同步保持其原业务逻辑。

Android 后台采用 connected 约束的普通 WorkManager，15 分钟周期和退避，系统可推迟；单次最多 8 分钟。前台任务退后台时安全交接，已由 Worker 持有的任务不会被 Activity 生命周期误取消。没有借播放服务维持同步、申请电池豁免、精确闹钟或长期 dataSync 前台服务。Doze、强制停止、厂商限制、系统配额和离线期间不承诺实时持续。官方机制与限制参见 [FileObserver](https://developer.android.com/reference/android/os/FileObserver)、[周期 WorkManager](https://developer.android.com/reference/androidx/work/PeriodicWorkRequest)、[Android 15 FGS 时限](https://developer.android.com/develop/background-work/services/fgs/timeout)。设置实际说明见[自动同步说明截图](evidence/lan-metadata-auto/android-auto-help.png)，[映射暂停截图](evidence/lan-metadata-auto/android-background-paused.png)。

MCP/诊断同步报告比较策略、metadataObservations、hashBytes、transferBytes、sidecarParseBytes，以及 trigger/dirty/running/backoff/paused/cancelled/lastSuccess/nextReconcile。开发版和正式 AOT 实测各 **40** 项工具，描述与 Schema 完全一致，复用相同业务逻辑。详见 [MCP](../desktop/docs/mcp.md) 和[同步协议](lan-sync.md)。

## 安全摘要仍何时发生

无变化对账不读取整文件、不计算内容 hash。确实需要传输、删除、条件提交或恢复时，才按需 file_verify 或在传输中计算摘要；目标、暂存、备份可能因完整性与安全身份复核被读取多次。size/mtime 只作变化版本，不作防恶意替换的身份。TLS、路径/句柄/同名抢占检查、条件创建提交、备份与事务恢复继续保留。公共 FUSE 不支持 flock/NOREPLACE 时仍按已有 conditional_copy 合同保留原文件并检查身份和完整摘要，不宣称整文件原子可见或能阻止所有恶意本机竞争。

## 验证结果与范围

最终 Windows Core 118、App 733 通过，2 项音频端点条件跳过；Android 单元测试 120 通过，lint/正式构建通过。真实 Windows TLS、配对能力、条件提交/删除与备份恢复回归通过。共享调解矩阵包含 18 项，覆盖精确比较、舍入映射、双改/改删、未知及迁移决策。

真实 Windows 服务与 Android 公共 SAF 合成目录完整矩阵 5 项通过（405.726 秒），原生 SAF/恢复 11 项通过。包括同 size 不同 mtime、不同 size、双改/改删、首次相同元数据复用（特意使用不同字节）、确认丢失后编辑、重开 SQLite/同步器恢复。实际测试介质本次保留了源 mtime（差值 0），其他精度舍入由共享矩阵验证，不冒充已在其他实体存储复现。

真实前台自动夹具通过：启动补扫、PC 改后手机下载、手机改后上传、突发事件合并、双向删除、取消保持/恢复、暂停/恢复、离线及前台恢复、自写反馈收敛。测试等待自动事件实际发生，没有直接调用 start 冒充自动触发。真实后台夹具通过：实际 UI 重新启用映射注册到期 WorkManager 工作，在 32 MiB 传输中退后台，最终 `foreground=false`、`trigger=workmanager_fallback`、内容一致。该操作 Android `transferBytes=33554432`、`hashBytes=100663296`，是操作核验三次读取的真实计数。自然等待 15 分钟周期与实体机 Doze 时序未测。

| 无变化合成样本 | 耗时 | 元数据观察 | hashBytes | transferBytes | sidecarParseBytes |
|---|---:|---:|---:|---:|---:|
| Windows 第二轮：128 × 256 KiB 音频及 1 CUE，音频共 32 MiB | 206.9 ms | 131 | 0 | 0 | 0 |
| Android 公共目录：32 MiB，连续两轮 | 92 ms | 以原生夹具日志为准 | 0 | 0 | 0 |

Windows 首轮仅解析 57 字节 CUE，hashBytes 同样为 0；持久缓存使无变化后续扫描不重新读取 CUE。上述是有界合成样本，不推算真实全曲库收益。重启后的缓存/确认恢复由定向测试覆盖。

未在实体机故障注入 watcher 溢出、存储重挂载或全部 OEM/SD 卡精度；机制保留元数据兜底和未知状态延期，不能把这些未测项目写成通过。所有同步写删验收使用隔离库，未在用户真实曲库制造变化或启动试验同步。可复用入口为 `desktop/tools/test-lan-files.py`、`test-lan-background.py` 与 Android instrumentation。公开计数、来源及保全布尔结果见[验证证据](evidence/lan-metadata-auto/verification.json)，原始私有日志、凭证、队列和数据库备份不入库。

## 当前正式客户端与保全

替换前即时 MCP 读取全部队列页、顺序/manual/来源、当前索引及播放进度、暂停/音量/静音、实际音频端点、筛选/列与布局、配对/学习状态，执行 checkpoint，保存当时配置及 SQLite 在线备份。未导入历史队列快照。替换后上述项目逐项一致，125 条原文件基线内容和键均完全一致；这证明当前基线未被清空，旧协议迁移分支另由夹具覆盖。

当前 Windows 为 CI 下载的 1.0.233 NativeAOT 正式 EXE，单客户端进程；显式与默认数据目录 MCP 连接同一实例。首次启动未建立 MCP 连接，重新启动后已完成全部实时核验；未据此声称已确认失败原因。开发输出已同步到日常启动目录。升级路径防火墙助手首次失败后，使用仓库既有受限脚本修复并重试成功；当前只有 1 条自建、1 条精确有效 TCP 应用规则，覆盖新 EXE/实际监听端口/绑定地址，状态 configured。未变更全局防火墙或 UAC 策略，外部可达性仍未验证。

Android 在隔离 `JeekLanEmpty35`（emulator-5584）将正式 1.0.229 用 `install -r` 覆盖到 1.0.233 并启动，没有卸载/清数据。firstInstallTime、配对配置逐字节一致；preferences.db 的 6 表、evaluation 的 1 表、jeek.db 的 17 表逻辑行全部一致。覆盖前有效/旧映射均为 0，未让测试映射遗留在正式包中；未连接或更新实体手机。夹具的公共合成目录已清理、debug 配置已还原。交付后的实际有效 enabled 映射会按本轮授权自动运行，旧映射仍不会触发。
