# 流式同步与元数据缓存性能验证

2026-11-14。用户反馈准备耗时过长。扫描本来已只读大小和修改时间，实际流程仍有额外等待：每个变化文件固定暂停 500ms、PC 清单与手机全目录扫描完成后才传输、短小 TLS 帧受到 TCP 延迟确认影响，以及每个文件落地后等待 MediaStore/曲库刷新。

本次修改：

- 两端 TCP 启用 NoDelay，保留认证、帧确认与请求限流。
- 去掉逐文件 500ms 等待，保留操作前、传输后、条件提交与基线确认的精确元数据检查。
- PC 清单 200 条分页到达即可比较变化文件；手机扫描也以 200 条短 SQLite 事务批次处理。网络 I/O 不持有数据库事务，已处理路径用磁盘暂存表去重。删除延后到完整快照，CUE 在音频后处理。
- 只在健康 watcher、实际根路径和 revision 相符时复用 PC 清单。事件立即失效，不等曲库的 2 秒合并；缓存最多 16 项、估算 64MiB，15 分钟强制重建。停服务、撤权、根变动和监听失败不复用缓存。
- 每设备每分钟 4 次预算仅计真实扫描，缓存命中不占用。其他资源限制不变；连续修改使缓存反复失效时仍可能受此预算影响。
- 文件提交前仍持久登记曲库刷新路径。映射完成后按最多 200 路径合并刷新，取消/错误保留未确认记录，恢复时可重复执行。界面及调度报告显示“正在更新曲库”/`library_refresh`。
- 修正完成页遗留的“SHA-256 已核验”为“同步完成”，与实际只比较大小和修改时间的行为一致。

仍有准备成本：首次 PC 计划完整枚举所选元数据，读取共享基线，确认手机目录身份。目录监听是提示和缓存失效依据，不代替提交前检查。手机通过[完整本地索引及日常扫描共享](sync-local-file-index.md)避免每轮重复枚举；首次建立、监听不可靠及定期补查仍重新枚举。普通元数据比较没有新增音频哈希。

## 同数据前后测量

驱动 `desktop/tools/test-lan-performance.py` 使用隔离 Windows 生产服务、真实 TLS 和 Android 生产协调器。21 个有效 WAV，总计 8,390,812 字节，同一模拟器、同一合成内容，两次独立运行；基线为本次优化前安装版本。本测试使用手机私有 native 文件目标，不调用 MediaScanner，因此只测同步引擎与传输，不能当作实体手机、SAF 或整库索引耗时。

| 项目 | 修改前 | 修改后 |
| --- | ---: | ---: |
| 连续 16 次小元数据请求 | 14,133.94 ms | 939.19 ms |
| 首次同步的首个数据块 | 8,608.95 ms | 1,433.73 ms |
| 首次同步完成 | 57,388.79 ms | 10,667.34 ms |
| 无变化再次对账 | 5,956.37 ms | 1,281.37 ms |

两次都传输相同字节数、完成 21 文件、没有冲突/延期，PC 音频 `hashBytes=0`。修改后首个数据块在手机全目录扫描前出现；第二轮命中 PC 清单缓存，不传输文件。这里只是同环境样本，不是多次统计或固定耗时保证。原始数据位于不入库的 `desktop/artifacts/sync-performance-before.json` 和 `sync-performance-after.json`。

独立 PC 元数据样本含 128 个音频文件（32MiB）和一个 CUE，第二轮不再读取 CUE 文本，且元数据观察计数只剩驱动显式探测的一次；音频哈希与传输均为零。缓存/watcher、诊断类型、跨端断点和真实 UI 的最终验证结果见下方验证记录。

## 复现

```powershell
python desktop/tools/test-lan-performance.py --exe bin/JeekMusic.exe --android emulator-5584 --report desktop/artifacts/sync-performance-after.json
python desktop/tools/test-lan-files.py --exe bin/JeekMusic.exe --benchmark
python desktop/tools/test-lan-files.py --exe bin/JeekMusic.exe --android emulator-5584
python desktop/tools/test-lan-background.py --exe bin/JeekMusic.exe --android emulator-5584 --manual --out desktop/artifacts/sync-performance-ui
```

先安装当前 debug 与 androidTest APK。所有驱动使用临时 PC 曲库、独立配对与合成文件；UI 驱动备份/恢复模拟器同步配置，仅删除自己生成的唯一测试目录，不修改真实用户音乐。MCP 新诊断字段及兼容策略见 [MCP 文档](../desktop/docs/mcp.md)。

## 最终验证记录

- Windows LAN 回归 111 项通过；Android debug 单元测试 129 项通过，debug 与 androidTest 构建成功。
- 7 项真实 Android/Windows 协调器回归全部通过，包含双向新增/修改/删除、精确元数据复用、真实冲突及并发选择、丢失确认后编辑与重启恢复。新增断言：手机扫描开始前取消，两端都不传播删除；首个下载在手机全量扫描前开始；1MiB 下载在 256KiB 后取消，只补传剩余 768KiB；排序靠前的 CUE 仍在新增音频后安装。
- 完整驱动按用例使用独立 PC 服务与凭证，避免测试之间累积超过 8 scopes/凭证和 120 次来源连接尝试/分钟。曾由夹具累积触发的拒绝没有通过放宽生产限制规避。最终原始日志：`desktop/artifacts/sync-performance-final-integration.log`。
- 真实 Activity 操作：点击立即同步、按 Home、等待真实 WorkManager、返回页面并等前台元数据对账结束。32MiB 有效 WAV 与 20 个小 WAV 文件完整，主文件字节相同，MediaStore 已入库；后台报告 `state=idle/cancelled=false/backoffUntil=0/hashBytes=0`。返回前台再次对账传输字节为 0，PC 清单缓存命中，监听恢复健康，没有用户取消提示。已实际查看运行中和完成状态截图：`desktop/artifacts/sync-performance-ui/android-preparation.png`、`android-returned.png`；结果在同目录 `background-result.json`。UIAutomator 曾短暂返回 null root，重试成功；未将这一瞬时读取错误当成客户端失败或跳过验收。
- Windows 保存窗口/队列后结束旧进程并部署、启动新进程；生产 MCP 确认 `manifestCache`、缓存命中扫描预算豁免和性能工具描述生效，输入 Schema 保持兼容。Android 模拟器安装当前 APK，夹具结束后恢复原配置并重新启动。

以上为隔离模拟器验收，未连接实体手机，不承诺实体手机、大曲库或系统受限后台调度具有相同耗时。
