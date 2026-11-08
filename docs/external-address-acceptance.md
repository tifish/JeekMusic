# 外网扫码地址验收（2026-11-08）

桌面设置 → 同步新增可保存的外网端点，扫码邀请优先使用保存的主机和外网端口。留空沿用局域网字段。协议仍为 TLS TCP 和现有证书指纹固定，不是 HTTP 服务，Android 邀请格式无需变化。MCP 的 external_address 操作、可选 host 和新增诊断与 UI 复用同一服务。

已完成验证：

- `dotnet test desktop/tests/JeekMusic.App.Tests --filter 'FullyQualifiedName~LanPairingQrTests|FullyQualifiedName~LanLifecycleTests|FullyQualifiedName~McpSessionTests' --nologo`：98 项通过。覆盖输入归一化、无效端点、外网端口、配置重载、监听不变、二维码生成、保存使旧邀请失效及旧显式 host 调用兼容。
- Release Windows x64 Composite ReadyToRun 日常发布通过，无新增裁剪/AOT 分析告警。NativeAOT 本次未重新发布；代码使用现有 JSON DOM 与同一服务，没有增加反射接口。
- 结束旧日常客户端，启动根 `bin/JeekMusic.exe`。通过 Windows UI Automation 实际打开设置 → 同步，填写并保存 `https://203.0.113.7:45678/`，输入框归一化为 `tls://203.0.113.7:45678`；检查保存提示和布局。
- 实际点击生成二维码，通过窗口只读邀请控件确认主机为测试外网 IP、端口为 45678。检查过程不输出邀请码或二维码，不保存含有效凭证的截图。
- 实际填写回环地址，保存被拒绝，输入框附近显示有效地址提示。测试后清空外网地址，生产 MCP 确认 externalAddress 为空、invitationActive=false、监听仍为 39843。
- 已检查真实客户端截图：`desktop/artifacts/external-address-saved.png`、`desktop/artifacts/external-address-invalid.png`。输入、按钮及对应反馈可见。截图不包含邀请码。

网络侧状态：本机 `192.168.1.7`，服务 `0.0.0.0:39843/TCP`，现有精确 Windows 应用入站规则处于 configured。已按全局 Edge MCP 配置，通过 Playwright 扩展访问默认 Edge 的 `http://192.168.1.1/`（TL-7DR7260 易展版）。路由器要求管理员密码，尚未登录；未添加端口转发，未取得/填写实际外网地址，也未验收公网到手机的实际连接。Windows 防火墙就绪、二维码生成与测试保留 IP 均不能视为公网连通通过。
