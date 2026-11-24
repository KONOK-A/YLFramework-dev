using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace YLFramework.Resource.Encryption
{
    internal sealed class EncryptedBundleCache : IDisposable
    {
        private const string TemporaryPrefix = ".yl-transfer-";
        private readonly object _gate = new object();
        private readonly string _root;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly HashSet<EncryptedBundleRecord> _description;
        private readonly HashSet<Task> _mountWork = new HashSet<Task>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly IDisposable _parents;
        private readonly SafeFileHandle _owner;
        private readonly HttpClient _http;
        private Task _closing;
        private long _verifiedCipherHashBytes;

        // Bytes read by successful full ciphertext SHA-256 checks, not native reads or OS disk I/O.
        internal long VerifiedCipherHashBytes => Interlocked.Read(ref _verifiedCipherHashBytes);

        private enum EntryState
        {
            Unknown, Available, Invalidated
        }

        private sealed class Entry
        {
            internal string Path;
            internal EntryState State;
            internal Attempt Current;
        }

        private sealed class Attempt
        {
            internal readonly bool HadHit;
            internal readonly TransferProgress Progress;
            internal readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task Work;

            internal Attempt(Entry entry, EncryptedBundleRecord record)
            {
                HadHit = entry.State == EntryState.Available;
                Progress = new TransferProgress(HadHit ? 0 : record.CipherLength);
            }
        }

        internal sealed class TransferProgress
        {
            private long _totalBytes;
            private long _downloadedBytes;

            internal TransferProgress(long totalBytes) => _totalBytes = totalBytes;

            internal long TotalBytes => Interlocked.Read(ref _totalBytes);
            internal long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

            internal void SetTotalBytes(long bytes) => Interlocked.Exchange(ref _totalBytes, bytes);
            internal void SetDownloadedBytes(long bytes) => Interlocked.Exchange(ref _downloadedBytes, bytes);
        }

        private EncryptedBundleCache(string root, EncryptedBundleDescription description, string excludedRoot)
        {
            _root = OwnedWindowsFiles.LocalAbsolute(root);
            if (_root.TrimEnd('\\') == Path.GetPathRoot(_root).TrimEnd('\\'))
            {
                throw new ArgumentException("The cipher cache must own a dedicated subdirectory.", nameof(root));
            }
            _description = new HashSet<EncryptedBundleRecord>(description.Bundles.Where(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps));
            _parents = PinCacheRoot(_root, excludedRoot);
            try
            {
                _owner = OwnedWindowsFiles.LockPinned(Path.Combine(_root, ".yl-cache.lock"));
                _http = new HttpClient(new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false,
                    AutomaticDecompression = DecompressionMethods.None
                });
            }
            catch
            {
                _owner?.Dispose();
                _parents.Dispose();
                _lifetime.Dispose();
                throw;
            }
        }

        private static IDisposable PinCacheRoot(string root, string excludedRoot)
        {
            if (excludedRoot == null)
                return OwnedWindowsFiles.PinDirectory(root, create: true);
            using var installed = OwnedWindowsFiles.InspectDirectory(excludedRoot, out string installedPath);
            using var expected = OwnedWindowsFiles.InspectDirectory(root, out string expectedPath);
            static string Prefix(string path) => path.TrimEnd('\\') + "\\";
            if (Prefix(installedPath).StartsWith(Prefix(expectedPath), StringComparison.OrdinalIgnoreCase) ||
                Prefix(expectedPath).StartsWith(Prefix(installedPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The protected cache must not overlap this version's installed content.");
            using var created = OwnedWindowsFiles.PinDirectory(root, create: true);
            var actual = OwnedWindowsFiles.InspectDirectory(root, out string actualPath);
            if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
            {
                actual.Dispose();
                throw new IOException("The physical cipher cache directory changed during preparation.");
            }
            return actual;
        }

        internal static Task<EncryptedBundleCache> OpenAsync(string root, EncryptedBundleDescription description,
            CancellationToken token = default, string excludedRoot = null)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var cache = new EncryptedBundleCache(root, description, excludedRoot);
                try
                {
                    cache.Scan(token);
                    return cache;
                }
                catch
                {
                    cache.Dispose();
                    throw;
                }
            }, token);
        }

        private void Scan(CancellationToken token)
        {
            foreach (string abandoned in Directory.EnumerateFiles(_root, TemporaryPrefix + "*.part", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileNameWithoutExtension(abandoned);
                if (name.Length == TemporaryPrefix.Length + 32 && name.StartsWith(TemporaryPrefix, StringComparison.Ordinal) &&
                    Guid.TryParseExact(name.Substring(TemporaryPrefix.Length), "N", out _))
                {
                    using var file = OwnedWindowsFiles.OpenRemovalPinned(abandoned);
                    OwnedWindowsFiles.DeleteOpen(file);
                }
            }
            foreach (var record in _description)
            {
                token.ThrowIfCancellationRequested();
                if (_entries.ContainsKey(record.CipherSha256))
                    continue;
                var entry = new Entry { Path = Path.Combine(_root, record.CipherSha256 + ".bin") };
                _entries.Add(record.CipherSha256, entry);
                try
                {
                    if (CheckExisting(entry.Path, record, token))
                        entry.State = EntryState.Available;
                }
                catch (EncryptedBundleIntegrityException) { entry.State = EntryState.Invalidated; }
            }
        }

        internal long GetRemainingBytes(EncryptedBundleRecord record) => TryGetVerifiedPath(record, out _) ? 0 : record.CipherLength;

        // Addressables ComputeSize may use this lookup on the Unity thread. No filesystem or key access.
        internal bool TryGetVerifiedPath(EncryptedBundleRecord record, out string path)
        {
            lock (_gate)
            {
                Entry entry = GetEntry(record);
                path = _closing == null && entry.State == EntryState.Available ? entry.Path : null;
                return path != null;
            }
        }

        internal Task EnsureAsync(EncryptedBundleRecord record, CancellationToken observer = default,
            Action<TransferProgress> reportProgress = null)
        {
            if (observer.IsCancellationRequested)
                return Task.FromCanceled(observer);
            Attempt attempt;
            lock (_gate)
            {
                if (_closing != null)
                    throw new ObjectDisposedException(nameof(EncryptedBundleCache));
                var entry = GetEntry(record);
                attempt = entry.Current;
                if (attempt == null || attempt.Ready.Task.IsCompleted)
                {
                    attempt = NewAttempt(entry, record);
                    attempt.Work = Task.Run(() => EnsureCoreAsync(entry, record, attempt, _lifetime.Token));
                }
            }
            reportProgress?.Invoke(attempt.Progress);
            return observer.CanBeCanceled ? ObserveAsync(attempt.Ready.Task, observer) : attempt.Ready.Task;
        }

        private static Attempt NewAttempt(Entry entry, EncryptedBundleRecord record)
        {
            var attempt = entry.Current = new Attempt(entry, record);
            _ = attempt.Ready.Task.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return attempt;
        }

        private Entry GetEntry(EncryptedBundleRecord record)
        {
            if (!_description.Contains(record))
                throw new InvalidOperationException("Bundle is not in the frozen description.");
            return _entries[record.CipherSha256];
        }

        private async Task EnsureCoreAsync(Entry entry, EncryptedBundleRecord record, Attempt attempt, CancellationToken token)
        {
            try
            {
                if (CheckExisting(entry.Path, record, token))
                {
                    attempt.Progress.SetTotalBytes(0);
                }
                else
                    await DownloadMissingAsync(entry, record, attempt, token).ConfigureAwait(false);
                PublishHit(entry, attempt);
            }
            catch (Exception error)
            {
                attempt.Ready.TrySetException(ResolveFailure(entry, record, attempt, error, token));
            }
        }

        private void PublishHit(Entry entry, Attempt attempt)
        {
            lock (_gate)
            {
                if (entry.Current == attempt)
                    entry.State = EntryState.Available;
            }
            attempt.Ready.TrySetResult(true);
        }

        private Exception ResolveFailure(Entry entry, EncryptedBundleRecord record, Attempt attempt, Exception error, CancellationToken token)
        {
            if (error is FileNotFoundException)
                error = new EncryptedBundleIntegrityException("Ciphertext disappeared before authentication.", error);
            if (error is not EncryptedBundleIntegrityException)
                return error;
            lock (_gate)
            {
                if (entry.Current != attempt)
                    return error;
                entry.State = EntryState.Invalidated;
            }
            try
            {
                RemoveOnlyIfDamaged(entry.Path, record, token);
            }
            catch (Exception cleanup) { return new AggregateException("Ciphertext validation and cache cleanup both failed.", error, cleanup); }
            return error;
        }

        private Task DownloadMissingAsync(Entry entry, EncryptedBundleRecord record, Attempt attempt, CancellationToken token)
        {
            if (attempt.HadHit)
                throw new EncryptedBundleIntegrityException("A previously verified ciphertext disappeared.");
            attempt.Progress.SetTotalBytes(record.CipherLength);
            return DownloadAsync(entry.Path, record, attempt.Progress, token);
        }

        internal Task<AuthenticatedBundleStream> OpenAuthenticatedAsync(EncryptedBundleRecord record, IEncryptedBundleKeySource keys,
            CancellationToken token = default, Action<TransferProgress> reportProgress = null)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled<AuthenticatedBundleStream>(token);
            Attempt attempt;
            Task<AuthenticatedBundleStream> work;
            lock (_gate)
            {
                if (_closing != null)
                    throw new ObjectDisposedException(nameof(EncryptedBundleCache));
                var entry = GetEntry(record);
                attempt = entry.Current;
                bool begins = attempt == null || attempt.Ready.Task.IsCompleted;
                if (begins)
                    attempt = NewAttempt(entry, record);
                work = Task.Run(() => MountCoreAsync(entry, record, keys, attempt, begins, token));
                if (begins)
                    attempt.Work = work;
                _mountWork.Add(work);
                _ = work.ContinueWith(done =>
                {
                    lock (_gate)
                        _mountWork.Remove(done);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            reportProgress?.Invoke(attempt.Progress);
            return work;
        }

        private async Task<AuthenticatedBundleStream> MountCoreAsync(Entry entry, EncryptedBundleRecord record,
            IEncryptedBundleKeySource keys, Attempt attempt, bool begins, CancellationToken observer)
        {
            var lifetime = _lifetime.Token;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(observer, lifetime);
            if (!begins)
                await ObserveAsync(attempt.Ready.Task, linked.Token).ConfigureAwait(false);
            try
            {
                FileStream file;
                try
                {
                    file = OwnedWindowsFiles.ReadPinned(entry.Path);
                }
                catch (FileNotFoundException) when (begins)
                {
                    await DownloadMissingAsync(entry, record, attempt, lifetime).ConfigureAwait(false);
                    file = OwnedWindowsFiles.ReadPinned(entry.Path);
                }
                // Only the exact bytes on this deny-write/delete handle may reach the decryptor.
                return AuthenticatedBundleStream.OpenVerified(file, record, keys, linked.Token, bytes =>
                {
                    RecordVerifiedCipherHash(bytes);
                    if (begins && attempt.Progress.DownloadedBytes == 0)
                        attempt.Progress.SetTotalBytes(0);
                    PublishHit(entry, attempt);
                }, begins ? lifetime : linked.Token);
            }
            catch (Exception error)
            {
                var failure = ResolveFailure(entry, record, attempt, error, lifetime);
                if (begins)
                    attempt.Ready.TrySetException(failure);
                if (failure == error)
                    throw;
                throw failure;
            }
        }

        private bool CheckExisting(string path, EncryptedBundleRecord record, CancellationToken token)
        {
            FileStream open;
            try
            {
                open = OwnedWindowsFiles.ReadPinned(path);
            }
            catch (FileNotFoundException) { return false; }
            using (open)
                RecordVerifiedCipherHash(AuthenticatedBundleStream.ValidateCipher(open, record, token));
            return true;
        }

        private void RecordVerifiedCipherHash(long bytes) => Interlocked.Add(ref _verifiedCipherHashBytes, bytes);

        private static void RemoveOnlyIfDamaged(string path, EncryptedBundleRecord record, CancellationToken token)
        {
            FileStream isolated;
            try
            {
                isolated = OwnedWindowsFiles.OpenRemovalPinned(path);
            }
            catch (FileNotFoundException) { return; }
            using (isolated)
            {
                try
                {
                    AuthenticatedBundleStream.ValidateCipher(isolated, record, token);
                }
                catch (EncryptedBundleIntegrityException) { OwnedWindowsFiles.DeleteOpen(isolated); }
            }
        }

        private async Task DownloadAsync(string final, EncryptedBundleRecord row, TransferProgress progress, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var transfer = deadline.Token;
            string name = Path.Combine(_root, TemporaryPrefix + Guid.NewGuid().ToString("N") + ".part");
            using var body = new HttpRequestMessage(HttpMethod.Get, row.Binding.Url);
            try
            {
                using var response = await _http.SendAsync(body, HttpCompletionOption.ResponseHeadersRead, transfer).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 ||
                    response.Content.Headers.ContentRange != null || (response.Content.Headers.ContentLength is long declared && declared != row.CipherLength))
                {
                    throw new IOException("Encrypted Bundle server did not supply the frozen complete ciphertext (HTTP " + (int)response.StatusCode + ").");
                }
                using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var part = OwnedWindowsFiles.CreateTemporaryPinned(name);
                RecordVerifiedCipherHash(await CommitCipherAsync(input, part, final, row, transfer, progress).ConfigureAwait(false));
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            {
                throw new IOException("Encrypted Bundle download timed out.", error);
            }
            catch (HttpRequestException error)
            {
                throw new IOException("Encrypted Bundle network failure.", error);
            }
        }

        internal static async Task<long> CommitCipherAsync(Stream input, FileStream part, string final, EncryptedBundleRecord row,
            CancellationToken transfer, TransferProgress progress = null)
        {
            try
            {
                var buffer = new byte[128 * 1024];
                long length = 0;
                while (true)
                {
                    int take = (int)Math.Min(buffer.Length, row.CipherLength - length + 1);
                    int count = await input.ReadAsync(buffer, 0, take, transfer).ConfigureAwait(false);
                    if (count == 0)
                        break;
                    length += count;
                    if (length > row.CipherLength)
                        throw new EncryptedBundleIntegrityException("Download exceeded the frozen ciphertext length.");
                    part.Write(buffer, 0, count);
                    progress?.SetDownloadedBytes(length);
                }
                part.Flush(true);
                long checkedBytes = AuthenticatedBundleStream.ValidateCipher(part, row, transfer);
                transfer.ThrowIfCancellationRequested();
                OwnedWindowsFiles.Publish(part, final);
                return checkedBytes;
            }
            catch (Exception error)
            {
                try
                {
                    OwnedWindowsFiles.DeleteOpen(part);
                }
                catch (Exception cleanup) { throw new AggregateException("Ciphertext transfer and temporary cleanup both failed.", error, cleanup); }
                throw;
            }
        }

        internal static async Task ObserveAsync(Task shared, CancellationToken token)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(shared, canceled.Task).ConfigureAwait(false) != shared)
                {
                    token.ThrowIfCancellationRequested();
                }
                await shared.ConfigureAwait(false);
            }
        }

        internal Task CloseAsync()
        {
            lock (_gate)
            {
                if (_closing == null)
                {
                    _lifetime.Cancel();
                    _closing = CloseCoreAsync(_entries.Values.Select(entry => entry.Current?.Work).Where(task => task != null).Concat(_mountWork).Distinct().ToArray());
                }
                return _closing;
            }
        }

        private async Task CloseCoreAsync(Task[] pending)
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception) { /* Each shared operation retains its own observed result. */ }
            finally
            {
                _http.Dispose();
                _owner.Dispose();
                _parents.Dispose();
                _lifetime.Dispose();
            }
        }

        public void Dispose() => _ = CloseAsync();
    }
}
