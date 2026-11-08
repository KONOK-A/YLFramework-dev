# 资源交付与更新

> [资源管理](../Resource.md) · [资源制作与构建](Authoring.md) · [安装](../../Installation.md) · [文档首页](../../README.md)

本页用于把已发布 Resource 内容交付给应用，包括 StreamingAssets 内置、私有 ArtifactStore、远端更新和可选内容加密。框架 UPM 安装与资源内容发布是两件事；安装不会部署内容、初始化服务或覆盖使用工程数据。

## 选择交付方式

| 需求 | 配置与结果 |
|---|---|
| 只消费确切本地/内置发布 | 每项目提供完整 ResourceLocalSnapshot，不传 cacheRoot，无持久 Store |
| 保留 active/LKG、准备与激活本地更新 | 明确应用私有绝对 cacheRoot，组成完整 ResourceProjectMount 闭包 |
| 远端更新 | 在上述 Store 配置中再显式提供可信 ResourceRemoteSource，不从远端导入信任根 |
| 加密 Bundle | Project 选择编码与非秘密 KeyId，应用注入 IResourceContentKeyProvider，不改变租约/更新责任图 |

普通本地 Bundle、远端和内容加密的后端范围不同，实施前核对[平台与后端限制](#平台与后端限制)，不能从一种路径可用推断其他路径已开放。

## 内置部署

### 预检与部署

先经 Builder/Publisher 发布确切版本，再用 Editor 专用 ResourceBuiltInDeployer。仅在稳定、非 Play/编译/导入中的 Unity 主线程调用。Builder/Publisher 不隐式写 StreamingAssets；部署不初始化 Resource/Core/Audio，不改 active/LKG 或强卸现有资源。

以下完整 Editor 类型按明确 digest 读取版本并预检；调用方核对计划后再显式 Deploy：

```csharp
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

public static class ApplicationBuiltInDelivery
{
    public static ResourceBuiltInPlan Inspect(ResourceProject project,
        string catalogDigest, string appId)
    {
        var release = ResourcePublisher.ReadVersion(project,
            EditorUserBuildSettings.activeBuildTarget, appId, catalogDigest);
        var plan = ResourceBuiltInDeployer.Preflight(release);
        Debug.Log(plan.ToString());
        return plan;
    }

    public static ResourceBuiltInDeployment Deploy(ResourceBuiltInPlan plan)
        => ResourceBuiltInDeployer.Deploy(plan);
}
```

Preflight 也接受经严格 ResourceLocalSnapshot.Open 打开的完整发布，不猜最新、不建目录/锁或恢复写入。检查 Source.Document、ProjectGuid、DirectoryPath、Files、PayloadBytes、WorkspacePath、IsRepeat；逻辑复制量不是磁盘峰值。目标为空也不代表可认领，归属冲突须先查明。

Deploy(plan,cancellation) 会重新预检，确认/预览不是免检凭证。结果含 Snapshot、RelativeDirectory、CopiedBytes、IsRepeat，身份以 Snapshot.Document 为准。将 ProjectId/Platform/AppContentCompatibilityId/CatalogDigest 记录到应用配置，不序列化开发机发布绝对路径。多必需项目逐项明确部署，再组成完整 mounts；Catalog v1 没有跨 Project 依赖或另一全局闭包权威。

### 运行时定位

```csharp
using System;
using System.Collections.Generic;
using YLFramework.Resource;

public static class ApplicationBuiltInStartup
{
    public static ResourceStartup Configure(string projectId, string digest,
        string platform, string appId, IEnumerable<Type> compiledTypes = null,
        string privateCacheRoot = null)
    {
        var snapshot = ResourceLocalSnapshot.OpenBuiltIn(projectId, digest, platform, appId);
        var mount = new ResourceProjectMount(projectId, platform, appId,
            snapshot, assetTypes: compiledTypes);
        var configuration = ResourceConfiguration.Create(ResourceLoadMode.Bundles,
            new[] { mount }, cacheRoot: privateCacheRoot);
        return new ResourceStartup(configuration);
    }
}
```

随后按[初始化与关闭](../Resource.md#初始化与关闭)调用 StartAsync/TakeResult、取得租约，再 StopAsync。OpenBuiltIn 只读明确身份，默认根为 `Application.streamingAssetsPath/YLFrameworkResources`，可显式传 resourcesRoot；不扫描最新、不复制或创建目录。

配置 Store 时有效 active/LKG 优先，没有有效持久版本才用明确内置，完整项目闭包整组选代、不拼代。仅回退消费内置不会因此创建持久缓存。Store 重开快照连同内置外壳一起复验，不只检查内部文件。

### 布局与元数据

部署到 `Assets/StreamingAssets/YLFrameworkResources/<ProjectId>/<Platform>/<CatalogDigest>/payload~/`。根/Project/Platform 各有 `ylr-owner.json~`，版本外壳含 `ylr-deployment.json~`，正式 Catalog/Complete/资源字节在 payload~ 中保持原样。

Unity 不导入 `~` 后缀目录/控制文件；正常目录 meta 只允许紧邻明确 owned 的根/Project/Platform/digest，且为合法 folder meta。payload~ 不生成目录或内部文件 meta，外壳 receipt 与 Complete 的 ProjectGuid/身份交叉验证。payload 额外 meta、孤立 meta、未知文件/目录、损坏归属、symlink/junction 都拒绝，不能靠 ignore、删除生成文件或关刷新绕过。

OpenBuiltIn 会验证外壳再定位 payload~，普通工程外导出布局仍用严格 Open；不能把 payload~ 直接传普通 Open 绕过外壳。部署只新建或使用已验证的 owned YLFrameworkResources，不认领已有空/未知根、不改父目录已有 meta、不碰其他 StreamingAssets 文件。调用方先准备合法 StreamingAssets 父目录，Windows 长路径限制也影响 stage 与最终包。

### 中断恢复

`AssetBundles/YLBI_<token>/` 是本操作的外部 workspace，仍校验完整 GUID/平台/工程根，短 token 碰撞只能拒绝。stage/锁/journal 均不进入 StreamingAssets、Player 或正式 payload。journal 登记闭合 staging 范围；复制、校验、目标复查后，将包含完整版本的最外层缺失节点搬入目标。相同完整版本幂等，不覆盖/自动删除其他版本。

提交前取消/失败保留旧版与可恢复 stage；提交后抛错不证明未安装。用 ResourceBuiltInDeployer.Recover(project,target)，或明确 projectId/platform/ProjectGuid 的重载确认。记录保留到真实导入、严格复验、结果构造均成功；若已装完整则返回确认结果，未装则只清 journal 登记 stage 并返回 null，随后重新预检。未知/损坏控制记录不自动删/补，Recover 不是任意目录清理许可。多 Project 逐项部署不是绝对断电原子事务，也没有全局内置版本垃圾回收。

### 在随包样例中操作

导入并生成 Resource Startup 样例后，退出 Play、保持所选确切 catalogDigest，先 `Preflight Selected Built-In Release`，再 `Deploy Selected Release To Built-In`。成功只将 useBuiltIn=true，不启动服务、不保存 Scene、不自动切 Bundles。显式选 Bundles 并保存 Scene 后运行；后续 Build 会选择新发布并重置 useBuiltIn=false，需重新明确预检/部署。中断用 `Recover Built-In Deployment`，再按结果复检。

Player 的 BeginBundles 或 Begin/Run 在 useBuiltIn=true 时只按完整身份 OpenBuiltIn，不读 snapshotDirectory。部署成功仍须检查实际 Player 包内文件、首次启动与持久重启，不能以 Editor 导入代替。

## 更新与激活

更新要求已配置 ArtifactStore。同一应用跨进程复用同一个独占私有 Store，不随机换 cacheRoot，也不收养未知目录。

| 阶段 | 作用与边界 |
|---|---|
| CheckUpdatesAsync(完整本地快照闭包) / CheckRemoteUpdatesAsync | 只读检查并产生 UpdatePlan，不写 active |
| PrepareUpdateAsync(plan) | 下载/复制并验证完整 To，产生 PreparedUpdate，不等于激活 |
| 业务 neutral 边界 | 停止新借出，归还自己资产、实例、Scene、Audio 尾轨/缓存等责任 |
| ActivatePreparedUpdateAsync(prepared) | 复验 from/To 与责任闸门后提交整组版本；Busy 保留原版，StaleBase 需重新 Check |
| RollbackAsync() | 显式选择 LKG，走相同闸门，不是任意历史/prepared 激活 |
| CleanupVersionsAsync() | 显式清理当前确认可删的 owned 内容，复验保护，不能由旧查询报告授权 |

以下是业务协程的成功路径片段。projectId/packagedDigest/App、公钥来源和可选 applicationDecryptionOptions 均由应用提供，startup 应由 owner 字段持有：

```csharp
using UnityEngine;
using YLFramework.Resource;

var builtIn = ResourceLocalSnapshot.OpenBuiltIn(projectId, packagedDigest,
    "StandaloneWindows64", appContentCompatibilityId);
var mount = new ResourceProjectMount(projectId, "StandaloneWindows64",
    appContentCompatibilityId, builtIn, remote: trustedRemoteSource);
var configuration = ResourceConfiguration.Create(ResourceLoadMode.Bundles,
    new[] { mount }, System.IO.Path.Combine(Application.persistentDataPath, "ResourceStore"),
    decryption: applicationDecryptionOptions);
startup = new ResourceStartup(configuration);
using (var start = startup.StartAsync())
{
    yield return start;
    start.TakeResult();
}
UpdatePlan plan;
using (var check = startup.Resources.CheckRemoteUpdatesAsync())
{
    yield return check;
    plan = check.TakeResult();
}
PreparedUpdate prepared;
using (var prepare = startup.Resources.PrepareUpdateAsync(plan))
{
    yield return prepare;
    prepared = prepare.TakeResult();
}
// 应用在此归还自己的 Lease、实例、Scene 和 Audio 责任。
using (var activate = startup.Resources.ActivatePreparedUpdateAsync(prepared))
{
    yield return activate;
    activate.TakeResult();
}
using (var stop = startup.StopAsync())
{
    yield return stop;
    stop.TakeResult();
}
```

TakeResult 抛错时也必须进入应用统一退出路径并保留 startup 重试 Stop，不靠执行到片段末尾保证关闭。完整 owner 见[随包最小接入](../Resource.md#最小接入)。Prepare 的取消不切 active，native 开始后的取消不能靠提前关输入抢占。

下次 Start 恢复 journal 后选 active/LKG 整组，不挑最新目录。持久 Prepare 可经 GetPreparedUpdates() 取回再显式激活；该 API 会恢复元数据并重建可操作对象，不是纯磁盘查询。离线激活仍需同 Project/平台/App 与受信公钥配置。本地更新走同一 Prepare/Activate 链。

### 远端信任与传输

应用显式配置 ResourceRemoteSource(endpoint,channel,trustedKeys,...)。端点须规范 HTTPS 目录、末尾 `/`，无凭据/query/fragment/转义路径；相对发布路径限制字符并保持同 origin。可信公钥从应用来，不从待验发布来。ResourceSigningKey 的 RSA 模数为 2048..8192 位，公钥标识唯一；Source 接受 1..16 个公钥，最低序号至少 1。

默认单文件上限 1GiB、整次发布 4GiB（含 Catalog/Complete），GET 总超时 60 秒、无数据 15 秒、最多 3 次尝试。可显式配置：超时 1..600、idle 不超总时长、attempts 1..5，文件/总量保持正数与大小关系。它不是无界下载器。

普通 Player 使用 Unity 默认 TLS 证书链/主机名校验，没有生产忽略证书开关。只有 Editor，或同时具备 UNITY_STANDALONE_WIN、ENABLE_MONO/ENABLE_IL2CPP、DEVELOPMENT_BUILD、YL_RESOURCE_ACCEPTANCE_TESTS 的隔离构建保留内部 loopback 精确证书 pin，不改系统信任。正常应用不加验收符号；回环 pin 不证明默认 CA 链正向联网可用。

partial 续传必须有实际 offset、强 ETag 和 If-Range。服务器 200 忽略 Range 或实体 ETag 变化时从零获取，Content-Range 失配拒绝后重头重试，弱 ETag 不续传。最终仍完整核对每文件 length/SHA 和内容认证，不盲追加半成品。HTTPS/SHA 成功不是解密成功，缺/错 key 不是网络重试理由。

### 签发与协议

ResourceReleaseSigner.Create 只读取完整发布，重新校验实际文件 length/digest 与 Envelope，再用应用持有 RSA 签发并自验，不部署资源或安装信任公钥。序号只检查正数，发布方负责历史递增；有效期须晚于当前且不超过 31 天。CreateFromEnvironment 使用工程外私有 PFX，路径/密码来自 YL_RESOURCE_SIGNING_PFX / YL_RESOURCE_SIGNING_PASSWORD，不存工程、Recipe、日志或 Runtime。Export 只新增允许绝对路径的文件，不覆盖；它检查信封结构而非重新验签，写入失败可留部分文件，重试前检查目标。

签名信封为规范 JSON SchemaVersion=1、Algorithm=`RSA-SHA256-PKCS1`，字段 SchemaVersion/Algorithm/KeyId/Payload/Signature。Payload 为规范 JSON 的 Base64，签名覆盖移除 Signature 后的规范信封，不是只签 CatalogDigest。信封上限 64KiB、Payload 上限 32KiB；缺/未知字段、非规范 Base64/JSON 均拒绝。签名 payload 固定 13 字段：

```text
ProjectId, Platform, UnityCompatibility, AppContentCompatibilityId,
Channel, ReleaseSequence, CatalogDigest, CatalogLength, BuildId,
CompleteLength, CompleteSha256, IssuedUnixSeconds, ExpiresUnixSeconds
```

当前无内容编码能力协商字段或独立客户端确认 bool。验签后仍核对项目/平台/完整 Unity/App/channel、序号、到期时间、Catalog/Complete 的确切 length/SHA/BuildId 与下载上限。签名认证确切发布，不证明它最新，也不防本地状态恶意回滚；公开错误中元数据传输、签名/摘要/序号拒绝通常包装为 UpdateFailed，须检查内层原因，不能把任意 UpdateFailed 当特定拒绝门禁通过。

### 控制格式与恢复

active/LKG 索引、prepared 和提交 journal 只读写控制格式 v2，ReleaseFloors/Releases 即使为空也保存为数组；Catalog v1、内容 Envelope v1、签名与归属/Complete 协议各自独立，不随控制版本改写。

仅接受当前 v2 控制版本；其他版本在恢复、候选清理或建写锁前 Unsupported，不当内容损坏而静默选 LKG/内置。全部持久 floor 无法确认且无可信 journal 时阻塞该 root，不写空历史。保留原文件，依据可信发布和已确认 channel/sequence 显式创建新应用自有 Store，不自动转换/认领/清原 root，其他独立 root 不受影响。

ReleaseFloor 按 Project/channel 保存最高**成功激活**序号/摘要，同序号异摘要拒绝；只读 Check、首次失败下载不确认新序号。显式 Rollback 和本地 unsigned 转换不降低已确认 signed 历史。首次安装、本地状态丢失/恶意回滚或合法旧发布冻结没有完整抗回滚保证。进程强停恢复也不等同断电保证。

## Store 只读查询

ResourceConfiguration.QueryArtifactsAsync(completed,maxEntries,cancellation) 或 ArtifactStore.QueryAsync 返回由调用方驱动的 IEnumerator，不创建协程宿主。MoveNext/Dispose 在 Unity 主线程；不需初始化 ResourceSystem，不 Mount/下载/Prepare/Recover/清理，不建 root、cache、staging 或锁。不要用 GetPreparedUpdates 替代纯查询。

以下完整静态协程可由现有 MonoBehaviour.StartCoroutine 驱动。cacheRoot 是应用明确私有绝对目录，mounts 闭包须与 Store owner 的 Project/Platform 一致：

```csharp
using System;
using System.Collections;
using System.Threading;
using UnityEngine;
using YLFramework.Resource;

public static class ArtifactQueryExample
{
    public static IEnumerator Inspect(string cacheRoot, CancellationToken cancellation = default)
    {
        var store = new ArtifactStore(cacheRoot, new[]
        {
            new ResourceProjectMount("my-project", "StandaloneWindows64", "app-v1")
        });
        var configuration = new ResourceConfiguration(store);
        ArtifactStoreReport report = null;
        var query = configuration.QueryArtifactsAsync(value => report = value,
            maxEntries: 10000, cancellation: cancellation);
        try
        {
            while (query.MoveNext()) yield return query.Current;
        }
        finally
        {
            (query as IDisposable)?.Dispose();
        }
        Debug.Log($"Store={report.Status}; bytes={report.ObservedBytes}; " +
            $"sizeComplete={report.IsSizeComplete}; candidates={report.CleanupCandidateBytes}");
        foreach (var issue in report.Issues) Debug.LogWarning(issue);
        foreach (var entry in report.Entries)
            Debug.Log($"{entry.ProjectId}/{entry.Platform} {entry.Kind} {entry.State} " +
                $"{entry.Path}; protection={entry.Protection}; candidate={entry.IsCleanupCandidate}");
    }
}
```

### 如何读报告

- Projects 是明确项目/平台/App 配置，不扫 persistentDataPath 或工程找其他 Store。Entries 含版本、staging、partial 文件组、控制元数据及受管层级未知项；PreparedUpdates 只复制身份和 from/to，不生成可激活对象。
- Protection 可组合 Active、LastKnownGood、PreparedFrom、PreparedTo、Mounted、Held、InFlight。完整镜像/Prepared 闭包复用正式校验，持有来自当前和仍排空旧会话，与清理共用来源；ProtectedVersions 也列磁盘项外的明确内置/持有路径，但不遍历它们。版本操作在途另报 InFlightOperations/Busy。
- CompleteRecord 只证明 owned Complete 身份及登记文件集合，不是 Bundle 全文 SHA、可挂载性、App 兼容、解密或签名新鲜度认证。必要控制/Complete 会查，默认不重读全部 Bundle；正式清理仍完整复验。允许回收的 owned remainder 可为 Incomplete，损坏/未知/外来归属/链接/读失败不成为候选。
- ObservedBytes 是观测到普通文件逻辑长度，含数据/控制文件；partial 的 .json/.part/.next 一组只算一次，版本/staging 父项不重复。IsSizeComplete=false 时仅已观测下界，不是完整 0；未知目录内部和链接目标不扫。CleanupCandidateBytes 只在 Ready 统计确认候选，不等磁盘实际可回收或 native memory。
- NotConfigured/Missing/RecoveryRequired/Invalid/Unreadable 分别为无 Store、root 缺失、待恢复、无效控制/归属、读失败，不能统称空目录。Ready 是一次上下文/扫描完成，仍查各项 State/Issues/IsSizeComplete；局部读失败保留条目，控制闭包不明则整体不给候选。
- 每段最多 32 步，默认总上限 10000，可选 1..100000，含必要复查；达上限 Incomplete，取消 Canceled。中止枚举器必须 Dispose，直接中止不产生报告；OS 单次 IO 不保证硬实时帧预算。
- 不锁整个扫描期。进程内 Store 事务、会话/持有集合变化为 Changed，在途操作/外部写锁为 Busy；观测路径属性/长度/mtime 变化也作废。所有非 Ready 撤销候选、CleanupCandidateBytes=null，结束操作后重查。这不是跨进程原子快照，也不防绕过合同伪造相同属性的外部篡改。

报告是脱离内部记录的只读副本，不加 pin 或持 Unity 对象，只描述 StartedUtc..CompletedUtc。报告生成后保护变化即可能过时；不得按 Path/旧 candidate 自删。实际删除只用已初始化服务 CleanupVersionsAsync，重查最新 owner、active/LKG、Prepared、在途和持有责任。

## 内容加密

内容加密是 Bundle 交付的可选编码，不是签名、密钥分发或另一加载器。Project.contentEncryption 默认 None，使用 LoadFromFile/LoadFromFileAsync；Simulation、Resources、直接图片不因此加密。None 不把加密配置加入 build settings 身份，但源码/保存配置改变仍可能正常使 input fingerprint 失效。

选择 Aes256GcmHkdfSegmentedV1，填写非秘密 contentKeyId（1..64 个 ASCII 字母/数字/`._-`），覆盖本 Project 内容包、共享依赖包和二进制 root manifest，文本 .manifest 旁车不加密。AES-256-GCM 固定 Windows CNG Microsoft Primitive Provider；随包官方 BouncyCastle 2.7.0 netstandard2.0 仅供 HKDF-SHA256，不软件 GCM 回退、不整包解密落盘、不整包内存 fallback、不复制系统 DLL。

### 配置构建与运行

Editor 方法内可明确保存并构建；project 已有且归属清楚、目标已激活，文件顶部引用 UnityEditor、YLFramework.Editor.Resource 和 YLFramework.Resource：

```csharp
project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
project.contentKeyId = "content-v1";
EditorUtility.SetDirty(project);
ResourceEditorOperations.SaveProject(project);
var keys = ResourceEditorOperations.ContentKeyFromEnvironment(
    project.projectId, project.contentKeyId, "YL_RESOURCE_CONTENT_KEY");
var build = ResourceBundleBuilder.BuildWithContentKey(
    project, BuildTarget.StandaloneWindows64, keys);
var release = ResourcePublisher.PublishWithContentKey(build, "app-v1", keys);
```

Editor 只接受明确进程 secret 名或调用方 provider；环境值严格为 Base64 的 32 字节。不在 SO/Inspector/EditorPrefs/指纹/日志保存 key，非秘密 KeyId/变量名不是 key。None 用 Build/Publish，无需 key。

应用实现 `IResourceContentKeyProvider.TryCopyKey(projectId,keyId,destination)`，将指定 key 复制到框架给出的确切 32 字节缓冲，不持有缓冲。provider 生命周期归应用，框架只持自己的副本/派生状态；应用回调只发生主线程，不在 native Stream/IO worker。异常文本不得含 key，框架保留因果但不串出敏感原文。

```csharp
// mounts/cacheRoot/appOwnedKeys 由应用提供；只消费本地/内置时 cacheRoot 可 null。
var config = ResourceConfiguration.Create(ResourceLoadMode.Bundles, mounts, cacheRoot,
    decryption: new ResourceDecryptionOptions(appOwnedKeys, 128L * 1024 * 1024));
var startup = new ResourceStartup(config, bindCore: true);
// 继续使用同一 StartAsync/TakeResult、Asset/Instance/Scene 租约及 StopAsync。
```

随包样例可在 Project.asset 选 None 或加密并显式保存，运行前设置非序列化 `example.ContentKeyProvider=applicationOwnedKeys`，Editor 构建按钮传同一 provider。开发期可填**进程环境变量名**并点 Inject Development Environment Key Provider，复用 32 字节校验，不在 Inspector 填 key。未注入时 Editor 构建可读 YL_RESOURCE_CONTENT_KEY，不等于 Runtime 自动有 key；Play/域重载后重新注入。Player 必须应用注入，不下载 key，也不依赖开发机环境或 EditorPrefs。

### 当前内容协议

raw workspace 留给 BuildPipeline/图集前置，独立 delivery 在 Catalog 封口前生成。Catalog v1 的 length/SHA 描述实际**交付字节**，报告分列 raw/delivery SHA、字节及编码成本，不把明文摘要误当可直接下载的密文身份。

Envelope v1 以 `YLRENC01` 开头，后跟 4 字节 little-endian JSON 头长度与规范 UTF-8 JSON（1..4096 字节）；头字段固定为 Algorithm/CiphertextSegmentSize/FileRole/KeyId/LogicalId/PlainLength/PlainSha256/Platform/ProjectId/UnityCompatibility/Version，Algorithm=`AES256-GCM-HKDF-SEGMENTED`、Version=1、CiphertextSegmentSize=65536。FileRole 为 Bundle 或 Manifest，Manifest 的 LogicalId=manifest。头部绑定 Project/平台/完整 Unity/角色/逻辑身份，并参与文件密钥派生；只 Inspect 格式/上下文不等于认证或受信发布。

分段采用 64KiB 密文段、16 字节 tag、首段含 40 字节流头，明文容量为 65520（首段另减 40）；流头为 1 字节长度 40、32 字节随机 salt、7 字节随机 nonce 前缀。HKDF-SHA256 使用应用 32 字节 key、该 salt，以及 `YL.Resource.BundleEnvelope/v1` 拼接完整外层前缀（magic/头长度/规范 JSON）作为 info，派生 32 字节文件 key。12 字节 nonce 由 7 字节前缀、4 字节 big-endian 段序号和 1 字节末段标志组成，不以文件名或时间戳代替随机材料。

segment count 有 32 位计数上限并检查长度/算术溢出，交付长度须与头部推导完全一致。新加密生成新随机材料；同身份/key 的候选只在本次完整认证成立时复用确切密文，同 KeyId 换材料不会静默重加密成成功，轮换须新 KeyId/配置分区。重复发布仍须当前 key 和完整验证，提交前失败/取消不切发布索引或破坏旧版本；提交后的故障按 Publisher 恢复确认。

### 输入、预算与认证

每共享 BundleRecord 仅一个输入：加密内容经认证、可 seek 的 Stream 送入 `AssetBundle.LoadFromStream/LoadFromStreamAsync`，None 仍走 `LoadFromFile/LoadFromFileAsync`。输入/CNG owner/预算保留到真实 native unload。SceneLease 归还不是 Scene 卸载，实例 destroy-pending、Audio 尾轨/缓存继续沿责任图保活。实例化、Ready 复用、Group/Scene/Audio 缓存的新借出均检查依赖闭包故障，首 fault 不强销毁已交付对象。

单输入固定数据缓冲为 196576B（64KiB 密文槽 + 两个明文槽），另有 32KiB native read buffer。默认数据预算 128MiB，不预分配整笔；新闭包原子预留，live 输入/在途预留计量，共享不双计。不足 ContentBufferUnavailable，释放自有责任后显式重试，不扩容/驱逐活跃输入。GetContentDiagnosticsSnapshot 与 GetDiagnosticsReport().Content 分列数据预算、native bridge 声明值、进程内本适配器 CNG provider/key/key-object 成本，不是密码模块或进程总内存上限。

同步取得承担完整 SHA/输入准备成本；异步纯 IO/SHA/认证可取消并在 worker，须等终态 TakeResult。native 开始后不能提前关输入抢占取消。初始化成功前只为当前选中 Project/KeyId 做一次轻量认证，同配置 join 不重复，不要求未选历史 key，不创建 native Bundle 代探针，全 None 无额外密码工作。

每次 Prepare（含重复）完整验证 To 的 SHA/内容，幂等复用不免复验；激活/回滚提交前完整认证，持稳定输入到提交结束。恢复、Mount、Check 和只读 Store 查询不取 key/解密；缺 key 不隐式重下载或切 LKG。签发复验实际交付与 Envelope，但内容密钥与发布 RSA 私钥/信任是不同责任。

## 密钥轮换与故障恢复

- 轮换创建新 KeyId/密文发布，Check/Prepare 后在业务 neutral 边界激活。保留必要旧 key 才能回滚旧密文 LKG；缺旧 key 明确失败，不改 active/LKG 或序号。兼容明文 LKG 无需内容 key，历史目录存在不是回滚许可。
- 远端 length/SHA/ETag/Range 必须描述同一份固定密文。网络完成后还要内容认证和 Prepare/激活，不因 key 错反复重下载或换版本。
- BuiltInDeployer 的 Preflight/Deploy/Recover 只复制/验证交付字节，无需内容 key，不改 Catalog/Complete/密文；Runtime 仍需 key。只读 Store 查询也不取 key，不因此取得删除权。
- ResourceEditorOperations.ExportRelease(release,destination) 的目标是工程外已有本地父目录下不存在且名等于 CatalogDigest 的子目录，只交付 Catalog/Complete/登记文件，不含 raw cache/key。
- ContentKeyUnavailable 应补应用当前 Project/KeyId 后显式重试；ContentAuthenticationFailed/ContentFormatUnsupported 拒绝故障输入闭包新取得，不回退明文/Simulation；ContentBufferUnavailable 先查责任图，不全局强清。

**Scene 风险：**Unity native 接手 Scene 后，延迟 Stream 读取失败可能导致 native 崩溃，不能保证所有损坏输入都转受控资源错误。这不为合法输入崩溃、竞争、提前释放或取消/退出/预算缺陷免责。native 前完整密文 length/SHA、首末认证、读取逐段认证，以及发布/Prepare/激活/回滚完整认证都必须保留，不用明文落盘或整包内存规避。

发布/部署中断分别使用 Publisher.Recover 与 BuiltInDeployer.Recover，不手改 journal/owner/floor 或删除未知目录。正常 Stop 后应核对 authority/Core 绑定、Lease/Bundle/实例/Scene、在途输入/预算/CNG 责任，不以全局卸载或终止用户 Editor 代替收尾。

## 平台与后端限制

以下为当前代码的接纳门禁与实现边界，不是历史测试回执或全平台支持承诺：

| 能力 | 当前边界 |
|---|---|
| Editor Simulation | 仅 Editor 工厂显式选择，不作为 Bundles 错误回退 |
| 普通本地 Bundle / 内置 | mount/Catalog 接受 StandaloneWindows64、StandaloneOSX、StandaloneLinux64，挂载匹配当前平台和完整 Unity 版本；构建还要求已激活/安装且主机 native 可读回，不表示三平台均已运行验证 |
| 远端更新 | WindowsEditor，或精确 Unity `2022.3.62f3c1` / `6000.3.27f1` 的 x64 Windows Player Mono/IL2CPP 候选；其他 Player、位数、版本 Unsupported |
| 内容加密 | Windows x64 CNG；Windows64 IL2CPP 额外硬查上述精确 Unity 版本。Editor/Mono 未同样按版本拦截不等于其他引擎版本已验证，无其他主机软件 GCM fallback |
| Windows64 IL2CPP 工具链 | 需要相应 Unity 模块、MSVC/Windows SDK。Low 裁剪/C++ Release 是定向验证配置，不是其他裁剪/新增反射/动态泛型已保全；框架不加全量 Preserve |
| Android / iOS / WebGL Bundle | 当前 mount/Catalog 不开放，Android/JAR/WebGL 提取未实现，不只是缺设备测试 |
| Resources / 直接图片 | 不受 Bundle 三桌面枚举约束，但仍受来源、文件系统、Unity/目标平台限制，不代表远端/加密可用 |

CNG 适配器按确切 System32 模块句柄绑定，LPWStr/x64 ABI，系统模块为进程期依赖，不是每 Bundle 的 provider/key owner；不随包分发系统 DLL。内容预算与 key-object 分别释放/诊断。IL2CPP 的 DriveInfo/GetDriveFormat 限制由 Store 的 Windows 可用空间 API 路径处理，保留配额与 64 位字节数，查询失败拒绝，不降级无限空间或跳检查。

不提供 archive、其他平台 Bundle 后端、native 多版本热共存或图集透明晚绑定。包解析、编译、构建、实际 Player 运行、图集像素、默认 TLS 链和音频听感分别验证，不能互相替代。

## 实现入口

- [`ResourceBuiltInDeployer.cs`](../../../Editor/Systems/Resource/Publishing/ResourceBuiltInDeployer.cs)、[`ResourceBuiltInLayout.cs`](../../../Runtime/Systems/Resource/Catalog/ResourceBuiltInLayout.cs)、[`ResourceLocalSnapshot.cs`](../../../Runtime/Systems/Resource/Catalog/ResourceLocalSnapshot.cs)：内置外壳、定位与恢复。
- [`Updates/`](../../../Runtime/Systems/Resource/Updates/)：Store、Prepare/激活/回滚、控制格式和只读查询。
- [`ResourceRemoteSource.cs`](../../../Runtime/Systems/Resource/Updates/ResourceRemoteSource.cs)、[`ResourceReleaseSignature.cs`](../../../Runtime/Systems/Resource/Updates/ResourceReleaseSignature.cs)、[`ResourceReleaseSigner.cs`](../../../Editor/Systems/Resource/Publishing/ResourceReleaseSigner.cs)：可信端点、验签与签发。
- [`Content/`](../../../Runtime/Systems/Resource/Content/)、[`ResourceDecryptionOptions.cs`](../../../Runtime/Systems/Resource/Content/ResourceDecryptionOptions.cs)：Envelope、认证 Stream、CNG 与数据预算。
- [`BouncyCastle 来源`](../../../Plugins/BouncyCastle/SOURCE.txt)与[主包许可证](../../../LICENSE.md)：随附二进制准确来源/SHA 与完整条款。
