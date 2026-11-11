# R10 修改器开发交接

## 版本与发布

| 项目 | 当前配置 |
|---|---|
| 开发版本 | v0.7.0，C# / .NET 10 / WPF，`win-x86` |
| 游戏版本 | 中文 BugFix 版，原始文件指纹见 [README](README.md#支持范围与限制) |
| 源码 | 开发分支 `codex/component-optimizations`，默认分支 `codex/initial-import`。[私有仓库 R10Trainer](https://github.com/Misakura-R1n/R10Trainer) |
| 最近发布 | 玩家包 `R10Trainer-v0.6.3-win-x86.zip`。[v0.6.3](https://github.com/Misakura-R1n/R10Trainer/releases/tag/v0.6.3) |
| 本地构建产物 | `artifacts/R10Trainer-v0.7.0-win-x86/` 及同名 ZIP |
| 构建身份 | 包内 `build-info.json` 记录版本、源码提交、工作树状态和组件指纹 |

安装、升级和构建参数见 [README](README.md)。协作规则见 [AGENTS.md](AGENTS.md)，版本变化见 [CHANGELOG.md](CHANGELOG.md)。

开始维护前检查 `git status --short --branch`、`git branch -vv` 和 `git log -5 --oneline`。以 `src/R10.App/R10.App.csproj` 确认产品版本，以实际发布包内的 `build-info.json` 确认构建身份。

## 本地开发环境

| 用途 | 配置与位置 |
|---|---|
| .NET SDK | `global.json` 固定 10.0.403。用 `dotnet --list-sdks` 确认版本，PATH 中缺少匹配版本时使用 `-DotNetPath` 指定 SDK 的可执行文件。 |
| 原生与 AIN 工具 | MinGW x86 工具与 alice-tools 0.13 分别由 `-BridgeToolchain` 和 `-AlicePath` 指定。默认路径见构建脚本。 |
| 原始 AIN 与实机源文件 | 源游戏目录及原始哈希见 `artifacts/live/battle-features-sandbox.json`。组件构建使用该目录中符合原始指纹的 AIN。 |
| 0.7.0 实机数据 | 游戏副本保留在 `artifacts/battle-features-game/`，约 4.08 GB。独立存档在 Windows 文档目录的 `AliceSoft/R10BattleFeaturesSandbox/SaveData`。验证使用序曲战斗前自动档 `LocalSave5005.asd`。 |
| 0.6.3 实机数据 | 游戏副本已清理。存档保留在 Windows 文档目录的 `AliceSoft/R10FeatureAuditSandbox/SaveData`，副本来源和身份见 `artifacts/live/feature-audit-sandbox.json`。 |
| 卡片管理实机数据 | 0.6.0 和 0.6.2 的游戏副本已清理。隔离存档位置见 `artifacts/live/card-management-sandbox.json` 和 `artifacts/live/card-access-sandbox.json`。0.6.2 的验证新档为 `LocalSave02.asd`。 |
| MCP 构建产物 | 常规 MCP 为 `artifacts/R10Developer/mcp/R10.Mcp.exe`，语义控制版为 `artifacts/R10Developer-controls/mcp/R10.Mcp.exe`。 |
| 隔离副本 | 用 [README 的副本准备命令](README.md#准备实机副本)创建新目录和独立存档名称，测试前核对进程、组件与存档位置。 |

0.7.0 新增普通战斗能力位 `1024`。能力值为 `full=2031`、`controls=2047`，玩家包使用 `full`。最终构建及隔离实机的组件身份如下。

| 组件文件 | SHA-256 |
|---|---|
| `full` AIN 解压内容 | `E8A93169E06209E422BF52E37C9D08B69EDB1D0D8013DACEEA513C50DBE6DF5B` |
| `full` AIN 压缩构建产物及实机副本 | `C171732DF07A76D542CEC3ABE99900B71CDC17CE56BC9D27A8848394D4F0C949` |
| 玩家包最终 `R10Bridge.dll` | `0AE3DB320BD1025DA9572F40EFC51B9D4285282CD4DC1CDA70596A7121127D83` |
| 隔离实机功能测试使用的 `R10Bridge.dll` | `1A5E8D37214FEB0A4CDA8AA93927B95848D645D1BA650CC136E20CAA6D63CC5E` |

`artifacts/` 和 `.tools/` 为本地目录。新测试副本使用独立的安装与恢复流程，避开 `.r10trainer-hp` 管理的玩家安装目录。

## 源码入口

| 模块 | 入口 |
|---|---|
| 功能定义、界面与图标 | `src/R10.Core/Contracts.cs`、`src/R10.App/ViewModels/FeatureViewModel.cs`、`src/R10.App/Controls/FeatureRow.xaml`、`src/R10.App/Assets/R10Trainer.ico` |
| 游戏选择、安装与恢复 | `src/R10.App/ViewModels/GameSetupViewModel.cs`、`src/R10.Bridge.Install/BridgeInstaller.cs` |
| 组件路由与退出恢复 | `src/R10.Bridge.Runtime/UnifiedTrainerRuntime.cs`、`src/R10.Bridge.Runtime/BridgeTrainerRuntime.cs` |
| 原生队列、开关及会话 | `native/R10Bridge/bridge.c`，语义命令由游戏线程消费 |
| 普通战斗 | `scripts/bridge/battle_ain.py` 处理战斗进入、退出、AP、技能、异常和行动判定。HP 请求绑定游戏会话、读档代次与行动阶段，游戏线程用实际 HP 回读完成命令。 |
| 卡片管理 | `src/R10.App/ViewModels/CardManagementViewModel.cs`、`src/R10.Bridge.Runtime/CardManagement.cs`、`scripts/bridge/card_management_ain.py`。卡片按版本 ID 获取，★ 按角色 ID 编辑，提交绑定原会话、读档代次及场景。 |
| 掉落去重 | `scripts/bridge/cards_ain.py`，围绕 `SceneGetCard@CreateCard` 管理同步批次并过滤两个卡池分支 |
| 重抽、重配与资源等级 | `scripts/bridge/feature_ain.py`、`scripts/bridge/data_ain.py`，切换游戏条件分支并调用游戏的资源和等级方法 |
| 外部数据与锁定 | `src/R10.Core/Data/R10DataEngine.cs`、`src/R10.Core/GameLoadFence.cs`、`src/R10.Core/TrainerRuntime.cs`，按写入归属与当前阶段检查回滚，读档代次或会话变化取消旧锁 |
| AIN 构建、识别和配方 | `scripts/bridge/build_instrumented_ain.py`、`scripts/bridge/controls_ain.py`、`scripts/bridge/export_portable_patch.py`、`src/R10.Core/Memory/GameConnection.cs`、`src/R10.Bridge.Install/AinPatch.cs` |
| MCP 与协议 | `src/R10.Mcp/McpServer.cs`、`src/R10.Mcp/GameInput.cs`、`src/R10.Bridge.Client/BridgeProtocol.cs` |

## 验证入口

| 层级 | 本地证据 |
|---|---|
| 0.7.0 模拟与离线检查 | `artifacts/live/battle-features-validation.log`。覆盖功能路由、HP 草稿失效、数值边界、AIN 插桩和安装恢复。符号链接用例因 Windows 权限不足跳过。 |
| 0.7.0 x86 辅助进程 | 同一日志记录两个配置各 118 项原生检查。HP 执行途中退出战斗后，旧命令会被拒绝，后续命令仍可进入队列。 |
| 0.7.0 界面演示 | `artifacts/live/battle-features-gameplay.jsonl` 的 `demo-wpf-*` 条目。覆盖战斗页控件、滚动、AP 读数和 HP 上限提示。 |
| 0.7.0 隔离实机 | `artifacts/live/battle-features-gameplay.jsonl` 记录游戏效果。`battle-features-player.jsonl` 记录玩家运行时和控件命令，`battle-features-mcp.jsonl` 记录状态与游戏输入。文件均在 `artifacts/live/`。 |
| 0.7.0 实机测试条件与隔离 | `artifacts/live/battle-features-fixture.json` 记录额外测试 AIN 的身份和条件。`artifacts/live/battle-features-integrity.json` 记录实机使用的 DLL 指纹及 4 个源游戏核心文件、21 个源存档文件的哈希核对结果。 |
| 0.7.0 本地玩家包 | `artifacts/live/v0.7.0-package-build.log`。由干净提交执行 `-Bridge -Publish`，包内身份见 `build-info.json`。 |
| 0.6.3 发布构建 | `artifacts/live/v0.6.3-release-build.log`，含模拟、离线 AIN、独立 x86 辅助进程及临时文件安装恢复。验证入口和参数见 [README](README.md#检查与发布)。 |
| 0.6.3 实机地图与资源 | `artifacts/live/feature-audit-published-map.log`、`artifacts/live/feature-audit-published-resources.log` |
| 0.6.3 实机战斗与结算 | `artifacts/live/feature-audit-hp.log`、`artifacts/live/feature-audit-near.log`、`artifacts/live/feature-audit-kill.log`、`artifacts/live/feature-audit-gameplay.jsonl` |
| 0.6.3 实机组件开关 | `artifacts/live/feature-audit-switches.log`、`artifacts/live/feature-audit-mcp.jsonl` |
| 0.6.2 卡片资格、获取及存读档 | `artifacts/live/card-access-live.jsonl`、`artifacts/live/card-access-mcp.jsonl` |
| 0.6.0 版本区分、共享 ★、读档保护和原文件校验 | `artifacts/live/card-management-live.jsonl`、`artifacts/live/card-management-mcp.jsonl`、`artifacts/live/card-management-isolation.json` |

0.7.0 隔离实机确认了双方 HP 写入至 1 和当前上限、AP 耗尽后补满、同回合连发、原生限次技能再次施放以及结算后停用 HP 编辑。三个越界输入的实机记录为辅助进程超时，明确拒绝及是否存在迟到写入待确认。附加测试 AIN 用于验证 AP 上限 10、中毒、睡眠、眩晕、石化与单次行动上限，相关证据使用上表的实机 DLL。

最终 DLL 增加了 HP 写入触发场景退出时的队列清理，已通过 x86 原生回归。最终 DLL 的游戏内复测暂停，待恢复实机操作后核对。隔离副本递归删除被环境策略拒绝，目录保留。

既有功能的实机覆盖范围见 [README](README.md#兼容性验证)。食券重抽与自动战斗重配待验证完整游戏内交互。掉落去重的餐券补位、固定卡预留、消耗型奖励及第二部过滤分支有离线覆盖。HP 保护的致死、毒伤、反射、简易战斗和其他章节仍需专项实机样例。卡片管理使用角色和装备样例验证，其他开放条目的实机检查覆盖获取资格。

## MCP 调试

启动前将 MCP 的 `--game-dir` 指向选定副本。重新构建或切换副本后，检查 `tools/list`、目标 PID 与组件能力。每次操作使用 `get_state` 返回的当前会话和 `loadGeneration`。语义读档和战斗操作要求 `controlsAvailable`，战斗操作另需 `battleReady`。写入结果未知时先查询状态或命令结果。参数、使用方式及限制见 [README](README.md#mcp-调试接口)。
