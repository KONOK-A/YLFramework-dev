# YLFramework 测试入口

本目录维护开发者侧的测试执行工具，不是正式 Runtime 或分发包的运行依赖。
所有模块声明集中在 `TestSuites.json`；唯一日常入口是 `Invoke-YLTests.ps1`。
测试叶由真实 NUnit / Unity Test Framework 发现，不维护复制的叶子名称或数量表。

## 开发目录

| 位置 | 职责 |
| --- | --- |
| `PackageSources/com.kano.ylframework*` | 正式 UPM 产品内容，测试宿主按声明安装。 |
| `Assets/Editor/YLFrameworkDev` | 仅开发仓库使用的产品资源作者工具。 |
| `Assets/Tests/YLFramework` | Unity 测试、验收入口、测试执行适配器和序列化夹具；作者支撑统一在 `Fixtures/Authoring`，程序集身份保持不变。 |
| `Assets/Settings` | 开发宿主的渲染配置，不是框架产品资源。 |
| `Tools/Testing/Common`、`Profiles`、`Tests` | 测试基础设施、引擎依赖基线及工具自检/专项入口。 |
| `Tools/Testing/Fixtures` | 不参与 Unity 导入的外部夹具准备脚本。 |
| `Tools/Excel/Restore-MiniExcel.ps1` | 固定来源的 Excel 依赖恢复，不升级版本或重建插件身份。 |
| `Tools/HybridCLR` | 当前发行链需要的原生来源准备、补丁身份与校验，不保留旧接收工程控制器。 |

PowerShell、Node 外部控制器不放在 `Assets` 中。固定接收工程、Stage9/B05、IDE 取证及旧 `.unitypackage` carrier 控制器已退役，历史实现由 Git 保留，不作为现阶段入口。正式验收使用本页声明的 UPM、模块与 Player 工具；旧历史结论不自动继承为当前通过。

## 前置

- Windows、PowerShell 7.6；使用 `pwsh -NoLogo -NoProfile -NonInteractive -File`。
- 显式指定 `-Unity` 和 `-WorkspaceRoot`，或只在当前进程设置 `YL_TEST_WORKSPACE`。
- Editor 按完整 ProductVersion 匹配 `TestSuites.json`，不能仅按主版本猜测兼容性。
- 普通离线编译沿用 Analyzer 锁定的 SDK `9.0.312` 和现有 NUnit `3.14.0`，不自动下载或升级。
- Save 的 BCL 专项明确使用已声明的 SDK `10.0.102` / Runtime `10.0.2`，缺少时直接报告前置不足，不切换到 Mono 冒充同一环境。
- Framework 模块需要声明中的现有插件、包锁和资产元数据。离线引用包含来源工程已导入的 UGUI 与 Json.NET，必须先完成真实 Unity 导入，工具不会伪造这些 DLL。
- Native 宿主只安装模块声明需要的包与源码，不恢复历史游戏或接收工程。图形模块不能使用无图形模式替代。

MCP 仅为开发工程工具，依赖声明与锁定结果保留在 `Packages/manifest.json` / `packages-lock.json`。`Assets/Plugins/NuGet/` 中的 DLL、`.meta`、安装状态及同级 `NuGet.meta` 由 MCP 恢复器生成，只在本机保留，不纳入 Git 或框架测试输入。普通 Editor 首次打开时会自动还原，需能够访问 OpenUPM 和 NuGet。

示例从当前进程环境变量取值并显式传参。运行者须先将它们设为本机实际 Editor 路径和明确的独立工作根，不复制其他机器或历史验收的路径：

```powershell
$unity = $env:YL_TEST_UNITY
$workspace = $env:YL_TEST_WORKSPACE
```

Unity 2022 与 Unity 6 使用各自真实可执行文件，支持的完整 ProductVersion 统一以 `TestSuites.json` 为准。它们与来源工程 `ProjectVersion.txt` 是不同身份；没有对应声明时不能仅替换路径声称兼容。

2022 自动化宿主的包基线位于 `Profiles/Unity2022`，这里只保存依赖配置，不是第二个 Unity 工程。人工兼容验证工程独立放在仓库外，导入正式框架包，不共享开发工程的源码目录或 Library。

## 操作

