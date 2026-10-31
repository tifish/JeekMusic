# 智能分类 v5 契约与验收记录

## 范围及保留规则

本轮落实按维度聚合、有界追加分析、预处理及失败可靠性、诊断/评估闭环。没有更换 YAMNet，也没有把速度或响度直接当运动标签。
**ClassicalTitles 两端原文件未修改，曲名/专辑名命中仍强制自动 CLASSICAL，手工覆盖最终优先。** 运动 LOOSE/NORMAL/STRICT 的 2.75/3.25/3.75 三档、用户设置和同步覆盖优先级保持不变。

v5 是可解释的候选规则，不是已经证明更准的模型。原来的分数校准针对均分；聚合及下混变化后需要独立人工复核，不能搬用旧成绩。`mobile/docs/design.md` §6 的既有逻辑回归、乐器加权、中位数实验是历史工作，本轮不宣称首次提出或已经证明模型上限。

## 预处理与采样

- 16,000 Hz、mono、有限 float、[-1,1]。先逐声道处理非有限值（置零）、夹紧，再算术平均。下采样沿用确定性的盒式平均；低于目标率用线性插值升采样。输出长度 floor(inputLength * target/source)。这是实用的确定性基线，不是高阶带限重采样器。
- Windows mpv / Android MediaCodec 保留原采样率和声道解码，再走上述相同算法。Codec 的 EOF/时间戳可能有数个样本误差；不伪造补零，裁掉超过请求终点的样本。原生低采样率、6 声道和非零起点有测试。
- 时间采用源文件毫秒半开区间。逻辑轨起点为 CUE start（普通歌为 0），终点受 CUE end 与 duration 双重约束。Android 精确裁剪跨边界输出 buffer，取消“失败则分析物理文件头 30 秒”的回退；无法跳转的格式明确失败，绝不拿别的子轨代替。
- <40 秒整首；其他歌曲取 30%/50%/70% 各 10 秒，40–50 秒内将重叠窗口向后推，容纳不了的窗口省略。基础最多 30 秒。
- 每个片段分别运行 15,600 样本（0.975 秒）的非重叠窗口；尾部不足一窗不推理、不跨接缝、不补零。<0.975 秒歌曲记录 `decode:insufficient_samples`。`sample_count`、`frames.length` 和位置可解释覆盖率/丢弃尾部。
- 特征提取在最长的有效独立片段进行，避免人为接缝起音。响度/动态范围是代理量，不是标准 LUFS/LRA；BPM 等诊断特征没有被当成新的运动硬规则。

## 各维度聚合与置信指标

每个片段保存原模型分组均分及每个有效窗的五组分数。

- 人声：以全体窗 vocal log-ratio 均值为基线；任一片段内连续 3 个窗口的最小 vocal 分数减 0.5 可以抬高该基线。这保留长间奏中持续出现的人声，孤立高分窗不能单独补救，且连续性不会跨拼接缝。它不是人声检测器的概率保证。
- 运动：取所有有效窗 energy/calm log-ratio 的中位数，再与原三档偏好阈值比较，需要持续动感，避免短暂强段主导均分。
- 古典：每段古典曲风/其他曲风 log-ratio 按有效窗口数加权。乐器类不作为古典加分；标题强制规则照旧在模型后生效。
- 诊断提供有符号阈值距离和样本支持比例。它们只说明所采片段的证据，不是校准概率，也不能证明未采样部分没有人声。UI 保持原二值语义。

## 第二轮、重试及失败

首轮若人声距离阈值 <=0.5、古典 <=0.35、运动距离任一档阈值 <=0.35，或片段分类冲突，或 <10 个有效窗/缺片段，则触发一次追加决策。候选按 10%、85%、0%、90% 顺序，截在逻辑轨内，排除所有已用区间及彼此重叠；最多两个 10 秒片段，绝不第三轮。短歌已全曲采样，不追加。

首轮 30 秒、追加最多 20 秒，通常最多 50 个模型窗；短歌最多 41 个。不对全库无条件扩大采样。模型正常但结论模糊也会保存结果和追加原因，不反复重排。第二轮任一片段失败时，保留首轮聚合，额外成功段也不单独改变标签，记录 `additional_failure_first_pass_retained`。

