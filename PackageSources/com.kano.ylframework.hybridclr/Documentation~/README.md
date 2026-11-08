# HybridCLR 集成使用手册

> [包入口](../README.md)

本适配把官方 HybridCLR 代码产物接入 Resource 的发布、校验和版本激活，并使用 Core 启动精确的热更 Module。它不提供第二套下载器、全局密钥系统、SDK 安装器或可卸载程序集运行时。

## 前置与当前支持域

当前自有包为主包和 `com.kano.ylframework.hybridclr`。包声明依赖官方 `com.code-philosophy.hybridclr` `9.0.3` 与 `com.unity.nuget.newtonsoft-json`；从源码使用时为主包、本适配和 SDK 明确提供来源，不指望尚未发布的 Registry 补齐自有兄弟包。

公共本地 UPM / Git 配置见[安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)。本手册所有跨包链接均指向源码仓库当前文档。

| 用途 | namespace | 消费方 asmdef 引用 |
| --- | --- | --- |
| 代码加载、请求、manifest | `YLFramework.Integrations.HybridCLR` | `YLFramework.HybridCLR` |
| Profile、窗口、构建编排 | `YLFramework.Integrations.HybridCLR.Editor` | `YLFramework.HybridCLR.Editor` |
| 资源服务与运行时地址 | `YLFramework.Resource` | 主包 `YLFramework.Runtime` |
| Module 派生类型 | `YLFramework.Core` | 主包 `YLFramework.Runtime` |
| 资源作者/发布 API | `YLFramework.Editor.Resource` | 主包 `YLFramework.Editor` |

本包 Runtime 还引用 `HybridCLR.Runtime`，Editor 引用官方 Runtime / Editor 和主包 Runtime / Editor。功能目录与 namespace 不等于额外 DLL，不按目录名猜程序集。

### SDK 与 Native 门禁

本包 asmdef 使用 SDK 版本定义 `YL_HYBRIDCLR_9_0_3`，表达式为 `[9.0.3,9.0.4)`；不要手工添加全局宏绕过。正式构建进一步要求解析包、Installer package version 与本地 libil2cpp version 都精确等于 `9.0.3`。

当前严格预检接受的 SDK 来源声明如下，合入使用工程 manifest 的 `dependencies`，保留其中其他依赖：

```json
{
  "dependencies": {
    "com.code-philosophy.hybridclr": "https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3"
  }
}
```

UPM 必须实际解析为 Git 来源，lock 的 URL 与上项一致，hash 必须是 `6cadd24d101f4cbd00cd80190106f6621132d24d`；可用的 PackageInfo revision/hash 也须一致。此 URL 是当前门禁的一部分，改成完整 commit URL、镜像、Registry 或同名本地包，即使字节或版本相同也不会通过该门禁。不要手工编辑 lock 伪造解析结果。

使用官方 Installer 显式准备项目本地 LocalIl2CppData，启用 HybridCLR 且 `useGlobalIl2cpp=false`。本适配不自动安装、切换后端或改共享 Editor。SDK/native、独立维护补丁及其许可证不在包内；若使用维护 native，应另外保留来源、patch、文件清单、安装树和最终 Player/AOT 指纹。版本标记相同不能单独证明 native 来源或可用性。

当前原生构建只接受 Windows Editor、已激活的 `StandaloneWindows64`、IL2CPP、`.NET Framework` API Compatibility、精确匹配的 Development 设置，以及已安装的 Windows IL2CPP build support、Visual Studio x64 C++ 工具和 Windows 10/11 SDK。生成可运行 Player 时 `Build Scripts Only` 与 Windows `Create Solution` 都必须为 false，导出 Visual Studio 工程不是 Player 成功。

这些是当前实现门禁，不是全部 Unity 补丁、High stripping、其他平台或任意动态泛型的通过承诺。每种 Editor/SDK/native/后端/裁剪组合仍需自己的真实验证。

Runtime 的 Native 入口也仅供 Windows IL2CPP 使用，Mono Player 或其他平台会被拒绝，不会自动转为 Editor 模拟。

## 配置 Profile

先在官方 HybridCLRSettings 登记热更 asmdef 或外部 DLL 的明确集合。不使用 Assembly-CSharp，不让 AOT 静态引用热更类型，也不手写 DLL 加载顺序。

打开 `YLFramework/Integrations/HybridCLR` 选择或新建配置；单独创建 Profile 的菜单是 `Assets/Create/YLFramework/HybridCLR/Build Profile`。窗口新建动作创建自有 ResourceProject、BuildAndPublish Recipe、Profile 和代码目录映射，不创建样例内容，也不修改官方热更登记。

`HybridCLRBuildProfile` 当前字段如下，不用其他拼写推断数据：

