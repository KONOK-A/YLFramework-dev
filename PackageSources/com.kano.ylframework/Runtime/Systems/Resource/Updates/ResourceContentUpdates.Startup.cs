using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using Version = YLFramework.Resource.ResourceContentStore.Version;

namespace YLFramework.Resource
{
    public enum ResourceContentStartupStatus
    {
        Baseline,
        Active,
        Interrupted,
        Activated,
        ActivationRejected,
        RolledBack,
        RollbackRejected
    }

    /// <summary>本次启动开放的版本及候选被拒绝时的原因。</summary>
    public sealed class ResourceContentStartupResult
    {
        internal ResourceContentStartupResult(ResourceContentStartupStatus status, ResourceContentManifest manifest, Exception rejection)
        {
            Status = status;
            Manifest = manifest;
            Rejection = rejection;
        }

        public ResourceContentStartupStatus Status
        {
            get;
        }
        public ResourceContentManifest Manifest
        {
            get;
        }
        public Exception Rejection
        {
            get;
        }
    }

    public sealed partial class ResourceContentUpdates
    {
        private static bool _officialStartupClaimed;

        /// <summary>启动完成后报告本次所选内容。失败回退可以与资源服务正常就绪同时发生。</summary>
        public ResourceContentStartupResult LastStartup
        {
            get; private set;
        }

        internal static bool IsOfficialStartupClaimed => _officialStartupClaimed;
        internal void RequireFrozenRouting() => RequireServing().CheckUnchanged();

