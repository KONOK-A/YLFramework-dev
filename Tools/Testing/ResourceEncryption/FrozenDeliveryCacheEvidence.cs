using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using YLFramework.Resource;
using YLFramework.Resource.Encryption;

namespace YLFramework.Testing
{
    public sealed partial class FrozenDeliveryProbe
    {
        [Serializable]
        private sealed class CacheInspectionEvidence
        {
            public CacheInspectionRound[] rounds;
            public bool writeDenied, readDenied, closedSessionRejected;
            public long heldPrefixBytes, stagedPrefixBytes;
        }

        [Serializable]
        private sealed class CacheInspectionRound
        {
            public string phase, startedUtc, endedUtc;
            public int keyDelta, mountDelta;
            public CacheEntry[] before, after;
            public CacheReportEvidence report;
        }

        [Serializable]
        private sealed class CacheEntry
        {
            public string path, fileId, created, lastWrite, sha256;
            public long bytes;
            public uint attributes;
        }

        [Serializable]
        private sealed class CacheReportEvidence
        {
            public string[] files, issues;
            public string ownedCertainty, retainedCertainty, candidateCertainty, undeterminedCertainty;
            public string ownedBytes, retainedBytes, candidateBytes, undeterminedBytes;
        }

        private sealed class ExperimentPrefetchFault : InvalidOperationException
        {
            internal ExperimentPrefetchFault() : base("Injected failure while the actual encrypted prefetch is held.") { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CacheFileInformation
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
                VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle OpenCacheEvidenceMetadata(string path, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out CacheFileInformation information);

        private IEnumerator InspectColdCache()
        {
            string cache = Argument("-ylFrozenCache");
            string denied = Path.Combine(cache, new string('d', 64) + ".bin");
            string legacy = Path.Combine(cache, ".yl-transfer-" + new string('a', 32) + ".part");
            string missing = cache + "-missing";
            var rounds = new List<CacheInspectionRound>();
            _row.cacheInspection = new CacheInspectionEvidence();
            Require(!Directory.Exists(missing), "The missing-cache fixture was not new.");
            yield return InspectCacheTwice(missing, "missing", null, Array.Empty<string>(), rounds, report =>
            {
                Require(!Directory.Exists(missing) && report.Files.Count == 0 && report.Owned.Bytes == 0 &&
                    report.Owned.Certainty == ResourceCacheCertainty.Exact && report.CleanupCandidates.Bytes == 0,
                    "Inspecting a missing cache must leave it absent and report an exact zero.");
            });

            AssertInstalledDirectoryReadOnly(cache);
            _row.cacheInspection.writeDenied = true;
            Require(ReadIsDenied(denied), "The denied-child fixture allowed its ciphertext body to be read.");
            yield return InspectCacheTwice(cache, "read-only-denied-and-unattributed", null, new[] { denied }, rounds, report =>
            {
                Require(report.Files.Count == 3 && report.Files.All(file => !file.OwnershipConfirmed &&
                    file.Disposition == ResourceCacheDisposition.Undetermined) && report.Owned.Bytes == null &&
                    report.Owned.Certainty == ResourceCacheCertainty.Unknown && report.CleanupCandidates.Bytes == null &&
                    report.Files.Any(file => file.RelativePath == Path.GetFileName(legacy)),
                    "The cold report attributed an inaccessible or unproven file to the cipher cache.");
            });
            Require(ReadIsDenied(denied), "Cache inspection changed the reader's access to the denied file.");
            _row.cacheInspection.readDenied = true;
            _row.cacheInspection.rounds = rounds.ToArray();
        }

        private IEnumerator InspectOpenCache()
        {
            string cache = Argument("-ylFrozenCache");
            string gate = Argument("-ylFrozenCacheGate");
            var open = OpenExperimentSession(cache);
            yield return Until(() => open.IsCompleted);
            var session = open.GetAwaiter().GetResult();
            var rounds = new List<CacheInspectionRound>();
            _row.cacheInspection = new CacheInspectionEvidence();
            var prefetch = Addressables.DownloadDependenciesAsync(SmallKey, false);
            try
            {
                CacheEntry prefix = null;
                yield return AwaitHeldCiphertextPrefix(cache, gate, prefetch, (received, sent) =>
                {
                    prefix = received;
                    _row.cacheInspection.heldPrefixBytes = sent;
                    _row.cacheInspection.stagedPrefixBytes = received.bytes;
                });
                Require(!prefetch.IsDone, "The ciphertext was already finished at the in-flight checkpoint.");

                string part = prefix.path;
                string proof = part + EncryptedBundleCache.TransferProofSuffix;
                string owner = Path.Combine(cache, EncryptedBundleCache.OwnerFile);
                yield return InspectCacheTwice(cache, "in-flight", session, new[] { part, proof, owner }, rounds, report =>
                {
                    var file = report.Files.SingleOrDefault(item => item.RelativePath == Path.GetFileName(part));
                    Require(!prefetch.IsDone && file != null && file.OwnershipConfirmed && file.Bytes == prefix.bytes &&
                        file.Retention.HasFlag(ResourceCacheRetention.Preparation) && file.Disposition == ResourceCacheDisposition.Retained &&
                        report.Owned.Certainty == ResourceCacheCertainty.ObservedLowerBound && report.CleanupCandidates.Bytes == null,
                        "The current session did not account conservatively for its held ciphertext prefix.");
                });

                ReleaseCacheGate(gate);
                yield return Until(() => prefetch.IsDone);
                Require(prefetch.Status == AsyncOperationStatus.Succeeded, "Held prefetch failed: " + prefetch.OperationException);
                string final = Directory.EnumerateFiles(cache, "*.bin").Single();
                CacheEntry completed = CacheEntryMetadata(final);
                Require(completed.fileId == prefix.fileId && completed.bytes > prefix.bytes && !File.Exists(part) && !File.Exists(proof),
                    "The successful transport did not publish the same staged file.");
                yield return InspectCacheTwice(cache, "warm", session, new[] { owner }, rounds, report =>
                {
                    var file = report.Files.Single();
                    Require(file.OwnershipConfirmed && file.RelativePath == Path.GetFileName(final) && file.Bytes == completed.bytes &&
                        file.Retention.HasFlag(ResourceCacheRetention.DeliveryDescription) &&
                        report.Owned.Bytes == completed.bytes && report.Owned.Certainty == ResourceCacheCertainty.Exact &&
                        report.Retained.Bytes == completed.bytes && report.CleanupCandidates.Bytes == 0,
                        "The warm report did not match the verified delivered ciphertext.");
                });
                _row.cacheInspection.rounds = rounds.ToArray();
            }
            finally
            {
                // 凡进入过暂停传输的轮次，都先解开端点，再交回本轮取得的 Addressables 句柄。
                try { ReleaseCacheGate(gate); }
                finally { if (prefetch.IsValid()) Addressables.Release(prefetch); }
            }
        }

        private static IEnumerator AwaitHeldCiphertextPrefix(string cache, string gate, AsyncOperationHandle prefetch,
            Action<CacheEntry, long> completed)
        {
            string marker = Path.Combine(gate, "in-flight");
            long sent = 0;
            yield return Until(() =>
            {
                if (sent == 0)
                {
                    if (!File.Exists(marker))
                        return false;
                    Require(long.TryParse(File.ReadAllText(marker), NumberStyles.None, CultureInfo.InvariantCulture, out sent) &&
                        sent > 0, "The endpoint did not publish a valid paused prefix length.");
                }
                // 端点已暂停，等待预下载确认消费完已发前缀。落盘长度另读，避免把有缓冲的写入当作磁盘字节。
                long consumed = prefetch.GetDownloadStatus().DownloadedBytes;
                Require(!prefetch.IsDone && consumed <= sent, "The download continued beyond the held prefix.");
                if (consumed < sent)
                    return false;
                string part = Directory.EnumerateFiles(cache, "*.part").SingleOrDefault();
                if (part == null)
                    return false;
                var prefix = CacheEntryMetadata(part);
                if (prefix.bytes == 0)
                    return false;
                completed?.Invoke(prefix, sent);
                return true;
            });
        }

        private IEnumerator InspectCacheTwice(string root, string phase, EncryptedDeliverySession session, string[] metadataOnly,
            List<CacheInspectionRound> rounds, Action<ResourceCacheUsageReport> verify)
        {
            string previousReport = null;
            for (int i = 0; i < 2; ++i)
            {
                var round = new CacheInspectionRound { phase = phase, before = CaptureCacheInventory(root, metadataOnly) };
                rounds.Add(round);
                _row.cacheInspection.rounds = rounds.ToArray();
                int keys = _experimentKeys?.Opens ?? 0;
                int mounts = session?.Provider.Creates ?? 0;
                round.startedUtc = DateTimeOffset.UtcNow.ToString("o");
                var inspecting = EncryptedDeliverySession.InspectCacheAsync(root, session);
                yield return Until(() => inspecting.IsCompleted);
                var report = inspecting.GetAwaiter().GetResult();
                round.report = RecordCacheReport(report);
                round.endedUtc = DateTimeOffset.UtcNow.ToString("o");
                round.keyDelta = (_experimentKeys?.Opens ?? 0) - keys;
                round.mountDelta = (session?.Provider.Creates ?? 0) - mounts;
                round.after = CaptureCacheInventory(root, metadataOnly);
                Require(round.keyDelta == 0 && round.mountDelta == 0 && round.before.Select(JsonEntry)
                    .SequenceEqual(round.after.Select(JsonEntry), StringComparer.Ordinal),
                    "InspectCacheAsync changed the cache inventory, file identity, content, key usage or mounts.");
                verify(report);
                string currentReport = JsonUtility.ToJson(round.report);
                Require(previousReport == null || currentReport == previousReport, "Two observations of a held fixture disagreed.");
                previousReport = currentReport;
            }

            static string JsonEntry(CacheEntry entry) => JsonUtility.ToJson(entry);
        }

        private CacheEntry[] CaptureCacheInventory(string root, string[] metadataOnly)
        {
            var paths = new List<string> { Argument("-ylFrozenCacheSentinel"),
                Path.Combine(Addressables.RuntimePath, _catalogName),
                Path.Combine(Addressables.RuntimePath, "settings.json"),
                Path.Combine(Addressables.RuntimePath, EncryptedBundleDescription.FileName) };
            if (Directory.Exists(root))
            {
                paths.Add(root);
                paths.AddRange(Directory.EnumerateFileSystemEntries(root));
            }
            return paths.OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            {
                var entry = CacheEntryMetadata(path);
                if ((entry.attributes & 0x10) == 0 && !metadataOnly.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    using var reader = File.OpenRead(path);
                    entry.sha256 = AuthenticatedBundleStream.Hash(reader, default);
                }
                return entry;
            }).ToArray();
        }

