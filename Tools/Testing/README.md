# YLFramework 测试入口

本目录维护开发者侧的测试执行工具，不是正式 Runtime 或分发包的运行依赖。
所有模块声明集中在 `TestSuites.json`；唯一日常入口是 `Invoke-YLTests.ps1`。
测试叶由真实 NUnit / Unity Test Framework 发现，不维护复制的叶子名称或数量表。

## 前置

- Windows、PowerShell 7.6；使用 `pwsh -NoLogo -NoProfile -NonInteractive -File`。
- 显式指定 `-Unity` 和 `-WorkspaceRoot`，或只在当前进程设置 `YL_TEST_WORKSPACE`。
- Editor 按完整 ProductVersion 匹配 `TestSuites.json`，不能仅按主版本猜测兼容性。
- 普通离线编译沿用 Analyzer 锁定的 SDK `9.0.312` 和现有 NUnit `3.14.0`，不自动下载或升级。
- Save 的 BCL 专项明确使用已声明的 SDK `10.0.102` / Runtime `10.0.2`，缺少时直接报告前置不足，不切换到 Mono 冒充同一环境。
- Framework 模块需要声明中的现有插件、包锁和资产元数据。离线引用包含来源工程已导入的 UGUI 与 Json.NET，必须先完成真实 Unity 导入，工具不会伪造这些 DLL。
- Native 宿主只安装模块声明需要的包与源码，不恢复历史游戏或接收工程。图形模块不能使用无图形模式替代。

示例从当前进程环境变量取值并显式传参。运行者须先将它们设为本机实际 Editor 路径和明确的独立工作根，不复制其他机器或历史验收的路径：

```powershell
$unity = $env:YL_TEST_UNITY
$workspace = $env:YL_TEST_WORKSPACE
```

Unity 2022 与 Unity 6 使用各自真实可执行文件，支持的完整 ProductVersion 统一以 `TestSuites.json` 为准。它们与来源工程 `ProjectVersion.txt` 是不同身份；没有对应声明时不能仅替换路径声称兼容。

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
| Resource 工作台、发布、Cost/Perf/多目录证据夹具 | 保留原NUnit/辅助源码；固定日期、工作区和证据根不是通用入口，不自动重演或覆盖历史记录。真实受控资产/进程前置须另行核实。 |
| Resource native crypto / TLS / 下载矩阵 | 引擎及平台适配必须有对应实际证据；普通逻辑通过不表示所有原生后端受支持，不自动修改信任、证书或网络策略。 |
| Audio DSP / 原生音频输出 | 原生测量断言保留，需要真实设备；AudioSession与日常集都不能替代DSP/可听输出验收。 |
| OS / IDE / 插件与物理输入设备 | 真实操作系统或外部宿主为独立前置；两个已声明NativeInput窗口用例不能替代这些专项，不创建替身或控制用户Editor。 |
| 历史独立交付 / Equipment / 旧主Editor接收模板 | 仅在明确声明和满足前置时安装为测试支持，不在生产默认编译图；旧一次性入口不作为当前验收导航，历史owner、提交或路径不是当前授权。 |
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

Git 卫生入口只读 Git 索引：拒绝整个 `.kilo/`、本机 AI 指令与工具规则、本机缓存、连接配置、商业 Editor Enhancers、生成工程和 HybridCLR 本机输入被跟踪，检查已跟踪路径不命中忽略规则，并保留两个手维护 Analyzer SDK 项目。它不读取配置值、不清理资产，不把空目录 `.meta` 简单判为孤立文件，也不代表 Unity 编译或测试通过。

安全回归在自有源码夹具中验证真实失败、增加叶子后拒旧选集、取消、进程清理与同宿主恢复。
还会实际排除整个PlayMode程序集验证正式CLI拒绝“漏一整组模式”；owner、宿主回执/响应与轮换摘要先检查JSON字段类型，再比较身份，不允许布尔强转为合法字符串。
`Tests/Test-YLDistributionGuards.ps1`在新建自有宿主验证缺请求、错误owner及交互调用均抛回调用者；分发入口只有完成全部归属/输出校验后，才有退出其自身进程的权限。
`Test-YLAssemblyLayout.ps1` 消费实际 Unity 编译图；静态图通过不是功能或 Player 构建通过。
其输入图必须标明来源工程、实际 Editor、采集时间和编译状态，不能用手写理想图冒充。