        internal async Task<Boot> BeginStartupAsync(CancellationToken cancellation)
        {
            ResourceSystem.CheckStartupThread();
            RequireUnstartedOfficial();
            var boot = new Boot(this);
            try
            {
                boot.InstallDir = ResourceContentFileIO.PinDirectory(_installRoot, false);
                boot.StoreDir = ResourceContentFileIO.PinDirectory(_store.Root, true);
                boot.Writer = AcquireWriter();
                boot.Original = boot.Writer.ReadState();
                var journal = boot.Writer.ReadJournal();

                boot.SettingsPin = ResourceContentFileIO.OpenRead(boot.SettingsPath);
                _installed = await Task.Run(() => ReadInstalled(boot, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                ResourceContentCatalog.RequireStartupSettings(boot.SettingsPin, _installed.CatalogRoute, _trust.Platform);

                var state = boot.Original;
                var installedVersion = VersionOf(_installed);
                if (state != null && _installed.Sequence > state.HighestSequence ||
                    state != null && installedVersion.Sequence == state.HighestSequence && !installedVersion.Equals(state.Highest))
                    throw new InvalidDataException("The installed baseline differs from the recorded application content history.");

                if (journal.Attempt != null)
                {
                    bool interrupted = journal.Attempt.Intent.Matches(state);
                    if (interrupted)
                        boot.Status = ResourceContentStartupStatus.Interrupted;
                    Version ready = journal.Ready;
                    if (ready != null && state != null && ready.Sequence <= state.HighestSequence)
                        ready = null;
                    journal = new ResourceContentStore.Journal(ready, null, null, null);
                    boot.Writer.WriteJournal(journal, cancellation);
                }

                var rollback = state != null && journal.Rollback?.Matches(state) == true &&
                    journal.Rollback.Target.Equals(state.Previous);
                var intent = rollback ? journal.Rollback : journal.Activation;
                if (intent != null && intent.Matches(state))
                {
                    var attempting = new ResourceContentStore.Attempt(intent, rollback);
                    boot.Writer.WriteJournal(new ResourceContentStore.Journal(journal.Ready, journal.Activation, journal.Rollback, attempting), cancellation);
                    try
                    {
                        var candidate = ReadVersion(intent.Target);
                        if (!rollback && candidate.CompareSequence(state.HighestSequence, state.HighestDigest) != ResourceContentSequenceComparison.New)
                            throw new ResourceException(ResourceError.StaleBase, "The pending release can no longer advance this installation.");
                        await boot.PreflightAsync(candidate, cancellation);
                        boot.Status = rollback ? ResourceContentStartupStatus.RolledBack : ResourceContentStartupStatus.Activated;
                        boot.Next = rollback
                            ? new ResourceContentStore.State(checked(state.Generation + 1), intent.Target, null, state.Highest)
                            : new ResourceContentStore.State(checked(state.Generation + 1), intent.Target, state.Active, intent.Target);
                        return boot;
                    }
                    catch (Exception error) when (IsCandidateRejection(error))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        boot.ReleaseSelection();
                        bool retry = !rollback && error is ResourceException resourceError &&
                            (resourceError.Kind == ResourceError.ContentKeyUnavailable || resourceError.Kind == ResourceError.ContentAuthenticationFailed);
                        var activation = retry ? intent : null;
                        boot.Writer.WriteJournal(new ResourceContentStore.Journal(journal.Ready, activation, null, null), cancellation);
                        boot.Status = rollback ? ResourceContentStartupStatus.RollbackRejected : ResourceContentStartupStatus.ActivationRejected;
                        boot.Rejection = error;
                    }
                }

                var current = state == null ? _installed : ReadVersion(state.Active);
                await boot.PreflightAsync(current, cancellation);
                if (state == null)
                {
                    boot.Status = ResourceContentStartupStatus.Baseline;
                    boot.Next = new ResourceContentStore.State(1, installedVersion, null, installedVersion);
                }
                return boot;
            }
            catch
            {
                boot.Dispose();
                throw;
            }
        }

        private ResourceContentManifest ReadInstalled(Boot boot, CancellationToken cancellation)
        {
            if (boot.SettingsPin.Length > 1024 * 1024 ||
                ResourceContentFileIO.Hash(boot.SettingsPin, cancellation) != _trust.SettingsSha256)
                throw new InvalidDataException("The installed Addressables settings differ from the Player trust configuration.");
            boot.SettingsPin.Position = 0;
            using var payload = ResourceContentFileIO.OpenRead(Path.Combine(_installRoot, BaselineManifest));
            using var signature = ResourceContentFileIO.OpenRead(Path.Combine(_installRoot, BaselineSignature));
            var manifest = ResourceContentManifest.Verify(ReadBounded(payload, ResourceContentManifest.MaximumPayloadBytes),
                ReadBounded(signature, ResourceContentManifest.MaximumSignatureEnvelopeBytes), _trust);
            var catalog = manifest.Artifacts[manifest.CatalogRoute];
            if (catalog.Source != InstalledPrefix + ResourceContentCatalog.CatalogName || catalog.OriginReleaseId != manifest.ReleaseId)
                throw new InvalidDataException("The sealed Player baseline must contain its original Catalog.");
            return manifest;
        }

        private ResourceContentManifest ReadVersion(Version target)
        {
            if (VersionOf(_installed).Equals(target))
                return _installed;

            using var directory = ResourceContentFileIO.PinDirectory(VersionsRoot, false);
            using var file = ResourceContentFileIO.OpenRead(AnnouncementPath(target.Digest));
            var manifest = ReadAnnouncement(ReadBounded(file, MaximumAnnouncementBytes));
            if (!VersionOf(manifest).Equals(target))
                throw new InvalidDataException("The signed local manifest differs from its committed identity.");
            return manifest;
        }

        private static bool IsCandidateRejection(Exception error) =>
            error is IOException || error is InvalidDataException || error is JsonException || error is FormatException ||
            error is InvalidOperationException || error is ArgumentException || error is ResourceException;

        private static void RequireUnstartedOfficial()
        {
            if (_officialStartupClaimed || Addressables.InternalIdTransformFunc != null || Addressables.WebRequestOverride != null ||
                Addressables.ResourceLocators.Any() || Addressables.ResourceManager.ResourceProviders.Count != 0)
                throw new ResourceException(ResourceError.ConfigurationConflict,
                    "A signed content session must own the first Addressables initialization in this process.");
        }

        /// <summary>清理可确认归属的暂存文件及无引用缓存，返回实际移除的文件数。</summary>
        public Task<int> CleanupAsync(CancellationToken cancellation = default) => Task.Run(() =>
        {
            var serving = RequireServing();
            using var directory = ResourceContentFileIO.PinDirectory(_store.Root, false);
            using var writer = AcquireWriter();
            var state = RequireState(writer);
            var journal = writer.ReadJournal();
            var retain = new HashSet<Version> { state.Active, VersionOf(serving.Manifest) };
            if (state.Previous != null)
                retain.Add(state.Previous);
            if (journal.Ready != null && journal.Ready.Sequence > state.HighestSequence)
                retain.Add(journal.Ready);

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in retain)
            {
                var manifest = VersionOf(serving.Manifest).Equals(version) ? serving.Manifest : ReadVersion(version);
                referenced.Add(AnnouncementPath(version.Digest));
                foreach (var artifact in manifest.Artifacts.Values.Where(artifact =>
                             !artifact.Source.StartsWith(InstalledPrefix, StringComparison.Ordinal)))
                    referenced.Add(ObjectPath(artifact));
            }

            return Clean(ObjectsRoot, false) + Clean(VersionsRoot, true) + writer.CleanupTemporaryRecords(cancellation);

            int Clean(string root, bool versions)
            {
                if (!Directory.Exists(root))
                    return 0;
                using var pinned = ResourceContentFileIO.PinDirectory(root, false);
                int removed = 0;
                foreach (string path in Directory.EnumerateFiles(root))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (referenced.Contains(path))
                        continue;
                    string name = Path.GetFileName(path);
                    string suffix = Path.GetExtension(name);
                    string digest = Path.GetFileNameWithoutExtension(name);
                    bool stage = name.StartsWith(".stage-", StringComparison.Ordinal) &&
                        Guid.TryParseExact(name.Substring(".stage-".Length), "N", out _);
                    bool validKind = versions ? suffix == ".json" :
                        suffix == ".json" || suffix == ".bin" || suffix == ".bundle" || suffix == ".cipher";
                    if (!stage && (!validKind || !ResourceContentStore.IsDigest(digest)))
                        continue;
                    try
                    {
                        using (var input = ResourceContentFileIO.OpenRead(path))
                        {
                            if (!stage && !IsOwned(input, digest, versions, cancellation))
                                continue;
                        }
                        File.Delete(path);
                        removed++;
                    }
                    catch (IOException error) when (error is FileNotFoundException || (error.HResult & 0xffff) == 32 ||
                        (error.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode == 32)
                    {
                        // 另一个会话在使用的文件保持原位。
                    }
                }
                return removed;
            }
        }, cancellation);