| 字段 | 当前含义 |
| --- | --- |
| `recipe` | Resource 构建发布配方，提供 project、target 和 appContentCompatibilityId |
| `development` | 与当前 Editor / Player 选项精确匹配的 Development 标记 |
| `generatedAssetDirectory` | 本配置拥有的 Assets 相对生成根 |
| `baselineDirectory` | 持久冻结基线绝对目录；不在 Assets、Packages、Library、Temp 内 |
| `hotUpdateAssemblies` | 项目入口的精确程序集简单名称；空集合表示官方全量 |
| `baseProfile` | 可选的直接基座拥有者，仅允许一层绑定 |

Recipe 必须使用 `BuildAndPublish`，不生成 C# 地址引用。构建前显式保存 Profile、Recipe、Project 与被绑定的基座配置；Validate 不自动替用户保存或修正设置。

### 生成资源映射与归属

以 `generatedAssetDirectory` 为 SourceDirectory，在 `recipe.project` 中选择其 `hybridclr` 子目录，只包含 `.bytes` / `.json`，不设置地址覆盖或文件夹别名，得到：

```text
hybridclr/manifest.json
hybridclr/assemblies/*.dll.bytes
hybridclr/metadata/*.dll.bytes
```

不要选择 owner 记录、原生生成目录或原始 `.dll`。窗口的“关联生成资源”显式建立规范目录选择，保留其他 Group，支持 Undo，重复调用不追加重复项。更换生成目录后，若另一来源仍占用 `hybridclr/*` 地址，先拒绝并要求明确调整，不删除文件或静默合并两套映射。

生成目录由具体 Profile 持有，拒绝未知文件、链接和认领已有用户目录。受管输出由生成器维护，写入保留现有 GUID，manifest 最后提交；不要手改生成文件或把中断目录当作已经完成、可恢复的成功产物。

## 构建新基座

1. 在专用构建工程准备上述 SDK/native、目标与工具链，保存所有输入。
2. 由应用显式准备标准 `Assets/StreamingAssets` 父目录，不手建或认领其下的 YL 受管根。
3. 在窗口执行“检查配置”，查看入口、当前 Player 脚本图/外部 DLL、目录与映射。这个检查可在 Mono Editor 执行，不代表正式编译或原生可用。
4. 执行“检查原生构建环境”，即 `HybridCLRBuildPipeline.Validate(profile)`，处理严格 Preflight 的首因。
5. 使用窗口构建新基座，或调用 `HybridCLRBuildPipeline.BuildBasePlayer` 提交新/空 Player 输出目录及与 enabled scenes 完全一致的场景列表。绑定了 baseProfile 的项目不能执行此操作。
6. 读取真实阶段报告、Player 输出和最终冻结基线，不能只检查生成目录存在或某条成功日志。

流程依次执行编译、Il2CppDef、link.xml、空桥、AOT 剥离、完整桥、manifest/metadata、代码载荷提交、正式 Resource 发布、内置部署、真实 Player 构建，以及最终 AOT 核对和基线冻结。任一步失败都不是基座成功，应保留已完成阶段与原始错误，不用手工补拷掩盖失败。

资源构建继续执行 Resource 正式指纹检查；不能因为未验证的 `-nographics` 环境中有成功日志就跳过。部署中断按正式 Resource Recover 依据 journal 处理，不手工删除发布或替换其受管根。资源作者与交付操作见[Resource 作者手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource/Authoring.md)与[交付手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource/Delivery.md)。

`BuildBasePlayer` 的 CancellationToken 在阶段边界检查，并传入资源流程；不强制中断同步 native 步骤，也不回滚之前已经生成、发布或部署的内容。应按报告处理真实状态，不把取消包装为成功。

## 冻结基线与补丁

持久基线不是可变补丁工作区。当前基线 schema 为 1，包含 AOT、热更输入、必要 native 生成文件、程序集 identity/MVID、每文件长度/hash、全局登记、构建设置指纹和 PlayerBuildHash。`baseline.json` 与 schema-1 `complete.json` 完成标记的摘要必须匹配，全部文件均受核验；不能靠手补完成标记跳过冻结。

PlayerBuildHash 是构建输出身份，不是运行成功证明。AOT / native / 程序集 API、泛型或裁剪输入变化时，即使程序集名称相同也不代表基座兼容。

热更代码改变后调用 `HybridCLRBuildPipeline.PublishPatch`。它读取完整持久基线，重新编译项目代码，使用冻结 AOT 校验，然后提交代码资源并执行 Resource 发布；不 Strip、GenerateAll、重建 native bridge、构建 Player、内置部署或修改冻结基线。

以下情况须使用新目录、新兼容号与新基座，不覆盖冻结制品：

- 增加官方全局热更登记，或出现未登记的 AOT metadata、泛型/native bridge、反向回调、calli、P/Invoke 需求。
- 基座 owner、工具/设置身份、平台、Development、兼容号不符，或冻结基线缺失、损坏、混入未知文件。
- 应用改变 native / AOT 输入，不能再证明补丁属于现有基座的冻结能力集合。

