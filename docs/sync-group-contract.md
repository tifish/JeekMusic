# 多设备同步 P0：共享合同基础 revision 1

本阶段实现跨 Windows／Android 的纯函数和共享夹具，不连接现有 LAN 服务、数据库、用户数据 format 2、文件比较或自动调度，不发布新的 capabilities。完整 format 3 envelope、认证、持久化和网络交互仍属于后续阶段，不能据此宣称多设备同步已可用。总体路线见[实施计划](multi-device-sync-plan.md)。

实现：[C#](../desktop/src/JeekMusic.Core/Sync/GroupSyncContract.cs)、[Kotlin](../mobile/app/src/main/kotlin/com/tinyfish/jeekmusic/data/sync/GroupSyncContract.kt)。结构定义见 [JSON Schema](../shared/contracts/sync-group-v1.schema.json)，共享行为见[测试夹具](../shared/testcases/sync-group-contract.json)。revision 1 表示基础合同版本，不是传输号、文件协议号或用户数据 format。

## 数字、身份与文本

- 所有 JSON 整数字段仅接受整数 token，拒绝小数／指数 token、字符串、布尔和 null；范围 0–9007199254740991。generation、originSequence 从 1 开始。
- logicalCounter 范围 0–2147483647。溢出时 physicalMs 加 1、counter 归零；若 physicalMs 也耗尽，返回 clock_exhausted，不回绕。
- groupId、collectionId、originDeviceId、deviceIncarnation 均为 1–64 个 ASCII 字符，首字符为小写字母或数字，其他字符可包含 `-`、`_`。按 ordinal 比较，大小写不归一化。旧身份的兼容由后续迁移明确处理，不能悄悄改写。
- 文本必须是有效 Unicode，不接受孤立 UTF-16 代理项。key／path 不含 U+0000–U+001F 和 U+007F；payload 可含控制字符。长度预算统一按 UTF-16 code unit 计数，两端相同。
- key 1–4096 code units；payload 最多 1048576 code units，允许空串。JSON Schema 提供结构和上界，整数 token、代理项、UTF-16 长度与路径语义仍由运行时检查。它不替代传输层已有帧和字节预算。
- 当前入口读取已经解析的 JSON 对象，不是完整 JSON 文本解码器；重复属性、原始帧大小和深度须由后续网络解码层在调用前拒绝。未知或缺少的合同字段立即拒绝。

## 版本与逻辑时钟

版本对象固定为：

```json
{"physicalMs":100,"logicalCounter":0,"originDeviceId":"pc-a","deviceIncarnation":"boot-a","originSequence":1}
```

按 `(physicalMs, logicalCounter, originDeviceId, deviceIncarnation, originSequence)` 逐项比较。返回 -1、0、1，不按显示名、当前接收设备或遍历顺序决胜。来源标识为最后三个字段；同一组内相同来源标识不得对应不同的时钟、key 或 payload。generation 是接收流上下文，不是重新产生业务版本的理由；只有协调节点的受控快照重建可以将原业务版本装入新 generation，旧客户端不能自行重标请求。

`NextClock(local, now, observed?)` 使用三者 physicalMs 的最大值。如果 local 和 observed 都等于最大值，取两者 counter 的最大值加 1；否则取达到最大值的那个已有时钟的 counter 加 1；只有 wall clock 单独较新时 counter 为 0。没有 observed 时不参与远端比较。墙上时间倒退不会倒退逻辑时钟。

时钟不是文件 mtime，也不证明真实编辑时间。调用方必须把时钟、originSequence 和业务变更原子持久化，接收时持久合并 observed clock 后才能确认。纯函数没有代替持久化，也不自动分配来源序号。普通进程／设备重启保持 incarnation；计数器丢失后必须使用新 incarnation，不能从 1 重用旧来源标识。

## 曲库引用

```json
{"collectionId":"collection-a","path":"Album/song.flac#02","cueTrackNo":2}
```

path 是保留大小写和 Unicode 原貌的逻辑相对路径，用 `/` 分隔；不做 URL 解码、Unicode 规范化或物理路径推导。拒绝空组件、起止 `/`、`.`、`..`、反斜杠、冒号、`<>"|?*` 和以空格／句点结尾的组件。上限 1024 code units。cueTrackNo 可省略；出现时必须是 1–99 的整数，不能是 null。路径里的 `#02` 是普通文件名，音轨身份由单独字段表示。

逻辑引用按 collectionId、path、cueTrackNo 分别比较，不能用简单分隔符拼接造成歧义。该校验不是文件写入授权：平台保留名、大小写碰撞、卷身份、reparse／symlink、实际目录边界及选择范围仍由原平台文件服务检查。不会仅凭逻辑路径验证通过就允许写磁盘。

## 不可变寄存器变更与合并

```json
{"groupId":"group-a","generation":1,"key":"tag/song/mood","version":{"physicalMs":100,"logicalCounter":0,"originDeviceId":"pc-a","deviceIncarnation":"boot-a","originSequence":1},"payload":"{\"value\":\"CALM\"}"}
```

key 在该组内唯一标识一个合并单元；具体歌单、标签、规则等 key 的编码和 payload 的业务 Schema 留给 format 3 定义。本例只是基础合同示例，不是已定稿的业务 key。payload 是不透明字符串，生产方编码一次，接收方与转发方保留其解码后的字符串原样，不解析后重排再写出。相同语义的不同 JSON 文本仍被视为不同载荷，避免两端数字、属性顺序等规范化差异。

payload=null 表示墓碑；payload 里的 JSON null、空串和墓碑不同。组合同只按版本选择每个 key 的最大变更，按 key 的 ordinal 顺序返回，墓碑不按时间过期。用户显式重建使用更新版本，旧的存活记录重放不能覆盖墓碑。

一次 Merge 接受最多 4096 条输入，包括重复项；输入和调用方声明的 groupId/generation 必须一致。先验证输入集内每个来源标识与完整变更相符，即使该变更后来被覆盖也不能复用它；重复的同一条记录幂等。返回结果不修改输入。

Merge 只保留每个 key 的赢家，**不充当持久去重索引**。跨批次被覆盖的来源标识仍须由后续服务的事务日志／去重账本验证。原始来源序号存在间隙时，不能用“已见过最大的 originSequence”跳过其他 key 的较小序号；接收顺序水位与来源序号不同。

## 检查点与删除压缩边界

检查点属于一条主 PC 的接收流：`(groupId, generation, cursor)`。服务器持有 `compactedThrough` 与 `head`，满足 `0 <= compactedThrough <= head`。cursor 表示已持久应用到的接收序号，不是文件时间或单一来源的 originSequence。

| 条件 | 结果 |
|---|---|
| 组不同 | group_mismatch |
| 请求 generation 比服务器旧 | reset_required |
| 请求 generation 比服务器新 | invalid_checkpoint |
| 同 generation，cursor 小于 compactedThrough | reset_required |
| 同 generation，cursor 大于 head | invalid_checkpoint |
| 同 generation，compactedThrough <= cursor <= head | 可继续读取 cursor 之后的记录 |

`SafeCompactionThrough(head, memberAcknowledgements)` 返回所有有效成员确认序号的最小值；离线成员也必须列入，未确认以 null 表示并阻止推进。空成员列表返回 0，不把缺少成员记录当作全部确认；任何确认大于 head 都拒绝。该函数的参数没有成员身份，调用方必须从同一组、同一 generation 的完整认证成员表构造，不能传活跃连接列表。

这个结果只是允许考虑压缩接收日志的位置，**不是删除所有旧墓碑的许可**。真正压缩还要原子保存包含当前状态、墓碑或等价重放抑制信息、确认水位和 generation 的快照，验证持久结果后才能删除旧日志。当前 P0 没有快照存储、日志删除或清理音乐的实现。

移除成员或主 PC 接收流重建时需要提升 generation，旧成员先 reset 再对账，不能把旧快照简单重标为当前 generation。本地未确认修改先保存，再经过受控重新加入流程处理，不自动复活旧数据。

## 稳定错误码

| 错误码 | 含义 |
|---|---|
| invalid_contract | 字段／类型／范围／路径／Unicode 不合法 |
| clock_exhausted | 逻辑时钟无法再递增 |
| origin_reused | 同一输入集内来源标识对应不同变更 |
| group_mismatch | 对象／检查点来自另一组 |
| reset_required | 需要重新初始化当前组的检查点 |
| invalid_checkpoint | 请求来自未来 generation 或水位超出流尾 |
| batch_limit | 输入超过 4096 条 |

这些错误不包含凭证、文件内容或用户标签。后续业务层可以增加设备／映射上下文，不能改变错误所代表的确认边界。

## 验证与后续工作

105 条共享夹具覆盖版本排序、数值／类型拒绝、时钟倒退／溢出、路径／CUE 身份、墓碑重放、不同 key 的来源序号间隙、身份复用、组隔离、检查点及压缩边界。两端另做合并输入的全排列、内存／JSON 往返、上限和无效 Unicode 测试，并回归现有 format 2 合并。

```powershell
dotnet test desktop/tests/JeekMusic.Core.Tests -c Release --filter "FullyQualifiedName~GroupSyncContractTests|FullyQualifiedName~SyncMergeTests"
cd mobile
.\gradlew.bat :app:testDebugUnitTest --tests com.tinyfish.jeekmusic.data.sync.GroupSyncContractTest --tests com.tinyfish.jeekmusic.data.sync.SyncMergeTest --console=plain
```

本阶段没有 UI 或 Android 运行时行为变化，JVM 测试不作为真实客户端多设备验收。现有 MCP 工具集和参数 Schema 保持原样，不增加无法执行同步的占位工具；MCP 演进仍按[实施计划](multi-device-sync-plan.md#7-界面与-windows-mcp)交付。

P0 后续接入条件：完成 format 3 的业务 key／payload Schema 与 bounded envelope、原始 JSON 解码约束；在 P2 的持久服务中实现来源账本、原子 outbox／回执、成员确认及快照压缩。未完成前不发布 syncGroupProtocol 或 userDataFormat=3 能力。P1 的多连接改造可复用这里的身份和引用约束，但不得将现有 format 2 自动接入新组。

本轮验证结果：Windows 新旧同步合同回归 119 项通过；Android 对应 JUnit 测试 13 项通过（其中共享测试执行全部 105 条夹具及合并排列）；Windows 日常 Release ReadyToRun 发布成功，Android debug／release 包构建成功。Windows 保存当前状态后重启，生产 MCP 的 43 个工具目录及状态请求正常；Android 调试包仅在 emulator-5584 覆盖安装并重启，确认进程启动。此记录不包含多设备运行时功能或 UI 验收，未操作实体手机。
