using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>专属一个 ResourceProject 的自动图集规则及生成归属；仅由显式登记入口创建。</summary>
    public sealed class ResourceAtlasConfiguration : ScriptableObject
    {
        /// <summary>自动图集规则；删除规则仅使旧输出失效，不自动删除其文件。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Rules")]
        public List<ResourceAtlasRule> rules = new();

        /// <summary>唯一所属 ResourceProject 的资产 GUID。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ProjectGuid")]
        [SerializeField, HideInInspector] internal string projectGuid;
        // 生成范围归属独立于来源目录集合；父目录由显式登记入口指定。
        /// <summary>显式选定的生成父目录 GUID，不代表接管整个父目录。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("GenerationParentGuid")]
        [SerializeField, HideInInspector] internal string generationParentGuid;
        /// <summary>本配置专属输出子目录的 GUID。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("DirectoryGuid")]
        [SerializeField, HideInInspector] internal string directoryGuid;
        /// <summary>配置自身登记时的 GUID，用于拒绝身份替换。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ConfigurationGuid")]
        [SerializeField, HideInInspector] internal string configurationGuid;
        /// <summary>登记的配置 meta 字节摘要；规则内容可编辑，meta 不可被静默接管。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ConfigurationMetaHash")]
        [SerializeField, HideInInspector] internal string configurationMetaHash;
        /// <summary>登记的输出子目录 meta 字节摘要。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("DirectoryMetaHash")]
        [SerializeField, HideInInspector] internal string directoryMetaHash;
        /// <summary>受管输出的身份及最近受控字节记录，包含尚未显式删除的失效输出。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Outputs")]
        [SerializeField, HideInInspector] internal List<Output> outputs = new();

        /// <summary>单个受管图集的归属记录；文件或 meta 偏离记录时拒绝覆盖。</summary>
        [Serializable]
        internal sealed class Output
        {
            /// <summary>规则 Id 与可选来源文件夹 GUID 组成的稳定输出键。</summary>
            [global::UnityEngine.Serialization.FormerlySerializedAs("Key")]
            public string key;
            /// <summary>专属目录内的图集文件名。</summary>
            [global::UnityEngine.Serialization.FormerlySerializedAs("FileName")]
            public string fileName;
            /// <summary>图集资产登记的 Unity GUID。</summary>
            [global::UnityEngine.Serialization.FormerlySerializedAs("Guid")]
            public string guid;
            /// <summary>最近生成时规则值、平台及来源文件和 meta 的输入摘要。</summary>
            [global::UnityEngine.Serialization.FormerlySerializedAs("InputHash")]
            public string inputHash;
            /// <summary>最近受控生成的图集资产字节摘要。</summary>
            [global::UnityEngine.Serialization.FormerlySerializedAs("AssetHash")]
            public string assetHash;
            /// <summary>最近受控生成的图集 meta 字节摘要。</summary>
            [global::UnityEngine.Serialization.FormerlySerializedAs("MetaHash")]
            public string metaHash;
        }
    }

    /// <summary>一条自动图集规则展开为输出的分组方式。</summary>
    public enum ResourceAtlasGrouping
    {
        /// <summary>将规则收集到的全部 Sprite 纹理放入一个图集。</summary>
        Together,
        /// <summary>按纹理的直接所属文件夹分别生成图集，以文件夹 GUID 保持输出身份。</summary>
        PerFolder
    }

    /// <summary>Id 是稳定技术身份。移动源文件夹不改变其 GUID；修改 Id 则视为新规则。</summary>
    [Serializable]
    public sealed class ResourceAtlasRule
    {
        /// <summary>稳定规则标识；更名产生新输出，旧输出保留为失效项。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Id")]
        [Tooltip("稳定规则 Id，生成后不要为了改显示/地址而重命名；重命名会产生新输出并保留旧输出为 Obsolete。")]
        public string id = "ui";
        /// <summary>有效输出直接接入的唯一目标 Group 标识。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("GroupId")]
        [Tooltip("目标 ResourceGroup.Id；输出直接成为其正式主资产，不用手动再加入选择。")]
        public string groupId;
        /// <summary>合为一个图集或按直接所属文件夹展开。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Grouping")]
        public ResourceAtlasGrouping grouping;
        /// <summary>合图逻辑地址，或按文件夹展开时的地址前缀。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Address")]
        [Tooltip("Together 的完整逻辑地址；PerFolder 在其后追加来源目录的固定地址前缀与目录内相对文件夹路径。")]
        public string address = "atlases/ui";
        /// <summary>来源目录内的文件夹或已按 Sprite 导入的纹理；不修改源 Importer。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Sources")]
        [Tooltip("已登记来源目录内的文件夹或 Sprite 类型 Texture2D；可跨多个来源，目录递归收集已导入的 Sprite，不修改 Importer。不能包含生成范围。")]
        public UnityEngine.Object[] sources = Array.Empty<UnityEngine.Object>();
        /// <summary>是否允许图集打包时旋转 Sprite。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("AllowRotation")]
        public bool allowRotation;
        /// <summary>是否按 Sprite 轮廓紧密打包。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("TightPacking")]
        public bool tightPacking;
        /// <summary>Sprite 间距像素数，仅允许 2、4、8。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Padding")]
        [Tooltip("合法值为 2、4、8。")]
        public int padding = 4;
        /// <summary>单页最大纹理尺寸，为 32 到 8192 的二次幂；多页由 Unity 处理。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("MaxTextureSize")]
        [Tooltip("32..8192 的二次幂；由 Unity 处理多页，不隐式拆分或更换图集身份。")]
        public int maxTextureSize = 2048;
        /// <summary>是否允许 CPU 读取生成的图集纹理。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Readable")]
        public bool readable;
        /// <summary>是否为图集纹理生成 mipmap。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("MipMaps")]
        public bool mipMaps;
        /// <summary>是否以 sRGB 颜色纹理方式采样图集。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("SRgb")]
        public bool srgb = true;
        /// <summary>生成图集纹理的过滤方式。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("FilterMode")]
        public FilterMode filterMode = FilterMode.Bilinear;
        /// <summary>默认平台的图集纹理压缩策略，独立于 Bundle 压缩。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Compression")]
        public UnityEditor.TextureImporterCompression compression = UnityEditor.TextureImporterCompression.Uncompressed;
    }
}