每片段最多 3 次尝试，退避 200/800ms。模型失败保留已解码 PCM，仅重试失败模块；已成功片段不重算。空/明显截短解码也会重试；先天不足一窗不盲目重试。特征模块同样最多 3 次。单次解码超时 45 秒；最坏损坏文件可能消耗多个超时，而非承诺固定毫秒总预算。取消在解码/窗/退避之间检查并传播，不保存“取消=失败”。桌面暂停条件在歌曲之间检查，退出取消当前歌曲；Android Worker 保留 300 首/8 分钟的批次上限（在歌曲边界检查）。

必需的基础片段或特征失败使记录不完整，保存模块错误类型、尝试数及已得到的证据；持久化 `analysis_attempts=3` 表示本签名/版本已经终止自动尝试。后续唤醒不会死循环。文件签名/分析版本变化或原有“重新分析全部”可以重启尝试。错误只保存模块/异常类别，避免把本地路径写入证据。

## 数据、同步和审核记录

- Analyzer / Rules 均升级到 5；Android Room 6→7 增加证据、终止尝试标记和本地审核表；Windows 追加数据库迁移创建审核表，不改旧迁移。
- 两端旧版本成功结果在签名匹配时继续按其旧规则显示，标记 legacy；不把旧分数伪装为 v5。后台正常条件下渐进重算。失败/缺模型/缺证据记录可重新排队，但终止记录不反复跑。
- v5 的 `analysis_evidence` 是同一个 snake_case JSON 字符串，随原 features JSON 同步。导入/导出只接受当前版本、模型分数齐全且证据完整的记录，继续做大小/时长校验，保留本地有效结果优先及所有手工覆盖逻辑。旧客户端因版本不同不消费 v5，不会把新聚合套到旧分数。
- Windows `SmartTagService.Override` 在同一事务保存自动/最终结果、版本、标题来源、证据、可选 reason 的前后快照。失败会与覆盖和物化标签一起回滚。Android setManual/clearManual 同样与审核快照事务保存，使用现有 recompute 流程。重命名时审核记录随稳定键迁移。
- 审核记录是纠正历史，不自动当独立真值；同步手工覆盖的优先级未变，审核历史当前仅保留本地，不作为新的同步冲突来源。

## MCP 与本地校准/评估

`explain_analysis {trackId, includeReviews?}` 只读调用同一个业务服务，返回 automatic/final/sources、classicalTitleMatched、版本/签名/完整性/重排状态、片段和窗、阈值距离/样本支持及预算。includeReviews 最多最近 100 条。`set_smart_tags` 增加可选 reason（<=500 字），其他参数兼容。两个工具在开发/正式版共用，无开发开关。

`desktop/tools/export-analysis-review.py --track-ids 1,2 --identity-salt <固定本地命名空间> --output <本地新文件.jsonl>` 最多读取 50 个指定 ID，调用生产 MCP，默认人工 labels 全部 `?`，不复制音频/路径，也不自动把手工结果当真值。导出内容包含审核信息，请保留在仓库外。歌曲/专辑/艺人 ID 使用同一个盐散列；需要人工统一不同发行版、别名和多艺人身份后才谈独立划分。

生产等价提取使用 Windows `desktop/tools/AnalysisProbe`：

```powershell
dotnet build desktop/tools/AnalysisProbe -p:_IsPublishing=true -p:SelfContained=false -p:PublishSelfContained=false
& desktop/tools/AnalysisProbe/bin/Debug/net10.0-windows10.0.19041.0/AnalysisProbe.exe <音频路径> <起点毫秒> <时长毫秒>
```

它直接复用生产 mpv、重采样、分片、追加和 ONNX 推理，输出 features/evidence、模型 SHA256 和 provenance，不扫描曲库、不播放、不写原音频。旧 `calibration-pcm.sh` / `yamnet_scores.py` 明确标记 legacy v4；原 FFmpeg 重采样和整秒截取不用于 v5 校准。

