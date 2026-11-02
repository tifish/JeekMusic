# Android 自更新：免确认与可验证结果

## 定位与边界

JeekMusic 原实现已请求 `USER_ACTION_NOT_REQUIRED` 并声明 `UPDATE_PACKAGES_WITHOUT_USER_ACTION`，但没有预检 `canRequestPackageInstalls()`，没有授权返回续装，也没有持久保存安装结果；`PENDING_USER_ACTION` 直接从广播启动系统界面。JeekAIChat 有未知来源授权引导和返回续装，两者请求免确认的参数相同。它采用 activity 回调和后台启动 options，这影响结果界面是否能显示，不改变系统的免确认资格。

[官方 SessionParams 文档](https://developer.android.com/reference/android/content/pm/PackageInstaller.SessionParams#setRequireUserAction(int))把自更新本身列为资格，并不要求上次由自己安装。需要请求免确认、声明相应权限、目标 APK 达到当前系统 target 门槛，并满足安装授权和系统策略。Music target37/min31；只读对照仓库 AIChat target36/min26。Music 本地/CI 使用正式 keystore；AIChat 的 release 使用本机 debug key，各自更新均须匹配各自已安装的签名，两个应用不必相同。

[AOSP PackageInstallerSession](https://android.googlesource.com/platform/frameworks/base/+/01d329349864f78f7f1b4c774341514e8719eb9c/services/core/java/com/android/server/pm/PackageInstallerSession.java)在检查 self-update 资格前处理强制提示、update owner 约束、未知来源/设备策略；后面还检查目标 API 和静默更新频率。[Android15 SilentUpdatePolicy](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android15-release/services/core/java/com/android/server/pm/SilentUpdatePolicy.java)默认频率间隔为30秒（系统/OEM可不同）。[canRequestPackageInstalls](https://developer.android.com/reference/android/content/pm/PackageManager#canRequestPackageInstalls())检查当前应用作为安装来源的授权。

已确认的应用缺口是没有先取得来源授权再提交新的自更新会话。它能解释一类系统界面，但还不能单凭仓库差异确认用户手机那次提示的唯一原因。没有该手机会话的实际权限/来源/状态证据，就不能归因厂商或承诺“确认一次后永不弹”。

## 实现

- 下载后检查大小、发布摘要（提供时）、实际 APK 包名、准确且递增的 versionCode、当前签名集合；PackageManager 在提交时继续负责最终 APK/签名验证。未知签名与计划外签名轮换拒绝，不能通过更换安装方式绕过。
- 未授权时不创建安装会话；前台显示说明并打开该应用的来源授权设置。返回后复用已验证下载包，重新验证并提交全新会话；关闭授权引导可稍后重试。
- 全量安装会话设置实际包名、size、下载来源和 `USER_ACTION_NOT_REQUIRED`。不申请 update ownership，不伪装其他安装来源，不改 target，不使用 root/Shizuku/无障碍点击。
- 结果回调是显式 mutable PendingIntent，只指向非导出的 receiver；绑定持久会话 ID 与随机回调身份，并验证返回会话 ID。随机身份不进入日志/诊断。不会把导出 MainActivity 当任意状态入口。
- 系统要求确认时保留 pending 状态，前台 activity 打开系统 Intent；应用在后台时不擅自启动界面。取消和失败可重试。进程重建后若系统确认 Intent 已不可用，可取消旧会话后重新创建；不会假报成功。
- noBackup 私有原子日志跨下载、授权、提交、pending、失败与包替换恢复。恢复未提交会话时放弃它，已提交的会话继续等待；会话消失不等于成功。成功必须读取实际已安装版本确认，不保证 Android 自动重启应用。
- 安装覆盖原包，不卸载、不清数据、不改变播放/配对/标签/学习数据。APK仅在验证安装完成后清除。

## 诊断与设备桥

设置 → 关于与更新 → 更新诊断，可复制 JSON。开发/正式版使用同一服务和字段：schema、Android/API/target、当前包/版本、安装来源与 API34+ owner、API33+ source、来源授权、免确认权限、请求 requireUserAction、目标版本、流程阶段、会话 ID、状态码、confirmationSeen、稳定错误码、脱敏平台 INSTALL_FAILED 标识与最近24次阶段事件。不含 URL、凭证、回调身份、确认 Intent、路径、歌曲或任意系统错误全文。

Android 当前没有独立 MCP 控制端口。本任务提供签名 instrumentation 桥 `SelfUpdateDeviceTest#bridge`，直接调用目标应用 UID 内的生产 `UpdateInstaller` 单例；生产不新增导出控制组件。Windows MCP 不受 Android 安装服务变更影响，无需造跨设备权限工具。

桥参数 `updateAction=diagnostics|install|cancel|recover`，install 接受 `updateApk` 本地测试 APK，复制进应用私有缓存后走同一验证和 PackageInstaller 流程。输出 `updateDiagnostics`。`validatesArchiveAndRejectsForgedResults` 验证真实归档、大小/摘要/版本拒绝、伪造结果拒绝、receiver 非导出和诊断脱敏。测试 APK 需要和被测正式版同签名；仅测试包提供该桥。

同签名连续版本用 `mobile/tools/self-update-fixture.init.gradle` 设置 `android.testBuildType=release` 和 `defaultConfig.versionCode/versionName`，不改共享版本文件。示例：`gradlew --init-script tools/self-update-fixture.init.gradle -PupdateFixtureVersion=220 :app:assembleRelease :app:assembleReleaseAndroidTest`。测试fixture关闭R8/资源裁剪以保留instrumentation共用依赖；正式包仍使用正常release配置。安装初始外部来源版本时可 `adb install -r`；后面必须由桥从应用自身调用真实安装服务。不能将 adb 覆盖成功计为免确认更新。

判定证据必须同时记录：提交前后版本和来源、请求/结果诊断、是否出现系统安装确认界面及截图。出现 pending 或手工点过系统安装按钮的轮次不可记为免确认成功。必须包含外部安装→自更新、自更新→再更新，以及授权、确认取消/失败、进程恢复场景。OEM真机未测时单独列明。

## 本轮实测

2026-11-02，在独占 `emulator-5584`（Android15/API35，x86_64）执行，未操作连接的实体手机。所有fixture都为正式包名 `com.tinyfish.jeekmusic`、target37、正式证书 SHA-256 `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe`。初始外部覆盖安装217保留原应用数据；之后的218/219/220均由目标应用UID中的生产服务提交PackageInstaller，没有用adb覆盖来充当自更新。

| 场景 | 实际结果 |
|---|---|
| 外部217，来源未授权 | canRequestPackageInstalls=false；停在permission_required、sessionId=-1，没有系统安装确认。强停进程后提示恢复。 |
| 真实设置界面授权返回，217→218 | 复用下载APK，新会话1107713339，requireUserAction=2；实际版本218、installer变为自身、confirmationSeen=false，无系统安装按钮点击。 |
| 系统确认回退 | 为负向测试将该模拟器静默限流收紧至300秒，应用仍请求NOT_REQUIRED；会话1016175647实际返回pending(-1)，系统出现Cancel/Update确认。**此轮不计为免确认成功**。 |
| 点击系统Cancel | 实际status=3、INSTALL_FAILED_ABORTED、error=cancelled，版本仍218；重启读取诊断仍保留该结果。 |
| 恢复默认30秒策略后218→219 | 从应用自身重新提交会话820602164，实际版本219、confirmationSeen=false、无系统安装按钮点击。 |
| 219→220，最终诊断修正 | 同一生产安装路径再次完成；实际版本220、confirmationSeen=false、当前error=null；自定义测试数据标记仍保留。 |
| 归档和安全边界 | 2项instrumentation通过：真实APK校验，大小/摘要/版本/错误包名/不同签名拒绝，伪造结果拒绝与非导出receiver；冷进程session消失不当成功，合法终态回调仍保留取消结果。 |
| UI与构建 | 实际客户端检查前置授权提示、真实系统来源设置、系统确认/取消和关于页诊断；串行release构建、lintDebug通过，5项TagClassifier+4项WorkoutEvidence+2项DesktopLearningResults单测通过。 |

正向测试没有设置无限静默更新、关闭校验或降低系统保护；负向限流测试后已恢复默认30秒。R8正常release构建通过；直接对裁剪release执行instrumentation遇测试运行器缺androidx.tracing.Trace，因此设备验证明确采用保留依赖的同签名fixture，并非完整正式交付包。最终正式包由集成线程在所有功能提交后正常重建。

证据保存在工作区 `output/android-update/`（不提交大文件）：`first-success/diagnostics.json`、`cancelled/diagnostics.json`、`second-success/diagnostics.json`、`final-success/diagnostics.json`、`security-and-recovery-tests.txt`、`debug-checks.log` 以及 `permission-restored/screen.png`、`confirmation-required/screen.png`、`diagnostics-dialog/screen.png`、最终220的 `final-diagnostics-ui/dialog.png` 与 `final-diagnostics-ui/copied.png`（实际点击复制后对话框关闭，关于页正常；未读取系统剪贴板）。`apk-manifest.json`记录fixture文件大小、SHA-256和最终设备版本。初次主机轮询在包替换期间遇到临时dumpsys服务失败，未把该脚本中断当作失败或成功证据；最终以重启后的应用私有会话记录、实际安装版本/来源和未点击确认的操作证据共同判定。

未验收：用户手机/OEM具体系统、管理策略、厂商额外确认、API31/33/34/36/37设备矩阵；不能承诺所有厂商静默，也不能以模拟器证明用户此前那次提示的唯一原因。若需定位该手机本次仍出现的提示，只需提供“关于与更新 → 更新诊断”的一份复制结果，不需分开索要型号、来源或截图。

## 发布身份修复

核对实际CI时发现另一个可复现的问题：构建 `ac608848`（218）的旧任务发布时，main已前进到 `6b506450`（219）；`v1.0.218` 被创建在新HEAD上，资产仍名为218。本次219任务因此看到“当前HEAD已打标签”，误判无应用变化，跳过正式签名与发布。旧任务最终显示cancelled也不能撤回已经发出的发布API请求。

`softprops/action-gh-release` 的 [target_commitish文档](https://github.com/softprops/action-gh-release#inputs)明确说明默认使用仓库默认分支。Mobile工作流现在显式绑定 `${{ github.sha }}`，并把自身workflow文件加入应用变更检测：修复发布流程也必须重建APK。已有错误标签保持原样，不移动已发布版本；以后交付必须分别核对实际run的headSha、远端tag解析SHA、APK版本/签名/摘要，不能只看版本标签或CI绿灯。该问题是发布身份错误，与系统安装确认资格分别处理。


## 正式发布包最终验收（221）

Mobile [run 41455431467](https://github.com/tifish/JeekMusic/actions/runs/41455431467) 对 `710f46f9e334d8db446eaa13fc42782e598455d0` 的lint、完整单元测试、正常R8/资源裁剪release和发布均实际成功。远端 `v1.0.221` 解析到同一SHA，release的target_commitish也为该SHA；APK manifest为com.tinyfish.jeekmusic / 221 / 1.0.221 / target37，正式证书与原安装一致。

随后使用已有220的instrumentation桥，从App自身提交真实PackageInstaller安装这份公开发布APK，完成 **220→221**，没有adb覆盖安装，也没有系统安装按钮点击。主机观察记录中没有系统安装界面；更新后拉取的实际base.apk与GitHub资产SHA-256逐字一致：`20abec49bc692ee1b9ef14890906ed552416a9dedacaa538a932e865351aa696`。该轮补足了正常裁剪正式发布包的实测，先前同签名fixture证据仍单独保留。

APK：`output/android-update/release-221/JeekMusic-1.0.221.apk`；来源校验：`release-221/provenance.json`；真实更新记录：`release-221/device/result.json`、`observations.json`、`request-instrumentation.txt`。第一次启动截图捕获偏早仍为launcher，未当作UI通过；补验明确等待真实客户端层级出现后截图，已目视验证：`release-221/client-ui-verified/screen.png` / `screen.xml`。最后设备为正常正式221，并已交回集成开发；实体手机/OEM仍未验收。
