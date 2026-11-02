# 配对即全能力与自动 Windows 防火墙验收

2026-11-03。配对是现有同步能力的明确授权边界。新设备成功配对即获得音乐读取、领域数据与学习事件写入、受边界保护的文件写删、归属学习管理与组件下载能力；已有正常配对设备统一归一，保留凭证，无需重新配对。权限 UI 已移除，保留身份、邀请与撤销。旧 `authorize` / `invite.permissions` 明确拒绝，兼容只读诊断仍提供六项 true 和 `authorizationPolicy=paired_all`。

安全边界保留：设备认证、TLS pin、撤销取消连接/归属任务、目录/路径边界、条件提交/基线冲突、恢复备份及全部既有预算。迁移不启动原本关闭的服务，不生成邀请、不自动写删或学习。首次实际下载仍须原有操作确认。长期 token 与五分钟一次性邀请规则不变。Android 遇受限旧桌面明确要求升级，失效凭证显示配对问题，不再静默跳过数据交换。

Windows 自动维护当前用户/数据目录所有者的精确规则：当前 EXE、实际 TCP 端口、本地绑定地址，Domain/Private/Public，非回环来源 Any，以兼容 LAN 和既有公网转发；回环无需放行。不修改 NAT/路由/UPnP、不关闭防火墙。只提权固定 helper，规则操作只使用内嵌固定脚本与结构化数据，不能提交任意命令/脚本路径。规则所有权用精确 Group/Description/Name 摘要和程序/TCP 结构复核。停服、退出、端口/升级路径变化只清理可核验自建规则，不碰用户手工或系统规则。失败独立报告；configured 不代表外网可达。

## 源码验证

- 桌面 Core 117 通过，App 731 通过、2 项真实音频端点条件跳过；新增配对迁移与防火墙生命周期/失败/参数验证。构建脚本检查通过。
- Android `testDebugUnitTest`、`lintDebug`、debug/instrumentation 包构建通过。
- `test-lan-paired.py` 真实 TLS/MCP：新配对与旧全 false 设备六能力、state/delta、学习管理/下载授权、合成文件写删及备份、越根拒绝、撤销立即断开、未知身份/复用 ID 的旧 token 拒绝、持久化重启、旧 false 再归一及下一次启动幂等通过。学习夹具无标签，业务停在 insufficient_material，未触发组件下载。
- `test-lan-files.py --android emulator-5584`：默认配对的真实 TLS 文件事务及实际 Android 三组双向矩阵通过；合成目录，未使用真实曲库。备份恢复/清除与基线保留通过。
- `test-lan-firewall.py --bind 192.168.1.7`：只对隔离 EXE、合成数据、该本机 IPv4 及临时单 TCP 端口创建规则；查询、幂等、端口更新、崩溃后升级路径替换、停服和回环清理通过。测试断言曾错误地要求回环为 configured，已修正为 not_required 后完整通过；未放宽产品行为。最终临时规则已清理。
- 实际 Windows 设置页截图确认没有六权限选择；点击隔离设备“撤销设备”后，生产业务 MCP 返回设备列表为空。截图只含隔离设备，无有效邀请码/真实曲库路径：[配对说明](evidence/lan-paired-all/paired-all-description.png)、[撤销前](evidence/lan-paired-all/device-before-revoke.png)、[撤销后](evidence/lan-paired-all/device-revoked.png)。

## 交付与限制

最终源码、同 SHA CI、正式包摘要、保全重启及最终运行状态记录在后续版本交付文档。本页不把开发版验收当最终 AOT 包验收。

当前执行环境已有管理员令牌，精确规则创建/清理由真实 Windows 完成；UAC 拒绝/权限拒绝由可注入后端定向测试覆盖，未假称实际操作过安全桌面的 UAC。企业管理策略及第三方防火墙没有真实环境覆盖；状态按查询结果报告，外网连通仍为 not_verified。Android 三组隔离矩阵不等于所有 OEM/ARM/SD 卡或真实公网验收。
