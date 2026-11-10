# Android 同步目录开关保持原位

## 问题与修复

同步设置中的目录按 `LanSyncStore.mappings()` 的顺序显示。原来的 `saveMapping()` 先移除同一 `rootId` 的条目，再把更新后的条目追加到末尾。点击 A 的启用开关后，A/B/C 因此变成 B/C/A；B 顶替 A 的原位置，造成操作了其他目录的观感。同步检查点保存及编辑目录也会触发同一问题。

现在已有目录按 `rootId` 在原索引替换，新目录才追加。界面同时按 `rootId` 为整组目录控件提供 Compose `key`，使控件身份跟随目录。配置格式及现有启停行为保持兼容；此修改仅涉及 Android，没有 Windows MCP 接口变更。

## 回归方式

`LanSyncStoreTest` 使用隔离的私有配置目录，检查三个目录反复启停时的顺序及独立状态、重新读取持久化文件后的结果，以及编辑与新增目录时的顺序。

真实客户端回归入口：

```powershell
python mobile/tools/settings_ui.py --serial emulator-5584 --output output/sync-folder-toggle/after verify-sync-mapping-order
```

该入口仅接受 `JeekLanEmpty35` 模拟器上的 `.debug` 应用。运行前先备份 `files/lan-sync.json`，结束应用，再通过 `run-as` 写入专用合成配置：不含 `connection`，`autoCancelled=true`；三个映射的名称分别为 `Toggle fixture A/B/C`，`rootId` 分别为 32 个小写 a/b/c，`layoutVersion=2`，初始 `enabled=false`。映射提供正常的 `selection=[""]`、`key`、`treeUri`、`documentId`、`volume` 和 `relative` 字段；目标使用不存在的 `ToggleFixtureA/B/C` 目录，不授予 SAF 权限。启动应用并关闭更新提示后运行回归。

回归实际点击整行和右侧开关，检查被点击行的屏幕位置、所有目录的 UI 状态与配置顺序；再返回重进并终止、重启应用，检查非默认状态持久化。每一步保存 UIAutomator 层级和真实屏幕截图。完成后终止应用，逐字节恢复备份配置，再启动新版本；不在实体手机或真实曲库上注入夹具。

## 本次验收

- 旧版客户端已实际复现：第一次点击 A 后，配置和真实截图均显示顺序变成 B/C/A。
- 新增的两项 `LanSyncStoreTest` 设备测试通过；完整 JVM 单元测试 122 项通过，无失败或跳过。
- `assembleDebug`、`assembleDebugAndroidTest`、`lintDebug` 与本地正式签名的 `assembleRelease` 均成功；Lint 为 0 错误、94 警告。
- 新版真实 UI 的六次连续切换、整行/右侧开关点击、返回页面及进程重启后的非默认状态持久化全部通过；每步均核对真实控件状态、屏幕位置和持久化配置。
- 合成夹具已移除，原 `lan-sync.json` 逐字节恢复；结束原调试客户端进程后，已启动包含修复的新版本。

[旧版点击 A 后移位](evidence/android-sync-folder-toggle/before-click-a.png)；[新版点击 A 后保持原位，B/C 状态不变](evidence/android-sync-folder-toggle/after-click-a.png)；[重启后完成回归](evidence/android-sync-folder-toggle/after-restart.png)；[真实 UI 验收结果](evidence/android-sync-folder-toggle/ui-results.json)。截图均来自 API 35 隔离模拟器上的真实客户端；本轮没有实体手机验收或真实文件传输验收。