        private bool IsOwned(FileStream input, string digest, bool version, CancellationToken cancellation)
        {
            if (!version)
                return ResourceContentFileIO.Hash(input, cancellation) == digest;
            try
            {
                return ReadAnnouncement(ReadBounded(input, MaximumAnnouncementBytes)).Digest == digest;
            }
            catch (Exception error) when (IsCandidateRejection(error))
            {
                return false;
            }
        }

        internal sealed class Boot : IDisposable
        {
            private readonly ResourceContentUpdates _owner;
            private ResourceContentCatalog _catalog;
            private IResourceContentExtensionSession _extensionSession;
            private Func<IResourceLocation, string> _transform;
            private bool _committed;
            private bool _selectedCatalogRead;
            private IResourceLocator[] _registeredLocators;
            private IResourceProvider[] _registeredProviders;

            internal Boot(ResourceContentUpdates owner)
            {
                _owner = owner;
            }

            internal IDisposable InstallDir
            {
                get; set;
            }
            internal IDisposable StoreDir
            {
                get; set;
            }
            internal FileStream SettingsPin
            {
                get; set;
            }
            internal ResourceContentStore.Writer Writer
            {
                get; set;
            }
            internal ResourceContentStore.State Original
            {
                get; set;
            }
            internal ResourceContentStore.State Next
            {
                get; set;
            }
            internal ResourceContentStartupStatus Status { get; set; } = ResourceContentStartupStatus.Active;
            internal Exception Rejection
            {
                get; set;
            }
            internal ResourceContentFiles Files
            {
                get; private set;
            }
            internal ResourceContentManifest Manifest
            {
                get; private set;
            }
            internal string SettingsPath => Path.Combine(_owner._installRoot, SettingsName);
            internal bool IsCommitted => _committed;

            internal async Task PreflightAsync(ResourceContentManifest manifest, CancellationToken cancellation)
            {
                _owner.RequireExtension(manifest);
                Manifest = manifest;
                Files = new ResourceContentFiles(SettingsPath);
                var installed = _owner._installed;
                bool isBaseline = installed.Digest == manifest.Digest;
                string installedCatalogPath = _owner.ResolveFile(installed.Artifacts[installed.CatalogRoute], installed);
                using var baselinePin = ResourceContentFileIO.OpenRead(installedCatalogPath);
                await Task.Run(() =>
                {
                    var required = new HashSet<string>(manifest.StartupRoutes, StringComparer.Ordinal);
                    foreach (var artifact in manifest.Artifacts.Values)
                    {
                        var path = _owner.ResolveFile(artifact, installed);
                        if (required.Contains(artifact.Route))
                        {
                            Files.Pin(artifact, path, cancellation);
                        }
                        else if (File.Exists(path))
                        {
                            try
                            {
                                Files.Pin(artifact, path, cancellation);
                            }
                            catch (Exception error) when (error is InvalidDataException || error is IOException)
                            {
                                // 可选章节可稍后重新准备，启动仍有完整的必需闭包。
                            }
                        }
                    }
                    if (!isBaseline)
                        ResourceContentFileIO.RequireIdentity(baselinePin, installed.Artifacts[installed.CatalogRoute].Length,
                            installed.Artifacts[installed.CatalogRoute].Sha256, cancellation);
                }, cancellation);
                cancellation.ThrowIfCancellationRequested();

                _catalog = ResourceContentCatalog.ReadLocal(Files.GetPath(manifest.CatalogRoute));
                if (manifest.DescriptionRoute != null)
                {
                    _extensionSession = await _owner._extension.VerifyStartupAsync(manifest, Files, _catalog.Locator, cancellation);
                    if (_extensionSession == null)
                        throw new InvalidDataException("The content extension did not verify the selected release.");
                }

                _catalog.Inspect(_owner._installRoot, ExtensionRoute);
                _catalog.RequireManifest(manifest);
                if (!isBaseline)
                {
                    var baseline = ResourceContentCatalog.ReadLocal(installedCatalogPath);
                    _catalog.RequireCompatibleWith(baseline, _owner._additionalAssetTypes);
                }
            }

