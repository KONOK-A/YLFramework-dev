using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Version = YLFramework.Resource.ResourceContentStore.Version;
using Classification = YLFramework.Resource.ResourceCacheUsageScan.Classification;
using FileIdentity = System.ValueTuple<ulong, uint, ulong>;
using FileStamp = System.ValueTuple<long, ulong, uint, ulong, ulong>;

namespace YLFramework.Resource
{
    public sealed partial class ResourceContentUpdates
    {
        private CachePreparation _cachePreparation;

        private IDisposable TrackCachePreparation(ResourceContentManifest manifest, IEnumerable<string> routes, bool publishesManifest = false)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (publishesManifest)
                paths.Add(AnnouncementPath(manifest.Digest));
            foreach (string route in routes)
            {
                var artifact = manifest.Artifacts[route];
                if (artifact.Source.StartsWith(InstalledPrefix, StringComparison.Ordinal))
                    continue;
                string path = ObjectPath(artifact);
                paths.Add(path);
            }
            var preparation = new CachePreparation(this, paths);
            Volatile.Write(ref _cachePreparation, preparation);
            return preparation;
        }

        private sealed class CachePreparation : IDisposable
        {
            private readonly ResourceContentUpdates _owner;
            private readonly object _gate = new();
            private readonly Dictionary<string, FileIdentity> _stages = new(StringComparer.OrdinalIgnoreCase);
            private long _stageRevision;
            internal readonly HashSet<string> Paths;

            internal CachePreparation(ResourceContentUpdates owner, HashSet<string> paths)
            {
                _owner = owner;
                Paths = paths;
            }

            internal void RegisterStage(string path, FileIdentity identity)
            {
                lock (_gate)
                {
                    _stages.Add(path, identity);
                    _stageRevision++;
                }
            }

            internal void UnregisterStage(string path)
            {
                lock (_gate)
                {
                    _stages.Remove(path);
                    _stageRevision++;
                }
            }

            internal Dictionary<string, FileIdentity> SnapshotStages(out long revision)
            {
                lock (_gate)
                {
                    revision = _stageRevision;
                    return new Dictionary<string, FileIdentity>(_stages, _stages.Comparer);
                }
            }

            internal bool StagesUnchanged(long revision)
            {
                lock (_gate)
                    return _stageRevision == revision;
            }

            public void Dispose() => Interlocked.CompareExchange(ref _owner._cachePreparation, null, this);
        }

        /// <summary>只读盘点本应用和渠道的补丁缓存。可在内容会话启动前调用。</summary>
        public Task<ResourceCacheUsageReport> InspectCacheAsync(CancellationToken cancellation = default) =>
            Task.Run(() => InspectCache(cancellation), cancellation);

