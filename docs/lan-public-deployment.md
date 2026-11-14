# 直接公网部署与安全边界（传输协议 4）

同步服务自动维护应用所需的精确 Windows 防火墙规则，不修改路由、UPnP 或公网映射，不自动创建生产邀请。该协议是 **TLS 1.2/1.3 内的长度前缀 JSON TCP 协议，不是 HTTP**。公网端到端可达性与分布式攻击未在真实公网验收；隔离负载回归不等于完整安全审计或绝对安全保证。

## 部署前必须了解

- 新安装默认关闭服务，初始绑定 `127.0.0.1:39843`。已有安装保留原启用状态、端口及原 IPv4 全网卡绑定，避免升级悄悄中断现有设备。`lan_sync_status.bindAddress` 是实际配置，不能把“局域网”名称当来源访问控制。
- 开启后仍自动共享**全部登记曲库**，新增曲库自动纳入；不要求逐目录勾选。读音乐的设备可读取所选根内允许扩展名的文件，不仅数据库收录的歌曲。管理员应只登记愿意共享的音乐目录。
- 设备凭证、TLS pin 和发布者代码签名是不同机制。Android 保留证书 DER SHA-256 pin 和证书有效期校验，不信任任意证书。Windows TLS 私钥留在当前用户证书存储，不导出到代理。
- MCP 仍是 current-user Windows named pipe/stdio 独立入口。**不要把 MCP、stdio 桥、named pipe 或调试接口转发到公网。**开发与 NativeAOT 使用同一服务、工具目录、Schema 和权限校验。

## 固定预算与过载行为

所有等待均有期限，接收循环不排队等待业务信号量。满载返回 `busy` 或在 TLS 前直接关闭；Android 合并连接间请求节奏，并对可幂等操作最多重试两次，每次先断开再等 30 秒，取消会中断等待。学习启动不自动重试。计数器为本次进程累计值，重启归零，不构成审计日志。

| 边界 | 预算 |
|---|---|
| 总连接 / 认证前 / 已认证 | 24 / 8 / 16；认证前每 IP 4，已认证每 IP 8，每设备 4（文件、学习、轮询及冲突状态独立会话） |
| 接收尝试 | 全局每秒 64；每 IP 每分钟 120；至多 1024 来源桶，60 秒窗口过后回收，容量满拒绝新来源，不无限增长 |
| TLS / 首次认证 | 各 5 秒总期限；逐字节到达不续期 |
| 已认证帧接收 / 普通操作 / 响应写入 | 分阶段 30 / 30 / 15 秒；错误响应最多 2 秒；旧端同步计划最多 90 秒 |
| 认证帧 | 4096 字节、4 层、8 字段/对象、32 节点、256 字符/字符串；控制字段最多 64 字符 |
| 普通请求 | 64 KiB；JSON 最多 32 层、100 万 token（DOM 前检查）和至多 100 万节点；拒绝重复字段、错误类型、非法 UTF-8；控制字段仍最多 64 字符 |
| 大请求 | state 保留 32,000,000 字节，preference_delta 4,000,000 字节；全局至多 2 个大帧/数据交换，认证后先校验小头与操作再接收正文 |
| 响应 | state 32,000,000 字节，其他 2,000,000 字节；流式有界序列化，超过预算拒绝；文件固定 256 KiB 块 |
| 请求频率 | 每设备每秒 80、全局每秒 320；state/delta 每设备间隔至少 500ms；计划每设备每分钟 4 次，学习启动每分钟 1 次 |
| 文件读取字节 | 每设备每秒 16 MiB、全局每秒 64 MiB；固定窗口允许边界短时突发，不是链路整形器 |
| 工作并发 | 全局计划 1；读取/树/数据交换共享另外 2 个许可，不被长时间计划哈希占用；设备最多一个活动计划任务 |
| 计划 | 30 分钟总期限、10 万文件、25 万枚举项、64 层目录、500 GiB、64 MiB 估算清单内存；遍历中立即检查，不先全量 ToArray |
| CUE | 每文件最大 2 MB，每 CUE 至多 1000 依赖，总依赖 10 万；依赖遍历计入工作预算 |
| 保留计划/任务 | 计划 2 小时、至多 16 个/总 20 万文件/估算 128 MiB；每设备新计划替换旧计划；至多 64 个任务记录，已完成同设备记录在新任务时回收 |
| 会话/凭证 | 会话最长 6 小时后重新连接，凭证每次请求重验；长期有效，直到手动撤销或重新配对；重新配对生成新 256 位 token，旧 token 失效 |
| 日志 | 安全计数不记录请求内容、token、邀请或私钥；日志队列 2048 条、单条 8192 字符、每批最多 257 条，文件 5 MiB 后轮转一个备份，满队列丢日志不阻塞 UI |

