using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using FileIdentity = System.ValueTuple<ulong, uint, ulong>;
using FileStamp = System.ValueTuple<long, ulong, uint, ulong, ulong>;

namespace YLFramework.Resource
{
    /// <summary>签名事务可选的密文接收能力。主包负责暂存、签名身份复核和发布。</summary>
    public interface IResourceContentCiphertextTransfer
    {
        /// <summary>接收清单所签的密文。按顺序等待每次写入，直至写入返回才可重用该段缓冲。</summary>
        Task ReceiveCiphertextAsync(ResourceContentArtifact artifact, Func<ReadOnlyMemory<byte>, CancellationToken, Task> accept,
            CancellationToken cancellation);
    }

    public sealed partial class ResourceContentUpdates
    {
        private const string ResumePrefix = ".resume-";
        private const string PartSuffix = ".part";
        private const string MarkSuffix = ".mark";
        private const int MaximumCheckpointBytes = 16 * 1024;
        private const int CheckpointSlotPayloadBytes = 2 * sizeof(long);
        private const int CheckpointSlotBytes = CheckpointSlotPayloadBytes + 32;
        private const int CheckpointTrailerBytes = 2 * CheckpointSlotBytes;
        private const int MaximumCheckpointRecordBytes = MaximumCheckpointBytes + CheckpointTrailerBytes;
        private const int CheckpointInterval = 1024 * 1024;
        private const long RetiredCheckpointOffset = -1;
        private static readonly TimeSpan _artifactInactivityLimit = TimeSpan.FromMinutes(5);

        internal Func<string, long?> AvailableSpaceOverride
        {
            get; set;
        }
        internal Action<string> BeforeVerifiedArtifactPublish
        {
            get; set;
        }
        internal Action<string, long> BeforeCheckpointRecordWrite
        {
            get; set;
        }
        internal Action<string> AfterCheckpointIntegrityFailure
        {
            get; set;
        }

        private sealed class ResumeCheckpoint
        {
            internal ResumeCheckpoint(string source, string sha256, long length, long offset, EntityTagHeaderValue tag)
            {
                Source = source;
                Sha256 = sha256;
                Length = length;
                Offset = offset;
                Tag = tag;
            }

            internal string Source
            {
                get;
            }
            internal string Sha256
            {
                get;
            }
            internal long Length
            {
                get;
            }
            internal long Offset
            {
                get; set;
            }
            internal EntityTagHeaderValue Tag
            {
                get;
            }
            internal FileIdentity? MarkerIdentity
            {
                get; set;
            }
            internal int HeaderLength
            {
                get; set;
            }
            internal long Revision
            {
                get; set;
            }
            internal FileIdentity? PartIdentity
            {
                get; set;
            }
            internal FileIdentity ObservedPart
            {
                get; set;
            }
            internal long ObservedOffset
            {
                get; set;
            }
        }