静态分析只覆盖静态可见需求，不承诺任意反射/动态泛型安全。项目入口可以在同一基座已经登记的集合内重新选择，但选择必须明确。

`BuildBasePlayer` / `PublishPatch` 接受应用注入 `IResourceContentKeyProvider`，保密密钥不写入 Profile。签名更新沿用 Resource 的签名、发布版本、下载校验与 Check / Prepare / Activate，不另建下载器和密钥系统。

## 项目选择与共享基座

`hotUpdateAssemblies` 是项目入口，不是第二份编译登记。空集合发布官方全量；非空从实际编译 DLL 的引用展开热更依赖闭包，只发布该闭包与其需要的 metadata。动态反射入口不能从静态引用推断，须显式选择。

原生编译、剥离、桥接与冻结登记覆盖官方全局并集。基线的 `InitialManifest` 表示全局登记，不等于某个项目实际发布的 manifest。

项目 B 可把 `baseProfile` 指向未绑定其他配置的拥有者 A。两者须使用同一个规范化基线目录、目标、Development 与兼容号，但各自保留 ProjectId、Recipe、生成目录和文件归属。拒绝自引用、链式绑定或错误 owner。

B 首发也走 PublishPatch，不再次构建 Player；其热更集合、metadata 与 native 需求必须是 A 冻结登记的子集。目录中存在某个 AOT DLL 不等于它已经登记为可用 metadata。

共享 DLL 保留完整 identity、MVID、hash 和 length。同一编译快照的投影使用相同字节；不同时间发布改变了共享 DLL 时，由应用协调版本，不能任意混用。Runtime 在不可逆加载前拒绝共享程序集冲突，不通过项目名前缀绕过身份。

## 窗口操作边界

窗口资产编辑使用 SerializedObject / Undo，顶部“保存”只保存当前 Profile、Recipe、Project。打开、绘制和切换配置不会安装、生成或自动保存；窄窗口使用任务下拉和滚动布局。

程序集资产选择器仅编辑项目入口。未在官方集合登记的项会提示并在正式预检拒绝，不自动写回全局设置。路径通过选择器明确指定，构建阶段与原始错误在诊断页分列。

## Runtime 加载

先启动并挂载 `ResourceSystem`，再在主线程构造 `new HybridCLRLoader(resources, appContentCompatibilityId)`。启动 Core Module 前还需完成 Core resource provider 绑定，资源接入见[Resource 手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource.md)。

| API | 责任 |
| --- | --- |
| `LoadAsync(manifestAddress)` | 验证并加载代码包，地址必须是明确 ProjectId 下的规范 `hybridclr/manifest.json` |
| `StartModuleAsync(manifestAddress, entryAssemblyName, moduleTypeFullName)` | 加载代码后，仅在精确入口程序集内解析具体 Module 类型并用 Core 启动 |
| `Dispose()` | 请求结束且本入口 Module 已关闭后，释放 manifest 屏障；不关闭借用的资源服务 |

请求可以 yield，提供 IsDone / Progress / Error、原始 Exception、Stage、AssemblyName、NativeErrorCode 和 RequiresRestart。Module 启动进度单调映射代码加载 0 到 0.8、Core 启动 0.8 到 1，终态为 1；Stage 区分读取、metadata/native 与 Core 阶段，不为同步 native 调用伪造时间平滑。

`Abort()` 始终在改变状态前抛出 NotSupportedException，不能撤销 native 加载。关闭流程必须先等请求终态，不能把取消资源请求等同于取消代码加载。

### Manifest 与进程身份

当前 manifest schema 为 1，目标是 StandaloneWindows64，HybridCLRVersion 为 9.0.3，并校验 ProjectId、应用兼容号、精确 UnityVersion 与 Development。代码清单上限 1 MiB，热更与 metadata 集合各最多 128 个程序集，单 DLL 最多 64 MiB、总载荷最多 256 MiB。

全部代码 bytes 在不可逆调用前验证 identity、引用关系、长度、SHA256、MVID 与 CodePackageId，然后主线程加载 SuperSet metadata 和依赖优先程序集。MVID 从已校验 PE/CLR Module metadata 读取，不依赖 Runtime dnlib，也不用 IL2CPP 未实现的 CLR ManifestModule/GUID 反射空值当验证通过。

IL2CPP 显式 neutral culture 的解析差异只作 invariant culture 规范化，其余版本、公钥、完整身份、hash、MVID 与 CodePackageId 仍严格比较。

### 版本屏障与关闭顺序

loader 借用 ResourceSystem，只保留小 manifest lease 作为活动代码版本屏障，不长期持有大 DLL 资源租约。活动闭包可以 Check / Prepare，但 Activate 会返回 Resource Busy。

