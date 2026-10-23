# 桌面端技术验证

> 2026-10-20 · 一次性的验证工程，正式工程另建：界面 `desktop/spike/`，播放内核 `desktop/spike-audio/`

# 一、界面：Avalonia

## 结论

**采用 Avalonia 12。** 能自动测量的几项（启动、内存、表格滚动、SMTC、AOT 下的 DataGrid 排序）全部通过，中日韩文字在 150% 缩放下显示清楚；中文输入法经作者人工检查也没有问题（2026-10-20）。

## 环境

- Windows 11 Pro for Workstations，显示缩放 150%，60Hz
- .NET 10 SDK（10.0.300），Avalonia 12.1.4，Avalonia.Controls.DataGrid 12.1.2（MIT）
- Native AOT（win-x64），GPU 渲染（Avalonia 默认，经 ANGLE）

## 结果

| 项 | 结果 | 说明 |
|---|---|---|
| 发布体积 | 约 42MB（不含 .pdb） | exe 23MB + libSkiaSharp 11.6MB + av_libglesv2（ANGLE）5.4MB + libHarfBuzzSharp 1.8MB；不需要安装 .NET 运行时 |
| 冷启动到首帧 | 约 370ms（第一次运行 660ms） | 20000 行合成数据，从进程创建到第二个 `RequestAnimationFrame` 回调 |
| 内存 | 工作集约 125MB，私有约 150MB，托管堆 18MB | 大头是 Skia / ANGLE 的 GPU 资源，不是 .NET 本身 |
| 表格滚动 | 60fps：帧间隔中位数 16.6ms，p95 约 17ms，6 秒内超过 2 倍间隔的帧 0–8 个 | 每帧往下滚 3 行，`ScrollIntoView`。真实曲库（1602 首）结果相同 |
| 软件渲染（`--software`） | 工作集 82MB、私有 56MB，但滚动只有 30fps，Mica 失效 | 不采用；以后如果在意后台内存，可以考虑最小化到托盘时释放窗口 |
| 中日韩文字 | 清楚，字体回退自动生效（中文是微软雅黑，韩文是 Malgun Gothic） | 日文假名用默认字体时间距偏松（如 シャルル），正式工程要指定字体回退顺序（Segoe UI → Microsoft YaHei UI → Yu Gothic UI → Malgun Gothic）；日文汉字用的是中文字形（中日韩统一汉字的常见问题，可接受） |
| Mica | 生效（`ActualTransparencyLevel = Mica`），可以随时开关 | DataGrid 表头默认是不透明的黑底，正式工程需要调样式让背景透出来 |
| SMTC | **完全可用**：系统会话管理器能看到本程序（`JeekMusic.Spike.exe`）和歌名，从系统发出的"播放 / 暂停"能收到 `ButtonPressed` | 通过窗口句柄取得（`SystemMediaTransportControlsInterop.GetForWindow`），CsWinRT 投影在 AOT 下正常；不需要 MSIX 打包 |
| DataGrid 排序（AOT） | 能用 | 发布时 DataGrid 有裁剪警告（IL2104），保留本程序集类型（`TrimmerRootAssembly`）后排序正常。排序规则是系统的中文排序，正式工程用移植过来的 SongSorter（拼音 A–Z） |

## 用法

```bash
cd desktop/spike
dotnet run -c Release                                   # 交互（合成 20000 行）
dotnet run -c Release -- --library 'D:\Mobile\Song'     # 用真实曲库的文件名
# AOT 发布（需要 VS 的 C++ 工具；在 Git Bash 里要先把 VS Installer 目录加入 PATH，ILCompiler 要用 vswhere）
dotnet publish -c Release -r win-x64 -o ../publish/spike
../publish/spike/JeekMusic.Spike.exe --measure-startup --scroll-bench --smtc-test --out result.txt
```

参数见 `SpikeOptions.cs`。

## 人工检查

