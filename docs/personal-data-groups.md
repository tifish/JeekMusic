# 个人数据同步组（P2）

本阶段把个人数据扩展为一个主 PC、多台成员 PC 和多部手机。Windows 主动文件同步与集合绑定属于 P3，不能据 P2 宣称完整的多 PC 曲库同步已交付。

## 使用

在主 PC 的设置 → 同步创建组并启动 LAN 服务。其他 Windows PC 使用一次性邀请添加连接，再加入此 PC 的组；Android 选择已配对主 PC 后点击加入组。每台设备同时加入一个个人数据组，只有一条主 PC 上游。没有文件映射也能同步个人数据。加入后停用旧 format 2 和目录交换；不提供新旧格式桥接。

支持歌单、四维手工标签及撤回、歌词偏移、双方支持的规则和导航项目。歌单以整份为合并单位，并发修改确定性选出一个版本，被替换版本沿用本机恢复历史。演唱语言保留 AUTO/MANUAL 及恢复自动状态；分析缓存按分析器版本和歌曲元数据分别保存。学习数据复用 protocol 3 的原事件身份和持久清除边界，经主 PC 转发，不生成新的本机学习答案或自动启用模型。

## 持久化和传播

Windows 追加数据库迁移，Android Room 9→10 增加 `sync_group_row`。来源序号、HLC、outbox、接收检查点与业务投影共用同一事务。规则文件的待应用意图先入库，提交后执行，重启可恢复。

每条修改保留 `(originDeviceId, deviceIncarnation, originSequence)` 和 HLC。初次加入保留已有编辑时间，不将旧全量快照伪造为刚发生的编辑。主 PC 分配接收流分页序号，转发不更换来源版本。重复来源不同载荷、来源序号缺口明确拒绝。只有核对来源序号和元数据指纹的有效回执才清理 outbox；指纹不读取音频。

固定快照每成员最多一个，15 分钟失效。每页最多 200 项，SQL 页另有约 1 MiB 字节预算；单载荷最多 512 KiB。分组存储最多 500,000 行／256 MiB，单组最多 100,000 个键、64 个有效成员。超限明确失败，不清理未确认修改。

墓碑保留。`compact` 等待全部有效成员（包括离线成员）确认，只压缩安全范围的接收日志和来源重放索引。成员移除提升 generation，旧检查点返回 `reset_required`。`clockAheadMs` 报告已观察时钟领先量；确定性排序不代表校准了设备真实编辑先后。

## 引用和缓存

`root:`、`peer:`、`collection:`、`device:` 均精确匹配，不回退到同名文件。主 PC 经已认证响应发布根别名；只有原配对根对应关系才能转换为同一逻辑引用。其他引用保留设备命名空间，不根据目录名推导身份。

歌曲尚不存在时保留歌单条目、标签、语言和分析载荷，歌曲出现后重试投影。缓存导入验证大小、时长、CUE 起点和支持的分析器版本。根离线或缓存未导出不生成删除。没有增加全库音频哈希或音频分析。

## 恢复

主 PC 先选择“允许此成员恢复检查点”，成员再执行恢复。恢复只重新编号已知未确认 outbox，保留原 HLC 和载荷；原始记录保留到恢复回执确认。授权及恢复响应丢失可重试。

被移除成员不能通过普通 `group_join` 自行激活。不用主 PC 快照抹掉未确认编辑，也不把旧全量状态重新导入为新编辑。忘记本机连接不代替远端撤销；凭证只保存在本机私有配置，不进入普通载荷和诊断输出。

该恢复入口要求本机仍保留组身份及已知 outbox。全部分组数据库／设备身份丢失时，应恢复匹配版本的本机备份，不能从普通歌单快照重建来源历史或自动复用旧身份。

## 协议与 MCP

TLS TCP 仍为 v4，文件协议仍为 3；新增 `syncGroupProtocol=1`、`userDataFormat=3`。组 RPC 为 `group_join/info/push/pull/snapshot/snapshot_rows/ack/recover`，来源身份必须属于已配对成员和当前 incarnation。

- `sync_peers`：`list`、`pair(invitation)`、`forget(peerId)`，最多 32 个脱敏连接。
- `sync_groups`：`status/create/join(peerId)/sync/pause/resume/remove(actorId)/allow_recovery(actorId)/recover/compact`。

严格校验 action 参数组合，拒绝无关字段。日常／正式版使用同一完整业务服务和工具集。组暂停保留本地编辑并阻止组个人数据网络交换，文件任务另行控制。

## 验证入口

共享夹具为 `shared/testcases/sync-group-transcript.json` 和 `sync-group-business.json`，覆盖来源指纹、原始时钟、回执、快照、删除重放、严格 JSON 和缓存版本。

```powershell
python desktop/tools/test-sync-groups.py --exe <独立发布的JeekMusic.exe> `
  --evidence desktop/artifacts/sync-groups-four-devices `
  --android emulator-5584 emulator-5586 --windows-ui
```

脚本只接受模拟器，先备份数据库、配置和配对信息，结束后恢复。邀请不写日志；恢复失败时才在忽略提交的 artifacts 目录保留私有恢复档。跨真实主机的防火墙、独立时钟和网络验证必须单独记录，本机多进程不能替代。

2026-11-17 实际通过：正式 NativeAOT 两台 PC 与 `emulator-5584`、`emulator-5586` 在无文件映射时的双向歌单、删除、静默轮次、离线积压、组暂停、成员移除／显式恢复及学习清除边界转发；Windows 创建／暂停／恢复／成员页和 Android 加入页均实际操作并检查截图。证据在本机 `desktop/artifacts/sync-groups-four-devices-final`；脚本已验证恢复后的全部私有文件内容一致。

回归通过 Windows LAN／组／MCP／存储 218 项、Core 组合同 114 项、Android 同步与引用 JVM 45 项，以及模拟器 9→10 迁移／共同回滚测试。Windows 日常 R2R、正式 NativeAOT 及 Android debug/release 构建成功。语言、分析缓存及绑定后真实歌曲的端到端匹配仍需随 P3 曲库副本验收补齐；本次未做跨真实主机验收。
