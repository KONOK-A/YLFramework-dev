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
    /// <summary>Explicit application startup gate for one shipped, immutable Addressables catalog.</summary>
    public sealed class EncryptedDeliverySession
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
        private bool _closed;

        private EncryptedDeliverySession(EncryptedBundleDescription description, EncryptedBundleCache cache,
            IEncryptedBundleKeySource keys, EncryptedAssetBundleProvider provider, IResourceLocator[] locators)
        {
            Cache = cache;
            Keys = keys;
            Provider = provider;
            _locators = locators;
            _providers = Addressables.ResourceManager.ResourceProviders.ToArray();
            _providerIds = new HashSet<string>(_providers.Select(candidate => candidate.ProviderId), StringComparer.Ordinal);
            _providerId = provider.ProviderId;
            _thread = Environment.CurrentManagedThreadId;
            _transfers = description.Bundles.ToDictionary(item => item.Binding.Url, StringComparer.Ordinal);
            _mounts = description.Bundles.ToDictionary(item => FrozenAddressablesRoutes.MountId(item.Binding), StringComparer.Ordinal);
            _transform = Transform;
        }

        internal EncryptedBundleCache Cache { get; }
        internal IEncryptedBundleKeySource Keys { get; }
        internal EncryptedAssetBundleProvider Provider { get; }

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
                    var providerId = description.Bundles[0].Binding.ProviderId;
                    var candidates = Addressables.ResourceManager.ResourceProviders.Where(provider => provider.ProviderId == providerId).ToArray();
                    if (candidates.Length != 1 || !(candidates[0] is EncryptedAssetBundleProvider correct))
                        throw new InvalidDataException("The expected protected Bundle Provider was not registered by the shipped catalog.");
                    FrozenAddressablesRoutes.CheckResolved(locator, description);
                    var providers = Addressables.ResourceManager.ResourceProviders.ToArray();
                    cache = await EncryptedBundleCache.OpenAsync(dedicatedCipherCache, description, token);
                    token.ThrowIfCancellationRequested();
                    var session = new EncryptedDeliverySession(description, cache, keys, correct, locators);
                    if (Addressables.InternalIdTransformFunc != null || Addressables.WebRequestOverride != null ||
                        !Addressables.ResourceLocators.SequenceEqual(locators) || !Addressables.ResourceManager.ResourceProviders.SequenceEqual(providers) ||
                        !SameLocalPath(Addressables.ResolveInternalId(FrozenAddressablesRoutes.RuntimeToken + "/" + description.CatalogName), expectedCatalog))
                        throw new InvalidDataException("Addressables settings changed during startup.");
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
            internal EncryptedBundleDescription Description { get; private set; }

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
                try { _catalog?.Dispose(); }
                finally
                {
                    try { _settings?.Dispose(); }
                    finally
                    {
                        try { _description?.Dispose(); }
                        finally { _root?.Dispose(); }
                    }
                }
            }
        }

        internal static string ReadText(FileStream file)
        {
            if (file.Length > EncryptedBundleDescription.MaximumBytes) throw new InvalidDataException("Local metadata exceeds the supported size.");
            using var text = new StreamReader(file, new UTF8Encoding(false, true), false, 4096, true);
            return text.ReadToEnd();
        }

        internal void CheckUnchanged()
        {
            if (Environment.CurrentManagedThreadId != _thread || _closed || Addressables.InternalIdTransformFunc != _transform ||
                Addressables.WebRequestOverride != null || !Addressables.ResourceLocators.SequenceEqual(_locators) ||
                !Addressables.ResourceManager.ResourceProviders.Where(candidate => _providerIds.Contains(candidate.ProviderId)).SequenceEqual(_providers))
                throw new InvalidOperationException("The protected Addressables session must not change after startup; use a new Player and catalog.");
        }

        internal EncryptedBundleRecord ResolveTransfer(IResourceLocation location)
        {
            if (location.ProviderId != _providerId || !_transfers.TryGetValue(location.InternalId, out var record) ||
                !(location.Data is AssetBundleRequestOptions options) || options.BundleName != record.Binding.BundleName ||
                options.BundleSize != record.CipherLength || !FrozenAddressablesRoutes.NoUnityCacheVersion(options.Hash))
                throw new InvalidDataException("Unverified protected transfer route.");
            return record;
        }

        internal EncryptedBundleRecord ResolveMount(IResourceLocation location) => location.ProviderId == _providerId &&
            _mounts.TryGetValue(location.InternalId, out var record) ? record : throw new InvalidDataException("Unverified protected mount route.");

        private string Transform(IResourceLocation location)
        {
            if (location.ProviderId != _providerId || location.ResourceType != typeof(IAssetBundleResource)) return location.InternalId;
            CheckUnchanged();
            return Cache.TryGetVerifiedPath(ResolveTransfer(location), out string path) ? path : location.InternalId;
        }

        /// <summary>Close only after all direct and YL resource handles have been released. Does not unload assets owned by anyone else.</summary>
        public Task CloseAsync()
        {
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Close encrypted delivery on the Unity thread.");
            if (_closed) return Cache.CloseAsync();
            if (Provider.Pending != 0 || Provider.Mounted != 0 || Provider.Unloading != 0 || Provider.UnloadFailure != null)
                throw new InvalidOperationException("Release all protected Bundle owners and wait for native unload before closing delivery.");
            _closed = true;
            if (Addressables.InternalIdTransformFunc == _transform) Addressables.InternalIdTransformFunc = null;
            Provider.Detach();
            return Cache.CloseAsync();
        }
    }
}