1. **中文输入法（决定性）：已通过（2026-10-20）**。检查内容：在搜索框里用微软拼音输入，检查：候选窗口是否跟着光标，没有跑到屏幕角落；拼写过程中输入框里的拼音和下划线显示是否正常；按空格上屏、按数字选词、按 Esc 取消、按 Shift 切换中英文是否正常；上屏后筛选是否立刻生效（状态栏显示每次筛选的耗时，没有自动测过）
2. Mica 在浅色 / 深色切换（右上角下拉框和系统设置都试一下）时是否正常，有没有闪烁
3. 点"播放选中的歌（SMTC）"后，Win11 音量弹窗里是否出现媒体卡片，键盘媒体键是否能切换卡片上的播放 / 暂停
4. 窗口拖到另一块不同缩放的显示器上，文字是否立即变清楚


# 二、播放内核：libmpv

## 结论

**libmpv 能满足要求，采用。** 独占模式下设备跟着文件的采样率走，不重采样；FLAC 和 CUE 分轨无缝而且逐样本一致，MP3 无缝也没有错位；跳转约 1ms；播放时 CPU 占用在 2% 以下。要处理的问题有两个：现成的 libmpv 是 **GPL** 版而且有 121MB（含全部视频功能），正式发布前要自己编一个只含音频、LGPL 的版本（见"许可证和体积"）；CUE 分轨的结束时间要比分轨点早 1 微秒。

## 环境

- libmpv：shinchiro/mpv-winbuild-cmake 20261020（mpv v0.41.0-1224，FFmpeg N-127734），`get-libmpv.sh` 下载
- 输出设备：默认设备 "Speakers (3- Monitor 09)"（显示器的 HDMI / DP 音频），共享模式下系统混音格式是 48kHz float
- 测试音频由 `make-testdata.py` 生成：连续信号（扫频 + 两个正弦，左右声道不同），在不对齐任何编码帧的样本位置（321987、654321）切成 3 段

## mpv 选项

`Mpv.BaseOptions()`：`config=no`、`load-scripts=no`、`video=no`（内嵌封面也是一路"视频"，不解码）、`volume=100`、`replaygain=no`、`audio-pitch-correction=no`。不设 `audio-samplerate`、`audio-format`、`af`，mpv 就不转换格式。独占模式是 `audio-exclusive=yes`，运行中切换要执行 `ao-reload`（重开设备，会停顿一下）。

## 结果

| 项 | 结果 | 说明 |
|---|---|---|
| 共享模式 | 44.1k / 96k / 192k 的文件都被转成 **48kHz float** | 由系统混音器做，和 Android 上不开浮点输出的情况一样 |
| **独占模式** | 设备格式跟着文件走：44.1k → 44100Hz，48k → 48000Hz，96k → 96000Hz，192k → 192000Hz；16 位和 24 位都以 s32 送出（补零，无损） | 日志：`Trying stereo s32 @ 96000hz (exclusive) -> ok`；设备周期 10ms |
| FLAC 无缝 | **逐样本一致**（882000 个样本，0 个不同），`gapless-audio=yes` 和 `weak` 都是 | 用 `ao=pcm` 把送给设备的样本写成文件，再用 `analyze.py` 和原信号比较 |
| MP3 无缝 | 总长度正好，两个分界处没有错位（偏移 0），残差和别处一样 | MP3 是有损的，不能逐样本比较；编码器延迟和补齐都按 LAME 头裁对了 |
| **CUE 分轨** | `loadfile 文件 append -1 start=6,end=13.4` 这种方式：6.0 秒处精确，**13.4 秒处多出 1 个样本**（分轨点上的样本前后两轨各放了一次） | 13.4 不能用浮点数精确表示。`end` 比分轨点早 1 微秒（小于 192kHz 的半个样本）后**逐样本一致**。正式工程的 CueResolver 要这样传时间 |
| 跳转 | 中位数 0.2–1.3ms，最慢 10ms | 发出 `seek` 到收到 `playback-restart`；设备缓冲另外约 10ms |
| CPU（只有播放内核） | 96k/24 FLAC 噪声和 MP3：0–1% 单核；WASAPI 共享 / 独占输出：0–1% | AOT 版，无窗口；Windows 的 CPU 计时精度是 15.6ms，这么低的数字只能看量级 |
| CPU（带窗口） | 空闲 0.1%；播放时最小化约 1.7%，窗口显示（每 250ms 刷新进度）约 2.9% | 正式工程：最小化或隐藏时停止刷新界面，只更新 SMTC；**不要观察 `time-pos`**，它每秒变几十次，每次都会让界面重绘（一开始这样做时 CPU 是 12%） |
| 内存 | libmpv 本身约 3MB 工作集；带窗口播放时整个进程约 90–115MB | 大头是界面（见第一部分） |
| SMTC | 和真实播放连起来了：歌名、艺术家、专辑来自 mpv 读到的标签，系统的播放 / 暂停 / 上一首 / 下一首 按钮直接控制 mpv | 需要人工确认（见下面） |
| AOT | P/Invoke（`LibraryImport`）在 Native AOT 下正常 | |

