# 测试工具说明

本目录维护开发者侧的测试执行、隔离与结果检查，不是产品 Runtime 或使用工程的依赖。模块与工具链声明集中在 [TestSuites.json](TestSuites.json)，日常入口为 [Invoke-YLTests.ps1](Invoke-YLTests.ps1)。真实测试叶由 NUnit / Unity Test Framework 发现，不维护复制的测试数量表。

## 选择验证层级

| 层级 | 用途 | 不证明什么 |
| --- | --- | --- |
| NoUnity 工具合同 | 校验脚本、包清单、元数据与内容边界 | Unity 编译、安装和框架行为 |
| Quick | 使用真实 NUnit 的离线逻辑测试 | Unity 时间、物理、窗口或 Player |
| Daily | 独立宿主中的 Unity EditMode / PlayMode | 所有专项或所有平台 |
| Special | 明确声明的会话、图形、设备、BCL 或 Player 专项 | 未执行的其他模块与平台 |
| 分发与安装 | 包来源、内容、程序集图及显式 Player 冒烟 | 完整游戏行为或任意 SDK 支持 |

先执行与改动最相关的检查，再按影响扩大范围。包构建、LocalTarball/Registry 验证和发行准备集中见[打包工具说明](../Packaging/README.md)，这里维护测试执行与结果约定。

安装入口还会独立编译 Windows x64 Player 脚本，记录产物哈希及实际 AssemblyRef，并递归审计实际引用到的预编译 DLL。缺失或同名异内容的依赖、Tests/Acceptance、MCP、Editor 与测试依赖均拒绝；不能借用同名 Editor IL 证明 Player 安全。Unity 安装目录内的编译引用是有路径、身份与哈希记录的平台边界，回执标记 `UnityPlatformReference`、`referencesInspected=false`，不伪装成已检查且为空的引用表。此脚本编译不代表 Player 构建或运行通过。

Unity 提供给编译器的候选引用可能同时出现在 `assemblyReferences` 和 `compiledAssemblyReferences`，不等于 asmdef 明确声明或实际 IL 使用了测试依赖。产品及已批准 SDK 的声明检查覆盖 `references`、`precompiledReferences` 和解析后的 GUID 引用；显式测试依赖不因“当前未使用”而豁免。上游分类单独写入 `upstreamInfrastructure`：

- 官方 `com.unity.test-framework` 的非 auto-referenced `UnityEngine.TestRunner` 只有在受信来源、源码归属正确，且实际 Player 脚本/预编译依赖闭包未消费它时，才可记为 `UnconsumedOfficialTestInfrastructure`。
- HybridCLR 仍只接受现有批准的 9.0.3 来源、提交或 Registry 合同；`HybridCLR.Editor`/`HybridCLR.Runtime` 的候选测试引用须无显式声明且有分侧实际 IL 证据，才记为 `ApprovedSdkUnusedCompilerTestReferences`。Editor 证据必须对应本轮 `Library/ScriptAssemblies` 的真实文件，Player 使用独立脚本产物，未知第三方不享受此分类。

上述分类不允许本方 Tests/Acceptance、MCP 或产品实际测试依赖进入消费者，不代表 Native/热更新 Player 或全部第三方 SDK 矩阵通过。

### 模块结项必需项

框架模块开发和功能改动必须包含当前使用的受支持 Unity/实际目标平台的 **IL2CPP Player 构建及实际运行验收**。Editor、Mono Player 或 NoUnity CI 均不能替代；只构建成功、只声明后端或只生成报告也不算完成。缺少工具链、构建失败、Player 未运行或未普通退出时，IL2CPP 验收仍为未完成，不能宣布模块已全部结项。

优先复用现有 PlayerNative 与模块夹具；没有模块级入口时先补入口和对应回归，不退回到只跑 Daily。报告分列 Editor、IL2CPP 构建和 Player 运行，记录实际后端、裁剪/构建模式、目标、输入指纹与进程结果。此要求不意味着每次重跑全部 Unity 补丁/平台矩阵；纯文档修改仍按事实、链接与差异检查，不冒充发生了新的平台验收。

## 前置

