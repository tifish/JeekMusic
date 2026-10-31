# 运动诊断 v1：实现与实验（2026-10-31）

后续生产改进见 [运动 v8](workout-music-v8.md)；本文保留 Analyzer 5 / Rules 7 的历史实验记录。

本轮完成生产链路的按需解释能力，没有改变运动规则。Analyzer 5、Rules 7、三档阈值、ClassicalTitles 强制古典、手工覆盖优先、安静 v7 与用户 LOOSE 设置均保留。没有把反馈写成手工覆盖，没有按歌曲或艺人设置分类分支，也没有强制全库重解码。

## 新实现

`AnalysisDiagnostics.Run` 是正式 Windows 公共服务，AnalysisProbe 和 MCP 共用。它观察 `TrackAnalyzer` 的同一轮生产取样、追加、重试与 `AudioTagger` 推理：

- 每个独立片段复用生产 16k mono 解码与 11.025k 特征重采样，直接读取 `FeatureExtractor` 的同一个频谱通量起音包络。没有拼接片段，也没有用新的 FFT 算法冒充原 BPM。
- 输出完整包络（hop 256/11025 秒）、频谱起音时间、幅度和相邻起音间隔。幅度只表示频谱起音强弱，不是人工标注的音乐重音；起音间隔不等于拍间隔。
- 输出 40–240 BPM 内每个整数 lag 的重叠序列 Pearson 自相关、生产 BPM 先验权重，以及同一 lag 在四个独立四分段内的相关性。原生产 BPM 继续另列；其峰比不是概率。全候选能复核半拍、倍拍及竞争峰，没有根据歌名或目标标签选择倍频。2.5 秒四分段的低速相关估计较短，不能声称已完成拍号、节拍跟踪或感知重音辨识。
- `AudioTagger.AnalyzeObserved` 在实际 ONNX 输出处复制每窗 521 类响应，不从五组分数反推。默认生产调用不复制原类数组，不执行节奏诊断。
- 每窗列出源文件 start/end、原类数组、energy/calm 和运动 log-ratio。group membership 直接来自生产 `YamnetScores`。均值贡献仅用于解释；分类实验始终逐窗取 log-ratio 再取中位数。
- 输出模型 SHA256、类表 SHA256、采样率/窗长、逻辑轨边界、生产 evidence、decode/model 尝试数、总耗时、模型加载耗时、额外节奏计算及 observer 耗时。诊断 contract 是 `analysis_diagnostics_v1`，`cacheCompatible=false`；没有新增可导入的 features 字段。

