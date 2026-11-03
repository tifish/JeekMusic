# 设备音乐同步（TLS传输4 / 文件合同2）

文件以最后一次双方确认的存在状态、size 与两端实际 mtime 版本作基线双向同步；用户数据沿用 format 2 / SyncMerge，个人学习事件沿用协议3。无需云账号、中继或自动发现。公网监听、预算、部署边界见[公网部署说明](lan-public-deployment.md)，不会自动开放公网或桥接MCP。

## 目录、映射和旧数据

Windows开启服务后自动共享全部已登记曲库根，新增曲库自动加入，不要求逐目录选择。rootId绑定不可复用的folderIdentity；移除曲库永久撤销旧rootId与计划，行号复用不会重定向授权。离线根保留身份，未知状态绝不解释为删除。

Android从电脑根内选择全部或子目录，通过系统SAF选择本机目标。**选择的目录就是最终根**：文件写到`目标/远端相对路径`，不追加rootId、电脑目录名或隐藏子目录。目标登记为普通曲库根，原有音乐继续可用。rootId只作内部授权与引用身份。

新映射layoutVersion=2。旧映射不可执行，提示重新选择；不兼容、不迁移，不搬动或删除旧子目录/音乐，也不复用旧计划、部分文件或所有权。配对、普通曲库、标注、学习数据保留。改变目标或选择范围建立新的mapping key与基线；相同有效映射可继续使用原基线。

不同远端根的目标不得相同、父子重叠或大小写等价。取消、停用、忘记映射均保留文件。旧“清理手机副本”按钮已移除，以免正常删除被误解为仅清手机。SAF授权、所有文件访问权限和真实无链接目录缺一时延期，不降级到非条件覆盖。当前支持本地ExternalStorageProvider（内存/SD卡），不支持网盘Provider。

目录中仅建立一个不含远端身份的`.jeek-sync-directory-id`标记，用来识别目录被替换/存储重新挂载；它不是文件命名空间。丢失/更换标记时停止写删，重新选择目录建立新基线。不能将目录不可达、扫描失败、权限丢失或选择外路径当作空目录。

## 自动处理与真实冲突

变化检测严格使用相对路径、size 和实际 mtime（协议 `m2:size:modifiedNs`），只有双方确认后推进持久基线。仅 mtime 改变也按修改处理，不计算 hash 排除。**同 size、同 mtime 的内容改变视为未变化**；不能声称能发现所有内容编辑。元数据版本不是安全身份。

基线同时保存 `localVersion`（Android 实际落地）和 `remoteVersion`（Windows 实际落地），不把 PC ns 原值假装成 SAF/FUSE 已成功保存的时间。Windows 表示精度 100ns，Android 使用 native stat 的 ns 表示；底层实际精度由提交后读回值体现。每一端只与自己的精确基线比较，无宽容时间窗口。舍入差异只在已确认的落地对之间建立映射，不会把附近时间的两次独立修改合并。双方新元数据精确相同自动确认收敛。

日常 manifest、Android scan、file_probe 和 file_ack 均不哈希文件。CUE 路径依赖仍保留：仅新增/元数据变化的 CUE 需要读取至多 2MB 解析，解析输入按精确元数据缓存在私有目录（最多 4096 项/64MiB）；未变化第二轮和重启后的 CUE 使用持久缓存，只枚举依赖/旁文件元数据。缓存损坏需要重建时有独立 sidecarParseBytes 计数，不偷偷哈希音频。

| 上次确认后变化 | 行为 |
|---|---|
| 单边新增或修改，另一边没改 | 自动传到另一边 |
| 单边删除，另一边仍是基线元数据版本 | 自动传播删除，保留恢复备份 |
| 两边删除 | 确认收敛 |
| 两边变成相同 size+mtime | 复用并确认，无需重复传输 |
| 两边改成不同元数据版本 | 保留双方，显示冲突 |
| 修改与删除相遇 | 保留修改版本，显示冲突 |
| 任一侧未知/离线/扫描失败 | 延期，绝不删除 |

