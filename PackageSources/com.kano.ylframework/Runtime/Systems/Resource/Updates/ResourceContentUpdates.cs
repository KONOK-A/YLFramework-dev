using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Version = YLFramework.Resource.ResourceContentStore.Version;

namespace YLFramework.Resource
{
    public enum ResourceContentCheckStatus
    {
        Available,
        NoUpdate,
        Rejected
    }

    public enum ResourceContentRejection
    {
        Replay,
        SequenceConflict,
        InvalidAnnouncement,
        Transport
    }

    /// <summary>一次受信发布检查的结果。可准备的候选固定了验签时的载荷。</summary>
    public sealed class ResourceContentCheckResult
    {
        internal ResourceContentCheckResult(ResourceContentCheckStatus status, ResourceContentManifest manifest = null,
            ResourceContentRejection? rejection = null, string detail = null, ResourceContentUpdates owner = null,
            byte[] announcement = null)
        {
            Status = status;
            Manifest = manifest;
            Rejection = rejection;
            Detail = detail;
            Owner = owner;
            Announcement = announcement;
        }

        public ResourceContentCheckStatus Status
        {
            get;
        }
        public ResourceContentManifest Manifest
        {
            get;
        }
        public ResourceContentRejection? Rejection
        {
            get;
        }
        public string Detail
        {
            get;
        }
        internal ResourceContentUpdates Owner
        {
            get;
        }
        internal byte[] Announcement
        {
            get;
        }
    }

    /// <summary>按应用和渠道持久提交的版本记录。</summary>
    public sealed class ResourceContentStateSnapshot
    {
        internal ResourceContentStateSnapshot(ResourceContentStore.State state)
        {
            Generation = state.Generation;
            ActiveSequence = state.Active.Sequence;
            ActiveDigest = state.Active.Digest;
            PreviousSequence = state.Previous?.Sequence;
            PreviousDigest = state.Previous?.Digest;
            HighestSequence = state.HighestSequence;
            HighestDigest = state.HighestDigest;
        }

        public long Generation
        {
            get;
        }
        public long ActiveSequence
        {
            get;
        }
        public string ActiveDigest
        {
            get;
        }
        public long? PreviousSequence
        {
            get;
        }
        public string PreviousDigest
        {
            get;
        }
        public long HighestSequence
        {
            get;
        }
        public string HighestDigest
        {
            get;
        }
    }

    /// <summary>按签名发布检查、准备制品和排定下次启动的版本操作。</summary>
    public sealed partial class ResourceContentUpdates
    {
        internal const string BaselineManifest = "yl-content-manifest.json";
        internal const string BaselineSignature = "yl-content-manifest.sig";
        internal const string SettingsName = "settings.json";
        private const int MaximumAnnouncementBytes = 12 * 1024 * 1024;
        private const string InstalledPrefix = "install:";
        private const int CopyBufferBytes = 128 * 1024;
        private static readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        private static readonly UTF8Encoding _utf8 = new(false, true);

        private readonly ResourceContentStore _store;
        private readonly ResourceContentManifestTrust _trust;
        private readonly Uri _announcementUri;
        private readonly IResourceContentExtension _extension;
        private readonly Type[] _additionalAssetTypes;
        private readonly string _installRoot;

        private Boot _serving;
        private ResourceContentManifest _installed;

        /// <summary>应用提供内置公钥、唯一发布入口、自有数据目录和已随 Player 安装的扩展。</summary>
        /// <remarks>当前版本使用本地 Win64 文件系统。可选 assetTypes 应为本应用已保留在 Player 中的资源类型。</remarks>
        public ResourceContentUpdates(ResourceContentManifestTrust trust, Uri announcementUri, string storageDirectory,
            IResourceContentExtension extension = null, IEnumerable<Type> assetTypes = null)
        {
            ResourceSystem.CheckStartupThread();
            _trust = trust ?? throw new ArgumentNullException(nameof(trust));
            _trust.RequireTrustedSource(announcementUri);
            _announcementUri = announcementUri;
            _installRoot = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "aa"));
            _store = new ResourceContentStore(storageDirectory, trust.Application, trust.Channel);
            if (IsWithin(_store.Root, _installRoot) || IsWithin(_installRoot, _store.Root))
                throw new ArgumentException("The update storage and installed content directories must be independent.", nameof(storageDirectory));

