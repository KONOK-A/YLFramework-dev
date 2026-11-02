using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using YLFramework.Singletons;

namespace YLFramework.Resource
{
    /// <remarks>制品查询只借读运行中的保护身份与原因，不增加资源 pin 或替代真实版本操作门禁。</remarks>
    public sealed partial class ResourceSystem
    {
        /// <summary>一次制品观测使用的运行中身份与保护副本，仅在内部汇总时可变。</summary>
        internal sealed class ArtifactRuntimeState
        {
            internal readonly Dictionary<string, ArtifactProtection> Protections = new(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Operations = new();
            internal string Identity;
            /// <summary>合并路径保护原因，不持有快照、文件或 Unity 对象。</summary>
            internal void Protect(string path, ArtifactProtection reason)
            {
                Protections.TryGetValue(path, out var current);
                Protections[path] = current | reason;
            }
        }

        // 只复制身份及原因；不增加 pin，不持有 Unity 对象，不清理已失效会话。
        /// <summary>在主线程采样同根 authority 与排空主体的版本保护、操作说明及变化身份。</summary>
        internal static ArtifactRuntimeState CaptureArtifactState(ArtifactStore store)
        {
            AssertThread();
            var result = new ArtifactRuntimeState();
            var states = new List<string> { _isEnding.ToString(), RuntimeHelpers.GetHashCode(SingletonSessionState.ExecutionSession).ToString() };
            foreach (var system in _draining.Concat(_authority == null ? Array.Empty<ResourceSystem>() : new[] { _authority }).Distinct())
            {
                if (!string.Equals(system._configuration?.ArtifactStore?.RootPath, store.RootPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                states.Add(RuntimeHelpers.GetHashCode(system) + ":" + system.State + ":" + system.IsSessionActive);
                foreach (var version in system.HeldStoreVersions())
                {
                    result.Protect(version.snapshot.DirectoryPath, version.reason);
                }

                foreach (var work in system._contentWork)
                {
                    foreach (var snapshot in work.Snapshots)
                    {
                        if (store.Projects.Any(x => x.ProjectId == snapshot.Document.ProjectId && x.Platform == snapshot.Document.Platform))
                        {
                            result.Protect(store.VersionPath(snapshot.Document.ProjectId, snapshot.Document.CatalogDigest), ArtifactProtection.Held | ArtifactProtection.InFlight);
                        }
                    }
                }

                foreach (var remote in system._remoteJobs.Values)
                {
                    foreach (var path in remote.ContentPaths)
                    {
                        result.Protect(path, ArtifactProtection.Held | ArtifactProtection.InFlight);
                    }
                }

                if (system._isUpdating)
                {
                    result.Operations.Add(system._activationProjects.Count == 0 ? "LocalVersionTransaction" : "Activation");
                }

                if (system._contentWork.Count != 0)
                {
                    result.Operations.Add("ContentIO");
                }

                if (system._retainedContentCleanup.Count != 0)
                {
                    result.Operations.Add("ContentCleanup");
                }

                foreach (var key in system._remoteJobs.Keys)
                {
                    result.Operations.Add(key == "check" ? "RemoteCheck" : "RemotePrepare");
                }
            }
            result.Identity = string.Join(";", states.Concat(result.Operations.OrderBy(x => x, StringComparer.Ordinal)).Concat(
                result.Protections.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + ":" + x.Value)));
            return result;
        }
    }
}
