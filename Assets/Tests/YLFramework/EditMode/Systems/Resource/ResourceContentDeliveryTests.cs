using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourceContentDeliveryTests
    {
        private const string State = "YL.E04.G4.Delivery.";
        [Serializable]
        private sealed class Layout
        {
            public SceneSetup[] Scenes;
        }

        [OneTimeSetUp]
        public void PublishAndExportOwnedDeliveryVersions()
        {
            if (SessionState.GetBool(State + "owned", false))
            {
                return;
            }

            ResourceSessionTests.EnsureRestorableScenes();
            SessionState.SetString(State + "layout", JsonUtility.ToJson(new Layout { Scenes = EditorSceneManager.GetSceneManagerSetup() }));
            var root = Path.Combine(Path.GetTempPath(), "kilo", "e4g4-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)), Is.False);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(root));
            SessionState.SetString(State + "root", root);
            using var fixture = new ResourceReleaseFixture();
            fixture.Project.groups.Clear();
            fixture.Project.groups.Add(new ResourceGroup
            {
                id = "entry",
                selections = new List<ResourceSelection> {
                new() { guid = AssetDatabase.AssetPathToGUID(fixture.TextPath) } }
            });
            var noise = new byte[256 * 1024];
            new System.Random(8904).NextBytes(noise);
            var suffix = Convert.ToBase64String(noise);
            var workspaces = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var keys = new DeliveryKeys();
                for (var version = 0; version < 3; version++)
                {
                    var project = fixture.Project;
                    project.contentEncryption = version == 0 ? ResourceContentEncryption.None : ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
                    project.contentKeyId = version == 0 ? "" : "g4-key-" + version;
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.TextPath), "g4-version-" + version + "\n" + suffix);
                    AssetDatabase.ImportAsset(fixture.TextPath, ImportAssetOptions.ForceSynchronousImport);
                    EditorUtility.SetDirty(project);
                    AssetDatabase.SaveAssetIfDirty(project);
                    var workspace = Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(project, EditorUserBuildSettings.activeBuildTarget));
                    Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace)), Is.False, "Every settings/key partition is owned by this fixture only.");
                    workspaces.Add(workspace);
                    var build = ResourceBundleBuilder.BuildWithContentKey(project, EditorUserBuildSettings.activeBuildTarget, keys);
                    var release = ResourcePublisher.PublishWithContentKey(build, "app-v1", keys);
                    fixture.ReleaseRoot = release.RootPath;
                    var destination = Path.Combine(root, release.Catalog.CatalogDigest);
                    var bytes = ResourceEditorOperations.ExportRelease(release, destination);
                    CollectionAssert.AreEqual(ResourceReleaseFixture.Snapshot(release.DirectoryPath), ResourceReleaseFixture.Snapshot(destination));
                    SessionState.SetString(State + "v" + version, destination);
                    SessionState.SetString(State + "project", release.Catalog.ProjectId);
                    TestContext.WriteLine("G4 delivery export " + new JObject
                    {
                        ["Version"] = version,
                        ["Directory"] = destination,
                        ["CatalogDigest"] = release.Catalog.CatalogDigest,
                        ["ProjectId"] = release.Catalog.ProjectId,
                        ["CopiedBytes"] = bytes
                    });
                }
            }
            finally
            {
                foreach (var workspace in workspaces)
                {
                    if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace)))
                    {
                        Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(workspace), true);
                    }
                }
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scenePath = "Temp/E04G4-" + Guid.NewGuid().ToString("N") + ".unity";
            Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
            SessionState.SetString(State + "scene", scenePath);
            SessionState.SetBool(State + "owned", true);
        }

        private static ResourceLocalSnapshot Version(int version)
        {
            var path = SessionState.GetString(State + "v" + version, "");
            return ResourceLocalSnapshot.Open(path, SessionState.GetString(State + "project", ""), Path.GetFileName(path), "StandaloneWindows64", "app-v1");
        }

        private static string Root(string name) => Path.Combine(SessionState.GetString(State + "root", ""), name);
        private static ResourceProjectMount Mount(ResourceLocalSnapshot snapshot, ResourceRemoteSource remote = null) =>
            new(snapshot.Document.ProjectId, snapshot.Document.Platform, snapshot.Document.AppContentCompatibilityId, snapshot, remote: remote);

        private sealed class DeliveryKeys : IResourceContentKeyProvider, IDisposable
        {
            internal bool MissingOld;
            internal bool Missing;
            internal bool Wrong;
            internal int Calls;
            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                Calls++;
                if (Missing || keyId == "g4-key-1" && MissingOld)
                {
                    return false;
                }

                Assert.That(keyId, Is.EqualTo("g4-key-1").Or.EqualTo("g4-key-2"));
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (byte)(i + (keyId == "g4-key-1" ? 31 : 71) + (Wrong ? 1 : 0));
                }

                return true;
            }
            public void Dispose()
            {
            }
        }

        [UnityTest]
        public IEnumerator CiphertextResumeBindsTheActualPartialAndRejectsChangedOrCorruptEntities()
        {
            yield return new EnterPlayMode();
            yield return ResumeBody();
        }

        private static IEnumerator ResumeBody()
        {
            var oldPfx = Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PFX");
            var oldPassword = Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD");
            try
            {
                var pfx = SessionState.GetString(State + "tls", "");
                if (pfx.Length != 0)
                {
                    ResourceLocalSnapshot.RejectLinks(pfx);
                    Assert.That(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(pfx)), Does.StartWith(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Path.GetTempPath(), "kilo"))) + Path.DirectorySeparatorChar));
                    Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PFX", pfx);
                    Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD", "e04-local-test-only");
                }
                using var certificate = ResourceDownloadTests.Certificate();
                using var signing = certificate.GetRSAPrivateKey();
                var current = Version(0);
                var target = Version(1);
                var envelope = ResourceReleaseSigner.Create(new ResourceRelease(Path.GetDirectoryName(target.DirectoryPath), target.Document),
                    "test", 7, "isolated-test", signing, DateTimeOffset.UtcNow.AddDays(1));
                var payload = ResourceCatalogCodec.ParseObject(Convert.FromBase64String((string)JObject.Parse(Encoding.UTF8.GetString(envelope))["payload"]));
                Assert.That(payload.Properties().Count(), Is.EqualTo(13), "Current signing keeps its authenticated payload, not a legacy-client confirmation argument.");
                var selected = target.Document.Bundles.Single();
                var files = target.Document.Bundles.Cast<ResourceCatalogFile>().Concat(target.Document.SupportFiles).Select(x => x.RelativeFile)
                    .Concat(new[] { "resource-catalog.json", ".ylr-complete.json" }).ToDictionary(x => "/" + target.Document.CatalogDigest + "/" + x,
                        x => File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(target.DirectoryPath, x))), StringComparer.Ordinal);
                var selectedPath = "/" + target.Document.CatalogDigest + "/" + selected.RelativeFile;
                var cipher = files[selectedPath];
                Assert.That(Encoding.ASCII.GetString(cipher, 0, 8), Is.EqualTo("YLRENC01"));
                Assert.That(ResourceCatalogCodec.Digest(cipher), Is.EqualTo(selected.Sha256));
                foreach (var mode in new[] { "206", "ignored-range", "changed-etag", "corrupt", "http-error" })
                {
                    using var server = new ResourceTlsServer(certificate);
                    var requests = new ConcurrentQueue<string>();
                    ResourceTlsServer.Response Serve(ResourceTlsServer.Request request) => request.Path == "/release.json" ? new()
                    {
                        Body = envelope
                    } :
                        files.TryGetValue(request.Path, out var body) ? new()
                        {
                            Body = body,
                            Headers = { ["ETag"] = "\"g4-fixed-cipher\"" }
                        } : new()
                        {
                            StatusCode = 404
                        };
                    server.Responder = Serve;
                    var store = new ArtifactStore(Root("s-" + mode), new[] { Mount(current, ResourceDownloadTests.Source(server, certificate)) });
                    using var keys = new DeliveryKeys();
                    using var init = ResourceSystem.InitializeAsync(new ResourceConfiguration(store, decryption: new ResourceDecryptionOptions(keys)));
                    yield return init;
                    var system = init.TakeResult();
                    using var check = system.CheckRemoteUpdatesAsync();
                    yield return check;
                    var plan = check.TakeResult();
                    server.Responder = request =>
                    {
                        var response = Serve(request);
                        if (request.Path == selectedPath)
                        {
                            response.StallAfterBytes = 32768;
                            response.ChunkSize = 4096;
                        }
                        return response;
                    };
                    using var interrupted = system.PrepareUpdateAsync(plan);
                    var parts = Path.Combine(store.RootPath, target.Document.ProjectId, target.Document.Platform, "parts");
                    string part = null;
                    var deadline = Time.realtimeSinceStartup + 20;
                    while (!interrupted.IsDone && Time.realtimeSinceStartup < deadline)
                    {
                        if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(parts)))
                        {
                            part = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(parts), "*.json").Where(x => (string)JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(x)))["File"] == selected.RelativeFile)
                                .Select(x => Path.ChangeExtension(x, ".part")).FirstOrDefault(x => File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(x)) && new FileInfo(x).Length > 0);
                        }

                        if (part != null)
                        {
                            break;
                        }

                        yield return null;
                    }
                    Assert.That(part, Is.Not.Null, interrupted.Error?.ToString());
                    interrupted.Cancel();
                    while (system.GetDiagnosticsReport().Updates.Count != 0)
                    {
                        yield return null;
                    }

                    Assert.That(interrupted.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                    var offset = new FileInfo(part).Length;
                    Assert.That(offset, Is.GreaterThan(0).And.LessThan(cipher.LongLength));
                    CollectionAssert.AreEqual(cipher.Take((int)offset), File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(part)));
                    var metadata = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.ChangeExtension(part, ".json"))));
                    Assert.That((string)metadata["ETag"], Is.EqualTo("\"g4-fixed-cipher\""));
                    Assert.That((string)metadata["Sha256"], Is.EqualTo(selected.Sha256));
                    Assert.That((long)metadata["Length"], Is.EqualTo(cipher.LongLength));
                    Assert.That(store.GetPreparedUpdates(), Is.Empty);
                    if (mode == "206")
                    {
                        // 真实关闭并重建Editor会话/Store，只从持久partial恢复；不是8.10的Player进程重启。
                        var storeRoot = store.RootPath;
                        using (var stopping = system.ShutdownAsync())
                        {
                            yield return stopping;
                            Assert.That(stopping.TakeResult(), Is.True);
                        }
                        Assert.That(ResourceSystem.Current, Is.Null);
                        Assert.That(new FileInfo(part).Length, Is.EqualTo(offset));
                        store = new ArtifactStore(storeRoot, new[] { Mount(current, ResourceDownloadTests.Source(server, certificate)) });
                        using var restart = ResourceSystem.InitializeAsync(new ResourceConfiguration(store, decryption: new ResourceDecryptionOptions(keys)));
                        yield return restart;
                        system = restart.TakeResult();
                        using var recheck = system.CheckRemoteUpdatesAsync();
                        yield return recheck;
                        plan = recheck.TakeResult();
                        Assert.That(plan.Projects.Single().ToDigest, Is.EqualTo(target.Document.CatalogDigest));
                        Assert.That(new FileInfo(part).Length, Is.EqualTo(offset));
                    }
                    server.Responder = request =>
                    {
                        var response = Serve(request);
                        if (request.Path != selectedPath)
                        {
                            return response;
                        }

                        request.Headers.TryGetValue("Range", out var range);
                        request.Headers.TryGetValue("If-Range", out var validator);
                        requests.Enqueue((range ?? "full") + ":" + validator);
                        if (mode == "http-error")
                        {
                            response.StatusCode = 404;
                            response.Body = Array.Empty<byte>();
                        }
                        else if (range != null && mode != "ignored-range")
                        {
                            var start = int.Parse(range.Substring(6).TrimEnd('-'));
                            response.StatusCode = 206;
                            response.Body = cipher.Skip(start).ToArray();
                            response.Headers["Content-Range"] = "bytes " + start + "-" + (cipher.Length - 1) + "/" + cipher.Length;
                            if (mode == "changed-etag")
                            {
                                response.Headers["ETag"] = "\"different-entity\"";
                            }

                            if (mode == "corrupt")
                            {
                                response.Body = (byte[])response.Body.Clone();
                                response.Body[0] ^= 1;
                            }
                        }
                        return response;
                    };
                    keys.Missing = mode == "206";
                    using var resumed = system.PrepareUpdateAsync(plan);
                    yield return resumed;
                    Assert.That(requests.ToArray()[0], Is.EqualTo("bytes=" + offset + "-:\"g4-fixed-cipher\""), mode);
                    Assert.That(system.GetActiveCatalogDigest(current.Document.ProjectId), Is.EqualTo(current.Document.CatalogDigest));
                    if (mode == "corrupt" || mode == "http-error" || mode == "206")
                    {
                        Assert.That(resumed.Status, Is.EqualTo(ResourceRequestStatus.Failed), mode);
                        Assert.That(store.GetPreparedUpdates(), Is.Empty, mode);
                        if (mode == "206")
                        {
                            Assert.That(resumed.Error.Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
                            var count = server.RequestCount;
                            keys.Missing = false;
                            keys.Wrong = true;
                            using var wrong = system.PrepareUpdateAsync(plan);
                            yield return wrong;
                            Assert.That(wrong.Error.Kind, Is.EqualTo(ResourceError.ContentAuthenticationFailed));
                            Assert.That(server.RequestCount, Is.EqualTo(count), "Wrong/missing keys must not become network retries.");
                            keys.Wrong = false;
                        }
                        if (mode == "corrupt")
                        {
                            Assert.That(new FileInfo(part).Length, Is.Zero, "Corrupt ciphertext cannot remain a complete reusable part.");
                        }

                        server.Responder = Serve;
                    }
                    using var preparing = system.PrepareUpdateAsync(plan);
                    yield return preparing;
                    var prepared = preparing.TakeResult();
                    if (mode == "206" || mode == "ignored-range")
                    {
                        Assert.That(requests.Count, Is.EqualTo(1), mode);
                    }

                    if (mode == "changed-etag")
                    {
                        Assert.That(requests.Count, Is.EqualTo(2));
                        Assert.That(requests.ToArray()[1], Is.EqualTo("full:"), "The changed entity restarts without Range or If-Range, never appends to the old prefix.");
                    }
                    CollectionAssert.AreEqual(cipher, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(prepared.Projects.Single().Target.DirectoryPath, selected.RelativeFile))));
                    using var activate = system.ActivatePreparedUpdateAsync(prepared);
                    yield return activate;
                    Assert.That(activate.TakeResult(), Is.True, activate.Error?.ToString());
                    using (var text = system.Acquire<TextAsset>(new ResourceAddress(target.Document.ProjectId, "entry.txt")))
                    {
                        Assert.That(text.Asset.text, Does.StartWith("g4-version-1\n"));
                    }

                    TestContext.WriteLine("G4 ciphertext " + mode + " offset=" + offset + " total=" + cipher.Length + " sha=" + selected.Sha256 + " requests=" + string.Join(",", requests));
                    using var stop = system.ShutdownAsync();
                    yield return stop;
                    Assert.That(stop.TakeResult(), Is.True);
                    Assert.That(server.Errors, Is.Empty);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PFX", oldPfx);
                Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD", oldPassword);
            }
        }

        [UnityTest]
        public IEnumerator RotationRollbackAndFailedCommitKeepTheOriginalTransactionIdentity()
        {
            yield return new EnterPlayMode();
            yield return RotationBody();
        }

        private static IEnumerator RotationBody()
        {
            var plain = Version(0);
            var first = Version(1);
            var rotated = Version(2);
            var store = new ArtifactStore(Root("rotation"), new[] { Mount(plain) });
            using var keys = new DeliveryKeys();
            using var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(store, decryption: new ResourceDecryptionOptions(keys)));
            yield return initialize;
            var system = initialize.TakeResult();
            foreach (var target in new[] { first, rotated })
            {
                using var check = system.CheckUpdatesAsync(new[] { target });
                yield return check;
                var plan = check.TakeResult();
                Assert.That(plan.Projects.Single().FromDigest, Is.EqualTo(system.GetActiveCatalogDigest(target.Document.ProjectId)));
                Assert.That(plan.Projects.Single().ToDigest, Is.EqualTo(target.Document.CatalogDigest));
                using var prepare = system.PrepareUpdateAsync(plan);
                yield return prepare;
                using var activate = system.ActivatePreparedUpdateAsync(prepare.TakeResult());
                yield return activate;
                Assert.That(activate.TakeResult(), Is.True, activate.Error?.ToString());
            }
            var project = rotated.Document.ProjectId;
            var address = new ResourceAddress(project, "entry.txt");
            keys.MissingOld = true;
            var before = ResourceReleaseFixture.Snapshot(store.RootPath);
            using (var missingOld = system.RollbackAsync())
            {
                yield return missingOld;
                Assert.That(missingOld.Error.Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            }
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(store.RootPath));
            Assert.That(system.GetActiveCatalogDigest(project), Is.EqualTo(rotated.Document.CatalogDigest));
            keys.MissingOld = false;
            using (var held = system.Acquire<TextAsset>(address))
            using (var busy = system.RollbackAsync())
            {
                yield return busy;
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
                Assert.That(held.Asset.text, Does.StartWith("g4-version-2\n"));
            }
            using (var canceled = system.RollbackAsync())
            {
                canceled.Cancel();
                while (system.GetContentDiagnosticsSnapshot().ReservedBufferBytes != 0 || system.GetDiagnosticsReport().Updates.Count != 0)
                {
                    yield return null;
                }

                Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            }
            var checkpoints = new List<string>();
            store.Checkpoint = phase =>
            {
                checkpoints.Add(phase);
                if (phase == "JournalVerified")
                {
                    throw new IOException("Owned G4 failure before the transaction decision.");
                }
            };
            try
            {
                using var failed = system.RollbackAsync();
                yield return failed;
                Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(failed.Error.Kind, Is.EqualTo(ResourceError.UpdateFailed));
                Assert.That(checkpoints, Does.Contain("JournalVerified"));
                Assert.That(checkpoints, Does.Not.Contain("CommitDecided"));
                Assert.That(system.GetActiveCatalogDigest(project), Is.EqualTo(rotated.Document.CatalogDigest));
                Assert.That(store.Mount().Single().Document.CatalogDigest, Is.EqualTo(rotated.Document.CatalogDigest));
                var projectRoot = Path.Combine(store.RootPath, project, rotated.Document.Platform);
                Assert.That((string)JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(projectRoot, "active.json"))))["Projects"].Single()["CatalogDigest"], Is.EqualTo(rotated.Document.CatalogDigest));
                Assert.That((string)JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(projectRoot, "last-known-good.json"))))["Projects"].Single()["CatalogDigest"], Is.EqualTo(first.Document.CatalogDigest));
                Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(store.RootPath), "commit-journal.json", SearchOption.AllDirectories), Is.Empty);
                Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(store.RootPath), "*.next", SearchOption.AllDirectories), Is.Empty);
                using var stillCurrent = system.Acquire<TextAsset>(address);
                Assert.That(stillCurrent.Asset.text, Does.StartWith("g4-version-2\n"));
                TestContext.WriteLine("G4 rollback failure from=" + rotated.Document.CatalogDigest + " to=" + first.Document.CatalogDigest +
                    " phases=" + string.Join(",", checkpoints) + " status=" + failed.Status + " cause=" + failed.Error);
            }
            finally
            {
                store.Checkpoint = null;
            }
            using (var rollback = system.RollbackAsync())
            {
                yield return rollback;
                Assert.That(rollback.TakeResult(), Is.True, rollback.Error?.ToString());
            }
            Assert.That(system.GetActiveCatalogDigest(project), Is.EqualTo(first.Document.CatalogDigest));
            using (var old = system.Acquire<TextAsset>(address))
            {
                Assert.That(old.Asset.text, Does.StartWith("g4-version-1\n"));
            }
            // LKG按原两槽合同推进；先显式将明文装入LKG，再从密文回滚，不能猜另一个历史版本。
            using (var check = system.CheckUpdatesAsync(new[] { plain }))
            {
                yield return check;
                using var prepare = system.PrepareUpdateAsync(check.TakeResult());
                yield return prepare;
                using var activate = system.ActivatePreparedUpdateAsync(prepare.TakeResult());
                yield return activate;
                Assert.That(activate.TakeResult(), Is.True);
            }
            using (var check = system.CheckUpdatesAsync(new[] { rotated }))
            {
                yield return check;
                using var prepare = system.PrepareUpdateAsync(check.TakeResult());
                yield return prepare;
                using var activate = system.ActivatePreparedUpdateAsync(prepare.TakeResult());
                yield return activate;
                Assert.That(activate.TakeResult(), Is.True);
            }
            keys.Missing = true;
            using (var rollback = system.RollbackAsync())
            {
                yield return rollback;
                Assert.That(rollback.TakeResult(), Is.True, rollback.Error?.ToString());
            }
            using (var text = system.Acquire<TextAsset>(address))
            {
                Assert.That(text.Asset.text, Does.StartWith("g4-version-0\n"));
            }

            Assert.That(system.GetContentDiagnosticsSnapshot().ReservedBufferBytes, Is.Zero);
            TestContext.WriteLine("G4 explicit ciphertext-to-None LKG=" + plain.Document.CatalogDigest + "; new smoke release, not historical evidence.");
        }

        [UnityTest]
        public IEnumerator PreE04OwnedCatalogIsConsumedWithoutRebuildingOrRewritingItsBytes()
        {
            yield return new EnterPlayMode();
            yield return HistoricalBody();
        }

        private static IEnumerator HistoricalBody()
        {
            var directory = SessionState.GetString(State + "legacy", "");
            Assert.That(directory, Is.Not.Empty, "Select an existing pre-E04 owned v1 artifact explicitly; never generate a replacement.");
            ResourceLocalSnapshot.RejectLinks(directory);
            var before = ResourceReleaseFixture.Snapshot(directory);
            var catalog = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(directory, "resource-catalog.json"))));
            var complete = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(directory, ".ylr-complete.json"))));
            Assert.That((string)complete["ProjectGuid"], Is.EqualTo("64b81f4b7f3ea07438164f8dd2f9d087"));
            var snapshot = ResourceLocalSnapshot.OpenOwned(directory, (string)catalog["ProjectId"], Path.GetFileName(directory), "StandaloneWindows64", "fixture-v1");
            Assert.That(snapshot.Document.ProjectId, Is.EqualTo("yl-release-ef5f7112de994b238bc4ff8239b7e258"));
            using var keys = new DeliveryKeys { Missing = true };
            var startup = new ResourceStartup(new ResourceConfiguration(new[] { snapshot.CreateCatalog() }, decryption: new ResourceDecryptionOptions(keys)));
            using var start = startup.StartAsync();
            yield return start;
            start.TakeResult();
            using (var text = startup.Resources.Acquire<TextAsset>(new ResourceAddress(snapshot.Document.ProjectId, "entry.txt")))
            {
                Assert.That(text.Asset.text, Is.EqualTo("first release content"));
            }

            using (var bundle = startup.Resources.AcquireBundle(snapshot.Document.ProjectId, snapshot.Document.Assets.Single(x => x.Address == "entry.txt").BundleId))
            {
                var input = (ResourceBundleInput)bundle.Record.Input;
                Assert.That(input.IsEncrypted, Is.False);
                Assert.That(input.Stream, Is.TypeOf<FileStream>());
            }
            Assert.That(keys.Calls, Is.Zero);
            using var stop = startup.StopAsync();
            yield return stop;
            Assert.That(stop.TakeResult(), Is.True);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(directory));
            TestContext.WriteLine("G4 historical pre-E04 v1 direct consumption " + directory + " digest=" + snapshot.Document.CatalogDigest + "; no rebuild/rewrite/key.");
        }

        [UnityTest]
        public IEnumerator EncryptedBuiltInDeployRecoverAndKeylessQueriesUseUnchangedDeliveryBytes()
        {
            var source = Version(1);
            var before = ResourceReleaseFixture.Snapshot(source.DirectoryPath);
            SessionState.SetBool(State + "builtInRootExisted", Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)));
            SessionState.SetString(State + "builtInProject", Path.Combine(ResourceBuiltInDeployer.ResourcesRoot, source.Document.ProjectId));
            var plan = ResourceBuiltInDeployer.Preflight(source);
            SessionState.SetString(State + "builtInWorkspace", plan.WorkspacePath);
            SessionState.SetString(State + "builtInGuid", (string)JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(source.DirectoryPath, ".ylr-complete.json"))))["ProjectGuid"]);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.Throws<IOException>(() => ResourceBuiltInDeployer.Execute(plan, default,
                phase =>
                {
                    if (phase == "BeforeImport")
                    {
                        throw new IOException("Owned G4 post-install interruption.");
                    }
                }));
            var deployed = ResourceBuiltInDeployer.Recover(source.Document.ProjectId, source.Document.Platform, SessionState.GetString(State + "builtInGuid", ""));
            Assert.That(deployed, Is.Not.Null);
            Assert.That(ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(source)).IsRepeat, Is.True);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(source.DirectoryPath));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(deployed.Snapshot.DirectoryPath));
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(deployed.Snapshot.DirectoryPath), "*.meta", SearchOption.AllDirectories), Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(deployed.Snapshot.DirectoryPath + ".meta")), Is.False);
            Assert.That(AssetDatabase.AssetPathToGUID("Assets" + deployed.Snapshot.DirectoryPath.Substring(Application.dataPath.Length).Replace('\\', '/')), Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(deployed.DirectoryPath + ".meta")), Is.True);
            TestContext.WriteLine("G4 key-free C02 payload=" + deployed.Snapshot.DirectoryPath + " bytes=" + plan.PayloadBytes + "; strict import/recover/repeat.");
            yield return new EnterPlayMode();
            yield return BuiltInBody();
        }

        private static IEnumerator BuiltInBody()
        {
            var source = Version(1);
            var builtIn = ResourceLocalSnapshot.OpenBuiltIn(source.Document.ProjectId, source.Document.CatalogDigest, source.Document.Platform, "app-v1");
            Assert.That(builtIn.DirectoryPath, Does.StartWith(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.streamingAssetsPath))));
            Assert.That(builtIn.DirectoryPath, Does.EndWith("payload~"));
            var store = new ArtifactStore(Root("built-in-store"), new[] { Mount(builtIn) });
            var before = ResourceReleaseFixture.Snapshot(store.RootPath);
            ArtifactStoreReport missingReport = null;
            yield return store.QueryAsync(x => missingReport = x);
            Assert.That(missingReport.Status, Is.EqualTo(ArtifactQueryStatus.Missing));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(store.RootPath)), Is.False, "E05 cannot create a root or a lock.");
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(store.RootPath));
            using var keys = new DeliveryKeys();
            var configuration = new ResourceConfiguration(store, decryption: new ResourceDecryptionOptions(keys));
            var startup = new ResourceStartup(configuration);
            using var start = startup.StartAsync();
            yield return start;
            start.TakeResult();
            using (var text = startup.Resources.Acquire<TextAsset>(new ResourceAddress(source.Document.ProjectId, "entry.txt")))
            {
                Assert.That(text.Asset.text, Does.StartWith("g4-version-1\n"));
            }

            using var check = startup.Resources.CheckUpdatesAsync(new[] { Version(2) });
            yield return check;
            using var prepare = startup.Resources.PrepareUpdateAsync(check.TakeResult());
            yield return prepare;
            prepare.TakeResult();
            var calls = keys.Calls;
            keys.Missing = true;
            var files = ResourceReleaseFixture.Snapshot(store.RootPath);
            ArtifactStoreReport report = null;
            yield return configuration.QueryArtifactsAsync(x => report = x);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Ready));
            Assert.That(keys.Calls, Is.EqualTo(calls), "E05 does not call application keys, decrypt or recover.");
            CollectionAssert.AreEqual(files, ResourceReleaseFixture.Snapshot(store.RootPath));
            foreach (var version in report.Entries.Where(x => x.Kind == ArtifactKind.Version))
            {
                Assert.That(version.ObservedBytes, Is.EqualTo(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(version.Path)).Sum(x => new FileInfo(x).Length)));
                Assert.That(version.IsCleanupCandidate, Is.False);
                Assert.That(version.Protection & (ArtifactProtection.PreparedFrom | ArtifactProtection.PreparedTo), Is.Not.EqualTo(ArtifactProtection.None));
            }
            Assert.That(report.Entries.Count(x => x.Kind == ArtifactKind.Version), Is.EqualTo(2));
            TestContext.WriteLine("G4 key-free E05 ciphertext logicalBytes=" + report.ObservedBytes + " prepared=" + report.PreparedUpdates.Count + " status=" + report.Status);
            using var stop = startup.StopAsync();
            yield return stop;
            Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTearDown]
        public IEnumerator ReleaseTheOwnedSession()
        {
            if (!Application.isPlaying)
            {
                yield break;
            }

            var system = ResourceSystem.Current;
            if (system != null && system.IsAvailable)
            {
                using var shutdown = system.ShutdownAsync();
                yield return shutdown;
                Assert.That(shutdown.TakeResult(), Is.True, shutdown.Error?.ToString());
            }
            if (system != null)
            {
                var content = system.GetContentDiagnosticsSnapshot();
                Assert.That(content.LiveInputs, Is.Zero);
                Assert.That(content.WorkCount, Is.Zero);
                Assert.That(content.AllocatedBufferBytes, Is.Zero);
                Assert.That(content.ReservedBufferBytes, Is.Zero);
                Assert.That(content.RetainedCleanupOwners, Is.Zero);
            }
            Assert.That(CngSegmentAead.ActiveAlgorithms, Is.Zero);
            Assert.That(CngSegmentAead.ActiveKeys, Is.Zero);
            Assert.That(CngSegmentAead.ActiveKeyObjectBytes, Is.Zero);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            yield return new ExitPlayMode();
        }

        [OneTimeTearDown]
        public void RestoreTheSceneAndKeepDeliveryArtifacts()
        {
            var layout = JsonUtility.FromJson<Layout>(SessionState.GetString(State + "layout", "{}"));
            if (layout?.Scenes != null)
            {
                EditorSceneManager.RestoreSceneManagerSetup(layout.Scenes);
            }

            var scene = SessionState.GetString(State + "scene", "");
            if (scene.Length != 0 && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(scene)))
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(scene));
            }

            var builtInProject = SessionState.GetString(State + "builtInProject", "");
            var builtInGuid = SessionState.GetString(State + "builtInGuid", "");
            if (builtInProject.Length != 0 && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(builtInProject)))
            {
                Assert.That((string)ResourceBuiltInLayout.ReadObject(Path.Combine(builtInProject, "ylr-owner.json~"))["ProjectGuid"], Is.EqualTo(builtInGuid));
                ResourceLocalSnapshot.RejectLinks(builtInProject);
                Assert.That(AssetDatabase.DeleteAsset("Assets" + builtInProject.Substring(Application.dataPath.Length).Replace('\\', '/')), Is.True);
            }
            var builtInWorkspace = SessionState.GetString(State + "builtInWorkspace", "");
            if (builtInWorkspace.Length != 0 && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(builtInWorkspace)))
            {
                Assert.That((string)ResourceBuiltInLayout.ReadObject(Path.Combine(builtInWorkspace, "ylr-owner.json~"))["ProjectGuid"], Is.EqualTo(builtInGuid));
                ResourceLocalSnapshot.RejectLinks(builtInWorkspace);
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(builtInWorkspace), true);
            }
            if (builtInProject.Length != 0 && !SessionState.GetBool(State + "builtInRootExisted", true) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)))
            {
                CollectionAssert.AreEquivalent(new[] { "ylr-owner.json~" }, Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)).Select(Path.GetFileName));
                Assert.That(AssetDatabase.DeleteAsset("Assets/StreamingAssets/YLFrameworkResources"), Is.True);
            }
            SessionState.SetString(State + "lastRoot", SessionState.GetString(State + "root", ""));
            foreach (var key in new[] { "layout", "root", "scene", "v0", "v1", "v2", "project", "tls", "builtInProject", "builtInGuid", "builtInWorkspace" })
            {
                SessionState.EraseString(State + key);
            }

            SessionState.EraseBool(State + "builtInRootExisted");
            SessionState.EraseBool(State + "owned");
        }
    }
}
