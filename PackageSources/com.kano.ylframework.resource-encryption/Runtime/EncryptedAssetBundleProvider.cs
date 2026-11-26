using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.Scripting;

namespace YLFramework.Resource.Encryption
{
    internal interface IEncryptedBundleSession
    {
        void CheckUnchanged();
        EncryptedBundleRecord ResolveTransfer(IResourceLocation location);
        EncryptedBundleRecord ResolveMount(IResourceLocation location);
        Task EnsureCiphertextAsync(EncryptedBundleRecord record, Action<EncryptedBundleCache.TransferProgress> progress);
        Task<AuthenticatedBundleStream> OpenBundleAsync(EncryptedBundleRecord record, Action<EncryptedBundleCache.TransferProgress> progress);
    }

    [Preserve]
    internal sealed class EncryptedMountedBundle : IAssetBundleResource
    {
        internal readonly AssetBundle Bundle;
        internal readonly AuthenticatedBundleStream Reader;
        internal readonly string Id;

        internal EncryptedMountedBundle(string id, AssetBundle bundle, AuthenticatedBundleStream reader)
        {
            Id = id;
            Bundle = bundle;
            Reader = reader;
        }

        public AssetBundle GetAssetBundle() => Bundle;
    }

    /// <summary>Addressables Bundle Provider for the fixed protected Catalog. Assets still use BundledAssetProvider.</summary>
    [Preserve]
    public sealed class EncryptedAssetBundleProvider : ResourceProviderBase
    {
        internal const int UnityReadBufferBytes = 32 * 1024;
        private readonly Dictionary<string, Task> _unloading = new Dictionary<string, Task>(StringComparer.Ordinal);
        private readonly List<EncryptedMountedBundle> _unconfirmed = new List<EncryptedMountedBundle>();
        private IEncryptedBundleSession _session;
        private Action _onMountAttempt;
        private int _ownedMounts;
        private int _ownedTransfers;

        private sealed class CiphertextReceipt : IAssetBundleResource
        {
            internal static readonly CiphertextReceipt Instance = new CiphertextReceipt();

            public AssetBundle GetAssetBundle() => null;
        }

        internal bool HasOwners => _ownedMounts != 0 || _ownedTransfers != 0;

        internal int Pending
        {
            get; private set;
        }
        internal int Mounted
        {
            get; private set;
        }
        internal int Creates
        {
            get; private set;
        }
        internal int Unloads
        {
            get; private set;
        }
        internal int StreamsClosed
        {
            get; private set;
        }
        internal int Unloading => _unloading.Count;
        internal Exception UnloadFailure
        {
            get; private set;
        }

        internal void Attach(IEncryptedBundleSession session, Action onMountAttempt = null)
        {
            if (_session != null)
                throw new InvalidOperationException("The protected Addressables provider already has an owner.");
            _onMountAttempt = onMountAttempt;
            _session = session;
        }

        internal void Detach()
        {
            _session = null;
            _onMountAttempt = null;
        }

        /// <inheritdoc />
        public override Type GetDefaultType(IResourceLocation location) => location.ResourceType == typeof(IAssetBundleResource)
            ? typeof(object) : typeof(IAssetBundleResource);

        /// <inheritdoc />
        public override bool CanProvide(Type type, IResourceLocation location) => type == typeof(IAssetBundleResource) ||
            (type == typeof(object) && location.ResourceType == typeof(IAssetBundleResource));

        /// <inheritdoc />
        public override void Provide(ProvideHandle handle)
        {
            // In both pinned Addressables versions, normal dependencies use the provider's default type. DownloadDependenciesAsync
            // requests IAssetBundleResource explicitly, which also separates its cached operation from the metadata dependency.
            if (handle.Location.ResourceType == typeof(IAssetBundleResource) && handle.Type == typeof(object))
            {
                // The mount is the only data operation for an asset load. This sibling must not check, transfer or compete with it.
                handle.Complete<object>(null, true, null);
                return;
            }

            handle.SetWaitForCompletionCallback(RejectSynchronousWait);
            var owner = _session;
            if (owner == null)
            {
                handle.Complete<IAssetBundleResource>(null, false, new InvalidOperationException("Protected assets require the application's verified encryption startup."));
                return;
            }
            _ = ProvideAsync(owner, handle);
        }

        private static bool RejectSynchronousWait() => throw new NotSupportedException("Protected Bundle transfers and mounts are asynchronous; do not call WaitForCompletion.");

