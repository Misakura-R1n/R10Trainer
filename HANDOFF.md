# R10 修改器交接

核对日期为 2026-11-10。本文件是唯一现行交接入口，记录本机工作现场与未完成事项。状态改变时原位更新，历史由 Git 保存。

先读 [协作规范](AGENTS.md)，再根据任务查阅 [使用、构建及架构说明](README.md)；重要版本变化见 [CHANGELOG.md](CHANGELOG.md)。不要将以下快照当作永久有效的进程、工具或构建状态。

## 当前状态

- 独立修改器当前版本 v0.6.0，C# / .NET 10 / WPF，目标 win-x86，支持 README 指纹对应的中文 BugFix 版。保留 18 项功能定义及 3 个独立锁，另有卡片管理页。
- 卡片管理可搜索、筛选及获取指定版本，并对已拥有的角色卡编辑共享 ★。单卡获取和设星通过游戏原函数执行。HP、掉卡去重、食券重抽、自动战斗重配、资源及等级也由组件执行。玩家通过修改器安装和使用，MCP 供助手调试。
- 源码仓库：`https://github.com/Misakura-R1n/R10Trainer`，私有；当前开发分支 `codex/component-optimizations`。功能入库基线为 `52de51c`，后续文档提交见 `git log`。
- 本轮只创建本地提交，没有推送；最近核对的远程分支为 `52de51c`。接手先重新确认本地和远程状态，不照抄此快照。
- 用户要求每次完成修改、验证后创建 commit；后续推送仍按明确授权执行。原游戏、原存档和已有未归属变更必须保留。
- 已完成三批优化：两项脚本补丁组件化（`04abec2`）、读档代次保护外部锁（`89927e0`）、资源与等级迁移（`16b38a0`）。自动检查、玩家界面、资源／星级数值、三项旧锁读档取消及退出清理已验证；重抽与重配的完整玩法交互仍未逐项实测。旧 0.4.0 玩家包保持原样。
- 0.5.1 独立提交了回滚归属（`5a6cafd`）、执行中命令期限（`eea991a`）、确认绑定原会话（`e486d31`）、死亡率阶段检查（`1f18359`）与 AIN 单独升级识别（`58f3d16`）。该版本完成自动回归、原生辅助进程和临时文件验证。0.6.0 另完成本页记录的卡片管理实机验证。

## 接手时先检查

1. 查看 `git status --short`、`git log -5 --oneline`、`git branch -vv` 和 `git remote -v`；不要重置或清理已有改动。
2. 从 `src/R10.App/R10.App.csproj` 确认产品版本，从 `global.json` 确认 SDK；所用发布包另查自己的 `build-info.json`，不要仅凭文件夹名称判断新旧。
3. 涉及游戏时核对选定目录、AIN/DLL 指纹、存档隔离位置及实际进程。进行 MCP 写入前重新 `get_state`，使用本次返回的会话和 `loadGeneration`；不复用聊天中的 PID 或旧代次。
4. 按任务查下面的源码和证据入口；通用构建、安装恢复和副本准备命令只维护在 README 中。

## 本机工作现场

下表记录当前开发机的路径，已删除的副本单独标注。克隆到其他机器后按 README 配置。`artifacts/`、`.tools/` 不入库。

| 用途 | 本机位置或说明 |
|---|---|
| 当前工程 | `D:\code\codex\R10修改器` |
| 参考旧项目 | `D:\code\codex\hanny`；独立工程没有加载旧 Core.dll |
| 受保护原游戏 | `D:\code\codex\hanny\兰斯10游戏本体和补丁\兰斯10 决战` |
| .NET SDK | `D:\code\codex\AliceInCradle\.tools\dotnet\dotnet.exe`；构建可通过 `-DotNetPath` 显式指定，不假定 PATH 中的 dotnet 有对应 SDK |
| MinGW | `D:\software\mingw64\bin` |
| alice-tools | `D:\code\deepseek\rc\文本文件\aindiff\vendor\alice\alice.exe` |
| 最近实机副本（已删除） | 原目录为 `artifacts/card-management-game`，游戏名称 `R10CardManagementSandbox`，验证时使用 0.6.0 玩家 `full` 组件 |
| 副本存档（保留） | Windows 文档目录下 `AliceSoft/R10CardManagementSandbox/SaveData`，含复制来的 `LocalSave00.asd` 与验证中保存的 `LocalSave01.asd` |
| 最近实机调试 MCP | `artifacts/R10Developer-optimizations/mcp/R10.Mcp.exe`，卡片管理验证时以独立 stdio 子进程和显式 `--game-dir` 绑定该副本，验证结束后已关闭 |

