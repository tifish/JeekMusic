# JeekMusic 桌面端

规划中的 Windows 本地音乐播放器，与移动端（[`../mobile/`](../mobile/)）遵循相同的原则：以文件夹为中心、不损音质、轻快。

## 技术选型

- UI：**Avalonia 12**（.NET，Native AOT）。验证结果见 [docs/tech-spike.md](docs/tech-spike.md)：启动约 370ms、20000 行表格 60fps 滚动、SMTC 可用、发布约 42MB；中文输入法人工检查通过，确定采用
- 播放：**libmpv**。验证结果见 [docs/tech-spike.md](docs/tech-spike.md) 第二部分：独占模式不重采样，FLAC / CUE 无缝且逐样本一致，播放时 CPU 2% 以下；正式发布前要自己编一个只有音频的 LGPL 版（现成的是 GPL，121MB）
- 标签：TagLib#；智能标签：ONNX Runtime 跑 YAMNet
- 与移动端互通：读写同一份 `.jeekmusic/backup.json`；艺术家拆分、文件名推断、CUE、LRC 等规则用两端共用的测试用例保证一致

需求和设计文档定稿后放在 `desktop/docs/`。`spike/`（界面）和 `spike-audio/`（播放内核）是技术验证用的一次性工程。
