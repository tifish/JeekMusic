# 配对全权限与自动防火墙正式交付：1.0.229

2026-11-03 核验。软件源码为 `a2bf486ce430510344491c7c87d9d8f292533699`。后续交付记录提交仅保存本文和证据，不改变软件，不重建同版本包。

**配对即可拥有全部现有同步权限，旧设备无需重新配对。**正常旧凭证的缺失/false 权限已统一归一，token、generation、TLS 证书、监听设置及学习数据保留。Windows 已移除逐项权限选择，保留设备识别、邀请和撤销；Android 不再静默跳过数据同步或要求到电脑逐项授权。细粒度 `authorize` / `invite.permissions` 明确拒绝，提示使用撤销，六权限字段保留兼容读诊断。MCP、开发版与正式 AOT 共用业务与 Schema。

配对授权范围仍是全部已登记曲库及应用现有学习操作，不是全盘访问或任意代码执行。设备认证、TLS pin、撤销断开/归属任务取消、目录边界、条件提交、基线冲突、备份及预算保留。长期凭证、五分钟一次性邀请码和全部登记目录共享规则不变。迁移不启动原本关闭的服务，也不自动生成邀请、执行真实曲库写删或下载学习组件。

## 同一源码 CI 与正式包

| 平台 | 最终 CI | 正式下载 | SHA-256 |
|---|---|---|---|
| Windows x64 NativeAOT | [Desktop success](https://github.com/tifish/JeekMusic/actions/runs/41501162146) | [1.0.229 ZIP](https://github.com/tifish/JeekMusic/releases/download/desktop-v1.0.229/JeekMusic-desktop-1.0.229-win-x64.zip) | `d7b40a21e901660059467a6b8cd1cc35955e439645e5f560219f009ae8bd8202` |
| Android 正式签名 | [Mobile success](https://github.com/tifish/JeekMusic/actions/runs/41501164114) | [1.0.229 APK](https://github.com/tifish/JeekMusic/releases/download/v1.0.229/JeekMusic-1.0.229.apk) | `093d9c997b042429133af5dfa913b6e8b969230a0b46550ee045f4fdb68a8dd0` |

两个 CI 的 headSha、两个 Release 的 target_commitish、实际 `desktop-v1.0.229` / `v1.0.229` 标签均为上述源码 SHA。下载字节摘要与 GitHub 资产 digest 相符。Windows FileVersion `1.0.229.0`，ProductVersion `1.0.229+a2bf486ce430510344491c7c87d9d8f292533699`。APK 包名 `com.tinyfish.jeekmusic`、versionCode 229、versionName 1.0.229；APK 没有独立内嵌源码 SHA，来源由同 SHA 构建日志、标签和上传资产共同核验。

APK 签名验证通过，证书 SHA-256 `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe` 与旧正式包一致。隔离模拟器 `emulator-5584` 从正式 224 使用 `install -r` 覆盖到 229，首次安装时间保持不变，未卸载或清数据。未操作用户真机。

Windows Authenticode 仍为 **NotSigned**。正式交付及当前运行均直接采用 CI 下载的包；本机隔离 AOT 构建只用于定位/回归启动竞态，未充当正式发布包。

## 实际验证

- 最终 Desktop CI：Core 117 通过，App 731 通过、2 项真实音频端点条件跳过；正式 AOT 的既有安全/文件回归、新旧设备全能力及重启幂等回归通过。Mobile lint、单元测试和签名构建成功；本机 Android 单元测试 119 通过。
- 新配对与旧全 false 设备通过真实 TLS/MCP 完成 state/delta、学习管理/下载授权、合成文件写删及备份；学习夹具无标签，停在 insufficient_material，没有启动模型/依赖下载。撤销立即关闭会话、取消归属任务；未知身份、撤销旧 token、复用 deviceId 的旧 token 被拒绝。持久化重启和下一次启动幂等通过，非权限配置不因重复迁移变化。
- 实际 Android 三组双向矩阵通过，覆盖原有正常同步、条件提交/冲突和恢复；只使用隔离合成目录，未在真实曲库实验。
- 实际 Windows 精确规则创建、查询、幂等、端口变更、崩溃后升级路径替换、停服/回环清理通过。测试仅对一个指定本机 IPv4、隔离 EXE 和临时单 TCP 端口操作，临时规则已清理。只核验和清理自建组，不删除用户手工/系统规则。
- 最终开发版与下载 AOT 的 **40 项** MCP 目录、描述和 Schema 完全一致；工具数量从实际目录读取。全权限兼容诊断、废弃参数明确拒绝、防火墙状态/修复、严格参数拒绝和 checkpoint 往返通过。
- 最终正式 AOT 的真实设置页显示 [已配置防火墙结果](evidence/lan-paired-all-1.0.229/firewall-configured.png)、[配对全能力说明](evidence/lan-paired-all-1.0.229/paired-all.png)。[撤销前](evidence/lan-paired-all-1.0.229/device-before-revoke.png)只有身份与撤销，没有逐权限控件；实际点击后 [设备移除](evidence/lan-paired-all-1.0.229/device-revoked.png)，MCP 确认列表为空。无有效邀请码或真实曲库路径进入这些截图。

启动竞态亦已补齐：原来 MCP 先可用、领域合并服务稍后就绪，极快的 AOT 第一次 state 可误报 request_refused。旧正式 AOT 隔离复现后，新增 `userDataSyncReady` 诊断；就绪前 state 返回临时 busy，沿用有界重试。夹具显式等待就绪后仍验证真实数据交换。Android 网络/配对/升级失败也不会停留在“同步中”。

## 最新状态保全与当前运行

重启前通过实时 MCP 重新读取全部队列及顺序/manual/来源、当前索引/进度/暂停、可读取音量与实际端点、输出选择、筛选/列、窗口/导航/歌词布局、配对及学习状态，执行 checkpoint，并保存当时配置及 SQLite 在线备份。实时队列当时仍为 577 项；**没有导入或恢复历史 577 项快照**。

随后结束原 224 PID，启动下载的正式 229，沿用默认数据目录。实际运行 PID `16240`，版本 `1.0.229.0`，NativeAOT；进程清点只有一个 JeekMusic 客户端。日常 `output/` 也已更新为同源码开发版。显式默认目录与省略目录的 MCP 指向同一 PID。

队列身份/顺序/manual/来源、索引、进度、暂停、音量、实际输出端点、筛选、列、完整持久布局配置、配对 ID/tokenHash/generation/证书、启用状态/绑定/端口及学习事件摘要重启前后全部相符。旧权限全部归一为 true。当前正式客户端通过真实 pinned TLS 拒绝未知身份；未生成生产邀请、未测试真实音乐写删或学习下载。

## 生产防火墙实际结果与未验项

**精确规则已实际配置成功**：只匹配当前交付路径下的 `JeekMusic.exe`、入站 TCP **39843**、原有绑定 **0.0.0.0**（LocalAddress Any），RemoteAddress Any，Domain/Private/Public。自建规则与活动精确规则均为 1；针对当前正式 EXE 的活动规则查询也只有这条允许规则。来源 Any 与原有全网卡绑定及计划公网 TCP 转发语义一致，没有变更绑定、端口、路由、NAT、UPnP 或系统防火墙开关。

当前执行环境本身已有管理员令牌且 `EnableLUA=0`，未修改该设置，播放器普通启动，没有请求整个播放器提权；产品需要权限时只运行固定规则 helper。另以受限用户令牌启动隔离开发客户端，通过实际 UI 验证可查询/修复关闭或回环状态。真实 UAC 确认/拒绝没有本机环境覆盖；拒绝/权限失败由注入后端测试覆盖。企业策略和第三方防火墙环境未实测。Windows 规则成功不等于真实公网已可达，`reachability=not_verified` 保持准确。

**生产前台仍有系统网络访问提示遮挡。**全屏截图确认 Windows Security 提示还在；它未向本会话 UIAutomation 暴露控件，不能确认提示所指的具体程序路径，也未代点其通用 Allow。已成功配置规则、MCP/TLS 往返和隔离正式 UI 验收有独立证据，但不能把当前生产前台当作“无系统提示通过”。这不是未获防火墙授权，也不是规则操作未执行。包含真实歌曲名称的生产截图只保留本机，未提交。

机器可读证据见 [verification.json](evidence/lan-paired-all-1.0.229/verification.json)。完整私有 checkpoint/备份、两端 CI 日志、正式包、目录对照及系统提示截图位于本机 `desktop/artifacts/delivery-1.0.229/`。部署与 API 合同见 [公网部署说明](lan-public-deployment.md)、[同步合同](lan-sync.md)、[MCP 文档](../desktop/docs/mcp.md)。
