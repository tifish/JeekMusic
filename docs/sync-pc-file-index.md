# PC 曲库扫描与同步共享文件信息

PC 原有清单缓存会被任意文件事件整份清空，曲库 2 秒合并后的扫描通知还会再次清空。现在共享清单由曲库服务持有，日常扫描和 LAN 同步使用同一份索引。

- 无变化时直接使用完整清单，不重列目录、不逐文件重新读大小和 mtime。
- 普通歌曲、歌词、封面文件的新增、修改、删除及改名只更新相应路径。日常曲库扫描已经读取的精确 FileInfo 大小/mtime 可共享，无需再查一遍；没有可靠共享观察时才重新观察变化文件。
- 新文件检查所在目录的全部兄弟名称，保留与完整扫描相同的 Unicode/大小写碰撞和路径规则；不读取兄弟文件内容，也不遍历整棵树。
- 目录结构/链接/仅大小写改名、短名称歧义、未知布局、CUE 及相关依赖目录变化，保守回退完整枚举。CUE 的目录外音频、歌词、封面发现规则保持不变。
- 首次、进程重启、根或授权变化、监听失败/溢出，以及原完整清单达到 15 分钟期限时重新枚举。增量更新不延长该期限，曲库延迟通知不再再次清空已更新清单。

共享观察和刷新均绑定进程内版本；枚举期间出现新事件、监听重建或授权变化时，旧结果不能发布为新的可复用清单。刷新失败不返回半份列表，也不把权限/存储不可用当作删除。增量刷新避开本应用标签提交的临时名字空档；上传、下载、删除和确认前的实时版本/句柄检查保持不变。原 mtime、大小和最后软件编辑时间的比较优先级不变，不记录修改历史、不增加音频读取或散列。

容量维持最多 16 份、估算 64 MiB 清单；共享的最新正向观察另限估算 64 MiB、每目录最多 100,000 个变化路径。原文件数、总字节、遍历、计划、配对和传输限制保持。增量刷新不消耗每设备每分钟 4 次的完整扫描预算，完整扫描仍受此预算限制。

FileSystemWatcher 的重复事件、目录移动和缓冲区溢出行为依据 [Microsoft 官方文档](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0)。事件是失效依据，不能代替提交前的实时核验。

## 协议与 MCP

`lan_sync_status.manifestCache` 增加 `incrementalHits/refreshedPaths/libraryMetadataReused/fullScans/sharedEstimatedBytes/maximumSharedBytes`；含义见[桌面 MCP 文档](../desktop/docs/mcp.md)。所有计数为当前 PC 进程内计数，调用前后取差值，不返回共享路径或文件内容。

TLS plan 的 `cacheHit=true` 包括直接复用和按路径刷新；新增可选 `incremental/refreshedPaths/libraryMetadataReused`。`scanMs` 在增量刷新时反映实际耗时。旧手机继续读取原有字段；手机之前新增的本地索引诊断不变。MCP 工具参数和输入 Schema 均不变，开发版和正式版共用业务实现。

## 验证

```powershell
dotnet test desktop/tests/JeekMusic.Core.Tests -c Release --filter "FullyQualifiedName~Lan|FullyQualifiedName~FileReconciliation|FullyQualifiedName~AudioEnvelope"
dotnet test desktop/tests/JeekMusic.App.Tests -c Release --filter "FullyQualifiedName~Lan|FullyQualifiedName~FileEditTimeStore|FullyQualifiedName~Scanner"
```

专项覆盖单个文件修改、新增/改名/删除与全量结果对照，实际曲库扫描共享、真实 Windows 目录监听，CUE/依赖回退、过期及监听/版本失效，Unicode 目录碰撞、取消、容量和不可用根。Android 联调仅使用模拟器，不操作实体手机。

2026-11-15：Core 同步相关回归 27 项、桌面 LAN/扫描/最后编辑时间回归 137 项通过。真实隔离 PC 客户端的合成 64 文件曲库验证连续 6 次修改全部增量刷新，不增加完整扫描次数；正常曲库扫描后的变化信息共享不再读取第二遍大小/mtime。新增、改名、删除及目录整体移入回退均通过，音频散列计数为 0。未变化 CUE 的重复观察不会制造新的脏状态；日常扫描也不能把安全清单未接纳的路径（例如链接目标）带入同步。

最终跨端测试在模拟器的公开 SAF 目录通过双向新增/删除、本地索引复用和 CUE 范围外音频修复。日常 PC 客户端保存队列/设置后部署并重启，生产 MCP 确认新诊断和工具说明生效，输入 Schema 不变。本轮没有 UI 改动，没有操作实体手机。

忽略的本机证据：`desktop/artifacts/pc-index-core-tests.log`、`pc-index-final-tests.log`、`pc-index-delivery-runtime.log`、`pc-index-delivery-runtime/result.json`、`pc-index-delivery-android.log`、`pc-index-delivery-cue.log`、`pc-index-deploy.log`。真实 PC 索引测试入口：

```powershell
python desktop/tools/test-pc-sync-index.py --exe desktop/artifacts/pc-index-delivery-publish/JeekMusic.exe --out desktop/artifacts/pc-index-delivery-runtime
```