Formatter 与 Analyzer 保留独立入口，不并入 UTF，也不改变默认部署行为。格式与工具边界见 [CodingStyle](../../Assets/YLFramework/Documentation/CodingStyle.md#格式工具)，诊断重建与验证见 [Attributes](../../Assets/YLFramework/Documentation/Modules/Attributes.md#重建与验证)。

旧 Verify/Offline/接收工具的辅助编译结果不能替代上述测试合同。
正式使用说明只记当前入口和前置；阶段计数、一次性失败、迁移取舍与未验收范围记在原任务 Plan。

## Player 运行冒烟

沿用正式包分发验证入口，默认检查导出、裸导入与普通Windows64 Mono构建。`-Backend`可选`Mono`或`IL2CPP`；显式`-RunPlayer`才安装运行探针并增加两次真实启动：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 `
  -Unity $unity -WorkspaceRoot $workspace -RunPlayer

pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 `
  -Unity $unity -WorkspaceRoot $workspace -Backend IL2CPP -RunPlayer -TimeoutSeconds 2400
```

- 先完成真实官方导出及无Tests/UTF的裸导入，随后仅向自有接收宿主的产品根外安装`Tools/Testing/PlayerSmoke.cs`、独立asmdef及Resources文本，不修改正式包或主工程场景。
- 使用`BuildOptions.None`构建小型专用场景，实际验证初始化、Resources加载、红点通知/释放、live Lease的Busy保护、归还后零计数及资源服务关闭。IL2CPP还核实真实GameAssembly、global-metadata.dat及实际ManagedStripped的生产程序集边界。
- 同一产物启动两次，分别写`PlayerRun`/`PlayerRepeat`。要求普通exit0、Job后代清零、版本/run/host/实际PID与编译宏后端匹配、完整有序阶段、无运行错误且产物SHA256未变，不能只凭日志PASS或旧报告接受。JSON字段类型严格校验，不允许布尔或数字通过PowerShell字符串强制转换冒充身份或阶段。
- 构建与运行分别记录在`Build/report.json`、`PlayerRun/result.json`和`PlayerRepeat/result.json`；超时、缺报告、残余租约、错误身份及非普通退出均不是通过。
- 普通冒烟采用`-batchmode -nographics`，证明对应Mono/IL2CPP后端的本场景逻辑与Resources运行链，不证明渲染、真实音频输出、任意反射类型保留或其他平台。默认分发构建仍不包含探针；HybridCLR需要下面独立显式模式。

### HybridCLR 原生闭环

仅在明确提供已存在、已核实的原生源时启用，要求同时指定`-Backend IL2CPP -RunPlayer -HybridCLR -NativeSource`。主工程的热更名单、API级别和本地基座不会被配置成测试状态。

```powershell
$nativeSource = $env:YL_HYBRIDCLR_NATIVE_SOURCE
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 `
  -Unity $unity -WorkspaceRoot $workspace -Backend IL2CPP -RunPlayer -HybridCLR `
  -NativeSource $nativeSource -TimeoutSeconds 2400 -PlayerTimeoutSeconds 180
```

- 普通包必须先通过原导出及无Tests/UTF的裸导入，然后才向自有接收宿主安装可选HybridCLR叶子、当前声明的精确SDK及产品根外的`HybridCLREntry.cs`/`HybridCLRSmoke.cs`。默认Mono/普通IL2CPP模式不安装该可选叶子。
- 原生源只读验证后，由官方`InstallFromLocal`在新宿主内复制已安装Editor的本地基座，不下载新原生源、不修改Editor/PackageCache/主工程Library。2022验证固定双Git提交、实际已合并文件及许可证；Unity6.3额外要求`-NativeSourceManifest`且与`-NativeSource`匹配，通过现有Verify核实`ParameterOwner`维护变体。不能把该变体成功外推为未修改官方6.3源成功。
- 不恢复旧接收工程或复用旧热更DLL/Player。调用正式`HybridCLRExampleAssets.Create`和`HybridCLRBuildPipeline.BuildBasePlayer`，登记Entry/Support样例，实际经过编译、Strip AOT、完整桥接、资源发布、部署、原生Player及不可变baseline冻结。阶段证据在`HybridBuild/stages.json`，最终回执在`HybridBuild/report.json`。
- 原生Bundle与发布快照有Windows Mono路径预算。使用足够短的工作根；短宿主标签已存在即拒绝，完整run/host身份仍独立绑定。baseline、运行缓存与Player分别在独立owner/lease的短Cache子目录，Player实际路径以回执`executable`为准，不排除Cpp备份、跳过长文件或缩短完整指纹制造通过。
- 原生样例使用真实Direct3D11设备，不加`-nographics`。要求Native/非Simulation、完整metadata和热更集匹配新基座、Module/async/热更Prefab执行、解释器泛型反射调用、重复load复用、错误基座在Manifest阶段拒绝；最后关闭业务实例/Module和资源权威，计数归零并普通退出。关闭资源不代表在同一进程卸载热更程序集。
- 两次新进程复用同一冻结Player，各有独立报告。缺少模块、C++工具链、已核实原生源、真实设备或任一阶段失败，均不改报通过；不自动安装外部工具链，不给Job清理或退出失败加版本豁免。
- 这是一条Windows64、Release/Low stripping、固定SDK与已验证原生源的最小闭环，不覆盖热更V2替换/回滚、任意AOT泛型、全部裁剪级别、NativeInput、DSP、移动平台或未声明SDK。原生源许可证随本轮`NativeInput`保存；本地产物是验收证据，不是正式游戏发布包。

## CI 分层

`.github/workflows/dotnet.yml` 在 Windows runner 运行 Git 索引卫生、格式、Analyzer 的 .NET 构建测试及测试工具合同自检。此工具层不安装 Unity、不需要 Unity 许可证，也不等同于主体 Unity 编译、UTF 或 Player 验收。

Unity 测试层仍沿用同一CLI、模块声明和退出合同，不再维护另一套测试名单或结果解释器。
CI必须提供明确Editor/SDK/可选包与短的独占工作根；缺前置报告未运行，不安装未授权依赖或改报通过。
上传本轮summary、selection、XML与必要脱敏日志；取消同样保留真实终态，不用上传成功替代测试成功。
图形/OS/DSP、AOT/Player和可选SDK门保留各自前置，不能用普通无图形代理或纯逻辑门外推。
当前远端工作流只执行工具层；Unity 安装、许可密钥和部署流程不内建，原生与 Player 前置仍须显式提供。
