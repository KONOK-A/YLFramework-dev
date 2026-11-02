using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>自有资源项目。SO GUID 表示配置归属，ProjectId 表示稳定技术身份；不读取旧 Importer 标记。</summary>
    [CreateAssetMenu(menuName = "YLFramework/Resource/Project", fileName = "ResourceProject")]
    public sealed class ResourceProject : ScriptableObject
    {
        /// <summary>运行时地址及输出归属使用的稳定项目标识；修改不会迁移已有缓存或发布。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ProjectId")]
        public string projectId;
        /// <summary>仅用于编辑器展示的项目名称。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("DisplayName")]
        public string displayName;
        /// <summary>允许选择资源及其非代码依赖的来源范围；登记目录本身不选择构建内容。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("SourceDirectories")]
        [Tooltip("允许选择源资源的文件夹。登记目录不会自动选择其中的资源；由 Group 中的资源条目决定构建内容。")]
        public List<ResourceSourceDirectory> sourceDirectories = new();

        /// <summary>Bundle 压缩策略，不关闭严格构建和完整性验证。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Compression")]
        [Tooltip("仅选择Bundle压缩方式；StrictMode及身份/完整性验证始终启用。Lz4适合日常加载，Lzma侧重交付体积，Uncompressed用于明确测量或调试。")]
        public ResourceCompression compression;
        /// <summary>Bundle 与二进制根 Manifest 的交付编码；文本 manifest 旁车保持原字节。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ContentEncryption")]
        [Tooltip("None 保留原始交付；加密覆盖全部 Bundle 与二进制 root manifest，仅支持 Windows x64 Editor/Mono。密钥不保存于工程。")]
        public ResourceContentEncryption contentEncryption;
        /// <summary>非秘密的内容密钥身份；轮换密钥时必须同时更换此标识。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ContentKeyId")]
        [Tooltip("非秘密、区分大小写的稳定内容 KeyId，1..64 个 ASCII 字母/数字/._-。轮换密钥必须使用新 KeyId。")]
        public string contentKeyId;
        /// <summary>显式登记的自有自动图集配置，其有效输出直接加入规则指定的 Group。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("AutomaticAtlases")]
        [Tooltip("通过 Project Inspector 显式登记的新自有图集范围；规则输出直接进入目标 Group，不修改源 Importer。")]
        public ResourceAtlasConfiguration automaticAtlases;
        /// <summary>登记时的图集配置 GUID；可见引用断开后仍保留身份保护。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("AutomaticAtlasConfigurationGuid")]
        [SerializeField, HideInInspector] internal string automaticAtlasConfigurationGuid;
        /// <summary>明确选择主资产并声明初始分包意图的分组。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Groups")]
        public List<ResourceGroup> groups = new();
    }

    /// <summary>以文件夹 GUID 登记来源范围和默认地址前缀，不授予该目录的生成或清理权限。</summary>
    [Serializable]
    public sealed class ResourceSourceDirectory
    {
        /// <summary>来源文件夹的 Unity GUID，路径随文件夹移动重新解析。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Guid")]
        public string guid;
        /// <summary>拼接目录内相对路径的固定逻辑前缀；留空时直接使用相对路径。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("AddressPrefix")]
        [Tooltip("默认加载地址的固定前缀。留空使用目录内相对路径；修改会影响下次构建，不会改写历史发布或自动增加别名。")]
        public string addressPrefix = "";
    }

    /// <summary>主资产的初始分包意图，最终布局仍受场景隔离和依赖图约束。</summary>
    public enum ResourcePacking
    {
        /// <summary>同组主资产合包，场景与普通资产分别存放。</summary>
        Together,
        /// <summary>每个主资产独立起包，依赖仍可共享或随环归并。</summary>
        PerAsset
    }
    /// <summary>允许用于资源 Bundle 的压缩方式。</summary>
    public enum ResourceCompression
    {
        /// <summary>使用分块 LZ4 压缩。</summary>
        Lz4,
        /// <summary>使用 Unity 默认的 LZMA 压缩。</summary>
        Lzma,
        /// <summary>不压缩 Bundle。</summary>
        Uncompressed
    }

    /// <summary>Group 同时定义资源选择和打包意图；最终 Bundle 可因 Scene 隔离、共享依赖或环归并而变化。</summary>
    [Serializable]
    public sealed class ResourceGroup
    {
        /// <summary>组的稳定技术标识，也是 Catalog 的 GroupId。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Id")]
        public string id;
        /// <summary>本组主资产的初始分包方式。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Packing")]
        public ResourcePacking packing;
        /// <summary>文件或递归文件夹的显式选择；不包含规则自动接入的图集输出。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Selections")]
        public List<ResourceSelection> selections = new();
    }

    /// <summary>显式文件/文件夹 GUID 选择。文件可覆写地址；文件夹递归选择，Extensions 为空表示所有可打包资产。</summary>
    [Serializable]
    public sealed class ResourceSelection
    {
        /// <summary>选中文件或文件夹的 Unity GUID。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Guid")]
        public string guid;
        /// <summary>文件主资产的逻辑地址覆写；为空使用默认地址，文件夹选择不能覆写。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Address")]
        public string address;
        /// <summary>同一文件的额外地址，子资产沿用这些别名并附加各自的子资产键。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Aliases")]
        [Tooltip("同一 canonical 资产的额外逻辑地址；仅文件选择支持，子资产继承这些 alias。")]
        public string[] aliases = Array.Empty<string>();
        /// <summary>按带点扩展名筛选所选文件；为空不限制扩展名，比较不区分大小写。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Extensions")]
        public string[] extensions = Array.Empty<string>();
    }
}
