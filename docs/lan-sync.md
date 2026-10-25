# 局域网音乐同步首版

PC 是文件主库；Android 选择根或子文件夹及本机目标。文件不反向上传，用户数据仍由既有 format 2 / SyncMerge 合并。首版手动连接，不包含自动发现、公网穿透、中继或云账号。

## 操作与目录契约

1. Windows「设置 → 同步 → 局域网音乐同步」启用服务，并明确勾选需要共享的**已登记曲库根**。未启用的根不能列目录或读取。稳定 rootId 保存在应用数据目录，绑定曲库记录的不可复用随机身份，由该记录解析当前位置；移除根会永久撤销其共享和计划，整数 folderId 复用不会继承授权或歌曲引用。旧候选的 folderId-only 共享需用户重新明确启用，不把绝对磁盘路径作为网络 API。
2. 填写 PC 的局域网 IPv4 地址/主机名，生成一次性邀请，私下填入 Android「设置 → 设备同步 → 与 PC 配对」。邀请只用一次，5 分钟过期；包含证书绑定信息，请勿截图、公开或记录。Windows 可撤销单个设备。
3. 手机打开根列表；同名根显示简短编号以区分，完整 ID、精确路径/URI 放在展开详情。选择整个根或任意子目录（包含子文件夹），可深入浏览、估算所选文件数量和大小。勾选父目录取代其子目录选择。
4. 通过系统 SAF 选择目标并持久授权，同时申请正常曲库读取音频权限。首版支持本地 ExternalStorageProvider（内部存储/SD 卡），不支持网盘 DocumentsProvider。
5. 实际位置固定为 `所选目标/JeekMusic-<完整rootId>/根下相对路径`。例如选择 Music 根的 Karajan、Celtic 后，保存为 `目标/JeekMusic-ID/Karajan/...`、`目标/JeekMusic-ID/Celtic/...`。不同根即使同名并选择同一目标也使用独立命名空间；拒绝互相嵌套的不同根目标。
6. 映射、选择、启用状态可保存和编辑。编辑目标默认保留原目标文件。停用/取消/移除映射不删除文件；「清理下载」需要明确确认，只删本任务仍匹配已保存 hash 和实际 mtime 的文件，冲突、播放及预加载文件保留。清理从不删除 PC 文件。

不得让 Syncthing 或其他引擎同时管理同一目标命名空间。保存映射前必须明确确认这个约束；程序不能完整检测 Syncthing，也不修改或关闭其配置。现有普通 Syncthing 曲库及 format 2/旧 backup 文件继续支持。新 `root:<id>/相对路径[#NN]` 引用只做精确身份匹配，不会按后缀匹配另一根或未下载的歌曲；旧文件能准确映射的引用继续合并，歧义项保留为不可用，不能猜测归属。

## 安全与传输

- 使用平台 .NET SslStream 和 Android SSLSocket，TLS 1.2/1.3。Android 通过邀请中的 SHA-256 指纹验证 PC 证书及有效期；授权值使用系统随机数，配对后换成每设备令牌。PC 仅保存令牌 hash，私钥保存在当前 Windows 用户证书存储；Android 凭证只保存在私有目录并排除换机迁移/云备份。
- 不提供 HTTP 或任意磁盘写入 API。每个请求重新校验设备授权；文件读取还必须属于该设备当前、限时的选择计划及已启用根。用户数据上传只能写该配对设备自己的私有 inbox，再交给正常合并服务。诊断不返回凭证。
- 默认 TCP 39843，服务默认关闭，不创建全局防火墙规则。连接失败时检查两端局域网、PC 地址、端口占用、授权及 Windows 防火墙。首版明确依赖手动邀请，不假称发现所有设备。
- 长连接复用平台 TLS；每个消息使用 4 字节大端长度和 UTF-8 JSON，最多 32,000,000 字节（与既有用户数据读取上限一致）。文件按最多 256 KiB 的连续范围读取，只有当前范围被 Base64 编码，不把完整音频放入内存。不实现通用块差分算法。
- 每次计划都重新计算**全部所选内容** SHA-256，不依赖 size/mtime 缓存。因此正常写标签或外部修改即使保持同 size/mtime，下一次「同步 / 内容核验 / 重试」也能看到内容版本变化。mtime 独立于内容 hash，仅时间变化无需重传音频。
- 目录树逐层浏览；计划只枚举选择子树并追加依赖，不先下载整库。清单分页 200 项；每个计划最多 100,000 个文件，过大时需要缩小选择。
- 拒绝绝对/驱动器/UNC/控制字符/保留设备名/不安全路径段；Windows 不跟随 reparse point，打开文件后验证句柄最终路径；拒绝大小写及 Unicode 归一化碰撞。Android 同样验证相对路径、目标大小写碰撞、SAF 根和本地目标链接边界。
- CUE FILE 可规范化到同一共享根内的其他目录；越根路径拒绝。清单记录依赖，音频及封面/歌词先安装，依赖失败不提交对应 CUE。CUE 首版要求能安全解析的 UTF-8/BOM 文本，无法解析的编码明确拒绝。手机持久保存明确 CUE → 音频绑定，定向及后续普通扫描复用 CueResolver，支持不同子目录的依赖，保持 CUE track key。

