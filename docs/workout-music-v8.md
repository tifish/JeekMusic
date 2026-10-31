# 运动分类：Analyzer 6 / Rules 8

后续新阶段的重音层级、振幅攻击与局部周期受控实验见 [重音评估](workout-accent-evaluation.md)。该评估没有改变本页生产规则，也没有解决剩余三首漏判。

本次交付是部分漏判改善：保留原运动中位数规则，增加“持续模型支持 + 独立音乐周期支持”的保守分支。没有将拍点模型的速度当成人类感知主拍，没有按歌名、艺人或人工反馈表查询生产标签。没有新增模型权重或运行依赖。

## 人工目标与范围

用户标准为“主要是有明显节奏感，但节奏不能过慢”。补充反例：《东风破》节奏不慢，但整体悠扬慵懒；《Lucky》主鼓点太慢。歌词、激励情绪不是主要目标；《和光同尘》仍是明确正例。

准确版本、时长、原话保存在 `shared/testcases/workout-feedback.json`。全部八首均 `used_for_tuning=true`、`independent=false`。原六首同艺人，新增两个负例也参与了方案选择，不能作为独立测试成绩。没有给反馈写手工覆盖。

| 曲库 ID / 录音 | 人工反馈 | 本次 LOOSE 自动结果 | 状态 |
|---|---|---|---|
| 1210 漫漫 / 小深情 / 233090ms | SPORT | NON_SPORT | 仍漏判 |
| 1148 初醒 / 白日提灯影视原声带 / 216192ms | SPORT | NON_SPORT | 仍漏判 |
| 1206 江湖觅知音 / 183118ms | SPORT | SPORT | 新分支改善 |
| 1127 Zoobi Doobi (Live) / 聚划算55青春选择之夜晚会Live / 217486ms | SPORT | SPORT | 新分支改善 |
| 1155 和光同尘 / 大江大河2电视剧原声带 / 248660ms | SPORT | NON_SPORT | 仍漏判 |
| 1212 浮光 / 274608ms | NON_SPORT | NON_SPORT | 保持排除 |
| 1105 东风破 / 周杰伦 / 315481ms | NON_SPORT | NON_SPORT | 保持排除 |
| 1970 Lucky / Jason Mraz、Colbie Caillat / 191713ms | NON_SPORT | NON_SPORT | 保持排除 |

## 实际研究与许可

本地 Python 3.11.14 隔离环境，CPU 推理。只有下载依赖和公开权重，用户音频不离开本机；没有下载额外受版权限制的音乐数据，也没有训练六样本高维分类头。下面耗时及峰值是本机研究进程观测值，非手机性能承诺，峰值包括 Python 和模型运行时。