        private async Task ProvideAsync(IEncryptedBundleSession owner, ProvideHandle handle)
        {
            Pending++;
            IAssetBundleResource result = null;
            Exception failure = null;
            EncryptedBundleCache.TransferProgress progress = null;

            DownloadStatus ReadProgress() => new DownloadStatus
            {
                DownloadedBytes = progress.DownloadedBytes,
                TotalBytes = progress.TotalBytes
            };

            void ReportProgress(EncryptedBundleCache.TransferProgress attempt)
            {
                progress = attempt;
                handle.SetDownloadProgressCallbacks(ReadProgress);
            }

            try
            {
                owner.CheckUnchanged();
                CheckUnloadSucceeded();
                var location = handle.Location;
                if (handle.Type != typeof(IAssetBundleResource))
                    throw new InvalidDataException("Unsupported protected Bundle result type.");
                if (location.ResourceType == typeof(IAssetBundleResource))
                {
                    var record = owner.ResolveTransfer(location);
                    await owner.EnsureCiphertextAsync(record, ReportProgress);
                    owner.CheckUnchanged();
                    CheckUnloadSucceeded();
                    result = CiphertextReceipt.Instance;
                    _ownedTransfers++;
                }
                else if (location.ResourceType == typeof(EncryptedMountedBundle))
                {
                    var record = owner.ResolveMount(location);
                    string id = location.InternalId;
                    if (_unloading.TryGetValue(id, out var unloading))
                    {
                        await unloading;
                        owner.CheckUnchanged();
                        CheckUnloadSucceeded();
                    }
                    var reader = await owner.OpenBundleAsync(record, ReportProgress);
                    EncryptedMountedBundle mounted = null;
                    bool createUnconfirmed = false;
                    try
                    {
                        owner.CheckUnchanged();
                        CheckUnloadSucceeded();
                        Creates++;
                        _onMountAttempt?.Invoke();
                        var request = AssetBundle.LoadFromStreamAsync(reader, 0, UnityReadBufferBytes);
                        createUnconfirmed = request != null;
                        await NativeCompletion(request);
                        var bundle = request.assetBundle;
                        createUnconfirmed = false;
                        if (!bundle)
                            throw new IOException("Unity rejected the authenticated Bundle stream.");
                        mounted = new EncryptedMountedBundle(id, bundle, reader);
                        Mounted++;
                        owner.CheckUnchanged();
                        CheckUnloadSucceeded();
                        result = mounted;
                        _ownedMounts++;
                    }
                    catch (Exception error) when (createUnconfirmed)
                    {
                        RetainUnconfirmed(new EncryptedMountedBundle(id, null, reader), error);
                        throw;
                    }
                    finally
                    {
                        if (result == null && !createUnconfirmed)
                        {
                            if (mounted != null)
                                await BeginUnload(mounted);
                            else
                                DisposeReader(reader);
                        }
                    }
                }
                else
                {
                    throw new InvalidDataException("Unsupported protected Bundle operation.");
                }
            }
            catch (Exception error) { failure = error; }
            finally { Pending--; }
            // ProviderOperation keeps the last sample when Complete removes this callback, even if nobody polled in flight.
            if (progress != null)
                handle.SetDownloadProgressCallbacks(ReadProgress);
            handle.Complete<IAssetBundleResource>(result, failure == null, failure);
        }

        /// <inheritdoc />
        public override void Release(IResourceLocation location, object asset)
        {
            if (asset is EncryptedMountedBundle mounted)
            {
                _ownedMounts--;
                _ = BeginUnload(mounted);
            }
            else if (asset is CiphertextReceipt)
            {
                _ownedTransfers--;
            }
        }

        internal async Task DrainUnloadsAsync()
        {
            await Task.WhenAll(_unloading.Values);
            CheckUnloadSucceeded();
        }

        private void CheckUnloadSucceeded()
        {
            if (UnloadFailure != null)
                throw new InvalidOperationException("A protected Bundle could not confirm native completion and release its stream.", UnloadFailure);
        }

        private Task BeginUnload(EncryptedMountedBundle mounted)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _unloading.Add(mounted.Id, completion.Task);
            _ = UnloadAsync(mounted, completion);
            return completion.Task;
        }

        private async Task UnloadAsync(EncryptedMountedBundle mounted, TaskCompletionSource<bool> completion)
        {
            try
            {
                if (!mounted.Bundle)
                    throw new InvalidOperationException("Unity no longer exposes the protected Bundle for native unload.");
                await NativeCompletion(mounted.Bundle.UnloadAsync(true));
                Mounted--;
                Unloads++;
                DisposeReader(mounted.Reader);
                StreamsClosed++;
            }
            catch (Exception error)
            {
                RetainUnconfirmed(mounted, error);
                Debug.LogException(error);
            }
            finally
            {
                _unloading.Remove(mounted.Id);
                // A settled unload can permit awaiters to continue, but only CheckUnloadSucceeded can allow further use.
                completion.TrySetResult(true);
            }
        }

        private void RetainUnconfirmed(EncryptedMountedBundle mounted, Exception error)
        {
            // Without confirmation, Unity could still read the stream. Keep a strong owner and prohibit replacement mounts.
            _unconfirmed.Add(mounted);
            UnloadFailure ??= error;
        }

        private static void DisposeReader(AuthenticatedBundleStream reader)
        {
            try
            {
                reader.Dispose();
            }
            catch (IOException error) when (error is EncryptedBundleKeySourceException)
            {
                // The native reader and file are closed, and the lease cleared its material before invoking application code.
                Debug.LogWarning(error.Message);
            }
        }

        private static Task NativeCompletion(AsyncOperation operation)
        {
            if (operation == null)
                throw new InvalidOperationException("Unity did not start the protected native Bundle operation.");
            if (operation.isDone)
                return Task.CompletedTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.completed += _ => completion.TrySetResult(true);
            return completion.Task;
        }
    }
}
