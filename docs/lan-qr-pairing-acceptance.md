# 扫码与手动配对候选验收（2026-10-26）

基于设置视觉提交 `4698ea21c78990175683399f0c0b25fc82f88aac`。本任务仅增加两种配对入口，不改变根授权、下载映射、文件事务和 SyncMerge。待父定向核验；未 push 或发布。

## 实现范围

- Windows 设置用 Net.Codecrete.QrCodeGenerator 3.2.1 生成内存 PNG，同时保留文本邀请。可选择多网卡地址或手动输入；优先显示有 IPv4 网关的接口。手机二维码拒绝回环、未指定、广播、组播及不适用的地址。刷新替换旧邀请，消费/停服/五分钟到期后清除界面码和文本。
- Android 保留独立「扫码配对」「手动输入配对」设置行，沿用 SettingsComponents。CameraX 1.6.2 + ML Kit 17.3.0 随包 QR 模型，正常运行时相机权限；可选硬件不影响手动配对。扫码后释放相机，核对地址后仅一次确认，成功显示 TLS 验证的 PC 名称/地址。
- 两条路径共用 LanInvitation 严格解析和原 pair 服务；不打开 URL，不放宽 TLS pin。URI 输入键盘关闭自动纠正。错误不回显凭证，中文/英文覆盖格式、失效/重放、权限、取消、网络、证书和映射保护。
- MCP 仍为 24 工具。原 invite 可选 `includeQr:true` 返回敏感内存 `qrPngBase64`，与设置使用同一编码器，并异步验证手机地址；默认文本请求保留旧 loopback 测试兼容。状态仅增加无凭证的 `invitationActive`。描述/Schema/文档及测试同步维护，两种构建完全一致。

## 构建与回归

- Windows 现有 App 测试：545 通过、1 个原有真实端点条件跳过；新增后 LAN 定向测试 13 通过，覆盖不可用手机地址、内存 PNG、严格生产 Schema。
- Android JVM：89 通过；完整 `lintDebug` 和签名 `assembleRelease` / R8 通过。正式签名 SHA-256 为 `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe`，打包 ARM64 与 x86_64。
- NativeAOT 成功。真实发布 EXE 的 QR 通过独立 zxing-cpp 解码逐字核对；真实 TLS 服务拒绝无效手机地址、单次邀请重放及实际等待超过 301 秒的邀请过期。开发/NativeAOT 24 工具目录完全相同。
- Release 实测发现 ML Kit Manifest 反射注册器构造函数被 R8 删除。设备日志为 `BarcodeRegistrar.<init>` / `VisionCommonRegistrar.<init>` 的 `NoSuchMethodException`；补针对组件注册器的无参构造函数 keep 规则后重新安装验收通过。没有关闭 R8 或泛化保留全部库。

## 实际相机与 UI

设备仅 `JeekLanSync35` / `emulator-5582` / API35 / x86_64，正式包 `com.tinyfish.jeekmusic`，原数据与 debug 包保留。隔离 PC NativeAOT 使用 `desktop/artifacts/lan-sync/windows-data`，源仅 `desktop/artifacts/lan-sync/source/Music` 和第二个合成 Music 根。

通过官方 `-camera-back imagefile:<private PNG>` 输入摄像头，真实生产路径为 **运行时权限 → CameraX ImageAnalysis → ML Kit QR → 严格解析 → 地址确认 → 原 pair → 证书绑定 TLS**。不是把字符串注入表单代替扫描。该模拟器图像摄像头需要 640×480 白底、二维码 x=24 的裁切校准；关闭并重新打开摄像头即可读取更换后的图像，无须反复重启。临时文件 ACL 限当前账户，凭证不打印；截图只在码已消失或已替换失效后保存，结束删除临时目录。

实际完成：

- 相机正常授权、扫码成功、持续重复帧只产生一次确认、同一已消费码再次扫码被拒绝。
- 普通 HTTPS 测试码扫描后显示无效邀请，未打开浏览器；权限拒绝及永久拒绝的手动备用入口、返回/取消与再次打开。
- 停隔离服务后扫码连接显示网络错误；另一个隔离证书服务占用同一测试端口后，原 pin 的扫码连接显示证书验证失败。原映射及已下载文件保留。
- 正式 APK 手动输入完整邀请，输入逐字核对一致，经过同一地址确认完成配对。
- 在线根、Karajan 子目录、选择/估算以及系统目录选择器和正常授权实际点击，取消编辑保留旧映射。
- 配对后已有 Review 映射同步完成。7 个 CUE/音频/歌词文件独立 SHA-256 全匹配，mtime 差小于 1000ns，源未被写入；正常歌曲搜索显示 `Finals One` / `Finals Two` 两条 CUE 曲目。

证据位于 `desktop/artifacts/qr-pairing/runtime/`：`01-camera-confirm-zh.png`、`02-paired-roots-zh.png`、`03-online-subfolders-zh.png`、`04-system-target.png`、`05-scan-cancelled-zh.png`、`06-replay-rejected-zh.png`、`07-camera-denied-zh.png`、`08-camera-denied-en.png`、`09-invalid-qr-en.png`、`10-other-pc-mapping-guard-en.png`、`11-network-error-en.png`、`12-certificate-error-en.png`、`13-manual-confirm-en.png`、`14-manual-paired-roots-en.png`、`15-final-camera-confirm-en.png`、`16-paired-options-en.png`、`17-final-sync-en.png`、`18-paired-options-zh.png`、`19-library-cue-visible-zh.png`；文件核验为 `files-after-smoke.json`，其他脱敏结果为 `build-manifest.json` / `catalog-parity.json` / `replay.json` / `expired.json`。最终 SHA/版本及日常重启保护读回由同目录最终 manifest 记录，避免文档提交再涨版本。

## 限制与边界

Windows 按 computer-use 技能入口检查一次，仍为 `Computer Use native pipe is unavailable: failed to connect native pipe: The system cannot find the file specified. (os error 2)`。PC 真实 UI 截图未验收；编码、NativeAOT、邀请业务和 Android 实机 UI 验证不能替代该项。

不宣称 ARM64/OEM、真实手机相机、物理 SD 卡或满盘通过；本轮不重跑旧同步故障矩阵。未操作真实音乐根、Syncthing 或恢复备份。用户数据/映射兼容；日常客户端仅保护即时队列/来源/位置/播放意图/settings 后重启候选，不恢复旧数据库。隔离服务结束关闭，释放 39843。
