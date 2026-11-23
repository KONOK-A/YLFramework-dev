#if UNITY_EDITOR || YL_RESOURCE_ADDRESSABLES_ACCEPTANCE
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.Scripting;
using YLFramework.Testing.NativeP0;

namespace YLFramework.Acceptance
{
    [Preserve]
    public sealed class P0HeldMountedBundle : IAssetBundleResource
    {
        private readonly AssetBundle _bundle;
        private P0NativeReadStream _stream;

        internal P0HeldMountedBundle(AssetBundle bundle, P0NativeReadStream stream) { _bundle = bundle; _stream = stream; }
        public AssetBundle GetAssetBundle() => _bundle;
        internal void Release()
        {
            _bundle.Unload(true);
            _stream.Dispose();
            _stream = null;
        }
    }

    [Preserve]
    public sealed class P0HeldBundleProvider : ResourceProviderBase
    {
        public const string MountPrefix = "yl-p0-mount/";
        internal ResourceAddressablesPlayerProbe Entry;
        internal string TransferId, MountId, Url, Path;
        internal Func<string, bool> Verify;
        internal Func<P0NativeReadStream> Open;
        internal int Downloads, Mounts, CreateCalls, Releases;
        private bool _started, _ready;
        private Exception _failure;

        public override Type GetDefaultType(IResourceLocation location) => typeof(IAssetBundleResource);

        public override void Provide(ProvideHandle handle)
        {
            bool mount = handle.Location.ResourceType == typeof(P0HeldMountedBundle) && handle.Location.InternalId == MountId;
            if (Entry == null || (!mount && (handle.Location.ResourceType != typeof(IAssetBundleResource) || handle.Location.InternalId != TransferId)))
            {
                handle.Complete<IAssetBundleResource>(null, false, new InvalidOperationException("Unverified test route."));
                return;
            }
            if (mount) Mounts++; else Downloads++;
            Entry.StartCoroutine(Fulfill(handle, mount));
        }

        private IEnumerator Fulfill(ProvideHandle handle, bool mount)
        {
            if (!_started)
            {
                _started = true;
                Entry.StartCoroutine(Transfer());
            }
            while (!_ready && _failure == null) yield return null;
            if (_failure != null || !mount)
            {
                // A successful transfer has no mounted wrapper. This keeps it out of BundledAssetProvider's bundle selection.
                handle.Complete<IAssetBundleResource>(null, _failure == null, _failure);
                yield break;
            }
            var opening = Task.Run(Open);
            while (!opening.IsCompleted) yield return null;
            if (opening.IsFaulted)
            {
                handle.Complete<IAssetBundleResource>(null, false, opening.Exception);
                yield break;
            }
            var stream = opening.Result;
            AssetBundleCreateRequest create = null;
            Exception failure = null;
            try { CreateCalls++; create = AssetBundle.LoadFromStreamAsync(stream, 0, 32 * 1024); }
            catch (Exception error) { failure = error; }
            if (create != null) yield return create;
            if (create == null || !create.assetBundle)
            {
                stream.Dispose();
                handle.Complete<IAssetBundleResource>(null, false, failure ?? new IOException("Unity refused the authenticated stream."));
                yield break;
            }
            handle.Complete<IAssetBundleResource>(new P0HeldMountedBundle(create.assetBundle, stream), true, null);
        }

        private IEnumerator Transfer()
        {
            string part = Path + ".part";
            using (var request = UnityWebRequest.Get(Url))
            {
                request.downloadHandler.Dispose();
                request.downloadHandler = new DownloadHandlerFile(part) { removeFileOnAbort = true };
                request.redirectLimit = 0;
                request.timeout = 24;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                    _failure = new IOException("Unexpected isolated HTTP response: " + request.responseCode + " " + request.error);
            }
            if (_failure != null) { File.Delete(part); yield break; }
            var publish = Task.Run(() =>
            {
                try
                {
                    if (!Verify(part)) throw new IOException("Fixture cipher bytes do not match the frozen description.");
                    File.Move(part, Path);
                }
                finally { if (File.Exists(part)) File.Delete(part); }
            });
            while (!publish.IsCompleted) yield return null;
            if (publish.IsFaulted) _failure = publish.Exception;
            else _ready = true;
        }

        public override void Release(IResourceLocation location, object asset)
        {
            if (asset is P0HeldMountedBundle mounted)
            {
                mounted.Release();
                Releases++;
            }
        }
    }

    public sealed partial class ResourceAddressablesPlayerProbe
    {
        private P0HeldBundleProvider _heldProvider;

        partial void ConfigureHeldCandidate(IResourceLocation location, HeldFixture fixture, int round)
        {
            if (_build.heldDownload != "Separated") return;
            _heldProvider = (P0HeldBundleProvider)System.Linq.Enumerable.Single(Addressables.ResourceManager.ResourceProviders,
                provider => provider.ProviderId == location.ProviderId);
            string keyPath = Path.Combine(_build.playerRoot, "p0-random-input.bin");
            string cache = Path.Combine(_build.playerRoot, "P0Cipher-" + round);
            Require(!Directory.Exists(cache), "The separated candidate must start with a cold cipher cache.");
            Directory.CreateDirectory(cache);
            string final = Path.Combine(cache, fixture.deliveredSha256 + ".bin");
            _heldProvider.Entry = this;
            _heldProvider.TransferId = location.InternalId;
            _heldProvider.MountId = P0HeldBundleProvider.MountPrefix + ((AssetBundleRequestOptions)location.Data).BundleName;
            _heldProvider.Url = Addressables.ResourceManager.TransformInternalId(location);
            _heldProvider.Path = final;
            _heldProvider.Verify = path => new FileInfo(path).Length == fixture.deliveredBytes && FileDigest(path) == fixture.deliveredSha256;
            _heldProvider.Open = () =>
            {
                RejectLinks(keyPath);
                var key = new byte[32];
                try
                {
                    using (var input = File.OpenRead(keyPath))
                        if (input.Length != key.Length || input.Read(key, 0, key.Length) != key.Length) throw new IOException("Invalid ephemeral test key.");
                    var stream = new P0NativeReadStream(final, key);
                    try
                    {
                        var scratch = new byte[32 * 1024];
                        try { while (stream.Read(scratch, 0, scratch.Length) > 0) { } }
                        finally { Array.Clear(scratch, 0, scratch.Length); }
                        stream.Position = 0;
                        return stream;
                    }
                    catch { stream.Dispose(); throw; }
                }
                finally { Array.Clear(key, 0, key.Length); }
            };
        }

        partial void CompleteHeldCandidate(HeldDownloadEvidence row)
        {
            if (_heldProvider == null) return;
            row.downloadProvides = _heldProvider.Downloads;
            row.realProvides = _heldProvider.Mounts;
            row.createCalls = _heldProvider.CreateCalls;
            row.releases = _heldProvider.Releases;
            int count = _report.round == 1 ? 0 : 1;
            Require(row.downloadProvides >= 1 && row.realProvides == count && row.createCalls == count && row.releases == count && row.leasesAtEnd == 0,
                "HeldDownloadHandle_DoesNotSatisfyAssetLoad: wrong separated create/release totals: " + JsonUtility.ToJson(row));
        }
    }
}
#endif
