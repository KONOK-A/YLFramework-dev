using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>旧版自动图集的迁移输入，保留规则与产物归属；不用于生成图集或登记 Addressables 条目。</summary>
    public sealed class ResourceAtlasConfiguration : ScriptableObject
    {
        /// <summary>旧版自动图集规则，供迁移时识别原有图集的来源与设置。</summary>
        public List<ResourceAtlasRule> rules = new();

        /// <summary>旧版所属 ResourceProject 的资产 GUID。</summary>
        [SerializeField, HideInInspector] internal string projectGuid;
        /// <summary>旧版登记的生成父目录 GUID，不表示整个父目录归该配置所有。</summary>
        [SerializeField, HideInInspector] internal string generationParentGuid;
        /// <summary>旧版配置专属输出子目录的 GUID。</summary>
        [SerializeField, HideInInspector] internal string directoryGuid;
        /// <summary>旧版登记时的配置 GUID，供迁移核对资产身份。</summary>
        [SerializeField, HideInInspector] internal string configurationGuid;
        /// <summary>旧版登记时的配置 .meta 字节摘要。</summary>
        [SerializeField, HideInInspector] internal string configurationMetaHash;
        /// <summary>旧版登记时的输出子目录 .meta 字节摘要。</summary>
        [SerializeField, HideInInspector] internal string directoryMetaHash;
        /// <summary>旧版产物的身份和摘要记录，含当时已失效但未删除的输出。</summary>
        [SerializeField, HideInInspector] internal List<Output> outputs = new();

        /// <summary>单个旧版图集的归属记录，供迁移时核对产物，不授予覆盖或删除权限。</summary>
        [Serializable]
        internal sealed class Output
        {
            /// <summary>规则 Id 与可选来源文件夹 GUID 组成的稳定输出键。</summary>
            public string key;
            /// <summary>专属目录内的图集文件名。</summary>
            public string fileName;
            /// <summary>图集资产登记的 Unity GUID。</summary>
            public string guid;
            /// <summary>最近生成时规则值、平台及来源文件和 meta 的输入摘要。</summary>
            public string inputHash;
            /// <summary>最近受控生成的图集资产字节摘要。</summary>
            public string assetHash;
            /// <summary>最近受控生成的图集 meta 字节摘要。</summary>
            public string metaHash;
        }
    }

    /// <summary>旧版自动图集规则的分组方式。</summary>
    public enum ResourceAtlasGrouping
    {
        /// <summary>将规则收集到的全部 Sprite 纹理放入一个图集。</summary>
        Together,
        /// <summary>按纹理的直接所属文件夹分别生成图集，以文件夹 GUID 保持输出身份。</summary>
        PerFolder
    }

    /// <summary>供迁移参考的旧版自动图集规则；Id 标识旧规则，与显示名或地址无关。</summary>
    [Serializable]
    public sealed class ResourceAtlasRule
    {
        /// <summary>旧版稳定规则标识，用于关联旧输出。</summary>
        [Tooltip("旧版规则 Id，用于识别迁移输入中的旧图集输出。")]
        public string id = "ui";
        /// <summary>旧版 Project 中的目标组标识，不是 Addressables Group。</summary>
        [Tooltip("旧版 ResourceGroup.Id，表示图集原先所属的组；不会登记 Addressables 条目。")]
        public string groupId;
        /// <summary>合为一个图集或按直接所属文件夹展开。</summary>
        public ResourceAtlasGrouping grouping;
        /// <summary>合图的旧逻辑地址，或按文件夹展开时的旧地址前缀。</summary>
        [Tooltip("旧版地址规则：Together 为完整地址；PerFolder 追加来源目录的固定地址前缀与目录内相对文件夹路径。")]
        public string address = "atlases/ui";
        /// <summary>旧版规则引用的文件夹或已按 Sprite 导入的纹理。</summary>
        [Tooltip("旧版规则的来源文件夹或已按 Sprite 导入的 Texture2D，供迁移参考；此规则不会收集或修改来源。")]
        public UnityEngine.Object[] sources = Array.Empty<UnityEngine.Object>();
        /// <summary>是否允许图集打包时旋转 Sprite。</summary>
        public bool allowRotation;
        /// <summary>是否按 Sprite 轮廓紧密打包。</summary>
        public bool tightPacking;
        /// <summary>Sprite 间距像素数，仅允许 2、4、8。</summary>
        [Tooltip("旧版图集间距（像素）：2、4、8。")]
        public int padding = 4;
        /// <summary>单页最大纹理尺寸，为 32 到 8192 的二次幂；多页由 Unity 处理。</summary>
        [Tooltip("旧版图集单页纹理尺寸上限：32 到 8192 的二次幂。")]
        public int maxTextureSize = 2048;
        /// <summary>是否允许 CPU 读取生成的图集纹理。</summary>
        public bool readable;
        /// <summary>是否为图集纹理生成 mipmap。</summary>
        public bool mipMaps;
        /// <summary>是否以 sRGB 颜色纹理方式采样图集。</summary>
        public bool srgb = true;
        /// <summary>生成图集纹理的过滤方式。</summary>
        public FilterMode filterMode = FilterMode.Bilinear;
        /// <summary>默认平台的图集纹理压缩策略，独立于 Bundle 压缩。</summary>
        public UnityEditor.TextureImporterCompression compression = UnityEditor.TextureImporterCompression.Uncompressed;
    }
}
