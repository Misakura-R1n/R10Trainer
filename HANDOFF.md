# R10 修改器开发交接

## 当前状态

| 项目 | 位置 |
|---|---|
| 源码 | v0.9.1 后续修订。开发分支 `codex/component-optimizations`，默认分支 `codex/initial-import` |
| 本地玩家包 | `artifacts/R10Trainer-v0.9.1-win-x86.zip`。构建提交和配方指纹见包内 `build-info.json` |
| 多版本构建输入 | `artifacts/patch-compatibility-inputs/<版本 ID>/Rance10.ain`。`bugfix` 和 `japanese-original` 目录含各自的 EXE 与 INI |
| 中文 BugFix 实机清单 | `artifacts/live/battle-features-sandbox.json` 记录源目录、隔离存档及指纹 |

[安装、构建与 MCP](README.md) · [发布记录](https://github.com/Misakura-R1n/R10Trainer/releases) · [维护规范](AGENTS.md)

## 验证

| 范围 | 本地证据 |
|---|---|
| 读档写入保护与排队 | `artifacts/bridge/20261112-review-fixes-data-probe.log`、`artifacts/bridge/20261112-r2-queue-probe.log`。模拟验证读档期间写入为 0，排队中的数值和锁定请求跨代次失效。 |
| Resume 与八版安装恢复 | `artifacts/bridge/20261112-r2-validation.log`。AIN 离线测试覆盖恢复场景、战斗对象和选择器判定，x86 辅助进程覆盖战斗规则与 HP 编辑的重新就绪。八版逐字节恢复通过。符号链接用例因 Windows 权限不足跳过。 |
| MCP 独立进程 | `artifacts/bridge/20261112-review-fixes-mcp.log`。重复键返回解析错误，后续请求正常。 |
| v0.9.1 玩家包 | `artifacts/bridge/v0.9.1-package-build.log` |
| AIN 离线校验 | `artifacts/bridge/full/` 和 `artifacts/bridge/full-compatibility/` 下的 `bridge-build.json` |
| 普通战斗与 AP | `artifacts/live/battle-features-gameplay.jsonl`。对应场景、附加测试条件和组件身份见 `battle-features-fixture.json`、`battle-features-integrity.json`。 |
| 地图、资源、HP 保护与宝箱 | `artifacts/live/feature-audit-gameplay.jsonl`、`feature-audit-published-map.log`、`feature-audit-published-resources.log` |
| 卡片获取、共享 ★ 和存读档 | `artifacts/live/card-access-live.jsonl`、`card-management-live.jsonl` |
| v0.9.0 战后卡片读取故障 | 中文 BugFix 运行中只读核对。桥接心跳正常，胜利结算已退出，`g_battleContext` 保留，`cardContext` 为 0。 |

## 待验证

- 用中文 BugFix 隔离存档复测普通战斗、结算和选卡后的卡表恢复。从战斗内的暂停存档恢复时，核对卡片限制、战斗开关和手动 HP 编辑。
- 核对「宝箱卡牌可获取＋1」在低阶宝箱、困难难度和已有周目奖励时的实机效果。
- 用各自的隔离存档核对中文 BugFix 之外七版的启动、连接、战斗写入和卡表读取。
- 复测普通战斗的三项越界输入及最终组件。此前越界检查在辅助进程中超时。
- 补测完整食券重抽与自动战斗重配交互，以及致死、毒伤、反射和简易战斗的 HP 保护。