默认操作只列声明，不创建工作区，不编译、不运行测试、不启动 Unity：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1
```

离线逻辑发现与执行：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Discover -Suite Quick -Module Domain -Unity $unity -WorkspaceRoot $workspace
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Run -Suite Quick -Module Domain -Unity $unity -WorkspaceRoot $workspace
```

Unity 原生模块执行：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Run -Suite Daily -Module Foundation -Unity $unity -WorkspaceRoot $workspace
```

显式专项：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Run -Suite Special -Module Domain -Unity $unity -WorkspaceRoot $workspace
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Run -Suite Special -Module Save -Unity $unity -WorkspaceRoot $workspace
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Run -Suite Special -Module Resource -Unity $unity -WorkspaceRoot $workspace
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Run -Suite Special -Module Audio -Unity $unity -WorkspaceRoot $workspace
```

`Discover` 只发现并保存选集，状态是 `Completed`，不是测试通过。
`Run` 会重新发现当前真实叶子，再执行；可通过 `-Selection <selection.json>` 复用发现结果。
源文件、元数据、执行器、引用、包、配置或实际叶子改变时，旧选集必须拒绝，不能沿用旧数量判断通过。

`-Module` 必须明确存在，不能用任意 namespace 或未声明的 `All` 扩大范围。
`Integration`、`Special` 是层级分类，不表示每个模块都有这些执行配置；未声明的组合直接失败。

## 模块边界

| 模块 | 层级 | 真实边界 |
| --- | --- | --- |
| Reddot | Quick / Daily | 小型逻辑闭包与真实 UGUI 订阅生命周期。切片不能替代完整 Runtime 编译。 |
| Foundation | Daily / Special | 完整 Runtime / Editor 加基础测试；双次 Play 会话单独执行。 |
| Core | Daily | 完整 MVC / UI / 资源夹具；不隐式安装旧宿主扩展或原游戏资产。 |
| Domain | Quick / Daily / Special | Skill / Buff / Mission / Reddot / Logging 逻辑、作者及场景生命周期；无 Domain Reload 会话使用独立宿主。 |
| Behavior | Quick / Daily / Special | 日常Overlap / CharacterMotor / MovingPlatform / 两套FSM；Special在显式自有Desktop宿主执行两个原生窗口事件合同。 |
| Save | Special | 真实 NUnit 执行 BCL 合同；不会执行需要 Unity JsonUtility 的叶子来伪造 native 验收。 |
| Data | Daily / Special | Save native JSON、Localization、DataAssets 作者和资源生命周期；会话前置不能由主工程代替。 |
| Excel | Daily | 显式安装现有可选适配与 MiniExcel，执行真实 XLSX、AssetDatabase 和预览合同；只声明 EditMode。 |
| Action | Daily | Action / Observable 的真实取消、释放、池化复用、场景结束与屏幕过渡；共享依赖检查不借用其他测试类。 |
| Audio | Daily / Special | 日常 AudioKit / source / owner / 作者与资源生命周期；Special执行连续Play、authority/provider与无输出采样的Bundle会话，真实音频输出/DSP仍独立。 |
| Resource | Daily / Special | 有界真实Catalog / Codec / Bundle构建；Special保留原场景安全、双次Play及租约隔离断言，使用碰撞拒绝的短宿主名。 |

准确范围以当前 `TestSuites.json` 和发现产物为准。模块声明不是所有平台或所有环境已验收的承诺。

Resource原生构建与Audio的原生Bundle会话受Windows Mono路径预算限制，需要足够短的工作根，不要放入深层恢复根；短宿主名只是目录标签，完整key与owner收据仍必须一致，冲突直接拒绝，不能减少归属检查或放宽产品路径限制。

Resource/Audio的Special使用独立`SessionScenes`宿主身份：只接受干净空启动场景，先打开自有已保存空场景，再核实UTF实际创建的布局。
UTF若生成默认Camera/Light场景，只能在测试尚未开始的回调中、严格核实根与组件后替换；未知、脏或非预期场景拒绝，不自动保存用户内容。
`session-scenes.json`记录实际双场景与活动空场景前置，CLI合同要求该证据。实际UTF版本写入结果，manifest请求版本不等于Unity最终解析版本。
Audio宿主显式安装原`AudioAcceptanceEntry.cs`供初始化/中断清理合同使用，不因此执行其中完整DSP流程，也不将它加入生产包。

