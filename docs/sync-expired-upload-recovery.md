# 过期上传恢复

2026-11-15 修复手机单文件反复提示 `Failed requirement`。

PC 已清理过期上传的部分文件，旧 `file_begin` 却直接返回 `state=expired, offset=0`。旧手机把它当作正在上传的任务，继续发数据块；PC 再返回未推进的偏移，手机的无说明 `require` 失败并终止整轮。重复同步仍复用该操作号，无法自行恢复。

现在 PC 对 expired/aborted 上传重新执行正常准入，严格匹配原操作身份、源版本及提交前提，重新检查基线、空间、每日预算和活动上传数；通过后从 0 开始。只复用原事务行，不放宽其他限制，不覆盖当前用户文件。prepared/committed/acknowledged 仍按原恢复或幂等路径处理；无法确认归属的私有部分文件保持原样并延期。

手机查询到已退役事务时将本地记录置为 aborted，下次创建新上传；异常开始偏移、块确认偏移及过期状态改为具体的单文件延期原因，其他文件可以继续。界面分别说明断点失效、电脑确认进度不一致或返回断点无效，保留文件供重试。日志记录具体文件原因，整轮意外异常保留堆栈。这里的记录属于已有事务检查点，不增加文件编辑历史。

## 验证

- 隔离测试先复现旧行为：期待 uploading，实际 expired。修复后通过。
- Windows LAN 回归 114 项通过，新增覆盖过期重启、复用已满事务行配额、提交幂等、基线已变化时拒绝重启、私有部分文件被替换时拒绝截断。
- Android 单元测试 133 项通过，其中 3 项验证退役状态、异常偏移的具体原因及正常续传/重新开始；debug、androidTest、签名 release 构建成功。
- 8 项隔离 Android/Windows 真实 TLS 协调器回归全部通过，包含断点续传及既有时间/大小规则。
- PC 日常客户端已部署并结束旧进程、启动新版。生产 MCP 核对 `expiredUploadPolicy=fresh_admission_restart`、`restartedUploads=1`。实体手机使用原版本在后台完成原失败文件，PC 事务为 acknowledged，待处理冲突为 0，两个映射均 idle、无待重试退避。

原始记录位于忽略的 `desktop/artifacts/sync-expired-*`，实体设备证据保留在私有 artifacts 中，不提交歌曲路径、配对信息或设备地址。

## 实体手机安装与 UI 验收

首次交付时，实体手机虽经无线 ADB 可连接，但系统拒绝同签名覆盖安装，返回 `INSTALL_FAILED_ABORTED: User rejected permissions`，随后锁屏；当时仅确认旧手机配合 PC 修复后台完成，未将新版手机 UI 标为通过。

用户重开无线 ADB 并解锁后，同签名覆盖安装成功。已核实实体手机版本为 1.0.270，结束原进程并启动新版，原有配对、目录映射和 Wi-Fi 选项保留；全程没有卸载或清除数据。

实际进入设置 → 同步，先确认自动对账完成，再点击“立即同步”、展开详情并等待本轮完成。真实页面显示“同步完成”，检查 1714 个路径，自动处理 0 项、冲突 0 项、延期 0 项，无原来的 Failed requirement。生产 MCP 对照前后报告：本次 trigger=manual、lastSuccess 推进、两个映射均 idle/running=false/backoffUntil=0、lastTransferBytes=0，PC 累积 transferBytes 未增长，pendingConflicts=0。原文件没有重复传输。

已实际查看完成状态截图 `desktop/artifacts/sync-expired-physical/v270-complete-before-manual.png`、`v270-complete-details.png`；私有前后证据为同目录 `v270-status-before.json`、`v270-manual-final.json`。正常完成状态和详情已完成真机 UI 验收；三个新增异常恢复提示仍未在真机故障状态下展示验收，其判定由单元测试覆盖。
