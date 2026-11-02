using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceUpdateTests
    {
        private const string Key = "YL.Resource87.";
        [Serializable]
        private sealed class SavedScenes
        {
            public SavedScene[] Items;
        }
        [Serializable]
        private sealed class SavedScene
        {
            public string Path;
            public bool Loaded;
            public bool Active;
        }

        [OneTimeSetUp]
        public void BuildVersions()
        {
            if (SessionState.GetBool(Key + "owned", false))
            {
                return;
            }

            YLFramework.Tests.Resource.ResourceSessionTests.EnsureRestorableScenes();
            var setup = EditorSceneManager.GetSceneManagerSetup();
            SessionState.SetString(Key + "scenes", JsonUtility.ToJson(new SavedScenes
            {
                Items = setup.Where(x => !string.IsNullOrEmpty(x.path))
                .Select(x => new SavedScene { Path = x.path, Loaded = x.isLoaded, Active = x.isActive }).ToArray()
            }));
            foreach (var project in new[] { "a", "b" })
            {
                var fixture = new ResourceReleaseFixture();
                SessionState.SetString(Key + project + "folder", fixture.Folder);
                SessionState.SetString(Key + project + "guid", AssetDatabase.AssetPathToGUID(fixture.Folder));
                for (var version = 1; version <= (project == "a" ? 3 : 2); version++)
                {
                    if (version != 1)
                    {
                        File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.TextPath), "release87-" + project + "-v" + version);
                        AssetDatabase.ImportAsset(fixture.TextPath, ImportAssetOptions.ForceSynchronousImport);
                    }
                    var build = fixture.Build();
                    var release = ResourcePublisher.Publish(build, "fixture-v1");
                    SessionState.SetString(Key + project + "workspace", Path.GetDirectoryName(build.WorkspacePath));
                    SessionState.SetString(Key + project + "root", release.RootPath);
                    SessionState.SetString(Key + project + "project", release.Catalog.ProjectId);
                    SessionState.SetString(Key + project + version, release.DirectoryPath);
                }
                fixture.AssertProtected();
            }
            SessionState.SetString(Key + "protected", ProtectedDigest());
            var neutral = "Temp/YL87-" + Guid.NewGuid().ToString("N") + ".unity";
            SessionState.SetString(Key + "neutral", neutral);
            Assert.That(EditorSceneManager.SaveScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single), neutral), Is.True);
            SessionState.SetString(Key + "stores", "[]");
            SessionState.SetBool(Key + "owned", true);
        }

        private static string ProtectedDigest() => ResourceCatalogCodec.Digest(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
            new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "AssetBundles/MainModule", "Packages", "ProjectSettings" }
                .SelectMany(x => ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(x))).Select(f => x + f)))));

        private static ResourceLocalSnapshot Snapshot(string project = "a", int version = 1)
        {
            var path = SessionState.GetString(Key + project + version, "");
            return ResourceLocalSnapshot.Open(path, SessionState.GetString(Key + project + "project", ""), Path.GetFileName(path), "StandaloneWindows64", "fixture-v1");
        }

        private static ArtifactStore NewStore(bool multi = false)
        {
            var path = Path.Combine(Path.GetTempPath(), "kilo", "r87-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var paths = JArray.Parse(SessionState.GetString(Key + "stores", "[]"));
            paths.Add(path);
            SessionState.SetString(Key + "stores", paths.ToString());
            return Store(path, multi);
        }

        private static ArtifactStore Store(string path, bool multi = false) => new(path, (multi ? new[] { "a", "b" } : new[] { "a" }).Select(x =>
            new ResourceProjectMount(Snapshot(x).Document.ProjectId, "StandaloneWindows64", "fixture-v1", Snapshot(x))));
        private static string ProjectPath(ArtifactStore store, string name = "active.json", string project = "a") =>
            Path.Combine(store.RootPath, Snapshot(project).Document.ProjectId, "StandaloneWindows64", name);
        private static string VersionPath(ArtifactStore store, int version, string project = "a") => Path.Combine(ProjectPath(store, "versions", project), Snapshot(project, version).Document.CatalogDigest);
        private static PreparedUpdate Prepare(ArtifactStore store, int version = 2, bool multi = false) => store.Prepare(store.Check(store.Mount(),
            multi ? new[] { Snapshot("a", version), Snapshot("b", Math.Min(version, 2)) } : new[] { Snapshot("a", version) }), CancellationToken.None);

        [OneTimeTearDown]
        public void CleanupVersions()
        {
            if (!SessionState.GetBool(Key + "owned", false))
            {
                return;
            }

            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            Assert.That(ProtectedDigest(), Is.EqualTo(SessionState.GetString(Key + "protected", "")));
            if (SessionState.GetBool(Key + "retain", false))
            {
                return;
            }

            foreach (var path in JArray.Parse(SessionState.GetString(Key + "stores", "[]")).Values<string>())
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path), true);
                }
            }

            foreach (var project in new[] { "a", "b" })
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + project + "root", "")), true);
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + project + "workspace", "")), true);
                var folder = SessionState.GetString(Key + project + "folder", "");
                Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(SessionState.GetString(Key + project + "guid", "")));
                AssetDatabase.DeleteAsset(folder);
                foreach (var suffix in new[] { "folder", "guid", "workspace", "root", "project", "1", "2", "3" })
                {
                    SessionState.EraseString(Key + project + suffix);
                }
            }
            var scenes = JsonUtility.FromJson<SavedScenes>(SessionState.GetString(Key + "scenes", ""));
            EditorSceneManager.RestoreSceneManagerSetup(scenes.Items.Select(x => new SceneSetup { path = x.Path, isLoaded = x.Loaded, isActive = x.Active }).ToArray());
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + "neutral", "")));
            foreach (var suffix in new[] { "scenes", "protected", "neutral", "stores", "restart" })
            {
                SessionState.EraseString(Key + suffix);
            }

            SessionState.EraseBool(Key + "owned");
        }

        [UnityTearDown]
        public IEnumerator ExitSession()
        {
            if (!EditorApplication.isPlaying)
            {
                yield break;
            }

            typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (new[] { "a", "b" }.Any(x => scene.path.StartsWith(SessionState.GetString(Key + x + "folder", "!"), StringComparison.Ordinal)))
                {
                    yield return SceneManager.UnloadSceneAsync(scene);
                }
            }
            var deadline = Time.realtimeSinceStartup + 10;
            while (AssetBundle.GetAllLoadedAssetBundles().Any() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            yield return new ExitPlayMode();
        }

        [Test]
        public void LocalVersionsHaveAnOwnedStoreAndExplicitCheckPrepareActivateRollbackContract()
        {
            Assert.That(typeof(ResourceSystem).Assembly.GetType("YLFramework.Resource.ArtifactStore"), Is.Not.Null,
                "8.7 requires an owned persistent store, not a mutable release directory or a guessed latest folder.");
            foreach (var name in new[] { "CheckUpdatesAsync", "PrepareUpdateAsync", "ActivatePreparedUpdateAsync", "RollbackAsync" })
            {
                Assert.That(typeof(ResourceSystem).GetMethods(), Has.Some.Matches<System.Reflection.MethodInfo>(x => x.Name == name), name);
            }
        }

        [Test]
        public void EditorCanSelectAnExactHistoricalReleaseWithoutChangingLastPublishedIndex()
        {
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(SessionState.GetString(Key + "afolder", "") + "/Project.asset");
            var root = SessionState.GetString(Key + "aroot", "");
            var before = ResourceReleaseFixture.Snapshot(root);
            var selected = ResourcePublisher.ReadVersion(project, EditorUserBuildSettings.activeBuildTarget, "fixture-v1", Snapshot().Document.CatalogDigest);
            Assert.That(selected.Catalog.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
            Assert.That(ResourcePublisher.ReadCurrent(project, EditorUserBuildSettings.activeBuildTarget, "fixture-v1").Catalog.CatalogDigest,
                Is.EqualTo(Snapshot("a", 3).Document.CatalogDigest));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(root));
        }

        [Test]
        public void CheckIsPureAndRepeatedPrepareRevalidatesWithoutRewriting()
        {
            var store = NewStore();
            var current = store.Mount();
            var check = store.Check(current, new[] { Snapshot("a", 2) });
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(store.RootPath)), Is.False, "Mount and Check cannot claim/create a root.");
            Assert.That(check.RequiredBytes, Is.GreaterThan(0));
            Assert.That(check.MissingBytes, Is.GreaterThan(0));
            Assert.That(check.ReusableBytes, Is.GreaterThan(0));
            Assert.Throws<OperationCanceledException>(() => store.Prepare(check, new CancellationToken(true)));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(store.RootPath)), Is.False);
            var prepared = store.Prepare(check, CancellationToken.None);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), Is.False);
            var before = ResourceReleaseFixture.Snapshot(store.RootPath);
            var modifiedTimes = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(store.RootPath), "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.GetLastWriteTimeUtc);
            store.Check(current, new[] { Snapshot("a", 3) });
            var checkpoints = new List<string>();
            store.Checkpoint = checkpoints.Add;
            var repeated = store.Prepare(check, CancellationToken.None);
            var expectedCheckpoints = new List<string> { "ReceiptShaStarted", "ReceiptShaFinished" };
            var contentFiles = check.Projects.Sum(project => project.Target.Document.Bundles.Count +
                project.Target.Document.SupportFiles.Count(file => file.RelativeFile == project.Target.Document.ManifestFile));
            Assert.That(contentFiles, Is.GreaterThan(0));
            for (var i = 0; i < contentFiles; i++)
            {
                expectedCheckpoints.Add("ContentShaStarted");
                expectedCheckpoints.Add("ContentShaFinished");
            }
            CollectionAssert.AreEqual(expectedCheckpoints, checkpoints,
                "Repeat Prepare must fully validate again, without installation or metadata-write checkpoints.");
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(store.RootPath));
            foreach (var file in modifiedTimes)
            {
                Assert.That(File.GetLastWriteTimeUtc(file.Key), Is.EqualTo(file.Value), file.Key);
            }
            Assert.That(repeated.Projects[0].ToDigest, Is.EqualTo(prepared.Projects[0].ToDigest));
            var restarted = Store(store.RootPath);
            var restored = restarted.GetPreparedUpdates().Single();
            Assert.That(restored.Projects[0].FromDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
            Assert.That(restored.Projects[0].ToDigest, Is.EqualTo(Snapshot("a", 2).Document.CatalogDigest));
            Assert.That(restarted.Mount().Single().Document.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
        }

        [Test]
        public void FailedOrCanceledPreparationNeverChangesActiveOrExistingCompleteVersions()
        {
            var store = NewStore();
            var prepared = Prepare(store);
            store.Commit(prepared);
            var active = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store)));
            var old = ResourceReleaseFixture.Snapshot(VersionPath(store, 1));
            var plan = store.Check(store.Mount(), new[] { Snapshot("a", 3) });
            using var cancellation = new CancellationTokenSource();
            store.Checkpoint = stage =>
            {
                if (stage == "FileCopied")
                {
                    cancellation.Cancel();
                }
            };
            Assert.Throws<OperationCanceledException>(() => store.Prepare(plan, cancellation.Token));
            store.Checkpoint = stage =>
            {
                if (stage == "CompleteWritten")
                {
                    throw new IOException("injected prepare failure");
                }
            };
            Assert.Throws<IOException>(() => store.Prepare(plan, CancellationToken.None));
            CollectionAssert.AreEqual(active, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))));
            CollectionAssert.AreEqual(old, ResourceReleaseFixture.Snapshot(VersionPath(store, 1)));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(VersionPath(store, 3))), Is.False);
            Assert.That(Store(store.RootPath).Mount().Single().Document.CatalogDigest, Is.EqualTo(Snapshot("a", 2).Document.CatalogDigest));
        }

        [Test]
        public void JournalInterruptionsRecoverOneCompleteClosureAtEveryMaterialBoundary()
        {
            foreach (var point in new[] { "BeforeReplace:commit-journal.json", "JournalVerified", "BeforeReplace:last-known-good.json", "LastGoodWritten", "BeforeReplace:active.json", "ActiveWritten", "BeforeCommitDecision", "CommitDecided" })
            {
                var store = NewStore(true);
                var prepared = Prepare(store, 2, true);
                var hit = false;
                store.Checkpoint = stage =>
                {
                    if (!hit && stage.StartsWith(point, StringComparison.Ordinal))
                    {
                        hit = true;
                        throw new IOException("interrupted at " + stage);
                    }
                };
                Assert.Throws<IOException>(() => store.Commit(prepared), point);
                Assert.That(hit, Is.True, point);
                var restarted = Store(store.RootPath, true);
                var selected = restarted.Mount();
                var expected = point == "CommitDecided" ? 2 : 1;
                foreach (var project in new[] { "a", "b" })
                {
                    Assert.That(selected.Single(x => x.Document.ProjectId == Snapshot(project).Document.ProjectId).Document.CatalogDigest,
                        Is.EqualTo(Snapshot(project, expected).Document.CatalogDigest), point + ":" + project);
                }

                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store, "commit-journal.json", string.CompareOrdinal(Snapshot().Document.ProjectId, Snapshot("b").Document.ProjectId) < 0 ? "a" : "b"))), Is.False);
                Assert.That(restarted.Mount().Select(x => x.Document.CatalogDigest), Is.EqualTo(selected.Select(x => x.Document.CatalogDigest)));
            }
        }

        [Test]
        public void PostDecisionCorruptionAfterReceiptReleaseRecoversOneCompleteClosure()
        {
            var store = NewStore(true);
            var prepared = Prepare(store, 2, true);
            var decided = false;
            store.Checkpoint = stage =>
            {
                if (stage == "CommitDecided")
                {
                    decided = true;
                    throw new IOException("interrupted after the durable decision, before recovery");
                }
            };
            Assert.Throws<IOException>(() => store.Commit(prepared));
            Assert.That(decided, Is.True);
            var leader = string.CompareOrdinal(Snapshot().Document.ProjectId, Snapshot("b").Document.ProjectId) < 0 ? "a" : "b";
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store, "commit-journal.json", leader))), Is.True);

            var target = Path.Combine(VersionPath(store, 2), Snapshot("a", 2).Document.Bundles[0].RelativeFile);
            Assert.That(new FileInfo(target).Length, Is.GreaterThan(1));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(target), new byte[] { 1 });
            CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(target)));

            var selected = Store(store.RootPath, true).Mount();
            Assert.That(selected.Count, Is.EqualTo(2));
            foreach (var project in new[] { "a", "b" })
            {
                Assert.That(selected.Single(snapshot => snapshot.Document.ProjectId == Snapshot(project).Document.ProjectId).Document.CatalogDigest,
                    Is.EqualTo(Snapshot(project).Document.CatalogDigest), "One invalid decided target must recover the whole From closure.");
            }
        }

        [Test]
        public void CorruptActiveFallsBackToLastGoodAndNeverGuessesLatestDirectory()
        {
            var store = NewStore();
            store.Commit(Prepare(store));
            var target = Path.Combine(VersionPath(store, 2), Snapshot("a", 2).Document.Bundles[0].RelativeFile);
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(target), new byte[] { 1, 2, 3 });
            Assert.That(Store(store.RootPath).Mount().Single().Document.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store)), "broken");
            Assert.That(Store(store.RootPath).Mount().Single().Document.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
            Assert.Throws<InvalidDataException>(() => new ArtifactStore(store.RootPath, new[] {
                new ResourceProjectMount(Snapshot().Document.ProjectId, "StandaloneWindows64", "wrong-app") }).Mount());
            var unknown = NewStore();
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(unknown.RootPath));
            Assert.Throws<InvalidDataException>(() => unknown.Mount());
            Assert.That(Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(unknown.RootPath)), Is.Empty);
        }

        [Test]
        public void CleanupProtectsActiveGoodPreparedAndUnknownContentAndReportsLockedFile()
        {
            var store = NewStore();
            var first = Prepare(store);
            store.Commit(first);
            var third = Prepare(store, 3);
            var unknown = Path.Combine(ProjectPath(store, "versions"), "foreign");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(unknown));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(unknown, "mine")), "must survive");
            Assert.That(store.Cleanup(store.Mount()).DeletedPaths, Is.Empty);
            store.Commit(third);
            var obsolete = Path.Combine(VersionPath(store, 1), Snapshot().Document.Bundles[0].RelativeFile);
            using (var locked = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(obsolete), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = store.Cleanup(store.Mount());
                Assert.That(failed.HasSucceeded, Is.False);
                Assert.That(failed.Errors, Is.Not.Empty);
            }
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(unknown, "mine"))), Is.EqualTo("must survive"));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(VersionPath(store, 2))), Is.True);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(VersionPath(store, 3))), Is.True);
        }
    }
}
