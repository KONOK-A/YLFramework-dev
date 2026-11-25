using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YLFramework.Resource.Encryption;

public static class YLResourceEncryptionContracts
{
    private const int Segment = 1024 * 1024;
    private const string ReleaseId = "build-a";
    private const string Group = "30925c6a78360f4128bc20b6b5ff4611";
    private const string BundleHash = "88765c6a78360f4128bc20b6b5ff4422";
    private const string Provider = "YLFramework.Resource.Encryption.EncryptedAssetBundleProvider";
    private const string CatalogHash = "bcdd4fe94f25e4bd0348e7d6322033b25850474a6d12489b989a41bdd6e9ca9f";
    private static int _passed;

    public static async Task<int> Run(string root, string nativeDll, string jar, string oracle)
    {
        var native = NativeLibrary.Load(nativeDll);
        Check(NativeLibrary.TryGetExport(native, "yl_read_statistics", out _), "native bridge must report actual cipher reads independently of managed heap size");
        using (var sample = new MemoryStream(new byte[] { 0x61, 0x62, 0x63 }))
        {
            sample.Position = 2;
            Check(AuthenticatedBundleStream.Hash(sample, default) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" && sample.Position == sample.Length,
                "the streaming SHA-256 backend hashes the entire input, against an independent known digest");
            Throws<OperationCanceledException>(() => AuthenticatedBundleStream.Hash(sample, new CancellationToken(true), clearBuffer: true),
                "streaming SHA-256 observes cancellation");
        }
        NativeLibrary.SetDllImportResolver(typeof(AuthenticatedBundleStream).Assembly, (name, _, _) =>
            name == "ylresource_tink" ? native : IntPtr.Zero);
        if (Directory.Exists(root) || !Directory.Exists(Path.GetDirectoryName(root))) throw new IOException("Test root must be fresh.");
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string release = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/assets/build-a/";
        byte[] key1 = RandomNumberGenerator.GetBytes(32);
        byte[] key2 = RandomNumberGenerator.GetBytes(32);
        string testKey = Path.Combine(root, "test-key");
        try
        {
            var plain = new byte[3 * Segment + 111];
            new Random(649928).NextBytes(plain);
            string clearPath = Path.Combine(root, "a.bundle");
            File.WriteAllBytes(clearPath, plain);
            var binding = Bind(release, "K1", "bundle-a", "a.bundle", plain);
            var binding2 = Bind(release, "K2", "bundle-b", "b.bundle", plain);
            var first = Seal(clearPath, Path.Combine(root, "cpp.bin"), binding, key1);
            var second = Seal(clearPath, Path.Combine(root, "cpp-2.bin"), binding2, key2);
            var repeated = Seal(clearPath, Path.Combine(root, "cpp-repeat.bin"), binding, key1);
            var description = new EncryptedBundleDescription("catalog.bin", CatalogHash, CatalogHash, CatalogHash, new[] { first.Record, second.Record });

            CheckPublication(root);
            string openStage = Path.Combine(root, "open-stage");
            string openRelease = Path.Combine(root, "open-release");
            using (var directory = OwnedWindowsFiles.CreatePublicationDirectory(openStage))
            {
                string payload = Path.Combine(openStage, "payload");
                File.WriteAllText(payload, "locked");
                OwnedWindowsFiles.Publish(directory, openRelease);
                string sealedPath = Path.Combine(openRelease, "payload");
                using var verified = OwnedWindowsFiles.ReadPinned(sealedPath);
                using var receipt = OwnedWindowsFiles.CreateTemporaryPinned(Path.Combine(openRelease, "receipt.part"));
                receipt.WriteByte(42);
                receipt.Flush(true);
                OwnedWindowsFiles.Publish(receipt, Path.Combine(openRelease, "delivery.json"));
                Throws<IOException>(() => File.OpenWrite(sealedPath).Dispose(),
                    "atomic receipt can be committed while verified release bytes remain write locked");
            }
            using (OwnedWindowsFiles.InspectDirectory(openRelease, out string physicalRelease))
            using (OwnedWindowsFiles.InspectDirectory(Path.Combine(openRelease, "not-created"), out string missingPhysical))
                Check(physicalRelease.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) &&
                    missingPhysical == Path.Combine(physicalRelease, "not-created") && !Directory.Exists(Path.Combine(openRelease, "not-created")),
                    "physical identity uses a real volume and pinned ancestors without creating a missing output");
            Check(first.Record.CipherLength == EncryptedBundleBinding.GetCipherLength(plain.Length) && first.Record.CipherSha256 != repeated.Record.CipherSha256,
                "fresh Tink randomization and exact cipher size");
            foreach (int length in new[] { 1, Segment - 56, Segment - 55, 2 * Segment - 72, 2 * Segment - 71 })
            {
                byte[] sample = plain.Take(length).ToArray();
                string path = Path.Combine(root, "boundary-" + length);
                File.WriteAllBytes(path, sample);
                var boundary = Seal(path, path + ".bin", Bind(release, "K1", "boundary", "edge.bundle", sample), key1);
                using var stream = Open(boundary.Path, boundary.Record, new Keys(("K1", key1)));
                if (stream.Length != length) throw new Exception("Tink boundary length disagrees: " + length);
            }
            Check(true, "exact segment boundaries round-trip");

            string json = description.Encode();
            Check(EncryptedBundleDescription.Parse(json, true).Encode() == json, "strict versioned description round-trips");
            Throws<InvalidDataException>(() => EncryptedBundleDescription.Parse(json), "production cannot use HTTP");
            RejectJson(json, node => node["bundles"][0]["path"] = "../outside.bundle", "path traversal");
            RejectJson(json, node => node["bundles"][0]["path"] = "sub/%2e%2e/outside.bundle", "encoded separators");
            RejectJson(json, node => node["bundles"][0]["path"] = "NUL.bundle", "Windows device name");
            RejectJson(json, node => node["bundles"][0]["cipherLength"] = 12, "wrong length");
            RejectJson(json, node => node["bundles"][0]["key"] = "reject-me", "unexpected key field");
            RejectJson(json, node =>
            {
                var a = node["bundles"].Single(row => (string)row["bundleName"] == "bundle-a");
                var b = node["bundles"].Single(row => (string)row["bundleName"] == "bundle-b");
                b["groupGuid"] = (string)a["groupGuid"]; b["keyId"] = (string)a["keyId"]; b["path"] = (string)a["path"];
            }, "duplicate path");
            Throws<InvalidDataException>(() => EncryptedBundleDescription.Parse("{\"version\":2," + json.Substring(1), true), "duplicate property");
            RejectJson(json, node => node["version"] = 1, "v1 is not read by the v2 Player");
            RejectJson(json, node => node["releaseId"] = "build-b", "prefix must end in the selected release id");
            RejectJson(json, node =>
            {
                node["bundles"][1]["mode"] = "Embedded";
                node["bundles"][1]["groupGuid"] = (string)node["bundles"][0]["groupGuid"];
                node["bundles"][1]["keyId"] = (string)node["bundles"][0]["keyId"];
            }, "delivery mode agrees within a group");
            RejectJson(json, node =>
            {
                node["bundles"][1]["path"] = "B.bundle";
                node["bundles"][1]["groupGuid"] = (string)node["bundles"][0]["groupGuid"];
                node["bundles"][1]["keyId"] = (string)node["bundles"][0]["keyId"];
            }, "case-equivalent delivery path");

            byte[] aad = binding.AssociatedData();
            var independent = new List<byte>(BitConverter.GetBytes(2));
            foreach (string field in new[] { "YL.BundleDelivery.StreamingAEAD", "AES256_GCM_HKDF_1MB", "kano.test", "StandaloneWindows64",
                "build-a", "RemoteHttps", release + "30925c6a78360f4128bc20b6b5ff4611/a.bundle", Group, "bundle-a", BundleHash, Provider, "K1", "a.bundle", Hash(plain) })
            {
                byte[] utf = Encoding.UTF8.GetBytes(field);
                independent.AddRange(BitConverter.GetBytes(utf.Length));
                independent.AddRange(utf);
            }
            independent.AddRange(BitConverter.GetBytes(plain.LongLength));
            Check(aad.SequenceEqual(independent), "AAD matches independent length-prefixed encoding");
            string aadPath = Path.Combine(root, "aad");
            File.WriteAllBytes(aadPath, independent.ToArray());
            File.WriteAllBytes(testKey, key1);
            string javaClear = Path.Combine(root, "java-plain");
            string java = Path.Combine(root, "java.bin");
            await Oracle(jar, oracle, "open", testKey, aadPath, first.Path, javaClear);
            Check(HashFile(javaClear) == Hash(plain), "Java opens native Tink ciphertext");
            await Oracle(jar, oracle, "seal", testKey, aadPath, clearPath, java);
            var keys = new Keys(("K1", key1), ("K2", key2));
            using (var stream = Open(java, Freeze(binding, java), keys))
            {
                var counters = stream.ReadStatistics();
                Check(counters.Logical == plain.LongLength && counters.Physical >= new FileInfo(java).Length && counters.Physical <= 2 * new FileInfo(java).Length + 40 &&
                    counters.Pages > 0 && counters.PayloadBound > 0 && counters.PayloadBound <= 4L * Segment,
                    "separately count successful plaintext, ciphertext and authenticated fills within the working-buffer bound");
                byte[] sample = new byte[225];
                stream.Seek(Segment - 84, SeekOrigin.Begin);
                Check(stream.Read(sample, 5, 200) == 200 && sample.Skip(5).Take(200).SequenceEqual(plain.Skip(Segment - 84).Take(200)), "native Tink opens Java and seeks across segments");
                stream.Seek(-13, SeekOrigin.End);
                Check(stream.Read(sample, 0, sample.Length) == 13 && stream.Read(sample, 0, sample.Length) == 0, "last segment and EOF are distinguished");
                var reread = stream.ReadStatistics();
                Check(reread.Logical - counters.Logical == 213 && reread.Pages > counters.Pages && reread.Physical > counters.Physical,
                    "native cipher-byte counters distinguish a crossing seek from logical bytes delivered");
                Throws<IOException>(() => File.Open(java, FileMode.Open, FileAccess.Write, FileShare.None).Dispose(), "stable file prevents writes while mounted");
            }
            using (File.Open(java, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Check(keys.Opens == 1 && keys.Returns == 1, "dispose returns exactly one retained key lease");
            Throws<EncryptedBundleAuthenticationException>(() => Open(first.Path, first.Record, new Keys(("K1", key2))), "wrong K1 material");
            Throws<EncryptedBundleKeyUnavailableException>(() => Open(first.Path, first.Record, new Keys(("K2", key2))), "no alternative key is tried");
            CheckStartupKeySnapshot(first.Path, first.Record, key1, key2, plain[0]);
            var wrongContext = Bind(release, "K1", "different-bundle", "a.bundle", plain);
            Throws<EncryptedBundleAuthenticationException>(() => Open(first.Path, Freeze(wrongContext, first.Path), keys), "different AAD despite correct ciphertext digest");
            EncryptedBundleBinding Rebind(string prefix, string id, EncryptedBundleDeliveryMode mode, string group, string key) =>
                new EncryptedBundleBinding("kano.test", id, prefix, mode, group, "bundle-a", BundleHash, Provider, "a.bundle", key, plain.LongLength, Hash(plain), true);
            foreach (var (changed, label) in new[]
            {
                (Rebind(release, ReleaseId, EncryptedBundleDeliveryMode.RemoteHttps, new string('a', 32), "K1"), "different group"),
                (Rebind(release, ReleaseId, EncryptedBundleDeliveryMode.Embedded, Group, "K1"), "different source mode"),
                (Rebind(release.Replace("build-a", "build-b"), "build-b", EncryptedBundleDeliveryMode.RemoteHttps, Group, "K1"), "different release"),
                (Rebind(release, ReleaseId, EncryptedBundleDeliveryMode.RemoteHttps, Group, "K2"), "different key id with same bytes")
            })
                Throws<EncryptedBundleAuthenticationException>(() => Open(first.Path, Freeze(changed, first.Path), new Keys(("K1", key1), ("K2", key1))), label);
            Throws<EncryptedBundleAuthenticationException>(() => Open(second.Path, Freeze(binding, second.Path), new Keys(("K1", key2))), "same-key bundle substitution");
            foreach (long position in new[] { Segment - 1L, 2L * Segment - 1, first.Record.CipherLength - 1 })
            {
                string tampered = Path.Combine(root, "tampered-" + position);
                File.Copy(first.Path, tampered);
                Flip(tampered, position);
                Throws<EncryptedBundleAuthenticationException>(() => Open(tampered, Freeze(binding, tampered), keys), "AEAD rejects bad tag at " + position);
            }
            var appended = Path.Combine(root, "appended.bin");
            File.Copy(first.Path, appended);
            using (var append = File.Open(appended, FileMode.Append, FileAccess.Write, FileShare.None)) append.WriteByte(0x01);
            Throws<EncryptedBundleIntegrityException>(() => Open(appended, first.Record, keys), "appended ciphertext is rejected before AEAD");
            var shortened = Path.Combine(root, "shortened.bin");
            File.Copy(first.Path, shortened);
            using (var truncate = File.Open(shortened, FileMode.Open, FileAccess.Write, FileShare.None)) truncate.SetLength(first.Record.CipherLength - 1);
            Throws<EncryptedBundleIntegrityException>(() => Open(shortened, first.Record, keys), "truncated ciphertext is rejected before AEAD");

            await Cache(root, description, first, second, repeated, listener, keys, new Keys(("K2", key2)), plain[0]);
            Console.WriteLine($"P1 contracts: {_passed} passed");
            return _passed;
        }
        finally
        {
            listener.Stop();
            Array.Clear(key1, 0, key1.Length);
            Array.Clear(key2, 0, key2.Length);
            File.Delete(testKey);
        }
    }

    private static void CheckStartupKeySnapshot(string path, EncryptedBundleRecord record, byte[] original, byte[] replacement, byte sentinel)
    {
        byte[] mutableKey = (byte[])original.Clone();
        try
        {
            var applicationKeys = new Keys(("K1", mutableKey));
            using var snapshot = new EncryptedBundleKeySnapshot();
            Check(snapshot.Capture(applicationKeys, "K1"), "startup captured the requested key");
            using (var validation = Open(path, record, snapshot))
                Check(validation.ReadByte() == sentinel, "startup authenticates with the captured key");
            Check(applicationKeys.Opens == 1 && applicationKeys.Returns == 1, "startup has already returned the application lease");
            Buffer.BlockCopy(replacement, 0, mutableKey, 0, mutableKey.Length);
            using var provider = Open(path, record, snapshot);
            snapshot.Dispose();
            Check(provider.ReadByte() == sentinel && applicationKeys.Opens == 1, "provider reads the authenticated startup key even after the application changes it");
            Throws<ObjectDisposedException>(() => snapshot.OpenReadKey("K1"), "closing the session destroys its key snapshot");
        }
        finally { Array.Clear(mutableKey, 0, mutableKey.Length); }
    }

    private static async Task Cache(string root, EncryptedBundleDescription description, (string Path, EncryptedBundleRecord Record) first,
        (string Path, EncryptedBundleRecord Record) second, (string Path, EncryptedBundleRecord Record) repeated, TcpListener listener, Keys keys, Keys k2Only, byte sentinel)
    {
        var a = first.Record;
        var b = second.Record;
        string cacheRoot = Path.Combine(root, "cache");
        using var cache = await EncryptedBundleCache.OpenAsync(cacheRoot, description);
        string file = Path.Combine(cacheRoot, a.CipherSha256 + ".bin");
        byte[] cipher = File.ReadAllBytes(first.Path);
        byte[] cipher2 = File.ReadAllBytes(second.Path);
        Check(cache.GetRemainingBytes(a) == a.CipherLength, "cold snapshot is exact cipher length");
        using var stopObserver = new CancellationTokenSource();
        EncryptedBundleCache.TransferProgress canceledProgress = null;
        EncryptedBundleCache.TransferProgress liveProgress = null;
        var request1 = cache.EnsureAsync(a, stopObserver.Token, attempt => canceledProgress = attempt);
        var request2 = cache.EnsureAsync(a, reportProgress: attempt => liveProgress = attempt);
        Check(ReferenceEquals(canceledProgress, liveProgress) && liveProgress.TotalBytes == a.CipherLength && liveProgress.DownloadedBytes == 0,
            "overlapping observers receive one cold attempt and its exact byte budget");
        using (var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10)))
        {
            NetworkStream channel = client.GetStream();
            await ReadHeaders(channel);
            stopObserver.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => request1, "cancelled observer detaches from shared download");
            const int PrefixBytes = 250;
            await Respond(channel, "200 OK", cipher.Take(PrefixBytes).ToArray(), cipher.Length);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (liveProgress.DownloadedBytes < PrefixBytes)
                await Task.Delay(10, deadline.Token);
            Check(liveProgress.DownloadedBytes == PrefixBytes && liveProgress.TotalBytes == a.CipherLength && !request2.IsCompleted && !File.Exists(file),
                "held ciphertext reports only the received prefix, without publishing or completing");
            await channel.WriteAsync(cipher.AsMemory(PrefixBytes));
        }
        await request2;
        Check(!listener.Pending() && cache.GetRemainingBytes(a) == 0 && HashFile(file) == a.CipherSha256 && !Directory.EnumerateFiles(cacheRoot, "*.part").Any(), "one complete transfer is published atomically");
        Check(liveProgress.DownloadedBytes == a.CipherLength && liveProgress.TotalBytes == a.CipherLength,
            "a verified physical transfer preserves its final byte counts");
        Check(cache.VerifiedCipherHashBytes == a.CipherLength,
            "measure the actual managed cipher SHA pass on download commit");
        EncryptedBundleCache.TransferProgress warmProgress = null;
        await ThrowsAsync<EncryptedBundleKeyUnavailableException>(() => cache.OpenAuthenticatedAsync(a, k2Only, reportProgress: attempt => warmProgress = attempt),
            "cached K1 with only K2 refuses without GET");
        Check(warmProgress.TotalBytes == 0 && warmProgress.DownloadedBytes == 0,
            "warm revalidation and failed authentication do not report disk work as downloaded bytes");
        Check(cache.GetRemainingBytes(a) == 0 && File.Exists(file) && !listener.Pending(), "missing K1 does not invalidate bytes or trigger transfer");
        var load2 = cache.EnsureAsync(b);
        await Serve(listener, "200 OK", cipher2, cipher2.Length);
        await load2;
        using (var stream = await cache.OpenAuthenticatedAsync(b, k2Only)) if (stream.ReadByte() != sentinel) throw new Exception("Independent K2 failed");
        long beforeMount = cache.VerifiedCipherHashBytes;
        using (var stream = await cache.OpenAuthenticatedAsync(a, keys)) if (stream.ReadByte() != sentinel) throw new Exception("Restored K1 failed");
        Check(cache.VerifiedCipherHashBytes - beforeMount == a.CipherLength, "warm mount hashes the pinned ciphertext exactly once");
        Check(!listener.Pending(), "restoring K1 and mounting K2 need no second GET");

        File.Copy(repeated.Path, file, true);
        Check(cache.GetRemainingBytes(a) == 0, "query is a snapshot");
        int opens = keys.Opens;
        await ThrowsAsync<EncryptedBundleIntegrityException>(() => cache.OpenAuthenticatedAsync(a, keys), "same-length replacement refused before key access");
        Check(cache.GetRemainingBytes(a) == a.CipherLength && keys.Opens == opens && !listener.Pending(), "corruption invalidates snapshot without retry or key access");
        EncryptedBundleCache.TransferProgress retryProgress = null;
        var retry = cache.EnsureAsync(a, reportProgress: attempt => retryProgress = attempt);
        Check(!ReferenceEquals(liveProgress, retryProgress) && liveProgress.DownloadedBytes == a.CipherLength &&
            retryProgress.TotalBytes == a.CipherLength && retryProgress.DownloadedBytes == 0,
            "an explicit repair starts a fresh attempt without rewriting a retained observer's progress");
        await Serve(listener, "200 OK", cipher, cipher.Length);
        await retry;
        cache.Dispose();
        Flip(file, 7);
        string abandoned = Path.Combine(cacheRoot, ".yl-transfer-" + Guid.NewGuid().ToString("N") + ".part");
        File.WriteAllText(abandoned, "abandoned");
        using var restarted = await EncryptedBundleCache.OpenAsync(cacheRoot, description);
        Check(restarted.GetRemainingBytes(a) == a.CipherLength && restarted.GetRemainingBytes(b) == 0 && !File.Exists(abandoned), "restart re-hashes files and recovers owned abandoned parts");
        Check(restarted.VerifiedCipherHashBytes == b.CipherLength, "track separately the successful full hashes during warm cache scan");
        await ThrowsAsync<EncryptedBundleIntegrityException>(() => restarted.EnsureAsync(a), "warm corruption fails this attempt");
        EncryptedBundleCache.TransferProgress truncatedProgress = null;
        var truncated = restarted.EnsureAsync(a, reportProgress: attempt => truncatedProgress = attempt);
        await Serve(listener, "200 OK", cipher.Take(250).ToArray(), cipher.Length);
        await ThrowsAsync<IOException>(() => truncated, "interrupted body refused");
        Check(truncatedProgress.TotalBytes == a.CipherLength && truncatedProgress.DownloadedBytes == 250,
            "a truncated transfer never advances download bytes to the advertised total");
        var redirected = restarted.EnsureAsync(a);
        await Serve(listener, "302 Found", Array.Empty<byte>(), 0, "Location: http://127.0.0.1:1/exit\r\n");
        await ThrowsAsync<IOException>(() => redirected, "redirect refused");
        var overlong = restarted.EnsureAsync(a);
        await Serve(listener, "200 OK", Array.Empty<byte>(), cipher.Length + 1);
        await ThrowsAsync<IOException>(() => overlong, "oversize body refused");
        var substituted = restarted.EnsureAsync(a);
        await Serve(listener, "200 OK", cipher2, cipher2.Length);
        await ThrowsAsync<EncryptedBundleIntegrityException>(() => substituted, "an independently valid, same-length Bundle cannot be downloaded in place of another");
        Check(!File.Exists(file) && !Directory.EnumerateFiles(cacheRoot, "*.part").Any() && restarted.GetRemainingBytes(b) == 0, "failed transfer retains no partial and leaves peer intact");
        var racedCommit = restarted.EnsureAsync(a);
        using (var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10)))
        {
            NetworkStream channel = client.GetStream();
            await ReadHeaders(channel);
            File.Copy(first.Path, file);
            await Respond(channel, "200 OK", cipher, cipher.Length);
        }
        await ThrowsAsync<IOException>(() => racedCommit, "atomic commit refuses to replace an existing valid ciphertext");
        Check(HashFile(file) == a.CipherSha256 && !Directory.EnumerateFiles(cacheRoot, "*.part").Any(), "commit I/O failure preserves the existing file and removes its temporary attempt");
        await restarted.EnsureAsync(a);
        Check(restarted.GetRemainingBytes(a) == 0 && !listener.Pending(), "explicit retry after commit failure can revalidate without network");

        string faultPath = Path.Combine(cacheRoot, ".yl-transfer-" + Guid.NewGuid().ToString("N") + ".part");
        string faultFinal = Path.Combine(cacheRoot, "must-not-publish.bin");
        const int diskFull = unchecked((int)0x80070070);
        using (var handle = OwnedWindowsFiles.CreateTemporaryPinned(faultPath))
        using (var output = new SpaceLimitedFile(handle.SafeFileHandle, diskFull))
        using (var body = new MemoryStream(cipher, writable: false))
        {
            bool reported = false;
            try { await EncryptedBundleCache.CommitCipherAsync(body, output, faultFinal, a, CancellationToken.None); }
            catch (IOException error) when (error.HResult == diskFull) { reported = true; }
            Check(reported && output.WriteAttempted, "cache reports the injected Win32 disk-full error after starting the real temporary write");
        }
        Check(!File.Exists(faultPath) && !File.Exists(faultFinal) && HashFile(file) == a.CipherSha256,
            "disk-full cleanup did not publish a truncated record or modify an existing cache hit");
    }

    private sealed class SpaceLimitedFile : FileStream
    {
        private readonly int _error;
        public bool WriteAttempted;

        public SpaceLimitedFile(Microsoft.Win32.SafeHandles.SafeFileHandle file, int error) : base(file, FileAccess.ReadWrite, 1, false) => _error = error;

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, Math.Min(count, 16));
            WriteAttempted = true;
            throw new IOException("Test-only exhausted file storage", _error);
        }
    }

    private static EncryptedBundleBinding Bind(string release, string keyId, string name, string path, byte[] plain) =>
        new EncryptedBundleBinding("kano.test", ReleaseId, release, EncryptedBundleDeliveryMode.RemoteHttps, GroupFor(keyId),
            name, BundleHash, Provider, path, keyId, plain.LongLength, Hash(plain), true);

    private static void CheckPublication(string root)
    {
        string source = Path.Combine(root, "private-parent");
        string destination = Path.Combine(root, "release-parent");
        DirectoryInfo sourceParent = Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var inherited = sourceParent.GetAccessControl();
        inherited.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        sourceParent.SetAccessControl(inherited);

        using var sourcePin = OwnedWindowsFiles.PinDirectory(source, false);
        using var destinationPin = OwnedWindowsFiles.InspectDirectory(destination, out string physicalDestination);
        Throws<IOException>(() => Directory.Move(source, source + "-renamed"), "the source parent is pinned against rename");
        Throws<IOException>(() => Directory.Move(destination, destination + "-renamed"), "the destination parent is pinned against rename");

        string stage = Path.Combine(source, "stage");
        string release = Path.Combine(destination, "release");
        using var identity = WindowsIdentity.GetCurrent();
        using var stageHandle = OwnedWindowsFiles.CreatePublicationDirectory(stage);
        Check(PublisherOnly(new DirectoryInfo(stage).GetAccessControl(), identity.User, inherit: false),
            "a publication directory starts with a protected publisher-only DACL even under a shared parent");
        string payload = Path.Combine(Directory.CreateDirectory(Path.Combine(stage, "Player")).FullName, "payload");
        using (var output = OwnedWindowsFiles.CreateTemporaryPinned(payload))
        {
            output.WriteByte(42);
            output.Flush(true);
            Check((File.GetAttributes(payload) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                Directory.EnumerateFiles(Path.GetDirectoryName(payload)).Single() == payload,
                "the tree can be inventoried while the owned output handle still rejects other data access");
            Throws<IOException>(() => File.OpenWrite(payload).Dispose(), "the owned payload handle prevents same-account writes");
            Throws<IOException>(() => OwnedWindowsFiles.Publish(stageHandle, Path.Combine(physicalDestination, "release")),
                "Windows refuses the root rename while an exclusive child handle is open");
            Check(!Directory.Exists(release), "the failed root rename did not expose a partial release");
        }

        string conflict = Directory.CreateDirectory(Path.Combine(destination, "conflict")).FullName;
        string marker = Path.Combine(conflict, "existing");
        File.WriteAllText(marker, "before");
        Throws<IOException>(() => OwnedWindowsFiles.Publish(stageHandle, Path.Combine(physicalDestination, "conflict")),
            "a staged root cannot replace an occupied destination");
        Check(File.ReadAllText(marker) == "before" && File.Exists(payload), "a root conflict preserves both trees");

        OwnedWindowsFiles.Publish(stageHandle, Path.Combine(physicalDestination, "release"));
        string published = Path.Combine(release, "Player", "payload");
        Check(!Directory.Exists(stage) && File.ReadAllBytes(published).SequenceEqual(new byte[] { 42 }),
            "publication moves the exact staged root and bytes by handle across pinned parents");
        Check(PublisherOnly(new DirectoryInfo(release).GetAccessControl(), identity.User, inherit: false) &&
            PublisherOnly(new DirectoryInfo(Path.GetDirectoryName(published)).GetAccessControl(), identity.User, inherit: true) &&
            PublisherOnly(new FileInfo(published).GetAccessControl(), identity.User, inherit: true),
            "the root and descendants retain publisher-only access through the root rename");
    }

    private static bool PublisherOnly(FileSystemSecurity security, SecurityIdentifier publisher, bool inherit)
    {
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        return security.AreAccessRulesProtected != inherit && rules.Count == 1 && rules[0] is FileSystemAccessRule rule &&
            rule.IdentityReference.Equals(publisher) && rule.IsInherited == inherit && rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
    }

    private static string GroupFor(string keyId) => keyId == "K1" ? Group : "20925c6a78360f4128bc20b6b5ff4611";
    private static EncryptedBundleRecord Freeze(EncryptedBundleBinding binding, string path) => new EncryptedBundleRecord(binding, new FileInfo(path).Length, HashFile(path));
    private static (string Path, EncryptedBundleRecord Record) Seal(string plain, string cipher, EncryptedBundleBinding binding, byte[] key)
    {
        using var lease = new EncryptedBundleKeyLease(key);
        using var source = File.OpenRead(plain);
        using (var destination = new FileStream(cipher, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            AuthenticatedBundleStream.Seal(source, destination, binding, lease);
        return (cipher, Freeze(binding, cipher));
    }
    private static AuthenticatedBundleStream Open(string file, EncryptedBundleRecord record, IEncryptedBundleKeySource keys) =>
        AuthenticatedBundleStream.OpenVerified(OwnedWindowsFiles.OpenRead(file), record, keys, CancellationToken.None);
    private static void RejectJson(string json, Action<JObject> change, string name)
    {
        var item = JObject.Parse(json);
        change(item);
        Throws<InvalidDataException>(() => EncryptedBundleDescription.Parse(item.ToString(), true), name);
    }
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        _passed++;
        Console.WriteLine("PASS " + name);
    }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, name); return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (T) { Check(true, name); return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + name);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string HashFile(string file) { using var input = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); }
    private static void Flip(string file, long offset)
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        input.Position = offset;
        int before = input.ReadByte();
        if (before < 0) throw new IOException("Invalid fixture offset");
        input.Position = offset;
        input.WriteByte((byte)(before ^ 1));
    }
    private static async Task Oracle(string jar, string oracle, string mode, string key, string aad, string input, string output)
    {
        var start = new ProcessStartInfo("java") { UseShellExecute = false };
        foreach (string value in new[] { "--class-path", jar, oracle, mode, key, aad, input, output }) start.ArgumentList.Add(value);
        using var process = Process.Start(start);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Tink Java oracle exit " + process.ExitCode);
    }
    private static async Task ReadHeaders(NetworkStream channel)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(channel, Encoding.ASCII, false, 512, true);
        string request = await reader.ReadLineAsync(limit.Token);
        if (request == null || !request.StartsWith("GET /assets/build-a/", StringComparison.Ordinal)) throw new IOException("Unexpected download request");
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(limit.Token))) { }
    }
    private static async Task Respond(NetworkStream channel, string status, byte[] body, int declared, string headers = "")
    {
        await channel.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {declared}\r\nConnection: close\r\n{headers}\r\n"));
        await channel.WriteAsync(body);
    }
    private static async Task Serve(TcpListener listener, string status, byte[] body, int declared, string headers = "")
    {
        using var connection = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var channel = connection.GetStream();
        await ReadHeaders(channel);
        await Respond(channel, status, body, declared, headers);
    }
    private sealed class Keys : IEncryptedBundleKeySource
    {
        private readonly Dictionary<string, byte[]> _keys;
        public int Opens;
        public int Returns;
        public Keys(params (string id, byte[] key)[] pairs) => _keys = pairs.ToDictionary(pair => pair.id, pair => pair.key);
        public EncryptedBundleKeyLease OpenReadKey(string keyId)
        {
            Opens++;
            return _keys.TryGetValue(keyId, out byte[] key) ? new EncryptedBundleKeyLease(key, () => Returns++) : null;
        }
    }
}
