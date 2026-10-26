# 远程桌面播放失败修复验证（2026-10-26）

## 失败现场与根因

- 基线 HEAD `53554167fc5fead97e21fc6f5d2930bb78be93ea`，工作区干净；原客户端 PID 19976，`output/JeekMusic.exe`，Session 1。`query session` 确认当前为 `rdp-tcp#0 / fmy / Active`。
- 改设置前保存生产 MCP 的 diagnostics、output、status、queue 首 200 项及 app/playback 日志；原输出为 `device=auto`、共享、自动匹配开启，当前《飘雪》ID 1581，位置 0，队列 1602 项、索引 0、随机开启。
- Remote Audio 枚举 ID：`wasapi/{3.0.0.00000001}.{6C26BA7D-F0B2-4225-B422-8168C5261E45}`。已有 WASAPI 44.1 kHz float 双声道流，但客户端音量及格式查询报 `0x80070490 Element not found`，输出安全门控为 `endpoint-read-failed`，保持暂停。原设置下调用 resume，2 秒后仍为 0 秒，再次记录同一保护错误。
- 旧实现的 EndpointVolume、DeviceAudioFormat、ExclusiveAudioFormat 仅识别 `{0.0.0.00000000}.` 本地前缀，给其他完整 ID 再拼接该前缀。默认设备初次解析成功，绑定后的显式 ID 查询却变成不存在的设备。错误来自应用的 ID 转换，并非 Windows 没有重定向设备，也不是本次 mpv WASAPI 启动失败。
- Windows 要求端点 ID 保持不透明、原样交给 GetDevice：[IMMDevice::GetId 文档](https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getid)。

## 修改与边界

- 三个 Core Audio 入口共用 `AudioEndpointId.FromDeviceName`，仅去掉 mpv 的 `wasapi/` 包装；完整本地、远程及未知形态 ID 保持原样。仅旧版裸花括号 GUID 别名仍补本地前缀。默认端点选择方式保持不变。
- 端点解析失败诊断包含实际尝试 ID；不存在/失效的固定端点仍失败，不静默改用其他设备。默认变化继续使用现有轮询重建机制。
- 当前 RDP 真实读回：端点 44100 Hz / 16-bit PCM / stereo，Windows mix 44100 Hz / 32-bit float / stereo。当前探测的独占完整组合为空，44100 Hz 独占选择结果为 unsupported；这不等于共享输出不可用。
- 沿用现有共享兼容路径：无法找到驱动接受的完整格式时保持实际 Windows 格式，保留 automaticFormat=true，报告 `No driver-supported complete format was found; keeping the Windows format.`。本轮没有新加独占转共享回退、没有关闭音量保护、没有按远程设备名称禁用功能、没有永久覆盖音质偏好。
- 无 UI 代码变更。暂停仍释放输出，MCP 的 actualDevice=null、resampled=false，设备默认格式单独呈现；当前曲琥珀整行高亮等界面规则未修改。
- MCP 仍为 24 工具。更新 get_output 描述、set_output.device Schema 说明及 mcp.md；原输入字段/类型/校验兼容。开发和正式版共用平台服务，无开发专用接口或开关。本轮未重新执行 NativeAOT 发布验证。

## 验证结果

- Release 针对性测试：137 通过，2 跳过，0 失败。覆盖 ID 归一化、完整格式/临时恢复、输出切换、失效端点重试、独占格式选择及 MCP 契约。
- 新增 fake 端点回归覆盖远程端点无独占格式组合、有/无共享格式控制能力时仍共享播放、自动偏好保留、暂停续播和不写音量；远程与本地固定端点失效后均保持保护、恢复后重试同一设备。已有格式写入失败/正常本地路径回归通过。
- 两个默认跳过项中，只读真实端点测试以 `JEEKMUSIC_TEST_ENDPOINT_READ=1` 单独运行并通过：默认、完整 ID、wasapi ID 均读到同一设备及能力，失效 ID 不回落默认。另一项会修改真实设备格式的测试未执行；本轮远程设备无可选独占格式，也未对本地物理设备做实机回归。
- 真实新客户端 PID 27348，Session 1，`D:\MyOpenSource\JeekMusic\output\JeekMusic.exe`。生产 MCP 播放原曲后 3 次采样位置为 2.4409139 → 5.450786 → 8.439864 秒，playing=true、outputPending=false、blockReason=null、volume diagnostic=null；暂停后 outputState=idle，续播推进到 11.359964 秒。
- 播放时真实输出 44100 Hz / 16-bit / stereo，WASAPI 客户端 float，未重采样；仍明确显示自动格式匹配能力限制警告，不能把这个警告解释为仍播放失败。暂停摘要为 `OGG · 44.1 kHz 共享 · 输出空闲`，无转换箭头/重采样误报。
- 设备音量为只读获取的 100%，未调用任何音量写入、未改系统安全/远程设置、未换设备、未调用队列编辑。脚本验证结束暂停原曲于 62.619781 秒，此时当前曲 ID、队列 1602 项/索引 0、随机模式及输出偏好保留。随后共享客户端出现本测试脚本之外的切歌/切源操作，最终只读快照为《怜悯 (Live)》ID 1174、周深艺术家队列 153 项、暂停于 15.348972 秒；保留该最新状态，不回写旧曲/队列。settings.json 逐键比较无变化。
- **队列顺序限制：** 首个 200 项分页比较发现重启前后的待播排列不同（例如第二项 181 → 1259），元数据/当前曲/总数相同；本轮未改队列或 UI 来源逻辑，后续日志明确出现脚本之外的多次切歌/切换艺术家来源，不能将跨段快照差异归因于重启，也不宣称顺序逐项保留。保存完整最终 153 项队列供后续诊断，未为掩盖差异而重建队列。
- 后续只读日志还记录了 48000 Hz 的《Make up forever (Live)》及《怜悯 (Live)》使用实际 44100 Hz 输出成功继续播放，转换警告未被隐藏。这些是共享客户端额外操作的日志证据，不算本脚本控制的独立用例。
- **真实 UI 阻断：** 修复前后重新调用 computer-use `sky.list_apps()` 均报 `Computer Use native pipe is unavailable: failed to connect native pipe: The system cannot find the file specified. (os error 2)`。无法操作原生窗口或获取真实截图；MCP 显示状态不能替代视觉验收。没有把 headless 测试算作真实 UI 通过。
- **听感限制：** 真实 WASAPI 状态与进度证明客户端已运行播放，工具无法确认远程扬声器实际可听声音，没有声称已听到。

## 本地证据

原始证据位于忽略目录 `desktop/artifacts/rdp-audio/`（不随 Git 提交，包含用户曲库路径）：

- `before-*.json`、`repro-*.json`：改动前诊断/日志、原设置复现，以及 settings/snapshot 备份。
- `rdp-audio.trx`、`endpoint-read.trx`：针对性单元测试和只读实机能力探测。
- `after-playing-{0,1,2}.json`、`after-playing-output.json`、`after-paused-output.json`、`after-resume2-status.json`：真实播放、暂停和续播证据。
- `after-tools-list.json`、`after-logs.json`、`final-diagnostics.json`、`final-settings.json`、`final-complete-queue.json`：最终生产 MCP、日志、配置和完整队列。