首次无基线：单边存在自动补到另一边；两边同 size+mtime 复用并建立基线；同路径元数据不同无法判断历史，显示真实冲突。首次单侧缺文件不是删除。只有本映射明确确认过的文件才允许传播删除。用户选“使用手机版本/使用电脑版本”仍要重新校验双方hash与基线版本；并发变化不会被无条件覆盖。

原子提交与确认是独立边界：操作号、上传块与提交结果可幂等重放；收到确认前不推进基线。断线/取消/重启保留事务记录，恢复已提交结果后再处理后续编辑。双方最后一次确认后又有变化时仍按三方规则处理，不用mtime猜顺序。

## 自动同步与取消

有效配对和当前 `layoutVersion=2`、enabled 映射即启用自动文件同步。Windows 复用现有 FolderWatcher，2秒合并事件（含溢出）；手机针对已授权本地 ExternalStorageProvider 的实际目录注册 FileObserver，最多4096目录，不监控 Document URI。目录事件只作提示；2秒 debounce、持续突发最多10秒合并，操作前再等待500ms并精确复查版本。一个客户端任务串行，运行时事件置 dirty，完成后至多续一轮；自身写入可能多触发一轮无变化元数据对账，随后停止。首次启动、重新前台、网络可用、新映射和恢复启用触发补对账。自动调度仅运行文件对账；立即同步仍沿用现有用户数据/学习 delta 交换，独立学习服务不因暂停文件映射而停用，文件扫描不会借学习验证预扫音频。

前台每30秒通过短期认证请求读取 PC 根 revision（进程 epoch+watch序号+根修订），连接随请求关闭；不会占满公网槽等长连接。即便没有事件，每15分钟补元数据对账；watch失败/溢出/重挂载/重启漏事件由此补偿。网络/忙碌错误按30秒至15分钟指数退避，保持原有设备/全局限流。已有冲突以映射/路径/基线/双方元数据形成稳定标识，不反复弹窗或传输；相关版本变化或用户明确选择后重新决策。