副本清单保存在 `artifacts/live/card-management-sandbox.json`，记录原始位置、隔离位置与哈希。删除副本后复核原游戏 4 个核心文件、21 个原存档文件，以及副本旧档 `LocalSave00.asd`，均匹配清单。

0.6.0 组件能力值为 `full=1007`、`controls=1023`。下表记录实机使用的组件，AIN 解压后指纹见 README。旧 `optimizations-game` 仍保留 0.5.0 `full` 组件，旧 `control-game` 及其存档亦保留。

| 实机验证时的副本文件 | SHA-256 |
|---|---|
| `Rance10.ain` | `DB5339319252861CED6C091E7D0EA6EA46F9E66D1EEC805B560C3DE05F89E85E` |
| `R10Bridge.dll` | `7E5D5966292E8874830901D5EAA6FC3C40EBDE048B9C84613133BE199E1D4C3A` |

不要把开发文件覆盖到 `.r10trainer-hp` 管理的玩家安装目录，也不要为了清理现场删除旧副本、存档、备份或构建证据。

## 已交付产物及追溯

- 当前玩家包为 `artifacts/R10Trainer-v0.6.0-win-x86.zip`，配套同名解压目录。包内 `build-info.json` 记录实际构建提交、工作树状态及源码和组件哈希，以该文件识别构建。包内含玩家程序、原生 DLL 和安装配方。
- 0.5.0 玩家包构建提交为 `c9ec628`。0.5.1 玩家包使用干净提交构建，发布日志为 `artifacts/live/review-fixes-release-build.log`。保留旧包。
- 保留的旧包 `artifacts/R10Trainer-v0.4.0-win-x86.zip` 的 SHA-256 为 `5080D1DF2210703A9614F508C607713F6BB8E1500EDCB538F70611C4435002FF`。它生成于首次 Git 提交前，内置身份是 `uncommitted` / `dirty: true`，不能作为新包的构建身份。

## 继续实现时的重要入口

| 任务 | 源码入口及要点 |
|---|---|
| 功能定义与 UI | `src/R10.Core/Contracts.cs`、`src/R10.App/ViewModels/FeatureViewModel.cs`、`src/R10.App/Controls/FeatureRow.xaml` |
| 游戏选择、安装与恢复 | `src/R10.App/ViewModels/GameSetupViewModel.cs`、`src/R10.Bridge.Install/BridgeInstaller.cs`；备份目录和配方文件沿用 `hp` 命名，不代表只服务 HP |
| 功能路由及退出恢复 | `src/R10.Bridge.Runtime/UnifiedTrainerRuntime.cs`、`src/R10.Bridge.Runtime/BridgeTrainerRuntime.cs`；各组件开关分别记录关闭责任，失败时保留恢复责任 |
| 原生队列、开关及批次生命周期 | `native/R10Bridge/bridge.c`；管道线程不直接访问 VM 对象，语义操作在游戏线程执行 |
| 掉卡去重插桩 | `scripts/bridge/cards_ain.py`；围绕 `SceneGetCard@CreateCard` 管理同步批次，在两个卡池分支的随机抽选前过滤 |
| 卡片管理 | `src/R10.App/ViewModels/CardManagementViewModel.cs`、`src/R10.Bridge.Runtime/CardManagement.cs`、`scripts/bridge/card_management_ain.py`。目录按具体卡片 ID 区分版本，设星按角色 ID。确认绑定进程、读档代次、操作场景及选中卡片，游戏线程重查资格和所有权后执行 |
| 重抽、重配与资源等级 | `scripts/bridge/feature_ain.py`、`scripts/bridge/data_ain.py`；前者切换原条件分支，后者在游戏线程调用资源 setter 与批量星级方法；管道不保存 VM 对象句柄 |
| 读档与外部锁 | `src/R10.Core/GameLoadFence.cs`、`src/R10.Core/TrainerRuntime.cs`；会话或代次变化取消旧锁，观察租约失效暂停写入，不能将跨进程写入视为原子事务 |
| 延迟确认与回滚 | `FeatureViewModel` 捕获原 `LoadEpoch`，`SetValueForEpochAsync` 传至桥接最新状态核验；外部事务只有成功写入且仍保持目标值才可尝试恢复，失败／部分写入不得仅凭回读认领归属；死亡率恢复还要检查当前阶段 |
| AIN 构建及语义控制 | `scripts/bridge/build_instrumented_ain.py`、`scripts/bridge/controls_ain.py` |
| 版本识别与配方 | `src/R10.Core/Memory/GameConnection.cs`、`src/R10.Bridge.Install/AinPatch.cs`、`scripts/bridge/export_portable_patch.py`；插桩变化时同步维护指纹与配方 |
| 外部内存功能 | `src/R10.Core/Data/R10DataEngine.cs`、`src/R10.Core/Patching/R10PatchEngine.cs`、`src/R10.Core/TrainerRuntime.cs` |
| MCP | `src/R10.Mcp/McpServer.cs`、`src/R10.Mcp/GameInput.cs`、`src/R10.Bridge.Client/BridgeProtocol.cs` |

