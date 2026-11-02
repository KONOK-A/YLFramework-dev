using UnityEditor;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>可随工程共享的非秘密执行配置；资源定义、压缩与加密只由引用的 Project 决定。</summary>
    [CreateAssetMenu(menuName = "YLFramework/Resource/Build Recipe", fileName = "ResourceBuildRecipe")]
    public sealed class ResourceBuildRecipe : ScriptableObject
    {
        /// <summary>提供资源选择、压缩和加密策略的项目定义。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Project")]
        public ResourceProject project;
        /// <summary>执行时必须已经激活且与 Editor 主机匹配的目标平台。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Target")]
        [Tooltip("明确目标，执行时必须已激活、安装且匹配主机；不隐式切平台。")]
        public BuildTarget target = BuildTarget.StandaloneWindows64;
        /// <summary>由应用约定并写入发布 Catalog 的资源兼容标识。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("AppContentCompatibilityId")]
        public string appContentCompatibilityId = "app-v1";
        /// <summary>选择只构建，或构建成功后再执行本地发布。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Action")]
        public ResourceBuildAction action;
        /// <summary>显式要求调用 Unity 强制重建，而不是整体跳过构建。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ForceRebuild")]
        public bool forceRebuild;
        /// <summary>本地发布成功后生成地址常量；生成失败不撤销发布。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("GenerateReferences")]
        [Tooltip("仅 BuildAndPublish 可请求常量生成；生成失败不撤销已成功发布。")]
        public bool generateReferences;
        /// <summary>常量源码及专属程序集的 Assets 相对输出目录，不接管未知内容。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ReferenceDirectory")]
        public string referenceDirectory = ResourceReferenceOutput.DirectoryPath;
    }

    /// <summary>配方可请求的本地执行范围，不包含部署、上传或签发。</summary>
    public enum ResourceBuildAction
    {
        /// <summary>只构建并验证可变工作区，不创建发布版本。</summary>
        BuildOnly,
        /// <summary>构建成功后发布本地不可变版本，可另行请求常量生成。</summary>
        BuildAndPublish
    }

    /// <summary>一次解析得到的只读执行输入，不持有密钥/provider；不替代 Builder 的完整输入冻结与后验。</summary>
    public sealed class ResourceBuildInvocation
    {
        /// <summary>解析时配方资产的路径。</summary>
        public string RecipePath
        {
            get;
        }
        /// <summary>解析时项目配置的持久 GUID，用于重新定位原项目。</summary>
        public string ProjectGuid
        {
            get;
        }
        /// <summary>冻结的项目技术标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>冻结的目标平台。</summary>
        public BuildTarget Target
        {
            get;
        }
        /// <summary>冻结的应用资源兼容标识。</summary>
        public string AppContentCompatibilityId
        {
            get;
        }
        /// <summary>冻结的构建或构建并发布操作。</summary>
        public ResourceBuildAction Action
        {
            get;
        }
        /// <summary>本次是否明确请求强制重建。</summary>
        public bool ForceRebuild
        {
            get;
        }
        /// <summary>本次是否请求在发布成功后生成常量。</summary>
        public bool GenerateReferences
        {
            get;
        }
        /// <summary>冻结的常量目标目录；未请求生成时为 null。</summary>
        public string ReferenceDirectory
        {
            get;
        }
        /// <summary>解析时项目的压缩策略。</summary>
        public ResourceCompression Compression
        {
            get;
        }
        /// <summary>解析时项目的交付编码策略。</summary>
        public ResourceContentEncryption ContentEncryption
        {
            get;
        }
        /// <summary>冻结的非秘密内容密钥标识；不加密时为 null。</summary>
        public string ContentKeyId
        {
            get;
        }

        /// <summary>复制配方及项目的执行选项，不保留可编辑定义或读取密钥。</summary>
        internal ResourceBuildInvocation(ResourceBuildRecipe recipe)
        {
            RecipePath = AssetDatabase.GetAssetPath(recipe);
            ProjectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(recipe.project));
            ProjectId = recipe.project.projectId;
            Target = recipe.target;
            AppContentCompatibilityId = recipe.appContentCompatibilityId;
            Action = recipe.action;
            ForceRebuild = recipe.forceRebuild;
            GenerateReferences = recipe.generateReferences;
            ReferenceDirectory = GenerateReferences ? recipe.referenceDirectory : null;
            Compression = recipe.project.compression;
            ContentEncryption = recipe.project.contentEncryption;
            ContentKeyId = ContentEncryption == ResourceContentEncryption.None ? null : recipe.project.contentKeyId;
        }

        /// <summary>展示本次冻结的非秘密执行选项。</summary>
        public override string ToString() => "Recipe=" + RecipePath + "\nProject=" + ProjectId + " / GUID=" + ProjectGuid +
            "\nTarget=" + Target + " / App=" + AppContentCompatibilityId + "\nAction=" + Action + " / ForceRebuild=" + ForceRebuild +
            " / Compression=" + Compression + " / Delivery=" + ContentEncryption +
            (ContentKeyId == null ? "" : " / KeyId=" + ContentKeyId) + "\nGenerateReferences=" + GenerateReferences +
            (GenerateReferences ? " / Directory=" + ReferenceDirectory : "");
    }
}
