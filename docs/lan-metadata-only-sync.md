# 文件同步仅按大小和修改时间比较

本轮实现：扫描、发送/接收、暂存、条件提交、删除、备份恢复均不计算文件内容摘要。相同 size + 精确修改时间即相同版本；即使内容不同也不会检测出来。这是明确的同步策略，不是内容完整性承诺。TLS、设备认证、路径边界、文件身份和不覆盖并发修改的事务保护保留。文件类型/CUE解析仍按原安全及曲库要求执行，不作为内容相等判据。

## 协议与旧数据

文件协议提升到3，双方都须升级；旧协议明确要求升级，不回退到内容哈希。wire 字段为 stamp/expectedStamp，值 m2:size:modifiedNs。原 comparisonProtocol=2 基线、落地精度对应关系及数据库列保留，不重置同步基线。旧事务优先使用既有 sourceVersion、expectedVersion、size/modifiedNs 和备份身份迁移；缺少可靠元数据的旧事务保留并延期，不能重新读文件计算摘要。

MCP lan_sync_status.fileSync 的文件诊断使用 fileProtocol=3、contentHashing=false、hashPurpose=none，hashBytes 兼容计数为0。尚未刷新文件存储诊断的 idle 状态只返回协议、比较方式和计数等基础字段，可能省略 contentHashing/hashPurpose；不能把字段缺失当成已经返回 false。参数Schema和工具数量不变。

## 性能修改

- Android 扫描、远端清单、基线快照每批最多200行在一次SQLite事务内写入，异常整批回滚；网络等待及实际文件事务日志不纳入此批处理。
- Windows目标暂存及Android公共目标复制使用1MiB缓冲，每8MiB或2秒记录一次持久进度，最终提交前仍强制落盘。Android下载私有部分也采用同样的检查点阈值。
- 未保存的部分遇到强杀，不假定内容可信；公共目标保留并延期，私有下载部分可以重下。正常取消在身份及元数据仍匹配时保存部分进度，便于恢复。
- Windows上传块在返回offset前仍落盘并保存元数据；移除同值记录的重复SQLite写入。Android确认完成后清理私有进度文件，避免检查点文件累积。
- Android单次观察复用一次fstat；元数据校验不重复打开同一文件。
- 元数据verify和ack直接返回，免去后台任务轮询；其他后台操作以50ms开始，指数退避到500ms，保留取消和超时。

## 验证记录

Windows App 735通过、2项硬件条件跳过；Core118通过；Android JVM122通过。开发版/NativeAOT真实EXE的41个MCP工具契约一致，隔离TLS上传/删除/恢复及公网防护/配对回归通过，全部操作 hashBytes=0。Android API35 x86_64真实公共目录6项双向协议矩阵通过；本地事务/正常取消续传9项、SQLite批次4项通过，lint与debug构建通过。矩阵保留生产每设备每分钟4次计划限额，耗时498.993秒，不用这段总时长宣称吞吐提升。未验证实体手机/OEM及ARM存储性能。未对用户真实媒体做测试写入或冲突选边。

## 正式发布与客户端恢复（2026-11-05）

- [Windows 1.0.248](https://github.com/tifish/JeekMusic/releases/tag/desktop-v1.0.248)：源码与标签为 `9a299d02ec38bb2154d6e8758ed69d142d5b73bd`，[Desktop CI 41946319900](https://github.com/tifish/JeekMusic/actions/runs/41946319900) 全部成功。下载的正式 ZIP SHA256 为 `f42d1ece9add394d279fe7dea3a226ee5f3290c4baa1ebb848de1560ea2806ea`，与 GitHub asset digest 一致；解压后的真实客户端 MCP 报告 `1.0.248.0`、NativeAOT，41工具契约与已验证版本完全相同。
- [Android 1.0.247](https://github.com/tifish/JeekMusic/releases/tag/v1.0.247)：源码与标签为 `2bdaf4f73ec64bf308edb49a5b6c0112d329a452`，[Mobile CI 41945237012](https://github.com/tifish/JeekMusic/actions/runs/41945237012) 成功。正式 APK SHA256 为 `1c4a2a99537a47d748eabf0a9f3b37b4783a48df0a7e5140a5e73eecba99643d`，与 GitHub asset digest 一致；签名证书 SHA256 为 `28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe`。API35 模拟器保留数据升级并冷启动，拉取已安装 APK 的摘要与正式包相同。
- Windows 后续提交只修改隔离文件同步回归脚本：对明确的 busy 响应限时重试，并严格断言 conflict_already_chosen。首次 Desktop CI 41945236988 在测试读取缺失 conflict 字段处失败，未输出实际响应，不能确定其根因；修订后的 CI 通过。Windows248 与 Android247 的生产文件同步实现一致，双方均为文件协议3。

交付时最初的旧客户端已退出，无法取得其退出前的实时快照。随后发现09:22自动启动的245实例，以该实例的当前状态重新执行 MCP checkpoint 和数据库备份，再更换正式248。核对保留1585项队列及其顺序、手动标志、索引103、来源/锚点、暂停及位置0、音量/静音、输出选择、过滤/列布局/窗口设置、配对及凭证/证书、学习事件和1714行文件同步基线。此核对不追溯更早退出前的播放进度；没有导入历史 artifact 快照，也没有写系统音量。

旧245启动期间的瞬时 LAN 状态为 false/0；其持久化配置原为启用、端口39843。正式248按该持久化配置恢复，bind、授权及配对保持一致，当前精确程序防火墙规则为 configured，外部可达性未测。首次普通启动的248进程随后退出，原因未查明；MCP随后启动的248实例经连接关闭后继续存活、单实例及显示命令核对，留给用户测试，不能据此宣称完整稳定性验收。

当前正式客户端 MCP 实测 fileProtocol=3、hashBytes=0；idle 诊断未刷新 contentHashing/hashPurpose 字段。contentHashing=false/hashPurpose=none 由生产服务源码及已验证隔离实例的全流程诊断确认，没有为获取字段而对用户媒体执行同步计划。本轮无 UI 修改，不宣称新增 UI 验收。软件包验签摘要、TLS/凭证及路径身份摘要仍保留，不属于同步文件内容哈希。