掉卡去重的既定语义是：按当前存档的具体卡片 ID 判断已拥有；同角色不同版本不合并；避开本批已抽出和指定卡片；保留原候选资格与剩余条目的相对权重；“拟态／”消耗型奖励豁免；空池走游戏原餐券补位。批次开始时冻结开关，不改已展示选项。借用数组句柄只在同步批次期间有效，读取仍由脚本完成；读档与批次结束清除临时状态。不要改为无限重抽或从全卡表无条件选卡。

## 验证证据与边界

各版本实机范围见 README 的“已验证范围”。

| 证据入口（本地、不入库） | 能证明的内容 |
|---|---|
| `artifacts/live/card-management-verified-build.log`、`card-management-release.log` | 0.6.0 的完整组件与玩家包构建。常规核心 72、桥接 74、完整安装恢复 26 项通过。独立 Win32 x86 助手加入后核心 73 项通过，含与常规检查重叠的 72 项。原生 x86 夹具 93 项通过。基础 AIN 22、掉卡 8、数据 6、条件分支 3、完整 AIN 指纹 2 项通过。副本准备 11 项通过。安装与副本准备均有因符号链接权限跳过的检查。 |
| `artifacts/live/card-management-live.jsonl`、`card-management-mcp.jsonl`、`card-management-isolation.json` | 玩家页获取 Lv45 兰斯，Lv55 未获取。Lv42、Lv45 共享 ★47，经验清零，下一级经验 8,819。MCP 操作游戏菜单完成存读档，读旧档后旧确认未生效，读新档后数据保留。隔离记录核对原文件哈希。 |
| `artifacts/live/review-fix1-build.log` 至 `review-fix5-build.log`；第二项使用 `review-fix2-validated-build.log` | 各独立修复对应检查。最终常规结果：核心 72、桥接含实际 ViewModel 源码 71、安装恢复 25 项通过，符号链接权限跳过 1 项。第二项初次构建日志保留了已解决的 freestanding x86 栈探测链接失败，不以该失败日志作为通过凭证。 |
| `artifacts/live/review-fix2-validated-build.log` | 原生辅助进程 83 项通过，含执行中跨 3 秒期限的资源／部队／星级参数保持、未开始命令过期；基础 AIN 21、掉卡 8、数据 6、条件分支 3 项通过，副本准备 11 通过、1 权限跳过；完整 AIN 识别 2 项及真实文件安装恢复通过。 |
| `artifacts/live/review-fixes-release-build.log` | 文档和版本提交后的 0.5.1 玩家发布检查；核心含 x86 辅助进程为 73 项，与常规 72 项重叠，不累加；未启动游戏。 |
| `artifacts/live/optimizations-validated-build.log` | 核心 69、桥接 67、原生组件 64、基础 AIN 21、掉卡 8、数据 AIN 6、分支 AIN 3 项通过；安装与副本准备各有符号链接权限跳过，未冒充通过。另含完整 AIN 识别与真实文件安装恢复检查。 |
| `artifacts/live/optimizations-player-build.log`、`optimizations-release-build.log` | 玩家发布检查；核心含 x86 辅助进程为 70 项，与上述 69 项重叠，不累加。后一个日志对应文档提交后的最终包。 |
| `artifacts/live/optimizations-mcp.jsonl`、`optimizations-data-readback.jsonl` | 实机命令、会话／代次及游戏状态；另一个日志独立只读核对资源、9 名角色星级和经验数据。 |
| `artifacts/live/optimizations-locks-before.txt`、`optimizations-locks-observed.json`、`optimizations-ui-closed.json` | 三项锁定前状态、读档后画面观察及退出后开关状态。首次读档后辅助功能树滞后，单独保留为 `optimizations-locks-after-first-stale.txt`，不能用它判断读档后锁状态。 |
| `artifacts/live/cards-final-build.log` | 核心检查 67 通过；桥接检查 61 通过；安装恢复 24 通过、1 项因符号链接权限跳过；发布时核心检查含 x86 Win32 辅助进程共 68 通过。67 与 68 有重叠，不合并计数。 |
| `artifacts/live/cards-after-reward.json` | 开发组件实机状态曾累计过滤 2 个候选、餐券补位 0 次；收尾状态为关闭。实际画面观察到两张不同的 NEW 卡并正常领取，画面结论另记录在 README，不能只由计数推出。 |
| `artifacts/live/cards-ui-enabled.json`、`cards-ui-closed.json` | 玩家 `full` 组件下，WPF 操作后去重开关为开启，正常关闭修改器后为关闭。 |
| `artifacts/control-game/bridge-sandbox.json` | 原始文件与独立副本的识别信息、哈希及存档来源；不是所有玩法效果的验证凭证。 |

