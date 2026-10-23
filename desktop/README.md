# JeekMusic 桌面端

Windows 本地音乐播放器，与 [Android 端](../mobile/)共用曲库规则和同步协议。界面使用 Avalonia 12，发布为免安装的 Native AOT 程序；播放使用自行编译的音频版 LGPL libmpv。

界面默认使用简体中文。

已实现文件夹曲库、艺术家和专辑浏览、播放列表、搜索和智能标签筛选、WASAPI 输出和独占模式、队列、托盘与快捷键、迷你播放器、桌面歌词、在线歌词与封面、多设备同步、自动更新及中英文界面。在线歌词和封面默认关闭。

同步文件位于音乐目录的 `.jeekmusic/sync/`，每台设备仅写自己的 JSON 文件。同步工具负责传输目录；播放器负责按修改时间合并数据。旧 `backup.json` 可以导入，新版本不再覆盖它。

常听的艺术家可以用艺术家页标题旁的图钉固定到左侧导航栏，也可以右键艺术家或单首歌曲选择“固定到导航栏”（合作歌曲可分别选择艺术家）。固定项支持拖动排序、右键取消固定，重启后保留，并随导航顺序同步到其他设备。

歌曲列表的标题旁显示封面缩略图，支持内嵌封面、同名图片、同专辑共用封面和目录中的 `cover.jpg` 等图片；没有封面时显示音符占位。列表只加载屏幕内的封面，手动更换或下载封面后会刷新。

需求：[requirements.md](docs/requirements.md)。设计：[design.md](docs/design.md)。当前验证结果及未验收项：[validation.md](docs/validation.md)。性能目标和设备兼容性仍以验证报告为准。

## 开发与验证

需要 Windows、.NET 10 SDK、Git Bash，以及 Native AOT 链接所需的 Visual Studio C++ 工具。

日常测试直接编译并运行仓库根目录的 `output/JeekMusic.exe`。Debug 和 Release 共用该目录，最后一次编译覆盖其中的程序；框架和运行时版本不再增加子目录。运行所需的 DLL、模型和许可证也会一并输出，复制测试版本时请复制整个 `output/` 目录。正在运行时，先退出客户端再重新编译。

```powershell
dotnet build desktop/src/JeekMusic.App
./output/JeekMusic.exe
```

```powershell
& 'C:/Program Files/Git/bin/bash.exe' desktop/tools/get-libmpv.sh --lgpl
dotnet test desktop/JeekMusic.slnx
dotnet publish desktop/src/JeekMusic.App -c Release -r win-x64 -o desktop/artifacts/publish
python desktop/tools/collect-licenses.py desktop/artifacts/publish
```

发布包需包含 `licenses/`、`Models/yamnet.onnx` 和原生 DLL。`collect-licenses.py` 从已还原的 NuGet 包收集许可证，不发送网络请求。LGPL DLL 的对应源码和编译参数随 [libmpv Release](https://github.com/tifish/JeekMusic/releases/tag/libmpv-lgpl-0.41.0-20261021-3) 提供；桌面 CI 下载失败时会停止，不能回退到 GPL 内核。

后台运行测试使用独立的临时数据目录：

```powershell
pwsh desktop/tools/test-app.ps1 -Data "$env:TEMP/jeekmusic-smoke" -Seconds 15 -GracefulExit
```

测试脚本默认运行 `output/JeekMusic.exe`；验证发布包时可用 `-Exe desktop/artifacts/publish/JeekMusic.exe` 指定程序。

`--background` 不激活窗口，截图通过 PrintWindow 获取；测试脚本检测到抢占焦点会失败。`JEEKMUSIC_DATA` 覆盖数据目录，单实例通信也按此目录隔离。正式数据默认放在 `%LOCALAPPDATA%/JeekMusic`。

`tools/prepare-benchmark.py` 可生成独立的五万首合成曲库。`spike/` 和 `spike-audio/` 保留早期技术验证工程，不是正式播放器。
