# 手机同步的 Wi-Fi 选项

设置 → 同步 → 自动同步与状态 → **仅在 Wi-Fi 下同步**，默认开启，关闭后允许移动网络。选项持久保存在手机 `lan-sync.json` 的本机 `wifiOnly` 字段，不参与跨设备配置合并；电脑只能读取调度诊断中的副本。已有用户没有该字段时也默认开启。

统一网络策略覆盖配对/目录查询、手动与自动文件同步、WorkManager 后台同步、用户数据、独立学习数据、冲突状态轮询和远端任务取消。Wi-Fi 按已连接网络的 `TRANSPORT_WIFI` 判断，计费 Wi-Fi 仍可用，不将“无流量计费”当作 Wi-Fi。即使 Android 仍将移动网络作为默认网络，也显式选择已连接的 Wi-Fi；不绕过活动 VPN。开启时 DNS 与 TCP 均绑定到同一 Wi-Fi 网络，网络丢失会关闭现有连接；移动网络不会作为回退。开启开关时，已有非 Wi-Fi 连接也立即关闭。

等待 Wi-Fi 不等于用户取消，不修改配对、映射、事务和已有文件。Wi-Fi 恢复或关闭限制时清除网络等待的退避，前台重新提示自动调度；后台仍使用 Android WorkManager 的正常周期和系统调度约束。旧版目录交换仅在本机读写文件，第三方应用实际上传这些文件的网络策略由第三方应用控制。

电脑 MCP 通过最后一次经认证的调度报告读取 `wifiOnly/waitingForWifi`，不提供修改手机偏好的接口。TLS 能力标记用于兼容旧电脑，详见 [MCP 文档](../desktop/docs/mcp.md)。

网络切换回归（只用于明确指定的隔离模拟器，预先安装新版 debug 与 androidTest APK）：

```powershell
python mobile/tools/test_sync_network.py --serial emulator-5584 --report desktop/artifacts/sync-wifi-network.json
```

测试使用独立手机设置和电脑本地 echo 服务，临时启用模拟器移动数据，实际切换 Wi-Fi，并恢复原网络状态。覆盖默认 Wi-Fi 放行、移动网络拒绝且不建立 TLS、失去 Wi-Fi 关闭已有连接、关闭选项后移动网络放行、重新开启关闭已有移动连接、恢复 Wi-Fi 后放行及持久化。不会读取已有配对或写入真实曲库。

真实 UI 验收需操作新版客户端开关、退出重启检查持久化，并截取开启/关闭及等待 Wi-Fi 的状态。截图及本地运行报告放在 `desktop/artifacts/sync-wifi/`，不将仅编译或渲染断言当作 UI 验收。

2026-11-14 验证通过：桌面 LAN 回归 104 项、Android 协议单测 6 项；真实模拟器 Wi-Fi/蜂窝切换回归通过，echo 对端恰好收到三条允许的连接，Wi-Fi 限制期间没有新增连接。使用 `mobile/tools/settings_ui.py --serial emulator-5584 --output desktop/artifacts/sync-wifi verify-sync-wifi` 实际操作新版客户端，开启/关闭、关闭后重启持久化、等待提示及恢复清除提示全部通过；已人工查看 `wifi-on.png`、`wifi-off-restarted.png`、`wifi-waiting.png` 的布局和状态。

平台依据：[Network.bindSocket / 网络专用 DNS](https://developer.android.com/reference/android/net/Network)、[NetworkCapabilities.TRANSPORT_WIFI](https://developer.android.com/reference/android/net/NetworkCapabilities#TRANSPORT_WIFI)。