`mobile/tools/calibration/grouped_evaluate.py records.jsonl --output report.json` 接受已冻结 automatic 预测和人工 labels，默认按歌曲、专辑、艺人三种身份的连通分量防止传递泄漏；固定 seed 散列 75% calibration / 25% test，重复歌曲去重，冲突标签拒绝。支持 `--group-by song,album` 等显式策略，但歌曲始终必需。没有人工独立审核、生产 v5 分数 provenance 或必要身份的整组只进入 regression，规则生成/复用标注也不能进入独立 test。输出拆分 manifest、输入/工具 SHA256、每维混淆、precision/recall、未知和缺预测计数。不训练、不自动调阈值、不生成标签；测试标签须在调参期间封存。

## 当前实际证据与限制（2026-10-30）

- 共享合成用例验证孤立/持续人声、长间奏、跨缝不连续、孤立/持续动感；不是音乐准确率样本。
- 桌面生产 ONNX：100 秒受控 8kHz 正弦，3×160,000 样本、30 窗，首次约 356ms（含解码/模型加载/特征），没有触发追加；模型 SHA256 `83bfd4629ffcfc65edf4f2aeae78510136fc5fbfeba572548b64ce75f058587b`。
- Android 15 x86_64 模拟器 MediaCodec/TFLite：8kHz mono、11.025kHz stereo、48kHz 六声道，非零 CUE 起点避开静音文件头；每段 1 个真实模型窗，模型加载+推理约 221/16/24ms。非 ARM 实机成绩，不推断手机功耗或速度。三项 Room 迁移 4→5、5→6、6→7 已实测通过。
- 两套旧标注 109 / 1551 首，104 个同名重叠。现有 calibration NPZ 匹配到 108 首。防泄漏工具将其全部归 regression，calibration/test 均 0；缺的那首未补造数据。
- 旧分数回归：VOICE TP74 FP0 TN32 FN0 unknown2，precision/recall 1/1；MOOD TP4 FP0 TN68 FN1 unknown35，precision/recall 1/0.8；GENRE TP30 FP3 TN69 FN1 unknown5，precision/recall 0.9091/0.9677。这些是旧分数+旧规则复算，**不是 v5 成绩，也不是独立泛化成绩**。
- 本轮没有独立审核的真实音乐测试集，也没有从旧 NPZ 伪造片段位置或生产 provenance。因此不能声称误判率下降。下一轮应从导出的争议歌曲人工盲评并补足独立艺人/专辑组，再决定阈值或模型变化。
- 本轮无控件/布局变更。native computer APIs 在当前工具环境禁用；未做桌面交互截图，不能声称真实 UI 视觉验收通过。非 UI 逻辑用真实客户端 MCP 和原生推理测试验证。


最终针对性验证：Windows App 80 项、Core（含同步）34 项通过；Android JVM 100 项通过，Android 15 模拟器原生解码/TFLite 1 项、Room 迁移 3 项、DAO 7 项通过；Python 防泄漏工具 3 项通过。托管开发 EXE 和 Release NativeAOT 均通过真实 MCP 手工覆盖/审核/回滚测试。Release NativeAOT 的隔离受控曲库经正常扫描→后台分析→MCP explain 链路验证，3 段/30 窗/各 160,000 样本，约 132ms，无播放。AOT 首次构建因 vswhere 不在 PATH 失败，给该构建进程补充 VS Installer 路径后发布成功；第三方 DataGrid 的既有 trim/AOT 警告保留。

后续规则 v6 新增独立安静维度，分析器及 evidence contract 继续为 v5，无条件重解码不适用；见 [安静标签契约及验证](calmness-v6.md)。

规则 v7 的低代理分数改进和单录音扩采对照见 [calmness-v7](calmness-v7.md)。`AnalysisProbe` 可追加 `--sampling-comparison`，对至少 100 秒录音比较现有首轮/有界追加、3×20s 和 6×10s；复用生产解码/模型，但输出明确不可写入缓存的诊断计划，生产采样与 v5 契约不变。分组评估把 `provenance.used_for_tuning=true` 的整个关联组强制留在 regression，即便误填 independent=true 也不能进入独立成绩。

按需逐段起音、自相关竞争及同窗原始 521 类解释已实现为独立诊断 contract，不改变 v5 缓存与 v7 分类；见 [运动诊断 v1 实验与验证](workout-diagnostics-v1.md)。