Core 的默认资源、序列化所有权、日志无副作用与正式 Analyzer 合同属于 Daily，不要求安装已退休宿主扩展或宿主资源。
跨模块作者合同保留 `YL.Integration.CrossModuleAuthoring` 类别与原断言，不进入 Core 的日常集。
主 Editor 专属保护用例使用 `YL.Special.MainEditor`，不会进入无人值守日常集。
真实窗口焦点/输入派发使用 `YL.Special.NativeInput`，通过 `Run -Suite Special -Module Behavior` 执行；宿主使用独立Desktop身份、显式`-ylTestInteractive`和真实`isBatchMode=false`，不接管用户已打开的Editor。需要真实图形桌面并串行执行，不能与其他窗口验收争抢焦点；未提供前置不能当作batch通过。原Mouse/Focus/Delete/Menu/坐标断言保留，不等于物理OS鼠标设备自动化。
右键合同在真实事件中捕获原菜单动作后清空显示项，避免Unity6的阻塞原生菜单停止测试更新；仍由原动作执行状态与子状态机创建，不直接调用工厂或跳过原生派发。
音频会话/资源集成使用 `YL.Special.AudioSession`；真实输出与原生Bundle音频采样仍为 `YL.Special.AudioNative`，不计入会话专项成功。缺少设备条件不得改写测量断言。
Daily 的类别排除必须出现在发现摘要的 `excludedCategories` / `excludedTests` 中，不能伪装成通过或未批准的 Skip。
需要额外上下文的合同必须显式准备并验证该上下文，不能为全绿恢复历史 SDK 或删掉断言。

## 专项边界

| 来源 | 保留边界 |
| --- | --- |
| Resource 工作台、发布、Cost/Perf/多目录证据夹具 | 保留 Unity 夹具和辅助源码；外部准备脚本在 `Tools/Testing/Fixtures/Prepare-ResourceCostEvidence.ps1`。固定日期、工作区和证据根不是通用入口，不自动重演或覆盖历史记录。真实受控资产/进程前置须另行核实。 |
| Resource native crypto / TLS / 下载矩阵 | 引擎及平台适配必须有对应实际证据；普通逻辑通过不表示所有原生后端受支持，不自动修改信任、证书或网络策略。 |
| Audio DSP / 原生音频输出 | 原生测量断言保留，需要真实设备；AudioSession与日常集都不能替代DSP/可听输出验收。 |
| OS / IDE / 插件与物理输入设备 | 真实操作系统或外部宿主为独立前置；两个已声明NativeInput窗口用例不能替代这些专项，不创建替身或控制用户Editor。 |
| Domain / Data / Equipment 作者支撑 | `Assets/Tests/YLFramework/Fixtures/Authoring` 由当前模块显式装入；保留被测试反射使用的类型与程序集，不恢复旧安装脚本或接收工程，不进入生产包。 |
| DomainExcel / DOTween 等可选适配 | 当前源码与宏前置保留，只有存在对应声明和可用依赖时才执行；旧复合归档脚本的历史结果不等于当前可选集成验收。 |

这些边界不是Skip白名单，不能用于把已选择的失败改报通过。
当前自动化范围以模块声明、真实发现及结果为准；专项未执行须在交付中单列。

## 隔离与身份

工作根不能是来源仓库、盘符根或来源仓库的祖先；已存在但没有合法 owner 的目录不接管。
工具创建 `.yl-workspace.json`、目录 `.yl-owner.json` 和独占 `.lease`，并将仓库身份绑定到工作根。
后续路径检查拒绝链接、junction、越界路径、未知子项和身份不匹配。

```text
<WorkspaceRoot>/
  Hosts/    可复用的自有 Unity 宿主
  Cache/    源码/工具链指纹绑定的构建缓存
  Results/  每轮唯一结果目录
```

Native Run 按选集中的模式分别启动 Editor，复用同一宿主但不复用上一个模式的进程状态。
这是为了隔离编辑测试留下的临时场景和静态状态；工具不自动保存、丢弃或关闭主工程场景。
主工程开着不妨碍运行自有宿主，但不能对同一个项目启动第二个 Editor。

SortingLayer、无 Domain Reload 等前置只能由声明中的自有宿主准备；实际设置文件加入输入身份。
普通生产 Player 不应包含测试程序集或验收探针；显式 Player 验收的 `YL_*_ACCEPTANCE` 符号或产品根外独立探针只在对应自有宿主设置。
Editor 图、辅助编译、真实测试、Player 构建、可选集成和 DSP/OS/IDE 专项必须分开报告。

