# R10 修改器开发交接

## 当前状态

| 项目 | 位置 |
|---|---|
| 源码 | v0.9.2 后的随机宝箱去重修复。开发分支 `codex/component-optimizations`，默认分支 `codex/initial-import` |
| 本地玩家包 | `artifacts/R10Trainer-v0.9.2-win-x86.zip`。构建提交和配方指纹见包内 `build-info.json` |
| 多版本构建输入 | `artifacts/patch-compatibility-inputs/<版本 ID>/Rance10.ain`。`bugfix` 和 `japanese-original` 目录含各自的 EXE 与 INI |
| 中文 BugFix 实机清单 | `artifacts/live/v0.9.2-sandbox.json` 记录源目录、隔离存档和组件指纹。`v0.9.2-integrity.json` 记录源文件校验及临时副本清理结果。 |

[安装、构建与 MCP](README.md) · [发布记录](https://github.com/Misakura-R1n/R10Trainer/releases) · [维护规范](AGENTS.md)

## 验证

| 范围 | 本地证据 |
|---|---|
| 中文 BugFix 第一部随机宝箱 | `artifacts/live/drop-dedup/live.jsonl`。原样 `full` 组件开出三个新候选，开启「＋1」后领取两张并回读卡表。定向实机用例固定出箱和候选池，在已持有 Lv40 卡拉、Lv30 赫尔曼骑士等八个版本时，开出 Lv35 卡拉、Lv33 赫尔曼骑士和餐券。两张新卡领取后均能回读，关闭连接后开关复位。同目录 `identity.json` 记录隔离存档、组件指纹、复用副本还原和源文件校验。 |
| 两部去重与八版安装恢复 | `artifacts/bridge/drop-dedup-build.log`。离线测试覆盖两部的权重、同箱占位、固定和消耗型奖励、空池补位及关闭开关。x86 辅助进程覆盖候选选择和读档清理。八版安装及逐字节恢复通过，符号链接用例因 Windows 权限不足跳过。 |
| 中文 BugFix 读档与排队 | `artifacts/live/v0.9.2-player-live.jsonl`。同进程读档后拒绝此前排队的回合写入和锁定，新档保持第 0 回合且未锁定。读档期间的普通和持续写入由 `artifacts/bridge/20261112-review-fixes-data-probe.log` 模拟覆盖。 |
| 中文 BugFix 战斗恢复 | `artifacts/live/v0.9.2-mcp.jsonl`、`v0.9.2-player-live.jsonl`。测试入口调用游戏引擎生成中断存档，原样 `full` 组件通过 `Reload.asd` 进入 `ResumeLoad`。实测 AP 全满、双方 HP、同回合重复行动、越界和旧目标拒绝。战斗与胜利结算中拒绝卡片请求，回到地图后可读取 1,134 张卡。存档指纹见 `v0.9.2-suspend-identity.json`。 |
| Resume 与八版安装恢复 | `artifacts/bridge/20261112-r2-validation.log`。AIN 离线测试覆盖恢复场景、战斗对象和选择器判定，x86 辅助进程覆盖战斗规则与 HP 编辑的重新就绪。八版逐字节恢复通过。符号链接用例因 Windows 权限不足跳过。 |
| MCP 独立进程 | `artifacts/bridge/20261112-review-fixes-mcp.log`。重复键返回解析错误，后续请求正常。 |
| v0.9.2 玩家包 | `artifacts/bridge/v0.9.2-package-build.log` |
| AIN 离线校验 | `artifacts/bridge/full/` 和 `artifacts/bridge/full-compatibility/` 下的 `bridge-build.json` |
| 普通战斗与 AP | `artifacts/live/battle-features-gameplay.jsonl`。对应场景、附加测试条件和组件身份见 `battle-features-fixture.json`、`battle-features-integrity.json`。 |
| 地图、资源、HP 保护与宝箱 | `artifacts/live/feature-audit-gameplay.jsonl`、`feature-audit-published-map.log`、`feature-audit-published-resources.log` |
| 卡片获取、共享 ★ 和存读档 | `artifacts/live/card-access-live.jsonl`、`card-management-live.jsonl` |
| v0.9.0 战后卡片读取故障 | 中文 BugFix 运行中只读核对。桥接心跳正常，胜利结算已退出，`g_battleContext` 保留，`cardContext` 为 0。 |

## 待验证

- 用中文 BugFix 隔离存档核对从结算或选卡场景直接恢复时的卡片管理限制及退出后的卡表读取。
- 用第二部隔离存档补测通用卡去重的实机表现。
- 核对「宝箱卡牌可获取＋1」在低阶宝箱、困难难度和已有周目奖励时的实机效果。
- 用各自的隔离存档核对中文 BugFix 之外七版的启动、连接、战斗写入和卡表读取。
- 补测完整食券重抽与自动战斗重配交互，以及致死、毒伤、反射和简易战斗的 HP 保护。
