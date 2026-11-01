# HybridCLR Integration

本目录是可选叶子，不随默认 YLFramework 包导出。先导入默认框架及其 UGUI、Newtonsoft.Json 依赖，再安装官方 `com.code-philosophy.hybridclr`：

`https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3`

解析 commit 必须为 `6cadd24d101f4cbd00cd80190106f6621132d24d`。不包含官方包、运行库、dnlib 或其他宿主扩展副本。下面的定点范围均为 Windows64、IL2CPP、.NET Framework；package 或本地版本文本等于 `9.0.3` 只是必要条件，不能单独证明实际 native 来源或可用性。

## Verified Runtime Scope

| Unity | Runtime 来源 | 已验证范围 |
|---|---|---|
| `2022.3.62f3c1` | SDK `9.0.3` 选择的官方对应 native；il2cpp_plus `ba3a76c9979d14cee404fb5d44301773e0768866` / hybridclr `83929787581bce6c7219418f62348a10c33582ee` | Development/Low 的两项目共享基座、精确程序集子集、MVID 冲突拒绝、泛型/async 与生命周期。code-only 子集不冒称完整配表 Module 或普通 Release 样例已重新验收。 |
| `6000.3.27f1` | 下述 `ParameterOwner` 维护 runtime | Development/Low 的完整样例与签名 HTTPS 更新/认证、载荷和部分 metadata 负向；非 Development/Low/C++ Release、空额外宏的完整样例、本地已发布 snapshot 更新及同 Player 重启 V2；两项目共享子集与相关负向。普通 Release 的本地更新不等于其远端 TLS 已验。 |

不外推 High stripping、其它平台/Unity 补丁或任意动态泛型。不同运行配置、项目选择和最终 AOT 都必须保留实际身份；EditorSimulation/EditorFast、目录里有 metadata 或仅类型存在都不是这些原生结果。`Dependencies.json` 的旧 `scope.nativeValidated=false` 保留为未作全能力总括声明，精确已验证能力以 `verifiedEnvironments` 为准。

### Unity 6.3 Maintained Runtime

未修改的官方 6.3 native 曾在合成热更元素数组的参数反射中把 AOT method definition 与 Interpreter 参数表混用；不能因为旧 Development 或某次运行未崩溃就宣布普通 Release 可用。

已验证的维护源固定为 il2cpp_plus `6eb38b29c3a7448b3ac6c4dee450de375b5d158d` 与 hybridclr `bf01f8eed56a077781c3a7b25f418603c7645588`（两者 tag `v6000.3.x-9.0.1`）。在独立维护源合入配套 `hybridclr/` 后，仅将 `libil2cpp/vm/GlobalMetadata.cpp` 的参数定义查询由 `klass->image` 改为既有 `methodDefinition` 重载。完整 patch、精确来源/哈希和两份上游 MIT 作为独立 `NativeRuntime-ParameterOwner` 附件提供，不在本可选 unitypackage 内，也不是升级为另一个 SDK 版本。

通过官方 Installer 的 local-source 流程（`InstallerController.InstallFromLocal`）安装到新工程，只使用项目内 LocalIl2CppData，`useGlobalIl2cpp=false`。不要手改 Library、PackageCache、Editor 安装或旧冻结基座；不要把 6.3 patch 套到 2022 tag。归档/native 源每文件清单、实际前后像/单文件差异、patch、SDK commit、安装树及最终 Player/AOT 指纹应一起核验。官方 Installer 仍会写 `9.0.3` 版本标记，因此必须另行记录 `ParameterOwner` 与源清单哈希，不能伪称未修改官方 native。

进程退出与功能证据也分开：Unity 2022 某些受控 Editor 退出可能挂起。任何允许消费已完成产物的处理必须针对明确操作/输入独立授权并保留真实超时/强退/失败，不能改成 exit0 或扩展到构建、Player。历史单次接受不是应用或 CI 的通用成功政策。

