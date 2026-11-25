using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace YLFramework.Resource
{
    public enum ResourceCacheOwner { SignedContent, FixedEncryptedDelivery }
    public enum ResourceCacheFileKind { Artifact, VersionManifest, ControlRecord, Temporary }
    public enum ResourceCacheDisposition { Retained, CleanupCandidate, Undetermined }
    public enum ResourceCacheCertainty { Unknown, ObservedLowerBound, Exact }

    [Flags]
    public enum ResourceCacheRetention
    {
        None = 0,
        ControlRecord = 1,
        ActiveVersion = 2,
        PreviousVersion = 4,
        ReadyVersion = 8,
        FrozenSession = 16,
        OpenHandle = 32,
        Preparation = 64,
        DeliveryDescription = 128,
        Resumable = 256
    }

    /// <summary>逻辑文件字节数及其可信程度。未知时 Bytes 为 null。</summary>
    public readonly struct ResourceCacheByteCount
    {
        internal ResourceCacheByteCount(long bytes, bool complete)
        {
            Certainty = complete ? ResourceCacheCertainty.Exact :
                bytes > 0 ? ResourceCacheCertainty.ObservedLowerBound : ResourceCacheCertainty.Unknown;
            Bytes = Certainty == ResourceCacheCertainty.Unknown ? null : bytes;
        }

        public long? Bytes { get; }
        public ResourceCacheCertainty Certainty { get; }
    }

    /// <summary>缓存根内的单个文件。仅归属已确认的文件进入汇总。</summary>
    public sealed class ResourceCacheFileUsage
    {
        internal ResourceCacheFileUsage(string relativePath, ResourceCacheFileKind kind, long? bytes,
            bool ownershipConfirmed, ResourceCacheDisposition disposition, ResourceCacheRetention retention)
        {
            RelativePath = relativePath;
            Kind = kind;
            Bytes = bytes;
            OwnershipConfirmed = ownershipConfirmed;
            Disposition = disposition;
            Retention = retention;
        }

        public string RelativePath { get; }
        public ResourceCacheFileKind Kind { get; }
        public long? Bytes { get; }
        public bool OwnershipConfirmed { get; }
        public ResourceCacheDisposition Disposition { get; }
        public ResourceCacheRetention Retention { get; }
    }

    /// <summary>对应用自有缓存的只读观察。候选仅说明观察时的去留。</summary>
    public sealed class ResourceCacheUsageReport
    {
        internal ResourceCacheUsageReport(ResourceCacheOwner owner, string root, string application, string channel,
            DateTimeOffset observedAtUtc, ResourceCacheFileUsage[] files, string[] issues,
            ResourceCacheByteCount owned, ResourceCacheByteCount retained, ResourceCacheByteCount cleanupCandidates,
            ResourceCacheByteCount undetermined)
        {
            Owner = owner;
            Root = root;
            Application = application;
            Channel = channel;
            ObservedAtUtc = observedAtUtc;
            Files = Array.AsReadOnly(files);
            Issues = Array.AsReadOnly(issues);
            Owned = owned;
            Retained = retained;
            CleanupCandidates = cleanupCandidates;
            Undetermined = undetermined;
        }

        public ResourceCacheOwner Owner { get; }
        public string Root { get; }
        public string Application { get; }
        public string Channel { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public IReadOnlyList<ResourceCacheFileUsage> Files { get; }
        /// <summary>影响读取、归属或去留判断的路径及原因。</summary>
        public IReadOnlyList<string> Issues { get; }
        public ResourceCacheByteCount Owned { get; }
        public ResourceCacheByteCount Retained { get; }
        public ResourceCacheByteCount CleanupCandidates { get; }
        public ResourceCacheByteCount Undetermined { get; }

        /// <summary>两种缓存报告均只统计其根内负责的文件逻辑长度。</summary>
        public string Exclusions => Owner == ResourceCacheOwner.SignedContent
            ? "Installed content, external files and the official Unity cache."
            : "Installed content, signed content storage, external files and the official Unity cache.";
    }

    internal sealed class ResourceCacheUsageScan : IDisposable
    {
        internal readonly struct Classification
        {
            internal Classification(bool owned, ResourceCacheRetention retention, bool canRemove = false)
            {
                Owned = owned;
                Retention = retention;
                Disposition = !owned ? ResourceCacheDisposition.Undetermined :
                    retention != ResourceCacheRetention.None ? ResourceCacheDisposition.Retained :
                    canRemove ? ResourceCacheDisposition.CleanupCandidate : ResourceCacheDisposition.Undetermined;
            }

            internal bool Owned { get; }
            internal ResourceCacheRetention Retention { get; }
            internal ResourceCacheDisposition Disposition { get; }
        }

        private readonly string _root;
        private readonly CancellationToken _cancellation;
        private readonly List<IDisposable> _pins = new();
        private readonly List<string> _issues = new();
        private readonly List<(ResourceCacheFileUsage Usage, Func<bool> Unchanged)> _files = new();
        private bool _inventoryIncomplete;

        internal ResourceCacheUsageScan(string root, CancellationToken cancellation)
        {
            _root = root;
            _cancellation = cancellation;
        }

        internal bool PinDirectory(string directory)
        {
            _cancellation.ThrowIfCancellationRequested();
            try
            {
                _pins.Add(ResourceContentFileIO.PinDirectory(directory, false));
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (Exception error) when (IsObservationFailure(error))
            {
                Issue(directory, error.Message, inventory: true);
                return false;
            }
        }

        internal void Issue(string path, string detail, bool inventory = false)
        {
            _issues.Add($"{Relative(path)}: {detail}");
            _inventoryIncomplete |= inventory;
        }

        internal void ReadFiles(string directory, Func<string, ResourceCacheFileKind?> getKind,
            Func<string, ResourceCacheFileKind, Classification> classify)
        {
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    _cancellation.ThrowIfCancellationRequested();
                    var kind = getKind(Path.GetFileName(path));
                    if (kind.HasValue)
                        InspectFile(path, kind.Value, classify);
                }
            }
            catch (Exception error) when (IsObservationFailure(error))
            {
                Issue(directory, error.Message, inventory: true);
            }
        }

        private void InspectFile(string path, ResourceCacheFileKind kind, Func<string, ResourceCacheFileKind, Classification> classify)
        {
            long? bytes = null;
            Func<bool> unchanged = null;
            Classification classification = default;
            try
            {
                var stamp = ResourceContentFileIO.ReadFileStamp(path);
                bytes = stamp.Length;
                unchanged = () => stamp == ResourceContentFileIO.ReadFileStamp(path);
                classification = classify(path, kind);
            }
            catch (Exception error) when (IsObservationFailure(error))
            {
                Issue(path, error.Message);
            }

            _files.Add((new ResourceCacheFileUsage(Relative(path), kind, bytes, classification.Owned,
                classification.Owned ? classification.Disposition : ResourceCacheDisposition.Undetermined,
                classification.Retention), unchanged));
        }

        internal ResourceCacheUsageReport Complete(ResourceCacheOwner owner, string application = null, string channel = null,
            bool stableReferences = true)
        {
            // The initial and final stamp reads bracket a common point for every retained observation.
            var observedAt = DateTimeOffset.UtcNow;
            var files = new List<ResourceCacheFileUsage>(_files.Count);
            long owned = 0, retained = 0, candidates = 0, undetermined = 0;
            bool ownedComplete = !_inventoryIncomplete;
            bool retainedComplete = ownedComplete, candidatesComplete = ownedComplete, undeterminedComplete = ownedComplete;
            foreach (var (observed, unchanged) in _files)
            {
                _cancellation.ThrowIfCancellationRequested();
                bool fileUnchanged = unchanged == null || StillObserved(observed, unchanged);
                bool control = observed.Kind == ResourceCacheFileKind.ControlRecord;
                var usage = fileUnchanged && (stableReferences || control) ? observed :
                    new ResourceCacheFileUsage(observed.RelativePath, observed.Kind,
                        fileUnchanged ? observed.Bytes : null, fileUnchanged && observed.OwnershipConfirmed,
                        ResourceCacheDisposition.Undetermined, ResourceCacheRetention.None);
                files.Add(usage);
                if (!usage.OwnershipConfirmed || !usage.Bytes.HasValue)
                {
                    ownedComplete = retainedComplete = false;
                    if (!control)
                        candidatesComplete = undeterminedComplete = false;
                    continue;
                }

                long bytes = usage.Bytes.Value;
                owned = checked(owned + bytes);
                switch (usage.Disposition)
                {
                    case ResourceCacheDisposition.Retained: retained = checked(retained + bytes); break;
                    case ResourceCacheDisposition.CleanupCandidate: candidates = checked(candidates + bytes); break;
                    default:
                        undetermined = checked(undetermined + bytes);
                        candidatesComplete = retainedComplete = false;
                        break;
                }
            }

            return new ResourceCacheUsageReport(owner, _root, application, channel, observedAt,
                files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(), _issues.ToArray(),
                new ResourceCacheByteCount(owned, ownedComplete), new ResourceCacheByteCount(retained, retainedComplete),
                new ResourceCacheByteCount(candidates, candidatesComplete), new ResourceCacheByteCount(undetermined, undeterminedComplete));
        }

        private bool StillObserved(ResourceCacheFileUsage usage, Func<bool> unchanged)
        {
            try
            {
                if (unchanged())
                    return true;
                _issues.Add($"{usage.RelativePath}: The file changed during inspection.");
            }
            catch (Exception error) when (IsObservationFailure(error))
            {
                _issues.Add($"{usage.RelativePath}: {error.Message}");
            }
            return false;
        }

        private string Relative(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

        internal static bool IsObservationFailure(Exception error) => error is IOException or UnauthorizedAccessException or
            InvalidDataException or FormatException or System.Security.Cryptography.CryptographicException;

        public void Dispose()
        {
            for (int index = _pins.Count - 1; index >= 0; index--)
                _pins[index].Dispose();
        }
    }
}