        private ResourceCacheUsageReport InspectCache(CancellationToken cancellation)
        {
            using var scan = new ResourceCacheUsageScan(_store.Root, cancellation);
            if (!scan.PinDirectory(_store.Root))
                return scan.Complete(ResourceCacheOwner.SignedContent, _trust.Application, _trust.Channel);

            string lockPath = Path.Combine(_store.Root, ResourceContentStore.WriterFile);
            using var writerObservation = ObserveWriter(scan, lockPath, out bool idle);
            var controls = idle ? ObserveControlRecords(scan) : null;
            bool objectsPresent = scan.PinDirectory(ObjectsRoot);
            bool versionsPresent = scan.PinDirectory(VersionsRoot);
            var preparation = Volatile.Read(ref _cachePreparation);
            long stageRevision = 0;
            var stages = preparation?.SnapshotStages(out stageRevision);
            var references = ReadCacheReferences(scan, idle && controls.HasValue, preparation, cancellation, out bool complete);
            scan.ReadFiles(_store.Root, ControlKind, Classify);
            if (objectsPresent)
                scan.ReadFiles(ObjectsRoot, name => TryClassifyPatchFile(name, false, out var kind) ? kind : null, Classify);
            if (versionsPresent)
                scan.ReadFiles(VersionsRoot, name => TryClassifyPatchFile(name, true, out var kind) ? kind : null, Classify);

            bool stable = true;
            if (idle && writerObservation == null)
            {
                using var current = ObserveWriter(scan, lockPath, out bool stillIdle);
                stable = stillIdle && current == null;
                if (!stable)
                    scan.Issue(lockPath, "A writer appeared during inspection.", inventory: true);
            }
            if (idle && controls.HasValue && !controls.Equals(ObserveControlRecords(scan)))
            {
                stable = false;
                scan.Issue(_store.Root, "The control records changed during inspection.", inventory: true);
            }
            if (preparation != Volatile.Read(ref _cachePreparation) || preparation?.StagesUnchanged(stageRevision) == false)
            {
                stable = false;
                scan.Issue(lockPath, "Local preparation changed during inspection.", inventory: true);
            }
            return scan.Complete(ResourceCacheOwner.SignedContent, _trust.Application, _trust.Channel, stable);

            Classification Classify(string path, ResourceCacheFileKind kind)
            {
                if (kind == ResourceCacheFileKind.ControlRecord)
                    return new Classification(true, ResourceCacheRetention.ControlRecord);

                if (stages != null && stages.TryGetValue(path, out var stage) &&
                    ResourceContentFileIO.ReadFileIdentity(path) == stage)
                    return new Classification(true, ResourceCacheRetention.Preparation);

                string name = Path.GetFileName(path);
                if (kind == ResourceCacheFileKind.Temporary)
                {
                    if (IsResumeFileName(name))
                        return ClassifyCheckpoint(path);
                    if (!ResourceContentStore.IsTemporaryRecordName(name))
                    {
                        scan.Issue(path, "The staging file has no confirmed transfer owner.");
                        return default;
                    }
                }

                references.TryGetValue(path, out var reference);
                var reasons = reference.Reasons;
                try
                {
                    if (idle && HasOpenCacheHandle(path))
                        reasons |= ResourceCacheRetention.OpenHandle;

                    using var input = ResourceContentFileIO.OpenRead(path);
                    bool owned = (!reference.Length.HasValue || input.Length == reference.Length.Value) &&
                        (kind == ResourceCacheFileKind.Temporary
                            ? _store.IsOwnedTemporaryRecord(input, name)
                            : IsOwned(input, Path.GetFileNameWithoutExtension(name),
                                kind == ResourceCacheFileKind.VersionManifest, cancellation));
                    if (!owned)
                        scan.Issue(path, "The file does not match a confirmed cache identity.");
                    return new Classification(owned, reasons, complete);
                }
                catch (Exception error) when (ResourceCacheUsageScan.IsObservationFailure(error))
                {
                    scan.Issue(path, error.Message);
                    return new Classification(false, reasons);
                }
            }

            Classification ClassifyCheckpoint(string path)
            {
                try
                {
                    string destination = ResumeObjectPath(path);
                    var checkpoint = ReadResumeMarker(destination);
                    if (checkpoint != null)
                    {
                        string partPath = ResumePath(destination, PartSuffix);
                        var part = ReadResumeStamp(partPath);
                        bool usable = IsUsableCheckpoint(checkpoint, part);
                        if (usable && !checkpoint.PartIdentity.HasValue)
                        {
                            // 旧版完整前缀只有通过整件摘要核验才能归属。
                            using var input = ResourceContentFileIO.OpenRead(partPath);
                            usable = ResourceContentFileIO.ReadFileStamp(input) == part.Value &&
                                ResourceContentFileIO.Hash(input, cancellation) == checkpoint.Sha256;
                        }
                        bool marker = Path.GetExtension(path) == MarkSuffix;
                        bool ownedPart = checkpoint.PartIdentity is FileIdentity owner && part is FileStamp stamp &&
                            IdentityOf(stamp) == owner;
                        if (marker || ownedPart || usable)
                        {
                            var retention = usable && idle ? ResourceCacheRetention.Resumable : ResourceCacheRetention.None;
                            if (idle && (HasOpenCacheHandle(path) ||
                                (ownedPart && HasOpenCacheHandle(marker ? partPath : ResumePath(destination, MarkSuffix)))))
                                retention |= ResourceCacheRetention.OpenHandle;
                            return new Classification(true, retention, complete && !usable);
                        }
                    }
                }
                catch (Exception error) when (ResourceCacheUsageScan.IsObservationFailure(error))
                {
                    scan.Issue(path, error.Message);
                    return default;
                }
                scan.Issue(path, "A checkpoint file's ownership could not be confirmed.");
                return default;
            }
        }

        private static bool HasOpenCacheHandle(string path)
        {
            try
            {
                using var probe = ResourceContentFileIO.OpenRead(path, exclusive: true);
                return false;
            }
            catch (IOException error) when (ResourceContentFileIO.IsSharingConflict(error))
            {
                return true;
            }
        }

        private static FileStream ObserveWriter(ResourceCacheUsageScan scan, string path, out bool idle)
        {
            try
            {
                var read = ResourceContentFileIO.OpenRead(path);
                idle = true;
                return read;
            }
            catch (FileNotFoundException)
            {
                idle = true;
                return null;
            }
            catch (Exception error) when (ResourceCacheUsageScan.IsObservationFailure(error))
            {
                idle = false;
                scan.Issue(path, error is IOException io && ResourceContentFileIO.IsSharingConflict(io)
                    ? "A writer owns the cache. Unregistered preparations may already have published files."
                    : error.Message, inventory: true);
                return null;
            }
        }

        private (FileStamp? State, FileStamp? Journal)? ObserveControlRecords(ResourceCacheUsageScan scan)
        {
            try
            {
                return (Stamp(ResourceContentStore.StateFile), Stamp(ResourceContentStore.JournalFile));
            }
            catch (Exception error) when (ResourceCacheUsageScan.IsObservationFailure(error))
            {
                scan.Issue(_store.Root, error.Message);
                return null;
            }

            FileStamp? Stamp(string name)
            {
                try
                {
                    return ResourceContentFileIO.ReadFileStamp(Path.Combine(_store.Root, name));
                }
                catch (FileNotFoundException) { return null; }
            }
        }

