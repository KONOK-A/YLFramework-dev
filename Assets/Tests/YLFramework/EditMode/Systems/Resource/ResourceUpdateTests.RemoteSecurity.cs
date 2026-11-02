using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceUpdateTests
    {
        [UnityTest]
        public IEnumerator TamperedRemoteMetadataCatalogCompleteAndBundleCannotPrepareOrChangeActiveAndGood()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            foreach (var mode in new[] { "signature", "catalog", "complete", "bundle", "path" })
            {
                var source = ResourceDownloadTests.Source(server, certificate);
                var store = RemoteStore(source);
                server.Responder = Serve(Signed(1, 5, signing), Snapshot());
                UpdatePlan initial = null;
                yield return Steps(store.CheckRemote(store.Mount(), value => initial = value));
                PreparedUpdate current = null;
                yield return Steps(store.PrepareRemote(initial, value => current = value));
                store.Commit(current);
                var active = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store)));
                var good = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store, "last-known-good.json")));
                var metadata = Signed(2, 8, signing);
                if (mode == "signature")
                {
                    var header = ResourceCatalogCodec.ParseObject(metadata);
                    var signature = Convert.FromBase64String((string)header["Signature"]);
                    signature[0] ^= 1;
                    header["Signature"] = Convert.ToBase64String(signature);
                    metadata = ResourceCatalogCodec.CanonicalBytes(header);
                }
                server.Responder = Serve(metadata, Snapshot("a", 2), (request, response) =>
                {
                    if (mode == "catalog" && request.Path.EndsWith("/resource-catalog.json", StringComparison.Ordinal))
                    {
                        response.Body = (byte[])response.Body.Clone();
                        response.Body[0] ^= 1;
                    }
                });
                if (mode == "path")
                {
                    var catalog = ResourceCatalogCodec.ParseObject(ResourceCatalogCodec.Write(Snapshot("a", 2).Document));
                    catalog["Bundles"][0]["RelativeFile"] = "../escape";
                    var bytes = ResourceCatalogCodec.CanonicalBytes(catalog);
                    var digest = ResourceCatalogCodec.Digest(bytes);
                    metadata = Resign(metadata, signing, data =>
                    {
                        data["CatalogDigest"] = digest;
                        data["CatalogLength"] = bytes.LongLength;
                    });
                    server.Responder = request => request.Path == "/release.json" ? new ResourceTlsServer.Response { Body = metadata } :
                        request.Path == "/" + digest + "/resource-catalog.json" ? new ResourceTlsServer.Response { Body = bytes } : new ResourceTlsServer.Response { StatusCode = 404 };
                }
                UpdatePlan plan = null;
                Exception rejection = null;
                yield return Steps(store.CheckRemote(store.Mount(), value => plan = value), error => rejection = error);
                if (mode == "complete" || mode == "bundle")
                {
                    Assert.That(plan, Is.Not.Null, mode);
                    var selected = mode == "complete" ? ".ylr-complete.json" : plan.Projects[0].Files.First(x => !x.IsReusable && x.Length > 0).RelativeFile;
                    server.Responder = Serve(metadata, Snapshot("a", 2), (request, response) =>
                    {
                        if (request.Path.EndsWith("/" + selected, StringComparison.Ordinal))
                        {
                            response.Body = (byte[])response.Body.Clone();
                            response.Body[0] ^= 1;
                        }
                    });
                    yield return Steps(store.PrepareRemote(plan, _ => Assert.Fail("Unverified remote bytes produced Prepared.")), error => rejection = error);
                }
                Assert.That(rejection, Is.TypeOf<InvalidDataException>(), mode + ": " + rejection);
                Assert.That(store.GetPreparedUpdates(), Is.Empty, mode);
                Assert.That(store.Mount()[0].Document.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
                CollectionAssert.AreEqual(active, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), mode);
                CollectionAssert.AreEqual(good, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store, "last-known-good.json"))), mode);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(VersionPath(store, 2))), Is.False, mode);
            }
        }

        [Test]
        public void PartialWrongOwnerOrMissingMarkerCannotBeAdoptedOverwrittenOrVerified()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var store = RemoteStore(ResourceDownloadTests.Source(server, certificate));
            Prepare(store);
            var release = new ResourceRemoteRelease(Signed(2, 7, signing), store.Projects[0], Application.unityVersion, DateTimeOffset.UtcNow);
            var file = Snapshot("a", 2).Document.Bundles.First();
            var part = new ArtifactStore.DownloadPart(store, release, file.RelativeFile, file.Length, file.Sha256);
            using (var output = part.OpenWrite(0, "\"stable\""))
            {
                output.WriteByte(7);
            }

            var path = Path.ChangeExtension(part.Path, ".json");
            var record = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var foreign = ResourceCatalogCodec.ParseObject(record);
            foreign["OwnerId"] = new string('0', 32);
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), ResourceCatalogCodec.CanonicalBytes(foreign));
            Assert.Throws<InvalidDataException>(() => part.OpenWrite(0, null));
            Assert.Throws<InvalidDataException>(() => part.Verify());
            store.Cleanup(store.Mount());
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(part.Path)), Is.EqualTo(new byte[] { 7 }));
            CollectionAssert.AreEqual(ResourceCatalogCodec.CanonicalBytes(foreign), File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path)); // 此测试创建的确切marker，模拟未注册partial；不让清理器猜测其归属。
            Assert.Throws<InvalidDataException>(() => new ArtifactStore.DownloadPart(store, release, file.RelativeFile, file.Length, file.Sha256));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(part.Path)), Is.EqualTo(new byte[] { 7 }));
        }

        [UnityTest]
        public IEnumerator ReusedOwnedVersionMustMatchSignedCompleteAtCheckPrepareAndCommit()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var store = RemoteStore(ResourceDownloadTests.Source(server, certificate));
            Prepare(store);
            var metadata = Signed(2, 7, signing);
            server.Responder = Serve(Resign(metadata, signing, data => data["CompleteSha256"] = new string('f', 64)), Snapshot("a", 2));
            Exception wrongManifest = null;
            yield return Steps(store.CheckRemote(store.Mount(), _ => Assert.Fail("Owned cache bypassed the authenticated Complete file identity.")), error => wrongManifest = error);
            Assert.That(wrongManifest, Is.TypeOf<InvalidDataException>());

            server.Responder = Serve(metadata, Snapshot("a", 2));
            UpdatePlan plan = null;
            yield return Steps(store.CheckRemote(store.Mount(), value => plan = value));
            var completePath = Path.Combine(VersionPath(store, 2), ".ylr-complete.json");
            var original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(completePath));
            var tampered = ResourceCatalogCodec.ParseObject(original);
            tampered["ProjectGuid"] = new string('0', 32);
            var other = ResourceCatalogCodec.CanonicalBytes(tampered);
            try
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(completePath), other);
                Exception rejected = null;
                yield return Steps(store.PrepareRemote(plan, _ => Assert.Fail("Prepare reused a Complete file different from signed authorization.")), error => rejected = error);
                Assert.That(rejected, Is.TypeOf<InvalidDataException>());
            }
            finally
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(completePath), original);
            }
            PreparedUpdate prepared = null;
            yield return Steps(store.PrepareRemote(plan, value => prepared = value));
            try
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(completePath), other);
                Assert.Throws<InvalidDataException>(() => store.Commit(prepared), "Activation must recheck authenticated Complete even when its JSON remains structurally valid.");
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), Is.False);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store, "commit-journal.json"))), Is.False);
            }
            finally
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(completePath), original);
            }
        }

        [UnityTest]
        public IEnumerator SignedPreparedAndConfirmedHistorySurviveActualDomainReload()
        {
            yield return SaveRemotePreparedForReload();
            yield return new EnterPlayMode();
            yield return ActivateRemotePreparedAfterReload();
            yield return new ExitPlayMode();
            yield return RejectReplayAfterReload();
        }

        private static IEnumerator SaveRemotePreparedForReload()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var store = RemoteStore(ResourceDownloadTests.Source(server, certificate));
            server.Responder = Serve(Signed(2, 7, signing), Snapshot("a", 2));
            UpdatePlan plan = null;
            yield return Steps(store.CheckRemote(store.Mount(), value => plan = value));
            yield return Steps(store.PrepareRemote(plan, _ => { }));
            SessionState.SetString(Key + "remote-restart", store.RootPath);
        }

        private static IEnumerator ActivateRemotePreparedAfterReload()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            var store = RemoteStore(ResourceDownloadTests.Source(server, certificate), SessionState.GetString(Key + "remote-restart", ""));
            yield return Initialize(store);
            var system = ResourceSystem.Current;
            var prepared = store.GetPreparedUpdates().Single();
            Assert.That(prepared.Releases.Single().Sequence, Is.EqualTo(7));
            using var activating = system.ActivatePreparedUpdateAsync(prepared);
            yield return Wait(activating);
            Assert.That(Take(activating), Is.True);
            using (var lease = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(lease.Asset.text, Is.EqualTo("release87-a-v2"));
            }

            Assert.That(Take(system.ShutdownAsync()), Is.True);
            yield break;
        }

        private static IEnumerator RejectReplayAfterReload()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var store = RemoteStore(ResourceDownloadTests.Source(server, certificate), SessionState.GetString(Key + "remote-restart", ""));
            server.Responder = Serve(Signed(1, 6, signing), Snapshot());
            Exception rejected = null;
            yield return Steps(store.CheckRemote(store.Mount(), _ => Assert.Fail("Restart forgot a confirmed sequence.")), error => rejected = error);
            Assert.That(rejected, Is.TypeOf<InvalidDataException>());
            Assert.That(store.Mount()[0].Document.CatalogDigest, Is.EqualTo(Snapshot("a", 2).Document.CatalogDigest));
        }
    }
}
