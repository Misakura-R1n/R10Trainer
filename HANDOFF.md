# R10 修改器开发交接

## 版本与发布

| 项目 | 当前配置 |
|---|---|
| 玩家版 | v0.6.3，C# / .NET 10 / WPF，`win-x86` |
| 游戏版本 | 中文 BugFix 版，原始文件指纹见 [README](README.md#支持范围与限制) |
| 源码 | 私有仓库 [R10Trainer](https://github.com/Misakura-R1n/R10Trainer)，默认分支 `codex/initial-import` |
| 发布 | [v0.6.3](https://github.com/Misakura-R1n/R10Trainer/releases/tag/v0.6.3)，玩家包 `R10Trainer-v0.6.3-win-x86.zip` |
| 本地构建产物 | `artifacts/R10Trainer-v0.6.3-win-x86/` 及同名 ZIP |
| 构建身份 | 包内 `build-info.json` 记录版本、源码提交、工作树状态和组件指纹 |

安装、升级和构建参数见 [README](README.md)。协作规则见 [AGENTS.md](AGENTS.md)，版本变化见 [CHANGELOG.md](CHANGELOG.md)。

开始维护前检查 `git status --short --branch`、`git branch -vv` 和 `git log -5 --oneline`。以 `src/R10.App/R10.App.csproj` 确认产品版本，以实际发布包内的 `build-info.json` 确认构建身份。

## 本地开发环境

| 用途 | 配置与位置 |
|---|---|
| .NET SDK | `global.json` 固定 10.0.403。用 `dotnet --list-sdks` 确认版本，PATH 中缺少匹配版本时使用 `-DotNetPath` 指定 SDK 的可执行文件。 |
| 原生与 AIN 工具 | MinGW x86 工具与 alice-tools 0.13 分别由 `-BridgeToolchain` 和 `-AlicePath` 指定。默认路径见构建脚本。 |
| 原始 AIN 与实机源文件 | 从本地 `artifacts/live/feature-audit-sandbox.json` 查询源游戏目录及原始哈希。组件构建使用该目录中符合原始指纹的 AIN。 |
| 0.6.3 实机数据 | 游戏副本已清理。存档保留在 Windows 文档目录的 `AliceSoft/R10FeatureAuditSandbox/SaveData`，副本来源和身份见同一清单。 |
| 卡片管理实机数据 | 0.6.0 和 0.6.2 的游戏副本已清理。隔离存档位置见 `artifacts/live/card-management-sandbox.json` 和 `artifacts/live/card-access-sandbox.json`。0.6.2 的验证新档为 `LocalSave02.asd`。 |
| MCP 构建产物 | 常规 MCP 为 `artifacts/R10Developer/mcp/R10.Mcp.exe`，语义控制版为 `artifacts/R10Developer-controls/mcp/R10.Mcp.exe`。 |
| 隔离副本 | 用 [README 的副本准备命令](README.md#准备实机副本)创建新目录和独立存档名称，测试前核对进程、组件与存档位置。 |

0.6.3 组件能力值为 `full=1007`、`controls=1023`。玩家包使用 `full`。组件文件及其指纹可在 `build-info.json` 和 `artifacts/live/feature-audit-sandbox.json` 中分别核对。

| 0.6.3 实机使用的 `full` 文件 | SHA-256 |
|---|---|
| `Rance10.ain`（压缩文件） | `FD06656F654326CEE2A3F26B7952F1206F26246E646DE8202E8777D07909F386` |
| `R10Bridge.dll` | `7E5D5966292E8874830901D5EAA6FC3C40EBDE048B9C84613133BE199E1D4C3A` |

`artifacts/` 和 `.tools/` 为本地目录。新测试副本使用独立的安装与恢复流程，避开 `.r10trainer-hp` 管理的玩家安装目录。

## 源码入口

| 模块 | 入口 |
|---|---|
| 功能定义、界面与图标 | `src/R10.Core/Contracts.cs`、`src/R10.App/ViewModels/FeatureViewModel.cs`、`src/R10.App/Controls/FeatureRow.xaml`、`src/R10.App/Assets/R10Trainer.ico` |
| 游戏选择、安装与恢复 | `src/R10.App/ViewModels/GameSetupViewModel.cs`、`src/R10.Bridge.Install/BridgeInstaller.cs` |
| 组件路由与退出恢复 | `src/R10.Bridge.Runtime/UnifiedTrainerRuntime.cs`、`src/R10.Bridge.Runtime/BridgeTrainerRuntime.cs` |
| 原生队列、开关及会话 | `native/R10Bridge/bridge.c`，语义命令由游戏线程消费 |
| 卡片管理 | `src/R10.App/ViewModels/CardManagementViewModel.cs`、`src/R10.Bridge.Runtime/CardManagement.cs`、`scripts/bridge/card_management_ain.py`。卡片按版本 ID 获取，★ 按角色 ID 编辑，提交绑定原会话、读档代次及场景。 |
| 掉落去重 | `scripts/bridge/cards_ain.py`，围绕 `SceneGetCard@CreateCard` 管理同步批次并过滤两个卡池分支 |
| 重抽、重配与资源等级 | `scripts/bridge/feature_ain.py`、`scripts/bridge/data_ain.py`，切换游戏条件分支并调用游戏的资源和等级方法 |
| 外部数据与锁定 | `src/R10.Core/Data/R10DataEngine.cs`、`src/R10.Core/GameLoadFence.cs`、`src/R10.Core/TrainerRuntime.cs`，按写入归属与当前阶段检查回滚，读档代次或会话变化取消旧锁 |
| AIN 构建、识别和配方 | `scripts/bridge/build_instrumented_ain.py`、`scripts/bridge/controls_ain.py`、`scripts/bridge/export_portable_patch.py`、`src/R10.Core/Memory/GameConnection.cs`、`src/R10.Bridge.Install/AinPatch.cs` |
| MCP 与协议 | `src/R10.Mcp/McpServer.cs`、`src/R10.Mcp/GameInput.cs`、`src/R10.Bridge.Client/BridgeProtocol.cs` |

## 验证入口

| 层级 | 本地证据 |
|---|---|
| 0.6.3 发布构建 | `artifacts/live/v0.6.3-release-build.log`，含模拟、离线 AIN、独立 x86 辅助进程及临时文件安装恢复。验证入口和参数见 [README](README.md#检查与发布)。 |
| 0.6.3 实机地图与资源 | `artifacts/live/feature-audit-published-map.log`、`artifacts/live/feature-audit-published-resources.log` |
| 0.6.3 实机战斗与结算 | `artifacts/live/feature-audit-hp.log`、`artifacts/live/feature-audit-near.log`、`artifacts/live/feature-audit-kill.log`、`artifacts/live/feature-audit-gameplay.jsonl` |
| 0.6.3 实机组件开关 | `artifacts/live/feature-audit-switches.log`、`artifacts/live/feature-audit-mcp.jsonl` |
| 0.6.2 卡片资格、获取及存读档 | `artifacts/live/card-access-live.jsonl`、`artifacts/live/card-access-mcp.jsonl` |
| 0.6.0 版本区分、共享 ★、读档保护和原文件校验 | `artifacts/live/card-management-live.jsonl`、`artifacts/live/card-management-mcp.jsonl`、`artifacts/live/card-management-isolation.json` |

实机覆盖范围见 [README](README.md#兼容性验证)。食券重抽与自动战斗重配待验证完整游戏内交互。掉落去重的餐券补位、固定卡预留、消耗型奖励及第二部过滤分支有离线覆盖。HP 保护的致死、毒伤、反射、简易战斗和其他章节仍需专项实机样例。卡片管理使用角色和装备样例验证，其他开放条目的实机检查覆盖获取资格。

## MCP 调试

启动前将 MCP 的 `--game-dir` 指向选定副本。重新构建或切换副本后，检查 `tools/list`、目标 PID 与组件能力。每次操作使用 `get_state` 返回的当前会话和 `loadGeneration`。语义读档和战斗操作要求 `controlsAvailable`，战斗操作另需 `battleReady`。写入结果未知时先查询状态或命令结果。参数、使用方式及限制见 [README](README.md#mcp-调试接口)。
