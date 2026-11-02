using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <summary>8.2c 冻结的 Schema v1 持久目录。它不挂载 ResourceSystem，也不按类型字符串反射创建对象。</summary>
    public sealed class ResourceCatalogDocument
    {
        private readonly byte[] _bytes;
        /// <summary>本类型支持的固定持久化 Schema 版本。</summary>
        public int SchemaVersion => 1;
        /// <summary>目录所属的稳定资源项目标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>不含 BuildId 字段的规范化有效载荷摘要，与最终目录字节摘要不同。</summary>
        public string BuildId
        {
            get;
        }
        /// <summary>含 BuildId 的最终规范目录字节的 SHA-256；摘要本身不是签名授权。</summary>
        public string CatalogDigest
        {
            get;
        }
        /// <summary>发布内容声明的桌面目标平台，读取时须与调用方预期精确一致。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>发布内容声明的 Unity 兼容值，读取时按精确文本匹配。</summary>
        public string UnityCompatibility
        {
            get;
        }
        /// <summary>应用显式约定的内容兼容标识，不由资源系统推断兼容范围。</summary>
        public string AppContentCompatibilityId
        {
            get;
        }
        /// <summary>构建方记录的设置指纹；编解码器检查其格式，不重建构建设置。</summary>
        public string BuildSettingsFingerprint
        {
            get;
        }
        /// <summary>发布中用于加载 Unity AssetBundleManifest 的固定文件名。</summary>
        public string ManifestFile
        {
            get;
        }
        /// <summary>Bundle 文件身份、直接依赖与场景属性的只读描述。</summary>
        public IReadOnlyList<ResourceCatalogBundle> Bundles
        {
            get;
        }
        /// <summary>规范主资产、子资产与场景的只读描述，不把别名复制为新资产。</summary>
        public IReadOnlyList<ResourceCatalogAsset> Assets
        {
            get;
        }
        /// <summary>以规范 AssetId 引用成员的逻辑组，不表示物理 Bundle 分组。</summary>
        public IReadOnlyList<ResourceCatalogGroup> Groups
        {
            get;
        }
        /// <summary>Manifest 与配套文本文件的交付校验描述；不含目录自身及完成标记。</summary>
        public IReadOnlyList<ResourceCatalogFile> SupportFiles
        {
            get;
        }

        /// <summary>从编解码器已验证的数据建立只读描述，并保留规范字节的独立副本。</summary>
        internal ResourceCatalogDocument(JObject data, byte[] canonical)
        {
            _bytes = (byte[])canonical.Clone();
            ProjectId = (string)data["ProjectId"];
            BuildId = (string)data["BuildId"];
            CatalogDigest = ResourceCatalogCodec.Digest(_bytes);
            Platform = (string)data["Platform"];
            UnityCompatibility = (string)data["UnityCompatibility"];
            AppContentCompatibilityId = (string)data["AppContentCompatibilityId"];
            BuildSettingsFingerprint = (string)data["BuildSettingsFingerprint"];
            ManifestFile = (string)data["ManifestFile"];
            Bundles = ((JArray)data["Bundles"]).Select(x => new ResourceCatalogBundle((JObject)x)).ToList().AsReadOnly();
            Assets = ((JArray)data["Assets"]).Select(x => new ResourceCatalogAsset((JObject)x)).ToList().AsReadOnly();
            Groups = ((JArray)data["Groups"]).Select(x => new ResourceCatalogGroup((JObject)x)).ToList().AsReadOnly();
            SupportFiles = ((JArray)data["SupportFiles"]).Select(x => new ResourceCatalogFile((JObject)x)).ToList().AsReadOnly();
        }
        /// <summary>返回目录规范字节的副本，避免写出方修改文档内部身份。</summary>
        internal byte[] CopyBytes() => (byte[])_bytes.Clone();
    }

    /// <summary>发布文件的定位与交付字节校验描述；读取描述本身不会打开或验证文件。</summary>
    public class ResourceCatalogFile
    {
        /// <summary>发布目录内的单层文件名，不接受路径分隔符或父目录跳转。</summary>
        public string RelativeFile
        {
            get;
        }
        /// <summary>交付文件预期字节数；加密内容对应密文交付文件，不是解密后长度。</summary>
        public long Length
        {
            get;
        }
        /// <summary>交付文件预期 SHA-256，小写十六进制；只有与明确选择的目录绑定才有校验意义。</summary>
        public string Sha256
        {
            get;
        }
        /// <summary>复制已通过 Schema 校验的文件名、长度与摘要。</summary>
        internal ResourceCatalogFile(JObject data)
        {
            RelativeFile = (string)data["RelativeFile"];
            Length = (long)data["Length"];
            Sha256 = (string)data["Sha256"];
        }
    }

    /// <summary>一个物理 Bundle 的目录描述，同时携带规范逻辑身份与直接依赖。</summary>
    public sealed class ResourceCatalogBundle : ResourceCatalogFile
    {
        /// <summary>项目命名空间内的 Bundle 逻辑标识，也是依赖边引用的键。</summary>
        public string LogicalId
        {
            get;
        }
        /// <summary>Unity 原生 Bundle 名；v1 要求与 LogicalId 相同，不等于交付文件名。</summary>
        public string NativeName
        {
            get;
        }
        /// <summary>Unity 构建记录的 Bundle Hash128，不替代交付文件的 SHA-256。</summary>
        public string UnityHash
        {
            get;
        }
        /// <summary>是否为场景 Bundle；普通资产来源不会把它作为资产加载。</summary>
        public bool IsScene
        {
            get;
        }
        /// <summary>以 LogicalId 表示的直接 Bundle 依赖，不是传递闭包。</summary>
        public IReadOnlyList<string> Dependencies
        {
            get;
        }
        /// <summary>复制已经校验的 Bundle 描述与直接依赖列表。</summary>
        internal ResourceCatalogBundle(JObject data) : base(data)
        {
            LogicalId = (string)data["LogicalId"];
            NativeName = (string)data["NativeName"];
            UnityHash = (string)data["UnityHash"];
            IsScene = (bool)data["IsScene"];
            Dependencies = ((JArray)data["Dependencies"]).Select(x => (string)x).ToList().AsReadOnly();
        }
    }

    /// <summary>GUID/localId 是 canonical 身份；SubAssetKey 是 invariant 十进制 localId。类型由 FullName+程序集简单名描述，native 按唯一 name+编译类型定位。</summary>
    public sealed class ResourceCatalogAsset
    {
        /// <summary>由 GUID、下划线和 localId 固定十进制表示组成的规范资产标识。</summary>
        public string AssetId
        {
            get;
        }
        /// <summary>Unity 来源资产文件的 GUID；同一文件的主资产与子资产共用。</summary>
        public string Guid
        {
            get;
        }
        /// <summary>Unity 文件内对象的非零 localId，与 GUID 一起区分规范资产。</summary>
        public long LocalId
        {
            get;
        }
        /// <summary>项目内规范逻辑路径；子资产沿用主资产路径并另以 SubAssetKey 区分。</summary>
        public string Address
        {
            get;
        }
        /// <summary>规范路径的显式别名；子资产须与其主资产保留相同别名集合。</summary>
        public IReadOnlyList<string> Aliases
        {
            get;
        }
        /// <summary>子资产 localId 的固定十进制文本；主资产与场景为 null，不使用对象名称代替。</summary>
        public string SubAssetKey
        {
            get;
        }
        /// <summary>所属 Bundle 的 LogicalId，不是文件系统路径。</summary>
        public string BundleId
        {
            get;
        }
        /// <summary>交给 Unity 的原生加载路径；普通资产为小写，场景保留 Assets 路径约定。</summary>
        public string LoadName
        {
            get;
        }
        /// <summary>描述性类型全名；场景使用 Scene 标记，普通资产须匹配调用方提供的已编译类型。</summary>
        public string AssetType
        {
            get;
        }
        /// <summary>类型所在程序集的简单名称；场景为 null，不用于反射创建类型。</summary>
        public string AssetAssembly
        {
            get;
        }
        /// <summary>经 Schema 限定的 Asset、SubAsset 或 Scene 种类文本。</summary>
        public string Kind
        {
            get;
        }
        /// <summary>原生子资产选择所需的精确对象名；须与编译类型一起唯一匹配，主资产和场景为 null。</summary>
        public string SubAssetName
        {
            get;
        }
        /// <summary>复制已经校验的规范身份、逻辑地址与原生选择描述。</summary>
        internal ResourceCatalogAsset(JObject data)
        {
            AssetId = (string)data["AssetId"];
            Guid = (string)data["Guid"];
            LocalId = (long)data["LocalId"];
            Address = (string)data["Address"];
            SubAssetKey = (string)data["SubAssetKey"];
            BundleId = (string)data["BundleId"];
            Aliases = ((JArray)data["Aliases"]).Select(x => (string)x).ToList().AsReadOnly();
            LoadName = (string)data["LoadName"];
            AssetType = (string)data["AssetType"];
            Kind = (string)data["Kind"];
            AssetAssembly = (string)data["AssetAssembly"];
            SubAssetName = (string)data["SubAssetName"];
        }
    }

    /// <summary>逻辑组中的一个规范资产引用及其失败策略，不建立新的地址或资产身份。</summary>
    public sealed class ResourceCatalogGroupMember
    {
        /// <summary>目录中已登记的规范 AssetId，不接受别名路径代替。</summary>
        public string AssetId
        {
            get;
        }
        /// <summary>成员失败是否导致本批取得或预加载失败；可选成员失败仍保留逐项原因。</summary>
        public bool Required
        {
            get;
        }
        /// <summary>从已验证的持久目录复制成员身份与必需标记。</summary>
        internal ResourceCatalogGroupMember(JObject data)
        {
            AssetId = (string)data["AssetId"];
            Required = (bool)data["Required"];
        }
        /// <summary>为内部目录装配保存显式成员身份与必需标记，不自行解析资产。</summary>
        internal ResourceCatalogGroupMember(string assetId, bool required)
        {
            AssetId = assetId;
            Required = required;
        }
    }

    /// <summary>以规范资产标识组织的逻辑消费集合；可以跨 Bundle，不自行加载成员。</summary>
    public sealed class ResourceCatalogGroup
    {
        /// <summary>项目内的组标识；持久目录拒绝仅大小写不同的组，消费时按精确文本查找。</summary>
        public string Id
        {
            get;
        }
        /// <summary>只读成员集合，成员通过 AssetId 而非地址别名关联规范资产。</summary>
        public IReadOnlyList<ResourceCatalogGroupMember> Members
        {
            get;
        }
        /// <summary>从已校验的持久目录复制组与成员列表。</summary>
        internal ResourceCatalogGroup(JObject data)
        {
            Id = (string)data["Id"];
            Members = ((JArray)data["Members"]).Select(x => new ResourceCatalogGroupMember((JObject)x)).ToList().AsReadOnly();
        }
        /// <summary>为内部目录装配复制显式成员集合，不执行加载或跨条目校验。</summary>
        internal ResourceCatalogGroup(string id, IEnumerable<ResourceCatalogGroupMember> members)
        {
            Id = id;
            Members = ResourceCatalog.Copy(members);
        }
    }
}
