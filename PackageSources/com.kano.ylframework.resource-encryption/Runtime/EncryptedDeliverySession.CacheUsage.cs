using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Classification = YLFramework.Resource.ResourceCacheUsageScan.Classification;

namespace YLFramework.Resource.Encryption
{
    public sealed partial class EncryptedDeliverySession
    {
        /// <summary>Inspect the dedicated ciphertext cache with optional evidence from its matching open session.</summary>
        /// <remarks>The cold query uses no content keys. Cache files that cannot be attributed are reported as undetermined.</remarks>
        public static Task<ResourceCacheUsageReport> InspectCacheAsync(string dedicatedCipherCache,
            EncryptedDeliverySession session = null, CancellationToken cancellation = default)
        {
            string root = OwnedWindowsFiles.LocalAbsolute(dedicatedCipherCache).TrimEnd('\\');
            if (root.Length == 2)
                throw new ArgumentException("The cipher cache must own a dedicated subdirectory.", nameof(dedicatedCipherCache));
            if (session != null && (session._cacheRoot == null || Volatile.Read(ref session._closing) != null ||
                !root.Equals(OwnedWindowsFiles.LocalAbsolute(session._cacheRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Pass an open session configured for the requested cipher cache.", nameof(session));
            return Task.Run(() => InspectCipherCache(root, session, cancellation), cancellation);
        }

        private static ResourceCacheUsageReport InspectCipherCache(string root, EncryptedDeliverySession session, CancellationToken cancellation)
        {
            using var scan = new ResourceCacheUsageScan(root, cancellation);
            if (!scan.PinDirectory(root))
                return scan.Complete(ResourceCacheOwner.FixedEncryptedDelivery);

            string ownerPath = Path.Combine(root, EncryptedBundleCache.OwnerFile);
            using var owner = ObserveOwner(scan, ownerPath, out bool busy);
            var cache = session?.Cache;
            var activity = cache?.SnapshotActiveParts();
            var activeParts = activity?.Parts;
            var records = session?._description.Bundles
                .Where(record => record.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps)
                .ToLookup(record => record.CipherSha256, StringComparer.Ordinal);

            if (busy && activity?.IsOpen != true)
                scan.Issue(ownerPath, "The cache is open in an unobserved session.", inventory: true);
            if (activeParts?.Count > 0)
                scan.Issue(ownerPath, "The cache has ongoing transfers.", inventory: true);
            scan.ReadFiles(root, KindOf, Classify);
            bool stable = true;
            if (cache != null)
            {
                var current = cache.SnapshotActiveParts();
                stable = current.Revision == activity.Value.Revision && current.IsOpen == activity.Value.IsOpen;
            }
            if (session != null && Volatile.Read(ref session._closing) != null)
                stable = false;
            if (owner == null && !busy)
            {
                using var laterOwner = ObserveOwner(scan, ownerPath, out bool laterBusy);
                if (laterBusy || laterOwner != null)
                    stable = false;
            }
            if (!stable)
                scan.Issue(ownerPath, "Cache or transfer ownership changed during inspection.", inventory: true);
            return scan.Complete(ResourceCacheOwner.FixedEncryptedDelivery, stableReferences: stable);

            Classification Classify(string path, ResourceCacheFileKind kind)
            {
                if (kind == ResourceCacheFileKind.Temporary)
                {
                    var identity = ResourceContentFileIO.ReadFileIdentity(path);
                    if (activity?.IsOpen == true && activeParts.TryGetValue(path, out var active) && active == identity)
                        return new Classification(true, ResourceCacheRetention.Preparation);

                    if (EncryptedBundleCache.ReadTransferProof(path) == identity)
                    {
                        if (busy)
                            return new Classification(true, ResourceCacheRetention.None);
                        try
                        {
                            using var probe = ResourceContentFileIO.OpenRead(path, exclusive: true);
                            return new Classification(ResourceContentFileIO.ReadFileIdentity(probe) == identity,
                                ResourceCacheRetention.None, canRemove: true);
                        }
                        catch (IOException error) when (ResourceContentFileIO.IsSharingConflict(error))
                        {
                            return new Classification(true, ResourceCacheRetention.OpenHandle);
                        }
                    }
                    scan.Issue(path, "The temporary file has no confirmed transfer owner.");
                    return default;
                }

                var record = records?[Path.GetFileNameWithoutExtension(path)].FirstOrDefault();
                if (record == null)
                {
                    scan.Issue(path, "The file cannot be attributed without a matching trusted delivery description.");
                    return default;
                }
                var reasons = ResourceCacheRetention.DeliveryDescription;
                try
                {
                    // Release the probe before hashing. Use a shared reader alongside AssetBundle opens.
                    using var probe = ResourceContentFileIO.OpenRead(path, exclusive: true);
                }
                catch (IOException error) when (ResourceContentFileIO.IsSharingConflict(error))
                {
                    reasons |= ResourceCacheRetention.OpenHandle;
                }
                using (var readCipher = ResourceContentFileIO.OpenRead(path))
                    ResourceContentFileIO.RequireIdentity(readCipher, record.CipherLength, record.CipherSha256, cancellation);
                return new Classification(true, reasons);
            }
        }

        private static FileStream ObserveOwner(ResourceCacheUsageScan scan, string path, out bool busy)
        {
            busy = false;
            try
            {
                return ResourceContentFileIO.OpenRead(path);
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException error) when (ResourceContentFileIO.IsSharingConflict(error))
            {
                busy = true;
                return null;
            }
            catch (Exception error) when (ResourceCacheUsageScan.IsObservationFailure(error))
            {
                scan.Issue(path, error.Message, inventory: true);
                busy = true;
                return null;
            }
        }

        private static ResourceCacheFileKind? KindOf(string name)
        {
            if (EncryptedBundleCache.IsTemporaryName(name))
                return ResourceCacheFileKind.Temporary;
            return Path.GetExtension(name) == ".bin" && ResourceContentStore.IsDigest(Path.GetFileNameWithoutExtension(name))
                ? ResourceCacheFileKind.Artifact : null;
        }
    }
}