若中断后宿主或缓存内容与收据不一致，工具会保留并拒绝复用。
不要修改 owner、伪造指纹、清空未知目录或关闭校验来继续。
应保留问题证据，选择新的明确自有工作根重新准备；Unity Mono 的深层文件测试还要求足够短的工作根，避免超过 Windows 路径前置。

## 结果与进程

每轮保存独立 `summary.json`、真实 `selection.json`、NUnit XML 和原始进程日志。
失败保留具体阶段及结果路径；输出中不能泄露完整 Hub/CIM 命令行的临时令牌。

只有同时满足下面条件才报告 `Passed`：

- 当前输入与发现选集身份一致，实际模式符合声明。
- 正式入口在执行前验证完整声明模式集合；整组PlayMode因asmdef条件消失也会失败，不只要求剩余选集非空。Special只要求自身声明的模式，不强加两种模式或固定叶数。
- 每个预期叶子恰好出现一次，没有多出、漏出、重复或错误程序集。
- 选集非空；没有 Failed、Inconclusive、Explicit 或未批准的 Skip。
- XML 内容与真实发现一致，不仅检查 XML 存在或根节点 Passed。
- 对应进程实际正常退出，观察与清理完成，没有超时、取消、强制回收或残留后代。

取消时回收工具自己创建的进程树，但状态仍是取消，不因已有通过 XML 改报成功。
不会停止用户 Editor、Hub 或未知进程，不放宽后代收尾门槛或按版本豁免失败。

## 保留与清理

每个组保留最近 1 份普通通过与最近 3 份非通过记录。
Tooling 结果按 Suite / 模块 / 完整 Editor 身份分组，避免两版或专项互相覆盖；旧历史桶不自动领养或重命名。
`-KeepArtifacts` 固定本次记录；固定、使用中、未知身份或结果不完整的内容不能当普通可删结果处理。

清理默认只预览，只有 `-Apply` 才处理当前模块组中符合保留规则的自有输出：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Clean -Suite Daily -Module Foundation -WorkspaceRoot $workspace
```

此操作不扫描或清理其他工作区、历史归档、原游戏或主工程缓存。

## 工具自检

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLTesting.ps1 -TemporaryRoot $env:TEMP
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLGitHygiene.ps1
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLQuick.ps1 -WorkspaceRoot $workspace -Unity $unity -Module Domain
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLUnity.ps1 -WorkspaceRoot $workspace -Unity $unity -Module Core
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLUnitySafety.ps1 -WorkspaceRoot $workspace -Unity $unity
```

Git 卫生入口只读 Git 索引：拒绝整个 `.kilo/`、本机 AI 指令与工具规则、本机缓存、连接配置、MCP NuGet 自动恢复目录、商业 Editor Enhancers、生成工程和 HybridCLR 本机输入被跟踪，检查已跟踪路径不命中忽略规则，并保留两个手维护 Analyzer SDK 项目。它不读取配置值、不清理资产，不把空目录 `.meta` 简单判为孤立文件，也不代表 Unity 编译或测试通过。

安全回归在自有源码夹具中验证真实失败、增加叶子后拒旧选集、取消、进程清理与同宿主恢复。
还会实际排除整个PlayMode程序集验证正式CLI拒绝“漏一整组模式”；owner、宿主回执/响应与轮换摘要先检查JSON字段类型，再比较身份，不允许布尔强转为合法字符串。
`Tests/Test-YLDistributionGuards.ps1`在新建自有宿主验证缺请求、错误owner及交互调用均抛回调用者；分发入口只有完成全部归属/输出校验后，才有退出其自身进程的权限。
`Test-YLAssemblyLayout.ps1` 消费实际 Unity 编译图；静态图通过不是功能或 Player 构建通过。
其输入图必须标明来源工程、实际 Editor、采集时间和编译状态，不能用手写理想图冒充。