- Windows 与 PowerShell Core `7.6+`，使用 `pwsh -NoLogo -NoProfile -NonInteractive -File`。这是运行下限，不按小版本阻止自动更新；报告和发行回执仍记录实际完整 patch 版本，未实测的未来版本不因此自动视为已验收。
- 原生测试显式提供实际 Editor `-Unity` 和独立工作根 `-WorkspaceRoot`；后者也可从当前进程的 `YL_TEST_WORKSPACE` 读取。
- Editor 完整 ProductVersion 必须匹配 `TestSuites.json` 的 profile。当前标准定点为 `2022.3.62f3` 与 `6000.3.27f1`；地区版 profile 仅在明确选择时使用，不自动增加验证范围。
- 普通离线编译使用锁定的 .NET SDK `9.0.312` 与 NUnit `3.14.0`；Save BCL 专项使用 SDK `10.0.102` / Runtime `10.0.2`，不自动下载、升级或替换工具链。
- 离线引用来自来源工程真实导入的 UGUI、Json.NET 等 DLL，按 manifest/lock/UPM resolution 校验。应先完成该 Editor 上下文的 Unity 导入，不能用另一工程缓存或替身 DLL 满足前置。
- 图形、DSP、OS、SDK、IL2CPP/C++ 工具链等专项必须具有对应实际环境；无图形结果不能代替原生输入或音频测量。

PowerShell 示例从仓库根执行，先在当前进程设定 `YL_TEST_UNITY` 和 `YL_TEST_WORKSPACE` 为本机真实 Editor 与明确的独立工作根：

```powershell
$unity = $env:YL_TEST_UNITY
$workspace = $env:YL_TEST_WORKSPACE
```

2022 自动化包基线位于 `Profiles/Unity2022`，只是依赖配置，不是第二个工程。框架宿主只安装当前模块声明的输入，不复制完整开发工程。测试工具和独立宿主不加载 MCP；直接打开开发宿主时，MCP NuGet 恢复是宿主自己的前置，不进入框架包或测试输入。

## 发现与执行

默认调用只列声明，不创建工作根、不编译、不启动 Unity：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1
```

先发现再执行离线逻辑：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Discover -Suite Quick -Module Domain -Unity $unity -WorkspaceRoot $workspace
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Run -Suite Quick -Module Domain -Unity $unity -WorkspaceRoot $workspace
```

