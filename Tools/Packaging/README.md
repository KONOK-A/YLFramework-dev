# 打包与发行准备

本目录维护 UPM 产品清单、固定提交制品构建和后续发行准备。框架尚未正式发布；存在构建工具和 OpenUPM 模板不表示 Registry 已有可安装版本。日常源码接入见[安装指南](../../PackageSources/com.kano.ylframework/Documentation~/Installation.md)，测试隔离与结果约定见[测试工具说明](../Testing/README.md)。

## 清单与包内容

[PackageCatalog.json](PackageCatalog.json) 定义自有包 ID、程序集归属、显式门控和特殊负载；版本、直接依赖及文件白名单读取各包 `package.json`。

- 每个包保留包根 `README.md`、`LICENSE.md`、`package.json` 及其现有元数据。
- 顶层文件/目录必须获 `files` 明确允许且真实存在。新增手册应声明 `Documentation~`，不能仅在源码中看到文件就假定会打包。
- 普通 Unity 资产要求配套 `.meta`；`Documentation~` / `Samples~` 下文件和目录有隐藏内容豁免，但已有 `.meta` 不能成为孤儿，GUID 不能跨包重复。
- 产品包不能夹带 Tests、宿主 Assets/Packages/ProjectSettings、Tools、缓存或本机配置。样例隐藏输入与显式导入副本分别管理。
- 依赖 DLL、第三方许可证、MiniExcel 来源与条件适配源码都是实际负载，不能因默认未编译就漏包。

维护包清单时同时检查真实 asmdef、Version Define、Editor/Player 图和安装前置，不增加无实现的空包来满足引用。

## 验证工作区内容

新增/移动文档、修改 `files` 或元数据后，可在不启动 Unity 的情况下验证当前工作区：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLRegistryPackagesNoUnity.ps1 -TemporaryRoot $env:TEMP
```

入口要求 PowerShell `7.6.x`，将当前 `PackageSources` 复制到自有临时夹具，检查白名单、必需文档、依赖、元数据、GUID 与负载。它会创建工具自己的临时目录，不覆盖给定父目录的其他内容。

显式提供 `-NodeRoot` 时才额外执行固定版本的真实 npm pack；未提供时该分支不会运行，不能把脚本完成称作真实 npm 打包或 UPM 安装通过。较小的 [Test-YLUpmPackages.ps1](../Testing/Tests/Test-YLUpmPackages.ps1) 只检查基础文档/GUID，不替代完整打包合同，也不在当前 CI 中。

## 构建固定提交候选

[Build-YLRegistryPackages.ps1](Build-YLRegistryPackages.ps1) 使用 Git 提交构建候选，不直接打包未提交的工作区。要求固定 Node `22.23.3`、随附 npm `10.9.9`、PowerShell `7.6.x`、Git 与 Git LFS；使用已准备的工具环境，不修改全局 Node/npm 配置或带入令牌。

从仓库根执行，先把 `YL_PACKAGE_WORKSPACE` 设为仓库外、明确自有的绝对输出根：

```powershell
$repository = (git rev-parse --show-toplevel).Trim()
$commit = (git rev-parse --verify HEAD).Trim()
$packageWorkspace = $env:YL_PACKAGE_WORKSPACE
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Packaging/Build-YLRegistryPackages.ps1 -Repository $repository -SourceCommit $commit -OutputRoot $packageWorkspace
```

源提交须为真实完整 SHA，已有未知目录不会被领养。工具在独立 checkout 中取得该提交并物化 LFS，以隔离 npm 配置/cache 执行两次 `npm pack --ignore-scripts --offline`；README 和 `files` 的未提交修改不会进入候选。

输出 `artifacts/` 包含各包 `<包ID>-<manifest版本>.tgz` 和 `release-manifest.json`。同批版本和自有依赖精确一致，回执记录：

- 源提交、工具链及 Local/CI producer 身份。
- 包依赖、tarball SHA-256、SHA-512 integrity 与完整文件集合。
- 逐文件原始字节及语义摘要；仅 `package.json` 允许严格 JSON 的属性语义规范化，其他文件不能任意改字节。

解包前审计 `package/` 根、路径、大小写、重复项、链接、类型、数量、大小和完整性。不能拿同版本本地重包结果替换已固定候选，也不能复用改动前的 payload 摘要。

## 安装与组合检查

`Test-YLDistribution.ps1` 验证主包来源、内容、图及显式 Player 冒烟；`Test-YLIntegrationPackages.ps1` 验证指定自有闭包或 SDK 组合，共用工具自有 owner、lease、进程与结果约定。

最小包检查示例，从仓库根使用实际 `$unity` 与独立 `$workspace`：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLIntegrationPackages.ps1 -Unity $unity -WorkspaceRoot $workspace -PackageId com.kano.ylframework -PackageSource File
```

| 来源 | 前置与证据 |
| --- | --- |
| File | 当前受管本地源码，不证明 Git 或 Registry 解析 |
| Git | 工具 GitFixture 或明确 GitRemote；两者分别记录，不混称远端安装 |
| LocalTarball | `-ReleaseManifest` 指向真实冻结回执，闭包全部使用已校验 tgz；不证明 Registry |
| Registry | 先真实发布并具备 HTTPS metadata、tarball、CI Release 字节与 generated lock，再提供 Registry URL/version 与回执；当前未发布状态不能直接运行成功 |

Registry scopes 不接管 `com.unity`。包版本相同不代表来源相同；每轮联合核对实际 PackageInfo、manifest、锁图与完整 payload。首次安装使用空的新 host/cache，暖重开、移除或阴性重试不算另一轮冷安装。

