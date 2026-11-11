# R10 修改器开发交接

## 当前状态

| 项目 | 位置 |
|---|---|
| 源码 | v0.9.0。开发分支 `codex/component-optimizations`，默认分支 `codex/initial-import` |
| 本地玩家包 | `artifacts/R10Trainer-v0.9.0-win-x86.zip`。构建提交和配方指纹见包内 `build-info.json` |
| 多版本构建输入 | `artifacts/patch-compatibility-inputs/<版本 ID>/Rance10.ain`。`bugfix` 和 `japanese-original` 目录含各自的 EXE 与 INI |
| 中文 BugFix 实机清单 | `artifacts/live/battle-features-sandbox.json` 记录源目录、隔离存档及指纹 |

[安装、构建与 MCP](README.md) · [发布记录](https://github.com/Misakura-R1n/R10Trainer/releases) · [维护规范](AGENTS.md)

## 验证

| 范围 | 本地证据 |
|---|---|
| v0.9.0 回归与八版安装恢复 | `artifacts/bridge/v0.9.0-validation.log`。覆盖选卡数量、周目奖励、回滚故障重选和 x86 检查。符号链接用例因 Windows 权限不足跳过。 |
| v0.9.0 玩家包 | `artifacts/bridge/v0.9.0-package-build.log` |
| AIN 离线校验 | `artifacts/bridge/full/` 和 `artifacts/bridge/full-compatibility/` 下的 `bridge-build.json` |
| 普通战斗与 AP | `artifacts/live/battle-features-gameplay.jsonl`。对应场景、附加测试条件和组件身份见 `battle-features-fixture.json`、`battle-features-integrity.json`。 |
| 地图、资源、HP 保护与宝箱 | `artifacts/live/feature-audit-gameplay.jsonl`、`feature-audit-published-map.log`、`feature-audit-published-resources.log` |
| 卡片获取、共享 ★ 和存读档 | `artifacts/live/card-access-live.jsonl`、`card-management-live.jsonl` |

## 待验证

- 核对「宝箱卡牌可获取＋1」在低阶宝箱、困难难度和已有周目奖励时的实机效果。
- 用各自的隔离存档核对中文 BugFix 之外七版的启动、连接、战斗写入和卡表读取。
- 复测普通战斗的三项越界输入及最终组件。此前越界检查在辅助进程中超时。
- 补测完整食券重抽与自动战斗重配交互，以及致死、毒伤、反射和简易战斗的 HP 保护。