        private Dictionary<string, (ResourceCacheRetention Reasons, long? Length)> ReadCacheReferences(
            ResourceCacheUsageScan scan, bool idle, CachePreparation preparation, CancellationToken cancellation, out bool complete)
        {
            var references = new Dictionary<string, (ResourceCacheRetention Reasons, long? Length)>(StringComparer.OrdinalIgnoreCase);
            var serving = Volatile.Read(ref _serving);
            var installed = Volatile.Read(ref _installed);
            if (serving != null)
            {
                AddManifest(serving.Manifest, ResourceCacheRetention.FrozenSession);
                foreach (string path in serving.Files.SnapshotVerifiedPaths())
                    if (IsWithin(path, ObjectsRoot))
                        Add(path, ResourceCacheRetention.OpenHandle);
            }

            if (preparation != null)
                foreach (string path in preparation.Paths)
                    Add(path, ResourceCacheRetention.Preparation);

            bool resolved = idle;
            if (!idle)
            {
                complete = false;
                return references;
            }

            cancellation.ThrowIfCancellationRequested();
            ResourceContentStore.State state = null;
            try
            {
                state = _store.ReadState();
            }
            catch (Exception error) when (IsUnresolvedReference(error))
            {
                Unresolved(Path.Combine(_store.Root, ResourceContentStore.StateFile), error);
            }

            if (state != null)
            {
                AddVersion(state.Active, ResourceCacheRetention.ActiveVersion);
                AddVersion(state.Previous, ResourceCacheRetention.PreviousVersion);
                try
                {
                    var journal = _store.ReadJournal();
                    if (journal.Ready != null && journal.Ready.Sequence > state.HighestSequence)
                        AddVersion(journal.Ready, ResourceCacheRetention.ReadyVersion);
                }
                catch (Exception error) when (IsUnresolvedReference(error))
                {
                    Unresolved(Path.Combine(_store.Root, ResourceContentStore.JournalFile), error);
                }
            }
            complete = resolved;
            return references;

            bool IsUnresolvedReference(Exception error) => ResourceCacheUsageScan.IsObservationFailure(error) ||
                error is JsonException or DecoderFallbackException;

            void Unresolved(string path, Exception error)
            {
                scan.Issue(path, error.Message);
                resolved = false;
            }

            void AddVersion(Version target, ResourceCacheRetention reason)
            {
                if (target == null)
                    return;
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    AddManifest(ResolveVersion(target), reason);
                }
                catch (Exception error) when (IsUnresolvedReference(error))
                {
                    Unresolved(AnnouncementPath(target.Digest), error);
                }
            }

            ResourceContentManifest ResolveVersion(Version target)
            {
                if (installed != null && VersionOf(installed).Equals(target))
                    return installed;

                try
                {
                    return ReadDownloadedVersion(target);
                }
                catch (FileNotFoundException)
                {
                    using var directory = ResourceContentFileIO.PinDirectory(_installRoot, false);
                    using var settings = ResourceContentFileIO.OpenRead(Path.Combine(_installRoot, SettingsName));
                    installed ??= ReadInstalled(settings, cancellation);
                    if (!VersionOf(installed).Equals(target))
                        throw new InvalidDataException("A referenced signed version manifest is missing.");
                    return installed;
                }
            }

            void AddManifest(ResourceContentManifest manifest, ResourceCacheRetention reason)
            {
                // Include downloaded optional chapters and their signed dependencies.
                Add(AnnouncementPath(manifest.Digest), reason);
                foreach (var artifact in manifest.Artifacts.Values)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!artifact.Source.StartsWith(InstalledPrefix, StringComparison.Ordinal))
                        Add(ObjectPath(artifact), reason, artifact.Length);
                }
            }

            void Add(string path, ResourceCacheRetention reason, long? length = null)
            {
                references.TryGetValue(path, out var existing);
                references[path] = (existing.Reasons | reason, existing.Length ?? length);
            }
        }

        private static ResourceCacheFileKind? ControlKind(string name) => name switch
        {
            ResourceContentStore.StateFile or ResourceContentStore.JournalFile or ResourceContentStore.WriterFile =>
                ResourceCacheFileKind.ControlRecord,
            _ => ResourceContentStore.IsTemporaryRecordName(name) ? ResourceCacheFileKind.Temporary : null
        };

        private static bool TryClassifyPatchFile(string name, bool versions, out ResourceCacheFileKind kind)
        {
            const string stagingPrefix = ".stage-";
            if ((!versions && IsResumeFileName(name)) ||
                (name.StartsWith(stagingPrefix, StringComparison.Ordinal) &&
                 Guid.TryParseExact(name.Substring(stagingPrefix.Length), "N", out _)))
            {
                kind = ResourceCacheFileKind.Temporary;
                return true;
            }
            kind = versions ? ResourceCacheFileKind.VersionManifest : ResourceCacheFileKind.Artifact;
            string suffix = Path.GetExtension(name);
            return (versions ? suffix == ".json" : suffix is ".json" or ".bin" or ".bundle" or ".cipher") &&
                ResourceContentStore.IsDigest(Path.GetFileNameWithoutExtension(name));
        }
    }
}
