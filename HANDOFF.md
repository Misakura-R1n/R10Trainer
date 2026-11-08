# R10 修改器交接

核对日期：2026-11-08。本文件是唯一现行交接入口，供后续维护者和开发助手继续工作；状态改变时原位更新，历史由 Git 保存。用户明确要求保留独立交接文档，因此把工作现场和未完成事项放在这里，避免继续扩充面向使用者的 README。

先读 [协作规范](AGENTS.md)，再根据任务查阅 [使用、构建及架构说明](README.md)；重要版本变化见 [CHANGELOG.md](CHANGELOG.md)。不要将以下快照当作永久有效的进程、工具或构建状态。

## 当前状态

- 独立修改器已完成 v0.4.0，C# / .NET 10 / WPF，目标 win-x86，仅支持 README 指纹对应的中文 BugFix 版。现有 18 项功能定义及 3 个独立锁，包含新增的随机掉落去重。
- 玩家通过修改器安装组件和使用功能，不依赖 MCP。MCP 只供助手调试。0.5.0 源码中 HP、掉卡去重、食券重抽、自动战斗重配、资源及等级通过组件执行；其余功能继续使用已核验版本的外部内存访问。
- 源码仓库：`https://github.com/Misakura-R1n/R10Trainer`，私有；当前开发分支 `codex/component-optimizations`。功能入库基线为 `52de51c`，后续文档提交见 `git log`。
- 本次核对远程分支仍为 `52de51c`，后续本地文档提交尚未推送。接手先重新确认本地和远程状态，不照抄此快照。
- 用户要求每次完成修改、验证后创建 commit；后续推送仍按明确授权执行。原游戏、原存档和已有未归属变更必须保留。
- 用户已授权按顺序实施优化：两项脚本补丁组件化、读档代次保护外部锁、资源与等级迁移。当前源码进入 0.5.0 开发，旧 0.4.0 玩家包保持原样。第一批脚本分支组件化已通过离线及辅助进程验证，完整玩法实机验证尚未完成；第二批读档代次保护已通过核心与桥接模拟检查（含原地址复用、失联、超时及重新锁定）；第三批资源与等级迁移已完成并实机验证数值；玩家界面与锁定读档联动验证正在进行。

## 接手时先检查

1. 查看 `git status --short`、`git log -5 --oneline`、`git branch -vv` 和 `git remote -v`；不要重置或清理已有改动。
2. 从 `src/R10.App/R10.App.csproj` 确认产品版本，从 `global.json` 确认 SDK；所用发布包另查自己的 `build-info.json`，不要仅凭文件夹名称判断新旧。
3. 涉及游戏时核对选定目录、AIN/DLL 指纹、存档隔离位置及实际进程。进行 MCP 写入前重新 `get_state`，使用本次返回的会话和 `loadGeneration`；不复用聊天中的 PID 或旧代次。
4. 按任务查下面的源码和证据入口；通用构建、安装恢复和副本准备命令只维护在 README 中。

## 本机工作现场

以下路径在核对时存在，只属于当前开发机；克隆到其他机器后按 README 配置。`artifacts/`、`.tools/` 不入库，不要假定远程仓库包含这些文件。

| 用途 | 本机位置或说明 |
|---|---|
| 当前工程 | `D:\code\codex\R10修改器` |
| 参考旧项目 | `D:\code\codex\hanny`；独立工程没有加载旧 Core.dll |
| 受保护原游戏 | `D:\code\codex\hanny\兰斯10游戏本体和补丁\兰斯10 决战` |
| .NET SDK | `D:\code\codex\AliceInCradle\.tools\dotnet\dotnet.exe`；构建可通过 `-DotNetPath` 显式指定，不假定 PATH 中的 dotnet 有对应 SDK |
| MinGW | `D:\software\mingw64\bin` |
| alice-tools | `D:\code\deepseek\rc\文本文件\aindiff\vendor\alice\alice.exe` |
| 开发游戏副本 | `artifacts/control-game`，游戏名称 `R10ControlSandbox` |
| 副本存档 | Windows 文档目录下 `AliceSoft/R10ControlSandbox/SaveData`；原存档位置从 `bridge-sandbox.json` 的 `saveSource` 读取 |
| 当前去重调试 MCP | `artifacts/R10Developer-cards/mcp/R10.Mcp.exe`；这是本机曾发布的调试程序，不是构建脚本的常规输出目录 |