        private static CacheEntry CacheEntryMetadata(string path)
        {
            // 只读文件元数据。持有独占写入句柄的在途文件也能取得物理身份及长度。
            using var file = OpenCacheEvidenceMetadata(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (file.IsInvalid || !GetFileInformationByHandle(file, out var info))
                throw new IOException("Could not read the fixture identity at " + path, new Win32Exception(Marshal.GetLastWin32Error()));
            Require((info.Attributes & 0x400) == 0, "The cache fixture contains a reparse point.");
            return new CacheEntry
            {
                path = path,
                fileId = $"{info.VolumeSerial:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}",
                created = $"{info.CreationHigh:X8}{info.CreationLow:X8}",
                lastWrite = $"{info.WriteHigh:X8}{info.WriteLow:X8}",
                bytes = ((long)info.SizeHigh << 32) | info.SizeLow,
                attributes = info.Attributes
            };
        }

        private static CacheReportEvidence RecordCacheReport(ResourceCacheUsageReport report) => new CacheReportEvidence
        {
            files = report.Files.Select(file => $"{file.RelativePath} {file.Kind} {file.Bytes} {file.OwnershipConfirmed} {file.Disposition} {file.Retention}").ToArray(),
            issues = report.Issues.OrderBy(issue => issue, StringComparer.Ordinal).ToArray(),
            ownedCertainty = report.Owned.Certainty.ToString(),
            retainedCertainty = report.Retained.Certainty.ToString(),
            candidateCertainty = report.CleanupCandidates.Certainty.ToString(),
            undeterminedCertainty = report.Undetermined.Certainty.ToString(),
            ownedBytes = report.Owned.Bytes?.ToString(),
            retainedBytes = report.Retained.Bytes?.ToString(),
            candidateBytes = report.CleanupCandidates.Bytes?.ToString(),
            undeterminedBytes = report.Undetermined.Bytes?.ToString()
        };

        private static bool ReadIsDenied(string path)
        {
            try { using var opened = File.OpenRead(path); }
            catch (UnauthorizedAccessException) { return true; }
            return false;
        }

        private static void ReleaseCacheGate(string gate)
        {
            string release = Path.Combine(gate, "release");
            if (!File.Exists(release))
                using (File.Open(release, FileMode.CreateNew)) { }
        }
    }
}
