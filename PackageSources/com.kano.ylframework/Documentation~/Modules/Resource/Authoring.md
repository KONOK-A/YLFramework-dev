# 资源制作与构建

> [资源管理](../Resource.md) · [资源交付与更新](Delivery.md) · [文档首页](../../README.md)

本页面向在 Unity Editor 中组织资源、准备图集、构建并本地发布的作者。ResourceProject 是工程内的一份配置，不是 Unity 工程；本地发布不等于导出、签发、部署或 Runtime 激活。

## 第一次构建

1. 打开 `YLFramework/Resource/Workbench`，选择已有资源项目，或用选择器的“新建Project...”创建配置。
2. 在“项目设置”添加实际使用的资源目录。音频、图片、Prefab、Scene 可位于不同目录，不必移动或复制，也不必把整个 Assets 登记为根。
3. 在“资源配置 > 主资源”创建 Group，从 Unity Project 选中文件/文件夹，用“添加资源…”或拖入当前 Group。目录登记只允许读取，不自动把其中所有文件选入构建。
4. 顶部“保存”保存资源项目。初次可选 contentEncryption=None；它不表示不压缩，也不替用户修改已有压缩/加密配置。
5. 进入“构建发布”，无需先建 Recipe。填写应用约定的“资源兼容标识（App）”，如 app-v1，核对已激活目标，再点“构建并本地发布”。先处理未保存配置、脏场景或资源校验提示，工作台不自动存场景或切平台。
6. 分别查看构建/发布/常量生成结果，再到“版本交付 > 发布版本”显式“读取已登记版本”。图集、分包分析、缓存查询、导出、签发与内置部署均按需使用，不是第一次构建的必填步骤。