内存额度是载荷与工作集合的上界，不是进程 RSS 保证：JSON DOM、TLS、SQLite、UI、运行时及哈希缓冲还有额外开销。按 IP 限制无法抵御分布式耗尽；全局上限保证资源有界及停滞连接过期后可恢复，不保证持续攻击下每个新合法连接都有名额。大量合法设备共用 NAT 也受每 IP 8 个已认证会话约束。

大曲库使用子目录分批（每批不超过 500 GiB/10 万文件）；目录与清单均按 200 条分页，依次拉取直至完整。计划任务返回 `{pending:true,job}`；`plan_status` 查询，`plan_cancel` 可取消。任务独立于网络连接，断线后用相同设备凭证查询；手机取消会关闭传输，并通过独立、有界队列发送 plan_cancel；若网络或过载导致取消报文不可达，计划最迟在 30 分钟截止时结束。本地 MCP 可用 cancel_task/jobId 单独取消计划，不需要撤销设备。撤销设备、移除曲库根立即取消关联任务；目录新增不打断现有连接。终止期间已完成的网络发送不可收回，已提交的数据不会回滚。

设备连接配额回归：`python desktop/tools/test-lan-session-budget.py --exe <新版JeekMusic.exe> --report desktop/artifacts/lan-session-budget.json` 启动独立回环服务及临时合成曲库，实际验证同设备四条 TLS 会话、第五条拒绝与槽位回收。安装 debug 和 androidTest APK 后，可加 `--android emulator-5584`，让真实 Android 协议客户端的 `file_revision` 在连接占满时收到 busy，释放一个槽位后等待 30 秒重试成功。该测试不连接日常服务，不修改真实曲库或手机已有配对。2026-11-14 上述跨端验证通过；桌面 LAN 回归 103 项、Android LanProtocolTest 6 项通过。

## 权限与升级迁移

**配对即明确授权全部现有同步能力**：读取全部登记曲库、双向用户/学习数据同步、范围内音乐文件新增/更新/已确认删除、管理本设备发起的学习、按应用原有流程下载学习组件。不要求逐项选择；这不授予全盘访问或任意代码执行。

旧设备保留 token、generation、证书 pin 和长期凭证，无需重新配对。启动时将正常有效凭证记录的旧缺失/false 权限统一归一为全 true，记录 `permissionMigration=paired_all`；重复启动不再次修改权限或其他配置。不执行历史 expiresAt。未配对或已撤销记录不会凭空生成，复用 deviceId 仍需新一次有效配对并生成新 token，旧 token 不复活。

`capabilities` / MCP `lan_sync_status` 提供 `authorizationPolicy=paired_all` 以及六个全 true 的兼容诊断字段。细粒度 `authorize` 和 `invite.permissions` 已废弃，明确拒绝并说明使用 `revoke`，不接受后静默忽略。省略 permissions 即可邀请；credentialDays 仍只作忽略的旧兼容输入。首次组件下载仍经原有用户操作确认，不因升级或权限迁移自动开始下载/学习/文件写删。远程学习重启保持 interrupted；撤销立即关闭连接、取消设备计划/上传及归属学习，不取消本机独立任务。

Android 不再把旧权限 false 当作有效的只读工作模式或跳过领域同步。遇受限旧桌面提示升级；认证失效显示配对问题。系统 SAF/Android 存储权限仍属于本地目录访问条件，与配对授权不同。

旧 Android 的小请求继续使用 TLS 及逐请求鉴权；首帧最多 4 KiB，后续最多 64 KiB，旧端超过帧预算收到 `upgrade_required`；旧端学习动作同样经过桌面权限检查。新 Android 要求协议 4，连接旧桌面明确提示升级，**没有不安全的自动降级**。学习事件格式仍为协议 3，用户数据格式仍为 format 2，二者与传输版本 4 不冲突。推荐先更新桌面，再覆盖安装 Android。

## 可执行部署步骤