`-PackageId` 从真实包依赖计算最小闭包，不强装可选适配；与 `-Integration` 互斥。`-Integration` 可选 `Excel`、`ExcelGameplay`、`ReddotSave`、`DOTween`、`HybridCLR`、`MovementOverlap`、`AllPackages`，由固定 profile 决定 roots，不接收任意多根。

- ExcelGameplay 与 ReddotSave 显式测试双方安装、单方移除/恢复和最终移除，验证适配程序集随包存在性变化，保留项目哨兵数据及 `.meta`。
- MovementOverlap 与 AllPackages 使用固定中立生命周期；AllPackages 安装清单全部 roots，启用真实包级适配但不暗中安装 DOTween SDK 或设置宏。
- [Test-YLOptionalAdapters.ps1](../Testing/Tests/Test-YLOptionalAdapters.ps1) 在自有夹具验证 neither、feature-only、backend-only、both 与移除 backend；NoUnity 清单检查不能代替此 Unity 编译矩阵。
- 示例导入单独在工具自有安装宿主进行；卸载用不引用框架/Json.NET/SDK 的中立探针，不以删用户数据或业务源码来制造通过。

DOTween SDK 专项与普通 Action 分开：`Required` 要求批准的完整 Standard archive、实际程序集与 `DOTWEEN`；`Absent` 不投放 SDK/宏；`SdkOnly` 投放 SDK 但验证宏及 Editor 导入状态；`DefineOnly` 缺 SDK，在启动 Unity 前报告前置不足。阴性结果不是 Required 正向通过。详情和行为入口见[测试专项条件](../Testing/README.md#专项条件)。

## Player 冒烟

默认分发检查验证安装和编译图，再做 Windows64 Mono 构建；显式 `-RunPlayer` 才加入两次真实运行，`-Backend IL2CPP` 选择独立后端前置：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 -Unity $unity -WorkspaceRoot $workspace -RunPlayer
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Tests/Test-YLDistribution.ps1 -Unity $unity -WorkspaceRoot $workspace -Backend IL2CPP -RunPlayer -TimeoutSeconds 2400
```

普通生产图不含 Tests/UTF/探针；运行模式向产品根外显式安装独立 probe，以 `BuildOptions.None` 构建，检查初始化、Resources、事件、Busy lease 保护、归还与关闭。IL2CPP 还检查实际 GameAssembly、metadata 和裁剪后程序集边界。

构建、首次运行、重复运行分别保存报告，要求普通退出、Job 后代清零、准确进程/版本/后端身份、完整阶段、无运行错误且产物摘要不变。`-batchmode -nographics` 逻辑冒烟不证明图形、真实音频、任意反射保留或其他平台。

## HybridCLR 原生前置

普通 IL2CPP 不自动安装 HybridCLR。只有显式 `-Backend IL2CPP -RunPlayer -HybridCLR -NativeSource` 才进入原生流程，SDK 来源必须符合适配包当前门禁，完整用户操作见 [HybridCLR 手册](../../PackageSources/com.kano.ylframework.hybridclr/Documentation~/README.md)。

- 原生来源准备与校验在 [Prepare-HybridCLRNativeSource.ps1](../HybridCLR/Prepare-HybridCLRNativeSource.ps1) 和 [Test-HybridCLRNativeSource.ps1](../HybridCLR/Test-HybridCLRNativeSource.ps1)。先核实脚本参数、来源许可与实际 Editor，不下载未授权工具链或修改用户 Editor。
- 原生流程只接受 File/Git 来源；Registry/LocalTarball native 被当前入口拒绝。Unity 6.3 额外要求与 NativeSource 匹配的 `-NativeSourceManifest`，维护变体不能冒充未修改官方源。
- 在新宿主中调用正式生成、Strip AOT、桥接、资源发布/部署和 Player 构建，之后冻结 baseline；不接管主工程配置、Library 或已有基座。
- Player 使用真实图形设备，两次独立进程复用同一冻结产物。先关闭业务 Module/实例，再释放 loader 与资源；Dispose 不表示程序集已卸载。
- 缺 Editor 模块、C++ 工具链、已核实原生源、设备或任何阶段失败均不是通过；不能外推移动平台、其他裁剪、任意 AOT 泛型或完整游戏发布。

## 未来发行步骤

[OpenUPM/](OpenUPM/) 目前只有主包、Excel、HybridCLR 三份登记模板，使用 `githubRelease`、Tag 前缀 `upm/` 与资产前缀 `<包ID>-`。模板是维护输入，不表示已登记；版本、Tag 和完整包集合须在实际发行时重新确认。

1. 固定准备发行的主线源提交，核对对应自动 CI 与原样下载的候选资产/回执，使用同一批字节完成实际安装与相关 Player/SDK 检查。
2. 核对所有包 ID、版本、依赖、许可证、OpenUPM 定义及精确 Tag/Release 目标。尚未实际完成的矩阵如实标示，不伪造成功状态。
3. 取得针对源提交、Tag、完整资产及摘要的明确公开写入授权后，再创建 Tag、Release 或登记。普通代码提交/推送不自动授权发布。
4. 发布附件必须与批准候选字节一致，不就地重包或覆盖同版本资产；Registry 就绪后再核验真实 metadata、冷安装、组合生命周期与 Player。

当前 GitHub 的工具层只证明对应检查本身，不证明 Unity 安装、Player 或公开渠道可用。未发布前不把这些准备步骤写成普通用户必经的安装流程。