            private string ExtensionRoute(IResourceLocation location) => _extensionSession?.GetCiphertextRoute(location);

            internal void InstallRouting()
            {
                ResourceSystem.CheckStartupThread();
                RequireUnstartedOfficial();
                if (!SamePath(Addressables.RuntimePath, _owner._installRoot))
                    throw new ResourceException(ResourceError.ConfigurationConflict, "Addressables does not point at the installed startup configuration.");
                _transform = Transform;
                Addressables.InternalIdTransformFunc = _transform;
                _officialStartupClaimed = true;
            }

            internal void ValidateOfficial(IResourceLocator locator)
            {
                CheckUnchanged();
                if (!_selectedCatalogRead)
                    throw new InvalidDataException("Official initialization did not read the verified Catalog entry.");
                _catalog.RequireOfficialLocator(locator, _owner._installRoot, ExtensionRoute);
                _registeredLocators = Addressables.ResourceLocators.ToArray();
                _extensionSession?.Attach(locator, CheckUnchanged);
            }

            internal void Commit(CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                CheckUnchanged();
                _registeredProviders = Addressables.ResourceManager.ResourceProviders.ToArray();
                if (Next != null && !Writer.TryCommit(Original?.Generation ?? 0, Next, cancellation))
                    throw new ResourceException(ResourceError.StaleBase, "The committed content state changed during startup.");

                _committed = true;
                _owner.LastStartup = new ResourceContentStartupResult(Status, Manifest, Rejection);
                Volatile.Write(ref _owner._serving, this);
                Writer.Dispose();
                Writer = null;
            }

            internal long GetDownloadSize(IEnumerable<string> keys)
            {
                var counted = Files.SnapshotVerifiedPaths();
                long bytes = 0;
                foreach (var route in GetArtifactRoutes(keys))
                {
                    var artifact = Manifest.Artifacts[route];
                    if (!artifact.Source.StartsWith(InstalledPrefix, StringComparison.Ordinal) && counted.Add(_owner.ObjectPath(artifact)))
                        bytes = checked(bytes + artifact.Length);
                }
                return bytes;
            }

            internal void RequirePreparedDependencies(IEnumerable<string> keys)
            {
                foreach (var route in GetArtifactRoutes(keys))
                    Files.GetPath(route);
            }

            private HashSet<string> GetArtifactRoutes(IEnumerable<string> keys)
            {
                var pending = new Stack<IResourceLocation>();
                foreach (var key in keys)
                {
                    var found = false;
                    foreach (var locator in _registeredLocators)
                    {
                        if (!locator.Locate(key, typeof(object), out var locations) || locations.Count == 0)
                            continue;
                        found = true;
                        foreach (var location in locations)
                            pending.Push(location);
                    }
                    if (!found)
                        throw new ResourceException(ResourceError.AddressNotFound, "No signed resource location exists for: " + key);
                }

                var visited = new HashSet<IResourceLocation>();
                var roots = new HashSet<string>(StringComparer.Ordinal);
                while (pending.Count != 0)
                {
                    var location = pending.Pop();
                    if (!visited.Add(location))
                        continue;
                    var route = ExtensionRoute(location);
                    if (route == null && location.ResourceType == typeof(IAssetBundleResource) &&
                        ResourceContentCatalog.IsStockId(location.ProviderId, typeof(AssetBundleProvider)))
                        route = ResourceContentCatalog.CanonicalId(location.InternalId, _owner._installRoot);
                    if (route != null)
                        roots.Add(route);
                    if (location.HasDependencies)
                        foreach (var dependency in location.Dependencies)
                            pending.Push(dependency);
                }
                return ExpandArtifactRoutes(Manifest, roots);
            }