## 提交、冲突与时间

下载断点位于手机私有目录，以任务、相对路径和内容 hash 隔离；取消/掉线保留，重试重新取得清单并继续相同版本。错误 hash 的断点丢弃并提示重试。完成后独立验证完整 hash，再流式写入 SAF 临时副本、同步落盘并校验目标 hash。

替换前重新检查任务所有权、当前内容 hash、保存的实际 mtime、当前/预加载播放文件。目标端通过提交日志及旧副本执行重命名；失败保留旧版本并停止后续文件提交，单个持久日志未恢复前禁止新提交；启动及取消后会独立尝试本地恢复，外部冲突保留日志与全部副本，不截断覆盖既有目标。提交后再次验证内容并读取实际时间；空间不足、网络/权限/重命名失败均可见。没有根据源删除、离线或根不可用自动批量删除的逻辑。

Android 的 SAF 授权本身不足以设置任意 mtime。可选「保留文件修改时间」明确引导授予应用已有的所有文件访问权限；传输目标仍必须经过 SAF。未授予时内容可下载，时间错误保留并提示，授权后重试补正。使用平台文件时间 API并将请求值与读回值分别保存；精度提示不当作音频失败。API 35 / x86_64 实测该 API 读回微秒精度，100 ns 尾数差异在普通 UI 本地化汇总为微秒精度，原始请求/读回值在展开详情可查；真正无法写时间仍显示错误；不承诺 ARM/OEM/SD 卡有相同精度。

提交前持久登记待刷新路径；曲库服务刷新成功后才确认清除，取消、错误和重启均可本地收尾。有未结束提交的文件暂缓扫描，短暂缺席不视为删除。CUE 改依赖时仅替换该 CUE 的绑定，刷新新旧音频并集，不删除其他 CUE 的边。提交后仅通知涉及的 MediaStore 路径，调用正常 `LibraryScanner.refreshFiles` 定向强制读取（覆盖同 size/mtime 内容变化）。文件替换不改队列或播放服务，不直接写歌曲 SQLite 行；TrackFactory 使用现有稳定 key/ID。用户数据继续同步播放列表、偏移、手动标签、规则、导航及已支持的分析结果，不传设备本地设置。

## 隔离验证与候选限制

所有本功能测试使用 `desktop/artifacts/lan-sync/` 合成 WAV、CUE、LRC 和独占的 `JeekLanSync35` API 35 / x86_64 AVD，ADB 命令均指定 `emulator-5582` 并检查 AVD 名称。实际用户曲库、恢复备份、旧 AVD 和用户手机不作为源或目标。

- `dotnet test desktop/tests/JeekMusic.Core.Tests`：选择、内容/时间独立变化、CUE 越根及依赖、碰撞、Windows junction 和引用精确匹配。
- `dotnet test desktop/tests/JeekMusic.App.Tests`：长度边界、MCP 描述/Schema 及既有回归。
- `mobile/gradlew :app:testDebugUnitTest :app:assembleDebug`：Android 相同路径/引用边界及既有协议回归。
- `python desktop/tools/test-lan-sync.py --interactive`：真实 Windows 服务、未配对/错误邀请/重放/越权根/未授权文件/撤销拒绝；保留服务供真实 Android UI。`invite` 只向已聚焦的模拟器配对框输入，不输出或保存凭证。
- `mobile/tools/lan_ui.py` 操作真实 Android 界面和系统目录授权，保存不含凭证的截图；`verify_lan_files.py` 独立核验目标 hash/大小/时间和未选目录缺席，冲突用 `--expect-conflict`。

当前桌面 Computer Use 原生通道报 `native pipe ... os error 2`；Windows 真实窗口交互验收受阻，不能用 MCP/Headless 替代宣称通过。模拟器宿主 Qt 窗口报 `Failed to load opengl32sw`，因此运行无宿主窗口的完整 Android 系统，通过 ADB 操作和截图检查实际 App/系统选择器。宿主窗口本身未验收。模拟器旧蜂窝栈出现 TLS 记录损坏，隔离 AVD 使用 `-feature WiFiPacketStream,-Vulkan`，连接 AndroidWifi 并停用模拟蜂窝数据后完成真实 TLS 传输；该现象对应 [Android Emulator 官方问题](https://issuetracker.google.com/issues/150758736)，不通过关闭证书校验绕过。

ARM64/OEM、物理 SD 卡、真实目标满盘、未覆盖的进程崩溃提交点、公网场景均不能根据本轮结果宣称通过。候选供父任务独立核验，不 push、不发布；最终逐项结果另见验收记录。


## 审查回归工具

`desktop/tools/test-lan-root-reuse.py --exe <隔离候选exe>` 使用新建合成 A/B 根及真实生产 TLS 服务，验证根移除、SQLite ID 复用、旧授权/计划拒绝和重新显式共享。`lan_sync_status.roots[].revoked` 说明授权是否已永久失效，保留原名称，不展示复用 ID 对应的新根名称；现有工具输入兼容。

本轮签名 Release 原版运行及四项定向回归见 [lan-sync-review-fixes.md](lan-sync-review-fixes.md)。Windows 原生 UI 工具阻断仍保留，不重复失败通道。
