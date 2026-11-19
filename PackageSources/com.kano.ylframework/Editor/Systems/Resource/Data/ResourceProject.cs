using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>旧版迁移输入。保留全部序列化字段，不再作为新版作者事实或构建入口。</summary>
    public sealed class ResourceProject : ScriptableObject
    {
        /// <summary>旧版地址及产物归属使用的稳定项目标识，供迁移核对。</summary>
        public string projectId;
        /// <summary>旧版项目的编辑器显示名。</summary>
        public string displayName;
        /// <summary>旧版资源及其非代码依赖的允许来源范围；实际选择另存于旧组。</summary>
        [Tooltip("旧版资源的来源目录，供迁移核对；不会创建 Addressables 条目。")]
        public List<ResourceSourceDirectory> sourceDirectories = new();

        /// <summary>旧版 Bundle 压缩设置，不设置 Addressables 组的压缩。</summary>
        [Tooltip("旧版 Bundle 压缩方式，仅供迁移参考；当前压缩由官方 Addressables 组配置决定。")]
        public ResourceCompression compression;
        /// <summary>旧版 Bundle 与二进制根 Manifest 的交付编码，不参与官方内容构建。</summary>
        [Tooltip("记录旧版交付是否加密，不为官方 Addressables 内容启用加密。")]
        public ResourceContentEncryption contentEncryption;
        /// <summary>旧版交付的非秘密内容密钥标识，不包含密钥。</summary>
        [Tooltip("旧版内容 KeyId，非秘密、区分大小写，仅用于识别旧交付数据。")]
        public string contentKeyId;
        /// <summary>旧版自动图集配置，记录来源、规则与产物归属。</summary>
        [Tooltip("旧版自动图集规则及生成归属，供迁移参考；不生成图集或登记 Addressables 条目。")]
        public ResourceAtlasConfiguration automaticAtlases;
        /// <summary>旧版登记时的图集配置 GUID，可见引用断开时仍可用于核对旧身份。</summary>
        [SerializeField, HideInInspector] internal string automaticAtlasConfigurationGuid;
        /// <summary>旧版主资产选择及分包意图的记录，不是 Addressables 组。</summary>
        public List<ResourceGroup> groups = new();
    }

    /// <summary>旧版来源范围及默认地址前缀的记录，不授予生成或清理权限。</summary>
    [Serializable]
    public sealed class ResourceSourceDirectory
    {
        /// <summary>旧版来源文件夹的 Unity GUID，供迁移解析当前路径。</summary>
        public string guid;
        /// <summary>旧版默认地址的固定前缀；留空表示使用目录内相对路径。</summary>
        [Tooltip("旧版默认地址的固定前缀；留空时旧版使用目录内相对路径，不设置当前 Addressables 地址。")]
        public string addressPrefix = "";
    }

    /// <summary>旧版主资产的初始分包意图，实际旧布局还受场景隔离和依赖图约束。</summary>
    public enum ResourcePacking
    {
        /// <summary>同组主资产合包，场景与普通资产分别存放。</summary>
        Together,
        /// <summary>每个主资产独立起包，依赖仍可共享或随环归并。</summary>
        PerAsset
    }
    /// <summary>旧版资源 Bundle 的压缩方式。</summary>
    public enum ResourceCompression
    {
        /// <summary>使用分块 LZ4 压缩。</summary>
        Lz4,
        /// <summary>使用 Unity 默认的 LZMA 压缩。</summary>
        Lzma,
        /// <summary>不压缩 Bundle。</summary>
        Uncompressed
    }

    /// <summary>旧版 Group 记录资源选择和打包意图，仅供迁移参考。</summary>
    [Serializable]
    public sealed class ResourceGroup
    {
        /// <summary>旧版组的稳定标识，与旧 Catalog 的 GroupId 一致。</summary>
        public string id;
        /// <summary>本组主资产的初始分包方式。</summary>
        public ResourcePacking packing;
        /// <summary>旧版显式选择的文件或递归文件夹，不包含由旧规则接入的图集输出。</summary>
        public List<ResourceSelection> selections = new();
    }

    /// <summary>旧版文件或文件夹的 GUID 选择，供迁移重建资源与旧地址的对应关系。</summary>
    [Serializable]
    public sealed class ResourceSelection
    {
        /// <summary>选中文件或文件夹的 Unity GUID。</summary>
        public string guid;
        /// <summary>文件主资产的逻辑地址覆写；为空使用默认地址，文件夹选择不能覆写。</summary>
        public string address;
        /// <summary>同一旧版文件的额外地址，子资产沿用这些别名并附加各自的子资产键。</summary>
        [Tooltip("旧版文件主资产的额外逻辑地址，子资产沿用；不会生成 Addressables 地址或标签。")]
        public string[] aliases = Array.Empty<string>();
        /// <summary>按带点扩展名筛选所选文件；为空不限制扩展名，比较不区分大小写。</summary>
        public string[] extensions = Array.Empty<string>();
    }
}