运行 Unity 日常集与显式专项：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Run -Suite Daily -Module Foundation -Unity $unity -WorkspaceRoot $workspace
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Run -Suite Special -Module Core -Unity $unity -WorkspaceRoot $workspace
```

`Discover` 保存实际选集，状态 `Completed` 不是通过。`Run` 重新发现当前叶子，`-Selection` 可使用发现产物；源码、引用、包、执行器或真实叶子改变时拒绝复用不匹配的选集。

`-Module` 只能使用已有声明，不能以任意 namespace 或未声明的 `All` 扩大范围；未声明的模块/层级组合直接失败。

## 模块范围

此表帮助选入口，准确模式、类别、前置和真实叶子以当前声明与发现结果为准。

| 模块 | 层级 | 主要范围 |
| --- | --- | --- |
| Foundation | Daily / Special | 基础机制、序列化、Logging 原生生命周期与会话 |
| Core | Daily / Special | MVC / UI / 资源合同与 HTTP 原生请求；Special 为真实输入及 UI / HTTP 连续 Play 会话 |
| Domain | Quick / Special | Gameplay / RedDots / Logging 逻辑及组合退出会话 |
| Gameplay | Daily / Special | 最小主包+Gameplay，四领域行为、作者工具与 Player |
| Equipment | Special | 独立 Equipment 退出钩子与连续 Play |
| PoolingPlayer | Special | 主包+Gameplay，Windows Mono 的 Release 耗时与 Development 分配事件分开测量，各双进程 |
| PoolingIL2CPP | Special | 相同池/领域负载，实际Windows x64 IL2CPP Release/Development各双进程；用于2022 LTS与Unity 6支持检查 |
| Behavior | Quick / Daily / Special | 受控数学、双 FSM、作者交互；Special 为原生窗口输入 |
| BehaviorPlayer | Special | 当前作者输入、持久 Contact Prefab 与双进程 Mono Player |
| Overlap / OverlapEditor | Daily | 独立检测/物理与 Detector，预览、Handles、Undo、Prefab |
| OverlapPlayer | Special | 真实 Query/Cast/Contact、时间、容量与多密度 Player 检查 |
| OverlapMovement | Daily | 主包+Overlap+Movement2D 的真实角色、平台与检测组合 |
| StateMachine | Daily / Special | 图、状态、生命周期；脚本生成重载与 Mono Player |
| Movement2D | Daily / Special | 角色/平台、Physics2D、持久 UnityEvent 与 Mono Player |
| Reddot | Quick / Daily | 独立红点闭包逻辑、组件订阅与会话 |
| Save | Daily / Special | Save+Json.NET 独立安装；原生存储、Mono/IL2CPP Player；Special 为 .NET 10 BCL |
| Data | Daily / Special | Save/字典与 DataAssets/Localization 的组合消费、会话与发布输入 |
| DataAssets | Daily / Special | 最小主包+DataAssets 的作者、请求、租约、会话和 Player |
| Localization | Daily / Special | 回退、显示、图片租约、作者、会话与 Player |
| Excel / ExcelGameplay | Daily | 实际 XLSX、预览与提交；后者显式组合 Gameplay |
| Action | Daily / Special | Action / Observable 的取消、池化、宿主与场景生命周期；Special 为 batchmode 的真实两次 Play 与 Timing 隔离 |
| ActionTimingIL2CPP | Special | 必需的 Windows x64 IL2CPP Release/Medium 裁剪构建与双进程 Action/Timing 运行回归，不安装可选 SDK |
| DOTween | Daily | 完整批准 SDK、条件程序集、行为与同宿主 Help XML |
| HybridCLR | Daily | SDK 前置下的 Editor/PlayMode 与显式样例，不代表 native 安装或热更 Player |
| Help | Daily | 真实元数据/XML、窗口、搜索、注册及只读边界 |
| Audio | Daily / Special | 播放、资源、所有权与会话；输出测量另用 AudioNative |
| AudioNative | Special | 官方 Addressables 内容、PCM Listener、DSP/真实输出与图形 Player |
| Resource | Daily / Special | 地址值合同；Special 验证真实的跨 Play 会话与资源所有权 |
| ResourceAddressables / ResourceAddressablesRuntime | Daily | 官方内容、Scope/Lease、场景、实例、图片及包版本 |
| ResourceUpdates | Special | 官方下载尺寸、预下载、Catalog 检查和首次启动更新窗口 |
| ResourceCurrentPublication | Special | 正式 Addressables 构建与双进程 Player 消费，验证部署内容摘要和关闭账本 |

组合测试不等于重复执行每个独立包的完整目录；独立 Save 用例也不应被装有主包的 Data 宿主代替。Quick 的源码切片不代表完整 Runtime 编译，安装图不代表 NUnit 或 Player 行为通过。

### 专项条件

- `Special/CoreClipboard` 需要系统剪贴板与交互 Editor，Daily 明确排除其类别；NativeInput 要真实桌面、`isBatchMode=false` 与独立交互宿主，串行执行以避免争抢焦点。
- `Special/Action` 不开启交互桌面、不抢焦点；正常完成后只退出工具自有的后台 Editor。`ActionVisualTests` 属于 `YL.Manual.ActionVisual` 且标记 Explicit，不在 Daily/Special 自动选集中。原生画面使用下述独立非退出入口，不能改回自动桌面门禁或以模拟点击绕过前置。
- `AudioSession` 只证明会话，`AudioNative` 才包含真实输出/DSP；设备条件不足应报告未执行，不改测量断言或系统音量。
- Resource/Audio 的原生 Bundle 与 Player 流程受 Windows Mono 路径预算限制，使用足够短的独立工作根，不能通过路径别名、少复制输入或删产物来规避。
- DOTween 用 `-DOTweenArchive` 显式提供批准 Standard SDK，测试工具校验完整归档，在自有宿主部署并设置宏，不读取或改写开发宿主 SDK/宏。普通 Action/Help 不安装此 SDK。
- 连续 Play / SessionScenes 使用工具拥有、已保存的空场景。只在严格确认 UTF 默认布局和测试尚未开始时准备场景，不自动保存未知或脏场景。
- Player 探针仅在相应 `YL_*_ACCEPTANCE` 构建符号或专用产品外程序集启用，不能进入普通生产编译图。
- Action/Timing 结项还必须运行 `-Suite Special -Module ActionTimingIL2CPP`，不能以 `Special/Action` 会话或人工 Game View 像素结果代替。首次 IL2CPP 构建应提供足够的 `-TimeoutSeconds`（如 3600）；构建器使用现有 C++ 工具链，不自动安装或升级它。
- PoolingPlayer选择Mono，PoolingIL2CPP选择实际IL2CPP；两者使用相同负载。Unity2022 LTS和IL2CPP是实际支持目标，应在对应Editor执行检查，不由当前Mono结果替代，也不等于要求每次小改全平台/全补丁矩阵。Release的 `measurementKind=ReleaseTiming`，分配为 `NotMeasured`；Development的 `measurementKind=DevelopmentAllocationEvents`，先验证空区间、32/8192字节分配分别记录0/1/1次事件，再采集当前线程限定区间的 `GC.Alloc` Count。事件不是字节，Development耗时不当作发行性能。记录输入指纹和逐批值，创建/重置/丢弃、活跃数与校验值必须正确；Destroy请求与含帧等待的完成耗时分列。校准/负载失败保留报告并失败退出，不伪造平台支持或零成本。
- 当前Pooling工作负载版本4为76病例，在原72固定输入后追加 `Audio.Pending.0/1/24/128`：真实公开异步请求持续Loading，同址共享一次provider，采样外绑定实际宿主Update；每批64次更新。Audio病例中的 `medianNanosecondsPerPair` 是通用报告字段，此处pairsPerIteration=1，单位为ns/Update；不是声部吞吐或真实帧FPS。取消和late lease释放在采样外断言，不将同帧接纳/List.Remove成本混入等待快照。

需要额外参数和特殊上下文时读取对应模块声明，不把上述条件当作 Skip 白名单。

### Action 原生画面

先在未打开的工具自有宿主中运行 `Special/Action`，准备正式输入、当前测试程序集与 `Assets/YLTestingProfile/Empty.unity`。随后显式打开该宿主，使用只写结果、不调用 Exit/Quit 的入口；不使用 `-batchmode`、`-quit`、`-runTests` 或 `-executeMethod YLTestEntry.Execute`：

```powershell
$hostProject = '<已准备的工具自有 Special/Action 宿主>'
$run = [guid]::NewGuid().ToString('N')
& $unity -projectPath $hostProject -ylTestInteractive `
    -executeMethod YLFramework.Tests.Actions.ActionVisualTests.RunManualAcceptance `
    -ylActionVisualRun $run `
    -logFile (Join-Path $hostProject "Logs/ActionVisual-$run-editor.log")
```