            private string Transform(IResourceLocation location)
            {
                if (_committed)
                    CheckUnchanged();
                if (location.ResourceType == typeof(ResourceManagerRuntimeData) &&
                    location.ProviderId == typeof(JsonAssetProvider).FullName && SamePath(location.InternalId, SettingsPath))
                    return SettingsPath;

                if (location.ResourceType == typeof(ContentCatalogData))
                {
                    var catalogPath = Files.GetPath(Manifest.CatalogRoute);
                    if (location.ProviderId == typeof(ContentCatalogProvider).FullName && location.PrimaryKey == ResourceContentCatalog.CatalogId &&
                        ResourceContentCatalog.CanonicalId(location.InternalId, _owner._installRoot) == Manifest.CatalogRoute)
                    {
                        _selectedCatalogRead = true;
                        return catalogPath;
                    }
                    if (_selectedCatalogRead && SamePath(location.InternalId, catalogPath) &&
                        (location.ProviderId == typeof(JsonAssetProvider).FullName ||
                        location.ProviderId.StartsWith("UnityEngine.ResourceManagement.ResourceProviders.BinaryAssetProvider", StringComparison.Ordinal)))
                        return catalogPath;
                    throw new InvalidDataException("A Catalog read attempted to leave the frozen signed route.");
                }

                if (_extensionSession != null && _extensionSession.TryTransform(location, out var extended))
                    return extended;

                if (location.ResourceType == typeof(IAssetBundleResource) &&
                    ResourceContentCatalog.IsStockId(location.ProviderId, typeof(AssetBundleProvider)))
                {
                    var route = ResourceContentCatalog.CanonicalId(location.InternalId, _owner._installRoot);
                    if (!Manifest.Artifacts.TryGetValue(route, out var artifact) || artifact.Kind != ResourceContentArtifactKind.Bundle)
                        throw new InvalidDataException("An ordinary Bundle route is absent from the signed content manifest.");
                    // 官方在启动依赖之前已登记父操作。空路径让缺少本地文件的操作正常失败并释放缓存键。
                    return Files.TryGetPath(route, out var verified) ? verified : string.Empty;
                }

                if (location.ResourceType == typeof(SceneInstance))
                    return _catalog.GetScenePath(location, _owner._installRoot);

                if (location.ProviderId == typeof(AtlasSpriteProvider).FullName)
                    return _catalog.GetAtlasSpriteId(location, _owner._installRoot, Addressables.ResourceManager);

                if (ResourceContentCatalog.IsStockId(location.ProviderId, typeof(BundledAssetProvider)) ||
                    location.ProviderId == "UnityEngine.ResourceManagement.ResourceProviders.LegacyResourcesProvider")
                    return location.InternalId;

                throw new InvalidDataException("A provider attempted to resolve a route outside the frozen content session.");
            }

            private static bool SamePath(string candidate, string path)
            {
                if (string.IsNullOrEmpty(candidate) || !Path.IsPathRooted(candidate))
                    return false;
                return string.Equals(Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            }

            internal void CheckUnchanged()
            {
                if (!ReferenceEquals(Addressables.InternalIdTransformFunc, _transform) || Addressables.WebRequestOverride != null ||
                    (_registeredLocators != null && !Addressables.ResourceLocators.SequenceEqual(_registeredLocators)) ||
                    (_registeredProviders != null && !Addressables.ResourceManager.ResourceProviders.SequenceEqual(_registeredProviders)))
                    throw new ResourceException(ResourceError.ConfigurationConflict, "Another Addressables owner changed the frozen content session.");
            }

            internal void ReleaseSelection()
            {
                try
                {
                    _extensionSession?.Dispose();
                }
                finally
                {
                    _extensionSession = null;
                    Files?.Close();
                    Files = null;
                    _catalog = null;
                    Manifest = null;
                }
            }

            public void Dispose()
            {
                if (_committed)
                    return;
                if (_transform != null && ReferenceEquals(Addressables.InternalIdTransformFunc, _transform))
                    Addressables.InternalIdTransformFunc = _ => throw new ResourceException(ResourceError.SessionUnavailable,
                        "Signed content initialization failed. Relaunch before opening a resource session.");
                try
                {
                    ReleaseSelection();
                }
                finally
                {
                    Writer?.Dispose();
                    SettingsPin?.Dispose();
                    StoreDir?.Dispose();
                    InstallDir?.Dispose();
                }
            }
        }
    }
}