        private async Task PrepareFileSetAsync(ResourceContentManifest manifest, IEnumerable<string> routes,
            long recordReserve, CancellationToken cancellation)
        {
            var existing = new List<FileStream>();
            var paths = new Dictionary<string, (long Length, string Sha256)>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<(ResourceContentArtifact Artifact, string Path, ResumeCheckpoint Checkpoint)>();
            long bytesNeeded = recordReserve;
            bool needsCheckpointSpace = false;
            try
            {
                foreach (string route in routes)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var artifact = manifest.Artifacts[route];
                    string path = ResolveFile(artifact, _installed);
                    if (paths.TryGetValue(path, out var knownIdentity))
                    {
                        if (knownIdentity.Length != artifact.Length || knownIdentity.Sha256 != artifact.Sha256)
                            throw new InvalidDataException("Signed routes sharing one file disagree on its identity.");
                        continue;
                    }
                    paths.Add(path, (artifact.Length, artifact.Sha256));
                    if (artifact.Source.StartsWith(InstalledPrefix, StringComparison.Ordinal))
                    {
                        using var installedDirectory = ResourceContentFileIO.PinDirectory(Path.GetDirectoryName(path), false);
                        using var installed = ResourceContentFileIO.OpenRead(path);
                        ResourceContentFileIO.RequireIdentity(installed, artifact.Length, artifact.Sha256, cancellation);
                        continue;
                    }
                    if (PinReusableFile(path, artifact, existing, cancellation))
                        continue;

                    bool plain = artifact.Kind != ResourceContentArtifactKind.Ciphertext;
                    var checkpoint = plain ? ReadMatchingCheckpoint(path, artifact, cancellation) : null;
                    long needed = artifact.Length - (checkpoint?.ObservedOffset ?? 0);
                    needsCheckpointSpace |= plain && needed > 0;
                    bytesNeeded = checked(bytesNeeded + needed);
                    missing.Add((artifact, path, checkpoint));
                }

                const int checkpointReserve = 2 * MaximumCheckpointRecordBytes;
                RequirePreparationSpace(checked(bytesNeeded + (needsCheckpointSpace ? checkpointReserve : 0)));
                foreach (var (artifact, path, checkpoint) in missing)
                {
                    cancellation.ThrowIfCancellationRequested();
                    bytesNeeded -= artifact.Length - (checkpoint?.ObservedOffset ?? 0);
                    if (artifact.Kind == ResourceContentArtifactKind.Ciphertext)
                        await DownloadWholeCiphertextAsync(artifact, path, cancellation).ConfigureAwait(false);
                    else
                        await DownloadOrdinaryAsync(artifact, path, checkpoint, bytesNeeded, cancellation).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var input in existing)
                    input.Dispose();
            }
        }

        private static bool PinReusableFile(string path, ResourceContentArtifact artifact, List<FileStream> pins,
            CancellationToken cancellation)
        {
            FileStream file;
            try
            {
                file = ResourceContentFileIO.OpenRead(path);
            }
            catch (FileNotFoundException) { return false; }
            try
            {
                if (file.Length != artifact.Length || ResourceContentFileIO.Hash(file, cancellation) != artifact.Sha256)
                    return false;
                pins.Add(file);
                file = null;
                return true;
            }
            finally { file?.Dispose(); }
        }

        private void RequirePreparationSpace(long bytesNeeded)
        {
            if (bytesNeeded == 0)
                return;
            var measure = AvailableSpaceOverride;
            long? free = measure == null ? ReadAvailableSpace(ObjectsRoot) : measure(ObjectsRoot);
            if (free is long available && available < bytesNeeded)
                throw new ResourceException(ResourceError.InsufficientStorage,
                    $"Signed content preparation needs {bytesNeeded} additional bytes. The target volume has {available} bytes available.");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong totalFree);

        private static long? ReadAvailableSpace(string directory)
        {
            if (GetDiskFreeSpaceExW(directory, out ulong available, out _, out _))
                return available > long.MaxValue ? long.MaxValue : (long)available;
            Debug.LogWarning($"Signed content target volume free space is unknown (Win32 {Marshal.GetLastWin32Error()}).");
            return null;
        }

        private static bool IsDiskFull(IOException error) => (error.HResult & 0xffff) is 39 or 112 ||
            (error.InnerException as Win32Exception)?.NativeErrorCode is 39 or 112;

        private static ResourceException DiskFull(IOException error) => new(ResourceError.InsufficientStorage,
            "The target volume ran out of space while preparing signed content.", error);

