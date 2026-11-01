# 运动识别固定宽松（Rules9）

2026-11-01 起，Windows 和 Android 不再提供运动识别灵敏度选择，所有生产路径采用宽松：模型 ln(动感/舒缓) 阈值2.75，旧声学规则阈值.50。历史NORMAL/STRICT枚举保留为兼容输入，其有效阈值也等于LOOSE。Windows读取/写入历史settings.json字段会规范为LOOSE；Android读取历史DataStore时返回LOOSE，兼容写入也保存LOOSE。普通同步不提供调节入口；无论旧输入从哪里进入，分类器的枚举阈值均固定。

Rules8的持续模型证据与独立音乐脉冲联合分支保留。追加采样的接近阈值和片段分歧只围绕2.75；已达到宽松阈值时不再为了历史严格阈值补采脉冲。窗支持率、解释的margin和运动最终判定均按同一宽松阈值。脉冲不是独立正例，缺材料仍可无结果，不把失败解释为负例。

规则版本从8升到9，分析版本仍为6。Windows启动及领域重算复用已有有效特征；Android规则升级触发一次缓存重算。签名匹配的完整v5证据在不需要脉冲时可无解码晋升v6。不会因这次策略调整清空普通分析缓存或重提取全库；原本过期、失败或不完整的音频仍按原分析任务规则处理。人工覆盖最终优先，ClassicalTitles仍优先于GENRE自动结果，人声、古典、安静及个人候选部署门槛不变。

MCP Schema拒绝旧`sensitivity`额外参数；描述和只读解释明确固定LOOSE。Debug与NativeAOT共用服务和目录。`MOOD_NORMAL`支持率字段是已弃用的技术兼容别名，值与新增`MOOD_LOOSE`相同。

共享契约用例验证NORMAL/STRICT/LOOSE输入判定相同。两端新增采样边界测试；Windows服务测试覆盖旧持久化设置规范化、有效缓存原样复用、手工覆盖和标题优先。完整构建和验收记录见[single-dataset-acceptance.md](single-dataset-acceptance.md)。
