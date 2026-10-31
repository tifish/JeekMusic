# 无音乐文件夹、默认共享与映射设置验收

2026-10-31，基线 `3cec9c4`。最初生产 MCP 核对为服务开启、端口 39843、已有一台配对设备、两个登记目录可用但 `roots=[]`：这是旧版尚未登记共享授权的配置状态，不是二维码或连接失败，也没有核验用户手机的版本。用户随后明确要求“默认共享全部已登记曲库”和“去掉手机 Syncthing 人工声明”，以下最终契约覆盖早先逐根勾选的候选流程。

## 最终行为

- 全局 LAN 服务开启时自动共享所有登记音乐目录，后续新增也自动纳入；不扩到父目录、未登记邻近目录或任意磁盘路径。原本关闭的服务保持关闭，添加目录、迁移或生成邀请均不会偷偷开启它。
- Windows 使用只读目录清单、数量、自动共享说明和零目录时前往曲库的入口。暂时离线的登记目录保留原身份和 rootId，明确标记离线，不推断删除。
- 有效 folderIdentity 的旧禁用记录自动启用并保留 rootId；缺少身份的旧记录、已撤销记录永不重绑定或复活。移除记录会撤销旧 rootId 和计划，整数 folderId 被复用时新目录使用新的 rootId。
- 旧 MCP share 是已开启服务下的弃用查询别名；unshare 总是明确报错，提示 stop 全局关闭，不返回虚假的成功。新增 sharingMode=library、libraryFolderCount、sharedFolderCount，开发和 NativeAOT 完全一致。
- Android 成功返回空列表时引导到电脑“设置 → 曲库”添加音乐文件夹，提供原弹窗刷新。服务/网络/鉴权错误不当作空列表，保留任务和下载。配对成功后的目录请求失败不会误报成配对失败。
- 手机保存映射不再有 Syncthing 人工声明项或 confirmedExclusive 业务参数；没有隐藏填写 true、确认记录或替代弹窗。仍执行 SAF、实际映射目标重叠、文件所有权、hash/mtime 冲突、播放文件避让及事务检查。不检测、关闭或修改其他同步软件。

## 验证及本地证据

证据位于未跟踪的 `desktop/artifacts/lan-empty/`。合成音频、设备配置和凭证不提交；邀请只直接送入隔离模拟器输入框并私下读回校验，没有输出或截取有效邀请/二维码。

| 项目 | 实际结果 |
|---|---|
| Windows 真实客户端 | `all-library-windows-empty.png` 显示零登记目录、自动共享引导；通过正常业务添加隔离目录后，无逐根操作即显示数量 1 和只读条目，只有全局服务复选框；`all-library-windows-auto-root.png` |
| Android 默认共享 UI | 独占 `emulator-5584 / JeekLanEmpty35`、API 35/x86_64，实际安装最新 Debug（包含并行任务的设置分类布局），由 settings_group_devices 进入 LAN。空曲库配对后，PC 添加合成目录，原配对直接刷新出现；`all-library-android-empty.png`、`all-library-android-auto-root.png` |
| 无人工声明保存映射 | 实际选择 Entire shared folder 和系统 SAF 下 Documents/JeekLanNoConsentTest，只有正常系统权限授权，没有 Syncthing 声明，Save 可直接保存。业务记录读回有 1 个映射且无任何 exclusive/confirm 字段；`no-consent-save-enabled.png`、`no-consent-mapping-saved.png`、`no-consent-mapping-proof.json`。未点击开始同步、未下载媒体 |
| 实际路径重叠保护 | 新 JVM 回归直接调用 saveMapping 使用的同一生产 LanMappingTargets 校验：同父目录独立命名空间可用，嵌套/反向包含/大小写变体重叠均拒绝。路径冲突没有用新的声明开关绕过；此项不是设备 UI 冲突测试 |
| 真实 NativeAOT TLS | `all-library-native-final.log`：空曲库配对；目录新增自动可见；未登记邻近目录/越根拒绝；暂时离线保留原身份且不可读，恢复后原 rootId 可用；unshare 明确失败；全局 stop 拒绝连接/邀请且不会重新自启，start 后原凭证及命名空间保持；旧 folderId-only 记录撤销 |
| ID 复用与旧计划 | `all-library-root-reuse.log`：实际 SQLite ID 复用，旧授权和计划不能指向新目录，新目录自动共享但使用新 rootId；旧 rootId 永久拒绝 |
| 迁移与开关单测 | LanRootPolicyTests 覆盖关闭时不自动纳入、开启时全根/新增/离线纳入、幂等稳定 rootId、旧有效禁用记录迁移、无身份/已撤销记录不复活、缺少中间空快照时的 ID 复用和服务关闭期间移除撤销 |
| 网络/撤销错误 | 相同 Android 刷新逻辑在本轮较早隔离 UI 回归中验证：网络错误、恢复重试及设备撤销分别显示错误，不假装空列表，配置文件摘要不变；`android-network-error-en.png`、`android-network-recovered-en.png`、`android-revoked-en.png` |
| 自动检查 | 最终 Windows App 636 通过、2 个环境相关音频端点检查跳过；Core 106 通过；Android JVM 111 通过（含新增 3 项映射目标测试）；Debug 构建成功，最终 Release/R8/lintVital 构建成功 |
| 构建和 MCP | 最终 Release/win-x64 NativeAOT publish 成功，已有 DataGrid IL2104/IL3053 警告保留；28 个完整工具、描述和 Schema 与日常版本核对一致 |
| 生产目录与重启 | 生产已核对共享仅为 D:\Mobile\Song\散曲 和 D:\Mobile\Music，自动共享模式、数量 2、原一台设备均保持。最终保全重启 PID 29832 → 39984，761 项即时队列、当时歌曲/位置 160.868 秒、暂停、音量/静音、随机/循环、筛选、队列来源、手动标签/审计和配对配置均核验一致；`policy-restart.log` 及私有前后状态 |

## 限制与交接

未连接实体 Android 手机，未操作用户真机、未宣称用户手机版本已升级或已刷新；ARM/OEM、物理 SD 卡未验收。最终新规则的设备验收使用 Debug；最新 Release 包已构建但未在本轮最后补验中安装，不能拿较早规则的 Release 截图冒充最终 Release 运行验证。

最初在原 5582 的新用户模式遇到 System UI 无响应，已切回用户 0 并删除仅本任务创建的用户；原用户数据保留。最终全部补验在新建专用 5584 完成。交接时没有运行中的 ADB、instrumentation 或 Gradle 命令，保留安装包、LAN 配对、1 个未启动的测试映射和空目标目录，不卸载或清数据。隔离 Windows 服务保持运行以保留测试连接；随后由设置分类任务接管设备，本任务不再操作设备。

并行设置分类任务的外层页面、分类文案、文档和工具不会纳入 LAN 提交。
