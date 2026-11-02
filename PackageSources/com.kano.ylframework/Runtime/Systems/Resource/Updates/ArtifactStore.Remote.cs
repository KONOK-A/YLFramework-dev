using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <remarks>远端检查只选择已认证发布；准备阶段下载并验证文件，完整准备记录与激活仍是后续独立步骤。</remarks>
    public sealed partial class ArtifactStore
    {
        /// <summary>依次验签远端发布并核对其绑定的 Catalog，结合本地复用情况生成完整闭包计划，不写 Store。</summary>
        internal IEnumerator CheckRemote(IEnumerable<ResourceLocalSnapshot> current, Action<UpdatePlan> complete, Action ensureAlive = null,
            Func<Action<CancellationToken>, IEnumerator> runIO = null, Action<string, string> observeTarget = null)
        {
            CheckThread();
            ResourceRemoteSource.RequireSupportedRuntime();
            if (IsOperating)
            {
                throw new ResourceException(ResourceError.Busy, "ArtifactStore is already operating.");
            }

            ValidateOwner();
            RequireNoJournal();
            var from = Closure(current);
            var entries = new List<UpdateProject>();
            long required = 16384;
            foreach (var mount in Projects.OrderBy(x => x.ProjectId, StringComparer.Ordinal))
            {
                ensureAlive?.Invoke();
                var old = from[mount.ProjectId];
                if (mount.Remote == null)
                {
                    entries.Add(new UpdateProject(old, old, Array.Empty<UpdateFile>()));
                    continue;
                }
                using var metadata = new ResourceTransfer(mount.Remote, mount.Remote.Resolve("release.json"));
                yield return metadata;
                var release = new ResourceRemoteRelease(metadata.Bytes, mount, _unityCompatibility, DateTimeOffset.UtcNow);
                CheckReleaseFloor(release);
                using var catalog = new ResourceTransfer(mount.Remote, mount.Remote.Resolve(release.CatalogDigest + "/resource-catalog.json"), release.CatalogLength, release.CatalogDigest);
                yield return catalog;
                release.ReadCatalog(catalog.Bytes, mount, _unityCompatibility);
                observeTarget?.Invoke(mount.ProjectId, release.CatalogDigest);
                IReadOnlyList<UpdateFile> files = null;
                if (runIO == null)
                {
                    files = CheckRemoteFiles(old, release, CancellationToken.None);
                }
                else
                {
                    yield return runIO(token => files = CheckRemoteFilesForContent(old, release, token));
                }

                ensureAlive?.Invoke();
                if (files.Count != 0)
                {
                    // partial、正式staging以及8.7保留From副本的保守上界；不是空间预留。
                    required = checked(required + Files(release.Catalog).Sum(x => x.Length) * 2 + release.CatalogLength * 2 + release.CompleteLength * 2 + 16384);
                }
                entries.Add(new UpdateProject(old, release, files));
            }
            if (entries.All(x => x.Remote == null))
            {
                throw new ResourceException(ResourceError.Unsupported, "No explicitly trusted remote projects are configured.");
            }

            foreach (var old in from.Values)
            {
                if (!Exists(VersionPath(old.Document.ProjectId, old.Document.CatalogDigest)))
                {
                    required = checked(required + Files(old.Document).Sum(x => x.Length) + ResourceCatalogCodec.Write(old.Document).LongLength + 16384);
                }
            }

            ValidateOwner();
            RequireNoJournal();
            ensureAlive?.Invoke();
            complete(new UpdatePlan(RootPath, entries, AvailableSpace(), required));
        }

        /// <summary>在只读内容 IO 门禁内检查远端目标的已安装版本或本地可复用文件。</summary>
        internal IReadOnlyList<UpdateFile> CheckRemoteFilesForContent(ResourceLocalSnapshot current, ResourceRemoteRelease release, CancellationToken cancellation)
        {
            using var guard = Enter(false, cancellation, true, true);
            RequireNoJournal();
            return CheckRemoteFiles(current, release, cancellation);
        }

        // 核对来源兼容范围；目标已安装则复验发布绑定，否则逐项验证可复用的来源文件。
        private IReadOnlyList<UpdateFile> CheckRemoteFiles(ResourceLocalSnapshot current, ResourceRemoteRelease release, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (release == null || current == null || current.Document.ProjectId != release.ProjectId ||
                !_mounts.TryGetValue(release.ProjectId, out var mount) || current.Document.Platform != mount.Platform ||
                current.Document.UnityCompatibility != _unityCompatibility || current.Document.AppContentCompatibilityId != mount.AppContentCompatibilityId)
            {
                throw new InvalidDataException("The remote check is outside this store's explicit compatible source closure.");
            }

            if (Exists(VersionPath(release.ProjectId, release.CatalogDigest)))
            {
                OpenRemoteVersion(release, true, cancellation);
                cancellation.ThrowIfCancellationRequested();
                return Array.Empty<UpdateFile>();
            }
            var files = new List<UpdateFile>();
            foreach (var file in Files(release.Catalog))
            {
                cancellation.ThrowIfCancellationRequested();
                var reusable = Files(current.Document).Any(x => x.RelativeFile == file.RelativeFile && x.Length == file.Length && x.Sha256 == file.Sha256);
                if (reusable)
                {
                    using (current.OpenVerified(file, cancellation))
                    {
                    }
                }

                files.Add(new UpdateFile(file.RelativeFile, file.Length, reusable, reusable ? Path.Combine(current.DirectoryPath, file.RelativeFile) : null));
            }
            cancellation.ThrowIfCancellationRequested();
            return files;
        }

        // 查询当前调用方在目标卷上的可用空间；查询失败不视为无限可用。
        private long AvailableSpace()
        {
#if !UNITY_EDITOR && UNITY_STANDALONE_WIN && ENABLE_IL2CPP
            // 该Unity版本的IL2CPP连DriveInfo构造都会调用未实现的GetDriveFormat。
            // 保留原调用者可用配额语义，查询失败明确拒绝，不能假报无限空间。
            if (!GetDiskFreeSpaceExW(Path.GetPathRoot(RootPath), out var available, out _, out _))
                throw new IOException("Cannot query available Resource volume space.", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            return checked((long)available);
#else
            return new DriveInfo(Path.GetPathRoot(RootPath)).AvailableFreeSpace;
#endif
        }

#if !UNITY_EDITOR && UNITY_STANDALONE_WIN && ENABLE_IL2CPP
        // 为受支持的 Windows IL2CPP 路径取得调用方可用配额，避开该版本 DriveInfo 限制。
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
#endif

        /// <summary>依次执行远端安装、同步内容验证和准备记录发布，不切换 active。</summary>
        internal IEnumerator PrepareRemote(UpdatePlan plan, Action<PreparedUpdate> complete, Action ensureAlive = null, ResourceDecryptionOptions decryption = null)
        {
            yield return InstallRemotePreparation(plan, ensureAlive);
            ensureAlive?.Invoke();
            using var receipt = CapturePreparation(plan, decryption);
            receipt.Validate();
            ensureAlive?.Invoke();
            complete(CompletePreparation(plan, receipt));
        }

        /// <summary>持有 Store 写入责任安装完整来源和目标，枚举器收尾才释放该责任。</summary>
        internal IEnumerator InstallRemotePreparation(UpdatePlan plan, Action ensureAlive = null,
            Func<Action<CancellationToken>, IEnumerator> runIO = null)
        {
            if (plan == null || plan.Root != RootPath || !plan.IsRemote)
            {
                throw new ArgumentException("A remote plan from this store is required.");
            }

            ResourceRemoteSource.RequireSupportedRuntime();
            ensureAlive?.Invoke();
            // 复用store排他锁，取消/退出由枚举器Dispose释放；旧版Acquire不需要此磁盘写锁。
            using var guard = Enter(true);
            RecoverMetadata();
            RequireNoJournal();
            if (AvailableSpace() < plan.RequiredBytes)
            {
                throw new IOException("Insufficient free disk space for remote preparation.");
            }

            foreach (var entry in plan.Projects)
            {
                ensureAlive?.Invoke();
                entry.Remote?.RequireFresh(DateTimeOffset.UtcNow);
                if (entry.Remote != null)
                {
                    CheckReleaseFloor(entry.Remote);
                }

                yield return RemotePreparationIO(token =>
                {
                    var alive = runIO == null ? ensureAlive : null;
                    Install(entry.From, token, ensureAlive: alive);
                    if (entry.Remote == null)
                    {
                        Install(entry.Target, token, entry.Files, alive);
                    }
                }, runIO);
                ensureAlive?.Invoke();
                if (entry.Remote != null)
                {
                    yield return InstallRemote(entry, ensureAlive, runIO);
                }
            }
            foreach (var entry in plan.Projects)
            {
                entry.Remote?.RequireFresh(DateTimeOffset.UtcNow);
            }

            ensureAlive?.Invoke();
        }

        // 将一段磁盘工作交给内容调度器；未提供调度器时在当前线程同步执行。
        private static IEnumerator RemotePreparationIO(Action<CancellationToken> work, Func<Action<CancellationToken>, IEnumerator> runIO)
        {
            if (runIO == null)
            {
                work(CancellationToken.None);
            }
            else
            {
                yield return runIO(work);
            }
        }

        // 复用已认证输入或续传分片，逐文件验 hash 后再组装此项目的正式版本。
        private IEnumerator InstallRemote(UpdateProject entry, Action ensureAlive, Func<Action<CancellationToken>, IEnumerator> runIO)
        {
            ensureAlive?.Invoke();
            var release = entry.Remote;
            if (Exists(VersionPath(entry.ProjectId, entry.ToDigest)))
            {
                yield return RemotePreparationIO(token => OpenRemoteVersion(release, true, token), runIO);
                yield break;
            }
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Files(release.Catalog).Concat(new[] { new ResourceCatalogFile(new JObject { ["RelativeFile"] = ".ylr-complete.json", ["Length"] = release.CompleteLength, ["Sha256"] = release.CompleteSha256 }) }))
            {
                ensureAlive?.Invoke();
                release.RequireFresh(DateTimeOffset.UtcNow);
                var reusable = entry.Files.FirstOrDefault(x => x.RelativeFile == file.RelativeFile && x.IsReusable);
                if (reusable != null)
                {
                    inputs.Add(file.RelativeFile, reusable.Input);
                    continue;
                }
                var part = new DownloadPart(this, release, file.RelativeFile, file.Length, file.Sha256);
                if (AvailableSpace() < Math.Max(0, file.Length - part.Length) + 16384)
                {
                    throw new IOException("Insufficient free disk space for release content.");
                }

                var completePart = false;
                if (part.Length == part.Expected)
                {
                    yield return RemotePreparationIO(token =>
                    {
                        try
                        {
                            part.Verify(token);
                            completePart = true;
                        }
                        catch (InvalidDataException)
                        {
                            part.Reset();
                        }
                    }, runIO);
                }

                ensureAlive?.Invoke();
                if (completePart)
                {
                    inputs.Add(file.RelativeFile, part.Path);
                    continue;
                }
                using var transfer = new ResourceTransfer(release.Source, release.Source.Resolve(release.CatalogDigest + "/" + file.RelativeFile), file.Length, file.Sha256,
                    part: part, deferPartVerification: runIO != null);
                yield return transfer;
                ensureAlive?.Invoke();
                yield return RemotePreparationIO(token =>
                {
                    try
                    {
                        part.Verify(token);
                    }
                    catch (InvalidDataException)
                    {
                        part.Reset();
                        throw;
                    }
                }, runIO);
                inputs.Add(file.RelativeFile, part.Path);
            }
            ensureAlive?.Invoke();
            yield return RemotePreparationIO(token => InstallRemoteFiles(entry, inputs, token, runIO == null ? ensureAlive : null), runIO);
        }

        // 将远端发布输入复制到独立 staging，写入已认证 Catalog 并完整校验后提升目录。
        private void InstallRemoteFiles(UpdateProject entry, IReadOnlyDictionary<string, string> inputs, CancellationToken cancellation, Action ensureAlive = null)
        {
            var release = entry.Remote;
            cancellation.ThrowIfCancellationRequested();
            ensureAlive?.Invoke();
            release.RequireFresh(DateTimeOffset.UtcNow);
            var stage = Path.Combine(ProjectRoot(entry.ProjectId), "staging", Guid.NewGuid().ToString("N"));
            ResourceLocalSnapshot.RejectLinks(stage);
            RequireOwner();
            Directory.CreateDirectory(stage);
            var stageMarker = new JObject
            {
                ["Version"] = 1,
                ["OwnerId"] = _ownerId,
                ["ProjectId"] = entry.ProjectId,
                ["Platform"] = entry.Platform,
                ["CatalogDigest"] = entry.ToDigest,
                ["Attempt"] = Path.GetFileName(stage),
                ["Files"] = new JArray(Files(release.Catalog).Select(x => x.RelativeFile).Concat(new[] { "resource-catalog.json", ".ylr-complete.json" }))
            };
            Write(Path.Combine(stage, ".ylr-stage.json"), stageMarker);
            foreach (var input in inputs)
            {
                cancellation.ThrowIfCancellationRequested();
                ensureAlive?.Invoke();
                RequireOwner();
                ResourceLocalSnapshot.RejectLinks(input.Value);
                using var stream = new FileStream(input.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(Path.Combine(stage, input.Key), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[64 * 1024];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    ensureAlive?.Invoke();
                    output.Write(buffer, 0, count);
                }
                output.Flush(true);
                Checkpoint?.Invoke("RemoteFileCopied");
                cancellation.ThrowIfCancellationRequested();
                ensureAlive?.Invoke();
            }
            var bytes = ResourceCatalogCodec.Write(release.Catalog);
            using (var output = new FileStream(Path.Combine(stage, "resource-catalog.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            var mount = _mounts[entry.ProjectId];
            Checkpoint?.Invoke("StagingSnapshotValidationStarted");
            try
            {
                ResourceLocalSnapshot.OpenStaging(stage, entry.ProjectId, entry.ToDigest, entry.Platform, mount.AppContentCompatibilityId, _unityCompatibility, cancellation);
            }
            finally
            {
                Checkpoint?.Invoke("StagingSnapshotValidationFinished");
            }
            release.RequireFresh(DateTimeOffset.UtcNow);
            cancellation.ThrowIfCancellationRequested();
            PromoteStage(stage, stageMarker, release.Catalog, ensureAlive, cancellation);
        }

        // 在 owned 版本校验之外，复验 Catalog 和 Complete 与本次签名发布绑定的长度及摘要。
        private ResourceLocalSnapshot OpenRemoteVersion(ResourceRemoteRelease release, bool refresh = true, CancellationToken cancellation = default)
        {
            var snapshot = Open(release.ProjectId, release.CatalogDigest, refresh, cancellation);
            // 既有Complete的结构有效不等于符合本次发布签名；8.7本地版本升级为可信远端版本时同样不能跳过。
            foreach (var file in new[]
            {
                new ResourceCatalogFile(new JObject { ["RelativeFile"] = "resource-catalog.json", ["Length"] = release.CatalogLength, ["Sha256"] = release.CatalogDigest }),
                new ResourceCatalogFile(new JObject { ["RelativeFile"] = ".ylr-complete.json", ["Length"] = release.CompleteLength, ["Sha256"] = release.CompleteSha256 })
            })
            {
                using (snapshot.OpenVerified(file, cancellation))
                {
                }
            }

            return snapshot;
        }

        // 将发布授权原信封按项目顺序保存，供以后按当前可信公钥重新验签。
        private static JArray ReleaseRecords(IEnumerable<ResourceRemoteRelease> releases) => new(releases.OrderBy(x => x.ProjectId, StringComparer.Ordinal).Select(x =>
            new JObject { ["ProjectId"] = x.ProjectId, ["Envelope"] = Convert.ToBase64String(x.Envelope) }));

        // 重验准备记录内的授权及目标摘要绑定；枚举可读取过期授权，提交另要求 fresh。
        private ResourceRemoteRelease[] ReadReleaseRecords(JObject record, bool fresh = false)
        {
            if (ResourceCatalogCodec.Integer(record, "Version") == 1)
            {
                return Array.Empty<ResourceRemoteRelease>();
            }

            var array = ReadArray(record, "Releases");
            if (array.Count == 0 || array.Count > _mounts.Count)
            {
                throw new InvalidDataException("Invalid prepared release closure.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            return array.Select(token =>
            {
                if (token is not JObject item)
                {
                    throw new InvalidDataException("Expected prepared release.");
                }

                ResourceCatalogCodec.Fields(item, "ProjectId,Envelope");
                var project = ResourceCatalogCodec.Text(item, "ProjectId");
                if (!_mounts.TryGetValue(project, out var mount) || !seen.Add(project))
                {
                    throw new InvalidDataException("Unknown or duplicate prepared release project.");
                }

                var release = new ResourceRemoteRelease(ResourceReleaseSignature.Base64(item, "Envelope", ResourceReleaseSignature.MaximumBytes), mount, _unityCompatibility,
                    DateTimeOffset.UtcNow, fresh);
                if (ReadTransition(record["Transition"]).Cast<JObject>().Single(x => (string)x["ProjectId"] == project)["To"].Value<string>() != release.CatalogDigest)
                {
                    throw new InvalidDataException("Prepared release does not authorize its target digest.");
                }

                return release;
            }).ToArray();
        }
    }
}