入口要求合法的 Host owner、唯一运行标识和无脏场景；在 UTF 的 RunStarted 边界只识别未修改的原生默认场景或声明的空场景，不保存、覆盖或丢弃未知场景。它只运行该 Explicit 用例，结束后释放自己的回调/更新泵并保持 Editor 打开。关闭窗口由操作者控制；重新准备同一宿主前必须先正常关闭，避免与自动 CLI 争用工程。

用例从 Game View 自身的 `m_RenderTexture` 原生读取，不把其他活动帧缓冲或只正确的 alpha 当作覆盖证据。先校准品红相机背景，再验证黑色遮罩覆盖中心与四角、退场后恢复背景。它只临时调整本方运行应用的后台运行标记并恢复，不改系统焦点设置、音量或产品资源。`WaitForEndOfFrame` 在 batchmode 不运行，Scene View 也可能令它暂停，不通过加超时掩盖缺失的渲染帧。

证据位于该宿主 `Logs/ActionVisual-<run>.xml` 及同前缀的 `-background.png`、`-opaque.png`、`-revealed.png`。仅 XML 的目标叶子真实 Passed、零失败/跳过且三个画面采样符合预期时才报告视觉通过，文件存在本身不是成功。当前已验证 Unity 6000.3.27f1 / Direct3D12 / 1920×1080；不据此宣称其他分辨率、管线、图形 API 或 Player 通过。

## 隔离与资产

工作根不能是来源仓库、盘符根或其祖先。已存在但没有合法 owner 的目录不接管；工具通过 `.yl-workspace.json`、`.yl-owner.json` 与独占 `.lease` 绑定仓库和目录身份，拒绝链接、junction、越界和未知子项。

```text
<WorkspaceRoot>/
  Hosts/    可复用的自有 Unity 宿主
  Cache/    与输入和工具链指纹绑定的缓存
  Results/  每轮独立结果
```