“诊断 > 开始使用”和空状态提供同一路径；导航按钮只切页，不构建、注入 key 或自动保存。运行正式样例见[最小接入](../Resource.md#最小接入)。

## 资源项目与目录

### 目录、Group 与地址

一个 ResourceProject 可有多个 `sourceDirectories` 和多个 Group。同 Group 可从不同目录选择；Group 表达组织与初始分包意图，不是磁盘目录，也不是物理 Bundle 的固定父节点。

```text
Assets/Art/Textures/     addressPrefix: textures
Assets/Art/Materials/    addressPrefix: materials
Assets/Audio/            addressPrefix: audio
Assets/GamePrefabs/      addressPrefix: prefabs
Assets/GameScenes/       addressPrefix: scenes
```

例如 Characters Group 可同时包含 GamePrefabs/Hero.prefab 和 Audio/Hero.wav，Prefab 依赖的 Material/Texture 仍由 BuildMap 解析共享依赖；未选中的独立文件不会因登记目录自动打包。文件夹定义会递归展开，extensions 留空为全部可打包类型，筛选使用带点扩展名如 `.png`。

目录以 Unity GUID 登记，检查时重新解析实际 Assets 路径。允许真实子目录，不能用整个 Assets、StreamingAssets、Packages、外部磁盘或链接目标。脚本、Unity 内置依赖与 Packages 代码仍按相应依赖规则处理，不能用来源目录规则误判合法代码依赖。

- 同 Project 目录不能重复/嵌套，不同 Project 的普通来源范围也互斥；其他 Project 的生成范围受保护，没有跨 Project 依赖协议。登记不授予删除或覆盖源文件/meta 的权限。
- 默认地址是持久前缀加目录内相对路径，如 prefabs/Hero.prefab；空前缀则只用相对路径。增其他目录、重排或改显示名不影响原地址，改前缀/移到另一来源目录可能改变下次地址。需要长期稳定时填写显式 address，别名 aliases 是同一主资源的额外入口。
- 地址、alias 与仅大小写不同的冲突均拒绝，显示双方来源，不加随机后缀。文件可覆写地址/alias，文件夹不能覆写；子资产沿主资产规则派生，没有另一个选择协议。
- 新增/移除/替换目录只改配置，可 Undo/Redo，不删源文件或 Group 条目。可保存待修草稿，但目录失效/重叠/失去合法来源的条目或图集规则会阻止构建，不静默漏包或扩大来源。

ResourceProject 使用 `projectId/displayName/sourceDirectories/groups/compression/contentEncryption/contentKeyId/automaticAtlases` 等当前作者字段。sourceDirectories 清空表示没有来源，构建拒绝。Editor 作者字段为 camelCase，不意味着 Catalog/workspace/发布协议的 PascalCase 键可改名；当前 Catalog Schema v1 不因作者目录结构而改变。

Editor 方法内可用支持 Undo 的入口编辑目录：

```csharp
using UnityEditor;
using YLFramework.Editor.Resource;

ResourceEditorOperations.SetSourceDirectories(project, new[]
{
    new ResourceSourceDirectory
    {
        guid = AssetDatabase.AssetPathToGUID("Assets/Art/Textures"),
        addressPrefix = "textures"
    },
    new ResourceSourceDirectory
    {
        guid = AssetDatabase.AssetPathToGUID("Assets/GamePrefabs"),
        addressPrefix = "prefabs"
    }
});
ResourceEditorOperations.SaveProject(project);
```

### 工作区归属

有效来源、前缀和生成登记都进入 Builder 冻结与后验，展示顺序不构成新语义。工具源码本身参与 Workspace 指纹，升级工具可能使缓存合法失效；不因此自动迁移/删除其他 owned 缓存。普通浏览、选中和排序不重算依赖或 SHA。

Workspace 按 Project GUID/设置隔离；不可变发布位于 `AssetBundles/YLFramework/<ProjectId>/<Platform>/<CatalogDigest>/`。未知文件、归属、链接或篡改拒绝，不覆盖已发布目录。源文件、图集生成物、raw workspace、交付版本与生成 C# 各有不同所有权，不能混作一个可随意清理的输出目录。

## 资源工作台

打开/浏览/排序/搜索不初始化 Runtime，不自动构建、读历史或扫缓存。五个任务页各有明确操作：

| 页面 | 任务与执行边界 |
|---|---|
| 资源配置 | 编辑真实 Group/文件/目录/图集；“计算预览”才解析 Map，图集预览/生成/准备/删除分别显式执行 |
| 分包分析 | 当前配置预估或所选 BuildResult，查看 Bundle/来源/共享依赖/被依赖树 |
| 构建发布 | 可选 Recipe 或无配方 App/force/常量选项，分阶段报告结果 |
| 版本交付 | 显式读历史/当前索引，选确切版本再导出、签发、部署；Publisher 与内置部署各自恢复 |
| 诊断 | 已启动全会话采样、明确 Store 只读查询或开始使用指南，不冒充当前所选 Project 的运行会话 |

### 编辑与保存

顶部选择器按显示名/ProjectId/路径检索，完整路径/GUID 可复制、定位。新建只建配置，不移动来源或接管输出。具体 Group 才能增删改，“全部 Group”只读聚合；高亮不是构建开关。Ctrl/Shift 多选，筛选隐藏项仍计入批量操作，执行前看底栏范围。文件夹保持一条真实定义；拖入子资产按主文件 GUID 登记。移除定义/来源可 Undo，不删资产；删除 Group 只删组和其选择。

Ctrl+F 搜索，Alt+G 新增组。顶栏保存始终只存 Project；项目设置/主资源 Ctrl+S 同样只存 Project，图集页只存当前规则，配方页只存所选 Recipe。Enter/失焦/保存前提交组名编辑；切页/关闭/收起不自动保存资产或场景。执行前仍要处理各自脏配置和场景并重新验证。

### 分析与结果

最终物理 Bundle 受共享依赖、Scene 分离和环归并影响。依赖闭包源文件不是逐文件因果边，不能据此断言某 Prefab 精确引入某文件。ResourceBuildAsset.Source 是 Map/BuildResult 的只读来源副本，不是第二 Project。定位定义会复验 Project/Group/Selection GUID、内容/路径或图集配置 GUID/规则 Id，不按过时 index 编辑；过期报告保留原事实，不用新配置覆盖。

构建 Placement/SourceBytes/Dependents 与实际 FileBytes/SHA 分开，源字节、压缩制品、native memory 与累计 IO 不是同一指标，不以共享引用次数判重复打包。

### 交付与诊断

“读取已登记版本”和“读取当前发布索引”保留各自时间/失效状态；所选版本、Publisher 当前索引、Runtime active 不等同。单次刷新不复验其他快照，不从目录名猜最新。导出捕获完整 Release，不覆盖/同步/上传；所选版签发严格 ReadVersion，当前索引签发是另一个显式选择。

内置部署先只读预检，确认窗展示身份、来源/目标、文件数、逻辑字节、StreamingAssets 文件、外部 stage/锁/journal 及目录 meta/导入影响。内容可完整滚动/选择/复制，关闭/Esc 取消；确认后 Deployer 仍复验。逻辑字节不是磁盘峰值；恢复不猜错误文本、不手改控制文件、不覆盖旧部署或切 active/LKG，见[内置部署](Delivery.md#内置部署)。

运行诊断未初始化表示不可用，不是故障。自动采样默认关闭，开启从 5 秒间隔开始；离页/隐藏/关闭/转换/重载暂停，返回不自启。只读查看不改历史，显式配置/关闭历史清 buffer、不回填。数据预算、native read buffer、CNG owner/key 不相加成总进程内存。

Store 查询必须输入应用实际绝对 cacheRoot 和完整 ProjectId/Platform/App，不猜 persistentDataPath、不写 Project/Recipe。每次一个在途查询，输入变/取消/离页/重载 Dispose 枚举器；旧报告标旧，不伪造 Canceled。此页不 Recover/Cleanup/删/激活/Mount/取 key/加 pin/建 root/锁。Ready 也不表示 App 兼容、签名新鲜度或解密通过，见[Store 只读查询](Delivery.md#store-只读查询)。

### 窗口与进度

分隔、输出高度、表头与可复核选择仅为本地窗口状态，不写业务资产。窄窗按需切列表/详情，重载后复核身份，歧义清选择；临时报告不恢复，也不重执行操作。最多保留 32 次构建尝试，切 Project 清排队回调，但已开始同步构建/发布不可交互取消。阶段只表示到达边界，不提供百分比/剩余时间/随时取消。

## 图集生产

### 手工图集

Atlas 必须在登记来源内、非 Variant、Include in Build=false、Sprite 名唯一，并作为主资产加入 Group。首次或改 packables 后保存配置，再显式 `ResourceEditorOperations.PrepareAtlases(project)`；它会先生成/更新配置的自动图集，再准备本 Project，不是只读调用，也不写无关 Project。

Runtime 用 SetAtlasSprite 或 SpriteAtlas Lease/GetSprite；克隆 Sprite 自毁、共享 Texture 不销毁，见[图片加载](../Resource.md#图片加载)。Variant、透明晚绑定和全局 atlasRequested 未提供。

### 自动图集

流程为 **Editor 规则 → 自有 Atlas 主资产 → Group/BuildMap/Bundle/Catalog**，不是另一 AtlasManager、Runtime 映射或透明 Sprite 替换。输出 `.spriteatlasv2`，Include in Build=false；不改源 Texture/Sprite Importer、手工 Atlas、全局图集设置或 Scene/Prefab。

1. 在 Project 登记来源目录与目标 Group；一个规则可用多个来源目录的 Sprite，Group 可只含自动 Atlas，无需虚假手工条目。
2. 保存 Project，在“资源配置 > 图集”或 Project Inspector 显式选 Assets 或其下已有文件夹作父目录，再“新建并登记生成目录”。只建此前不存在的子目录，父目录按 GUID 登记，不按来源推断。不接管已有空目录、文件或孤立 meta。生成目录、AutomaticAtlases.asset、Atlas 和 meta 须一起保留，缺失/换绑原身份会阻止构建。
3. rules 中 id 为稳定技术键，groupId 对应已有 Group，sources 为目录或已按 Sprite 导入的 Texture2D。递归收集，支持 Multiple Sprite；普通纹理不改 Importer，空目录不建空 Atlas。一个纹理不能入多个自动规则，单 Atlas Sprite 名非空唯一。
4. Together 将规则所有 Sprite 合图，address 是完整逻辑地址，如 atlases/ui。PerFolder 按真实父目录分图，地址由规则地址、所属资源目录持久前缀、相对文件夹构成；输出身份含文件夹 GUID，同名末级不合并。移动可保留 Atlas GUID，但派生地址可变；需稳定单一业务地址时用 Together。
5. 配置 Padding=2/4/8、MaxTextureSize=32..8192 的二次幂、旋转/紧密、Readable、MipMaps、sRGB、FilterMode、压缩。默认 Padding=4、不旋转/紧密/可读/mip，sRGB=true、Bilinear、2048、Uncompressed。Unity 可多页承载，不用不稳定成员 hash 改名。规则 sources 不能与生成范围相交，含其祖先来源目录，不把输出/配置再作输入。
6. 保存，预览 Create/Update/Current/Obsolete，再“生成、更新并准备”。只对明确候选做必要正常同步导入，不改 Importer、不替用户保存其待提交改动。正式 Build 同样先生成/准备，再冻结输入、分析依赖和构建，不必手工加输出到 Group。
7. 存续 Atlas 保留 meta/GUID，相同有效输入不改字节；改地址/设置不改 GUID，改规则 Id 是新身份。Obsolete 退出选择但不自动删，显式“检查引用并删除失效输出”或 DeleteObsolete(project,key) 会复验归属/内容/meta/引用，仍被用则拒绝。

生成目录建议位于所有来源之外，也可位于某来源内；Group 文件夹递归会排除登记生成范围，但 Atlas 规则来源仍不得与其相交。生成目录无需再登记为普通来源；有效 Atlas 经指定 Group 入包，内部 Sprite 不自动变 Catalog 子资产。另选源纹理的普通 Sprite 子资产沿既有规则处理，不能手工选择生成配置/输出绕过所有权。

### 脚本配置与恢复

以下完整 Editor 类型要求 Project 已保存、ui-atlases Group 存在，sprites 位于登记来源且已按 Sprite 导入，generationParentPath 为明确已有 Assets 父目录：

```csharp
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

public static class ApplicationAtlases
{
    public static ResourceAtlasConfiguration Configure(ResourceProject project,
        Texture2D sprites, string generationParentPath)
    {
        var configuration = ResourceAtlasGenerator.CreateConfiguration(
            project, generationParentPath, "GeneratedAtlases");
        configuration.rules.Add(new ResourceAtlasRule
        {
            id = "ui", groupId = "ui-atlases", address = "atlases/ui",
            grouping = ResourceAtlasGrouping.Together,
            sources = new Object[] { sprites }
        });
        EditorUtility.SetDirty(configuration);
        AssetDatabase.SaveAssetIfDirty(configuration);
        return configuration;
    }

    public static ResourceEditorBuildReport Build(ResourceProject project)
    {
        Debug.Log(ResourceEditorOperations.Preflight(project, "app-v1"));
        return ResourceEditorOperations.BuildAndPublish(project, "app-v1");
    }
}
```

Preview 不导入/存/准备，Generate(project,cancellation) 是显式写入。只读 BuildMap/Simulation 遇待生成/更新会拒绝，不用旧发布冒充。Preflight 可报告图集计划并说明最终依赖/Workspace 未完成；正式 Build 仍在生成后全部验证。

冲突、未知文件、缺失输出/meta、外部修改明确抛错，不以普通 Update 强盖。异常/取消只回滚本次实际开始写且仍可确认归属的资产对/登记，写入与恢复前都复验；外部占用但自己尚未写的路径不删。写后又被外部修改则保留冲突，报告原异常和恢复异常；校验磁盘 meta GUID，不能只靠 AssetDatabase 缓存。不承诺断电/崩溃绝对原子恢复；恢复原 Atlas/meta 字节，或人工确认归属/引用后另设范围，不自动认领未知文件。

ResourceBuildResult.AtlasGeneration 为本次生成前计划。BuildError/PublicationError/GenerationError 要分别看，GenerationError 指**资源地址常量**生成，不表示自动 Atlas 状态。换引擎/平台/设置后分别验证构建、实际像素和 Atlas Lease，不能仅检查资产存在。

## 构建与发布

压缩仅 Lz4/Lzma/Uncompressed，始终 StrictMode；压缩进入 Workspace 身份，forceRebuild 显式选择，不接受任意 flags。只构建已激活、已安装且主机可 native 读回的桌面目标，不自动切平台。支持范围不是三平台实测承诺，见[平台与后端限制](Delivery.md#平台与后端限制)。

Publisher 输出 resource-catalog.json 和 release index，BuildId 标识 payload，CatalogDigest 标识最终目录字节；摘要不是签名。ReadHistory/ReadVersion 只读 owned 完整版本；ExportRelease 只向工程外已有父目录下不存在且名称等于 digest 的目录导出并复验，不上传、覆盖或同步。

Windows Mono 文件 API 的长路径约束仍影响 Unity 2022.3 构建，实际 Workspace/加密交付全路径须在 260 字符内。Builder 在认领 Workspace/native 前检查，不自动缩名/搬产物/清缓存。所需工程路径余量随 Bundle 名改变，没有所有工程通用的固定根路径上限。

### 分阶段处理失败

| 提示或结果 | 处理 |
|---|---|
| 不在登记来源 / 目录重复、重叠、失效 | 核对实际路径/GUID，登记真实范围或调整条目，不自动扩大读取或删文件 |
| 地址/alias 冲突 | 按双方来源改显式地址、持久前缀或 alias，再计算 |
| 配置未保存 / 脏场景 | 分别保存 Project、图集或 Recipe，自行处理场景，顶栏不代存全部 |
| 无法确认文件归属 / 图集外改 | 保留内容，核对 Project/配置/GUID/meta/登记，不覆盖认领 |
| BuildError | 构建未完成，先处理来源、图集、目标、路径或输入验证 |
| PublicationError | 发布提交可能尚未确认，用 Publisher 专用恢复核实，不能断言索引没变 |
| 已发布但 GenerationError | 版本仍有效，修生成配置后单独重试，不重发或回滚 |

构建、指纹/SHA 后验、内容认证、复制、下载和 native 物化分别计量，命中缓存不等于整体 no-op；有无法冻结的构建回调时仍可能 native 重建。Editor 与受控回环吞吐不代表目标 Player/公网表现。不可用计数应保留 null/Unavailable，不按 0 宣称零分配。

## 构建配方

通过 `Create/YLFramework/Resource/Build Recipe` 创建保存在 Assets 的 ResourceBuildRecipe，设置 project/target/appContentCompatibilityId/action/forceRebuild 与必要常量选项后明确保存。没有 Recipe 也可直接 BuildOnly/BuildAndPublish 或 Builder/Publisher；选中 Recipe 丢失则报错，不静默回退。

- 资源、Group、压缩与加密来自引用 Project，Recipe 不覆盖/复制/临时修改 Project，无多级继承。
- target 明确、已安装/激活并匹配主机可读回桌面目标。BuildOnly 不发布/生成，BuildAndPublish 才可选 generateReferences/referenceDirectory。
- ResolveRecipe 只解析非秘密值，不取 key/构建/发布/生成/初始化。ExecuteRecipe 要求 Recipe/Project 已存，起始复制成只读 ResourceBuildInvocation；后续编辑影响下一次。资源由 Builder 冻结并完整后验，不能冻结的回调仍阻止整体 skip。
- Inspector/Workbench/无窗口都走 ExecuteRecipe。Invocation/Summary 保留实际配方路径、Project/GUID、平台、App、action、force、压缩/交付/生成选项；Recipe 名/GUID 不进构建内容身份。
- Workbench 主动作照 recipe.action，不临时改 Action。recipe.project 与上下文不符时列双方 Id/路径/GUID，阻止并提供显式切换/解除选择，不自动改资产；执行时再冻结/复验。
- 常量集合为当前平台已登记发布 Project 的 Catalog，要求 Unity/App 一致，不只当前 Project。集合检查失败保留真实原因，版本仍有效，可在“版本交付 > 恢复与其他”单独生成，不一概归因目录。
- CheckReadiness/InspectRecipePreflight(full:false) 只查静态前缀，不取 key、建 Map、扫 Workspace/历史或 SHA。InspectPreflight/InspectRecipePreflight(full:true)/Preflight 共用正式流，结构化状态区分 StaticBlocked/StaticReady、AtlasPreparationRequired、Complete、Failed；图集待生成不是完整成功。
- ResourceBuildProgress.StageReached 是主线程实际阶段观察，非控制/故障注入点；订阅方负责退订，不写入或重入构建。observer 内执行重入拒绝，观察异常隔离且不输出敏感原文，不替换输入冻结或 finally。Workbench 只在自身操作范围订阅，finally/关闭解绑。
- Recipe 不存密钥或签名私钥。加密显式传 IResourceContentKeyProvider，或用明确进程来源 YL_RESOURCE_CONTENT_KEY；浏览/保存不取环境值。不签发、不部署 StreamingAssets、不上传/激活 Runtime。

以下完整类型可放 Editor-only 程序集，不依赖导入样例：

```csharp
using UnityEditor;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

public static class ApplicationResourceBuild
{
    public static ResourceEditorBuildReport Build(string recipeAssetPath,
        IResourceContentKeyProvider applicationKeys = null)
    {
        var recipe = AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(recipeAssetPath);
        return ResourceEditorOperations.ExecuteRecipe(recipe, provider: applicationKeys);
    }
}
```

在稳定 Editor 主线程传工程内已保存 Recipe 的 Assets 路径，不打开窗口、不依赖 Selection。路径是配置入口，不是输出位置。随包对应例子为 [`ResourceBuildRecipeExample.cs`](../../../Samples~/ResourceStartup/Editor/ResourceBuildRecipeExample.cs)；这不是通用 CLI/batchmode 入口，也不代表独立 CI 已验证。

## 地址常量生成

输入是正式发布 Catalog，默认输出 `Assets/YLFrameworkData/Generated/ResourceReferences/YLResourceReferences.cs`，namespace `YLFramework.Resources`。仅生成 ProjectId/Addresses/Aliases/Groups/SubAssets 键，不是可变目录或加载器，不改变常量中的地址文本。

生成程序集 `YLFramework.Generated.ResourceNames` 不依赖框架/Unity/Editor/插件。预定义程序集自动引用，业务 asmdef 显式引用它及 YLFramework.Runtime；生成物归使用工程，不是 UPM 包输入。标识符按 Unicode 字母/数字分词、Invariant PascalCase，如 hero_icon→HeroIcon、http_url→HttpUrl，保留 UI/IO，数字开头按需加 `_`；转换后碰撞仍拒绝，不追加后缀。

引用目录专属 YLResourceReferences.cs、YLFramework.Generated.ResourceNames.asmdef 和对应两份 meta，不混入 Module、SO 或未知文件。碰撞/类型校验先于写入，未知/篡改文件与孤立 meta 不认领；幂等更新保留 GUID，失败保留原输出。发布成功、生成成功和 Unity 消费者编译成功是三种结果。

任何位置存在同名程序集都会阻止在另一目录写第二份定义，错误指出原位置，不自动移动/替换/认领/删。要移动确为自有的输出：先备份并核对源码/asmdef/meta，再用 Unity Project 视图带原 meta 一起移动到未占用独立叶目录而非复制；避开不适用的祖先 asmdef/asmref，保留程序集名/GUID，更新显式配置，实际导入和编译消费者后再生成。数据根与目录 meta 仍归工程，不随输出自动删除。

## 实现入口

- [`ResourceProject.cs`](../../../Editor/Systems/Resource/Data/ResourceProject.cs) / [`ResourceProjectPaths.cs`](../../../Editor/Systems/Resource/Data/ResourceProjectPaths.cs)：作者定义、来源与归属验证。
- [`Workbench/`](../../../Editor/Systems/Resource/Workbench/)、[`ResourceEditorOperations.cs`](../../../Editor/Systems/Resource/ResourceEditorOperations.cs)：任务页与统一编辑/执行入口。
- [`ResourceAtlasGenerator.cs`](../../../Editor/Systems/Resource/Build/ResourceAtlasGenerator.cs) / [`ResourceAtlasConfiguration.cs`](../../../Editor/Systems/Resource/Data/ResourceAtlasConfiguration.cs)：自动图集生产与规则。
- [`Build/`](../../../Editor/Systems/Resource/Build/)、[`Publishing/`](../../../Editor/Systems/Resource/Publishing/)、[`ResourceBuildRecipe.cs`](../../../Editor/Systems/Resource/Data/ResourceBuildRecipe.cs)：构建、发布与配方。
- [`CodeGeneration/`](../../../Editor/Systems/Resource/CodeGeneration/)：地址常量生成及独占输出。