掉卡规则的离线回归入口为 `scripts/bridge/test_cards_ain.py`，执行实际生成的过滤字节码；开关、线程和批次检查在 `tests/R10.NativeBridge.Tests`；会话、能力、路由和退出责任检查在 `tests/R10.Bridge.Tests`。重跑方式按 README 的构建检查表执行。

目前未逐项完成实机验证的范围包括：

- 掉卡空池餐券补位、固定卡预留、消耗型奖励及第二部过滤分支；这些已有离线覆盖，不能称为实机通过。
- HP 保护的致死、毒伤、反射、简单战斗及其他章节全部扣血路径。
- 食券重抽、自动战斗重配的完整交互；已有真实生成字节码测试和实机开关检查不能替代完整玩法验证。
- 卡片管理实机验证覆盖序曲地图的兰斯版本。其他角色、特殊条目和后续章节尚未逐项验证。
- MCP 的其他章节、特殊战斗、所有技能及非标准留黑边窗口布局。

## MCP 当前状态与后续工作

卡片管理验证时，预加载的 `r10_bridge` 指向旧目录。验证使用上表的独立 MCP 子进程绑定当时的副本，核对实际 PID 和组件能力后，通过 `input` 操作游戏菜单。卡片获取与设星由 WPF 玩家页触发，游戏效果从原生管道回读。

后续需要用新 MCP 接口时，按 README 发布或选用明确核验的程序，重新加载对应 MCP 服务并检查 `tools/list`。语义读档和战斗操作要求 `controlsAvailable`；战斗操作还要求 `battleReady`。工具输入被拒绝时先看焦点、遮挡、会话和代次，不自动重发结果未知的写操作。优先使用 MCP，窗口激活或观察需要时再用 computer-use。

后续实机验证前重新核对所选副本的游戏进程、组件与存档位置，再补充上述未覆盖场景。外部内存读、检查、写之间仍非原子操作。完成相关工作时同步更新本文件，通用操作继续查阅 README。