1. 安装/覆盖本轮 Windows 包，保持原数据目录和同一个 Windows 账户；不要删除 `lan-sync.json`、数据库或证书存储。Android 用原正式签名覆盖安装，不卸载清数据。校验交付 SHA-256。
2. 查看设置“同步”或本地 MCP `lan_sync_status`，核对服务状态、绑定、共享根、设备权限/长期凭证策略、失败计数及任务归属。先撤销不认识的设备；配对设备均具备全部同步能力。查看独立 firewall 状态，不能只凭 enabled 判断可连。
3. 确定稳定的**本机 IPv4 地址/网卡**和监听端口。先停止服务，再在高级配置保存；例如只绑定指定网卡 `192.168.1.20:39843`。如只经本机四层代理进入，可保留回环绑定。`0.0.0.0` 会扩大到所有 IPv4 网卡，需管理员明确选择。
4. 确定手机可达 IPv4 或仅有 A 记录的域名，以及外部 TCP 端口（可与内部端口不同）。本轮不原生监听 IPv6；不承诺 IPv6 地址/AAAA 优先解析可用。邀请 UI 的地址与可达端口用于该外部入口，不必使用检测到的 LAN IP。
5. Windows 防火墙由程序自动维护：只允许当前 EXE 路径、实际 TCP 监听端口和指定本地 IPv4（0.0.0.0 对应 Any），覆盖 Domain/Private/Public，非回环绑定的来源为 Any，因此兼容 LAN 与既有公网 TCP 转发。回环监听不创建入站放行。启用前先核验规则；失败不启动远程监听。只提权窄权限 helper，合法 UAC 需系统确认，不提升常态播放器。路由/公网映射仍由管理员单独配置；四层代理必须原样转发 TLS，不转发 MCP 或私钥。来源/IP 配额仍按实际代理 IP 聚合。
6. 确认要授权该设备全部同步能力时，才生成 5 分钟一次性邀请，通过当面扫码或可信私密渠道传给设备。不要公开二维码、放网页/日志/聊天群，不在截图中保留有效邀请。
7. 手机从独立外网实际连接，先同步一个合成/非敏感小目录，再核对 hash、分页、撤销与重连。检查 diagnostics 的活动连接/拒绝/认证失败/限流是否合理。公网真实端到端尚未验收时保持“待公网验证”，不能用本机测试宣布已经安全开放。
8. 故障/异常时先停服务或关闭已有公网转发，再撤销失陷设备、重新配对轮换 token；证书私钥疑似泄露时需要管理员在停服并备份配置后有计划更换证书，所有客户端重新获得可信 pin。不要为了恢复连接关闭 pin 校验。

本地 MCP 示例（仅示范，**本轮未对生产执行**）：

```json
{"action":"stop"}
{"action":"configure","bindAddress":"192.168.1.20","port":39843}
{"action":"start"}
{"action":"firewall_repair"}
{"action":"revoke","deviceId":"<要撤销的已配对设备ID>"}
```

以上均调用 `lan_sync_control`。`invite` 可传 `host`、`advertisedPort`、`credentialDays`（忽略的兼容输入，不传 permissions）；返回值含秘密，切勿记录。`revoke` 只需 action/deviceId。MCP 仍只在本机当前用户上下文可用。

## 自动防火墙生命周期与失败状态

规则稳定所有者为当前 Windows 用户 SID 与规范化数据目录的 SHA-256；固定 Group / Description 标记以及按程序路径、本地地址和端口计算的 Name 共同核验所有权，只查询这个精确组。旧规则只有同时满足所有权标记、名称摘要、JeekMusic.exe 路径、TCP/Inbound/Allow 结构才允许移除；不删除用户手工规则、系统弹窗规则或其他软件规则。

停服/正常退出清理自建规则；更换端口或升级程序路径时清理同一所有者旧规则并创建新精确规则；崩溃遗留在下次启动/修复时收敛。重复修复幂等。进程被强制结束时来不及执行清理，遗留规则仍只限定原 EXE/地址/端口。清理被拒绝时状态明确显示失败，不宣称已移除。

`firewall.state` 区分 pending/configuring/configured/not_required/repair_required/uac_cancelled/access_denied/policy_blocked/configuration_failed/check_failed；含实际程序、端口、localAddress、remoteAddress、profiles、组内自建规则数、活动精确规则数。`firewall_repair` 走同一服务，重试规则或此前失败的启动。监听失败单独在 problem 中报告。企业策略拒绝本地入站规则、PowerShell/NetSecurity 被禁用、UAC 拒绝均不会冒充成功；查询/规则操作有超时。只核验 Windows 规则，第三方防火墙及真实外网连通仍需另行验证，`reachability=not_verified` 不会变成可达保证。不关闭 Windows 防火墙、不改 NAT/路由/UPnP。

## 验证与交付限制

执行 `python desktop/tools/test-lan-security.py --exe <隔离构建EXE>`：工具创建全新私有数据、合成 WAV、回环临时端口，并确认实际 PID/EXE；不会读取/改动生产设置、真实音乐或生产端口。`--android emulator-...` 需先安装 debug 与 androidTest APK，仅在自有隔离模拟器执行生产 LanProtocol 的 TLS/pin/分页/数据测试；凭证通过应用私有文件临时传递并删除。`--ui` 仅为隔离真实客户端设备管理界面验收保留进程。单元测试为补充，UI 需实际窗口截图。