Android 后台使用正式 WorkManager：connected约束、15分钟周期（系统可推迟）、指数退避至少15分钟；离开前台时，仅将没有系统执行保障的前台任务安全取消并交给普通 Worker；已经由 WorkManager 持有的任务继续运行，不因页面退后台而错误取消。每次后台工作最多8分钟，保留已有分块/事务恢复能力；没有借用播放服务、申请电池豁免、长期 dataSync FGS 或精确闹钟。大任务可在前台继续。强制停止、Doze、系统配额、离线期间不承诺实时持续。依据：[FileObserver](https://developer.android.com/reference/android/os/FileObserver)、[PeriodicWorkRequest](https://developer.android.com/reference/androidx/work/PeriodicWorkRequest)、[Android 15 FGS 时限](https://developer.android.com/develop/background-work/services/fgs/timeout)。

“取消”中断当前工作，并持久抑制自动文件同步，直到点击“立即同步”或重新启用映射；watch/前台/重启不会立即把取消复活。暂停只作用于该文件映射，保留文件与基线，不关闭播放、学习或配对；立即同步仍跳过暂停映射。忘记映射同样保留文件。旧映射不会参与 watcher/后台自动同步，也不迁移旧目录。

## 基线升级

不删除/重建原 scope、baseline 或事务表。新增 metadata 列保存合同2双端版本，已有合同2精确证据直接复用；原合同1基线只有 hash，没有足够的双方实际 size/mtime，不能从 hash 或“较新mtime”伪造版本。旧 tombstone 保留版本与已删除历史；双方都缺失的旧存活记录确认成 tombstone，双方当前元数据相同则安全建立新元数据基线。旧存活记录双方不同（包括单侧缺失）保留 `legacy_metadata_unconfirmed` 冲突，不据缺失传播误删，也不自动重新下载。用户按既有冲突选择解决。新首次无基线单侧新增仍自动补齐。不会清标签、学习、配对、SAF权限或其他配置。

传输/安全与变化判断独立。仅确实需要传输、删除或恢复的文件调用按需 file_verify（计划内只读依赖用 verify），取得操作摘要；256KiB传输块使用TLS，接收部分文件做完整性核验，提交继续使用句柄、路径检查、同名抢占拒绝、摘要、条件提交和持久恢复日志。可能多次读取单次操作的目标/暂存/备份，不宣称省掉所有操作IO。确认记录落地版本；提交已成功但确认丢失时通过持久操作结果恢复因果关系，再三方处理后续编辑，不把时间舍入误报成永久冲突。

## 权限与文件安全

配对是全部现有同步操作的授权边界：readMusic、writeSync、writeFiles、deleteFiles、manageLearning、downloadLearning 对有效已配对设备统一为 true，不再逐项选择。旧 false/缺失权限在启动时统一归一，token/generation 与其他配置保留，旧设备无需重配。细粒度 authorize / invite.permissions 明确拒绝；移除访问使用 revoke。MCP 与 UI 使用同一服务，兼容 read 诊断保留。

持久 token 长期有效直到手动撤销或重新配对，不执行历史 expiresAt。邀请码仍为 256bit、5 分钟、一次性原子消费；Android 验证 TLS pin 与有效期。撤销立即关闭连接、取消该设备计划/上传/学习；复用 deviceId 不复活旧 token 或 scope。首次实际学习下载仍需原有操作确认，权限迁移不启动同步/学习。配对全权限不意味着允许任意磁盘写入或代码执行。

Windows 自动维护精确程序路径、TCP 实际监听端口与本地绑定地址的入站规则，所有网络配置文件，非回环来源 Any（兼容 LAN 与既有公网映射）。停服、端口及升级路径变更只清理经核验的自建旧规则；回环无需放行。UAC/策略失败独立报告，不修改路由/UPnP；详情见部署说明。

文件合同2在传输4内协商；两端均必须协商 fileProtocol=2。旧端的 plan 和文件操作收到 upgrade_required，不会收到空 hash 后误判删除；不自动回退全库哈希。已有 TLS pin、长期凭证和授权不变，无需重配。manifest 不再发送内容摘要，存在由 exists/metadataVersion 明确表示。所有文件操作限定到当前credential-owner/root/mapping/selection scope，读块/上传块最大256KiB。只允许原音频、CUE/LRC和封面扩展名，并验证上传容器签名、拒绝可执行文件封装。CUE路径不能越根；音轨依赖有冲突、未同步或在选择外时，上传延期。

Windows以无reparse父目录/最终句柄固定目标，独占原文件并条件重命名；Android使用openat/O_NOFOLLOW/renameat2(RENAME_NOREPLACE)、句柄身份、hash复核与持久日志。占用/并发更改延期，保留双方；不沿链接、不写出根、不递归删除用户目录。平台不支持NOREPLACE时使用明确的条件复制合同；独占创建、句柄身份或边界保护不可用则拒绝，不使用公共目标覆盖。

替换/删除把旧文件留在同目录`.jeek-sync-<operation>.bak`；它是恢复文件，不是共享目录。备份达到32GiB预算时停止新增变更，不能为继续同步擅自删备份。Windows本机MCP`lan_file_backups`分页列出回执；停服务后可restore或purge单个已核验回执。restore仅在当前文件仍匹配该事务结果时执行，保留替换结果且不改同步基线；purge只允许已确认且hash未改变的备份。未经确认的prepared事务保留，扫描跳过该根的短暂缺席，等待事务恢复。

## 工作与容量预算

全局文件工作1项，无无界等待队列；file_probe 直接返回元数据，file_verify/确认/提交返回可查询可取消的有界任务，连接与任务寿命分离。网络层总连接、IP/设备限流、握手/空闲/写超时仍独立生效，详见部署说明。

单文件8GiB，单端暂存16GiB，恢复备份32GiB；每scope10万基线、桌面全局100万、最多128scope/每凭证8scope；事务25万、SQLite最大1GiB、页缓存8MiB。每计划10万文件/25万枚举项/64层/500GiB，扫描中即止损；快照与清单200条分页，不持有全库JSON。设备每天最多预约64GiB上传、全局256GiB（UTC日，失败预约亦计费，防重复消耗）；目标盘保留至少256MiB与约5%空间（上限5GiB），不会仅检查私有暂存盘。

上传暂存2小时/闲置10分钟回收，prepared事务不当作无用暂存删除。凭证永久有效与这些资源TTL互不影响。大曲库可分选择范围分批，预算拒绝明确延期；不会为了通过同步关闭保护。

## 曲库和学习数据

提交前持久登记受影响路径；MediaStore与LibraryScanner定向刷新后清除。CUE绑定按当前hash核验，刷新新旧音轨并集。正在播放/预加载的文件避让，不清队列。用户数据继续合并播放列表、偏移、手工标签、规则、导航与支持的分析结果，不同步设备SAF、路径、布局、私钥或播放器位置。

个人学习四维独立，模型同步不代表启用，也不在手机执行Windows产物。任务归属与权限见[个人学习](personal-tag-learning.md)；结构化错误及本次解码裁尾修复见[学习失败报告](learning-failure-reporting.md)。

## 验证与限制

`desktop/tools/test-lan-security.py`在新私有数据、合成文件和回环临时端口运行真实TLS慢握手/帧/限流/撤销/恢复。`test-lan-files.py --exe <candidate> --android emulator-...`运行真实服务文件合同与Android生产调解器的变化矩阵，不读取生产凭证或真实曲库。Core/Android复用`shared/testcases/file-reconciliation-v1.json`，另有Windows句柄事务测试与Android原生句柄instrumentation。

最新结果以[公网验收记录](lan-public-acceptance.md)与交付记录为准。旧候选文档描述历史行为，不能代替当前双向合同。隔离负载不等于完整审计；真实公网、分布式耗尽、所有OEM/SD卡和系统崩溃组合未据此宣称验证。TLS是自定义TCP协议，不是HTTP，部署须TCP透传，不终止TLS或导出私钥。

文件hash工作另有每凭证每小时2TiB、全局4TiB读取预算，进程重启清零；上传日预约持久保存。Android公共共享存储的FUSE可能不实现advisory flock，遇ENOSYS/EOPNOTSUPP/EINVAL时仍执行句柄身份与完整hash复核、RENAME_NOREPLACE及保留原文件；真实锁占用仍延期。flock本身不是其他应用必须遵守的写锁，不能据此声称能阻止恶意本机应用的所有竞争。句柄/路径保护不可用时拒绝提交。公共FUSE目录不支持NOREPLACE时使用下述明确的条件复制合同，绝不对公共目标使用覆盖rename。依据：[Android共享存储权限](https://developer.android.com/training/data-storage/manage-all-files)、[Linux renameat2](https://man7.org/linux/man-pages/man2/rename.2.html)、[Windows句柄重命名](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info)。

Android公共共享目录实测不支持flock/NOREPLACE，不能用应用私有目录测试结果冒充指定SAF目录可用。其commitMode=conditional_copy明确采用：原文件移到本应用排他创建、私有日志固定目录身份的`.jeek-sync-<operation>.recovery/original`恢复槽；公共目标只能O_EXCL创建，按私有日志记录的inode复制/续传，重启仅续写与源完全相同的已有前缀，不截断公共文件。目标被其他文件抢占或已有前缀被修改时保留双方，进入延期/冲突。普通rename仅面向该内部新建恢复槽，绝不用于覆盖公共目标。这些内部目录只存恢复版本，不是音乐文件命名空间，所选目录仍直接保存音乐相对路径。

条件复制期间本应用扫描器依持久事务排除未完成路径；其他不协作的应用可能看到部分目标文件，因此不宣称公共FUSE上具备整文件原子可见性。拥有本机广泛文件权限的恶意应用可破坏共享存储内部恢复槽，属于额外本机信任风险；网络协议拒绝这些点号内部路径，不能由远端选择或写入。使用完整hash/身份复核、原版本保留和条件创建限制正常并发竞争，不能据此声称抵抗已失陷本机的所有竞争。


后台真实验证入口：`python desktop/tools/test-lan-background.py --exe <fixture EXE> --android emulator-5584 --out desktop/artifacts/<private-output>`。严格限定命名隔离 AVD、debug 包和公共 SAF 合成子目录，保存并恢复原配对/映射配置，不调用应用 start()。实际 UI 重新启用映射产生到期的 WorkManager 工作，传输期间退到后台，验证 32MiB 内容及 foreground=false/trigger=workmanager_fallback。自然15分钟周期、实体机Doze/厂商限制另列未验，不通过篡改调度数据库提前到期。
