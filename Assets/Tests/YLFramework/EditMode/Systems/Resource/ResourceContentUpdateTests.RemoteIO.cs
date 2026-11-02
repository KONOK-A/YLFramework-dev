using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceContentUpdateTests
    {
        private static ResourceRemoteSource RemoteSource(RSA key)
        {
            var publicKey = key.ExportParameters(false);
            return new ResourceRemoteSource("https://fixture.invalid/releases/", "isolated", new[] {
                new ResourceSigningKey("test-signing", Convert.ToBase64String(publicKey.Modulus), Convert.ToBase64String(publicKey.Exponent)) });
        }

        private static ResourceRemoteRelease SignedRemote(UpdateFixture fixture, ResourceLocalSnapshot snapshot, RSA key)
        {
            var document = snapshot.Document;
            var catalog = ResourceCatalogCodec.Write(document);
            var complete = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(snapshot.DirectoryPath, ".ylr-complete.json")));
            var payload = new JObject
            {
                ["ProjectId"] = document.ProjectId,
                ["Platform"] = document.Platform,
                ["UnityCompatibility"] = document.UnityCompatibility,
                ["AppContentCompatibilityId"] = document.AppContentCompatibilityId,
                ["Channel"] = "isolated",
                ["ReleaseSequence"] = 1,
                ["IssuedUnixSeconds"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60,
                ["ExpiresUnixSeconds"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
                ["BuildId"] = document.BuildId,
                ["CatalogDigest"] = document.CatalogDigest,
                ["CatalogLength"] = catalog.Length,
                ["CompleteLength"] = complete.Length,
                ["CompleteSha256"] = ResourceCatalogCodec.Digest(complete)
            };
            var envelope = new JObject
            {
                ["SchemaVersion"] = 1,
                ["Algorithm"] = "RSA-SHA256-PKCS1",
                ["KeyId"] = "test-signing",
                ["Payload"] = Convert.ToBase64String(ResourceCatalogCodec.CanonicalBytes(payload))
            };
            envelope["Signature"] = Convert.ToBase64String(key.SignData(ResourceCatalogCodec.CanonicalBytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            var mount = fixture.Store.Projects.Single();
            var release = new ResourceRemoteRelease(ResourceCatalogCodec.CanonicalBytes(envelope), mount, Application.unityVersion, DateTimeOffset.UtcNow);
            release.ReadCatalog(catalog, mount, Application.unityVersion);
            return release;
        }

        [Test]
        public void RemoteInstalledReuseDispatchesFullIOWithoutAnyNetworkOrMainThreadHash()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore("The remote runtime platform gate remains Windows Editor only.");
            }

            using var key = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
            using var fixture = new UpdateFixture(remote: RemoteSource(key));
            var target = fixture.Create("already-installed");
            fixture.Store.Prepare(fixture.Store.Check(fixture.Store.Mount(), new[] { target }), CancellationToken.None);
            var release = SignedRemote(fixture, target, key);
            var plan = new UpdatePlan(fixture.Store.RootPath, new[] { new UpdateProject(fixture.BuiltIn, release, Array.Empty<UpdateFile>()) }, long.MaxValue, 16384);
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var ioThreads = new List<int>();
            IEnumerator Dispatch(Action<CancellationToken> action)
            {
                RunIOWorker(token =>
                {
                    ioThreads.Add(Thread.CurrentThread.ManagedThreadId);
                    action(token);
                });
                yield break;
            }
            var stack = new Stack<IEnumerator>();
            stack.Push(fixture.Store.InstallRemotePreparation(plan, runIO: Dispatch));
            try
            {
                while (stack.Count != 0)
                {
                    var routine = stack.Peek();
                    if (!routine.MoveNext())
                    {
                        stack.Pop();
                        (routine as IDisposable)?.Dispose();
                    }
                    else if (routine.Current is IEnumerator child)
                    {
                        stack.Push(child);
                    }
                    else
                    {
                        Assert.Fail("Installed reuse must not perform a network GET.");
                    }
                }
            }
            finally
            {
                while (stack.Count != 0)
                {
                    (stack.Pop() as IDisposable)?.Dispose();
                }
            }
            Assert.That(ioThreads.Count, Is.EqualTo(2), "Both From retention and signed installed-To revalidation go through the supplied IO dispatcher.");
            Assert.That(ioThreads.All(x => x != mainThread), Is.True);
            Assert.That(fixture.Store.GetPreparedUpdates().Count, Is.EqualTo(1), "Installation alone cannot create a second Prepared authorization.");
        }

        [Test]
        public void CompletedPartialVerificationIsCancelablePureIOAndNeverBecomesAVersionCredential()
        {
            using var key = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
            using var fixture = new UpdateFixture(remote: RemoteSource(key));
            var target = fixture.Create("part-source");
            fixture.Store.Prepare(fixture.Store.Check(fixture.Store.Mount(), new[] { target }), CancellationToken.None);
            var release = SignedRemote(fixture, target, key);
            var file = target.Document.Bundles[0];
            var part = new ArtifactStore.DownloadPart(fixture.Store, release, file.RelativeFile, file.Length, file.Sha256);
            using (var output = part.OpenWrite(0, "\"stable\""))
            {
                var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(target.DirectoryPath, file.RelativeFile)));
                output.Write(bytes, 0, bytes.Length);
            }
            Assert.Throws<OperationCanceledException>(() => RunIOWorker(part.Verify, new CancellationToken(true)));
            Assert.That(part.Length, Is.EqualTo(file.Length), "Cancellation preserves a resumable Partial, not a Complete claim.");
            RunIOWorker(part.Verify);
            Assert.That(fixture.Store.GetPreparedUpdates().Count, Is.EqualTo(1));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("active.json"))), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemoteCheckUsesReadOnlyWorkerForReusableAndInstalledDeliveryBytesWithoutKeys(bool installed)
        {
            if (installed)
            {
                RequireEncryptedBackend();
            }

            using var key = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
            using var fixture = new UpdateFixture(remote: RemoteSource(key));
            var target = installed ? fixture.Create("installed-ciphertext", encrypted: true, corruptMiddle: true) : fixture.BuiltIn;
            if (installed)
            {
                fixture.Store.InstallPreparation(fixture.Store.Check(new[] { fixture.BuiltIn }, new[] { target }), CancellationToken.None);
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.Store.RootPath, ".ylr-store.lock")));
            }
            var release = SignedRemote(fixture, target, key);
            var before = ReadStoreTree(fixture.Store.RootPath);
            IReadOnlyList<UpdateFile> files = null;
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var checkThread = mainThread;
            RunIOWorker(token =>
            {
                checkThread = Thread.CurrentThread.ManagedThreadId;
                files = fixture.Store.CheckRemoteFilesForContent(fixture.BuiltIn, release, token);
            });
            Assert.That(checkThread, Is.Not.EqualTo(mainThread));
            if (installed)
            {
                Assert.That(files, Is.Empty, "Signed installed delivery revalidation does not imply AEAD/key availability.");
            }
            else
            {
                Assert.That(files, Is.Not.Empty);
                Assert.That(files.All(x => x.IsReusable), Is.True);
            }
            Assert.That(ReadStoreTree(fixture.Store.RootPath), Is.EqualTo(before));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.Store.RootPath, ".ylr-store.lock"))), Is.False);
        }

        [Test]
        public void CanceledRemoteCheckCreatesNoStoreAndAnExplicitRetryRemainsKeyFree()
        {
            using var key = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
            using var fixture = new UpdateFixture(remote: RemoteSource(key));
            var release = SignedRemote(fixture, fixture.BuiltIn, key);
            Assert.Throws<OperationCanceledException>(() => RunIOWorker(token =>
                fixture.Store.CheckRemoteFilesForContent(fixture.BuiltIn, release, token), new CancellationToken(true)));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Store.RootPath)), Is.False);
            IReadOnlyList<UpdateFile> files = null;
            RunIOWorker(token => files = fixture.Store.CheckRemoteFilesForContent(fixture.BuiltIn, release, token));
            Assert.That(files.All(x => x.IsReusable), Is.True);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Store.RootPath)), Is.False);
        }
    }
}
