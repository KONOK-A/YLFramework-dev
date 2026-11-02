using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceUpdateTests
    {
        private static ArtifactStore RemoteStore(ResourceRemoteSource source, string path = null, bool multi = false)
            => new(path ?? NewStore(multi).RootPath, (multi ? new[] { "a", "b" } : new[] { "a" }).Select(x =>
                new ResourceProjectMount(Snapshot(x).Document.ProjectId, "StandaloneWindows64", "fixture-v1", Snapshot(x), remote: x == "a" ? source : null)));

        private static Func<ResourceTlsServer.Request, ResourceTlsServer.Response> Serve(byte[] envelope, ResourceLocalSnapshot snapshot,
            Action<ResourceTlsServer.Request, ResourceTlsServer.Response> edit = null)
        {
            var catalog = ResourceCatalogCodec.Write(snapshot.Document);
            var files = snapshot.Document.Bundles.Cast<ResourceCatalogFile>().Concat(snapshot.Document.SupportFiles).Select(x => x.RelativeFile)
                .Concat(new[] { ".ylr-complete.json" }).ToDictionary(x => "/" + snapshot.Document.CatalogDigest + "/" + x, x => File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(snapshot.DirectoryPath, x))), StringComparer.Ordinal);
            files.Add("/" + snapshot.Document.CatalogDigest + "/resource-catalog.json", catalog);
            return request =>
            {
                var response = request.Path == "/release.json" ? new ResourceTlsServer.Response { Body = envelope } :
                    files.TryGetValue(request.Path, out var body) ? new ResourceTlsServer.Response { Body = body, Headers = { ["ETag"] = "\"stable\"" } } :
                    new ResourceTlsServer.Response { StatusCode = 404 };
                edit?.Invoke(request, response);
                return response;
            };
        }

        private static IEnumerator Steps(IEnumerator routine, Action<Exception> rejection = null)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            try
            {
                while (stack.Count != 0)
                {
                    var current = stack.Peek();
                    bool more = false;
                    Exception failure = null;
                    try
                    {
                        more = current.MoveNext();
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                    if (failure != null)
                    {
                        if (rejection == null)
                        {
                            throw failure;
                        }
                        rejection(failure);
                        yield break;
                    }
                    if (!more)
                    {
                        stack.Pop();
                        (current as IDisposable)?.Dispose();
                        continue;
                    }
                    if (current.Current is IEnumerator child)
                    {
                        stack.Push(child);
                    }
                    else
                    {
                        yield return null;
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
        }

        [Test]
        public void OwnedPartialCleanupRemovesOnlyRegisteredUnverifiedFilesAndPreservesUnknownData()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var source = ResourceDownloadTests.Source(server, certificate);
            var store = RemoteStore(source);
            Prepare(store);
            var release = new ResourceRemoteRelease(Signed(2, 7, signing), store.Projects[0], Application.unityVersion, DateTimeOffset.UtcNow);
            release.ReadCatalog(ResourceCatalogCodec.Write(Snapshot("a", 2).Document), store.Projects[0], Application.unityVersion);
            var file = release.Catalog.Bundles.First();
            var part = new ArtifactStore.DownloadPart(store, release, file.RelativeFile, file.Length, file.Sha256);
            using (var output = part.OpenWrite(0, "\"stable\""))
            {
                output.WriteByte(7);
            }

            Assert.Throws<InvalidDataException>(() => part.Verify());
            var unknown = Path.Combine(ProjectPath(store, "parts"), "user.part");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(unknown), "retained unknown data");
            store.Cleanup(store.Mount());
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(part.Path)), Is.False, "Explicit store cleanup owns registered partial data.");
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.ChangeExtension(part.Path, ".json"))), Is.False);
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(unknown)), Is.EqualTo("retained unknown data"));
            Assert.That(store.GetPreparedUpdates().Count, Is.EqualTo(1), "Partial cleanup cannot discard a verified prepared closure.");
        }

        [UnityTest]
        public IEnumerator RemoteConnectionInterruptionAutomaticallyResumesAndUntrustedBoundsNeverPrepare()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var source = ResourceDownloadTests.Source(server, certificate);
            var store = RemoteStore(source);
            var envelope = Signed(2, 7, signing);
            server.Responder = Serve(envelope, Snapshot("a", 2));
            UpdatePlan plan = null;
            yield return Steps(store.CheckRemote(store.Mount(), value => plan = value));
            var selected = plan.Projects[0].Files.First(x => !x.IsReusable && x.Length > 32).RelativeFile;
            var calls = 0;
            var ranges = 0;
            server.Responder = Serve(envelope, Snapshot("a", 2), (request, response) =>
            {
                if (!request.Path.EndsWith("/" + selected, StringComparison.Ordinal))
                {
                    return;
                }

                if (Interlocked.Increment(ref calls) == 1)
                {
                    response.DisconnectAfterBytes = response.Body.Length / 2;
                    response.ChunkSize = 32;
                    return;
                }
                Assert.That(request.Headers["If-Range"], Is.EqualTo("\"stable\""));
                var start = int.Parse(request.Headers["Range"].Substring(6).TrimEnd('-'));
                Interlocked.Increment(ref ranges);
                var total = response.Body.Length;
                response.Body = response.Body.Skip(start).ToArray();
                response.StatusCode = 206;
                response.Headers["Content-Range"] = "bytes " + start + "-" + (total - 1) + "/" + total;
            });
            PreparedUpdate prepared = null;
            yield return Steps(store.PrepareRemote(plan, value => prepared = value));
            Assert.That(prepared, Is.Not.Null);
            Assert.That(ranges, Is.EqualTo(1));
            var before = ResourceReleaseFixture.Snapshot(store.RootPath);
            var impossible = new UpdatePlan(store.RootPath, plan.Projects, 0, long.MaxValue);
            Exception capacity = null;
            yield return Steps(store.PrepareRemote(impossible, _ => Assert.Fail("Unbounded preparation passed capacity validation.")), e => capacity = e);
            Assert.That(capacity, Is.TypeOf<IOException>());
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(store.RootPath));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), Is.False);
        }

        [UnityTest]
        public IEnumerator RemoteSignedClosurePreparesWithoutActivationAndConfirmedFloorSurvivesFailureRestartAndRollback()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var source = ResourceDownloadTests.Source(server, certificate);
            var store = RemoteStore(source, multi: true);
            server.Responder = Serve(Signed(2, 7, signing), Snapshot("a", 2));
            UpdatePlan plan = null;
            yield return Steps(store.CheckRemote(store.Mount(), value => plan = value));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(store.RootPath)), Is.False, "Authenticating remote metadata must not claim or mutate the store.");
            Assert.That(plan.Projects.Count, Is.EqualTo(2), "Unchanged projects remain in the exact 8.7 closure.");
            PreparedUpdate prepared = null;
            yield return Steps(store.PrepareRemote(plan, value => prepared = value));
            Assert.That(prepared, Is.Not.Null);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), Is.False, "Verified is not Active.");
            var requests = server.RequestCount;
            yield return Steps(store.PrepareRemote(plan, value => prepared = value));
            Assert.That(server.RequestCount, Is.EqualTo(requests), "Complete owned targets are reverified, not redownloaded.");
            store.Checkpoint = point =>
            {
                if (point == "JournalVerified")
                {
                    throw new IOException("injected before commit decision");
                }
            };
            Assert.Throws<IOException>(() => store.Commit(prepared));
            store = RemoteStore(source, store.RootPath, true);
            Assert.That(store.Mount().Single(x => x.Document.ProjectId == Snapshot().Document.ProjectId).Document.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
            server.Responder = Serve(Signed(1, 6, signing), Snapshot());
            yield return Steps(store.CheckRemote(store.Mount(), _ => { })); // 失败激活没有确认7。
            prepared = store.GetPreparedUpdates().Single();
            store.Commit(prepared);
            foreach (var choice in new[] { (1, 6L), (3, 7L) })
            {
                server.Responder = Serve(Signed(choice.Item1, choice.Item2, signing), Snapshot("a", choice.Item1));
                Exception rejection = null;
                yield return Steps(store.CheckRemote(store.Mount(), _ => Assert.Fail("Rejected release produced a plan.")), e => rejection = e);
                Assert.That(rejection, Is.TypeOf<InvalidDataException>(), choice.ToString());
            }
            server.Responder = Serve(Signed(2, 7, signing), Snapshot("a", 2));
            yield return Steps(store.CheckRemote(store.Mount(), _ => { })); // 同序同digest幂等。
            store.Commit(store.Rollback(store.Mount()));
            var restarted = RemoteStore(source, store.RootPath, true);
            Assert.That(restarted.Mount().Single(x => x.Document.ProjectId == Snapshot().Document.ProjectId).Document.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
            server.Responder = Serve(Signed(1, 6, signing), Snapshot());
            Exception oldRelease = null;
            yield return Steps(restarted.CheckRemote(restarted.Mount(), _ => Assert.Fail("Rollback lowered the release floor.")), e => oldRelease = e);
            Assert.That(oldRelease, Is.TypeOf<InvalidDataException>());
        }

        [UnityTest]
        public IEnumerator RemoteDownloadedVersionUsesNativeBusyBarrierLoadsNewAssetsInstancesScenesAndRollsBack()
        {
            yield return new EnterPlayMode();
            yield return DownloadedNativeVersion();
        }

        private static IEnumerator DownloadedNativeVersion()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            server.Responder = Serve(Signed(2, 7, signing), Snapshot("a", 2));
            var store = RemoteStore(ResourceDownloadTests.Source(server, certificate));
            yield return Initialize(store);
            var system = ResourceSystem.Current;
            using var checking = system.CheckRemoteUpdatesAsync();
            using var joined = system.CheckRemoteUpdatesAsync();
            Assert.That(system.GetDiagnosticsReport().Updates.Single().Phase, Is.EqualTo("Checking"));
            Assert.That(system.GetDiagnosticsReport().Updates.Single().WaiterCount, Is.EqualTo(2));
            checking.Cancel();
            Assert.That(system.GetDiagnosticsReport().Updates.Single().WaiterCount, Is.EqualTo(1));
            yield return Wait(joined);
            var plan = Take(joined);
            Assert.That(checking.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            using var preparing = system.PrepareUpdateAsync(plan);
            using var other = system.PrepareUpdateAsync(plan);
            var pendingUpdate = system.GetDiagnosticsReport().Updates.Single();
            Assert.That(pendingUpdate.Phase, Is.EqualTo("Preparing"));
            Assert.That(pendingUpdate.WaiterCount, Is.EqualTo(2));
            preparing.Cancel();
            yield return Wait(other);
            var prepared = Take(other);
            Assert.That(system.GetDiagnosticsReport().Updates, Is.Empty);
            Assert.That(pendingUpdate.WaiterCount, Is.EqualTo(2), "Update diagnostics must be a copied snapshot, not another mutable job view.");
            using (var old = system.Acquire<TextAsset>(Address()))
            {
                using var blocked = system.ActivatePreparedUpdateAsync(prepared);
                yield return Busy(blocked);
                Assert.That(old.Asset.text, Is.EqualTo("first release content"));
            }
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), Is.False, "Busy cannot advance active or release history.");
            using var activating = system.ActivatePreparedUpdateAsync(prepared);
            yield return Wait(activating);
            Assert.That(Take(activating), Is.True);
            using (var updated = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(updated.Asset.text, Is.EqualTo("release87-a-v2"));
            }

            var instance = system.Instantiate(Address("A.prefab"));
            Assert.That(instance.GameObject, Is.Not.Null);
            instance.Discard();
            system.ClearInstanceCaches(); // Discard结束checkout；空桶的prefab pin仍按既有8.5合同显式退休。
            yield return Drain(system);
            using var loading = system.LoadSceneAsync(Address("Owned.unity"), LoadSceneMode.Additive);
            yield return Wait(loading);
            var scene = loading.TakeResult();
            var native = scene.Scene;
            scene.Dispose();
            Assert.That(native.isLoaded, Is.True);
            var unloading = SceneManager.UnloadSceneAsync(native);
            Assert.That(unloading, Is.Not.Null);
            while (!unloading.isDone)
            {
                yield return null;
            }

            yield return Drain(system);
            using var rollingBack = system.RollbackAsync();
            yield return Wait(rollingBack);
            Assert.That(Take(rollingBack), Is.True);
            using (var old = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(old.Asset.text, Is.EqualTo("first release content"));
            }

            server.Responder = Serve(Signed(1, 6, signing), Snapshot());
            using var replay = system.CheckRemoteUpdatesAsync();
            yield return Wait(replay);
            Assert.That(replay.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(replay.Error.InnerException, Is.TypeOf<InvalidDataException>());
            Assert.That(Take(system.ShutdownAsync()), Is.True);
        }

        [UnityTest]
        public IEnumerator RemoteCancellationKeepsOwnedPartialAndRangeModesReachVerifiedTerminalWithoutMixingRepresentations()
        {
            yield return new EnterPlayMode();
            yield return ResumeModes();
        }

        private static IEnumerator ResumeModes()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var envelope = Signed(2, 7, signing);
            foreach (var mode in new[] { "206", "200", "416", "changed-etag", "no-validator", "wrong-range" })
            {
                var source = ResourceDownloadTests.Source(server, certificate);
                var store = RemoteStore(source);
                yield return Initialize(store);
                var system = ResourceSystem.Current;
                server.Responder = Serve(envelope, Snapshot("a", 2));
                using var check = system.CheckRemoteUpdatesAsync();
                yield return Wait(check);
                var plan = Take(check);
                var file = plan.Projects[0].Files.First(x => !x.IsReusable && x.Length > 32).RelativeFile;
                server.Responder = Serve(envelope, Snapshot("a", 2), (request, response) =>
                {
                    if (!request.Path.EndsWith("/" + file, StringComparison.Ordinal))
                    {
                        return;
                    }

                    response.StallAfterBytes = Math.Min(128, response.Body.Length / 2);
                    response.ChunkSize = 32;
                    if (mode == "no-validator")
                    {
                        response.Headers.Remove("ETag");
                    }
                });
                using var first = system.PrepareUpdateAsync(plan);
                var deadline = Time.realtimeSinceStartup + 10;
                while (!first.IsDone && first.Progress <= 0 && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(first.IsDone, Is.False, first.Error?.ToString());
                Assert.That(first.Progress, Is.GreaterThan(0), mode);
                var downloading = system.GetDiagnosticsReport().Updates.Single();
                Assert.That(downloading.Phase, Is.EqualTo("Downloading"));
                Assert.That(downloading.TransferredBytes, Is.GreaterThan(0));
                Assert.That(downloading.ExpectedBytes, Is.GreaterThan(downloading.TransferredBytes));
                Assert.That(downloading.RetryCount, Is.Zero);
                first.Cancel();
                Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                Assert.That(store.GetPreparedUpdates(), Is.Empty);
                var partPath = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store, "parts")), "*.part").Single(x => new FileInfo(x).Length > 0 && new FileInfo(x).Length < plan.Projects[0].Files.First(f => f.RelativeFile == file).Length);
                Assert.That(new FileInfo(partPath).Length, Is.GreaterThan(0));
                var ranged = 0;
                var full = 0;
                server.Responder = Serve(envelope, Snapshot("a", 2), (request, response) =>
                {
                    if (!request.Path.EndsWith("/" + file, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (request.Headers.TryGetValue("Range", out var range))
                    {
                        Interlocked.Increment(ref ranged);
                        Assert.That(request.Headers["If-Range"], Is.EqualTo("\"stable\""));
                        var start = int.Parse(range.Substring(6).TrimEnd('-'));
                        var total = response.Body.Length;
                        if (mode == "416")
                        {
                            response.StatusCode = 416;
                            response.Body = Array.Empty<byte>();
                            response.Headers["Content-Range"] = "bytes */" + total;
                        }
                        else if (mode != "200")
                        {
                            response.StatusCode = 206;
                            response.Body = response.Body.Skip(start).ToArray();
                            response.Headers["Content-Range"] = "bytes " + (mode == "wrong-range" ? start + 1 : start) + "-" + (total - 1) + "/" + total;
                            if (mode == "changed-etag")
                            {
                                response.Headers["ETag"] = "\"changed\"";
                            }
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref full);
                    }
                });
                using var resumed = system.PrepareUpdateAsync(plan);
                yield return Wait(resumed);
                var prepared = Take(resumed);
                Assert.That(ranged, Is.EqualTo(mode == "no-validator" ? 0 : 1), mode);
                Assert.That(full, Is.EqualTo(mode == "206" || mode == "200" ? 0 : 1), mode);
                Assert.That(prepared.Projects[0].Target.Document.CatalogDigest, Is.EqualTo(Snapshot("a", 2).Document.CatalogDigest));
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), Is.False);
                Assert.That(Take(system.ShutdownAsync()), Is.True);
            }
        }
    }
}
