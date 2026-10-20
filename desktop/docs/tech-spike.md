# 桌面端技术验证：Avalonia

> 2026-10-20 · 验证工程：`desktop/spike/`（一次性代码，正式工程另建）

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

## 下一步

- 播放内核验证：libmpv 的 WASAPI 独占、无缝播放、CUE
- 写 `desktop/docs/requirements.md` 和 `design.md`，再建正式工程
