# JeekMusic

简洁的本地音乐播放器：围绕你选择的音乐文件夹组织曲库，不损音质，不做任何统计或追踪。

<p>
<img src="mobile/fastlane/metadata/android/zh-CN/images/phoneScreenshots/1.png" width="200">
<img src="mobile/fastlane/metadata/android/zh-CN/images/phoneScreenshots/2.png" width="200">
<img src="mobile/fastlane/metadata/android/zh-CN/images/phoneScreenshots/3.png" width="200">
<img src="mobile/fastlane/metadata/android/zh-CN/images/phoneScreenshots/4.png" width="200">
</p>

## 目录结构

| 目录 | 内容 | 状态 |
|---|---|---|
| [`mobile/`](mobile/) | 移动端（Android，Kotlin + Jetpack Compose）：独立的 Gradle 工程，包含自己的文档（`mobile/docs/`）、开发脚本（`mobile/tools/`）和商店元数据（`mobile/fastlane/`） | 1.0 已发布，见 [Releases](https://github.com/tifish/JeekMusic/releases) |
| [`desktop/`](desktop/) | 桌面端（Windows） | 技术选型中 |

两端各自独立构建和发布：`.github/workflows/mobile.yml` 只在 `mobile/` 有改动时运行。

## 许可证

[MIT](LICENSE)
