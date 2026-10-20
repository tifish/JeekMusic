# JeekMusic 桌面端

规划中的 Windows 本地音乐播放器，与移动端（[`../mobile/`](../mobile/)）遵循相同的原则：以文件夹为中心、不损音质、轻快。

## 技术选型

- UI：**Avalonia 12**（.NET，Native AOT）。验证结果见 [docs/tech-spike.md](docs/tech-spike.md)：启动约 370ms、20000 行表格 60fps 滚动、SMTC 可用、发布约 42MB；只剩中文输入法要人工确认，不过关就改用 WinUI 3
- 播放：先用 **libmpv**（WASAPI 独占、无缝播放），以后需要更细的控制再换成 FFmpeg 解码 + 自己的 WASAPI 输出
- 标签：TagLib#；智能标签：ONNX Runtime 跑 YAMNet
- 与移动端互通：读写同一份 `.jeekmusic/backup.json`；艺术家拆分、文件名推断、CUE、LRC 等规则用两端共用的测试用例保证一致

需求和设计文档定稿后放在 `desktop/docs/`。`spike/` 是技术验证用的一次性工程。