`artifacts/control-game/bridge-sandbox.json` 保留原游戏和原存档哈希。本次只读复核原游戏 4 个核心文件、21 个原存档文件均匹配清单。后续实机工作前后仍应重新核对。

当前 `control-game` 使用开发 `controls` 插桩，包含语义操作，不能当作玩家 `full` 组件兼容性样本直接给修改器使用。本机该 AIN 来自 `artifacts/bridge/cards-controls/Rance10.ain`；常规 `-Controls` 的输出则是 `artifacts/bridge/controls/`，两者不要只看目录名就互相替换。

| 当前副本文件 | SHA-256 |
|---|---|
| `Rance10.ain` | `6D781626F7265941BDAC78C500EA3D8FBCD3F9E3B7F134509FF45ABA23765421` |
| `R10Bridge.dll` | `88AF1D03B6D623B64305FC83F36C5599D78803EB35182B6AB4DF1CB75C405109` |

不要把开发文件覆盖到 `.r10trainer-hp` 管理的玩家安装目录，也不要为了清理现场删除旧副本、存档、备份或构建证据。

## 已交付产物及追溯

- 玩家包：`artifacts/R10Trainer-v0.4.0-win-x86.zip`，SHA-256 为 `5080D1DF2210703A9614F508C607713F6BB8E1500EDCB538F70611C4435002FF`。本地存在配套解压目录，玩家包不含 MCP、完整 AIN、游戏资源和存档。
- 该包生成于首次 Git 提交之前，内置 `build-info.json` 如实记录 `commit: uncommitted`、`dirty: true`。不能把后来的提交号手工填进去，声称它是在干净提交上构建的。
- 产品源码曾核对与此包的源文件哈希一致；此后的 README、协作规范、CHANGELOG 和本交接文档属于文档更新，尚未重新打包，包内 README 不是当前仓库文档。
- 后续需要发布带明确提交身份的包时，先完成并提交改动，再按 README 从干净工作区重新构建、验证，保留真实的 `build-info.json`。当前旧包保持原样。

## 继续实现时的重要入口

| 任务 | 源码入口及要点 |
|---|---|
| 功能定义与 UI | `src/R10.Core/Contracts.cs`、`src/R10.App/ViewModels/FeatureViewModel.cs`、`src/R10.App/Controls/FeatureRow.xaml` |
| 游戏选择、安装与恢复 | `src/R10.App/ViewModels/GameSetupViewModel.cs`、`src/R10.Bridge.Install/BridgeInstaller.cs`；备份目录和配方文件沿用 `hp` 命名，不代表只服务 HP |
| 功能路由及退出恢复 | `src/R10.Bridge.Runtime/UnifiedTrainerRuntime.cs`、`src/R10.Bridge.Runtime/BridgeTrainerRuntime.cs`；HP 与卡片能力和关闭责任独立，失败时保留恢复责任 |
| 原生队列、开关及批次生命周期 | `native/R10Bridge/bridge.c`；管道线程不直接访问 VM 对象，语义操作在游戏线程执行 |
| 掉卡去重插桩 | `scripts/bridge/cards_ain.py`；围绕 `SceneGetCard@CreateCard` 管理同步批次，在两个卡池分支的随机抽选前过滤 |
| AIN 构建及语义控制 | `scripts/bridge/build_instrumented_ain.py`、`scripts/bridge/controls_ain.py` |
| 版本识别与配方 | `src/R10.Core/Memory/GameConnection.cs`、`src/R10.Bridge.Install/AinPatch.cs`、`scripts/bridge/export_portable_patch.py`；插桩变化时同步维护指纹与配方 |
| 外部内存功能 | `src/R10.Core/Data/R10DataEngine.cs`、`src/R10.Core/Patching/R10PatchEngine.cs`、`src/R10.Core/TrainerRuntime.cs` |
| MCP | `src/R10.Mcp/McpServer.cs`、`src/R10.Mcp/GameInput.cs`、`src/R10.Bridge.Client/BridgeProtocol.cs` |