Windows 现有发布流程提供 NativeAOT ZIP，**原来并无 Authenticode 签名**，本机无代码签名证书；没有可信发布者证书就不能交付“已签名 Windows 包”，不能用 TLS 证书或临时自签名冒充。Android 继续用现有正式 keystore；须以 apksigner 比对旧正式 APK 签名后才称覆盖包。最终验收记录列出版本、修复 SHA、实际 GitHub Desktop/Mobile CI、包哈希、真实 UI/模拟器结果与未验项。

参考已核验的官方说明：[SslStream 异步握手与取消](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream.authenticateasserverasync)、[Android 签名与升级](https://developer.android.com/studio/publish/app-signing)。同步目录/磁盘元数据的底层阻塞仍依赖 Windows 文件系统驱动，取消是协作式的；独立计划许可使其不会占用普通读写许可，但不能承诺中断所有驱动或网络盘故障。未执行真实公共模型下载中的撤销/完整训练负载，仅用受控 worker token 验证归属取消，不能冒充真实模型任务验收。

软件预算不能替代部署层访问控制、系统补丁、监控、备份或独立审计。


凭证策略更新：已配对token长期有效（credentialPolicy=until_revoked），不执行旧expiresAt，不自动轮换或周期重配；旧token哈希不重置。credentialDays仅保留旧调用兼容且忽略。仍保留手动撤销/重配和降权立即终止会话/归属任务。邀请码仍为5分钟一次性；握手/空闲/6小时会话、计划/暂存资源期限不变。回归验证历史过期字段仍可认证。

## 双向文件合同1

新增fileProtocol=1，在传输4鉴权后协商，缺能力明确升级，不降级文件写。旧设备新增writeFiles/deleteFiles均为false；旧四个权限输入继续兼容，新两项可选且默认false。管理员须明确授予文件写/删除，writeSync不授文件系统写。文件基线、直目录映射、冲突规则与安全恢复见[同步合同](lan-sync.md)。

单文件8GiB，暂存16GiB，备份32GiB；scope10万基线，全局100万，128scope/每credential8scope，事务25万，SQLite最大1GiB。目标盘保留至少256MiB且约5%（最高5GiB）空间。每UTC日预约上传每设备64GiB/全局256GiB，失败预约不退款；分块最大256KiB，所有帧仍预鉴权/分级准入。文件全局工作1项，无等待队列；重工作15分钟deadline，可查询/取消，prepared资源保留用于恢复，不因暂存TTL丢弃已移走的原文件。

备份保留在原目录旁，未确认不清除。桌面设置或本地MCP lan_file_backups可分页查看；停止服务后按receipt核验恢复/清除单个备份，不递归搬删目录、不改变同步基线。Android设置有同一生产句柄服务的备份管理。达到备份/元数据预算时延期；管理员检查后明确清除，不擅自丢弃恢复副本。

文件hash工作另有每凭证每小时2TiB、全局4TiB读取预算，进程重启清零；上传日预约持久保存。Android公共共享存储的FUSE可能不实现advisory flock，遇ENOSYS/EOPNOTSUPP/EINVAL时仍执行句柄身份与完整hash复核、RENAME_NOREPLACE及保留原文件；真实锁占用仍延期。flock本身不是其他应用必须遵守的写锁，不能据此声称能阻止恶意本机应用的所有竞争。句柄/路径保护不可用时拒绝提交。公共FUSE目录不支持NOREPLACE时使用下述明确的条件复制合同，绝不对公共目标使用覆盖rename。依据：[Android共享存储权限](https://developer.android.com/training/data-storage/manage-all-files)、[Linux renameat2](https://man7.org/linux/man-pages/man2/rename.2.html)、[Windows句柄重命名](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info)。

Android公共共享目录实测不支持flock/NOREPLACE，不能用应用私有目录测试结果冒充指定SAF目录可用。其commitMode=conditional_copy明确采用：原文件移到本应用排他创建、私有日志固定目录身份的`.jeek-sync-<operation>.recovery/original`恢复槽；公共目标只能O_EXCL创建，按私有日志记录的inode复制/续传，重启仅续写与源完全相同的已有前缀，不截断公共文件。目标被其他文件抢占或已有前缀被修改时保留双方，进入延期/冲突。普通rename仅面向该内部新建恢复槽，绝不用于覆盖公共目标。这些内部目录只存恢复版本，不是音乐文件命名空间，所选目录仍直接保存音乐相对路径。

条件复制期间本应用扫描器依持久事务排除未完成路径；其他不协作的应用可能看到部分目标文件，因此不宣称公共FUSE上具备整文件原子可见性。拥有本机广泛文件权限的恶意应用可破坏共享存储内部恢复槽，属于额外本机信任风险；网络协议拒绝这些点号内部路径，不能由远端选择或写入。使用完整hash/身份复核、原版本保留和条件创建限制正常并发竞争，不能据此声称抵抗已失陷本机的所有竞争。
