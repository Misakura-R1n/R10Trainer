# R10 修改器

《兰斯 10》Windows x86 修改器。源码版本 **0.9.2**。

[发布记录](https://github.com/Misakura-R1n/R10Trainer/releases) · [变更日志](CHANGELOG.md) · [支持版本](#支持范围与限制) · [构建](#开发与构建)

## 安装与使用

1. 解压整个 `R10Trainer-v<版本>-win-x86.zip`，运行 `R10Trainer.exe`。运行时随包提供，游戏本体由玩家自行准备。
2. 点击 **选择游戏**，选中目标游戏的 `Rance10.exe`。
3. 退出游戏后点击 **安装游戏组件**，再点击 **启动游戏**。修改器和游戏须使用相同的权限级别。

组件开关随游戏启动默认关闭。正常退出修改器时，会撤销本次连接开启的组件开关。`R10Trainer.exe --demo` 可用内置数据预览界面，可加 `--theme light` 或 `--theme dark`。

### 升级与恢复

关闭旧修改器和游戏，将新版解压到新目录。提示组件版本过旧时，先点击 **恢复游戏文件**，再安装新版组件。

卸载组件时，在游戏退出后点击 **恢复游戏文件**。安装器从 `.r10trainer-hp` 还原安装前的 AIN 和 INI、移除组件 DLL，并归档备份。安装保留原有存档路径，恢复后仍使用原来的游戏补丁。已写入存档的数值和奖励随存档保留。

### 常见问题

| 现象 | 处理方式 |
|---|---|
| 等待游戏连接 | 核对所选目录、实际运行目录和两边的权限级别。 |
| 功能暂时不可用 | 检查组件版本，等待读档或场景过渡结束。 |
| 版本不支持或恢复被拒绝 | 核对 EXE 与 AIN 指纹，保留 `.r10trainer-hp`，先确认其他补丁对文件的改动。 |
| 安装组件后游戏无法启动 | 游戏退出后，在修改器中恢复游戏文件。 |
| 关闭时提示恢复未完成 | 保留窗口，按提示重试。 |
| 提示数据写入暂停 | 回滚未能完整恢复。重选或重连同一游戏进程会保留保护，退出游戏后重新启动并连接。 |
| 窗口位置或游戏路径设置异常 | 退出修改器，备份并重命名 `%LOCALAPPDATA%\R10Trainer\settings.json`。演示模式使用 `demo-settings.json`。 |

## 功能

| 分组 | 功能 |
|---|---|
| 战斗 | HP 保护、双方 HP、AP 全满、技能次数、异常免疫、行动限制、敌人濒死与直死、难度 |
| 战略 | 回合、累计人类死亡率、四国兵力、四国战果。前三项支持锁定 |
| 资源 | 食券／金块、勋章、友情次数、资源全满、宝箱发现率、宝箱卡牌可获取＋1、随机掉落去重 |
| 成长与辅助 | 部队等级、批量角色★、食券重抽、自动战斗重配 |

- 数值用 Enter 或 **应用** 提交。锁定保持当前值，四国兵力分别锁定。装有组件时，读档或切换游戏会话会解除旧锁，组件失联时暂停锁定。未装组件时请在读档前解除锁定。结算阶段可能暂停死亡率写入。
- **资源全满**将不足 3 的食券／金块、勋章和友情补到 3，正常消费后继续补足。单改这三项前先关闭该开关。
- **批量角色★**可将已创建角色设为 10、40、80 或 200★，清空当前经验并重算下一级经验。可降级，可能影响继承，超出游戏上限时整次拒绝。
- **宝箱卡牌可获取＋1**从下次打开选卡界面起，普通难度可领 2 张，困难难度可领 3 张。两张候选的宝箱扩为三张。使用同一选卡界面的奖励也适用，与同名周目奖励共用一次加成。
- **随机掉落去重**从下一批随机奖励开始，排除已持有的卡片版本和同批重复选项。同一角色的其他版本仍可出现。固定、消耗型奖励及候选资格沿用游戏规则，空池由食券补位。
- 食券重抽保留正常消耗。整合补丁与「自动战斗＋餐券无需 SL」的食券和重配规则由游戏补丁控制。

### 普通战斗

我方行动选择阶段显示 AP 与双方 HP。手动编辑 HP 时输入 1 到当前上限之间的整数，目标变化或阶段结束后重新输入。敌人濒死与直死互斥，开启任一项时暂停手动编辑敌方 HP。

| 开关 | 效果 |
|---|---|
| HP 保护 | 在已接入的伤害结算中保留我方当前 HP。治疗和初始化按游戏规则执行。 |
| AP 全满 | 按队伍上限补满 AP，技能照常扣费。 |
| 限次技能不限次 | 次数耗尽后仍可使用技能，按游戏规则结算次数与 AP。 |
| 我方异常免疫 | 清除并阻止我方队伍异常及角色的眩晕、石化。 |
| 同回合重复行动 | 已行动的角色可以再次选择技能。卡面可能仍显示「行动结束」。 |
| 解除我方行动次数上限 | 解除战斗效果施加的全队行动次数上限。 |

### 卡片管理

在地图等可操作场景打开 **卡片管理**，按卡名、角色或拥有状态筛选。选中未持有的版本后点击 **获取此卡**。拟态消耗品和测试条目只供查看，装备或道具可按卡表资格获取。战斗、结算、选卡和读档期间暂停操作，回到地图后恢复读取。

选中已拥有的角色卡，输入 0 至游戏上限之间的整数，再点击 **设置角色★**。同一角色的各版本共享 ★，普通兰斯和魔王兰斯分别成长。设星后清空当前经验，由游戏重算下一级经验。正常保存游戏后保留获取和设星结果。

## 支持范围与限制

安装和连接均校验 `Rance10.exe` 与 `Rance10.ain` 的组合。`AliceStart.ini` 须使用 `CodeName = "Rance10.ain"`。原始文件及安装后组件指纹见 [游戏版本清单](src/R10.Core/Memory/GameVersions.json)。

| 游戏版本 | 构建目录 ID |
|---|---|
| 中文 BugFix | `bugfix` |
| 40级补丁＋百分百宝箱补丁 | `level40-treasure` |
| 百分百宝箱补丁 | `treasure` |
| 宝箱卡牌可获取＋1 | `extra-card` |
| 整合补丁（RAR 与 ZIP 同版） | `combined` |
| 自动战斗＋餐券无需 SL | `auto-food` |
| 中文原版 | `chinese-original` |
| 日文原版 | `japanese-original` |

### 兼容性验证

八种配方已通过离线校验和临时文件安装恢复。中文 BugFix 的实机验证覆盖序曲地图、普通战斗、宝箱和卡片管理，以及读档前排队的写入、战斗恢复后的 AP 和 HP、结算后的卡表读取。「宝箱卡牌可获取＋1」、其他游戏版本、特殊伤害路径及完整食券重抽与重配流程待实机验证。[验证入口](HANDOFF.md#验证)

## 开发与构建

在仓库根目录用 PowerShell 7 执行。SDK 版本见 [global.json](global.json)。

```powershell
./scripts/build.ps1
dotnet run --project src/R10.App -c Release -- --demo
```

构建玩家包还需 Python 3.11+、MinGW x86 工具和 alice-tools 0.13。按上表的 ID 准备原始 AIN。中文 BugFix 和日版同目录提供对应 EXE 与 INI，缺少独立 EXE 的中文补丁沿用 BugFix 的 EXE 与 INI。

```powershell
$build = @{
    DotNetPath = 'dotnet'
    PythonPath = 'python'
    BridgeToolchain = 'C:\Tools\mingw64\bin'
    AlicePath = 'C:\Tools\alice\alice.exe'
    SourceAin = 'D:\R10Inputs\bugfix\Rance10.ain'
}
$versions = Get-Content src/R10.Core/Memory/GameVersions.json -Raw | ConvertFrom-Json
$compatibilityAin = $versions | Where-Object id -ne 'bugfix' | ForEach-Object {
    "D:\R10Inputs\$($_.id)\Rance10.ain"
}
./scripts/build.ps1 @build -Bridge -CompatibilityAin $compatibilityAin -Publish
```

### 检查与发布

`build.ps1` 执行常规检查，`-Bridge` 增加 x86 组件、AIN 及安装恢复验证，`-Publish` 增加 Win32 辅助进程检查并生成 ZIP。发布前更新 `R10.App.csproj` 和 `CHANGELOG.md`，从干净提交重建 `full` 组件。

产物位于 `artifacts/R10Trainer-v<版本>-win-x86/` 及同名 ZIP。`build-info.json` 记录源码提交、组件哈希和配方列表。玩家包包含运行时和 `full` 组件，MCP 与 `controls` 组件供开发使用。

### 准备实机副本

以中文 BugFix 原始文件为输入，使用 `-Controls` 的构建产物。目标游戏目录须为 `artifacts` 下的新目录，`SandboxName` 须使用新存档名。

```powershell
./scripts/prepare-bridge-sandbox.ps1 `
    -SourceDirectory 'D:\Games\Rance10' `
    -TargetDirectory "$PWD\artifacts\control-game-test" `
    -SandboxName 'R10ControlTest' `
    -BridgeDirectory "$PWD\artifacts\bridge\controls" `
    -BridgeDll "$PWD\artifacts\bridge-controls-native\R10Bridge.dll"
```

原存档默认取自 Windows 文档目录的 `AliceSoft/<原游戏名称>/SaveData`，也可用 `-SourceSaveDirectory` 指定。副本使用 `AliceSoft/<SandboxName>/SaveData`。启动前核对目标与独立存档路径，结束后按副本清单核对源文件哈希。

### MCP 调试接口

常规调试用 `./scripts/build.ps1 -DeveloperTools`，输出到 `artifacts/R10Developer/mcp/`。语义读档、结束回合和使用技能需要 `-Controls`。

```powershell
./scripts/build.ps1 @build -Controls
codex mcp add r10-bridge -- "$PWD\artifacts\R10Developer-controls\mcp\R10.Mcp.exe" --game-dir "$PWD\artifacts\control-game-test"
```

`--game-dir` 指定绝对目录并随游戏重启重新发现进程。也支持 `--pid`，对应环境变量为 `R10_BRIDGE_GAME_DIR` 和 `R10_BRIDGE_PID`。

| 工具 | 用途 |
|---|---|
| `get_state` | 读取会话、读档代次、组件能力和场景状态。 |
| `set_feature`、`set_value` | 切换组件功能或修改数值，分别检查对应能力。数值写入另需 `dataReady`。 |
| `get_command_result` | 用原始 `session` 与命令 `id` 查询结果。 |
| `input` | 向选定窗口发送有界键鼠输入。鼠标坐标以 1920×1080 游戏画布为基准。 |
| `load_save` | 载入已有 `LocalSaveNN.asd`，`slot` 使用内部编号。读档会替换未保存进度。 |
| `end_turn`、`use_skill` | 调用游戏原有战斗操作。屏幕从左到右的 `party_index` 为 5、3、1、0、2、4、6，上下技能分别为 0、1。 |

先读 `get_state`，使用当前的 `session` 和 `loadGeneration` 提交写操作。语义操作需要 `controlsAvailable`，战斗操作还需 `battleReady`。`queued` 表示排队，`applied` 表示游戏线程已处理命令。读档完成以 `lastCompletedGeneration` 等于新 `loadGeneration` 且 `lastLoadOutcome` 为 1 为准。回复丢失时用原会话和命令 ID 查询。参数定义见 [McpServer.cs](src/R10.Mcp/McpServer.cs)。

### 架构与扩展

| 模块 | 职责 |
|---|---|
| `src/R10.App` | WPF 界面、游戏选择和组件管理 |
| `src/R10.Core` | 版本校验、外部内存访问、数据事务及恢复 |
| `src/R10.Bridge.Install` | 配方应用、备份和恢复 |
| `src/R10.Bridge.Client`、`src/R10.Bridge.Runtime` | 命名管道、会话校验、功能路由及关闭 |
| `native/R10Bridge`、`scripts/bridge` | 游戏线程命令、AIN 插桩和配方生成 |
| `src/R10.Mcp` | 开发调试接口 |

功能定义位于 `src/R10.Core/Contracts.cs`。组件功能需同步维护能力、协议、关闭恢复和对应测试，AIN 变更还需更新版本指纹和配方。游戏语义、布局与字节资料参照 Hanny 提交 `82b06b1e283a80ce98aa1a1fe1796d8a2280f8af`。

## 许可证

本项目原创代码与文档采用 [MIT 许可证](LICENSE)。游戏图标及其他第三方素材适用各自的授权条款。发布包中的 .NET 运行时许可和第三方声明见包内 `licenses/`。
