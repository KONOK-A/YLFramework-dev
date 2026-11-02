using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceUpdateTests
    {
        [UnityTest]
        public IEnumerator RecoveryKeepsConfirmedHistoryWhenBothContentPointersFailButTheirFloorMetadataIsIntact()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var source = ResourceDownloadTests.Source(server, certificate);
            var store = RemoteStore(source);
            var replay = Signed(1, 5, signing);
            foreach (var version in new[] { 1, 2, 3 })
            {
                server.Responder = Serve(version == 1 ? replay : Signed(version, version + 4, signing), Snapshot("a", version));
                UpdatePlan plan = null;
                yield return Steps(store.CheckRemote(store.Mount(), value => plan = value));
                PreparedUpdate prepared = null;
                yield return Steps(store.PrepareRemote(plan, value => prepared = value));
                store.Commit(prepared);
            }
            foreach (var version in new[] { 2, 3 })
            {
                var file = Snapshot("a", version).Document.Bundles.First().RelativeFile;
                using var output = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(VersionPath(store, version), file)), FileMode.Append, FileAccess.Write, FileShare.None);
                output.WriteByte(42);
            }
            store = RemoteStore(source, store.RootPath);
            Assert.That(store.Mount()[0].Document.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
            Assert.That((long)ResourceCatalogCodec.ParseObject(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))))["ReleaseFloors"][0]["Sequence"], Is.EqualTo(7));
            server.Responder = Serve(Signed(1, 8, signing), Snapshot());
            UpdatePlan repair = null;
            yield return Steps(store.CheckRemote(store.Mount(), value => repair = value));
            PreparedUpdate pending = null;
            yield return Steps(store.PrepareRemote(repair, value => pending = value));
            store.Checkpoint = point =>
            {
                if (point == "JournalVerified")
                {
                    throw new IOException("injected verified-phase repair failure");
                }
            };
            Assert.Throws<IOException>(() => store.Commit(pending));
            store = RemoteStore(source, store.RootPath);
            var selected = store.Mount();
            Assert.That(selected[0].Document.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
            server.Responder = Serve(replay, Snapshot());
            Exception rejected = null;
            yield return Steps(store.CheckRemote(selected, _ => Assert.Fail("Recovery discarded readable confirmed release history.")), error => rejected = error);
            Assert.That(rejected, Is.TypeOf<InvalidDataException>());
            Assert.That((long)ResourceCatalogCodec.ParseObject(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))))["ReleaseFloors"][0]["Sequence"], Is.EqualTo(7), "Failure neither confirms8 nor forgets7.");
        }

        [UnityTest]
        public IEnumerator SameTransitionUpgradesV1PreparedAndRefreshesAuthenticatedMetadataWithoutLosingHigherSequence()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var source = ResourceDownloadTests.Source(server, certificate);
            var store = RemoteStore(source);
            var local = Prepare(store);
            var envelope = Signed(2, 7, signing);
            foreach (var metadata in new[] { envelope, Resign(envelope, signing, data => data["ExpiresUnixSeconds"] = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds()), Signed(2, 8, signing) })
            {
                server.Responder = Serve(metadata, Snapshot("a", 2));
                UpdatePlan plan = null;
                yield return Steps(store.CheckRemote(store.Mount(), value => plan = value));
                var before = server.RequestCount;
                PreparedUpdate refreshed = null;
                yield return Steps(store.PrepareRemote(plan, value => refreshed = value));
                CollectionAssert.AreEqual(metadata, refreshed.Releases.Single().Envelope);
                Assert.That(server.RequestCount, Is.EqualTo(before), "Same complete target is not redownloaded for an authorization refresh.");
            }
            var stillLocal = Prepare(store);
            Assert.That(stillLocal.Releases.Single().Sequence, Is.EqualTo(8), "Local idempotence must not strip an existing remote authorization.");
            Assert.That(store.GetPreparedUpdates().Count, Is.EqualTo(1));
            store.Commit(local); // 稳定transition旧句柄仍使用当前权威记录，而非过期的授权副本。
            Assert.That((long)ResourceCatalogCodec.ParseObject(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))))["ReleaseFloors"][0]["Sequence"], Is.EqualTo(8));
        }

        [UnityTest]
        public IEnumerator CancellationDuringSynchronousCopyChecksEveryConsumerAndSessionExitCannotPublishLatePrepared()
        {
            yield return new EnterPlayMode();
            yield return VerifyCopyCancellationAndExit();
        }

        private static IEnumerator VerifyCopyCancellationAndExit()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            server.Responder = Serve(Signed(1, 7, signing), Snapshot());
            var store = RemoteStore(ResourceDownloadTests.Source(server, certificate));
            yield return Initialize(store);
            var system = ResourceSystem.Current;
            using var check = system.CheckRemoteUpdatesAsync();
            yield return Wait(check);
            var plan = Take(check);
            using (var cancellation = new CancellationTokenSource())
            {
                store.Checkpoint = point =>
                {
                    if (point == "FileCopied")
                    {
                        cancellation.Cancel();
                    }
                };
                using var only = system.PrepareUpdateAsync(plan, cancellation.Token);
                yield return Wait(only);
                Assert.That(only.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                yield return WaitForUpdates(system);
                Assert.That(system.IsAvailable, Is.True);
                Assert.That(store.GetPreparedUpdates(), Is.Empty, "Canceled synchronous copy must not publish Prepared.");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                store.Checkpoint = point =>
                {
                    if (point == "FileCopied")
                    {
                        cancellation.Cancel();
                    }
                };
                using var canceled = system.PrepareUpdateAsync(plan, cancellation.Token);
                using var remaining = system.PrepareUpdateAsync(plan);
                yield return Wait(remaining);
                Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                Assert.That(Take(remaining), Is.Not.Null, "One canceled waiter cannot cancel the remaining producer owner.");
            }
            store.Checkpoint = null;
            var metadata = Signed(2, 8, signing);
            server.Responder = Serve(metadata, Snapshot("a", 2));
            using var checking = system.CheckRemoteUpdatesAsync();
            yield return Wait(checking);
            var next = Take(checking);
            var file = next.Projects[0].Files.First(x => !x.IsReusable && x.Length > 32).RelativeFile;
            server.Responder = Serve(metadata, Snapshot("a", 2), (request, response) =>
            {
                if (request.Path.EndsWith("/" + file, StringComparison.Ordinal))
                {
                    response.StallAfterBytes = 128;
                    response.ChunkSize = 32;
                }
            });
            using var late = system.PrepareUpdateAsync(next);
            var deadline = Time.realtimeSinceStartup + 10;
            while (!late.IsDone && late.Progress <= 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(late.Progress, Is.GreaterThan(0));
            typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Assert.That(late.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            yield return WaitForUpdates(system);

            Assert.That(store.GetPreparedUpdates().All(x => x.Projects[0].ToDigest != Snapshot("a", 2).Document.CatalogDigest), Is.True);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), Is.False);
        }
    }
}
