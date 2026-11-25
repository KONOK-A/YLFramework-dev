using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace YLFramework.Resource.Encryption
{
    /// <summary>Explicit application startup gate for one immutable content version and its Addressables catalog.</summary>
    public sealed class EncryptedDeliverySession : IEncryptedBundleSession
    {
        private static bool _attempted;
        private readonly Dictionary<string, EncryptedBundleRecord> _transfers;
        private readonly Dictionary<string, EncryptedBundleRecord> _mounts;
        private readonly Func<IResourceLocation, string> _transform;
        private readonly IResourceLocator[] _locators;
        private readonly IResourceProvider[] _providers;
        private readonly HashSet<string> _providerIds;
        private readonly string _providerId;
        private readonly int _thread;
        private readonly EncryptedBundleDescription _description;
        private readonly string _versionContentRoot;
        private readonly string _cacheRoot;
        private readonly CancellationTokenSource _remoteLifetime;
        private Task _remotePreparation;
        private Task _closing;
        private long _embeddedCipherHashBytes;

        private EncryptedDeliverySession(EncryptedBundleDescription description, string versionContentRoot, string cacheRoot,
            EncryptedBundleCache cache, IEncryptedBundleKeySource keys, EncryptedAssetBundleProvider provider, IResourceLocator[] locators)
        {
            _description = description;
            _versionContentRoot = versionContentRoot;
            _cacheRoot = cacheRoot;
            _remoteLifetime = description.ReleasePrefix != null ? new CancellationTokenSource() : null;
            Cache = cache;
            Keys = keys;
            Provider = provider;
            _locators = locators;
            _providers = Addressables.ResourceManager.ResourceProviders.ToArray();
            _providerIds = new HashSet<string>(_providers.Select(candidate => candidate.ProviderId), StringComparer.Ordinal);
            _providerId = provider.ProviderId;
            _thread = Environment.CurrentManagedThreadId;
            _transfers = description.Bundles.ToDictionary(item => ResolveSourceId(item.Binding.SourceId), StringComparer.Ordinal);
            _mounts = description.Bundles.ToDictionary(item => FrozenAddressablesRoutes.MountId(item.Binding), StringComparer.Ordinal);
            _transform = Transform;
        }

        internal EncryptedBundleCache Cache
        {
            get; private set;
        }
        internal long EmbeddedCipherHashBytes => Interlocked.Read(ref _embeddedCipherHashBytes);

        /// <summary>Whether the dedicated remote cache is ready for protected downloads and loads in this session.</summary>
        public bool IsRemoteReady => Cache != null && _closing == null;
        internal IEncryptedBundleKeySource Keys
        {
            get;
        }
        internal EncryptedAssetBundleProvider Provider
        {
            get;
        }

        /// <summary>Call on Unity's main thread before publishing any YL or official Addressables resource consumer.</summary>
        public static Task<EncryptedDeliverySession> StartAsync(string dedicatedCipherCache, IEncryptedBundleKeySource keys,
            CancellationToken token = default) => StartCoreAsync(dedicatedCipherCache, keys, false, token);

        internal static async Task<EncryptedDeliverySession> StartCoreAsync(string dedicatedCipherCache, IEncryptedBundleKeySource keys,
            bool allowLoopback, CancellationToken token = default)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
                Debug.isDebugBuild || SynchronizationContext.Current == null)
                throw new PlatformNotSupportedException("Encrypted delivery starts only in Win64 Release Player on the Unity thread.");
#if !ENABLE_IL2CPP && !ENABLE_MONO
            throw new PlatformNotSupportedException("Encrypted delivery requires Mono or IL2CPP.");
#else
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (_attempted || Addressables.ResourceLocators.Any() || Addressables.InternalIdTransformFunc != null || Addressables.WebRequestOverride != null)
                throw new InvalidOperationException("The application must validate encrypted delivery before Addressables startup, without other Locators or transforms.");
            RequireUninitializedProviders();
            _attempted = true;
            string root = OwnedWindowsFiles.LocalAbsolute(Path.Combine(Application.streamingAssetsPath, "aa"));
            if (!SameLocalPath(Addressables.RuntimePath, root)) throw new InvalidDataException("Addressables runtime path differs from the shipped directory.");
            EncryptedBundleCache cache = null;
            try
            {
                using var shipped = await Task.Run(() => ShippedFiles.Open(root, allowLoopback, token), token);
                var description = shipped.Description;
                if (Addressables.ResourceLocators.Any() || Addressables.InternalIdTransformFunc != null || Addressables.WebRequestOverride != null)
                    throw new InvalidOperationException("Addressables was used before startup validation finished.");
                RequireUninitializedProviders();
                string expectedCatalog = Path.Combine(root, description.CatalogName);
                if (!SameLocalPath(Addressables.ResolveInternalId(FrozenAddressablesRoutes.RuntimeToken + "/" + description.CatalogName), expectedCatalog))
                    throw new InvalidDataException("The default catalog runtime token resolves outside the exact shipped catalog path.");
                var handle = Addressables.InitializeAsync(false);
                try
                {
                    var locator = await handle.Task;
                    token.ThrowIfCancellationRequested();
                    var locators = FrozenAddressablesRoutes.CheckLocators(locator);
                    var providerId = description.ProviderId;
                    var candidates = Addressables.ResourceManager.ResourceProviders.Where(provider => provider.ProviderId == providerId).ToArray();
                    if (candidates.Length != 1 || !(candidates[0] is EncryptedAssetBundleProvider correct))
                        throw new InvalidDataException("The expected protected Bundle Provider was not registered by the shipped catalog.");
                    FrozenAddressablesRoutes.CheckResolved(locator, description);
                    var providers = Addressables.ResourceManager.ResourceProviders.ToArray();
                    if (description.Bundles.All(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps))
                        cache = await OpenRemoteAsync(dedicatedCipherCache, root, description, token);
                    token.ThrowIfCancellationRequested();
                    if (Addressables.InternalIdTransformFunc != null || Addressables.WebRequestOverride != null ||
                        !Addressables.ResourceLocators.SequenceEqual(locators) || !Addressables.ResourceManager.ResourceProviders.SequenceEqual(providers) ||
                        !SameLocalPath(Addressables.ResolveInternalId(FrozenAddressablesRoutes.RuntimeToken + "/" + description.CatalogName), expectedCatalog))
                        throw new InvalidDataException("Addressables settings changed during startup.");
                    var session = new EncryptedDeliverySession(description, root, dedicatedCipherCache, cache, keys, correct, locators);
                    correct.Attach(session);
                    Addressables.InternalIdTransformFunc = session._transform;
                    return session;
                }
                finally { Addressables.Release(handle); }
            }
            catch
            {
                if (cache != null) await cache.CloseAsync();
                throw;
            }
#endif
        }

        /// <summary>On the Unity thread, prepare the configured protected remote cache. Embedded content is already available.</summary>
        /// <remarks>Overlapping callers share preparation. Cancelling one wait leaves the shared work running.</remarks>
        public Task PrepareRemoteAsync(CancellationToken token = default) => PrepareRemoteAsync(token, null);

        // Allows the Player fixture to hold readiness before any filesystem work, without replacing the real cache.
        internal Task PrepareRemoteAsync(CancellationToken token, Task beforeCache)
        {
            CheckUnchanged();
            if (token.IsCancellationRequested)
                return Task.FromCanceled(token);
            if (_remoteLifetime == null)
                throw new InvalidOperationException("This content version does not configure protected remote Bundles.");
            if (Cache != null)
                return Task.CompletedTask;
            if (_remotePreparation == null || _remotePreparation.IsCompleted)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _remotePreparation = completion.Task;
                _ = _remotePreparation.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _ = PrepareRemoteCoreAsync(completion, beforeCache);
            }
            return token.CanBeCanceled ? EncryptedBundleCache.ObserveAsync(_remotePreparation, token) : _remotePreparation;
        }

        private async Task PrepareRemoteCoreAsync(TaskCompletionSource<bool> completion, Task beforeCache)
        {
            EncryptedBundleCache candidate = null;
            try
            {
                var token = _remoteLifetime.Token;
                if (beforeCache != null)
                    await EncryptedBundleCache.ObserveAsync(beforeCache, token);
                candidate = await OpenRemoteAsync(_cacheRoot, _versionContentRoot, _description, token);
                token.ThrowIfCancellationRequested();
                CheckUnchanged();
                Cache = candidate;
                completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                try
                {
                    if (candidate != null)
                        await candidate.CloseAsync();
                }
                catch (Exception cleanup) { error = new AggregateException("Remote cache preparation and release both failed.", error, cleanup); }
                completion.TrySetException(error);
            }
        }

        private static Task<EncryptedBundleCache> OpenRemoteAsync(string cacheRoot, string versionRoot,
            EncryptedBundleDescription description, CancellationToken token)
        {
            if (string.IsNullOrEmpty(cacheRoot))
                throw new ArgumentException("Protected remote Bundles require a dedicated cipher cache path.", nameof(cacheRoot));
            return EncryptedBundleCache.OpenAsync(cacheRoot, description, token, excludedRoot: versionRoot);
        }

        internal static EncryptedBundleDescription ReadShippedDescription(string root, bool allowLoopback, CancellationToken token)
        {
            using var files = ShippedFiles.Open(root, allowLoopback, token);
            return files.Description;
        }

        private static bool SameLocalPath(string actual, string expected) => string.Equals(OwnedWindowsFiles.LocalAbsolute(actual),
            OwnedWindowsFiles.LocalAbsolute(expected), StringComparison.OrdinalIgnoreCase);

        private static void RequireUninitializedProviders()
        {
            if (Addressables.ResourceManager.ResourceProviders.Count != 0)
                throw new InvalidOperationException("An Addressables provider already registered before the fixed startup gate.");
        }

        private sealed class ShippedFiles : IDisposable
        {
            private IDisposable _root;
            private FileStream _description, _settings, _catalog;
            internal EncryptedBundleDescription Description
            {
                get; private set;
            }

            internal static ShippedFiles Open(string root, bool allowLoopback, CancellationToken token)
            {
                var files = new ShippedFiles();
                try
                {
                    files._root = OwnedWindowsFiles.PinDirectory(root, false);
                    files._description = OwnedWindowsFiles.ReadPinned(Path.Combine(root, EncryptedBundleDescription.FileName));
                    var description = EncryptedBundleDescription.Parse(ReadText(files._description), allowLoopback);
                    files._settings = OwnedWindowsFiles.ReadPinned(Path.Combine(root, "settings.json"));
                    files._catalog = OwnedWindowsFiles.ReadPinned(Path.Combine(root, description.CatalogName));
                    if (AuthenticatedBundleStream.Hash(files._settings, token) != description.SettingsSha256 ||
                        AuthenticatedBundleStream.Hash(files._catalog, token) != description.CatalogSha256)
                        throw new InvalidDataException("The shipped settings or Catalog differs from the frozen delivery description.");
                    files._settings.Position = 0;
                    FrozenAddressablesRoutes.CheckRuntimeSettings(ReadText(files._settings), description.CatalogName);
                    files.Description = description;
                    return files;
                }
                catch { files.Dispose(); throw; }
            }

            public void Dispose()
            {
                try
                {
                    _catalog?.Dispose();
                }
                finally
                {
                    try
                    {
                        _settings?.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            _description?.Dispose();
                        }
                        finally { _root?.Dispose(); }
                    }
                }
            }
        }

        internal static string ReadText(FileStream file)
        {
            if (file.Length > EncryptedBundleDescription.MaximumBytes)
                throw new InvalidDataException("Local metadata exceeds the supported size.");
            using var text = new StreamReader(file, new UTF8Encoding(false, true), false, 4096, true);
            return text.ReadToEnd();
        }

        void IEncryptedBundleSession.CheckUnchanged() => CheckUnchanged();

        internal void CheckUnchanged()
        {
            if (Environment.CurrentManagedThreadId != _thread || _closing != null || Addressables.InternalIdTransformFunc != _transform ||
                Addressables.WebRequestOverride != null || !Addressables.ResourceLocators.SequenceEqual(_locators) ||
                !Addressables.ResourceManager.ResourceProviders.Where(candidate => _providerIds.Contains(candidate.ProviderId)).SequenceEqual(_providers))
                throw new InvalidOperationException("The selected protected content and Addressables configuration must remain fixed throughout the session.");
        }

        private string ResolveSourceId(string source) => source.StartsWith(EncryptedBundleBinding.EmbeddedPrefix, StringComparison.Ordinal)
            ? _versionContentRoot.Replace('\\', '/') + source.Substring(FrozenAddressablesRoutes.RuntimeToken.Length) : source;

        EncryptedBundleRecord IEncryptedBundleSession.ResolveTransfer(IResourceLocation location) => ResolveTransfer(location);

        internal EncryptedBundleRecord ResolveTransfer(IResourceLocation location)
        {
            if (location.ProviderId != _providerId || !_transfers.TryGetValue(ResolveSourceId(location.InternalId.Replace('\\', '/')), out var record) ||
                !(location.Data is AssetBundleRequestOptions options) || options.BundleName != record.Binding.BundleName ||
                options.BundleSize != record.CipherLength || !FrozenAddressablesRoutes.NoUnityCacheVersion(options.Hash))
                throw new InvalidDataException("Unverified protected transfer route.");
            return record;
        }

        EncryptedBundleRecord IEncryptedBundleSession.ResolveMount(IResourceLocation location) => ResolveMount(location);

        internal EncryptedBundleRecord ResolveMount(IResourceLocation location) => location.ProviderId == _providerId &&
            _mounts.TryGetValue(location.InternalId, out var record) ? record : throw new InvalidDataException("Unverified protected mount route.");

        private string Transform(IResourceLocation location)
        {
            if (location.ProviderId != _providerId || location.ResourceType != typeof(IAssetBundleResource))
                return location.InternalId;
            CheckUnchanged();
            var record = ResolveTransfer(location);
            if (record.Binding.Mode == EncryptedBundleDeliveryMode.Embedded)
                return EmbeddedPath(record);
            return Cache != null && Cache.TryGetVerifiedPath(record, out string path) ? path : record.Binding.SourceId;
        }

        private string EmbeddedPath(EncryptedBundleRecord record) => Path.Combine(_versionContentRoot,
            EncryptedBundleBinding.EmbeddedFolder, record.Binding.GroupGuid, record.Binding.RelativePath);

        Task IEncryptedBundleSession.EnsureCiphertextAsync(EncryptedBundleRecord record, Action<EncryptedBundleCache.TransferProgress> progress)
            => EnsureCiphertextAsync(record, progress);

        internal Task EnsureCiphertextAsync(EncryptedBundleRecord record, Action<EncryptedBundleCache.TransferProgress> progress)
        {
            if (record.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps)
                return RequireRemote().EnsureAsync(record, reportProgress: progress);
            progress(new EncryptedBundleCache.TransferProgress(0));
            return Task.Run(() =>
            {
                using var file = OpenEmbedded(record);
                RecordEmbeddedHash(AuthenticatedBundleStream.ValidateCipher(file, record, CancellationToken.None));
            });
        }

        Task<AuthenticatedBundleStream> IEncryptedBundleSession.OpenBundleAsync(EncryptedBundleRecord record,
            Action<EncryptedBundleCache.TransferProgress> progress) => OpenBundleAsync(record, progress);

        internal Task<AuthenticatedBundleStream> OpenBundleAsync(EncryptedBundleRecord record,
            Action<EncryptedBundleCache.TransferProgress> progress)
        {
            if (record.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps)
                return RequireRemote().OpenAuthenticatedAsync(record, Keys, reportProgress: progress);
            progress(new EncryptedBundleCache.TransferProgress(0));
            return Task.Run(() => AuthenticatedBundleStream.OpenVerified(OpenEmbedded(record), record, Keys,
                CancellationToken.None, RecordEmbeddedHash));
        }

        private void RecordEmbeddedHash(long bytes) => Interlocked.Add(ref _embeddedCipherHashBytes, bytes);

        private FileStream OpenEmbedded(EncryptedBundleRecord record)
        {
            try
            {
                return OwnedWindowsFiles.OpenRead(EmbeddedPath(record));
            }
            catch (IOException error) { throw new EncryptedBundleIntegrityException("The installed protected Bundle is missing or unreadable.", error); }
        }

        private EncryptedBundleCache RequireRemote() => Cache ?? throw new InvalidOperationException("The protected remote cache is not ready. Call PrepareRemoteAsync first.");

        /// <summary>After all protected handles are released, wait for native unload and key return before closing the cipher cache.</summary>
        /// <remarks>Call on the Unity thread. Outstanding owners or provider work are rejected; accepted calls share one terminal task.
        /// An observed native unload failure faults the task without releasing the potentially live stream or cache.
        /// If Unity never signals completion, the task stays pending.</remarks>
        public Task CloseAsync()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Close encrypted delivery on the Unity thread.");
            if (_closing != null)
                return _closing;
            if (Provider.Pending != 0 || Provider.HasOwners)
                throw new InvalidOperationException("Release all protected handles and let outstanding provider work settle before closing delivery.");

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _closing = completion.Task;
            _ = FinishCloseAsync(completion);
            return _closing;
        }

        private async Task FinishCloseAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                _remoteLifetime?.Cancel();
                if (_remotePreparation != null)
                {
                    try
                    {
                        await _remotePreparation;
                    }
                    catch (Exception) { /* The preparation task exposes its own failure. */ }
                }
                await Provider.DrainUnloadsAsync();
                if (Addressables.InternalIdTransformFunc == _transform)
                    Addressables.InternalIdTransformFunc = null;
                Provider.Detach();
                if (Cache != null)
                    await Cache.CloseAsync();
                _remoteLifetime?.Dispose();
                completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }
    }
}
