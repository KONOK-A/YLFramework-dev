using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Audio;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourceContentValidationTests
    {
        [Test]
        public void ConfigurationIsLazyAndDoesNotOwnApplicationKeyProvider()
        {
            using var provider = new TestKeys();
            var options = new ResourceDecryptionOptions(provider);
            var configuration = new ResourceConfiguration(Array.Empty<ResourceCatalog>(), decryption: options);
            Assert.That(configuration.Decryption, Is.SameAs(options));
            Assert.That(options.MaxBufferedBytes, Is.EqualTo(128L * 1024 * 1024));
            Assert.That(provider.Copies, Is.Zero);
            Assert.That(provider.Disposed, Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceDecryptionOptions(provider, 0));
        }

        [Test]
        public void ValidationCopiesOneSelectedKeyAndClearsOnlyItsOwnedCopy()
        {
            using var fixture = new ContentFixture();
            using var keys = new TestKeys();
            var budget = new ResourceContentBudget(ResourceDecryptStream.DataBufferBytes);
            var validation = ResourceContentValidation.Capture(new[] { fixture.Snapshot }, new ResourceDecryptionOptions(keys),
                representativesOnly: true, budget: budget);
            Assert.That(keys.Copies, Is.EqualTo(1), "Only the selected project/key pair is requested, not unselected history.");
            Assert.That(budget.ReservedBytes, Is.EqualTo(ResourceDecryptStream.DataBufferBytes));
            Assert.Throws<InvalidOperationException>(validation.RequireStable);
            validation.Validate();
            validation.RequireStable();
            Assert.That(budget.AllocatedBytes, Is.EqualTo(ResourceDecryptStream.DataBufferBytes));
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty, "Key probes must not create native Bundles.");
            validation.Dispose();
            validation.Dispose();
            Assert.That(budget.AllocatedBytes + budget.ReservedBytes, Is.Zero);
            Assert.That(keys.LastDestination.All(x => x == 0), Is.True);
            Assert.That(keys.Disposed, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingAndIncorrectSelectedKeysAreNotFileCorruptionOrFallback(bool wrong)
        {
            using var fixture = new ContentFixture();
            using var keys = new TestKeys { Missing = !wrong, Wrong = wrong };
            ResourceException error;
            if (!wrong)
            {
                error = Assert.Throws<ResourceException>(() => ResourceContentValidation.Capture(
                new[] { fixture.Snapshot }, new ResourceDecryptionOptions(keys), true));
            }
            else
            {
                using var validation = ResourceContentValidation.Capture(new[] { fixture.Snapshot }, new ResourceDecryptionOptions(keys), true);
                error = Assert.Throws<ResourceException>(() => validation.Validate());
            }
            Assert.That(error.Kind, Is.EqualTo(wrong ? ResourceError.ContentAuthenticationFailed : ResourceError.ContentKeyUnavailable));
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
        }

        [Test]
        public void ProviderExceptionRetainsCauseWithoutLeakingItsSensitiveText()
        {
            using var fixture = new ContentFixture();
            using var keys = new TestKeys { Throw = true };
            var error = Assert.Throws<ResourceException>(() => ResourceContentValidation.Capture(
                new[] { fixture.Snapshot }, new ResourceDecryptionOptions(keys), true));
            Assert.That(error.Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            Assert.That(error.InnerException, Is.Not.Null);
            Assert.That(error.ToString(), Does.Not.Contain("test-sensitive-provider-material"));
            Assert.That(keys.LastDestination.All(x => x == 0), Is.True);
        }

        [Test]
        public void CanceledPureValidationReturnsItsReservationAndNeverCreatesNative()
        {
            using var fixture = new ContentFixture();
            using var keys = new TestKeys();
            var budget = new ResourceContentBudget(ResourceDecryptStream.DataBufferBytes);
            using (var validation = ResourceContentValidation.Capture(new[] { fixture.Snapshot }, new ResourceDecryptionOptions(keys), budget: budget))
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => validation.Validate(cancellation.Token));
            }
            Assert.That(budget.AllocatedBytes + budget.ReservedBytes, Is.Zero);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
        }

        [Test]
        public void OptionalCostCheckpointsBracketRealShaAndAuthenticationWithoutSkippingEither()
        {
            var method = typeof(ResourceContentValidation).GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(CancellationToken), typeof(Action<string>) }, null);
            Assert.That(method, Is.Not.Null, "The existing validation path needs optional stage boundaries, not a second verifier.");
            using var fixture = new ContentFixture();
            using var keys = new TestKeys();
            using var validation = ResourceContentValidation.Capture(new[] { fixture.Snapshot }, new ResourceDecryptionOptions(keys));
            var events = new List<string>();
            method.Invoke(validation, new object[] { CancellationToken.None, new Action<string>(events.Add) });
            validation.RequireStable();
            var count = ResourceContentValidation.ContentFiles(fixture.Snapshot).Count();
            CollectionAssert.AreEqual(Enumerable.Range(0, count).SelectMany(_ => new[] {
                "ContentShaStarted", "ContentShaFinished", "ContentAuthenticationStarted", "ContentAuthenticationFinished" }), events);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
        }

        [Test]
        public void FullValidationKeepsCiphertextStableUntilExplicitRelease()
        {
            using var fixture = new ContentFixture();
            using var keys = new TestKeys();
            var path = Path.Combine(fixture.Snapshot.DirectoryPath, fixture.Snapshot.Document.ManifestFile);
            using (var validation = ResourceContentValidation.Capture(new[] { fixture.Snapshot }, new ResourceDecryptionOptions(keys)))
            {
                validation.Validate();
                validation.RequireStable();
                Assert.Throws<IOException>(() =>
                {
                    using var writer = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                });
            }
            using var writable = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }

        [Test]
        public void FailedValidationDisposalRetainsItsTicketUntilAnExplicitSuccessfulRetry()
        {
            using var fixture = new ContentFixture();
            using var keys = new TestKeys();
            var budget = new ResourceContentBudget(ResourceDecryptStream.DataBufferBytes);
            var validation = ResourceContentValidation.Capture(new[] { fixture.Snapshot }, new ResourceDecryptionOptions(keys), budget: budget);
            const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var inputs = (IList)typeof(ResourceContentValidation).GetField("_inputs", Hidden).GetValue(validation);
            Assert.That(inputs.Count, Is.GreaterThanOrEqualTo(2));
            var field = inputs[0].GetType().GetField("Stream", Hidden);
            var original = (FileStream)field.GetValue(inputs[0]);
            var path = original.Name;
            original.Dispose();
            var failing = new FailOnceFileStream(path);
            field.SetValue(inputs[0], failing);
            try
            {
                Assert.Throws<IOException>(validation.Dispose);
                Assert.That(budget.ReservedBytes, Is.EqualTo(ResourceDecryptStream.DataBufferBytes));
                Assert.That(keys.LastDestination.All(x => x == 0), Is.True);
                validation.Dispose();
                Assert.That(budget.AllocatedBytes + budget.ReservedBytes, Is.Zero);
            }
            finally
            {
                // RED也只清理由本测试持有的输入和票据，不遗留锁阻碍夹具收尾。
                foreach (var input in inputs)
                {
                    ((FileStream)input.GetType().GetField("Stream", Hidden).GetValue(input))?.Dispose();
                }
((IDisposable)typeof(ResourceContentValidation).GetField("_reservation", Hidden).GetValue(validation))?.Dispose();
                validation.Dispose();
            }
        }

        [Test]
        public void NativeReadbackReleaseRejectsAnEarlierSharedInputFaultEvenWhenTheLastInputIsHealthy()
        {
            using var fixture = new ContentFixture();
            using var keys = new TestKeys();
            var key = ResourceContentValidation.CopyKey(new ResourceDecryptionOptions(keys), "catalog", "test-only-v1");
            var inputs = new List<ResourceBundleInput>();
            const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var ownerType = typeof(ResourcePublisher).GetNestedType("NativeReadback", BindingFlags.NonPublic);
            var owners = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ownerType));
            try
            {
                foreach (var file in ResourceContentValidation.ContentFiles(fixture.Snapshot).Take(2))
                {
                    var input = ResourceBundleInput.Open(fixture.Snapshot.OpenVerified(file), ResourceContentValidation.Context(fixture.Snapshot, file), key);
                    inputs.Add(input);
                    var owner = Activator.CreateInstance(ownerType, true);
                    ownerType.GetField("Input", Hidden).SetValue(owner, input);
                    owners.Add(owner);
                }
                Assert.That(inputs.Count, Is.EqualTo(2));
                // 模拟S在自身检查完成后、A消费依赖时已锁存的故障，不执行失败native读/Scene实验。
                typeof(ResourceDecryptStream).GetMethod("Latch", Hidden).Invoke(inputs[0].Stream,
                    new object[] { new System.Security.Cryptography.CryptographicException("owned late dependency fault") });
                Assert.That(inputs[1].Fault, Is.Null);
                var release = typeof(ResourcePublisher).GetMethod("ReleaseNativeReadbacks", BindingFlags.Static | BindingFlags.NonPublic);
                var error = (Exception)release.Invoke(null, new object[] { owners });
                Assert.That(error, Is.Not.Null, "An earlier dependency fault must prevent a successful publication, not disappear during cleanup.");
                Assert.That(error.ToString(), Does.Contain("owned late dependency fault"));
                Assert.That(inputs.All(x => x.IsDisposed), Is.True, "A content fault does not excuse leaving safely unloaded inputs alive.");
            }
            finally
            {
                foreach (var input in inputs)
                {
                    input.Dispose();
                }

                Array.Clear(key, 0, key.Length);
            }
        }

        private sealed class FailOnceFileStream : FileStream
        {
            private bool _hasFailed;
            internal FailOnceFileStream(string path) : base(path, FileMode.Open, FileAccess.Read, FileShare.Read) { }
            protected override void Dispose(bool disposing)
            {
                if (disposing && !_hasFailed)
                {
                    _hasFailed = true;
                    throw new IOException("owned first close failure");
                }
                base.Dispose(disposing);
            }
        }

        [Test]
        public void KnownDeliveryLengthMismatchIsRejectedBeforeReadingTheWholeFile()
        {
            using var fixture = new ContentFixture();
            var original = fixture.Snapshot.Document.Bundles[0];
            var mismatch = new ResourceCatalogFile(new JObject
            {
                ["RelativeFile"] = original.RelativeFile,
                ["Length"] = original.Length + 1,
                ["Sha256"] = original.Sha256
            });
            using var stream = new CountingFileStream(Path.Combine(fixture.Snapshot.DirectoryPath, original.RelativeFile));
            Assert.Throws<InvalidDataException>(() => ResourceLocalSnapshot.VerifyContentFile(stream, mismatch));
            Assert.That(stream.ReadCalls, Is.Zero, "Catalog length is already known; a mismatch must not cause a whole-file SHA scan.");
        }

        private sealed class CountingFileStream : FileStream
        {
            internal int ReadCalls;
            internal CountingFileStream(string path) : base(path, FileMode.Open, FileAccess.Read, FileShare.Read) { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                return base.Read(buffer, offset, count);
            }
        }

        [Test]
        public void AudioCacheRejectsNewBorrowersAfterABackendFaultWithoutDestroyingAnExistingVoiceLease()
        {
            var clip = AudioClip.Create("owned-admission-test", 8, 1, 8000, false);
            var provider = new CacheAdmissionProvider(clip);
            var cache = new AudioClipCache();
            AudioClipLease held = null;
            AudioClipLease unwanted = null;
            try
            {
                var address = new AudioClipAddress(provider, "owned");
                held = cache.Load(address);
                provider.Faulted = true;
                var error = Assert.Throws<ResourceException>(() => unwanted = cache.Load(address));
                Assert.That(error.Kind, Is.EqualTo(ResourceError.ContentAuthenticationFailed));
                Assert.That(held.Clip, Is.SameAs(clip));
                Exception asyncError = null;
                AudioClipLease asyncLease = null;
                using var waiter = cache.LoadAsync(address, (lease, failure) =>
                {
                    asyncLease = lease;
                    asyncError = failure;
                }, null);
                Assert.That(asyncLease, Is.Null);
                Assert.That(asyncError, Is.SameAs(error));
                Assert.That(provider.Loads, Is.EqualTo(1), "The cached path must reject admission without opening a second backend.");
                held.Dispose();
                Assert.That(provider.Releases, Is.EqualTo(1));
            }
            finally
            {
                unwanted?.Dispose();
                held?.Dispose();
                cache.Dispose();
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        private sealed class CacheAdmissionProvider : IAudioClipProvider, IDisposable
        {
            private readonly AudioClip _clip;
            internal bool Faulted;
            internal int Loads;
            internal int Releases;
            internal CacheAdmissionProvider(AudioClip clip) => this._clip = clip;
            public bool CanCache => true;
            public AudioClipLease Load(string key)
            {
                Loads++;
                return new AudioClipLease(_clip, this, () =>
                {
                    if (Faulted)
                    {
                        throw new ResourceException(ResourceError.ContentAuthenticationFailed, "owned cached backend fault");
                    }
                });
            }
            public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress) => completed(Load(key), null);
            public void Dispose() => Releases++;
        }

        private sealed class TestKeys : IResourceContentKeyProvider, IDisposable
        {
            internal int Copies;
            internal byte[] LastDestination;
            internal bool Missing;
            internal bool Wrong;
            internal bool Throw;
            internal bool Disposed;
            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                Copies++;
                LastDestination = destination;
                Assert.That(projectId, Is.EqualTo("catalog"));
                Assert.That(keyId, Is.EqualTo("test-only-v1"));
                Assert.That(destination.Length, Is.EqualTo(32));
                if (Throw)
                {
                    throw new InvalidOperationException("test-sensitive-provider-material");
                }

                if (Missing)
                {
                    return false;
                }

                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = Wrong ? (byte)0 : (byte)(i + 1);
                }

                return true;
            }
            public void Dispose() => Disposed = true;
        }

        private sealed class ContentFixture : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "kilo", "cv-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            internal ResourceLocalSnapshot Snapshot
            {
                get;
            }
            internal ContentFixture()
            {
                var payload = ResourceCatalogCodecTests.Payload();
                payload["UnityCompatibility"] = Application.unityVersion;
                var files = new Dictionary<string, byte[]>();
                // Non-production deterministic material is confined to this synthetic codec/Store fixture.
                var key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
                try
                {
                    foreach (JObject record in ((JArray)payload["Bundles"]).Concat((JArray)payload["SupportFiles"]))
                    {
                        var name = (string)record["RelativeFile"];
                        var role = record["LogicalId"] != null ? "Bundle" : name == "manifest" ? "Manifest" : null;
                        var bytes = Encoding.UTF8.GetBytes("UnityFS\0synthetic-codec-fixture:" + name);
                        if (role != null)
                        {
                            using var raw = new MemoryStream(bytes);
                            using var encoded = new MemoryStream();
                            ResourceBundleEnvelope.Encrypt(raw, encoded, new ResourceBundleContext("catalog", "StandaloneWindows64",
                                Application.unityVersion, role, role == "Manifest" ? "manifest" : (string)record["LogicalId"]), "test-only-v1", key);
                            bytes = encoded.ToArray();
                        }
                        record["Length"] = bytes.Length;
                        record["Sha256"] = ResourceCatalogCodec.Digest(bytes);
                        files.Add(name, bytes);
                    }
                }
                finally
                {
                    Array.Clear(key, 0, key.Length);
                }
                var catalog = ResourceCatalogCodec.Seal(ResourceCatalogCodec.CanonicalBytes(payload));
                var digest = ResourceCatalogCodec.Digest(catalog);
                var document = ResourceCatalogCodec.Read(catalog, "catalog", "StandaloneWindows64", Application.unityVersion, "app-v1");
                var path = Path.Combine(_root, digest);
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                foreach (var file in files)
                {
                    File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, file.Key)), file.Value);
                }

                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, "resource-catalog.json")), catalog);
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, ".ylr-complete.json")), ResourceCatalogCodec.CanonicalBytes(new JObject
                {
                    ["Version"] = 1,
                    ["ProjectGuid"] = new string('1', 32),
                    ["ProjectId"] = "catalog",
                    ["Platform"] = "StandaloneWindows64",
                    ["BuildId"] = document.BuildId,
                    ["CatalogDigest"] = digest
                }));
                Snapshot = ResourceLocalSnapshot.Open(path, "catalog", digest, "StandaloneWindows64", "app-v1");
            }
            public void Dispose()
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_root)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_root), true);
                }
            }
        }
    }
}
