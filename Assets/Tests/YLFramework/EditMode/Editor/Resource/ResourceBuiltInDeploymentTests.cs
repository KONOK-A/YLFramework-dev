using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceBuiltInDeploymentTests
    {
        [Test]
        public void ExplicitSnapshotHasAReadOnlyBuiltInDeploymentPreflight()
        {
            using var source = new BuiltInSource();
            var before = ResourceReleaseFixture.Snapshot(Application.streamingAssetsPath);
            var plan = ResourceBuiltInDeployer.Preflight(source.Snapshot);
            Assert.That(plan.Source.Document.CatalogDigest, Is.EqualTo(source.Snapshot.Document.CatalogDigest));
            Assert.That(plan.DirectoryPath, Does.StartWith(ResourceBuiltInDeployer.ResourcesRoot + Path.DirectorySeparatorChar));
            Assert.That(plan.Files, Does.Contain("resource-catalog.json"));
            Assert.That(plan.Files, Does.Contain(".ylr-complete.json"));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(plan.WorkspacePath)), Is.False);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Application.streamingAssetsPath));
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        [Test]
        public void TwoVersionsAndRepeatSurviveRealUnityImportWithoutChangingSourceOrOldBytes()
        {
            using var source = new BuiltInSource();
            var first = source.Snapshot;
            var sourceBefore = ResourceReleaseFixture.Snapshot(first.DirectoryPath);
            var firstPlan = ResourceBuiltInDeployer.Preflight(first);
            var installed = ResourceBuiltInDeployer.Deploy(firstPlan);
            Assert.That(installed.IsRepeat, Is.False);
            Assert.That(installed.Snapshot.DirectoryPath, Is.EqualTo(Path.Combine(firstPlan.DirectoryPath, "payload~")));
            Assert.That(installed.CopiedBytes, Is.EqualTo(firstPlan.PayloadBytes));
            Assert.That(installed.RelativeDirectory, Is.EqualTo("YLFrameworkResources/" + source.ProjectId + "/StandaloneWindows64/" + first.Document.CatalogDigest));
            foreach (var directory in new[] { ResourceBuiltInDeployer.ResourcesRoot, source.DeployedProject,
                Path.GetDirectoryName(firstPlan.DirectoryPath), firstPlan.DirectoryPath })
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory + ".meta")), Is.True, "Unity must really import the deployment shell: " + directory);
            }

            var before = ResourceReleaseFixture.Snapshot(source.DeployedProject);
            var oldVersionBefore = ResourceReleaseFixture.Snapshot(installed.DirectoryPath);
            var times = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(source.DeployedProject), "*", SearchOption.AllDirectories).ToDictionary(x => x, File.GetLastWriteTimeUtc);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var repeated = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(first));
            Assert.That(repeated.IsRepeat, Is.True);
            Assert.That(repeated.CopiedBytes, Is.Zero);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(source.DeployedProject));
            foreach (var pair in times)
            {
                Assert.That(File.GetLastWriteTimeUtc(pair.Key), Is.EqualTo(pair.Value));
            }

            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(installed.Snapshot.DirectoryPath), "*.meta", SearchOption.AllDirectories), Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(firstPlan.DirectoryPath, "payload~.meta"))), Is.False);
            Assert.That(AssetDatabase.AssetPathToGUID(RelativeAsset(installed.Snapshot.DirectoryPath)), Is.Empty);
            var second = source.Version("second");
            var secondInstalled = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(second));
            Assert.That(secondInstalled.Snapshot.Document.CatalogDigest, Is.Not.EqualTo(first.Document.CatalogDigest));
            CollectionAssert.AreEqual(sourceBefore, ResourceReleaseFixture.Snapshot(first.DirectoryPath));
            Assert.That(Open(first).Document.CatalogDigest, Is.EqualTo(first.Document.CatalogDigest));
            CollectionAssert.AreEqual(oldVersionBefore, ResourceReleaseFixture.Snapshot(installed.DirectoryPath));
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot), "*", SearchOption.AllDirectories)
                .Any(x => x.Contains("journal") || x.Contains("deploy.lock") || x.Contains(".next")), Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        [TestCase("JournalWritten")]
        [TestCase("StagingCreated")]
        [TestCase("FileCopied")]
        [TestCase("StageVerified")]
        [TestCase("BeforeInstall")]
        [TestCase("CommitReady")]
        public void CancellationBeforeCommitKeepsOldDeploymentAndRequiresExplicitRecovery(string checkpoint)
        {
            using var source = new BuiltInSource();
            var old = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(source.Snapshot));
            var before = ResourceReleaseFixture.Snapshot(source.DeployedProject);
            var next = source.Version("next");
            var plan = ResourceBuiltInDeployer.Preflight(next);
            using var cancel = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => ResourceBuiltInDeployer.Execute(plan, cancel.Token, point =>
            {
                if (point == checkpoint)
                {
                    cancel.Cancel();
                }
            }));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(source.DeployedProject));
            Assert.That(Open(old.Snapshot).Document.CatalogDigest, Is.EqualTo(old.Snapshot.Document.CatalogDigest));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(plan.DirectoryPath)), Is.False);
            Assert.Throws<InvalidOperationException>(() => ResourceBuiltInDeployer.Preflight(next));
            Assert.That(ResourceBuiltInDeployer.Recover(source.ProjectId, next.Document.Platform, source.ProjectGuid), Is.Null);
            Assert.That(ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(next)).Snapshot.Document.CatalogDigest, Is.EqualTo(next.Document.CatalogDigest));
        }

        [TestCase("VersionInstalled")]
        [TestCase("BeforeImport")]
        [TestCase("AfterImport")]
        public void PostCommitFaultIsRecoverableWithoutTheOriginalSourceAndPreservesOldVersion(string checkpoint)
        {
            using var source = new BuiltInSource();
            var first = source.Snapshot;
            ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(first));
            var second = source.Version("committed");
            var plan = ResourceBuiltInDeployer.Preflight(second);
            Assert.That(Assert.Throws<IOException>(() => ResourceBuiltInDeployer.Execute(plan, default,
                point =>
                {
                    if (point == checkpoint)
                    {
                        throw new IOException("fixture commit interruption");
                    }
                })).Message, Is.EqualTo("fixture commit interruption"));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(plan.WorkspacePath, "journal.json"))), Is.True);
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(second.DirectoryPath), true);
            var recovered = ResourceBuiltInDeployer.Recover(source.ProjectId, second.Document.Platform, source.ProjectGuid);
            Assert.That(recovered.Snapshot.Document.CatalogDigest, Is.EqualTo(second.Document.CatalogDigest));
            Assert.That(Open(first).Document.CatalogDigest, Is.EqualTo(first.Document.CatalogDigest));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(plan.WorkspacePath, "journal.json"))), Is.False);
            Assert.That(ResourceBuiltInDeployer.Recover(source.ProjectId, second.Document.Platform, source.ProjectGuid), Is.Null);
        }

        [TestCase("payload-meta")]
        [TestCase("wrapper-file")]
        [TestCase("orphan-meta")]
        [TestCase("bad-owner")]
        [TestCase("corrupt-bundle")]
        public void UnknownOrCorruptedManagedContentIsNeverOverwritten(string kind)
        {
            using var source = new BuiltInSource();
            var first = source.Snapshot;
            var installed = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(first));
            var path = kind switch
            {
                "payload-meta" => Path.Combine(installed.Snapshot.DirectoryPath, "resource-catalog.json.meta"),
                "wrapper-file" => Path.Combine(installed.DirectoryPath, "foreign.txt"),
                "orphan-meta" => Path.Combine(source.DeployedProject, "foreign.meta"),
                "bad-owner" => Path.Combine(source.DeployedProject, "ylr-owner.json~"),
                _ => Path.Combine(installed.Snapshot.DirectoryPath, first.Document.Bundles[0].RelativeFile)
            };
            var original = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) ? File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)) : null;
            try
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "foreign-or-corrupt");
                var before = ResourceReleaseFixture.Snapshot(source.DeployedProject);
                Assert.Throws<InvalidDataException>(() => ResourceBuiltInDeployer.Preflight(first));
                CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(source.DeployedProject));
                if (kind == "payload-meta" || kind == "wrapper-file" || kind == "corrupt-bundle")
                {
                    Assert.Throws<InvalidDataException>(() => Open(first));
                }
            }
            finally
            {
                if (original == null)
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                }
                else
                {
                    File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), original);
                }
            }
            Assert.That(Open(first), Is.Not.Null);
        }

        [Test]
        public void UnknownStageContentAndDamagedJournalBlockRecoveryWithoutDeletingAnything()
        {
            using var source = new BuiltInSource();
            var plan = ResourceBuiltInDeployer.Preflight(source.Snapshot);
            Assert.That(Assert.Throws<IOException>(() => ResourceBuiltInDeployer.Execute(plan, default,
                point =>
                {
                    if (point == "FileCopied")
                    {
                        throw new IOException("fixture interrupted copy");
                    }
                })).Message, Is.EqualTo("fixture interrupted copy"));
            var stage = Directory.GetDirectories(global::YLFramework.Testing.PackageTestPaths.Physical(plan.WorkspacePath)).Single();
            var foreign = Path.Combine(stage, "do-not-delete.txt");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(foreign), "unregistered");
            var before = ResourceReleaseFixture.Snapshot(plan.WorkspacePath);
            Assert.Throws<InvalidDataException>(() => ResourceBuiltInDeployer.Recover(source.ProjectId, source.Snapshot.Document.Platform, source.ProjectGuid));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(plan.WorkspacePath));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(foreign));
            var journal = Path.Combine(plan.WorkspacePath, "journal.json");
            var original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(journal));
            try
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(journal), "{}");
                Assert.Throws<InvalidDataException>(() => ResourceBuiltInDeployer.Recover(source.ProjectId, source.Snapshot.Document.Platform, source.ProjectGuid));
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(stage)), Is.True);
            }
            finally
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(journal), original);
            }
            Assert.That(ResourceBuiltInDeployer.Recover(source.ProjectId, source.Snapshot.Document.Platform, source.ProjectGuid), Is.Null);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(stage)), Is.False);
        }

        [Test]
        public void AConflictingNextJournalIsNotSilentlyDeletedByRecovery()
        {
            using var source = new BuiltInSource();
            var plan = ResourceBuiltInDeployer.Preflight(source.Snapshot);
            Assert.That(Assert.Throws<IOException>(() => ResourceBuiltInDeployer.Execute(plan, default,
                point =>
                {
                    if (point == "StagingCreated")
                    {
                        throw new IOException("fixture staged");
                    }
                })).Message, Is.EqualTo("fixture staged"));
            var next = Path.Combine(plan.WorkspacePath, "journal.json.next");
            try
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(next), "foreign record");
                var before = ResourceReleaseFixture.Snapshot(plan.WorkspacePath);
                Assert.Throws<InvalidDataException>(() => ResourceBuiltInDeployer.Recover(source.ProjectId, source.Snapshot.Document.Platform, source.ProjectGuid));
                CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(plan.WorkspacePath));
            }
            finally
            {
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(next)))
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(next));
                }
            }
        }

        [Test]
        public void SourceChangesAfterPreflightAndSameDigestDifferentProvenanceAreRejected()
        {
            using var source = new BuiltInSource();
            var plan = ResourceBuiltInDeployer.Preflight(source.Snapshot);
            var bundle = Path.Combine(source.Snapshot.DirectoryPath, source.Snapshot.Document.Bundles[0].RelativeFile);
            var original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(bundle));
            try
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(bundle), "tampered");
                Assert.Throws<InvalidDataException>(() => ResourceBuiltInDeployer.Deploy(plan));
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(plan.DirectoryPath)), Is.False);
            }
            finally
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(bundle), original);
            }
            ResourceBuiltInDeployer.Deploy(plan);
            var completePath = Path.Combine(source.Snapshot.DirectoryPath, ".ylr-complete.json");
            var complete = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(completePath));
            try
            {
                var data = JObject.Parse(Encoding.UTF8.GetString(complete));
                data["ProjectGuid"] = Guid.NewGuid().ToString("N");
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(completePath), ResourceCatalogCodec.CanonicalBytes(data));
                Assert.Throws<InvalidDataException>(() => ResourceBuiltInDeployer.Deploy(plan));
            }
            finally
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(completePath), complete);
            }
            Assert.That(Open(source.Snapshot), Is.Not.Null);
        }

        [Test]
        public void JunctionTargetIsRejectedWithoutTouchingTheOutsideContentOrOldDeployment()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore("Windows junction contract.");
            }

            using var source = new BuiltInSource();
            var first = source.Snapshot;
            ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(first));
            var next = source.Version("outside");
            var plan = ResourceBuiltInDeployer.Preflight(next);
            var external = ResourceReleaseFixture.Snapshot(source.Root);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                "/c mklink /J \"" + plan.DirectoryPath + "\" \"" + next.DirectoryPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.Zero);
            try
            {
                Assert.Throws<InvalidDataException>(() => ResourceBuiltInDeployer.Preflight(next));
                Assert.That(Open(first), Is.Not.Null);
                CollectionAssert.AreEqual(external, ResourceReleaseFixture.Snapshot(source.Root));
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(plan.DirectoryPath), false);
            }
        }

        [Test]
        public void DanglingRootJunctionIsRejectedByReadOnlyPreflightRatherThanTreatedAsMissing()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore("Windows junction contract.");
            }

            using var source = new BuiltInSource();
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)))
            {
                Assert.Ignore("This test never replaces an existing deployment root.");
            }

            var missing = Path.Combine(source.Root, "nonexistent-outside-target");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                "/c mklink /J \"" + ResourceBuiltInDeployer.ResourcesRoot + "\" \"" + missing + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.Zero);
            try
            {
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(missing)), Is.False, "The regression requires a genuinely missing junction target; Mono may still report the junction itself as an existing directory.");
                Assert.That(File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)) & FileAttributes.ReparsePoint, Is.Not.Zero);
                Assert.Throws<InvalidDataException>(() => ResourceBuiltInDeployer.Preflight(source.Snapshot));
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(missing)), Is.False);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(source.WorkspacePath)), Is.False);
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot), false);
            }
        }

        private static string RelativeAsset(string path) => "Assets" + path.Substring(Application.dataPath.Length).Replace('\\', '/');
        private static ResourceLocalSnapshot Open(ResourceLocalSnapshot snapshot) => ResourceLocalSnapshot.OpenBuiltIn(snapshot.Document.ProjectId,
            snapshot.Document.CatalogDigest, snapshot.Document.Platform, snapshot.Document.AppContentCompatibilityId);

        [Test]
        public void StoreMountAndPrepareRevalidateTheEntireBuiltInShellWithoutWeakeningOrdinaryOpen()
        {
            using var source = new BuiltInSource();
            var first = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(source.Snapshot));
            var next = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(source.Version("store target")));
            var cache = Path.Combine(source.Root, "private-store");
            var store = new ArtifactStore(cache, new[] { new ResourceProjectMount(source.ProjectId, first.Snapshot.Document.Platform, "app-v1", first.Snapshot) });
            var mounted = store.Mount();
            Assert.That(mounted.Single().DirectoryPath, Is.EqualTo(first.Snapshot.DirectoryPath));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(cache)), Is.False, "First built-in fallback mount remains read-only.");
            Assert.Throws<InvalidDataException>(() => ResourceLocalSnapshot.Open(first.Snapshot.DirectoryPath, source.ProjectId,
                first.Snapshot.Document.CatalogDigest, first.Snapshot.Document.Platform, "app-v1"));
            var receipt = Path.Combine(first.DirectoryPath, "ylr-deployment.json~");
            var original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(receipt));
            try
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(receipt), "{}");
                Assert.Throws<InvalidDataException>(() => store.Mount(), "Reopening only the payload would miss corrupted provenance.");
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(cache)), Is.False);
            }
            finally
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(receipt), original);
            }
            var plan = store.Check(mounted, new[] { next.Snapshot });
            var prepared = store.Prepare(plan, default);
            Assert.That(prepared.Projects.Single().FromDigest, Is.EqualTo(first.Snapshot.Document.CatalogDigest));
            Assert.That(prepared.Projects.Single().ToDigest, Is.EqualTo(next.Snapshot.Document.CatalogDigest));
            Assert.That(Open(first.Snapshot), Is.Not.Null);
            Assert.That(Open(next.Snapshot), Is.Not.Null);
        }

        internal sealed class BuiltInSource : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "kilo", "c02-" + Guid.NewGuid().ToString("N"));
            internal readonly string ProjectId = "c02-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            internal readonly string ProjectGuid = Guid.NewGuid().ToString("N");
            internal ResourceLocalSnapshot Snapshot;
            private readonly string _workspace;
            internal string WorkspacePath => _workspace;
            private readonly bool _deploymentRootExisted = Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot));
            internal string DeployedProject => Path.Combine(ResourceBuiltInDeployer.ResourcesRoot, ProjectId);

            internal BuiltInSource()
            {
                Version("first");
                _workspace = ResourceBuiltInDeployer.Preflight(Snapshot).WorkspacePath;
            }

            internal ResourceLocalSnapshot Version(string content)
            {
                var payload = ResourceCatalogCodecTests.Payload();
                payload["ProjectId"] = ProjectId;
                payload["UnityCompatibility"] = Application.unityVersion;
                var oldFile = (string)payload["Bundles"][0]["RelativeFile"];
                var name = "ylr_" + ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(ProjectId)).Substring(0, 32) + "_a_fixture";
                payload["Bundles"][0]["LogicalId"] = name;
                payload["Bundles"][0]["NativeName"] = name;
                payload["Assets"][0]["BundleId"] = name;
                var newFile = "b_" + ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(name)).Substring(0, 32);
                payload["Bundles"][0]["RelativeFile"] = newFile;
                foreach (var file in (JArray)payload["SupportFiles"])
                {
                    if ((string)file["RelativeFile"] == oldFile + ".manifest")
                    {
                        file["RelativeFile"] = newFile + ".manifest";
                    }
                }

                byte[] Bytes(JToken file) => Encoding.UTF8.GetBytes((file["LogicalId"] != null || (string)file["RelativeFile"] == (string)payload["ManifestFile"] ? "UnityFS\0" : "") + content);
                foreach (var file in ((JArray)payload["Bundles"]).Concat((JArray)payload["SupportFiles"]))
                {
                    var bytes = Bytes(file);
                    file["Length"] = bytes.Length;
                    file["Sha256"] = ResourceCatalogCodec.Digest(bytes);
                }
                var catalogBytes = ResourceCatalogCodec.Seal(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
                var digest = ResourceCatalogCodec.Digest(catalogBytes);
                var directory = Path.Combine(Root, digest);
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(directory));
                foreach (var file in ((JArray)payload["Bundles"]).Concat((JArray)payload["SupportFiles"]))
                {
                    File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(directory, (string)file["RelativeFile"])), Bytes(file));
                }

                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(directory, "resource-catalog.json")), catalogBytes);
                var catalog = ResourceCatalogCodec.Read(catalogBytes, ProjectId, "StandaloneWindows64", Application.unityVersion, "app-v1");
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(directory, ".ylr-complete.json")), ResourceCatalogCodec.CanonicalBytes(new JObject
                {
                    ["Version"] = 1,
                    ["ProjectGuid"] = ProjectGuid,
                    ["ProjectId"] = ProjectId,
                    ["Platform"] = catalog.Platform,
                    ["BuildId"] = catalog.BuildId,
                    ["CatalogDigest"] = digest
                }));
                return Snapshot = ResourceLocalSnapshot.Open(directory, ProjectId, digest, catalog.Platform, "app-v1");
            }

            public void Dispose()
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_workspace)))
                {
                    Assert.That((string)ResourceBuiltInLayout.ReadObject(Path.Combine(_workspace, "ylr-owner.json~"))["ProjectGuid"], Is.EqualTo(ProjectGuid));
                    ResourceBuiltInDeployer.Recover(ProjectId, "StandaloneWindows64", ProjectGuid);
                    ResourceBuildOperation.RejectLinks(_workspace);
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_workspace), true);
                }
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(DeployedProject)))
                {
                    Assert.That((string)ResourceBuiltInLayout.ReadObject(Path.Combine(DeployedProject, "ylr-owner.json~"))["ProjectGuid"], Is.EqualTo(ProjectGuid));
                    ResourceBuildOperation.RejectLinks(DeployedProject);
                    Assert.That(AssetDatabase.DeleteAsset(RelativeAsset(DeployedProject)), Is.True);
                }
                if (!_deploymentRootExisted && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)))
                {
                    CollectionAssert.AreEquivalent(new[] { "ylr-owner.json~" }, Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)).Select(Path.GetFileName));
                    Assert.That(AssetDatabase.DeleteAsset("Assets/StreamingAssets/YLFrameworkResources"), Is.True);
                }
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(Root), true);
                }
            }
        }
    }
}