应用必须按此顺序结束自己的责任：

1. 等待代码/Module 启动请求终态，停止使用入口，关闭本 loader 启动的 Module，并释放应用持有的热更实例/资源。
2. Dispose loader，释放 manifest 版本屏障；仍有请求或 Starting / Running / Closing Module 时拒绝 Dispose。
3. 最后由应用关闭自己拥有的 ResourceStartup，等待并检查关闭请求；不能让 loader 关闭共享服务。

Dispose 不卸载 DLL、不清理进程代码身份，也不能允许同进程切换版本。同名不同内容、未知来源已加载程序集或部分 native 失败会要求重启进程，释放全部资源也不消除 RequiresRestart。替换代码后须在新进程执行，不能靠关闭 Domain Reload 或重建 loader 换版。

## Editor 两种模式

`HybridCLRLoadResult.Mode` 区分 `Native`、`EditorSimulation`、`EditorFast`；当前 `IsSimulation` 在两种 Editor 模式中都为 true。

普通 EditorSimulation 验证 manifest 和全部代码 bytes，再复用 AppDomain 已加载的精确程序集，不调用 native。外部独立 DLL 不因此自动变成可模拟，metadata 返回 OK 也不证明 IL2CPP 路径可用。

仅在 `UNITY_EDITOR` 可调用 `StartEditorModuleAsync(projectId, entryAssemblyName, moduleTypeFullName)` 显式选择 EditorFast。它要求 ResourceSystem 与 Core provider 已绑定，只使用 AppDomain 中唯一且精确的入口程序集/类型，不读 manifest、DLL、metadata，不调用 native、不登记进程代码身份，CodePackageId 为空。

EditorFast 不在正式加载失败后自动启用，不能证明发布产物可用。它仍由 loader 持有 Module 责任，必须先关闭 Module 再 Dispose，单纯关闭借用 ResourceSystem 不能绕过门禁。

## 导入并运行样例

样例位于本包 [Samples~/HybridCLR](../Samples~/HybridCLR/)，安装后默认不编译。操作顺序如下：

1. 在 Package Manager 的 Samples 显式 Import `HybridCLR Module`。
2. 调用 `YLFramework.Samples.HybridCLR.Editor.HybridCLRExampleAssets.Create(newAssetsDirectory, persistentBaselineDirectory)`，在新自有 Assets 目录生成项目、分组、Recipe、Profile 和样例内容，不更改官方设置。
3. 在官方设置把 `YLFramework.HybridCLR.Sample.Entry`、`YLFramework.HybridCLR.Sample.Support` 登记为热更程序集，再按前述流程建立新基座。
4. 由应用持有 `YLFramework.Samples.HybridCLR.HybridCLRExample`，在应用协程中等待 `Run(configuration, projectId, compatibilityId)`，读取 Result；关闭时等待 `CloseAsync()` 并检查结果。

Host 只保存入口字符串，不静态引用热更类型。Entry 引用 Support，通过 Core 配表路径创建 Model、执行支持程序集的泛型/async；作者工具还生成附加真实 `ExampleBehaviour` 的 Prefab。Host 加载 DLL 后才通过 Resource 实例化并检查该 Prefab，不在初始化 Catalog 时推断尚未加载的热更 ScriptableObject 类型。

ResourceConfiguration 可结合 `ResourceLocalSnapshot.OpenBuiltIn` / ArtifactStore 构造。样例不自动启动、不扫描全场景，也不接管现有游戏。失败后仍由应用关闭已经创建的入口。

更新实验可以先 `CloseCode()` 关闭 Module、实例和 manifest 屏障，待原生延迟销毁排空后 Activate，但另一代码版本仍须新进程运行。只改 Support 版本文本能用于补丁实验，不替代同一个 Player 身份下的新进程原生执行验证。

## 所有权与验证边界

导入样例副本、场景、Profile / Recipe / Project、SDK、LocalIl2CppData、生成资源、冻结基线与 Player 均由使用工程明确持有。升级前核对自有修改，不保留重复同名样例程序集；安装、升级或卸载适配不改写这些数据。

本包不携带 Tests、官方 SDK/native、dnlib 副本、生成 DLL、基线、StreamingAssets 制品、私钥或仓库测试工具。不要手改 Library、PackageCache、Editor 安装或冻结制品，需要改变 native/AOT 时明确建立新的输入与基座。

配置检查、编译、Player 构建、原生运行、HTTPS 签名更新、版本替换/回滚、裁剪和平台结果是不同证据，必须绑定实际源码、依赖、基座与制品。进程超时、强退或非零退出如实保留，不改成成功。EditorSimulation / EditorFast、源文件准备或目录中存在 metadata 不能替代真实 native Module 与 Prefab 执行链，也不能外推任意反射和动态泛型。