运行时 namespace 保持 `YLFramework.Integrations.HybridCLR`，Editor 为 `YLFramework.Integrations.HybridCLR.Editor`；程序集名仍为 `YLFramework.HybridCLR` / `YLFramework.HybridCLR.Editor`。宿主使用的 Resource/Core 当前分别为 `YLFramework.Resource` / `YLFramework.Core`；默认框架仍是 Runtime/Editor 两个主体，功能目录不是额外 DLL。完整身份与依赖见 [架构总览](../../Documentation/Architecture.md#namespace-与迁移)。

## Build

1. 在官方 HybridCLRSettings 配置明确的热更 asmdef/外部 DLL 名单。AOT 不可静态引用热更类型，不使用 Assembly-CSharp 或手写加载顺序。
2. 在 `YLFramework/Integrations/HybridCLR` 窗口选择或新建配置。新建动作创建自有 ResourceProject、BuildAndPublish Recipe、Profile 和代码目录映射，不创建样例内容或修改官方设置。Profile 配置 Development、自有 Assets 生成目录、持久基线目录及项目入口程序集；身份、平台和兼容号从 Recipe 读取。基线必须在 Assets/Packages/Library/Temp 之外。
3. 用独立接收工程显式配置 Windows64、IL2CPP、.NET Framework、C++ 工具链和 Windows SDK；生成可运行基座时 `Build Scripts Only` 与 Windows `Create Solution` 都须为 false，导出 Visual Studio 工程不是 Player 成功。Validate 不自动安装或切换设置。当前目标和 Development 必须完全匹配，所有输入资产须显式保存。CI 的资源构建沿用正式 Resource 指纹检查；未验证的 `-nographics` 环境不能据成功日志跳过该检查。
4. 应用显式准备标准 `Assets/StreamingAssets` 父目录（不手建或认领其下的 YL 受管根），再在 `YLFramework/Integrations/HybridCLR` 窗口或 `HybridCLRBuildPipeline.BuildBasePlayer` 提交新 Player 路径和与 enabled scenes 相同的场景列表。流程依次检查编译、生成、剥离、完整桥、metadata/manifest、正式 Resource 发布、显式内置部署、真实 Player 和最终 AOT 基线。任一步失败都不是基座成功，读取报告的首因及已完成阶段；部署失败按正式 Resource Recover 验证 journal，不手工补拷或删除旧发布。
5. 更改热更代码后，只调用 `PublishPatch`。它读取持久冻结 AOT 和完成标记；不调用 Strip/GenerateAll、不构建 Player、不改变基座。新增全局登记、未预登记的 AOT/原生需求、损坏基线或身份不符需新兼容号和新基座；项目入口可在既有基座已登记集合内重新选择。静态 analyzer 不能承诺任意反射/动态泛型均安全。

Recipe.Project 必须明确选择 `Generated/hybridclr` 下的 `.bytes`/`.json`，以生成根为 SourceDirectory，得到规范地址 `hybridclr/manifest.json`、`hybridclr/assemblies/*.dll.bytes`、`hybridclr/metadata/*.dll.bytes`。不要选择 owner 记录、原生生成目录或原始 `.dll`。不要修改受管生成文件、遗留未知文件、链接或认领已有用户目录；写入保留 GUID、manifest 最后提交，中断时保留诊断而不冒充可恢复成功。

`BuildBasePlayer`、`PublishPatch` 接受应用注入的 `IResourceContentKeyProvider`；保密密钥不存 profile。签名更新继续使用既有 Resource 签名、Check/Prepare/Activate、发布版本与下载验证，不建立第二套下载器或密钥系统。

这些 Editor 入口使用 `YLFramework.Integrations.HybridCLR.Editor`；资源构建/发布入口使用 `YLFramework.Editor.Resource`。功能域 namespace 升级涉及 AOT API、泛型或裁剪输入时，必须用本轮新基座、metadata 与补丁完成闭环；程序集名称相同不代表旧基座兼容，不覆盖或复用冻结制品来冒充新验收。

## Project Selection And Shared Base

`Profile.HotUpdateAssemblies` 是项目入口的精确程序集名，不是第二份编译登记。空数组保持已有配置的“官方全量”语义；非空时从实际编译 DLL 的引用求热更依赖闭包，只发布该闭包和它需要的 metadata。动态反射入口无法由静态引用推断，应显式选择。原生编译、裁剪、桥接和冻结登记始终覆盖官方全局并集；baseline 的 `InitialManifest` 表示全局登记，不等于某个项目实际发布的 manifest。

项目 B 可显式将 `BaseProfile` 指向未绑定其他配置的基座拥有者 A。两者必须使用同一个规范化基线目录、目标平台、Development 和兼容号；各自保留自己的 ProjectId、Recipe、生成目录及文件归属。只允许一层绑定，拒绝自引用、链式绑定和错误拥有者。B 首发使用 `PublishPatch`，不再构建 Player；热更集合、metadata 和 native 需求均须是 A 冻结登记的子集。目录里存在 AOT 文件不等于该 metadata 已登记。原 schema1 和未指定新字段的已有 Profile 继续按原来的全量/自有基座方式工作。

共享 DLL 保留完整 identity/MVID/hash/length。相同编译快照的 A/B 投影使用相同字节；不同时间发布若改变共享 DLL，不保证任意历史版本可以混用，需由应用协调版本。Runtime 仍在不可逆加载前拒绝共享 DLL 冲突，不通过项目名前缀绕过程序集身份。

## Editor Workflow

窗口分为“配置 / 构建发布 / 诊断 / 导出”，窄窗口使用任务下拉和滚动布局。资产字段通过 SerializedObject/Undo 编辑，顶部“保存”仅保存当前 Profile、Recipe 与 Project；打开、绘制、切换配置不安装、不生成、不保存。

“检查配置”可在 Mono Editor 检查项目入口、当前 Player 脚本图/外部 DLL、目录与资源映射；它不是正式编译或原生可用性证明。“检查原生构建环境”执行严格 Validate。程序集资产选择用于编辑项目入口；未在官方集合登记的项会明确提示，正式预检拒绝，不自动写回全局设置。路径提供选择器，构建阶段与原始错误在诊断页分别显示。

“关联生成资源”显式创建规范目录选择，保留其他 Group，支持 Undo，重复调用不追加重复项。更换生成目录后若仍有旧的规范代码映射，会先拒绝并提示显式调整旧来源/选择，不删除旧文件、不静默合并两套 `hybridclr/*` 地址。

## Runtime

调用方使用 `using YLFramework.Integrations.HybridCLR;` 与 `using YLFramework.Resource;`，自有 Module 派生类型继续使用 `YLFramework.Core`。旧类型字符串须按真实身份迁移；`MovedFrom` 不是 `Type.GetType` 的旧 AQN 别名。

先启动并挂载 ResourceSystem，再构造 `new HybridCLRLoader(resources, appContentCompatibilityId)`。调用 `LoadAsync` 或 `StartModuleAsync(manifestAddress, entryAssemblyName, moduleTypeFullName)`；请求可 yield，提供 `IsDone/Progress/Error`、原始 Exception、阶段/程序集/native 错误码和 RequiresRestart。类型只在精确入口程序集内解析。启动进度单调映射代码读取 0–0.8 与 Core 启动 0.8–1，终态为 1；Stage 区分读取、metadata/native 和具体 Core 阶段，不为同步 native 调用伪造时间平滑。`Abort` 总在改变状态前拒绝。

加载器借用 ResourceSystem。先验证全部 bytes，再在主线程加载 SuperSet metadata 和依赖优先程序集；只保留一个小 manifest lease 作为活动代码版本屏障，不保留大 DLL 资源租约。可以 Check/Prepare，但活动闭包 Activate 返回 Resource Busy。先关闭本入口启动的 Module，再 Dispose loader，最后由应用关闭自己拥有的 ResourceStartup。Dispose 不关闭外部服务、不卸载 DLL，也不允许同进程换版；同名不同内容、未知来源已加载或部分 native 失败需要重启进程。

MVID 从已校验 bytes 的 PE/CLR Module metadata 在不可逆调用前读取，使用范围检查的最小读取器，不依赖运行时 dnlib；当前 IL2CPP 未实现 CLR ManifestModule/GUID 反射，不能用它的空值表示验证通过。对 IL2CPP 显式 neutral culture 解析差异只做 invariant culture 规范化，其余完整身份、版本、公钥、hash、MVID 与 CodePackageId 比较保持严格。

`HybridCLRLoadResult.Mode` 明确区分 `Native`、`EditorSimulation`、`EditorFast`。已有 `Simulation` 读取语义保留，两个 Editor 模式均为 true。普通 Editor Simulation 仍验证 manifest 和全部代码 bytes，再复用 AppDomain 已加载的精确程序集；外部独立 DLL 不因此变成可模拟。Editor metadata 返回 OK 不等于 IL2CPP 验收，关闭 Domain Reload 也不会卸载程序集。

需要跳过代码产物、直接调试 Module 时，由应用主动选择仅在 `UNITY_EDITOR` 存在的 API：

```csharp
#if UNITY_EDITOR
var request = loader.StartEditorModuleAsync(projectId, entryAssemblyName, moduleTypeFullName);
yield return request;
#endif
```

EditorFast 要求已有 ResourceSystem 与 Core provider 绑定，仅使用 AppDomain 中唯一且精确的入口程序集和类型，不读取 manifest/DLL/metadata、不调用 native、不登记进程代码身份，`CodePackageId` 为空。它不会在正式加载失败后自动启用，也不能证明发布产物可用。仍由 loader 持有 Module 所有权：需先关闭 Module 再 Dispose；单纯关闭借用的 ResourceSystem 不能绕过这个门禁。

## Sample And Export

独立样例位于本可选根的 `Samples`，而非默认主体 `Samples` 根。显式调用 `HybridCLRExampleAssets.Create(newAssetsDirectory, persistentBaselineDirectory)` 生成自有项目、分组、Recipe 和 Profile，不更改官方设置。把 `YLFramework.HybridCLR.Sample.Entry` 和 `YLFramework.HybridCLR.Sample.Support` 配成热更程序集。Host 只保存入口字符串，不静态引用热更类型；Entry 依赖 Support，并通过既有 Core 配表路径创建 Model。

作者工具同时创建附加真实热更 `ExampleBehaviour` 的 Prefab。Module 通过 Core 读取资源配置并执行支持程序集中的泛型/async 调用；Host 在加载 DLL 后才通过 Resource 实例化该 Prefab，并检查热更脚本与异步结果，不在初始化 Catalog 时推断尚不存在的热更 ScriptableObject 类型。

应用显式持有 `HybridCLRExample`，yield `Run(configuration, projectId, compatibilityId)` 后读取 Result；关闭时 yield `CloseAsync()`。更新实验可先显式 `CloseCode()` 关闭 Module、实例和 manifest 屏障，待原生销毁排空后 Activate，但仍须新进程运行另一版代码。配置可由 ResourceLocalSnapshot.OpenBuiltIn/ArtifactStore 构造。样例不自动启动、找全场景或接管现有游戏。只改 Support 的版本文本可做 v2 补丁实验，但正式证明仍须相同 Player hash 下的新进程原生执行。

`HybridCLRIntegrationExporter.Export(absolutePackagePath, includeSample: true)` 显式导出本叶子的源码、asmdef 和依赖说明。默认不带样例，不递归依赖，不带 Tests、官方包、生成 DLL、基线、StreamingAssets 制品、私钥或仓库验收工具；默认框架仍导出六个目录（Editor、Plugins/BouncyCastle、Prefabs、Runtime、Samples、Documentation）及根文件 LICENSE.md，代码规范随 Documentation 导出，不含本可选叶子或宿主商业插件。

## 验证与测试边界

框架或相关程序集重编译后，源码仓库的 loader / Module 测试须先以当前程序集执行 `YLFramework.Tests.Integrations.HybridCLR.HybridCLRTestFixtures.Prepare()` 生成本机输入，不复用旧 bytes / manifest；执行方式见源码仓库的[测试工具说明](https://github.com/KONOK-A/YLFramework-dev/blob/main/Tools/Testing/README.md)。默认产品包不提供这些仓库工具或夹具。

原生验证需要对应 Editor / SDK / 后端的真实基座、metadata、热更程序集、Module 与 Prefab 执行链。EditorSimulation / EditorFast 不能代替 Native，最小本地闭环不能代替 HTTPS 更新、版本替换 / 回滚或任意反射泛型覆盖。

构建、运行、签名网络更新、裁剪级别和平台结果分别报告，并绑定当前源码、依赖、基座与制品。原专项的失败、修复和测试记录从 [历史索引](../../Documentation/IntegrationPlan.md) 查找，不在使用说明中复制旧叶子数量或将旧 Player 解释为当前成功。
