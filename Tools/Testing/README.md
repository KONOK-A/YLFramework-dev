# 测试工具说明

[测试入口](Invoke-YLTests.ps1) · [模块清单](TestSuites.json) · [专项](#专项运行) · [结果](#结果解释) · [清理](#保留与清理)

## 快速开始

在仓库根目录使用 Windows PowerShell Core `7.6+`。先列出入口，再选一个层级和模块运行。将 `$unity` 设为实际的 `Unity.exe`。示例读取当前进程的 `YL_TEST_UNITY`。

```powershell
$runner = 'Tools/Testing/Invoke-YLTests.ps1'
$workspace = 'G:/Unity/Tests'
$unity = $env:YL_TEST_UNITY

pwsh -NoLogo -NoProfile -NonInteractive -File $runner -Action List
pwsh -NoLogo -NoProfile -NonInteractive -File $runner -Action Run -Suite Daily -Module Save -Unity $unity -WorkspaceRoot $workspace
```

`Run` 自动发现当前用例。只有事先要核对选集时才单独跑 `Discover`。`Run -Selection` 会重新核对当前输入和用例。单次操作的默认超时为 `600` 秒，可用 `-TimeoutSeconds` 调整。

本机用仓库外的测试工作根，由工具管理 `Hosts`、`Cache`、`Results` 和所有权标记。标准 Editor 为 `2022.3.62f3` 与 `6000.3.27f1`，完整 ProductVersion 以 [TestSuites.json](TestSuites.json) 为准。

离线测试使用 .NET SDK `10.0.404` 和 NUnit `3.14.0`。Quick 由 Unity Mono 执行，`Special/Save` 的 BCL 用例使用 .NET Runtime `10.0.15`。离线引用来自来源工程在对应 Editor 下的真实包解析结果。

## 模块范围

| 要验证的内容 | 常用入口 |
| --- | --- |
| 离线逻辑 | `Quick/Reddot`、`Quick/Domain`、`Quick/Behavior` |
| 框架基础、UI、HTTP、资源生命周期 | `Daily/Foundation`、`Daily/Core`、`Daily/Action`、`Daily/Audio` |
| 游戏逻辑、物理与创作 | `Daily/Gameplay`、`Daily/Movement2D`、`Daily/StateMachine`、`Daily/Overlap` |
| 存档、数据、本地化、工作簿 | `Daily/Save`、`Daily/DataAssets`、`Daily/Localization`、`Daily/Excel` |
| Addressables、生成器与帮助工具 | `Daily/ResourceAddressables`、`Daily/ResourceAddressablesRuntime`、`Daily/Generators`、`Daily/Help` |
| Player、正式内容构建和组合验收 | `Special/SavePlayer`、`Special/ActionTimingIL2CPP`、`Special/ResourcePackedCatalog`、`Special/ExcelGameplay` 等 |
| 安装、卸载、组合包与发行 | [打包与发行准备](../Packaging/README.md) |

按改动选择单个入口，完整列表以 `-Action List` 为准。Core、Audio、DataAssets 和 Gameplay 各跑自身的资源回归。DataAssets 与 Gameplay 的同宿主交互用 `Daily/ResourceAddressablesConsumers`。

### 模块结项必需项

框架模块开发或功能改动结项，须用受支持的 Unity 在实际目标平台**构建并运行 IL2CPP Player**，确认正常退出。分列 Editor、Player 构建和运行结果，并记录版本、后端、裁剪、输入与退出状态。前置不足记为未完成，交付前核对[本轮测试缓存清理](#本轮测试缓存清理)。

## 结果解释

常规入口用终端 JSON 的 `summary` 找到 `Results` 下的本轮目录。先看 `status` 和 `reason`，再核对 `selection.json`、NUnit XML 和进程日志。`Completed` 表示发现完成，`Passed` 需实际用例、程序集、模式与结果匹配，且进程和清理正常收尾。缺前置、超时和取消按实际状态报告。

| 专项 | 终态回执 |
| --- | --- |
| Addressables Player | `Results/ResourceAAIL2CPP/<run-id>/execution.json` |
| 加密交付 | `Results/FrozenEncryption/<run-id>/frozen.json` |
| 固定提交安装／移除 | `Results/NativeDeliveryFinal/<run-id>/p0-c.json` |
| ResourceUpdates Player | `Results/ResourceUpdates/<run-id>/summary.json`，大包和空间等专项有各自的结果组 |

## 保留与清理

### 本轮测试缓存清理

本轮通过、失败或中止后，先保全日志、回执和验收输入。核对归属、进程、租约及原路径依赖后，回收本轮可重建且已无用途的缓存和 Player。固定现场、仍用于验收或复现的原件、唯一或归属不明的数据保留原位。交付时复核实际清理结果和保留原因。

### 常规结果轮换

`Clean` 默认预览。核对候选后加 `-Apply`，仅处理当前工作根内所选模块与层级的 `Run` 和 `Discover` 结果。

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File $runner -Action Clean -Suite Daily -Module Save -WorkspaceRoot $workspace
```

轮换按 Action、Suite、模块和 Editor 身份分组，每组留最新 1 份普通通过或发现完成记录，以及 3 份非普通终态。常规入口的 `-KeepArtifacts` 固定结果。使用中和无法确认归属或完整性的目录受保护。

### 专项产物回收

- 一次性 Player 专项普通通过后保留回执并回收宿主。固定提交验收、显式 `-KeepArtifacts` 和未通过的轮次保留已有现场。
- ResourceUpdates 普通通过后回收 Player、应用数据、缓存和构建产物。`RU` 原位留下 `.yl-inputs.json` 与 `PackageSources` 供 `-CompareBaselineHost` 重建，配对的 `RP` 宿主随本轮回收。需要原安装时加 `-KeepArtifacts`，失败轮次也保留已有现场。
- 用 `retention.json` 核对回收。`Pruned` 表示保留冻结源码，`Removed` 表示宿主已移除，`Pinned` 表示现场已固定。出现 `CleanupFailed` 或只有 `retirement-intent.json` 时，按宿主身份复核。历史 `Archive` 按原归属管理。

## 专项运行

### 专项条件

- `Special/DOTween` 用 `-DOTweenArchive` 提供批准的完整 Standard SDK。`Special/HybridCLR` 和 `Special/ResourceAddressablesHybridCLR` 用批准的 Git SDK。原生安装和热更见[打包说明](../Packaging/README.md)。
- `Special/CoreClipboard` 与 `Special/Behavior` 的原生窗口用例需要真实桌面，串行执行。`Special/AudioNative` 需要实际音频输出和 DSP。
- `Special/PoolingPlayer` 和 `Special/PoolingIL2CPP` 分别用 Mono 与 IL2CPP 测同一负载。Release 记录耗时，Development 记录校准后的 `GC.Alloc` 事件数。详见[Pooling 验收夹具](../../Assets/Tests/YLFramework/Fixtures/Pooling/Editor/PoolingPlayerTests.cs)。
- Packed Catalog 默认覆盖 Unity 2022 的 JSON 和 Unity 6 的二进制编码。Unity 2022 的显式二进制用 `Special/ResourcePackedCatalogBinary`。

### Resource Addressables Win64 Player

[Player 入口](Invoke-YLResourceAddressablesPlayer.ps1)默认执行 Win64 Release IL2CPP。`-Profile Unity2022` 和 `-Profile Unity6` 各自配对应 Editor。`-ScopeCloseBenchmark` 和 `-EncryptedRemoteGroup` 都使用 `Release -Graphics`。加密交付按需选后端与布局。

固定提交验收在干净 worktree 中同时给 `-ReleaseManifest` 和完整 `-SourceCommit`。安装和移除用 [Invoke-P0NativeDelivery.ps1](Invoke-P0NativeDelivery.ps1)，未提交诊断包用[本地打包入口](ResourceEncryption/Invoke-LocalEncryptionPack.ps1)。性能回放、读流测量和原生互读分别见[暖载门禁](ResourceEncryption/Compare-FrozenRemotePerformance.ps1)、[读流脚本](ResourceEncryption/Measure-EncryptionStreams.ps1)和[原生合同](ResourceEncryption/Invoke-Contracts.ps1)。暖载对照须匹配输入、环境和受控串行窗口。

### Resource 签名内容更新与离线回滚

`Special/ResourceUpdates` 跑 Editor 合同。真实 Player 用 [Invoke-ContentUpdates.ps1](ResourceUpdates/Invoke-ContentUpdates.ps1)。完整六格验收传 `-Unity2022` 和 `-Unity6`，需事后检查原安装时加 `-KeepArtifacts`。下面的单格示例使用 Unity 6 的 `Unity.exe`。

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/ResourceUpdates/Invoke-ContentUpdates.ps1 -WorkspaceRoot $workspace -Unity6 $unity -Cell Unity6-Plain-IL2CPP
```

专项参数包括 `-CacheUsage`、`-LargeDownload`、`-SpaceFaults` 和 `-ResumeContracts`。配对下载对照用一格、`-LargeDownload -ExpectTuned` 和对应的 `-CompareBaselineHost`。

### Action 原生画面

先跑 `Special/Action` 准备自有宿主。确认宿主已关闭且使用干净场景，将 `$hostProject` 设为本轮宿主路径。交互 Editor 保持打开，XML 与画面写入宿主的 `Logs`。

```powershell
& $unity -projectPath $hostProject -ylTestInteractive -executeMethod YLFramework.Tests.Actions.ActionVisualTests.RunManualAcceptance -ylActionVisualRun ([guid]::NewGuid().ToString('N'))
```

## 工具自检

测试工具合同见 [Test-YLTesting.ps1](Tests/Test-YLTesting.ps1)，格式和分析器见[代码规范](../../PackageSources/com.kano.ylframework/Documentation~/CodingStyle.md#格式工具)。

### 离线文档检查

```powershell
pwsh -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDocumentationNoUnity.ps1
```

## CI 边界

[Repository Tooling (No Unity)](../../.github/workflows/dotnet.yml) 校验仓库工具、包和迁移合同，并核对固定提交打包。Unity、Player 和设备验收走本页对应入口。合入证据应标明实际运行范围和结果路径。
