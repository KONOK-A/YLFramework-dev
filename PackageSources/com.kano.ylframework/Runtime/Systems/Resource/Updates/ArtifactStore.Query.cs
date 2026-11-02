using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <remarks>制品查询分段复制观测结果，不持有跨段文件锁，也不把候选判断作为实际删除授权。</remarks>
    public sealed partial class ArtifactStore
    {
        /// <summary>分段只读观测，不 Mount、不 Recover、不建锁。每次 MoveNext 须在 Unity 主线程，中止须 Dispose。</summary>
        /// <remarks>默认最多处理 10000 个扫描步骤，每段最多 32 步。正常 Store 事务不等待此扫描；事务/会话/保护变化使候选结论失效。</remarks>
        /// <param name="completed">观测结束后接收报告的回调；提前释放枚举器不会交付报告。</param>
        /// <param name="maxEntries">扫描步骤上限，范围为 1 至 100000，并非仅统计最终条目数。</param>
        /// <param name="cancellation">取消观测后交付 Canceled 报告，不修改 Store。</param>
        public IEnumerator QueryAsync(Action<ArtifactStoreReport> completed, int maxEntries = 10000, CancellationToken cancellation = default)
            => Query(this, completed, maxEntries, cancellation);

        /// <summary>在调用时校验线程、回调和扫描预算，再创建可处理未配置 Store 的观测枚举器。</summary>
        internal static IEnumerator Query(ArtifactStore store, Action<ArtifactStoreReport> completed, int maxEntries, CancellationToken cancellation)
        {
            ResourceSystem.AssertThread();
            store?.CheckThread();
            if (completed == null)
            {
                throw new ArgumentNullException(nameof(completed));
            }

            if (maxEntries < 1 || maxEntries > 100000)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntries));
            }

            return RunQuery(store, completed, maxEntries, cancellation);
        }

        // 分段扫描并复核运行中身份与文件观测值；发生变化时保留数据但撤销候选结论。
        private static IEnumerator RunQuery(ArtifactStore store, Action<ArtifactStoreReport> completed, int maxEntries, CancellationToken cancellation)
        {
            ResourceSystem.AssertThread();
            var scan = new ArtifactScan(store);
            if (store == null)
            {
                completed(scan.Report(ArtifactQueryStatus.NotConfigured));
                yield break;
            }
            var runtime = ResourceSystem.CaptureArtifactState(store);
            foreach (var item in runtime.Protections)
            {
                scan.Protect(item.Key, item.Value);
            }

            scan.Operations.AddRange(runtime.Operations);
            var serial = _activitySerial;
            var steps = 0;
            using var work = scan.Scan().Concat(scan.VerifyObservations()).GetEnumerator();
            var done = false;
            while (!done)
            {
                ResourceSystem.AssertThread();
                store.CheckThread();
                if (cancellation.IsCancellationRequested)
                {
                    scan.Status = ArtifactQueryStatus.Canceled;
                    break;
                }
                var current = ResourceSystem.CaptureArtifactState(store);
                if (store.IsOperating || current.Operations.Count != 0)
                {
                    scan.Status = steps == 0 ? ArtifactQueryStatus.Busy : ArtifactQueryStatus.Changed;
                    scan.Issues.Add("A version operation is in progress; retry after it settles.");
                    break;
                }
                if (_activitySerial != serial || current.Identity != runtime.Identity)
                {
                    scan.Status = ArtifactQueryStatus.Changed;
                    scan.Issues.Add("Store activity, session or version retention changed during observation; retry.");
                    break;
                }
                try
                {
                    store.ProbeQueryLock();
                    for (var i = 0; i < 32; i++)
                    {
                        if (++steps > maxEntries)
                        {
                            scan.Status = ArtifactQueryStatus.Incomplete;
                            scan.Issues.Add("The scan step limit was reached; increase maxEntries explicitly or retry with a smaller Store closure.");
                            done = true;
                            break;
                        }
                        if (!work.MoveNext())
                        {
                            done = true;
                            break;
                        }
                        if (cancellation.IsCancellationRequested)
                        {
                            scan.Status = ArtifactQueryStatus.Canceled;
                            done = true;
                            break;
                        }
                    }
                }
                catch (ObservationChanged error)
                {
                    scan.Status = ArtifactQueryStatus.Changed;
                    scan.Issues.Add(error.Message);
                    done = true;
                }
                catch (ResourceException error) when (error.Kind == ResourceError.Busy)
                {
                    scan.Status = ArtifactQueryStatus.Busy;
                    scan.Issues.Add(error.Message);
                    done = true;
                }
                catch (Exception error) when (IsFileFailure(error) || error is OverflowException)
                {
                    scan.Status = error is InvalidDataException || error is ArgumentException ? ArtifactQueryStatus.Invalid : ArtifactQueryStatus.Unreadable;
                    scan.Issues.Add(error.Message);
                    done = true;
                }
                if (!done)
                {
                    yield return null;
                }
            }
            completed(scan.Report(scan.Status));
        }

        // 仅探测已有文件锁是否可读，不创建、等待或跨扫描步骤持有锁。
        private void ProbeQueryLock()
        {
            var path = Path.Combine(RootPath, ".ylr-store.lock");
            ResourceLocalSnapshot.RejectLinks(path);
            if (!Exists(path))
            {
                return;
            }

            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException error)
            {
                throw new ResourceException(ResourceError.Busy, "The existing Store lock is unavailable; no lock was created or retained.", error);
            }
        }

        /// <summary>标记文件系统观测值在本次分段扫描期间发生变化。</summary>
        private sealed class ObservationChanged : IOException
        {
            /// <summary>保存需要重新观测的路径说明。</summary>
            internal ObservationChanged(string path) : base("An observed filesystem entry changed; retry: " + path) { }
        }

        /// <summary>保存一次分段扫描的可变中间数据，最终报告只复制值与只读集合。</summary>
        private sealed class ArtifactScan
        {
            private readonly ArtifactStore _store;
            private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
            private readonly List<Entry> _entries = new();
            private readonly List<ArtifactPreparedInfo> _prepared = new();
            private readonly Dictionary<string, ArtifactProtection> _protections = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _completeVersions = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, (bool exists, FileAttributes attributes, long length, long ticks)> _observations = new(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Operations = new();
            internal readonly List<string> Issues = new();
            internal ArtifactQueryStatus Status = ArtifactQueryStatus.Ready;
            /// <summary>绑定要观测的 Store，不执行目录访问。</summary>
            internal ArtifactScan(ArtifactStore store) => this._store = store;

            /// <summary>扫描期间逐步补充的单个条目，不直接暴露给报告使用者。</summary>
            private sealed class Entry
            {
                internal string Path;
                internal ResourceProjectMount Project;
                internal string Digest;
                internal ArtifactKind Kind;
                internal ArtifactState State;
                internal long Bytes;
                internal bool IsSizeComplete = true;
                internal bool IsEligible;
                internal readonly List<string> Issues = new();
            }

            /// <summary>合并某路径的持久或运行中保护原因，不实际保活版本。</summary>
            internal void Protect(string path, ArtifactProtection reason)
            {
                _protections.TryGetValue(path, out var current);
                _protections[path] = current | reason;
            }

            // 采集路径存在性、属性、逻辑长度和修改时间；不跟随链接或读取内容 hash。
            private static (bool exists, FileAttributes attributes, long length, long ticks) Observe(string path)
            {
                try
                {
                    ResourceLocalSnapshot.RejectLinks(path);
                    var attributes = File.GetAttributes(path);
                    return (true, attributes, (attributes & FileAttributes.Directory) != 0 ? 0 : new FileInfo(path).Length,
                        (attributes & FileAttributes.Directory) != 0 ? Directory.GetLastWriteTimeUtc(path).Ticks : File.GetLastWriteTimeUtc(path).Ticks);
                }
                catch (FileNotFoundException)
                {
                    return default;
                }
                catch (DirectoryNotFoundException)
                {
                    return default;
                }
            }

            // 保存路径观测值，重复遇到同一路径时要求仍一致。
            private void Remember(string path)
            {
                var value = Observe(path);
                if (_observations.TryGetValue(path, out var previous) && previous != value)
                {
                    throw new ObservationChanged(path);
                }

                _observations[path] = value;
            }

            /// <summary>分步复查已采集路径，并要求持久保护版本具有已识别的完整记录集合。</summary>
            internal IEnumerable<object> VerifyObservations()
            {
                if (Status != ArtifactQueryStatus.Ready)
                {
                    yield break;
                }

                foreach (var pair in _observations)
                {
                    if (Observe(pair.Key) != pair.Value)
                    {
                        throw new ObservationChanged(pair.Key);
                    }

                    yield return null;
                }
                foreach (var pair in _protections.Where(x => (x.Value & (ArtifactProtection.Active | ArtifactProtection.LastKnownGood | ArtifactProtection.PreparedFrom | ArtifactProtection.PreparedTo)) != 0))
                {
                    if (!_completeVersions.Contains(pair.Key))
                    {
                        Status = ArtifactQueryStatus.Invalid;
                        Issues.Add("A protected version has missing, unknown or invalid records/files: " + pair.Key);
                    }
                    yield return null;
                }
            }

            /// <summary>依次读取归属、控制记录、保护与目录布局，发现待恢复控制槽时不代为恢复。</summary>
            internal IEnumerable<object> Scan()
            {
                Remember(_store.RootPath);
                if (!_observations[_store.RootPath].exists)
                {
                    Status = ArtifactQueryStatus.Missing;
                    yield break;
                }
                Remember(_store.OwnerPath);
                foreach (var mount in _store.Projects)
                {
                    Remember(Path.Combine(_store.ProjectRoot(mount.ProjectId), OwnerFile));
                }

                _store.RequireOwner();
                foreach (var path in _store.ControlFiles().SelectMany(x => new[] { x, x + ".next" }))
                {
                    Remember(path);
                    if (_observations[path].exists && (path == _store.JournalPath || path.EndsWith(".next", StringComparison.Ordinal)))
                    {
                        AddFile(path, null, ArtifactKind.Metadata);
                        Status = ArtifactQueryStatus.RecoveryRequired;
                        Issues.Add("Recovery is required; query did not recover or remove: " + path);
                        yield break;
                    }
                    yield return null;
                }
                foreach (var record in _store.IndexProtectionRecords(false, Remember))
                {
                    foreach (var item in _store.ProtectionPaths(record.data, record.reason))
                    {
                        Protect(item.path, item.reason);
                    }

                    yield return null;
                }
                foreach (var step in Prepared())
                {
                    yield return step;
                }

                foreach (var step in Layout(_store.RootPath, _store.Projects.Select(x => x.ProjectId), new[] { OwnerFile, ".ylr-store.lock" }, null))
                {
                    yield return step;
                }

                foreach (var mount in _store.Projects)
                {
                    var root = _store.ProjectRoot(mount.ProjectId);
                    foreach (var step in Layout(Path.GetDirectoryName(root), new[] { mount.Platform }, Array.Empty<string>(), mount))
                    {
                        yield return step;
                    }

                    var coordinator = root == _store.CoordinatorRoot;
                    foreach (var step in Layout(root, coordinator ? new[] { "versions", "staging", "parts", "prepared" } : new[] { "versions", "staging", "parts" },
                        coordinator ? new[] { OwnerFile, "active.json", "last-known-good.json", JournalFile } : new[] { OwnerFile, "active.json", "last-known-good.json" }, mount))
                    {
                        yield return step;
                    }

                    foreach (var step in Versions(mount, "versions", ArtifactKind.Version))
                    {
                        yield return step;
                    }

                    foreach (var step in Versions(mount, "staging", ArtifactKind.Staging))
                    {
                        yield return step;
                    }

                    foreach (var step in Parts(mount))
                    {
                        yield return step;
                    }
                }
            }

            // 复制正式准备记录的转换并登记保护，未知或 next 条目仅报告而不收尾。
            private IEnumerable<object> Prepared()
            {
                Remember(_store.PreparedRoot);
                foreach (var path in Directory.EnumerateFileSystemEntries(_store.PreparedRoot))
                {
                    var attributes = File.GetAttributes(path);
                    var ordinaryFile = (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
                    if (ordinaryFile && path.EndsWith(".json", StringComparison.Ordinal))
                    {
                        Remember(path);
                        var record = _store.ReadPreparedRecord(path);
                        foreach (var item in _store.ProtectionPaths(record, ArtifactProtection.PreparedFrom | ArtifactProtection.PreparedTo))
                        {
                            Protect(item.path, item.reason);
                        }

                        _prepared.Add(new ArtifactPreparedInfo(path, _store.ReadTransition(record["Transition"]).Cast<JObject>()
                            .Select(x => new ArtifactTransition((string)x["ProjectId"], (string)x["Platform"], (string)x["From"], (string)x["To"]))));
                        AddFile(path, null, ArtifactKind.Metadata);
                    }
                    else
                    {
                        AddUnknown(path, null);
                        if (ordinaryFile && path.EndsWith(".json.next", StringComparison.Ordinal))
                        {
                            Status = ArtifactQueryStatus.RecoveryRequired;
                            Issues.Add("A Prepared replacement requires an explicit recovery/preparation operation: " + path);
                        }
                        else if ((attributes & FileAttributes.ReparsePoint) != 0 && path.EndsWith(".json", StringComparison.Ordinal))
                        {
                            Status = ArtifactQueryStatus.Invalid;
                            Issues.Add("A linked Prepared record has unconfirmed protection; its target was not followed: " + path);
                        }
                    }
                    yield return null;
                }
            }

            // 核对当前层的预期目录和控制文件，将未登记条目单独保留为未知观测。
            private IEnumerable<object> Layout(string root, IEnumerable<string> directories, IEnumerable<string> files, ResourceProjectMount project)
            {
                Remember(root);
                var dirs = new HashSet<string>(directories, StringComparer.Ordinal);
                var names = new HashSet<string>(files, StringComparer.Ordinal);
                foreach (var path in Directory.EnumerateFileSystemEntries(root))
                {
                    var name = Path.GetFileName(path);
                    if (dirs.Contains(name))
                    {
                        ResourceLocalSnapshot.RejectLinks(path);
                        if (!Directory.Exists(path))
                        {
                            throw new InvalidDataException("Expected a managed directory: " + path);
                        }
                        Remember(path);
                    }
                    else if (names.Contains(name))
                    {
                        AddFile(path, project, ArtifactKind.Metadata);
                    }
                    else
                    {
                        AddUnknown(path, project);
                    }

                    yield return null;
                }
            }

            // 建立并登记扫描中的可变条目，后续统计与错误写入同一对象。
            private Entry NewEntry(string path, ResourceProjectMount project, ArtifactKind kind, ArtifactState state)
            {
                var entry = new Entry { Path = path, Project = project, Kind = kind, State = state };
                _entries.Add(entry);
                return entry;
            }

            // 记录普通文件的逻辑长度并累加，路径消失或改成目录视为观测变化。
            private void AddBytes(Entry entry, string path)
            {
                Remember(path);
                var value = _observations[path];
                if (!value.exists || (value.attributes & FileAttributes.Directory) != 0)
                {
                    throw new ObservationChanged(path);
                }

                entry.Bytes = checked(entry.Bytes + value.length);
            }

            // 将单个已知文件登记为仅统计大小的条目，不赋予清理资格。
            private void AddFile(string path, ResourceProjectMount project, ArtifactKind kind)
            {
                var entry = NewEntry(path, project, kind, ArtifactState.Unknown);
                Attempt(entry, () => AddBytes(entry, path));
            }

            // 只观察未知条目本身；未知目录不遍历、链接不跟随且始终保留。
            private void AddUnknown(string path, ResourceProjectMount project)
            {
                var entry = NewEntry(path, project, ArtifactKind.Unknown, ArtifactState.Unknown);
                Attempt(entry, () =>
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        entry.State = ArtifactState.Linked;
                        entry.IsSizeComplete = false;
                        entry.Issues.Add("Link target was not followed.");
                        return;
                    }
                    Remember(path);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        entry.IsSizeComplete = false;
                        entry.Issues.Add("Unknown ownership; directory contents were not traversed.");
                    }
                    else
                    {
                        AddBytes(entry, path);
                    }
                });
                entry.Issues.Add("Not a registered cleanup target; preserved.");
            }

            // 将文件类失败写入条目并撤销候选资格，观测变化则上抛以终止整次结论。
            private static bool Attempt(Entry entry, Action action)
            {
                try
                {
                    action();
                    return true;
                }
                catch (ObservationChanged)
                {
                    throw;
                }
                catch (Exception error) when (IsFileFailure(error))
                {
                    entry.State = error is InvalidDataException || error is ArgumentException ? ArtifactState.Invalid : ArtifactState.Unreadable;
                    entry.IsSizeComplete = false;
                    entry.IsEligible = false;
                    entry.Issues.Add(error.Message);
                    return false;
                }
            }

            // 分步核对正式或 staging 的归属与文件集合，不重新计算内容摘要。
            private IEnumerable<object> Versions(ResourceProjectMount project, string folder, ArtifactKind kind)
            {
                var root = Path.Combine(_store.ProjectRoot(project.ProjectId), folder);
                Remember(root);
                foreach (var path in Directory.EnumerateFileSystemEntries(root))
                {
                    var markerName = kind == ArtifactKind.Version ? VersionFile : ".ylr-stage.json";
                    var markerPath = Path.Combine(path, markerName);
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0 || !File.Exists(markerPath))
                    {
                        AddUnknown(path, project);
                        yield return null;
                        continue;
                    }
                    var entry = NewEntry(path, project, kind, kind == ArtifactKind.Version ? ArtifactState.CompleteRecord : ArtifactState.Staging);
                    JObject marker = null;
                    HashSet<string> registered = null;
                    if (!Attempt(entry, () =>
                    {
                        Remember(path);
                        Remember(markerPath);
                        marker = _store.Read(markerPath);
                        if (kind == ArtifactKind.Version)
                        {
                            _store.ValidateVersionMarker(marker, project.ProjectId, Path.GetFileName(path));
                        }
                        else
                        {
                            _store.ValidateStage(marker, project.ProjectId, Path.GetFileName(path));
                        }

                        entry.Digest = (string)marker["CatalogDigest"];
                        registered = new HashSet<string>(RegisteredFiles(marker), StringComparer.Ordinal);
                    }))
                    {
                        yield return null;
                        continue;
                    }
                    var allowed = new HashSet<string>(registered, StringComparer.Ordinal) { markerName };
                    if (kind == ArtifactKind.Staging)
                    {
                        allowed.UnionWith(new[] { VersionFile, VersionFile + ".next", markerName + ".next" });
                    }
                    // 正式版本 next 必须由原 RecoverMetadata 处理，查询不顺手收尾。
                    if (kind == ArtifactKind.Version && Exists(Path.Combine(path, VersionFile + ".next")))
                    {
                        Status = ArtifactQueryStatus.RecoveryRequired;
                        entry.Issues.Add("Version metadata replacement is pending.");
                    }
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    using var files = KnownDirectoryFiles(path, allowed).GetEnumerator();
                    var valid = true;
                    while (true)
                    {
                        var more = false;
                        if (!Attempt(entry, () => more = files.MoveNext()))
                        {
                            valid = false;
                            break;
                        }
                        if (!more)
                        {
                            break;
                        }

                        if (!Attempt(entry, () =>
                        {
                            AddBytes(entry, files.Current);
                            seen.Add(Path.GetFileName(files.Current));
                        }))
                        {
                            valid = false;
                            break;
                        }
                        yield return null;
                    }
                    if (valid)
                    {
                        var completePath = Path.Combine(path, ".ylr-complete.json");
                        entry.IsEligible = !seen.Contains(".ylr-complete.json") || Attempt(entry, () =>
                            ResourceLocalSnapshot.ValidateComplete(_store.Read(completePath), project.ProjectId, project.Platform, entry.Digest));
                        if (kind == ArtifactKind.Version && !registered.IsSubsetOf(seen))
                        {
                            if (entry.IsEligible)
                            {
                                entry.State = ArtifactState.Incomplete;
                            }
                            entry.Issues.Add("Owned remainder has missing registered files; it is not mountable. Original cleanup can reclaim it.");
                        }
                        if (entry.State == ArtifactState.CompleteRecord)
                        {
                            _completeVersions.Add(path);
                        }
                    }
                    yield return null;
                }
            }

            // 将可匹配的分片与元数据作为一组统计，未认领路径另列为未知条目。
            private IEnumerable<object> Parts(ResourceProjectMount project)
            {
                var root = Path.Combine(_store.ProjectRoot(project.ProjectId), "parts");
                Remember(root);
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in Directory.EnumerateFileSystemEntries(root))
                {
                    paths.Add(path);
                    yield return null;
                }
                var claimed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in paths)
                {
                    if (claimed.Contains(path) || !path.EndsWith(".json", StringComparison.Ordinal) && !path.EndsWith(".json.next", StringComparison.Ordinal))
                    {
                        yield return null;
                        continue;
                    }
                    var candidateOnly = path.EndsWith(".next", StringComparison.Ordinal);
                    var metadata = candidateOnly ? path.Substring(0, path.Length - 5) : path;
                    var part = Path.ChangeExtension(metadata, ".part");
                    if (candidateOnly && (paths.Contains(metadata) || paths.Contains(part)))
                    {
                        yield return null;
                        continue;
                    }
                    var entry = NewEntry(path, project, ArtifactKind.Partial, ArtifactState.Partial);
                    var files = candidateOnly ? new[] { path } : new[] { metadata, part, metadata + ".next" }.Where(paths.Contains).ToArray();
                    foreach (var file in files)
                    {
                        claimed.Add(file);
                    }

                    var valid = Attempt(entry, () =>
                    {
                        // 先记录验证输入，再读取；统计跨段时不得把后来替换的 next 当作已验证输入。
                        Remember(metadata);
                        Remember(part);
                        Remember(metadata + ".next");
                        var record = _store.Read(path);
                        _store.ValidatePartial(record, metadata, project);
                        if (!candidateOnly)
                        {
                            _store.ValidatePartialFiles(metadata, record, project);
                        }

                        entry.Digest = (string)record["CatalogDigest"];
                    });
                    foreach (var file in files)
                    {
                        if (!Attempt(entry, () => AddBytes(entry, file)))
                        {
                            valid = false;
                        }
                        yield return null;
                    }
                    entry.IsEligible = valid;
                }
                foreach (var path in paths)
                {
                    if (!claimed.Contains(path))
                    {
                        AddUnknown(path, project);
                    }
                    yield return null;
                }
            }

            /// <summary>复制扫描中间数据，仅为 Ready 且归属、大小及保护判断合格的条目标记候选。</summary>
            internal ArtifactStoreReport Report(ArtifactQueryStatus status) => new(_store, _started, status,
                _entries.Select(x =>
                {
                    _protections.TryGetValue(x.Path, out var protection);
                    return new ArtifactEntry(x.Path, x.Project, x.Digest, x.Kind, x.State, protection, x.Bytes, x.IsSizeComplete,
                        status == ArtifactQueryStatus.Ready && x.IsEligible && x.IsSizeComplete && protection == ArtifactProtection.None, x.Issues);
                }), _prepared, _protections.Select(x => new ArtifactVersionProtection(x.Key, x.Value)), Operations, Issues);
        }
    }
}
