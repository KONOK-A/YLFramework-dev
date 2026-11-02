using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>Editor窗口的纯值选定身份；不是发布索引或许可，使用时必须ReadVersion。</summary>
    [Serializable]
    internal sealed class ResourceReleaseIdentity
    {
        /// <summary>选定发布所属项目配置的 GUID。</summary>
        [SerializeField] private string projectGuid;
        /// <summary>选定发布的项目技术标识。</summary>
        [SerializeField] private string projectId;
        /// <summary>选定发布的平台名称。</summary>
        [SerializeField] private string platform;
        /// <summary>选定发布要求的 Unity 版本。</summary>
        [SerializeField] private string unity;
        /// <summary>重新打开发布时使用的应用资源兼容标识。</summary>
        [SerializeField] private string app;
        /// <summary>选定 Catalog 的规范构建身份。</summary>
        [SerializeField] private string buildId;
        /// <summary>选定 Catalog 全部字节的摘要。</summary>
        [SerializeField] private string digest;
        /// <summary>选定发布的确切来源目录，不用当前索引替代。</summary>
        [SerializeField] private string directory;
        internal string ProjectGuid => projectGuid;
        internal string ProjectId => projectId;
        internal string Platform => platform;
        internal string App => app;
        internal string CatalogDigest => digest;
        internal string DirectoryPath => directory;

        /// <summary>复制明确发布的完整身份，不将副本当作后续操作的验证凭证。</summary>
        internal ResourceReleaseIdentity(ResourceRelease release)
        {
            if (release?.Catalog == null || string.IsNullOrEmpty(release.ProjectGuid))
            {
                throw new ArgumentException("需要确切owned Release身份。");
            }

            projectGuid = release.ProjectGuid;
            projectId = release.Catalog.ProjectId;
            platform = release.Catalog.Platform;
            unity = release.Catalog.UnityCompatibility;
            app = release.Catalog.AppContentCompatibilityId;
            buildId = release.Catalog.BuildId;
            digest = release.Catalog.CatalogDigest;
            directory = release.DirectoryPath;
        }

        /// <summary>按原项目和摘要重读历史发布，再与保存的完整身份及来源路径比较。</summary>
        internal ResourceRelease Resolve()
        {
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(projectGuid));
            if (!project || project.projectId != projectId)
            {
                throw new InvalidOperationException("选定版本的Project身份已删除/变化，不能改读另一Project或当前Index。");
            }

            if (!Enum.TryParse(platform, out BuildTarget target) || !Enum.IsDefined(typeof(BuildTarget), target))
            {
                throw new InvalidDataException("选定版本平台不可识别。");
            }

            var release = ResourcePublisher.ReadVersion(project, target, app, digest);
            RequireSame(release);
            return release;
        }

        /// <summary>核对已捕获发布与选定身份一致，并通过 Publisher 复验导出来源。</summary>
        internal ResourceRelease ResolveForArchive(ResourceRelease captured)
        {
            RequireSame(captured);
            ResourcePublisher.VerifyExportInput(captured);
            return captured;
        }

        /// <summary>要求复验后的项目、平台、版本摘要及来源目录仍与选定副本相同。</summary>
        private void RequireSame(ResourceRelease release)
        {
            if (release?.Catalog == null || release.ProjectGuid != projectGuid || release.Catalog.ProjectId != projectId || release.Catalog.Platform != platform ||
                release.Catalog.UnityCompatibility != unity || release.Catalog.BuildId != buildId || release.Catalog.CatalogDigest != digest || release.DirectoryPath != directory)
            {
                throw new InvalidDataException("复验后的完整身份/来源与选定副本不一致。");
            }
        }

        /// <summary>展示选定身份的非秘密技术信息，不读取文件或验证发布。</summary>
        public override string ToString() => "ProjectId=" + projectId + " / GUID=" + projectGuid + "\nPlatform=" + platform + " / Unity=" + unity +
            "\nApp=" + app + "\nCatalogDigest=" + digest + "\nBuildId=" + buildId + "\n发布版本目录=" + directory;
    }
}