        private async Task DownloadExtensionCiphertextAsync(IResourceContentCiphertextTransfer transport,
            ResourceContentArtifact artifact, string path, CancellationToken cancellation)
        {
            var source = new Uri(artifact.Source, UriKind.Absolute);
            _trust.RequireTrustedSource(source);
            using var stage = CreateStage(TemporaryPath(ObjectsRoot));
            using (var output = stage.Stream)
            {
                await transport.ReceiveCiphertextAsync(artifact, WriteChunk, cancellation).ConfigureAwait(false);
                output.Flush(flushToDisk: true);

                async Task WriteChunk(ReadOnlyMemory<byte> chunk, CancellationToken transfer)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (chunk.Length > artifact.Length - output.Position)
                        throw new InvalidDataException("The received ciphertext exceeds the signed artifact length.");
                    ArtifactReceived?.Invoke(source, chunk.Length);
                    BeforeArtifactWrite?.Invoke(source, output, chunk.Length);
                    await output.WriteAsync(chunk, transfer).ConfigureAwait(false);
                }
            }
            stage.Publish(path, artifact.Length, artifact.Sha256, cancellation);
            ArtifactPublishedBoundary?.Invoke(path);
        }

        private async Task DownloadOrdinaryAsync(ResourceContentArtifact artifact, string path, ResumeCheckpoint checkpoint,
            long otherBytesNeeded, CancellationToken cancellation)
        {
            if (checkpoint?.ObservedOffset == artifact.Length)
            {
                PublishCheckpoint(path, artifact, checkpoint, cancellation);
                return;
            }

            var uri = new Uri(artifact.Source, UriKind.Absolute);
            _trust.RequireTrustedSource(uri);
            while (true)
            {
                long start = checkpoint?.Offset ?? 0;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (checkpoint != null)
                {
                    request.Headers.Range = new RangeHeaderValue(start, null);
                    request.Headers.IfRange = new RangeConditionHeaderValue(checkpoint.Tag);
                }
                deadline.CancelAfter(_artifactInactivityLimit);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                if (checkpoint != null && response.StatusCode != HttpStatusCode.OK)
                {
                    if (TryReadContinuation(response, artifact, checkpoint, out long length))
                    {
                        await ReceiveOrdinaryAsync(response, uri, path, checkpoint, start, length, deadline, cancellation).ConfigureAwait(false);
                        if (checkpoint.Offset < artifact.Length)
                            continue;
                        PublishCheckpoint(path, artifact, checkpoint, cancellation);
                        return;
                    }
                    if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
                        response.EnsureSuccessStatusCode();
                    // A range proof failed. Retire its record before at most one full-body fallback.
                    RetireCheckpoint(path, checkpoint, cancellation);
                    checkpoint = null;
                    RequirePreparationSpace(checked(otherBytesNeeded + artifact.Length + 2 * MaximumCheckpointRecordBytes));
                    continue;
                }
                if (checkpoint != null)
                {
                    RetireCheckpoint(path, checkpoint, cancellation);
                    RequirePreparationSpace(checked(otherBytesNeeded + artifact.Length + 2 * MaximumCheckpointRecordBytes));
                }
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 ||
                    response.Content.Headers.ContentRange != null ||
                    (response.Content.Headers.ContentLength is long declared && declared != artifact.Length))
                    throw new InvalidDataException("The patch server did not provide the signed complete representation.");
                var tag = response.Headers.ETag;
                checkpoint = IsStrongTag(tag) && !ResumeNamesOccupied(path) &&
                    _utf8.GetByteCount(artifact.Source) + _utf8.GetByteCount(tag.Tag) + 128 <= MaximumCheckpointBytes
                    ? new ResumeCheckpoint(artifact.Source, artifact.Sha256, artifact.Length, 0, tag) : null;
                await ReceiveOrdinaryAsync(response, uri, path, checkpoint, 0, artifact.Length, deadline, cancellation).ConfigureAwait(false);
                if (checkpoint != null)
                    PublishCheckpoint(path, artifact, checkpoint, cancellation);
                return;
            }
        }

        private static bool TryReadContinuation(HttpResponseMessage response, ResourceContentArtifact artifact,
            ResumeCheckpoint checkpoint, out long length)
        {
            length = 0;
            if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentEncoding.Count != 0 ||
                response.Headers.ETag?.IsWeak != false || response.Headers.ETag.Tag != checkpoint.Tag.Tag)
                return false;
            var range = response.Content.Headers.ContentRange;
            if (range == null || range.Unit != "bytes" || range.Length != artifact.Length ||
                range.From != checkpoint.Offset || !(range.To is long end && end >= checkpoint.Offset && end < artifact.Length))
                return false;
            length = end - checkpoint.Offset + 1;
            return !(response.Content.Headers.ContentLength is long declared) || declared == length;
        }

        private async Task ReceiveOrdinaryAsync(HttpResponseMessage response, Uri uri, string path, ResumeCheckpoint checkpoint,
            long start, long length, CancellationTokenSource deadline, CancellationToken cancellation)
        {
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            string temporary = checkpoint == null ? TemporaryPath(ObjectsRoot) : ResumePath(path, PartSuffix);
            using var stage = start == 0 ? CreateStage(temporary) : null;
            bool invalidBody = false;
            CachePreparation resumedPreparation = null;
            try
            {
                using (var output = stage?.Stream ?? ResourceContentFileIO.OpenReadWriteStage(temporary))
                {
                    var identity = stage?.Identity ?? ResourceContentFileIO.ReadFileIdentity(output);
                    if (output.Length != start || (start != 0 && identity != checkpoint.ObservedPart))
                    {
                        invalidBody = true;
                        throw new InvalidDataException("The staged file changed after checking the checkpoint.");
                    }
                    if (start != 0)
                    {
                        var preparation = Volatile.Read(ref _cachePreparation);
                        preparation?.RegisterStage(temporary, identity);
                        resumedPreparation = preparation;
                    }
                    if (checkpoint != null && stage != null)
                    {
                        checkpoint.PartIdentity = checkpoint.ObservedPart = identity;
                        // 先记录实际创建的文件身份，即使在第一次刷入正文前退出也能认领。
                        FlushCheckpoint(output, path, checkpoint, 0);
                        stage.Preserve();
                    }
                    output.Position = start;
                    var buffer = new byte[CopyBufferBytes];
                    long written = start;
                    try
                    {
                        long end = checked(start + length);
                        while (written < end)
                        {
                            int count = await ReadProgressAsync(input, buffer, (int)Math.Min(buffer.Length, end - written),
                                deadline).ConfigureAwait(false);
                            if (count == 0)
                                throw new IOException("The patch response ended before its declared bound.");
                            ArtifactReceived?.Invoke(uri, count);
                            BeforeArtifactWrite?.Invoke(uri, output, count);
                            await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                            written += count;
                            if (checkpoint != null && written < end && written - checkpoint.Offset >= CheckpointInterval)
                                FlushCheckpoint(output, path, checkpoint, written);
                        }
                        if (await ReadProgressAsync(input, buffer, 1, deadline).ConfigureAwait(false) != 0)
                        {
                            ArtifactReceived?.Invoke(uri, 1);
                            invalidBody = true;
                            throw new InvalidDataException("The patch response exceeds its declared bound.");
                        }
                    }
                    catch (Exception error) when (checkpoint != null && !invalidBody && written < checkpoint.Length &&
                        (error is OperationCanceledException || error is IOException io && !IsDiskFull(io) || error is HttpRequestException))
                    {
                        if (written > checkpoint.Offset && output.Length == written)
                            FlushCheckpoint(output, path, checkpoint, written);
                        throw;
                    }
                    if (checkpoint != null)
                        FlushCheckpoint(output, path, checkpoint, written);
                    else
                        output.Flush(flushToDisk: true);
                }
                if (checkpoint == null)
                {
                    stage.Publish(path, length, Path.GetFileNameWithoutExtension(path), cancellation);
                    ArtifactPublishedBoundary?.Invoke(path);
                }
            }
            finally
            {
                resumedPreparation?.UnregisterStage(temporary);
                if (invalidBody)
                    RetireCheckpoint(path, checkpoint, CancellationToken.None);
            }
        }

        private static async Task<int> ReadProgressAsync(Stream stream, byte[] buffer, int count, CancellationTokenSource deadline)
        {
            deadline.CancelAfter(_artifactInactivityLimit);
            try
            {
                return await stream.ReadAsync(buffer, 0, count, deadline.Token).ConfigureAwait(false);
            }
            finally { deadline.CancelAfter(Timeout.InfiniteTimeSpan); }
        }

        private void PublishCheckpoint(string path, ResourceContentArtifact artifact, ResumeCheckpoint checkpoint,
            CancellationToken cancellation)
        {
            string part = ResumePath(path, PartSuffix);
            try
            {
                BeforeVerifiedArtifactPublish?.Invoke(part);
                ResourceContentFileIO.VerifyAndPublishStage(part, path, artifact.Length, artifact.Sha256,
                    cancellation, checkpoint.ObservedPart);
            }
            catch (InvalidDataException)
            {
                AfterCheckpointIntegrityFailure?.Invoke(part);
                RetireCheckpoint(path, checkpoint, CancellationToken.None);
                throw;
            }
            RemoveResumeMarker(path, checkpoint, CancellationToken.None);
            ArtifactPublishedBoundary?.Invoke(path);
        }

        private static string ResumePath(string path, string suffix) =>
            Path.Combine(Path.GetDirectoryName(path), ResumePrefix + Path.GetFileName(path) + suffix);

        private static bool IsStrongTag(EntityTagHeaderValue tag) => tag != null && !tag.IsWeak &&
            tag.Tag.Length > 1 && tag.Tag[0] == '"' && tag.Tag[tag.Tag.Length - 1] == '"';

        private void FlushCheckpoint(FileStream output, string path, ResumeCheckpoint checkpoint, long offset)
        {
            output.Flush(flushToDisk: true);
            if (checkpoint.MarkerIdentity.HasValue)
            {
                BeforeCheckpointRecordWrite?.Invoke(ResumePath(path, PartSuffix), offset);
                WriteCheckpointOffset(path, checkpoint, offset);
            }
            else
            {
                PublishResumeMarker(path, checkpoint);
            }
            checkpoint.ObservedOffset = offset;
        }

        private void PublishResumeMarker(string path, ResumeCheckpoint checkpoint)
        {
            string marker = ResumePath(path, MarkSuffix);
            using var stage = CreateStage(TemporaryPath(Path.GetDirectoryName(path)), isAsync: false);
            using (var file = stage.Stream)
            {
                using var writer = new BinaryWriter(file, _utf8, leaveOpen: true);
                var part = checkpoint.PartIdentity.Value;
                writer.Write(2);
                writer.Write(checkpoint.Source);
                writer.Write(checkpoint.Sha256);
                writer.Write(checkpoint.Length);
                writer.Write(0L);
                writer.Write(checkpoint.Tag.Tag);
                writer.Write(part.Item1);
                writer.Write(part.Item2);
                writer.Write(part.Item3);
                writer.Flush();
                checkpoint.HeaderLength = checked((int)file.Position);
                writer.Write(new byte[CheckpointTrailerBytes]);
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            checkpoint.MarkerIdentity = IdentityOf(ResourceContentFileIO.PublishNewStage(stage.Path, marker, stage.Identity));
            stage.Preserve();
        }

        private static void WriteCheckpointOffset(string path, ResumeCheckpoint checkpoint, long offset)
        {
            using var file = ResourceContentFileIO.OpenCheckpointRecord(ResumePath(path, MarkSuffix), writing: true);
            if (!MatchesResumeMarker(file, checkpoint))
                throw new InvalidDataException("The checkpoint marker changed during reception.");

            // Keep the identity header and the previous revision intact until the new slot is durable.
            long revision = checked(checkpoint.Revision + 1);
            var record = new byte[CheckpointSlotBytes];
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(), revision);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(sizeof(long)), offset);
            using var hash = SHA256.Create();
            hash.ComputeHash(record, 0, CheckpointSlotPayloadBytes).CopyTo(record, CheckpointSlotPayloadBytes);
            file.Position = checkpoint.HeaderLength + ((revision - 1) % 2) * CheckpointSlotBytes;
            file.Write(record, 0, record.Length);
            file.Flush(flushToDisk: true);
            checkpoint.Offset = offset;
            checkpoint.Revision = revision;
        }

        private ResumeCheckpoint ReadMatchingCheckpoint(string path, ResourceContentArtifact artifact,
            CancellationToken cancellation)
        {
            ResumeCheckpoint saved;
            FileStamp? part;
            try
            {
                saved = ReadResumeMarker(path);
                if (saved == null)
                    return null;
                // v1 部分前缀无法确定文件身份，保留原位并改用独立暂存接收。
                if (!saved.PartIdentity.HasValue && saved.Offset != saved.Length)
                    return null;
                part = ReadResumeStamp(ResumePath(path, PartSuffix));
            }
            catch (Exception error) when (ResourceCacheUsageScan.IsObservationFailure(error))
            {
                // 不能核对的路径由原主保留，本次可以用独立暂存接收。
                return null;
            }
            if (saved.Source == artifact.Source && saved.Length == artifact.Length && IsUsableCheckpoint(saved, part))
            {
                saved.ObservedPart = IdentityOf(part.Value);
                saved.ObservedOffset = part.Value.Item1;
                return saved;
            }
            // 只回收能确认归属的失败暂存，再计算本次所需的总空间。
            RetireCheckpoint(path, saved, cancellation);
            return null;
        }

        private ResumeCheckpoint ReadResumeMarker(string path)
        {
            ResumeCheckpoint checkpoint;
            try
            {
                using var file = ResourceContentFileIO.OpenCheckpointRecord(ResumePath(path, MarkSuffix));
                if (file.Length > MaximumCheckpointRecordBytes)
                    return null;
                checkpoint = ReadCheckpoint(ReadBounded(file, MaximumCheckpointRecordBytes));
                if (checkpoint == null || checkpoint.Sha256 != Path.GetFileNameWithoutExtension(path) ||
                    !Uri.TryCreate(checkpoint.Source, UriKind.Absolute, out var uri))
                    return null;
                _trust.RequireTrustedSource(uri);
                checkpoint.MarkerIdentity = ResourceContentFileIO.ReadFileIdentity(file);
            }
            catch (FileNotFoundException) { return null; }
            catch (InvalidDataException) { return null; }
            return checkpoint;
        }

        private static ResumeCheckpoint ReadCheckpoint(byte[] contents)
        {
            try
            {
                using var data = new MemoryStream(contents, writable: false);
                using var reader = new BinaryReader(data, _utf8);
                int version = reader.ReadInt32();
                if (version is not (1 or 2))
                    return null;
                string source = ReadCheckpointString(reader);
                string sha = ReadCheckpointString(reader);
                long length = reader.ReadInt64();
                long offset = reader.ReadInt64();
                if (length <= 0 || offset < 0 || offset > length || (version == 1 && offset == 0) ||
                    !ResourceContentStore.IsDigest(sha) ||
                    !EntityTagHeaderValue.TryParse(ReadCheckpointString(reader), out var tag) || !IsStrongTag(tag))
                    return null;
                var checkpoint = new ResumeCheckpoint(source, sha, length, offset, tag);
                if (version == 1)
                    return data.Position == data.Length ? checkpoint : null;
                checkpoint.PartIdentity = (reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt64());
                checkpoint.HeaderLength = checked((int)data.Position);
                if (checkpoint.HeaderLength > MaximumCheckpointBytes || data.Length - data.Position > CheckpointTrailerBytes)
                    return null;
                ReadCheckpointOffsets(contents, checkpoint);
                return checkpoint;
            }
            catch (Exception error) when (error is EndOfStreamException or InvalidDataException or FormatException or ArgumentException)
            {
                return null;
            }
        }

        private static void ReadCheckpointOffsets(byte[] contents, ResumeCheckpoint checkpoint)
        {
            if (contents.Length - checkpoint.HeaderLength < CheckpointSlotBytes)
                return;
            using var hash = SHA256.Create();
            for (int slot = 0; slot < 2; slot++)
            {
                int start = checkpoint.HeaderLength + slot * CheckpointSlotBytes;
                if (contents.Length - start < CheckpointSlotBytes)
                    break;
                var record = contents.AsSpan(start, CheckpointSlotBytes);
                long revision = BinaryPrimitives.ReadInt64LittleEndian(record);
                long offset = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(sizeof(long)));
                if (revision <= checkpoint.Revision || (revision - 1) % 2 != slot || offset < RetiredCheckpointOffset ||
                    offset > checkpoint.Length || !record.Slice(CheckpointSlotPayloadBytes).SequenceEqual(
                        hash.ComputeHash(contents, start, CheckpointSlotPayloadBytes)))
                    continue;
                checkpoint.Offset = offset;
                checkpoint.Revision = revision;
            }
        }

        private static bool MatchesResumeMarker(FileStream file, ResumeCheckpoint checkpoint)
        {
            if (ResourceContentFileIO.ReadFileIdentity(file) != checkpoint.MarkerIdentity || file.Length > MaximumCheckpointRecordBytes)
                return false;
            var current = ReadCheckpoint(ReadBounded(file, MaximumCheckpointRecordBytes));
            return current != null && current.HeaderLength == checkpoint.HeaderLength && current.Source == checkpoint.Source &&
                current.Sha256 == checkpoint.Sha256 && current.Length == checkpoint.Length && current.Tag.Tag == checkpoint.Tag.Tag &&
                current.PartIdentity == checkpoint.PartIdentity && current.Revision == checkpoint.Revision && current.Offset == checkpoint.Offset;
        }

        private static FileStamp? ReadResumeStamp(string path)
        {
            try
            {
                return ResourceContentFileIO.ReadFileStamp(path);
            }
            catch (FileNotFoundException) { return null; }
        }

        private static FileIdentity IdentityOf(FileStamp stamp) => (stamp.Item2, stamp.Item3, stamp.Item4);

        private static bool IsUsableCheckpoint(ResumeCheckpoint checkpoint, FileStamp? part)
        {
            if (checkpoint.Offset == RetiredCheckpointOffset || part is not FileStamp current)
                return false;
            long length = current.Item1;
            if (checkpoint.PartIdentity is FileIdentity owner)
                return owner == IdentityOf(current) && (length == checkpoint.Length || (checkpoint.Offset > 0 && length == checkpoint.Offset));
            return length == checkpoint.Length && checkpoint.Offset == length;
        }

        private static string ReadCheckpointString(BinaryReader reader)
        {
            // BinaryWriter strings carry a 7-bit byte length. Bound it before reading any string.
            int length = 0;
            for (int shift = 0; shift <= 14; shift += 7)
            {
                int next = reader.ReadByte();
                length |= (next & 0x7f) << shift;
                if (length > MaximumCheckpointBytes)
                    break;
                if ((next & 0x80) == 0)
                {
                    if (length > reader.BaseStream.Length - reader.BaseStream.Position)
                        break;
                    return _utf8.GetString(reader.ReadBytes(length));
                }
            }
            throw new InvalidDataException("Invalid patch checkpoint string length.");
        }

        private static bool IsResumeFileName(string name)
        {
            string suffix = Path.GetExtension(name);
            if ((suffix != PartSuffix && suffix != MarkSuffix) || !name.StartsWith(ResumePrefix, StringComparison.Ordinal))
                return false;
            string destination = name.Substring(ResumePrefix.Length, name.Length - ResumePrefix.Length - suffix.Length);
            return ResourceContentStore.IsDigest(Path.GetFileNameWithoutExtension(destination)) &&
                Path.GetExtension(destination) is ".bundle" or ".json" or ".bin";
        }

        private static string ResumeObjectPath(string stagingPath)
        {
            string name = Path.GetFileNameWithoutExtension(stagingPath);
            return Path.Combine(Path.GetDirectoryName(stagingPath), name.Substring(ResumePrefix.Length));
        }

        private static bool ResumeNamesOccupied(string objectPath)
        {
            return Occupied(ResumePath(objectPath, PartSuffix)) || Occupied(ResumePath(objectPath, MarkSuffix));

            static bool Occupied(string path)
            {
                try
                {
                    File.GetAttributes(path);
                    return true;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    return false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            }
        }

        private int CleanupAbandonedCheckpoint(string path, CancellationToken cancellation)
        {
            ResumeCheckpoint checkpoint;
            try
            {
                checkpoint = ReadResumeMarker(path);
                if (checkpoint == null || IsUsableCheckpoint(checkpoint, ReadResumeStamp(ResumePath(path, PartSuffix))))
                    return 0;
            }
            catch (Exception error) when (ResourceCacheUsageScan.IsObservationFailure(error))
            {
                // 记录或数据无法归属时保留原位。
                return 0;
            }
            return RetireCheckpoint(path, checkpoint, cancellation);
        }

        private static int RetireCheckpoint(string path, ResumeCheckpoint checkpoint, CancellationToken cancellation)
        {
            if (checkpoint?.MarkerIdentity == null)
                return 0;

            cancellation.ThrowIfCancellationRequested();
            if (checkpoint.PartIdentity is not FileIdentity owned)
                return RemoveResumeMarker(path, checkpoint, CancellationToken.None) ? 1 : 0;

            // Flush retirement before trying to reclaim the part. A busy part keeps its ownership record.
            if (checkpoint.Offset != RetiredCheckpointOffset)
                WriteCheckpointOffset(path, checkpoint, RetiredCheckpointOffset);

            string part = ResumePath(path, PartSuffix);
            int removed = 0;
            try
            {
                if (ReadResumeStamp(part) is FileStamp current && IdentityOf(current) == owned)
                {
                    if (!ResourceContentFileIO.TryRemoveOwnedFile(part, opened =>
                            ResourceContentFileIO.ReadFileIdentity(opened) == owned, CancellationToken.None))
                        return 0;
                    removed++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The retired record still identifies the part if it becomes accessible later.
                return removed;
            }
            return RemoveResumeMarker(path, checkpoint, CancellationToken.None) ? removed + 1 : removed;
        }

        private static bool RemoveResumeMarker(string path, ResumeCheckpoint checkpoint, CancellationToken cancellation) =>
            checkpoint.MarkerIdentity.HasValue &&
            ResourceContentFileIO.TryRemoveOwnedFile(ResumePath(path, MarkSuffix),
                opened => MatchesResumeMarker(opened, checkpoint), cancellation);

        private CreatedStage CreateStage(string path, bool isAsync = true) =>
            new(path, Volatile.Read(ref _cachePreparation), isAsync);

        private sealed class CreatedStage : IDisposable
        {
            private readonly CachePreparation _preparation;
            private bool _preserve;

            internal CreatedStage(string path, CachePreparation preparation, bool isAsync)
            {
                Path = path;
                _preparation = preparation;
                Stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferBytes, isAsync);
                try
                {
                    Identity = ResourceContentFileIO.ReadFileIdentity(Stream);
                    _preparation?.RegisterStage(Path, Identity);
                }
                catch
                {
                    Stream.Dispose();
                    throw;
                }
            }

            internal string Path
            {
                get;
            }
            internal FileStream Stream
            {
                get;
            }
            internal FileIdentity Identity
            {
                get;
            }

            internal void Preserve() => _preserve = true;

            internal void Publish(string path, long length, string sha256, CancellationToken cancellation)
            {
                ResourceContentFileIO.VerifyAndPublishStage(Path, path, length, sha256, cancellation, Identity);
                Preserve();
            }

            public void Dispose()
            {
                try
                {
                    Stream.Dispose();
                }
                finally
                {
                    try
                    {
                        if (!_preserve)
                            ResourceContentFileIO.TryRemoveOwnedFile(Path,
                                opened => ResourceContentFileIO.ReadFileIdentity(opened) == Identity, CancellationToken.None);
                    }
                    finally
                    {
                        _preparation?.UnregisterStage(Path);
                    }
                }
            }
        }

    }
}
