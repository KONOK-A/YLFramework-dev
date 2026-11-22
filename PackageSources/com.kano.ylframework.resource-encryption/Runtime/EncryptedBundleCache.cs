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
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly IDisposable _parents;
        private readonly SafeFileHandle _owner;
        private readonly HttpClient _http;
        private Task _closing;
        private long _verifiedCipherHashBytes;

        // Bytes read by successful full ciphertext SHA-256 checks, not native reads or OS disk I/O.
        internal long VerifiedCipherHashBytes => Interlocked.Read(ref _verifiedCipherHashBytes);

        private sealed class Entry
        {
            internal string Path;
            internal bool Verified;
            internal Task Shared;
        }

        private EncryptedBundleCache(string root, EncryptedBundleDescription description)
        {
            _root = OwnedWindowsFiles.LocalAbsolute(root);
            if (_root.TrimEnd('\\') == Path.GetPathRoot(_root).TrimEnd('\\'))
            {
                throw new ArgumentException("The cipher cache must own a dedicated subdirectory.", nameof(root));
            }
            _description = new HashSet<EncryptedBundleRecord>(description.Bundles);
            _parents = OwnedWindowsFiles.PinDirectory(_root, create: true);
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

        internal static Task<EncryptedBundleCache> OpenAsync(string root, EncryptedBundleDescription description, CancellationToken token = default)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var cache = new EncryptedBundleCache(root, description);
                try
                {
                    cache.Scan(description, token);
                    return cache;
                }
                catch
                {
                    cache.Dispose();
                    throw;
                }
            }, token);
        }

        private void Scan(EncryptedBundleDescription description, CancellationToken token)
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
            foreach (var record in description.Bundles)
            {
                token.ThrowIfCancellationRequested();
                if (_entries.ContainsKey(record.CipherSha256)) continue;
                var entry = new Entry { Path = Path.Combine(_root, record.CipherSha256 + ".bin") };
                _entries.Add(record.CipherSha256, entry);
                try { entry.Verified = CheckExisting(entry.Path, record, token); }
                catch (EncryptedBundleIntegrityException) { entry.Verified = false; }
            }
        }

        internal long GetRemainingBytes(EncryptedBundleRecord record) => TryGetVerifiedPath(record, out _) ? 0 : record.CipherLength;

        // Addressables ComputeSize may use this lookup on the Unity thread. No filesystem or key access.
        internal bool TryGetVerifiedPath(EncryptedBundleRecord record, out string path)
        {
            lock (_gate)
            {
                Entry entry = GetEntry(record);
                path = _closing == null && entry.Verified ? entry.Path : null;
                return path != null;
            }
        }

        internal Task EnsureAsync(EncryptedBundleRecord record, CancellationToken observer = default)
        {
            if (observer.IsCancellationRequested) return Task.FromCanceled(observer);
            Task shared;
            lock (_gate)
            {
                if (_closing != null) throw new ObjectDisposedException(nameof(EncryptedBundleCache));
                var entry = GetEntry(record);
                if (entry.Shared == null || entry.Shared.IsCompleted)
                {
                    entry.Shared = Task.Run(() => EnsureCoreAsync(entry, record, _lifetime.Token));
                    _ = entry.Shared.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                shared = entry.Shared;
            }
            return observer.CanBeCanceled ? ObserveAsync(shared, observer) : shared;
        }

        private Entry GetEntry(EncryptedBundleRecord record)
        {
            if (!_description.Contains(record)) throw new InvalidOperationException("Bundle is not in the frozen description.");
            return _entries[record.CipherSha256];
        }

        private async Task EnsureCoreAsync(Entry entry, EncryptedBundleRecord record, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (CheckExisting(entry.Path, record, token))
                {
                    lock (_gate) { entry.Verified = true; }
                    return;
                }
                lock (_gate)
                {
                    if (entry.Verified)
                    {
                        throw new EncryptedBundleIntegrityException("A previously verified ciphertext disappeared.");
                    }
                }
            }
            catch (EncryptedBundleIntegrityException)
            {
                lock (_gate) { entry.Verified = false; }
                RemoveOnlyIfDamaged(entry.Path, record, token);
                throw;
            }

            await DownloadAsync(entry.Path, record, token).ConfigureAwait(false);
            lock (_gate) { entry.Verified = true; }
        }

        internal async Task<AuthenticatedBundleStream> OpenAuthenticatedAsync(EncryptedBundleRecord record, IEncryptedBundleKeySource keys,
            CancellationToken token = default)
        {
            await EnsureAsync(record, token).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                var entry = GetEntry(record);
                using var pin = OwnedWindowsFiles.PinDirectory(_root, create: false);
                try
                {
                    // Nothing can replace, write or delete this file from digest computation through the last Unity read.
                    return AuthenticatedBundleStream.OpenVerified(OwnedWindowsFiles.ReadPinned(entry.Path), record, keys, token, RecordVerifiedCipherHash);
                }
                catch (FileNotFoundException error)
                {
                    lock (_gate) { entry.Verified = false; }
                    throw new EncryptedBundleIntegrityException("Ciphertext disappeared before authentication.", error);
                }
                catch (EncryptedBundleIntegrityException)
                {
                    lock (_gate) { entry.Verified = false; }
                    RemoveOnlyIfDamaged(entry.Path, record, token);
                    throw;
                }
            }, token).ConfigureAwait(false);
        }

        private bool CheckExisting(string path, EncryptedBundleRecord record, CancellationToken token)
        {
            FileStream open;
            try { open = OwnedWindowsFiles.ReadPinned(path); }
            catch (FileNotFoundException) { return false; }
            using (open) RecordVerifiedCipherHash(AuthenticatedBundleStream.ValidateCipher(open, record, token));
            return true;
        }

        private void RecordVerifiedCipherHash(long bytes) => Interlocked.Add(ref _verifiedCipherHashBytes, bytes);

        private static void RemoveOnlyIfDamaged(string path, EncryptedBundleRecord record, CancellationToken token)
        {
            FileStream isolated;
            try { isolated = OwnedWindowsFiles.OpenRemovalPinned(path); }
            catch (FileNotFoundException) { return; }
            using (isolated)
            {
                try { AuthenticatedBundleStream.ValidateCipher(isolated, record, token); }
                catch (EncryptedBundleIntegrityException) { OwnedWindowsFiles.DeleteOpen(isolated); }
            }
        }

        private async Task DownloadAsync(string final, EncryptedBundleRecord row, CancellationToken token)
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
                RecordVerifiedCipherHash(await CommitCipherAsync(input, part, final, row, transfer).ConfigureAwait(false));
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

        internal static async Task<long> CommitCipherAsync(Stream input, FileStream part, string final, EncryptedBundleRecord row, CancellationToken transfer)
        {
            try
            {
                var buffer = new byte[128 * 1024];
                long length = 0;
                while (true)
                {
                    int take = (int)Math.Min(buffer.Length, row.CipherLength - length + 1);
                    int count = await input.ReadAsync(buffer, 0, take, transfer).ConfigureAwait(false);
                    if (count == 0) break;
                    length += count;
                    if (length > row.CipherLength) throw new EncryptedBundleIntegrityException("Download exceeded the frozen ciphertext length.");
                    part.Write(buffer, 0, count);
                }
                part.Flush(true);
                long checkedBytes = AuthenticatedBundleStream.ValidateCipher(part, row, transfer);
                transfer.ThrowIfCancellationRequested();
                OwnedWindowsFiles.Publish(part, final);
                return checkedBytes;
            }
            catch (Exception error)
            {
                try { OwnedWindowsFiles.DeleteOpen(part); }
                catch (Exception cleanup) { throw new AggregateException("Ciphertext transfer and temporary cleanup both failed.", error, cleanup); }
                throw;
            }
        }

        private static async Task ObserveAsync(Task shared, CancellationToken token)
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
                    _closing = CloseCoreAsync(_entries.Values.Select(entry => entry.Shared).Where(task => task != null).ToArray());
                }
                return _closing;
            }
        }

        private async Task CloseCoreAsync(Task[] pending)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
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