| 路线 | 官方来源与许可 | 本地开销与结论 |
|---|---|---|
| Beat This! | [官方仓库](https://github.com/CPJKU/beat_this)，固定 `b95c8ab0c58c2d9fcfd40508ae8dffbc05ac4f5c`；官方明确代码及发布权重 MIT | small0/1 各约 8.45MB，final0 约 81MB。small0 每 10s 约 0.14–0.16s，每 30s 约 1.2s，峰值 350–403MB；final0 每 30s 约 2.1s、497MB。许可可用于交付，但本次没有转换/打包进 Windows 或 Android。|
| HPSS | [librosa 官方文档](https://librosa.org/doc/0.11.0/generated/librosa.decompose.hpss.html)，ISC，无权重 | 打击乐能量占比单独失败；最终采用自行在 C#/Kotlin 实现的确定性软掩码和周期特征，无 Python/原生库部署要求。|
| Music2Emo / MERT | [官方仓库](https://github.com/AMAAI-Lab/Music2Emotion) `c5f7984f78d9928b841f25bd05a47377c12c58ca` 代码 MIT；底座 [MERT 模型卡](https://huggingface.co/m-a-p/MERT-v1-95M) 权重 **CC-BY-NC-4.0** | MERT 约 378MB，每 30s 约 3.2s，峰值 1.83GB。18 段实际离线运行；仅研究，未分发，也未视为 Android 可交付方案。|
| Demucs htdemucs | [Meta 官方仓库与许可](https://github.com/facebookresearch/demucs)，MIT | 权重约 84MB；每 30s 分离约 12–21s，峰值 1.62GB。用于检查鼓/伴奏，不适合此次移动端预算，未转换或分发。|
| Essentia 情绪/舞蹈代理模型 | [官方许可说明](https://essentia.upf.edu/licensing_information.html)：代码 AGPL、相关预训练权重 CC-BY-NC-SA | 官方权重站点 DNS 失败，未成功运行，不能列为实测结果；未进入产品。|

Beat This! 对原六首的片段估拍约为：漫漫 88、初醒 85/170、江湖 100、Zoobi 90/103、和光 76、浮光 120。后者更快，却是用户负例。更长 30s 上下文也不能消除层级竞争。Demucs 鼓/伴奏周期中和光出现 152、浮光出现 80，但东风破和 Lucky 也出现 146/130 的细分峰；不能仅给正例倍频。

Music2Emo 原六首中位 arousal 为 4.981 / 5.369 / 6.809 / 6.722 / 4.196 / 4.418，和光仍低于浮光；代理唤醒度不等于运动偏好。HPSS 能量占比阈值 .10/.15，在复用的 24 个旧粗标负例中分别放入 8/7 首。因此均未上线。

权重身份（SHA256）：

- small0：`6074be2c4d490c5f6101fcc374a1ec72ae93456e23bb6019783b849f5dc7d47b`；final0：`8c328b45f59d8dd3dff219253ff6a8d6482be57d0133a29140e2febbf8eb8331`，均核对官方 SHA1。
- MERT：revision `12af15fef9d0ac838c3f475bfbbf26d2060dd4f5`，`pytorch_model.bin` 377552987 字节，`a2b8b747f72c06e0595aeae41ae5473f4364938c6b39b2c58be38c48e6bd3fcd`。
- Demucs：`955717e8-8726e21a.th`，`8726e21a993978c7ba086d3872e7608d7d5bfca646ca4aca459ffda844faa8b4`。

## 生产规则与上下文

1. 原 `median(ln((energy+.003)/(calm+.003)))`，LOOSE/NORMAL/STRICT 阈值 **2.75/3.25/3.75** 不变，原来达到阈值的 SPORT 保留。
2. 新增分支要求至少两个不重叠上下文，各自对应的原模型片段均有至少 3 个连续窗口达到当前灵敏度阈值。绝不跨片段拼接强窗。
3. 每个对应片段中心补采连续 30s（短曲限制在逻辑曲长内），最多三个上下文，不越过 CUE 分轨边界，不拼接。原模型单窗约 .975s，原来最多五段/50s 的模型输入不变；长上下文只用于新节奏特征。
4. 16k PCM 使用固定 129-tap/441 相位 Blackman sinc 重采样到 11025Hz。HPSS：FFT1024/hop128，31×31 中值软掩码。提取 30–150Hz 低频与宽频起音包络；40–240 的候选周期取正 Pearson ACF × Fourier coherence，保留最多八个局部峰。
5. 每段需要低频 salience ≥.025、宽频 ≥.05，双方在 95–220 BPM 内且差 ≤3。此范围是测量证据的支持约束，**不是用户给定 BPM 下限，不是已识别的人类主拍**。同一算法应用全部歌曲，没有按标签选择半拍/倍拍。

早期 10s 脉冲上下文在真实 Windows 解码后不能稳定支持江湖，因此最终为 30s。保留独立模型强窗是为了拒绝“周期明显但整体舒缓”的反例，但它仍受 YAMNet 音乐类型代理偏差影响；这正是漫漫、初醒、和光仍未修复的原因之一。没有宣称已解决主鼓点与细分拍的普遍辨别。

## 迁移、失败和两端

Windows/Android 同步 Analyzer **6**、Rules **8**、节奏 contract **1**。现有模型权重及声学旧特征不变。完整、当前录音签名匹配的 v5 非候选可不解码晋升，明确 `baseEvidenceReused=true`、`rhythmState=not_needed`；候选保留旧结果，按原后台充电/电量/全屏等条件渐进补充节奏。v5 记录不能携带伪装的新脉冲字段，行版本和证据版本必须一致。

每模块最多三次重试，成功解码后的特征重试复用 PCM；取消传播到解码/计算循环。必要节奏失败时 `rhythmState=failed`，原模型已达 SPORT 仍可保留，否则 MOOD 无结果，不能把模型失败当成 NON_SPORT。`complete` 保持原模型/声学完整语义，节奏状态单独报告。显式 `analyze_tracks` 可重试失败结果；取消/预算不覆盖当前未提交歌曲旧缓存，已完成提交保留。

手工覆盖最终优先，ClassicalTitles 标题/专辑强制古典保留；独立 CALMNESS 和旧 QUIET 兼容不变；不改用户灵敏度。开发/NativeAOT 同为 28 个 MCP 工具，新增有界生产分析批次，不存在研究专用发布开关。[参数、写入范围及预算](../desktop/docs/mcp.md#运动分析-v6--rules-8)。

## 验证与证据边界

原六首及新增两首用于方案选择，没有重新拟合后的留出成绩。逐首/按专辑留出只有在外部训练时才构成测试，本次不以固定规则回看已知八首伪造交叉验证。旧 29 首对照（5 个粗标运动、12 个普通非运动、12 个安静）通过最终生产解码、模型和 30s 节奏重跑；LOOSE 为 5TP/2FP/22TN/0FN，与原中位数规则同两首粗标误检，没有新增。旧标签身份与审核 provenance 不完整，且部分录音与新反馈重叠，均只作回归控制，不能声称跨艺人泛化准确率。

早期 10s 代理的 ±6dB 增益、±2s 中心偏移保留江湖/Zoobi 的融合支持，但和光的节奏单独支持不稳定；不能将此宣称为最终 30s 方案的完整鲁棒性测试。最终生产版对从 96 个旧“两段强窗”候选中按 hash 选取的 24 首、13 位艺人重跑。含已知调参曲 Zoobi 一首，其余 23 首未知标签；其中 7 首从原中位数 NON_SPORT 改为 SPORT。原模型中位数与缓存一致。这个选择集富集了会变化的候选，不能外推全库比例；未知标签只报告改变数量，不制造误检/正确率。

固定生产 30s 证据的局部敏感性复算：两项 salience 门槛同时降低 20% 时江湖仍有 2 段、Zoobi 3 段；同时提高 20% 时江湖仅 1 段（重新漏判），Zoobi 仍 3 段。BPM 下界改为 90/100 时两首支持数不变。这个边界敏感性必须保留，不能把江湖描述为大裕量、已充分校准的改进；未按这些复算结果继续搜索阈值。

数值核对：C# 与 librosa 相同 PCM 的 24 段参考结果误差 ≤2e-5。共享合成用例验证持续强窗、独立上下文、灵敏度、失败无结果、标题和其他维度；真实模型测试与合成契约测试明确区分。

研究脚本：`desktop/tools/research-workout-{beats,rhythm,pulse}.py`，显式本地 manifest/权重输入，禁止导入生产缓存。manifest 是 `{track:{id,path,durationMs}, explanation:{evidence:{segments:[{start_ms,end_ms,...}]}}}` 列表，区间须为非 CUE 的相对源边界。生产验证用 `AnalysisProbe <audio> <start-ms> <duration-ms>`，输出诚实版本及三档分类；CUE 用真实逻辑起点。研究 JSON 不是生产可导入特征。

环境实际版本：torch/torchaudio 2.5.1+cpu、numpy 1.26.4、librosa .11.0、scipy 1.15.3、transformers 4.44.2、demucs 4.0.1、soundfile .13.1、einops .8.0。Beat This! 使用官方 minimal postprocessor；Music2Emo 调用官方 `predict`，MERT 改为固定本地快照、eval/inference_mode，离线开关禁止隐式联网；Demucs 使用 htdemucs、CPU、shifts=0、split=true，以同中心 30s 分离鼓/伴奏再提取上述包络。未将这些研究模型转换为 ONNX/TFLite。

所有私有音频、manifest、完整原类/包络、权重、机器路径均仅留在忽略的 `desktop/artifacts/motion/`，不提交或上传。该目录含本地研究及生产验证 JSON/日志，项目外复现需有合法可用的录音。研究依赖不会成为应用安装依赖。

Windows 生产六首冷进程实测：非候选基础分析约 .38–.53s，江湖约 5.0s、Zoobi 约 7.2s（含 2/3 个 30s 节奏上下文）。节奏大数组按单上下文有界分配并释放；移动端实测结果另列。

## 最终实测记录（2026-10-31）

- Windows Core 分析定向测试 19 项、App 分析/智能标签/MCP 定向测试 92 项通过；含真实 ONNX。Python 数值保护 8 项、分组评估 5 项通过；Android 全部 JVM 108 项通过。
- NativeAOT 发布成功，保留既有 Avalonia DataGrid trim/AOT 警告。托管版和 NativeAOT 分别运行真实 EXE/MCP 的 `test-workout-mcp.py`：28 工具、v5 复用、新脉冲、force 真实模型推理、原子回滚、取消均通过；另有原安静/手工优先回归通过。
- 新日常客户端实际 `analyze_tracks` 对八首执行默认升级，批次约 22.3s；六首非候选明确复用晋升，江湖与 Zoobi 补充生产脉冲并提交 SPORT。全部签名匹配、Analyzer6/Rules8、AUTO 来源，手工覆盖及审核表哈希、队列/播放未变。
- Android API36 x86_64 本地模拟器：八份原录音逐文件 SHA256 验证后，通过实际 MediaExtractor/MediaCodec、TFLite、生产 v6 全流程，八首结果与上表一致。江湖/Zoobi 约 23.3/24.0s，其他约 3.1–10.3s；整个三个仪器测试约 80s。没有上传，亦没有导入手机曲库。这不是 ARM 实机验收。
- 最终 Android 解码/CUE/TFLite、长 Vorbis 与 Room 迁移/手工覆盖/事务仪器回归共 11 项通过，约 8.6s；与上述八曲完整生产实验分开计数。
- 实测发现原 Android 解码器对长 Vorbis seek 返回短 0.15–0.30s 的 PCM，未放宽截断检测。现解码 1s preroll 后仍仅保留时间戳限定区间，不填充缺口；同八首取样恢复完整。并避免刚 start 就 flush 导致 codec-specific-data 丢失的生命周期风险（[Android 官方说明](https://developer.android.com/reference/android/media/MediaCodec#flush())）。非首个 seek 仍 flush；新增无版权 40s 合成 Vorbis 与重复 fresh/seek 回归。preroll 不是返回给模型的额外上下文，也不更改播放解码器。
- 本轮中主机发生意外重启，部分刚产生的 APK/日志/模拟器 manifest 损坏。没有把损坏日志算作通过证据；重新构建、CRC/SHA256 检查并重跑相关验证。重启原因未确定。
- 没有改布局。仍实际操作新 Windows 客户端的运动筛选和搜索并截图：江湖、Zoobi 各显示 1 首，浮光显示 0 首；随后清空搜索、恢复原筛选和导航。截图保存在忽略目录 `desktop/artifacts/motion/v6-sport-*.png`，未提交含个人曲库的截图。
- UI 测试触发客户端原有的“显示列表接续队列”行为，暂时追加了目录来源条目。结束验收后仅移除此次追加的尾部，恢复测试前最新的单项队列，保留当前和光同尘、随机/循环、暂停与位置；未恢复任务开始时的旧播放快照。

原三首漏判仍未解决。下一次新特征评估必须保留这三个正例及三个负例约束，扩充经过人类审核的主拍/整体动感证据，不能由现有代理估得更快就推翻用户标签。此次未承诺跨艺人误检率或普遍感知主拍准确性。