Native Run 按选集模式分别启动进程，避免复用测试留下的静态和场景状态。同一项目只允许一个 Editor；主工程可以保持打开，工具不保存、丢弃或关闭用户场景，不停止用户 Editor/Hub。

生成配置、SortingLayer、无 Domain Reload、SDK 和输入设置只在声明的工具自有宿主准备。测试资产、冻结夹具与身份 JSON 按其所有者管理，不因含日期或路径不在当前源布局中就重写；正式作者输入使用独立自有副本。夹具准备入口位于 `Tools/Testing/Fixtures`，不进入产品包。

宿主或缓存中断后与收据不符时会保留并拒绝复用。不要修改 owner、伪造指纹、清空未知目录或关闭校验；保留证据，使用新的明确工作根重新准备。

## 结果解释

每轮保存 `summary.json`、实际 `selection.json`、NUnit XML 和原始进程日志。只有同时满足以下条件才报告 `Passed`：

- 输入、真实发现、程序集和模式与声明一致，不能漏掉整个被条件编译隐藏的 PlayMode 程序集。
- 选集非空，每个预期叶子恰好出现一次，无额外、漏出、重复或错误程序集。
- 没有 Failed、Inconclusive、Explicit 或未批准的 Skip；Daily 的排除项在发现摘要中明确记录。
- XML 与实际发现匹配，不仅是存在文件或根节点声称 Passed。
- 对应进程普通退出，观察与清理结束，无超时、取消、强制回收或残留后代。

取消会回收工具自己创建的进程树，但仍报告取消。退出码、清理失败或前置不足不能因已经产生 PASS 日志而改报成功。日志应脱敏，不泄露 Hub/CIM 命令行中的临时令牌。

Editor 编译、离线逻辑、EditMode、PlayMode、Player 构建、Player 运行及 DSP/OS/IDE 专项必须分别报告。某个 profile 通过只适用于其实际输入与环境，不外推其他补丁或平台。

## 保留与清理

每组保留最近 1 份普通通过和最近 3 份非通过记录；按 Suite、模块、完整 Editor 身份分组。`-KeepArtifacts` 固定本轮记录，使用中、固定、未知身份或不完整结果不能作为普通可删输出。

清理默认仅预览，显式 `-Apply` 才处理当前组满足保留规则的自有输出：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 -Action Clean -Suite Daily -Module Foundation -WorkspaceRoot $workspace
```

它不扫描其他工作区、主工程缓存或外部归档。

## 工具自检

从仓库根按影响运行；有 `TemporaryRoot` 的脚本会在给定父目录下创建自己的临时夹具，先核实该父目录：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLTesting.ps1 -TemporaryRoot $env:TEMP
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLGitHygiene.ps1
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLQuick.ps1 -WorkspaceRoot $workspace -Unity $unity -Module Domain
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLUnity.ps1 -WorkspaceRoot $workspace -Unity $unity -Module Core
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLUnitySafety.ps1 -WorkspaceRoot $workspace -Unity $unity
```

Git 卫生只读 Git 索引，检查生成物、本机配置和不允许入库的插件，不清理文件，也不代表 Unity 编译。安全回归在自有夹具验证失败、输入漂移、整组模式缺失、取消、进程回收与恢复；不通过放宽正式入口来让夹具成功。

`Test-YLAssemblyLayout.ps1` 消费真实 Unity 编译图，图必须包含来源、实际 Editor、采集时间与编译状态，不能用手写理想图替代。Formatter 与 Analyzer 的独立入口分别见[格式工具](../../PackageSources/com.kano.ylframework/Documentation~/CodingStyle.md#格式工具)及[重建与验证](../../PackageSources/com.kano.ylframework/Documentation~/Modules/Attributes.md#重建与验证)。

## CI 边界

[Repository Tooling (No Unity)](../../.github/workflows/dotnet.yml) 在 Windows runner 执行 Git 卫生、格式、Analyzer 的 .NET 构建/测试、测试工具合同及固定提交包构建。不安装 Unity，不需要 Unity 许可证，也不证明框架已在 Unity、UTF 或 Player 通过。

Unity CI 需要另行提供 Editor、许可、SDK、包与短独占工作根。图形/OS/DSP、AOT/Player 和可选 SDK 仍有各自前置；缺失应报告未运行。上传本轮 summary、selection、XML 和必要脱敏日志，不用上传成功代替测试成功。