            _extension = extension;
            _additionalAssetTypes = (assetTypes ?? Array.Empty<Type>()).Distinct().ToArray();
            if (_additionalAssetTypes.Any(type => type == null || !typeof(UnityEngine.Object).IsAssignableFrom(type)))
                throw new ArgumentException("Additional asset types must be supported Unity object types.", nameof(assetTypes));
        }

        /// <summary>读取当前持久记录。记录损坏时报告错误。</summary>
        public ResourceContentStateSnapshot ReadState()
        {
            if (!Directory.Exists(_store.Root))
                return null;
            using var directory = ResourceContentFileIO.PinDirectory(_store.Root, false);
            var state = _store.ReadState();
            return state == null ? null : new ResourceContentStateSnapshot(state);
        }

        /// <summary>检查唯一受信入口，读取一份新的完整签名候选。</summary>
        public async Task<ResourceContentCheckResult> CheckAsync(CancellationToken cancellation = default)
        {
            RequireServing();
            byte[] announcement;
            try
            {
                using var timeout = CreateTransferDeadline(cancellation);
                using var response = await GetAsync(_announcementUri, timeout.Token).ConfigureAwait(false);
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                announcement = await ReadBoundedAsync(stream, MaximumAnnouncementBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return new ResourceContentCheckResult(ResourceContentCheckStatus.Rejected,
                    rejection: ResourceContentRejection.Transport, detail: "The update announcement request timed out.");
            }
            catch (Exception error) when (error is HttpRequestException || error is IOException && !(error is InvalidDataException))
            {
                return new ResourceContentCheckResult(ResourceContentCheckStatus.Rejected,
                    rejection: ResourceContentRejection.Transport, detail: error.Message);
            }
            catch (InvalidDataException error)
            {
                return RejectAnnouncement(error);
            }

            ResourceContentManifest manifest;
            try
            {
                manifest = ReadAnnouncement(announcement);
                RequireExtension(manifest);
            }
            catch (Exception error) when (error is InvalidDataException || error is JsonException || error is FormatException ||
                error is DecoderFallbackException)
            {
                return RejectAnnouncement(error);
            }

            var state = _store.ReadState();
            if (state == null)
                throw new ResourceException(ResourceError.UpdateFailed, "The committed content record disappeared.");
            switch (manifest.CompareSequence(state.HighestSequence, state.HighestDigest))
            {
                case ResourceContentSequenceComparison.Lower:
                    return new ResourceContentCheckResult(ResourceContentCheckStatus.Rejected, manifest,
                        ResourceContentRejection.Replay, "The sequence precedes the highest successfully activated release.");
                case ResourceContentSequenceComparison.Conflict:
                    return new ResourceContentCheckResult(ResourceContentCheckStatus.Rejected, manifest,
                        ResourceContentRejection.SequenceConflict, "The release sequence was already committed with a different manifest.");
                case ResourceContentSequenceComparison.Same:
                    return new ResourceContentCheckResult(ResourceContentCheckStatus.NoUpdate, manifest);
                default:
                    try
                    {
                        manifest.RequireUnexpiredAnnouncement(DateTimeOffset.UtcNow);
                    }
                    catch (InvalidDataException error)
                    {
                        return RejectAnnouncement(error);
                    }
                    return new ResourceContentCheckResult(ResourceContentCheckStatus.Available, manifest, owner: this,
                        announcement: announcement);
            }
        }

        private static ResourceContentCheckResult RejectAnnouncement(Exception error) =>
            new(ResourceContentCheckStatus.Rejected, rejection: ResourceContentRejection.InvalidAnnouncement, detail: error.Message);

        /// <summary>下载并封存候选的启动闭包，排入下次启动。当前会话的版本保持冻结。</summary>
        public Task PrepareAsync(ResourceContentCheckResult candidate, CancellationToken cancellation = default) =>
            Task.Run(() => PrepareCandidateAsync(candidate, cancellation), cancellation);

        private async Task PrepareCandidateAsync(ResourceContentCheckResult candidate, CancellationToken cancellation)
        {
            RequireServing();
            if (candidate == null || candidate.Status != ResourceContentCheckStatus.Available || !ReferenceEquals(candidate.Owner, this))
                throw new ArgumentException("Prepare requires an available candidate returned by this service.", nameof(candidate));

            using var directory = ResourceContentFileIO.PinDirectory(_store.Root, false);
            using var writer = AcquireWriter();
            var state = RequireState(writer);
            var journal = writer.ReadJournal();
            var manifest = candidate.Manifest;
            var comparison = manifest.CompareSequence(state.HighestSequence, state.HighestDigest);
            if (comparison == ResourceContentSequenceComparison.Same)
                return;
            if (comparison != ResourceContentSequenceComparison.New)
                throw new ResourceException(ResourceError.StaleBase, "The candidate no longer advances the committed sequence.");
            manifest.RequireUnexpiredAnnouncement(DateTimeOffset.UtcNow);
            using var preparation = TrackCachePreparation(manifest, manifest.StartupRoutes, publishesManifest: true);
            try
            {
                using var objects = ResourceContentFileIO.PinDirectory(ObjectsRoot, true);
                await PrepareFileSetAsync(manifest, manifest.StartupRoutes,
                    candidate.Announcement.LongLength + 2L * ResourceContentStore.MaxRecordBytes, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                SaveAnnouncement(manifest.Digest, candidate.Announcement);

                var version = VersionOf(manifest);
                writer.WriteJournal(new ResourceContentStore.Journal(version,
                    new ResourceContentStore.Intent(version, state.Generation, state.Active), journal.Rollback, null), cancellation);
            }
            catch (IOException error) when (IsDiskFull(error))
            {
                throw DiskFull(error);
            }
        }

        /// <summary>为当前冻结版本补齐可选制品及其签名依赖。无需处理其它可选章节。</summary>
        public Task PrepareActiveAsync(IEnumerable<string> artifactRoutes, CancellationToken cancellation = default)
        {
            if (artifactRoutes == null)
                throw new ArgumentNullException(nameof(artifactRoutes));
            string[] routes = artifactRoutes.ToArray();
            return Task.Run(() => PrepareActiveCoreAsync(routes, cancellation), cancellation);
        }

        private async Task PrepareActiveCoreAsync(string[] artifactRoutes, CancellationToken cancellation)
        {
            var serving = RequireServing();
            var routes = ExpandArtifactRoutes(serving.Manifest, artifactRoutes, cancellation);

            using var directory = ResourceContentFileIO.PinDirectory(_store.Root, false);
            using var writer = AcquireWriter();
            // 另一进程可以提交新版本，当前进程仍从自己的已签清单准备并锁定制品。
            RequireState(writer);
            using var preparation = TrackCachePreparation(serving.Manifest, routes);
            try
            {
                using var objects = ResourceContentFileIO.PinDirectory(ObjectsRoot, true);
                await PrepareFileSetAsync(serving.Manifest, routes, 0, cancellation).ConfigureAwait(false);
            }
            catch (IOException error) when (IsDiskFull(error))
            {
                throw DiskFull(error);
            }
            foreach (var route in routes)
            {
                var artifact = serving.Manifest.Artifacts[route];
                serving.Files.Pin(artifact, ResolveFile(artifact, _installed), cancellation);
            }
        }

        private static HashSet<string> ExpandArtifactRoutes(ResourceContentManifest manifest, IEnumerable<string> roots,
            CancellationToken cancellation = default)
        {
            var routes = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(roots);
            while (pending.Count != 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var route = pending.Dequeue();
                if (route == null || !manifest.Artifacts.TryGetValue(route, out var artifact))
                    throw new ArgumentException("A requested artifact route is outside the frozen manifest.");
                if (routes.Add(route))
                    foreach (var dependency in artifact.Dependencies)
                        pending.Enqueue(dependency);
            }
            return routes;
        }

        /// <summary>排定下次启动显式回滚到上一版。返回是否存在可排定的上一版。</summary>
        public Task<bool> RequestRollbackAsync(CancellationToken cancellation = default) => Task.Run(() =>
        {
            RequireServing();
            using var directory = ResourceContentFileIO.PinDirectory(_store.Root, false);
            using var writer = AcquireWriter();
            var state = RequireState(writer);
            if (state.Previous == null)
                return false;
            var journal = writer.ReadJournal();
            var rollback = new ResourceContentStore.Intent(state.Previous, state.Generation, state.Active);
            cancellation.ThrowIfCancellationRequested();
            writer.WriteJournal(new ResourceContentStore.Journal(journal.Ready, null, rollback, null), cancellation);
            return true;
        }, cancellation);

        /// <summary>保留同一候选并重新排定因强退或验证失败而中断的启动尝试。</summary>
        public Task<bool> RetryPreparedAsync(CancellationToken cancellation = default) => Task.Run(() =>
        {
            RequireServing();
            using var directory = ResourceContentFileIO.PinDirectory(_store.Root, false);
            using var writer = AcquireWriter();
            var state = RequireState(writer);
            var journal = writer.ReadJournal();
            if (journal.Ready == null || journal.Ready.Sequence <= state.HighestSequence)
                return false;
            var activation = new ResourceContentStore.Intent(journal.Ready, state.Generation, state.Active);
            cancellation.ThrowIfCancellationRequested();
            writer.WriteJournal(new ResourceContentStore.Journal(journal.Ready, activation, journal.Rollback, null), cancellation);
            return true;
        }, cancellation);

        private Boot RequireServing() => Volatile.Read(ref _serving) ??
            throw new ResourceException(ResourceError.SessionUnavailable, "Start the signed resource content session first.");

        internal long GetDownloadSize(IEnumerable<string> keys) => RequireServing().GetDownloadSize(keys);

        internal void RequirePreparedDependencies(IEnumerable<string> keys) => RequireServing().RequirePreparedDependencies(keys);

        private void RequireExtension(ResourceContentManifest manifest)
        {
            if (manifest.DescriptionRoute != null && _extension == null)
                throw new InvalidDataException("The candidate requires an encrypted content extension installed with the Player.");
        }

        private ResourceContentStore.Writer AcquireWriter()
        {
            if (!_store.TryAcquireWriter(out var writer))
                throw new ResourceException(ResourceError.Busy, "Another process is changing resource content for this application and channel.");
            return writer;
        }

        private static ResourceContentStore.State RequireState(ResourceContentStore.Writer writer) =>
            writer.ReadState() ?? throw new ResourceException(ResourceError.UpdateFailed, "The committed content record disappeared.");

        private string ObjectsRoot => Path.Combine(_store.Root, "objects");
        private string VersionsRoot => _store.VersionsRoot;
        private string AnnouncementPath(string digest) => Path.Combine(VersionsRoot, digest + ".json");
        private string ObjectPath(ResourceContentArtifact artifact)
        {
            string suffix = artifact.Kind switch
            {
                ResourceContentArtifactKind.Catalog => Path.GetExtension(ResourceContentCatalog.CatalogName),
                ResourceContentArtifactKind.Bundle => ".bundle",
                ResourceContentArtifactKind.Ciphertext => ".cipher",
                ResourceContentArtifactKind.EncryptedDescription => ".json",
                _ => throw new InvalidDataException("The signed artifact has an unsupported file kind.")
            };
            return Path.Combine(ObjectsRoot, artifact.Sha256 + suffix);
        }

        private string ResolveFile(ResourceContentArtifact artifact, ResourceContentManifest baseline)
        {
            if (!artifact.Source.StartsWith(InstalledPrefix, StringComparison.Ordinal))
                return ObjectPath(artifact);
            if (!baseline.Artifacts.TryGetValue(artifact.Route, out var installed) ||
                installed.Source != artifact.Source || installed.OriginReleaseId != artifact.OriginReleaseId ||
                installed.Kind != artifact.Kind || installed.Length != artifact.Length || installed.Sha256 != artifact.Sha256)
                throw new InvalidDataException("A reused installed object does not match the signed baseline identity.");
            string file = Path.GetFullPath(Path.Combine(_installRoot, artifact.Source.Substring(InstalledPrefix.Length)));
            if (!IsWithin(file, _installRoot))
                throw new InvalidDataException("An installed artifact route escapes the sealed Player content root.");
            return file;
        }

        private static bool IsWithin(string path, string directory) =>
            path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private async Task DownloadWholeCiphertextAsync(ResourceContentArtifact artifact, string path, CancellationToken cancellation)
        {
            if (_extension is IResourceContentCiphertextTransfer transport)
            {
                await DownloadExtensionCiphertextAsync(transport, artifact, path, cancellation).ConfigureAwait(false);
                return;
            }
            var uri = new Uri(artifact.Source, UriKind.Absolute);
            using var timeout = CreateTransferDeadline(cancellation);
            using var response = await GetAsync(uri, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long length && length != artifact.Length)
                throw new InvalidDataException("The remote artifact length differs from its signed identity.");
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var stage = CreateStage(TemporaryPath(ObjectsRoot));
            using (var output = stage.Stream)
            {
                var remaining = artifact.Length;
                var buffer = new byte[CopyBufferBytes];
                while (remaining != 0)
                {
                    int bytes = await input.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), timeout.Token).ConfigureAwait(false);
                    if (bytes == 0)
                        throw new InvalidDataException("The remote artifact ended before its signed length.");
                    ArtifactReceived?.Invoke(uri, bytes);
                    BeforeArtifactWrite?.Invoke(uri, output, bytes);
                    await output.WriteAsync(buffer, 0, bytes, timeout.Token).ConfigureAwait(false);
                    remaining -= bytes;
                }
                if (await input.ReadAsync(buffer, 0, 1, timeout.Token).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("The remote artifact exceeds its signed length.");
                output.Flush(true);
            }
            stage.Publish(path, artifact.Length, artifact.Sha256, timeout.Token);
            ArtifactPublishedBoundary?.Invoke(path);
        }

        private static CancellationTokenSource CreateTransferDeadline(CancellationToken cancellation)
        {
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            return deadline;
        }

        private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellation)
        {
            _trust.RequireTrustedSource(uri);
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            try
            {
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        private ResourceContentManifest ReadAnnouncement(byte[] bytes)
        {
            var envelope = JObject.Parse(_utf8.GetString(bytes), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (envelope.Count != 2 || envelope["payload"]?.Type != JTokenType.String || envelope["signature"]?.Type != JTokenType.String)
                throw new InvalidDataException("The content announcement is missing its signed payload or detached signature.");
            return ResourceContentManifest.Verify(Convert.FromBase64String(envelope["payload"].Value<string>()),
                Convert.FromBase64String(envelope["signature"].Value<string>()), _trust);
        }

        private void SaveAnnouncement(string digest, byte[] bytes)
        {
            using var versions = ResourceContentFileIO.PinDirectory(VersionsRoot, true);
            using var stage = CreateStage(TemporaryPath(VersionsRoot), isAsync: false);
            using (var file = stage.Stream)
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            Publish(stage.Path, AnnouncementPath(digest));
            stage.Preserve();
        }

        private static string TemporaryPath(string directory) => Path.Combine(directory, ".stage-" + Guid.NewGuid().ToString("N"));
        private static void Publish(string temporary, string path)
        {
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }

        private static async Task<byte[]> ReadBoundedAsync(Stream input, int maximumBytes, CancellationToken cancellation)
        {
            using var result = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
            {
                if (result.Length + count > maximumBytes)
                    throw new InvalidDataException("The update announcement exceeds its size limit.");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }

        private static byte[] ReadBounded(FileStream input, int maximumBytes)
        {
            if (input.Length > maximumBytes)
                throw new InvalidDataException("A signed release record exceeds its size limit.");
            var bytes = new byte[checked((int)input.Length)];
            for (var offset = 0; offset < bytes.Length;)
            {
                int read = input.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                    throw new EndOfStreamException("A signed release record is truncated.");
                offset += read;
            }
            return bytes;
        }

        private static Version VersionOf(ResourceContentManifest manifest) => new(manifest.Sequence, manifest.Digest);

        internal Action<bool> StateCommitBoundary
        {
            get => _store.StateCommitBoundary;
            set => _store.StateCommitBoundary = value;
        }

        internal Action<string> ArtifactPublishedBoundary
        {
            get; set;
        }
        internal Action<Uri, int> ArtifactReceived
        {
            get; set;
        }
        internal Action<Uri, FileStream, int> BeforeArtifactWrite
        {
            get; set;
        }
    }
}
