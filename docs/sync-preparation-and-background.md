# 同步准备、后台交接与续传

旧提示“正在 PC 核验全部所选内容”覆盖过大的准备范围，已改为明确阶段：恢复未完成文件操作、读取已确认记录、读取电脑目录元数据、分页获取电脑清单、扫描手机目录、比较变化。可计数的阶段显示记录/文件数。目录枚举和手机数据库读写仍随文件数量增长，不能仅凭旧提示把耗时归到 PC。

日常判断只比较相对路径、文件大小和精确修改时间，不逐首读取音频或计算内容哈希；变化的 CUE 可能需要读取小型文本以解析依赖。每轮仍需获取清单与扫描目录；这些准备步骤没有保存“扫描到第几个文件”的跨轮断点。

准备阶段的另一种等待来自旧的文件工作准入：PC 的单一文件工作许可一旦短暂被状态报告或元数据请求占用，另一请求立即收到 busy，手机随后等待 30 秒。现在认证后的文件请求最多短等 250 ms，短操作结束即可继续；仍只有一个文件工作者，仍受连接总数和请求截止限制。真正持续忙碌仍返回 busy，不承诺任意曲库都在固定秒数内准备完成。

保留的是文件与操作进度：

- 已经由双方确认的文件保留基线，下次检查未变化时不重新传输。
- 下载使用 256 KiB 块。正常取消或网络错误时，退出前同步落盘并原子保存最后一个完整块的位置，版本未变化时继续剩余部分。周期检查点仍为 8 MiB/2 秒。
- 上传复用持久操作号与 PC 已确认的偏移；提交成功但确认丢失时恢复操作结果，不重复覆盖文件。
- 未完成的替换/删除使用事务日志恢复。源文件版本改变、私有部分文件不匹配检查点，或进程被强杀导致最新检查点没有写成时，当前文件可能重做；不能承诺任意崩溃都零重复字节。

切换到其他程序时，前台协程没有后台系统执行保障，因此交给 WorkManager。先取消前台工作、等清理与检查点落盘完成，再排入后台；快速回到前台则唤起前台调度。交接提示“同步已暂停，等待 Android 接管后台同步”，不修改用户取消开关，不增加失败退避。只有明确点击取消才持久停止自动同步。已经由 Worker 执行的任务不因页面退后台而取消。

Android 12 及以上对手动同步的交接请求使用加急 WorkManager，配额不足降级为普通任务，不丢弃；更早版本继续使用普通 Worker，避免引入需要额外通知实现的兼容前台服务。调度报告的 `trigger` 保留当前/最近一次实际执行来源，排队的目录变化仅置 dirty，不覆盖正在执行或已经完成的 Worker 来源。

旧流程还存在两个问题：Worker 过早启动会把仍在退出的前台任务误认为有效任务，直接返回；WorkManager 的 CONNECTED 约束可能阻挡能连接 PC、但未通过互联网验证的局域网 Wi-Fi。现在等待旧任务结束后再排入 Worker，周期配置使用 UPDATE 替换旧约束，实际网络选择与 Wi-Fi 限制仍由生产传输策略检查。系统可能延迟后台任务，且单轮后台仍最多 8 分钟；这里不保证切后台后瞬时、无限持续执行。

MCP 继续通过现有调度报告显示阶段与后台等待状态，不新增开发专用接口，详见 [MCP 文档](../desktop/docs/mcp.md)。

回归入口：

```powershell
python desktop/tools/test-lan-files.py --exe bin/JeekMusic.exe --android emulator-5584 --case interruptedDownloadResumesTheLastCompleteBlock
python desktop/tools/test-lan-files.py --exe bin/JeekMusic.exe --android emulator-5584 --auto --public-saf --case backgroundHandoffWaitsForForegroundCleanup
python desktop/tools/test-lan-background.py --exe bin/JeekMusic.exe --android emulator-5584 --manual --out desktop/artifacts/sync-handoff-ui
```

使用独立回环 PC 服务、临时合成曲库及隔离模拟器。下载回归在 1 MiB 文件接收首个 256 KiB 块后取消，检查落盘检查点，重试断言只再传 768 KiB。后台回归用真实 Activity 生命周期和真实 WorkManager，检验旧任务已完成才开始新 Worker、不出现用户取消提示、没有失败退避。UI 夹具实际点击立即同步后按 Home，检查后台传输结果并截图返回状态；不用直接调用 start 冒充 UI 交互。

2026-11-14 验证通过：桌面 LAN 回归 107 项、Android 协议单测 6 项、上述两项真实跨端回归通过。真实客户端手动同步后切到桌面，32 MiB 主文件与 20 个小文件由后台完成，主文件字节一致、小文件数量完整；最终报告 foreground=false、cancelled=false、backoffUntil=0、state=idle、hashBytes=0，后台传输 33,555,648 字节（另一个 64 字节文件已在前台传完）。返回客户端无用户取消提示。已查看 `desktop/artifacts/sync-handoff-ui/android-preparation.png`、`android-returned.png`，原始运行结果为同目录 `background-result.json`。这些结果不等于实体手机的大曲库耗时测试，不承诺每次准备耗时固定。

平台依据：[WorkManager 网络连接控制器（验证互联网连接）](https://android.googlesource.com/platform/frameworks/support/+/f2e05c341382db64d127118a13451dcaa554b702/work/workmanager/src/main/java/androidx/work/impl/constraints/controllers/NetworkConnectedController.java)、[周期任务 UPDATE 策略](https://developer.android.com/reference/androidx/work/ExistingPeriodicWorkPolicy#UPDATE)、[加急任务及配额降级](https://developer.android.com/develop/background-work/background-tasks/persistent/getting-started/define-work#expedited)。