随程序嵌入的 [TensorFlow YAMNet 原类表](https://github.com/tensorflow/models/blob/master/research/audioset/yamnet/yamnet_class_map.csv) 统一 LF 换行后的 SHA256 为 `b03d48f9ebe23f69ea825193de7e736934086a12410f0af47babe897b78bc0d3`，加载时验证哈希及 521 类数量；许可证沿用 `desktop/licenses/YAMNet.txt`。本轮模型 SHA256 为 `83bfd4629ffcfc65edf4f2aeae78510136fc5fbfeba572548b64ce75f058587b`。

Energy 12 类：Exciting、Happy、Dance、Electronic、Hip hop、Rock、Drum kit、Drum、Rapping、Disco、Pop、Electronic dance music。Calm 6 类：Tender、Sad、Lullaby、New-age、Ambient、Background music。原类 sigmoid 响应互不排斥，不是总和为 1 的概率分布。

## 使用与预算

```powershell
dotnet build desktop/tools/AnalysisProbe -p:_IsPublishing=true -p:SelfContained=false -p:PublishAot=false
# 路径仅传给本地进程，不写入诊断 JSON；输出放在已忽略的 artifacts 目录。
AnalysisProbe.exe <音频路径> <逻辑起点毫秒> <时长毫秒> --diagnostics
python desktop/tools/evaluate-workout-diagnostics.py desktop/artifacts/motion --output desktop/artifacts/motion/evaluation.json
# 可选 --cache-db <明确指定的 library.db>：mode=ro，只导出计数，不导出曲库身份。
```

MCP `diagnose_analysis` 在普通托管版与 NativeAOT 中完全相同：

```json
{"action":"start","trackId":1210,"budgetSeconds":60}
{"action":"status"}
{"action":"result","segmentIndex":0}
{"action":"cancel"}
```

start 必须明确一个 trackId，无全库/当前列表范围。预算 15–180 秒，默认 60；立即返回，单任务防重入，完成后只保留一个内存结果直到下一次 start/退出。status 只返回元数据、类表及生产 evidence，不复制原始 521 数组；result 按片段取原始诊断。status/cancel 只接受 action，result 只接受 action/segmentIndex。离线或文件签名已变化拒绝，结束前再次核查录音身份。

生产选段最多 50 秒，至多 5 段；每模块每段最多 3 次尝试，最坏重复解码 150 秒音频，解码单次仍有 45 秒超时且受总取消 token 限制。总预算在解码、模型窗和计算边界协作取消，不承诺强占式实时中断。cancel 丢弃部分诊断，deadline 报 `budget_exhausted`，失败仅报告异常类型，释放任务槽后可显式重试。无需降低桌面后台保护或改用户分析偏好。MCP 断开不取消已显式启动的任务，它仍受预算约束，可从另一连接 status/cancel。

不播放、不修改音频、不写音频临时文件、不上传、不写缓存/标签/审核覆盖、不改队列或筛选。诊断原始输出和包含私有路径的本地操作日志均留在已忽略的 `desktop/artifacts/motion/`；只提交六条反馈身份与标签来源记录及汇总结论。

## 六录音新证据与具体结果

[反馈记录](../shared/testcases/workout-feedback.json) 保存用户指定录音的 trackId、标题/专辑/艺人、时长/格式、稳定键 SHA256、用户原话和生产 MCP 核查状态。六条均 `used_for_tuning=true, independent=false`；《和光同尘》明确为运动正例，早期负例假设作废。不是自动加载的覆盖表或独立准确率集。

实际取样 24×10 秒、240 个 0.975 秒窗；各窗尾部余量没有拼接。新推理逐首复现既有中位数。三档中当前 LOOSE 阈值为 2.75，自动结果如下：

| 录音 | 用户标签 | 段/窗 | 中位数 | 各段最长连续过线窗数 | 自动结果 |
|---|---|---:|---:|---|---|
| 漫漫 1210 | SPORT | 3/30 | 0.928012 | 0/1/1 | NON_SPORT，未修复 |
| 初醒 1148 | SPORT | 3/30 | 1.766806 | 4/0/1 | NON_SPORT，未修复 |
| 江湖觅知音 1206 | SPORT | 5/50 | 2.548547 | 6/1/9/1/2 | NON_SPORT，未修复 |
| Zoobi Doobi (Live) 1127 | SPORT | 5/50 | 2.697290 | 3/1/9/0/10 | NON_SPORT，未修复 |
| 和光同尘 1155 | SPORT | 3/30 | 0.171693 | 0/0/0 | NON_SPORT，未修复 |
| 浮光 1212 | NON_SPORT | 5/50 | 1.486546 | 0/5/0/0/2 | NON_SPORT，符合反馈 |

段顺序沿用生产 evidence：30%/50%/70%，追加时再列 10%/85%。六首另外三个维度均 AUTO VOCAL/NON_CLASSICAL/NON_CALM；生产签名匹配、完整、无待重算和手工覆盖。

### 原类贡献

六首 Drum kit+Drum 的全窗平均响应依次为 0.001693、0、0.000781、0.000156、0.001302、0.000156。模型在这些混合音乐窗中的鼓类响应很弱；这只能证明模型响应弱，不能据此断言录音没有鼓或定位到量化前模型的原因。

Energy 平均响应中 Pop 占比依次约 29%、70%、62%、64%、49%、88%。《浮光》有高 Pop 响应，说明组和确实混入曲风代理；《和光同尘》Lullaby/Tender 平均响应约 0.0311/0.0148，显著抬高 calm 分母。这与用户“可运动”的标签可以同时成立，不能把 calm 反标签直接当运动真值。

《漫漫》组外有 Music for children/Jingle/tinkle/Christmas 等主要响应；Zoobi Doobi 的 Music of Asia/Bollywood 很强。这些是诊断发现，不能变成特定曲风或身份的运动规则。完整每窗原类留在本地可复核。

### 节奏与持续性

《漫漫》三段可见约 44.6/89.1/172–185 的竞争；约 89 BPM 并不唯一决定应当倍频。《浮光》首段约 80.7 的相关性 0.582，四分段约 0.54/0.65/0.51/0.67，比《漫漫》的若干约 89 BPM 段更持续；《和光同尘》后两段同时存在约 76/152 的峰，生产 BPM 本身由约 76 跳到约 152。因此不能只对运动正例选快的一倍，也不能把“周期稳定”当运动偏好。

`evaluate-workout-diagnostics.py` 固定比较以下可解释实验，不搜索最优阈值、不生成生产规则：

- 任一片段连续 3 个过线窗：初醒/江湖/Zoobi 转正，同时错误收进浮光；漫漫/和光仍不通过。
- 连续 3 窗并且同段存在持续周期（相关 >=0.3、四分段至少 3 个 >=0.2）：本组六首与上一实验相同，仍误收浮光。此筛选只描述周期，不等同音乐拍点。
- 至少两段各连续 3 个过线窗：仅江湖/Zoobi 转正，浮光保持负；仍无法解释漫漫/初醒/和光。这是相关调参样本上的候选，没有足够误检约束支持上线。
- 原 energy 再加全部未包含的 156–178 打击乐类，逐窗重新算中位数：依次 1.146/1.767/2.549/2.709/0.422/1.645，六首均未过线。父子类别重叠会重复计数，故该实验只是宽松的“漏加打击乐能否补救”反事实，不是校准新分组。

缓存只读影响审查覆盖 1,932 首当前签名、完整 v5 记录，原 LOOSE 自动 SPORT 133 首。任一段 3 窗会新增 202 首，两段 3 窗新增 96 首；没有其他人工标签，这些是改变范围而非正确率。缓存没有新的包络/原类证据，没有假装为这些歌提供节奏融合评价。

可定位的瓶颈是：当前运动代理主要反映风格/情绪响应，鼓类很弱；节奏周期与运动偏好也不等价。进一步可支持的修复需要有误检约束的音乐拍点/节奏表征，而不是再松一次现有门槛。本轮已排除上述具体路径，没有证据支持把这些候选直接部署。

## 验证与兼容

- Windows App 针对性测试 86 项通过；Core（分析/同步）23 项通过；Python 诊断评估 3 项通过。包含真实 ONNX 原类与组响应一致、静音/噪声、半倍拍歧义、短/持续活动、坏输入、重入/取消/15 秒预算耗尽、结果隔离、原有重试/坏窗/版本兼容和覆盖优先。
- 共享 v5 用例新增 4/5/6 个强窗的中位数边界，两端同读；没有新增缓存 contract、版本迁移或后台全库重算。Windows 生产阈值及 Android 分类实现不变。
- Android Debug 和 androidTest 构建通过，JVM 104 项通过；API 35 x86_64 模拟器真实 MediaCodec/TFLite 控制音频测试 1 项通过（8k mono、11.025k stereo、48k 六声道及非零 CUE 窗）。未测 ARM 实机、手机性能/功耗。
- 普通托管与 Release NativeAOT 实际 MCP 均验证新诊断：3 段/30 窗、521 原类重组一致、原缓存/标签保持、坏参数拒绝、无播放；原 `set_smart_tags`/`explain_analysis`/审核及事务回滚回归均通过。初次 NativeAOT 链接因 vswhere 不在 PATH 失败，仅补入构建进程 PATH 后发布成功；已有第三方 DataGrid trim/AOT 警告保留。
- 当前桌面条件延后了隔离 fixture 的自动后台分析，测试以明确的 `--diagnostics-only` 参数运行并报告 `backgroundAnalysisVerified=false`；默认测试仍要求自动后台完整结果，不放宽原验收门槛。通过的是显式诊断调用的真实生产解码/推理，不把延后的后台流程说成通过，也没有关闭保护。
- 最后一轮六首进程内总分析约 3,951ms（单首 544–803ms，排除进程启动和 JSON 输出），其中重复提取逐段节奏约 1,157ms；这是诊断开销，不是生产默认延迟或手机性能数据。没有为诊断扩展生产采样。
- 本轮无 UI 修改；没有声称新增 UI 交互/视觉验收。最终客户端替换会在停止前重读生产状态，保留当时歌曲、队列、进度、播放状态及筛选，再通过新客户端 MCP 复核。
