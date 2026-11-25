using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace YLFramework.Resource.Encryption
{
    /// <summary>Authenticates protected Bundles for the version selected by Resource startup.</summary>
    public sealed class EncryptedResourceContentExtension : IResourceContentExtension, IResourceContentCiphertextTransfer
    {
        private static readonly Lazy<HttpClient> SignedTransfers = new Lazy<HttpClient>(() => EncryptedBundleCache.CreateCiphertextHttpClient(useProxy: true));
        private static readonly TimeSpan SignedInactivity = TimeSpan.FromMinutes(5);
        private readonly IEncryptedBundleKeySource _keys;
        private readonly bool _allowLoopback;
        private long _keyRents;
        private long _keyReturns;
        private long _authenticatedBundles;
        private long _mountAttempts;

        /// <summary>Uses the application's local content keys for each attempted startup.</summary>
        public EncryptedResourceContentExtension(IEncryptedBundleKeySource keys) : this(keys, false)
        {
        }

        internal EncryptedResourceContentExtension(IEncryptedBundleKeySource keys, bool allowLoopback)
        {
            _keys = keys ?? throw new ArgumentNullException(nameof(keys));
            _allowLoopback = allowLoopback;
        }

        /// <summary>Successful application key leases across attempted startups using this instance.</summary>
        public long KeyRents => Interlocked.Read(ref _keyRents);

        /// <summary>Application key leases returned across attempted startups using this instance.</summary>
        public long KeyReturns => Interlocked.Read(ref _keyReturns);

        /// <summary>Startup ciphertexts whose full plaintext authenticated across attempted startups.</summary>
        public long AuthenticatedBundles => Interlocked.Read(ref _authenticatedBundles);

        /// <summary>Native Bundle creation attempts by the attached Provider.</summary>
        public long MountAttempts => Interlocked.Read(ref _mountAttempts);

        async Task IResourceContentCiphertextTransfer.ReceiveCiphertextAsync(ResourceContentArtifact artifact,
            Func<ReadOnlyMemory<byte>, CancellationToken, Task> accept, CancellationToken cancellation)
        {
            if (artifact == null || artifact.Kind != ResourceContentArtifactKind.Ciphertext || artifact.Length <= 0 ||
                !Uri.TryCreate(artifact.Source, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !(_allowLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1")) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsoluteUri != artifact.Source)
                throw new ArgumentException("Expected a signed remote ciphertext artifact.", nameof(artifact));
            if (accept == null)
                throw new ArgumentNullException(nameof(accept));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(SignedInactivity);
            try
            {
                using var response = await EncryptedBundleCache.GetCompleteCiphertextAsync(SignedTransfers.Value, artifact.Source,
                    artifact.Length, deadline.Token).ConfigureAwait(false);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var buffer = new byte[128 * 1024];
                long remaining = artifact.Length;
                while (true)
                {
                    int take = remaining >= buffer.Length ? buffer.Length : checked((int)remaining + 1);
                    int count = await EncryptedBundleCache.ReadCiphertextAsync(input, buffer, take, deadline, SignedInactivity).ConfigureAwait(false);
                    if (count == 0 && remaining == 0)
                        return;
                    if (count == 0 || count > remaining)
                        throw new InvalidDataException("Encrypted Bundle response differs from the signed ciphertext length.");
                    cancellation.ThrowIfCancellationRequested();
                    await accept(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    remaining -= count;
                }
            }
            catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
            {
                throw new IOException("Signed ciphertext download timed out.", error);
            }
            catch (HttpRequestException error)
            {
                throw new IOException("Signed ciphertext network failure.", error);
            }
        }

        /// <inheritdoc />
        public async Task<IResourceContentExtensionSession> VerifyStartupAsync(ResourceContentManifest manifest,
            ResourceContentFiles files, IResourceLocator locator, CancellationToken cancellation)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
                Debug.isDebugBuild || SynchronizationContext.Current == null)
            {
                throw new PlatformNotSupportedException("Encrypted content startup requires a Win64 Release Player on the Unity thread.");
            }
#if !ENABLE_IL2CPP && !ENABLE_MONO
            throw new PlatformNotSupportedException("Encrypted content startup requires Mono or IL2CPP.");
#else
            var description = await Task.Run(() => ReadDescription(manifest, files, cancellation), cancellation);
            cancellation.ThrowIfCancellationRequested();
            FrozenAddressablesRoutes.CheckCatalog(locator, description);
            var keys = new EncryptedBundleKeySnapshot();
            try
            {
                await Task.Run(() => AuthenticateStartup(manifest, files, description, keys, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                return new Session(files, description, keys, () => Interlocked.Increment(ref _mountAttempts));
            }
            catch (Exception error)
            {
                keys.Dispose();
                if (error is EncryptedBundleKeyUnavailableException)
                {
                    throw new ResourceException(ResourceError.ContentKeyUnavailable, error.Message, error);
                }
                if (error is EncryptedBundleAuthenticationException)
                {
                    throw new ResourceException(ResourceError.ContentAuthenticationFailed, error.Message, error);
                }
                throw;
            }
#endif
        }

        private EncryptedBundleDescription ReadDescription(ResourceContentManifest manifest, ResourceContentFiles files,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using var input = files.OpenRead(manifest.DescriptionRoute);
            var description = EncryptedBundleDescription.Parse(EncryptedDeliverySession.ReadText(input), _allowLoopback);
            var catalog = manifest.Artifacts[manifest.CatalogRoute];
            if (description.Application != manifest.Application || manifest.Platform != EncryptedBundleBinding.Platform ||
                description.CatalogName != Path.GetFileName(catalog.Route) || description.CatalogSha256 != catalog.Sha256 ||
                description.SettingsSha256 != manifest.SettingsSha256)
            {
                throw new InvalidDataException("The encrypted description does not match the signed application, Catalog or settings.");
            }

            if (manifest.Artifacts.Values.Count(artifact => artifact.Kind == ResourceContentArtifactKind.Ciphertext) != description.Bundles.Count)
            {
                throw new InvalidDataException("The encrypted description does not match the signed ciphertext inventory.");
            }
            foreach (var record in description.Bundles)
            {
                var binding = record.Binding;
                if (!manifest.Artifacts.TryGetValue(binding.SourceId, out var artifact) || artifact.Kind != ResourceContentArtifactKind.Ciphertext ||
                    artifact.OriginReleaseId != binding.ReleaseId ||
                    artifact.Length != record.CipherLength || artifact.Sha256 != record.CipherSha256)
                {
                    throw new InvalidDataException("A protected transfer differs from its signed ciphertext binding.");
                }
            }
            return description;
        }

        private void AuthenticateStartup(ResourceContentManifest manifest, ResourceContentFiles files,
            EncryptedBundleDescription description, EncryptedBundleKeySnapshot keys, CancellationToken cancellation)
        {
            var startupRoutes = new HashSet<string>(manifest.StartupRoutes, StringComparer.Ordinal);
            var required = description.Bundles.Where(record => startupRoutes.Contains(record.Binding.SourceId)).ToArray();
            Action rented = () => Interlocked.Increment(ref _keyRents);
            Action returned = () => Interlocked.Increment(ref _keyReturns);
            foreach (var record in required)
            {
                cancellation.ThrowIfCancellationRequested();
                var keyId = record.Binding.KeyId;
                if (!keys.Capture(_keys, keyId, rented, returned))
                {
                    throw new EncryptedBundleKeyUnavailableException(keyId);
                }
                using (AuthenticatedBundleStream.OpenVerified(files.OpenRead(record.Binding.SourceId), record, keys, cancellation))
                {
                    Interlocked.Increment(ref _authenticatedBundles);
                }
            }

            // 可选文件由同一版本准备。提前取得可用的钥快照，缺少仅用于可选内容的钥不阻止启动。
            var requiredKeyIds = new HashSet<string>(required.Select(record => record.Binding.KeyId), StringComparer.Ordinal);
            foreach (string keyId in description.Bundles.Select(record => record.Binding.KeyId).Distinct(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!requiredKeyIds.Contains(keyId))
                {
                    keys.Capture(_keys, keyId, rented, returned);
                }
            }
        }

        private sealed class Session : IResourceContentExtensionSession, IEncryptedBundleSession
        {
            private readonly ResourceContentFiles _files;
            private readonly EncryptedBundleDescription _description;
            private readonly EncryptedBundleKeySnapshot _keys;
            private readonly Action _onMountAttempt;
            private readonly string _runtimePath;
            private readonly Dictionary<string, EncryptedBundleRecord> _transfers;
            private readonly Dictionary<string, EncryptedBundleRecord> _mounts;
            private EncryptedAssetBundleProvider _provider;
            private Action _checkUnchanged;
            private bool _disposed;

            internal Session(ResourceContentFiles files, EncryptedBundleDescription description, EncryptedBundleKeySnapshot keys,
                Action onMountAttempt)
            {
                _files = files;
                _description = description;
                _keys = keys;
                _onMountAttempt = onMountAttempt;
                _runtimePath = Addressables.RuntimePath;
                _transfers = description.Bundles.ToDictionary(record => record.Binding.SourceId, StringComparer.Ordinal);
                _mounts = description.Bundles.ToDictionary(record => FrozenAddressablesRoutes.MountId(record.Binding), StringComparer.Ordinal);
            }

            public string GetCiphertextRoute(IResourceLocation location)
            {
                CheckOpen();
                if (location.ProviderId != _description.ProviderId)
                {
                    return null;
                }
                var record = location.ResourceType == typeof(EncryptedMountedBundle)
                    ? ResolveMount(location) : ResolveTransfer(location);
                return record.Binding.SourceId;
            }

            public bool TryTransform(IResourceLocation location, out string internalId)
            {
                var route = GetCiphertextRoute(location);
                if (route == null)
                {
                    internalId = null;
                    return false;
                }
                internalId = location.ResourceType == typeof(EncryptedMountedBundle)
                    ? location.InternalId : _files.GetPath(route);
                return true;
            }

            public void Attach(IResourceLocator officialLocator, Action checkUnchanged)
            {
                CheckOpen();
                if (_provider != null)
                {
                    throw new InvalidOperationException("The encrypted content session is already attached.");
                }
                if (checkUnchanged == null)
                {
                    throw new ArgumentNullException(nameof(checkUnchanged));
                }
                FrozenAddressablesRoutes.CheckResolved(officialLocator, _description);
                var providers = Addressables.ResourceManager.ResourceProviders
                    .Where(candidate => candidate.ProviderId == _description.ProviderId).ToArray();
                if (providers.Length != 1 || !(providers[0] is EncryptedAssetBundleProvider provider))
                {
                    throw new InvalidDataException("The selected Catalog did not register its expected protected Bundle Provider.");
                }
                provider.Attach(this, _onMountAttempt);
                _provider = provider;
                _checkUnchanged = checkUnchanged;
            }

            public void CheckUnchanged()
            {
                CheckOpen();
                _checkUnchanged();
            }

            public EncryptedBundleRecord ResolveTransfer(IResourceLocation location)
            {
                if (location.ProviderId != _description.ProviderId || location.ResourceType != typeof(IAssetBundleResource) ||
                    !_transfers.TryGetValue(FrozenAddressablesRoutes.CanonicalId(location.InternalId, _runtimePath), out var record) ||
                    location.PrimaryKey != record.Binding.RelativePath || !(location.Data is AssetBundleRequestOptions options) ||
                    options.BundleName != record.Binding.BundleName || options.BundleSize != record.CipherLength ||
                    !FrozenAddressablesRoutes.NoUnityCacheVersion(options.Hash) || options.UseUnityWebRequestForLocalBundles)
                {
                    throw new InvalidDataException("Unverified protected transfer route.");
                }
                return record;
            }

            public EncryptedBundleRecord ResolveMount(IResourceLocation location)
            {
                if (location.ProviderId != _description.ProviderId || location.ResourceType != typeof(EncryptedMountedBundle) ||
                    !_mounts.TryGetValue(location.InternalId, out var record) || location.PrimaryKey != location.InternalId || location.Data != null)
                {
                    throw new InvalidDataException("Unverified protected mount route.");
                }
                return record;
            }

            public Task EnsureCiphertextAsync(EncryptedBundleRecord record, Action<EncryptedBundleCache.TransferProgress> progress)
            {
                progress(new EncryptedBundleCache.TransferProgress(0));
                return Task.Run(() =>
                {
                    using var file = _files.OpenRead(record.Binding.SourceId);
                    AuthenticatedBundleStream.ValidateCipher(file, record, CancellationToken.None);
                });
            }

            public Task<AuthenticatedBundleStream> OpenBundleAsync(EncryptedBundleRecord record,
                Action<EncryptedBundleCache.TransferProgress> progress)
            {
                progress(new EncryptedBundleCache.TransferProgress(0));
                return Task.Run(() => AuthenticatedBundleStream.OpenVerified(_files.OpenRead(record.Binding.SourceId), record,
                    _keys, CancellationToken.None));
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                if (_provider != null)
                {
                    if (_provider.HasOwners || _provider.Pending != 0 || _provider.Unloading != 0 || _provider.UnloadFailure != null)
                    {
                        throw new InvalidOperationException("Protected Bundle operations must finish before detaching their session.");
                    }
                    _provider.Detach();
                    _provider = null;
                }
                _disposed = true;
                _keys.Dispose();
            }

            private void CheckOpen()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(Session));
                }
            }
        }
    }
}
