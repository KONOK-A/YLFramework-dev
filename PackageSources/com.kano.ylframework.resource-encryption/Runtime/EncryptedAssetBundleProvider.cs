using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.Scripting;

namespace YLFramework.Resource.Encryption
{
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

    /// <summary>Addressables Bundle Provider for the single fixed protected group. Assets still use BundledAssetProvider.</summary>
    [Preserve]
    public sealed class EncryptedAssetBundleProvider : ResourceProviderBase
    {
        internal const int UnityReadBufferBytes = 32 * 1024;
        private readonly Dictionary<string, Task> _unloading = new Dictionary<string, Task>(StringComparer.Ordinal);
        private readonly List<EncryptedMountedBundle> _unconfirmed = new List<EncryptedMountedBundle>();
        private EncryptedDeliverySession _session;
        internal int Pending { get; private set; }
        internal int Mounted { get; private set; }
        internal int Creates { get; private set; }
        internal int Unloads { get; private set; }
        internal int StreamsClosed { get; private set; }
        internal int Unloading => _unloading.Count;
        internal Exception UnloadFailure { get; private set; }

        internal void Attach(EncryptedDeliverySession session)
        {
            if (_session != null) throw new InvalidOperationException("The protected Addressables provider already has an owner.");
            _session = session;
        }

        internal void Detach() => _session = null;

        /// <inheritdoc />
        public override Type GetDefaultType(IResourceLocation location) => typeof(IAssetBundleResource);

        /// <inheritdoc />
        public override void Provide(ProvideHandle handle)
        {
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

        private async Task ProvideAsync(EncryptedDeliverySession owner, ProvideHandle handle)
        {
            Pending++;
            EncryptedMountedBundle result = null;
            Exception failure = null;
            try
            {
                owner.CheckUnchanged();
                if (UnloadFailure != null) throw new InvalidOperationException("A protected Bundle could not complete native unload.", UnloadFailure);
                var location = handle.Location;
                if (location.ResourceType == typeof(IAssetBundleResource))
                {
                    var record = owner.ResolveTransfer(location);
                    await owner.Cache.EnsureAsync(record);
                    owner.CheckUnchanged();
                }
                else if (location.ResourceType == typeof(EncryptedMountedBundle))
                {
                    var record = owner.ResolveMount(location);
                    string id = location.InternalId;
                    if (_unloading.TryGetValue(id, out var unloading)) await unloading;
                    var reader = await owner.Cache.OpenAuthenticatedAsync(record, owner.Keys);
                    EncryptedMountedBundle mounted = null;
                    try
                    {
                        owner.CheckUnchanged();
                        Creates++;
                        var request = AssetBundle.LoadFromStreamAsync(reader, 0, UnityReadBufferBytes);
                        await NativeCompletion(request);
                        var bundle = request.assetBundle;
                        if (!bundle) throw new IOException("Unity rejected the authenticated Bundle stream.");
                        mounted = new EncryptedMountedBundle(id, bundle, reader);
                        Mounted++;
                        owner.CheckUnchanged();
                        result = mounted;
                    }
                    finally
                    {
                        if (result == null)
                        {
                            if (mounted != null) await BeginUnload(mounted);
                            else DisposeReader(reader);
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
            handle.Complete<IAssetBundleResource>(result, failure == null, failure);
        }

        /// <inheritdoc />
        public override void Release(IResourceLocation location, object asset)
        {
            if (asset is EncryptedMountedBundle mounted) _ = BeginUnload(mounted);
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
                await NativeCompletion(mounted.Bundle.UnloadAsync(true));
                Mounted--;
                Unloads++;
                DisposeReader(mounted.Reader);
                StreamsClosed++;
                completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                // If Unity never confirms native unload, it could still read this stream. Do not dispose it or allow a replacement mount.
                _unconfirmed.Add(mounted);
                UnloadFailure = error;
                completion.TrySetException(error);
                _ = completion.Task.Exception;
                Debug.LogException(error);
            }
            finally { _unloading.Remove(mounted.Id); }
        }

        private static void DisposeReader(AuthenticatedBundleStream reader)
        {
            try { reader.Dispose(); }
            catch (IOException error) when (error is EncryptedBundleKeySourceException)
            {
                // The native reader and file are closed, and the lease cleared its material before invoking application code.
                Debug.LogWarning(error.Message);
            }
        }

        private static Task NativeCompletion(AsyncOperation operation)
        {
            if (operation == null) throw new InvalidOperationException("Unity did not start the protected native Bundle operation.");
            if (operation.isDone) return Task.CompletedTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.completed += _ => completion.TrySetResult(true);
            return completion.Task;
        }
    }
}
