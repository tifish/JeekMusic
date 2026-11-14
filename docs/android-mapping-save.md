# Android 同步目录保存等待修复

2026-11-14。发现并复现的阻塞路径：`saveMapping` 在检查同步任务前等待整轮同步使用的 `gate`；同步任务又在发布运行状态前，持锁向电脑上报调度状态。网络等待期间，界面可能仍允许保存，而保存必须等同步释放锁。未取得实体手机当次调用的日志，不能据此认定它是唯一耗时来源。

保存现在尝试获取锁，忙碌时立即返回中文/英文提示，保留当前目录选择；不会把这次保存排队到同步完成之后。目录编辑弹窗观察同步和调度状态，同步中禁用保存并提示先关闭窗口、取消同步。关闭按钮仍可使用。同步在等待锁或网络前发布运行状态，取消等待也会清除运行状态。向电脑上报调度状态独立执行，不再占用目录编辑锁。目录选择、SAF 授权、目录注册、映射持久化及同步规则保持原有逻辑。

## 验证

- `:app:testDebugUnitTest`：122 项通过，0 失败/错误/跳过。
- `:app:lintDebug`：0 错误，94 警告；debug、instrumentation 和启用 R8 的正式签名 release 构建通过。
- `LanMappingSaveTest` 两项设备回归通过：锁长期被占用时及时拒绝且不修改设置；任务已启动但尚未获取锁时也及时拒绝；输入校验失败释放锁；等待锁期间运行状态可见，取消后清除。
- 在已有隔离 `JeekLanEmpty35` / `emulator-5584`（API 35）实际启动客户端，使用 Accessibility 操作现有设置页及真实目录弹窗。Windows 端使用单独数据目录、空合成曲库和临时回环 TLS 服务，经 ADB reverse 连接；没有连接日常曲库或实体手机。
- UI 夹具在弹窗已显示空闲后占用服务的真实互斥锁，实际点击保存：182 ms 出现忙碌提示，原映射未更改。随后发布同步状态，保存按钮禁用且提示可见，实际关闭窗口成功。
- 重新打开同一目录弹窗后，把测试连接改为关闭的回环端口 9，实际点击保存：270 ms 完成，弹窗关闭，映射 key 保持、enabled 持久化为 true。使用模拟器已有的公共 SAF 目录授权；未重新测试系统目录选择器、新授权或实体 SD 卡性能。
- 截图已目检，位于本地 `desktop/artifacts/mapping-save-ui/{ready,busy-error,busy-disabled,saved-offline}.png`。第一次交互夹具因未等待父列表的启用状态刷新而中止；修正等待和截图动画时序后整段通过。
- 测试完成恢复原配对配置和映射，移除本次新注册的测试目录记录，结束临时 Windows 服务及 ADB reverse，重新启动修复后的模拟器客户端。没有更改 Windows 软件/MCP 接口或日常服务配置。

复现及交互验收命令（需已安装对应 debug 和 instrumentation APK，并保留该 AVD 已有测试 SAF 授权）：

```powershell
python mobile/tools/verify_mapping_save.py --serial emulator-5584
adb -s emulator-5584 shell am instrument -w -e class com.tinyfish.jeekmusic.data.sync.LanMappingSaveTest#saveDoesNotWaitForBusySyncAndDoesNotChangeSettings,com.tinyfish.jeekmusic.data.sync.LanMappingSaveTest#runningStateIsVisibleBeforeGateAndClearsWhenCancelled com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner
```

实体手机没有连接 ADB，未安装、重启或验收实体手机。用户原版本为 1.0.260，修复需要安装更新后的正式签名 APK；不要求重新配对或删除现有映射。
