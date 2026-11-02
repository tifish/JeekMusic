# 公网加固与双向同步最终交付：1.0.224

本文记录 1.0.224 当时的行为。当前配对全权限及自动 Windows 防火墙行为见 [1.0.229 正式交付](lan-paired-delivery-1.0.229.md)。

2026-11-03 核验。软件包源码为 `e9f9a22b648c11074ba1451cdf3b526d0a69c79f`；后续提交仅保存本文和验收证据，不改变软件，不重建或另发同一软件包。全部既有范围已实现，以下明确区分通过项与环境受阻项。

## 正式包与同一源码 CI

| 平台 | 最终 CI | 正式下载 | SHA-256 |
|---|---|---|---|
| Windows x64 NativeAOT | [Desktop：success](https://github.com/tifish/JeekMusic/actions/runs/41485753850) | [1.0.224 ZIP](https://github.com/tifish/JeekMusic/releases/download/desktop-v1.0.224/JeekMusic-desktop-1.0.224-win-x64.zip) | `c6b080145588bf4afd7d1e490712894f46ef5ef99ad621267da48916c77c7da8` |
| Android 正式签名 | [Mobile：success](https://github.com/tifish/JeekMusic/actions/runs/41485753897) | [1.0.224 APK](https://github.com/tifish/JeekMusic/releases/download/v1.0.224/JeekMusic-1.0.224.apk) | `90f833d1ae41cc3c97ac13db4578078e76d5d5c8a2ee9fcab37086217a5252b4` |

两个 CI 的 headSha、两个发布记录的 targetCommitish，以及实际标签 `desktop-v1.0.224` / `v1.0.224` 都是上述源码 SHA。CI checkout 日志也记录该 SHA，不采用旧提交的绿色结果。Desktop Core 117 通过，App 722 通过、2 跳过；Mobile 的 lintDebug、testDebugUnitTest、assembleRelease 成功。旧工具数断言失败已由最终源码修正，未禁测试或放宽功能断言。

包已下载到工作区固定目录 `desktop/artifacts/delivery-1.0.224/`，直接使用 CI 正式产物，未本地重建 AOT/APK。下载字节摘要与 GitHub 发布资产 digest 相符。Windows EXE FileVersion 为 `1.0.224.0`，ProductVersion 为 `1.0.224+e9f9a22b648c11074ba1451cdf3b526d0a69c79f`。APK 内包名 `com.tinyfish.jeekmusic`、versionCode 224、versionName `1.0.224`；来源链为同 SHA CI 的 assembleRelease、Prepare release 及该次上传日志，APK 本身没有独立内嵌源码 SHA，不能把版本号单独当源码证明。

Android apksigner 校验通过，证书 SHA-256 为 `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe`，与此前正式包一致。Windows Authenticode 状态仍为 **NotSigned**，不是已签名发布者包。

## 本次补完的验收

- 对最终源码构建开发版（未重建正式包），分别启动隔离开发客户端与下载的正式 AOT：实际 MCP 往返通过，40 个工具的目录、描述、Schema 完全一致；六项权限、备份分页、协议 4、checkpoint 及严格参数拒绝通过。未启动公网映射或向真实音乐写删。
- API35 隔离 AVD `JeekLanEmpty35 / emulator-5584` 从正式 221 保留数据覆盖安装到正式 224。读取生产 MCP 已恢复的脱敏历史 VOICE `decode_length` 状态，仅暂存到该 AVD 的本地状态缓存，实际打开标签学习、失败原因、下一步及高级诊断并截图。验收后恢复原缓存，无卸载、清数据、重训或用户真机操作。这证明正式 UI 展示，不冒充手机经真实公网接收失败的端到端测试。
- [失败原因与下一步截图](evidence/lan-public-1.0.224/android-learning-failure.png)；[展开诊断截图](evidence/lan-public-1.0.224/android-learning-diagnostics.png)。历史日志未记录的 exitCode / samples 保持 unknown；此前 480103 对 480000 的旧失败与裁尾验证见原有 decoder-trim-proof 证据，没有为了验收启动用户训练。
- 原生产客户端经实时 MCP 确认为 `1.0.215.0`。最终重启前重新捕获全部 577 项队列及顺序/manual 标志、当前索引/进度/暂停、音量与实际输出端点、筛选、列、来源、布局、配对及学习状态，执行新的 checkpoint；未导入历史队列。随后结束原 PID 并启动正式 AOT `1.0.224.0`（核验 PID 30088，实际版本目录下的 EXE），使用原默认 `%LOCALAPPDATA%\JeekMusic`。
- 重启后队列顺序、索引、进度、暂停、可读取的音量/端点、输出选择、筛选/列、持久窗口/导航/歌词布局、配对 token/证书/服务配置均保留。学习 events 151 条和 evaluation 0 条，重启前后规范化事件摘要一致。显式默认数据目录与省略目录的 MCP 均指向同一 PID；进程清点只有一个正确默认实例。日常 `output/` 也已更新为同源码开发版，生产实际运行的是上述正式 AOT。
- 最终生产 MCP 的 `explain_analysis` 显示 NORMAL、rules10、模型阈值 3.25、有效缓存复用且 pendingReanalysis=false；旧回退阈值固定 .60。历史 VOICE 原因恢复为 extract/decode 的 decode_length，带下一步和 historical=true。没有全库重训。

机器可读摘要见 [verification.json](evidence/lan-public-1.0.224/verification.json)。完整私有 checkpoint、两端 CI 日志、隔离 MCP 目录和脚本留在本机版本固定目录；含配对信息/歌曲路径的私有文件未提交。

## 未验收及运行环境限制

Windows 新 EXE 路径继承已有启用的同步服务配置，系统弹出防火墙网络授权，阻挡前台。MCP 状态和持久化核对均通过；实际截图显示的是系统授权弹窗，不能当最终客户端视觉通过。未点击 Allow、未改防火墙规则、未绕过系统授权；原有 Windows 六权限/备份页面真实截图证据仍保留，但不能代替本次最终包的前台视觉验收。安全桌面不向本会话 UIAutomation 暴露该弹窗，最终前台视觉状态明确为**受阻待验**。该系统弹窗的发布者文字不代表 Authenticode 已签名。

保留既有隔离 TLS 安全、双向三组矩阵、真实公共 FUSE 十项及 SAF 直接目标/冲突选边证据，不无理由重复整套耗时测试。公共 SAF 实测不代表 ARM、所有 OEM、SD 卡或真实公网全覆盖。公共 FUSE 的 conditional_copy 不保证其他应用看不到半成品；不得称整文件原子可见。未向真实曲库回写/删除，未启动全库训练，未开放公网，未做完整渗透审计或真实公网端到端验证。

## 使用与部署要点

- 已配对凭证长期有效，直到手动撤销或重新配对；一次性邀请码仍为 5 分钟。保留现有配对，无需周期重配。
- 旧目录映射须重新选择；旧文件不搬迁、不删除。新映射直接写入 SAF 所选目录，以音乐相对路径保存，不再加旧命名空间。
- 双向同步以持久已确认基线判定：单边变更按授权传播，同内容合并；双方修改、修改对删除、无基线异内容均保留双方并等待选边。删除另须 deleteFiles，writeSync 不等于文件写/删授权；新文件 writeFiles/deleteFiles 默认关闭。替换版本保留恢复副本，管理员按 receipt 核验后恢复/清除。
- 对公网部署前阅读 [部署步骤与边界](lan-public-deployment.md) 和 [同步合同](lan-sync.md)：协议为 TLS 内自定义 TCP，不是 HTTP；只登记愿共享的曲库，核对实际绑定/端口/六权限，由管理员单独配置最小来源范围的 TCP 转发。四层代理须原样转发 TLS，不暴露 MCP 或私钥。邀请码只经可信私密渠道传递。真实外网合成目录、hash、撤销/重连验收前保持待公网验证，不能宣称已安全对公网开放。
