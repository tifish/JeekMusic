# 设置项交互可发现性验证（2026-10-26）

基线：`8496c307eda05c015adb6a11fa9425785abf06af`（1.0.176）。仅修改 Android 设置展示和交互语义；存储键、默认值、同步协议及业务服务不变。扫码配对留给下一任务。

## 展示约定

- `SettingsComponents.kt`：导航/弹窗/授权入口用 `SettingItem`，整行按钮语义、按压反馈、至少 56dp 高，右侧独立 24dp chevron；标题/当前值可以换行。静态设备信息/源码地址没有箭头或空点击，使用较小字号和次要色。
- 执行操作用 `SettingAction` 或明确的 Button/OutlinedButton；状态/说明在按钮外，长操作标题允许换行。下载执行时按钮显示“停止”；危险操作确认保留。按钮既有 testTag 继续定位按钮本身。
- `SwitchRow` / `SettingCheckbox` / `SettingRadio` 由整行拥有 toggleable/selectable 语义及唯一回调，指示器不另设点击处理；至少 48dp 触摸区。LAN 启用状态也用 Switch，清理/移除分成按钮，FlowRow 适应大字体。
- 关闭文件名推断时，格式整行禁用并解释启用条件。来源只剩一个选中时禁止取消并解释至少保留一个来源。忙碌的 LAN 入口显式禁用并显示“暂不可用”。
- 标签栏管理增加操作说明，名称保持静态，拖动手柄/取消固定图标维持原有行为。

## 自动验证

在仓库根执行：

```powershell
.\mobile\gradlew.bat -p mobile :app:testDebugUnitTest :app:assembleDebug :app:lintDebug :app:assembleRelease --console=plain
```

88 项现有 JVM 测试，0 失败/错误/跳过；Debug 与启用 R8 的签名 Release 构建。未为低风险样式编写镜像测试。最终 lint 为 0 错误、18 项位于既有代码的警告，本轮组件没有新增警告。完整结果见本机 `output/settings-ui-20261026/build-accepted.log`，不关闭任何检查。一次中间运行出现 lint Kotlin FIR 分析器内部异常，稳定源码重跑已成功。

Release 使用本地正式签名；`apksigner verify --print-certs` 验证成功，证书 SHA-256：`28a86a1affb4a31ed0a2a14e602b2ebed23d1c4385b366e9ccfac3eaf5a972fe`，可覆盖模拟器现有正式包。

## 真实客户端验收

先以 `adb devices -l` 和 `adb -s emulator-5582 emu avd name` 核对唯一连接为 **JeekLanSync35 / API35 x86_64 / emulator-5582**。T3 设备面板返回 `Agent device access is turned off for this environment.`，按 `mobile/README.md` / `tools/ui.sh` 的 ADB uiautomator/input/screencap 流程实际操作；未使用 headless 结果替代客户端验收。

- Debug 中文浅色、正常字体：普通设置、LAN 配对/权限/任务入口、按钮/状态层级、滚动和多行路径目检。艺术家行可进入子页；关闭推断后点击另一格式不改变原选中值；离开不应用草稿。
- 配对弹窗打开，空输入连接禁用，取消退出；截图不含邀请或凭证。权限行实际进入 Android 所有文件访问授权页，未更改授权。清理按钮实际弹出原危险确认，取消未执行清理/移除。
- LAN 启用标题点击和开关位置点击各改变一次状态；测试后两个 Debug 映射均恢复启用。服务原有 setEnabled 会重排映射，按可见路径核对后恢复。未执行同步下载、清理或移除任务。
- 已有映射弹窗能打开，整个根和确认声明支持整行勾选，保存启用条件保持；目标行打开系统 SAF 文件夹选择器，返回不保存。
- Debug 英文深色、字体 1.5：普通设置、同步长标题/当前值、操作按钮分行、设备名称编辑、播放列表历史、来源复选项、标签栏管理均操作并截图。最后一个来源禁用、点击保持选中且显示解释；恢复全部来源并关闭临时开启的在线歌词。
- 英文浅色、字体 1.5：外观/关于区，入口箭头、静态源码说明和开关目检；主题恢复跟随系统。
- 签名 Release 覆盖安装并重启，普通设置和同步页实际打开并截图；配对/艺术家入口作最小复验。未改 Windows 客户端，无需 Windows 重启或新增无关 MCP 接口。

截图和对应 UI XML 在本机 `output/settings-ui-20261026/`（不入 Git）。供父目检的主要文件：

- `release-zh-light-settings.png`、`release-zh-light-sync.png`、`release-artists.png`、`release-pair-dialog.png`
- `debug-en-dark-large-settings.png`、`debug-en-dark-large-sync.png`、`debug-en-dark-large-sync-actions.png`
- `debug-zh-light-artists-disabled.png`、`debug-zh-light-artists-disabled-after-tap.png`
- `debug-source-disabled.png`、`debug-source-disabled-after-tap.png`
- `debug-en-light-large-about.png`、`debug-en-dark-large-tab-manager.png`
- `debug-zh-pair-dialog.png`、`debug-permission-entry.png`、`debug-clean-confirm.png`、`debug-mapping-selected.png`、`debug-target-picker.png`、`debug-device-name-dialog.png`、`debug-history-dialog.png`

## 限制与交接

现有 Debug 配对 PC 测试端口 `10.0.2.2:39843` 返回 `ECONNREFUSED`。已看到根列表请求期间的禁用提示、随后原有错误弹窗；**未验收在线根列表内容、远端子目录浏览/估算和运行中同步状态**。未重启/修改 PC 网络服务或重新配对。保存映射弹窗仍可在关闭错误弹窗后验收本地控件。标签管理只有内置标签，取消固定艺术家的图标/实际删除未做本轮设备验收（未改其逻辑）。无 TalkBack 人工朗读验收。

未接触用户手机、D:\Mobile 真实音频、恢复备份或 Syncthing 配置；未清数据/删除既有证据。结束时字体恢复 1.0、系统浅色、Debug 与 Release 均保持中文；Debug 临时选择恢复，最终 Release 客户端停留设置供父目检。后续 QR 实现热点为 `LanSyncSettings.kt`、`values/strings.xml`、`values-zh-rCN/strings.xml`，公共入口直接复用同包 `SettingItem`（标题、summary、enabled、onClick；onClick 用具名参数）。