Formatter 与 Analyzer 保留独立入口，不并入 UTF，也不改变默认部署行为。格式与工具边界见 [CodingStyle](../../PackageSources/com.kano.ylframework/Documentation~/CodingStyle.md#格式工具)，诊断重建与验证见 [Attributes](../../PackageSources/com.kano.ylframework/Documentation~/Modules/Attributes.md#重建与验证)。

旧 Verify/Offline/接收工具的辅助编译结果不能替代上述测试合同。
正式使用说明只记当前入口和前置；阶段计数、一次性失败、迁移取舍与未验收范围记在原任务 Plan。

## Registry 与冻结产物

`Test-YLDistribution.ps1` 与 `Test-YLIntegrationPackages.ps1` 共用既有 owner、lease、process 和结果合同，不采用 npm publish 或开发宿主依赖作为发行证明。自有包的五份登记模板位于 `Tools/Packaging/OpenUPM/`，使用 OpenUPM `trackingMode: githubRelease`、Git Tag 字面过滤前缀 `upm/` 和各自的稳定资产匹配前缀 `<包ID>-`，不是仓库根目录 Git tracking。该匹配字段不展开 `{version}`；实际预览 Tag 仍为 `upm/v0.1.0-preview.1`，实际资产仍为 `<包ID>-0.1.0-preview.1.tgz`。这些模板本身不代表已在 OpenUPM 登记或发布。

### 打包与回执

固定工具链为 Node `22.23.3`、其随附 npm `10.9.9` 和 Windows PowerShell Core `7.6.x`。使用 CI 或工具拥有的便携环境；不要升级全局 Node、修改用户 npmrc、携带令牌，或把本地工具版本不符当作允许更换包版本的理由。

```powershell
$commit = (git rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'A real full source commit is required.' }
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Packaging/Build-YLRegistryPackages.ps1 `
  -Repository $repository -SourceCommit $commit -OutputRoot $packageWorkspace
```

`$repository` 必须是实际 Git 仓库根，`$packageWorkspace` 是其外部的明确绝对工作根；未知已有目录不领养。入口从独立 checkout 检出完整 40hex 提交、物化 LFS，再以隔离 npm 配置/cache 执行两次 `npm pack --ignore-scripts --offline`。当前未提交的 README、manifest 或代码不会被复制进固定提交候选。五包发布白名单分别按真实目录检查，不把源码 `.gitattributes` 当 tarball 必需内容。

结果 `artifacts/` 保存五个 `<包ID>-0.1.0-preview.1.tgz` 和 `release-manifest.json`。回执包含源提交、固定工具链、Local 或 CI producer、包依赖、tarball SHA-256 / SHA-512 integrity、完整逐文件原字节摘要及语义内容摘要。只有 `package.json` 做严格 JSON 的递归 ordinal 规范化；非 manifest 文件原字节不可变化。tar 在提取前审计 `package/` 根、路径、重复项、大小写、链接、条目类型、数量、大小和完整性，不信任请求提供的逐文件列表来替代真实负载。

### 两种新来源

| 来源 | 必需参数 | 自有包直接声明 | 证据边界 |
| --- | --- | --- | --- |
| `LocalTarball` | `-ReleaseManifest` | 从回执同目录取五个固定文件名；所选自有闭包全部使用真实 `file:<绝对tgz>` locator | `ArtifactPreflight`，不是 Registry 或自动间接依赖证明 |
| `Registry` | `-RegistryUrl https://package.openupm.com -RegistryVersion 0.1.0-preview.1 -ReleaseManifest` | 只声明所选唯一入口；主包、Excel、官方依赖与 SDK 均由 UPM 解析 | 联合核验 Registry metadata、下载字节、冻结 Release、PackageInfo 和 generated lock |

`File` / `Git` 拒绝以上 Registry 参数；新来源拒绝 Git 参数。`LocalTarball` 不接受 Registry URL/version 来混淆身份。Registry scope 仅覆盖 `com.kano.ylframework`；HybridCLR 额外覆盖 `com.code-philosophy.hybridclr`，不将 `com.unity` 路由到 OpenUPM。

使用从已批准 CI 候选原样下载的真实回执，不能手写 sourceCommit 或以同版本自行重打包替换：

```powershell
$releaseManifest = (Resolve-Path -LiteralPath $env:YL_RELEASE_MANIFEST).Path
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 `
  -Unity $unity -WorkspaceRoot $workspace -PackageSource LocalTarball -ReleaseManifest $releaseManifest -RunPlayer
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 `
  -Unity $unity -WorkspaceRoot $workspace -PackageSource Registry -RegistryUrl https://package.openupm.com `
  -RegistryVersion 0.1.0-preview.1 -ReleaseManifest $releaseManifest -RunPlayer
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLIntegrationPackages.ps1 `
  -Unity $unity -WorkspaceRoot $workspace -Integration DomainExcel -PackageSource Registry `
  -RegistryUrl https://package.openupm.com -RegistryVersion 0.1.0-preview.1 -ReleaseManifest $releaseManifest
```

分别指定标准版 `2022.3.62f3` 与 `6000.3.27f1`，各执行主包、Excel、DomainExcel、DOTween、HybridCLR 五个入口；China `2022.3.62f3c1` 是独立 profile，不能按同样 FileVersion 前缀冒用。`-Integration` 可选 `Excel`、`DomainExcel`、`DOTween`、`HybridCLR`。主包还须真实 Windows64 Mono 构建与运行，默认资源、GUID、Prefab 和现有行为断言不减少。

每次正向首次安装使用新 host/cache；先记录空 cache，再比对实际 Editor 进程的三项 UPM cache 环境。独立 Registry metadata / tarball 检查下载在结果目录中，不预热 UPM cache。重开、卸载、暖缓存重复解析和阴性重试均不计为另一轮冷安装。报告联合检查 name/version/source/packageId/resolvedPath/isDirectDependency/registryUrl/dependencies、完整锁图及合法受管 `_fingerprint`，不从包版本字符串推断来源。

DOTween 默认 `-DOTweenSdkMode Required`，必须取得并核对批准的 Standard archive、实际程序集、路径、摘要、Modules 和 `DOTWEEN`。指定 `-DOTweenSdkMode Absent` 时拒绝 archive、不下载或投放 SDK、不设置宏，并要求包装程序集在 Editor/Player 图均缺席，结果为 `NegativeNoSdk`，不计正向集成。无法取得必需 SDK 是前置不足，不是 Required 通过。

受管 `_fingerprint` 按实际来源格式验证：Unity 6 的 LocalTarball 安装写入 32hex，Git / Registry 及官方 BuiltIn 缓存观测使用 40hex；Unity 2022 的 LocalTarball 可以没有该字段。只在对应已验证安装来源中规范化这个已知字段，不向 File 或源 tarball 开放，也不忽略未知 `_` 字段。字段不是包内容身份，冻结负载摘要及完整文件集合仍须匹配。

HybridCLR 的新来源以官方 Registry SDK `9.0.3` 为间接依赖；先核对独立 metadata 的仓库和 revision、tarball integrity、许可与实际安装内容。`repository.revision` 只是 metadata 来源声明，不冒充 Git checkout SHA。该阶段不配置或运行 native Installer、IL2CPP 基座或热更 Player；旧 File/Git native 专项仍是独立显式流程。

默认图继续拒绝 Tests、MCP、未导入 Sample 和未授权 Assets 源。正向样例/作者工具阶段在另一个自有安装宿主显式 Import，并记录真实生成数据和元数据；卸载使用独立中立探针与宿主，不引用框架/Json.NET/SDK，也不靠删除用户代码或数据来通过。相同版本暖重开不叫跨版本升级。

### 公开批准点

GitHub `Repository Tooling (No Unity)` 对新合同和固定提交打包运行工具门禁；只有成功的 `main` push run 才保存五包和回执的只读候选 artifact。PR / workflow_dispatch / 本地 producer 不等于已批准候选，工具门禁也不是 Unity 安装或 Player 通过。

1. 先完成获准的代码 PR / main CI，固定主线源 SHA、workflow / run / attempt、artifact 和六个文件的字节身份；用同一批字节执行两版 LocalTarball 预检。任何必需案例失败或前置不足，均不是 `ReadyForPublish`。
2. 重新检查五个包 ID、目标版本、OpenUPM 定义、Tag 和 Release。与同一来源/字节匹配的登记可以复用；只有内容更正或真实不兼容占用才考虑五包同步递增预览版本，不能因临时服务故障 bump。已发布同版本内容不覆盖。
3. 在对外写入前，独立批准精确源提交、预览 Tag、五包、回执摘要和全部候选资产摘要。批准前不创建公开 Tag / Release、不提交 OpenUPM 登记、不直接 npm publish。Code commit、push、PR、Tag、Release 与 OpenUPM 写入是不同授权。
4. 获准发布时使用普通 annotated Tag，并核对 peeled commit；annotated Tag object SHA 不等于其目标提交。Release 附件必须与已批准 CI artifact 字节相同，缺失或不符即停止，不就地重包或替换已公开附件。
5. 五个版本在 Registry 都就绪后再执行两版真实最小安装、正负 SDK、样例、暖重开、中立卸载和 Player 矩阵。未就绪保留公开阶段前置阻塞，不用抽取源码、Git 或 LocalTarball 结果替代。

## Player 运行冒烟

沿用正式 UPM 分发入口，默认检查安装来源、内容与编译图，再做普通 Windows64 Mono 构建。`-Backend` 可选 `Mono` 或 `IL2CPP`；显式 `-RunPlayer` 才安装运行探针并增加两次真实启动：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 `
  -Unity $unity -WorkspaceRoot $workspace -RunPlayer

pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 `
  -Unity $unity -WorkspaceRoot $workspace -Backend IL2CPP -RunPlayer -TimeoutSeconds 2400
```

- 先完成真实 UPM 安装及无 Tests/UTF 的图验证，仅向自有接收宿主的产品根外安装 `Tools/Testing/PlayerSmoke.cs`、独立 asmdef 及 Resources 文本，不修改正式包或主工程场景。旧 unitypackage 导出/导入已退役。
- 使用`BuildOptions.None`构建小型专用场景，实际验证初始化、Resources加载、红点通知/释放、live Lease的Busy保护、归还后零计数及资源服务关闭。IL2CPP还核实真实GameAssembly、global-metadata.dat及实际ManagedStripped的生产程序集边界。
- 同一产物启动两次，分别写`PlayerRun`/`PlayerRepeat`。要求普通exit0、Job后代清零、版本/run/host/实际PID与编译宏后端匹配、完整有序阶段、无运行错误且产物SHA256未变，不能只凭日志PASS或旧报告接受。JSON字段类型严格校验，不允许布尔或数字通过PowerShell字符串强制转换冒充身份或阶段。
- 构建与运行分别记录在`Build/report.json`、`PlayerRun/result.json`和`PlayerRepeat/result.json`；超时、缺报告、残余租约、错误身份及非普通退出均不是通过。
- 普通冒烟采用`-batchmode -nographics`，证明对应Mono/IL2CPP后端的本场景逻辑与Resources运行链，不证明渲染、真实音频输出、任意反射类型保留或其他平台。默认分发构建仍不包含探针；HybridCLR需要下面独立显式模式。

### 两版定点

每次调用只接受一个精确 `-Unity`，分别运行标准 `2022.3.62f3` 和 `6000.3.27f1`；旧 `-ImportUnity` 参数被拒绝，不再通过一个 Editor 导出 unitypackage 后交给另一个导入。Registry/LocalTarball 两版使用同一冻结回执及资产字节，每轮保存各自 host/cache/process 和实际依赖身份。定点结果不外推其它补丁版本或平台，HybridCLR native 仍须匹配自身 Editor / runtime / 基座。

### HybridCLR 原生闭环

仅在明确提供已存在、已核实的原生源时启用，要求同时指定`-Backend IL2CPP -RunPlayer -HybridCLR -NativeSource`。主工程的热更名单、API级别和本地基座不会被配置成测试状态。

```powershell
$nativeSource = $env:YL_HYBRIDCLR_NATIVE_SOURCE
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 `
  -Unity $unity -WorkspaceRoot $workspace -Backend IL2CPP -RunPlayer -HybridCLR `
  -NativeSource $nativeSource -TimeoutSeconds 2400 -PlayerTimeoutSeconds 180
```

- File/Git native 模式显式安装主包、HybridCLR 叶子及当前声明的精确 SDK，先完成无 Tests/UTF 的 UPM 图验证，再在自有宿主执行产品根外的 `HybridCLREntry.cs` / `HybridCLRSmoke.cs`。默认 Mono / 普通 IL2CPP 不安装该选装包；Registry/LocalTarball native 模式被明确拒绝。
- 原生源只读验证后，由官方`InstallFromLocal`在新宿主内复制已安装Editor的本地基座，不下载新原生源、不修改Editor/PackageCache/主工程Library。2022验证固定双Git提交、实际已合并文件及许可证；Unity6.3额外要求`-NativeSourceManifest`且与`-NativeSource`匹配，通过现有Verify核实`ParameterOwner`维护变体。不能把该变体成功外推为未修改官方6.3源成功。
- 不恢复旧接收工程或复用旧热更DLL/Player。调用正式`HybridCLRExampleAssets.Create`和`HybridCLRBuildPipeline.BuildBasePlayer`，登记Entry/Support样例，实际经过编译、Strip AOT、完整桥接、资源发布、部署、原生Player及不可变baseline冻结。阶段证据在`HybridBuild/stages.json`，最终回执在`HybridBuild/report.json`。
- 原生Bundle与发布快照有Windows Mono路径预算。使用足够短的工作根；短宿主标签已存在即拒绝，完整run/host身份仍独立绑定。baseline、运行缓存与Player分别在独立owner/lease的短Cache子目录，Player实际路径以回执`executable`为准，不排除Cpp备份、跳过长文件或缩短完整指纹制造通过。
- 原生样例使用真实Direct3D11设备，不加`-nographics`。要求Native/非Simulation、完整metadata和热更集匹配新基座、Module/async/热更Prefab执行、解释器泛型反射调用、重复load复用、错误基座在Manifest阶段拒绝；最后关闭业务实例/Module和资源权威，计数归零并普通退出。关闭资源不代表在同一进程卸载热更程序集。
- 两次新进程复用同一冻结Player，各有独立报告。缺少模块、C++工具链、已核实原生源、真实设备或任一阶段失败，均不改报通过；不自动安装外部工具链，不给Job清理或退出失败加版本豁免。
- 这是一条Windows64、Release/Low stripping、固定SDK与已验证原生源的最小闭环，不覆盖热更V2替换/回滚、任意AOT泛型、全部裁剪级别、NativeInput、DSP、移动平台或未声明SDK。原生源许可证随本轮`NativeInput`保存；本地产物是验收证据，不是正式游戏发布包。

## CI 分层

UPM 安装验收区分 `File`、工具拥有的 `GitFixture` 和真实远端 `GitRemote`，不能互相替代。普通负载文件保持逐字节摘要；只有 `package.json` 按 JSON 属性语义规范化，因为 UPM 会重排格式并在 Git 安装中增加 `_fingerprint`。该注解仅允许格式正确的实际 Git 安装，源包和 File 输入不接受它；版本、依赖和其他属性仍完整纳入摘要。包内 `.gitattributes` 约束子目录检出，避免调用者 `autocrlf` 改写已批准负载。

`Test-YLDistributionManifest.ps1`、`Test-YLSampleHost.ps1` 与 `Test-YLIntegrationPackagesNoUnity.ps1` 分别验证 manifest 语义、示例副本所有权和 SDK 前置。这些是 NoUnity 合同，不代表实际安装或运行通过。`ResourceSamples` 会显式导入所声明示例，并记录绑定宿主、完整源输入和文件哈希的收据；修改过或未知的副本拒绝覆盖，旧包输入不能静默复用示例。

Core 的两项真实系统剪贴板断言在 `Special/CoreClipboard` 的工具拥有交互 Editor 中执行，批处理 `Daily/Core` 会明确报告对应排除项。桌面专项沿用同一断言，不用清空缓存、模拟剪贴板或无图形结果替代。资源构建与原生验收必须选足够短的独立工作根，保留 Windows Mono 路径预算检查，不使用链接或路径别名绕过所有权校验。

`.github/workflows/dotnet.yml` 在 Windows runner 运行 Git 索引卫生、格式、Analyzer 的 .NET 构建测试及测试工具合同自检。此工具层不安装 Unity、不需要 Unity 许可证，也不等同于主体 Unity 编译、UTF 或 Player 验收。

此工具层及按模块声明准备的独立框架测试宿主均不加载 MCP，无需恢复其 DLL。若以后在 Unity CI 中直接打开带 MCP 的开发工程，必须在编译前显式恢复依赖；当前 MCP 恢复器检测到 CI 环境时会跳过自动下载，不能把本机安装或已有缓存当作 CI 前置已满足。

Unity 测试层仍沿用同一CLI、模块声明和退出合同，不再维护另一套测试名单或结果解释器。
CI必须提供明确Editor/SDK/可选包与短的独占工作根；缺前置报告未运行，不安装未授权依赖或改报通过。
上传本轮summary、selection、XML与必要脱敏日志；取消同样保留真实终态，不用上传成功替代测试成功。
图形/OS/DSP、AOT/Player和可选SDK门保留各自前置，不能用普通无图形代理或纯逻辑门外推。
当前远端工作流只执行工具层；Unity 安装、许可密钥和部署流程不内建，原生与 Player 前置仍须显式提供。