## 要注意的地方

- **独占模式下的音量**：端点软件音量对独占无效，硬件音量则同时作用于共享和独占；mpv 软件音量保持 100（睡眠渐弱例外）。正式工程统一用 `IAudioEndpointVolume`，明确检查硬件能力；无硬件音量时提示用物理旋钮并由用户按播放确认。当前实现与验证以设计 §7.4 和 validation.md 为准。
- **换歌时格式变化**：独占模式下，前后两首采样率不同（如 44.1k → 96k）就必须重开设备，中间会有一点停顿。专辑内部通常格式一致，影响不大
- **`gapless-audio`**：`weak`（默认）只在前后格式不同时重开设备，`yes` 会把后一首重采样成前一首的格式来保证不断开，这违背"不损音质"。所以用 `weak`
- **设备变化**：mpv 会监听默认设备的变化（日志里的 `Monitoring changes in device`）；拔插耳机、切换默认设备的行为留到正式工程里测

## 许可证和体积

- 这个 libmpv 编译时没有关掉 GPL（`mpv-configuration` 里没有 `-Dgpl=false`），**是 GPL 版**。JeekMusic 是 MIT 的，带着它发布，整个发布包就要按 GPL 提供。mpv 加 `-Dgpl=false`、FFmpeg 不开 `--enable-gpl` 就能编出 **LGPL 版**，动态链接不影响 MIT
- `libmpv-2.dll` 有 **121MB**，大部分是视频（libplacebo、Vulkan、各种视频解码器）。只保留音频解码器、解封装器和 WASAPI 输出的话，估计能小一个数量级
- 所以正式发布前要**自己编一个只有音频的 LGPL libmpv**（在 CI 里用 mpv-winbuild-cmake 或 MSYS2 编）。开发阶段先用现成的版本

## 用法

```bash
cd desktop/spike-audio
./get-libmpv.sh                 # 下载 libmpv-2.dll 到 native/
python make-testdata.py         # 生成 testdata/
dotnet run -c Release -- --test-info --test-formats --test-gapless --test-seek --test-cpu
python analyze.py               # 比较 --test-gapless 的输出
dotnet run -c Release           # 手动测试窗口；也可以 -- --folder <专辑目录>
```

`--test-formats` 和 `--test-seek` 会用静音文件以独占模式打开默认设备，其他程序的声音会中断一两秒。

在作者的电脑上跑窗口测试时，不能抢焦点：加 `--background`（不激活、不进任务栏、放到所有窗口后面），并且用 `ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true }` 启动（这是控制台程序，否则会弹出控制台窗口）。`--minimized` 最小化但不激活，`--measure-cpu <秒>` 测完 CPU 后退出，`--no-timers` 不刷新界面。

## 人工检查

用手动测试窗口（`dotnet run -c Release`，打开一张专辑）：

1. **无缝播放**：放一张连续的现场专辑或古典专辑，听换歌的地方有没有停顿或爆音；`weak` 和 `no` 对比一下
2. **独占模式**：勾上"WASAPI 独占"后，窗口底部的"设备"一行应该和"文件"的采样率一样；如果有外置 DAC，看它的采样率显示是否跟着变；这时其他程序应该没有声音
3. **SMTC**：音量弹窗里的媒体卡片显示的是不是当前这首歌，卡片上的按钮和键盘媒体键能不能控制播放、切歌
4. **CUE**：这个窗口不会解析 .cue，CUE 的效果只做了自动测试

# 下一步

- 写 `desktop/docs/requirements.md` 和 `design.md`，再建正式工程
- 正式发布前：在 CI 里编一个只有音频、LGPL 的 libmpv
