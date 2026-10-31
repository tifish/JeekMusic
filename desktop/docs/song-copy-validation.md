# 歌曲列表 Ctrl+C 验证（2026-10-31）

列表焦点内 Ctrl+C 复制选中行完整显示歌名。多选按当前 Rows 顺序，以 Windows 换行分隔；包括未呈现的虚拟化行，保留重复播放列表条目及不可用行的显示名。无选择不改剪贴板，文本输入框保留原生复制行为。使用表格隧道路由先于 DataGrid 默认复制，避免复制艺术家、专辑等其他列；剪贴板写入失败显示可重试提示。

MCP 继续用 get_track/search_tracks 获取歌名，新增只读 get_diagnostics.mainLayout.songCopy：快捷键、排序语义、选中数量、最近复制状态和成功数量；不读取剪贴板内容。工具数量仍为 28，输入 Schema 不变。

## 验证

- SongCopyTests + PlaybackShortcutTests：67 项通过。新增测试覆盖完整长中文/Unicode歌名、多选逆序选择仍按列表顺序、虚拟化行、单首、空选择、修饰键边界、搜索框选中文字及 MCP 诊断。补充 MCP 断言及清理测试警告后，3 项 SongCopyTests 重跑通过。
- 普通构建：0 警告、0 错误。为避免测试覆盖运行中的 output，测试以隔离 publish 路径、非 AOT/非自包含引用构建；初次未显式关闭自包含时遇到 NETSDK1151，已纠正参数后通过。
- NativeAOT 发布成功；本机最初未将 vswhere 所在目录加入 PATH，补齐本次进程 PATH 后通过。保留 DataGrid 上游 IL2104/IL3053 警告。
- 真实客户端 PID 3660：Win32 鼠标消息定位《蓝色降落伞》，Ctrl+C 实际系统剪贴板等于完整歌名；Shift+Up 多选后，Ctrl+C 实际输出为《灯火里的中国（国家大剧院合唱）》与《蓝色降落伞》，以 CRLF 分隔，无其他列。MCP state=copied、lastCopiedCount=2。实际窗口截图已目检，两行选中状态和当前曲播放边框正确。
- Windows 拒绝前台激活，最初 SendKeys 未到达列表；后改为向真实窗口发送 Win32 键盘消息并暂时附加目标线程键盘状态（结束即恢复）。没有将最初失败或 Headless 结果当作真实交互通过。先前鼠标多选尝试受播放期间视口变化影响，最终采用键盘扩选核对准确歌名。
- 重启时动态捕获状态：24440 → 3660，曲目 1290、761 项队列及顺序、播放位置约 6.55 秒、播放模式、音量、筛选恢复；手工覆盖与审核记录校验和不变。之后用户播放/自然切歌状态保留，未回滚至旧截图歌曲。

本地证据（忽略目录，不提交私人曲库信息）：

- `desktop/artifacts/song-copy/native-keyboard-multiselect.png`：真实客户端双选截图。
- `desktop/artifacts/song-copy/native-result.json`：真实剪贴板匹配结果。
- `desktop/artifacts/song-copy/daily-diagnostics.json`、`mcp-parity.json`：日常/NativeAOT 诊断与完整目录/Schema 对比。
- `desktop/artifacts/song-copy/build.log`、`external-research-restart-*-private.json`：构建及重启状态核对。

没有改音频文件、智能标签、播放逻辑或移动端。个人偏好方案另由独立任务提交，不包含在本次快捷键提交中。
