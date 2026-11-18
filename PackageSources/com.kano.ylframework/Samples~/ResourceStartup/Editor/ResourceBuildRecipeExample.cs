using System.Threading;
using UnityEditor.AddressableAssets.Settings;
using YLFramework.Editor.Resource;

namespace YLFramework.Samples.Resource.Editor
{
    /// <summary>显式使用现有官方 Settings；文件保留原 GUID，不在 Editor 启动任何运行时服务。</summary>
    public static class ResourceContentBuildExample
    {
        /// <summary>在稳定 Editor 中正式构建，并向明确的新路径归档本 Player 的官方内容状态；不上传或部署。</summary>
        public static ResourceEditorBuildReport Build(AddressableAssetSettings settings, string contentStateArchivePath,
            CancellationToken cancellation = default) => ResourceEditorOperations.BuildContent(settings, contentStateArchivePath, cancellation);

        /// <summary>使用目标 Player 的冻结官方状态构建更新；限制冲突需用户先在官方工具中解决。</summary>
        public static ResourceEditorBuildReport BuildUpdate(AddressableAssetSettings settings, string previousContentStatePath,
            CancellationToken cancellation = default) => ResourceEditorOperations.BuildContentUpdate(settings, previousContentStatePath, cancellation);
    }
}