掉卡去重的既定语义是：按当前存档的具体卡片 ID 判断已拥有；同角色不同版本不合并；避开本批已抽出和指定卡片；保留原候选资格与剩余条目的相对权重；“拟态／”消耗型奖励豁免；空池走游戏原餐券补位。批次开始时冻结开关，不改已展示选项。借用数组句柄只在同步批次期间有效，读取仍由脚本完成；读档与批次结束清除临时状态。不要改为无限重抽或从全卡表无条件选卡。

## 验证证据与边界

下面是已有验证记录，不表示本次文档更新重新运行了这些测试或启动了游戏。完整实机范围以 README 的“已验证范围”为准。

| 证据入口（本地、不入库） | 能证明的内容 |
|---|---|
| `artifacts/live/cards-final-build.log` | 核心检查 67 通过；桥接检查 61 通过；安装恢复 24 通过、1 项因符号链接权限跳过；发布时核心检查含 x86 Win32 辅助进程共 68 通过。67 与 68 有重叠，不合并计数。 |
| `artifacts/live/cards-after-reward.json` | 开发组件实机状态曾累计过滤 2 个候选、餐券补位 0 次；收尾状态为关闭。实际画面观察到两张不同的 NEW 卡并正常领取，画面结论另记录在 README，不能只由计数推出。 |
| `artifacts/live/cards-ui-enabled.json`、`cards-ui-closed.json` | 玩家 `full` 组件下，WPF 操作后去重开关为开启，正常关闭修改器后为关闭。 |
| `artifacts/control-game/bridge-sandbox.json` | 原始文件与独立副本的识别信息、哈希及存档来源；不是所有玩法效果的验证凭证。 |

掉卡规则的离线回归入口为 `scripts/bridge/test_cards_ain.py`，执行实际生成的过滤字节码；开关、线程和批次检查在 `tests/R10.NativeBridge.Tests`；会话、能力、路由和退出责任检查在 `tests/R10.Bridge.Tests`。重跑方式按 README 的构建检查表执行。

目前未逐项完成实机验证的范围包括：

- 掉卡空池餐券补位、固定卡预留、消耗型奖励及第二部过滤分支；这些已有离线覆盖，不能称为实机通过。
- HP 保护的致死、毒伤、反射、简单战斗及其他章节全部扣血路径。
- 食券重抽、自动战斗重配的完整交互；已有补丁字节恢复检查不能替代玩法验证。
- MCP 的其他章节、特殊战斗、所有技能及非标准留黑边窗口布局。

## MCP 当前状态与后续工作

核对时，本会话已经加载的 `r10_bridge.set_feature` 工具 schema 仍只接受 `hp_guard`；仓库源码和本机 `R10Developer-cards` 程序已经支持 `no_duplicate_cards`。不要把旧工具列表当成新二进制已经加载的证明。

后续需要用新 MCP 接口时，按 README 发布或选用明确核验的程序，重新加载对应 MCP 服务并检查 `tools/list`。语义读档和战斗操作要求 `controlsAvailable`；战斗操作还要求 `battleReady`。工具输入被拒绝时先看焦点、遮挡、会话和代次，不自动重发结果未知的写操作。优先使用 MCP，窗口激活或观察需要时再用 computer-use。

当前需继续验证玩家界面、读档与旧锁取消，然后从干净提交生成 0.5.0 玩家包。资源与星级的 MCP 实机证据见 `artifacts/live/optimizations-mcp.jsonl` 和 `optimizations-data-readback.jsonl`。新副本为 `artifacts/optimizations-game`，独立存档名称 `R10OptimizationsSandbox`，目前刚结束 controls 验证，将切换 full。此前 MCP schema 与旧玩家包信息仍是旧版本快照；不要覆盖旧包或擅自推送远程。完成相关工作时同步更新本文件的当前状态，保留 README 作为通用说明入口。
