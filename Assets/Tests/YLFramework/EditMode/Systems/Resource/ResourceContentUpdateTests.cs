using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceContentUpdateTests
    {
        [TestCase("Bundle")]
        [TestCase("Manifest")]
        public void DirectPreparationRejectsUnknownContentBeforeRecordingPrepared(string role)
        {
            using var fixture = new UpdateFixture();
            var target = fixture.Create("candidate", role);
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            var error = Assert.Throws<ResourceException>(() => fixture.Store.Prepare(plan, CancellationToken.None),
                "Matching Catalog delivery hashes do not authenticate an unknown content encoding.");
            Assert.That(error.Kind.ToString(), Is.EqualTo("ContentFormatUnsupported"));
            Assert.That(fixture.Store.GetPreparedUpdates(), Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("active.json"))), Is.False);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("last-known-good.json"))), Is.False);
            Assert.That(fixture.Store.Mount().Single().Document.CatalogDigest, Is.EqualTo(fixture.BuiltIn.Document.CatalogDigest));
        }

        [Test]
        public void UnchangedProjectStillRequiresContentValidationBeforePrepared()
        {
            using var fixture = new UpdateFixture("Manifest");
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { fixture.BuiltIn });
            Assert.That(plan.Projects.Single().IsChanged, Is.False);
            var error = Assert.Throws<ResourceException>(() => fixture.Store.Prepare(plan, CancellationToken.None));
            Assert.That(error.Kind.ToString(), Is.EqualTo("ContentFormatUnsupported"));
            Assert.That(fixture.Store.GetPreparedUpdates(), Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("commit-journal.json"))), Is.False);
        }

        [Test]
        public void CheckAndMountOnlyVerifyDeliveryBytesAndDoNotCreateAStore()
        {
            using var fixture = new UpdateFixture("Manifest");
            var current = fixture.Store.Mount();
            var plan = fixture.Store.Check(current, new[] { fixture.BuiltIn });
            Assert.That(plan.Projects.Single().ToDigest, Is.EqualTo(fixture.BuiltIn.Document.CatalogDigest));
            Assert.That(current.Single().Document.CatalogDigest, Is.EqualTo(fixture.BuiltIn.Document.CatalogDigest));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Store.RootPath)), Is.False,
                "Content availability cannot turn a read-only Check or Mount into key acquisition or repair.");
        }

        [UnityTest]
        public IEnumerator RuntimeInitializationRejectsIncompatibleSnapshotsBeforeNativeLoading()
        {
            yield return new EnterPlayMode();
            VerifyRuntimeCompatibility();
            yield return new ExitPlayMode();
        }

        private static void VerifyRuntimeCompatibility()
        {
            Assert.That(UnityEditor.EditorUserBuildSettings.activeBuildTarget, Is.EqualTo(UnityEditor.BuildTarget.StandaloneWindows64));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            foreach (var field in new[] { "Platform", "UnityCompatibility" })
            {
                using var fixture = new UpdateFixture();
                var otherUnity = Application.unityVersion == "6000.3.27f1" ? "2022.3.62f3c1" : "6000.3.27f1";
                var snapshot = fixture.Create("runtime-mismatch",
                    platform: field == "Platform" ? "StandaloneLinux64" : null,
                    unityCompatibility: field == "UnityCompatibility" ? otherUnity : null);
                if (field == "UnityCompatibility")
                {
                    Assert.Throws<InvalidDataException>(() => ResourceLocalSnapshot.Open(snapshot.DirectoryPath, "catalog",
                        snapshot.Document.CatalogDigest, snapshot.Document.Platform, "app-v1"));
                }
                var catalogPath = Path.Combine(snapshot.DirectoryPath, "resource-catalog.json");
                var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(catalogPath));
                // 目录按自身声明已合法冻结；这里验证真实消费者，不让 Codec 的 scope 拒绝冒充运行时门禁。
                using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { snapshot.CreateCatalog() }));
                Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Failed), field);
                Assert.That(initialized.Error.Kind, Is.EqualTo(ResourceError.Unsupported), field);
                Assert.That(initialized.Error.Message, Does.Contain("does not match this Unity runtime/platform"));
                Assert.That(ResourceSystem.Current, Is.Null);
                Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Store.RootPath)), Is.False);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(catalogPath)));
            }

            using var recovered = ResourceSystem.InitializeAsync(new ResourceConfiguration(Array.Empty<ResourceCatalog>()));
            Assert.That(recovered.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            var system = recovered.TakeResult();
            Assert.That(system.GetDiagnosticsSnapshot().NativeBundleLoadCount, Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().NativeBundleUnloadCount, Is.Zero);
            using var closed = system.ShutdownAsync();
            Assert.That(closed.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(closed.TakeResult(), Is.True);
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        // 仅测试 Store 的交付与认证边界；UnityFS 前缀夹具不声称可由 native 加载。
        private sealed class UpdateFixture : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "kilo", "cu-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            internal ResourceLocalSnapshot BuiltIn
            {
                get;
            }
            internal ArtifactStore Store
            {
                get;
            }

            internal UpdateFixture(string invalidRole = null, ResourceRemoteSource remote = null)
            {
                BuiltIn = Create("built-in", invalidRole);
                Store = new ArtifactStore(Path.Combine(_root, "store"), new[] {
                    new ResourceProjectMount("catalog", "StandaloneWindows64", "app-v1", BuiltIn, remote: remote) });
            }

            internal string Control(string name) => Path.Combine(Store.RootPath, "catalog", "StandaloneWindows64", name);
            internal string Owned(ResourceLocalSnapshot snapshot) => Path.Combine(Control("versions"), snapshot.Document.CatalogDigest);

            internal ResourceLocalSnapshot Create(string version, string invalidRole = null, bool encrypted = false, bool corruptMiddle = false,
                string platform = null, string unityCompatibility = null)
            {
                var payload = ResourceCatalogCodecTests.Payload();
                payload["Platform"] = platform ?? "StandaloneWindows64";
                payload["UnityCompatibility"] = unityCompatibility ?? Application.unityVersion;
                var files = new Dictionary<string, byte[]>();
                foreach (JObject record in ((JArray)payload["Bundles"]).Concat((JArray)payload["SupportFiles"]))
                {
                    var name = (string)record["RelativeFile"];
                    var role = record["LogicalId"] != null ? "Bundle" : name == "manifest" ? "Manifest" : null;
                    var bytes = Encoding.UTF8.GetBytes((role == null ? "support:" : role == invalidRole ? "invalid:" : "UnityFS\0") + version + ":" + name);
                    if (encrypted && role != null)
                    {
                        Array.Resize(ref bytes, 200000);
                        using var raw = new MemoryStream(bytes, false);
                        using var output = new MemoryStream();
                        ResourceBundleEnvelope.Encrypt(raw, output, new ResourceBundleContext("catalog", (string)payload["Platform"], (string)payload["UnityCompatibility"],
                            role, role == "Bundle" ? (string)record["LogicalId"] : "manifest"), "test-content", _testMaterial);
                        bytes = output.ToArray();
                        if (corruptMiddle && role == "Bundle")
                        {
                            bytes[12 + BitConverter.ToInt32(bytes, 8) + 65536 + 50] ^= 1;
                        }
                    }
                    record["Length"] = bytes.Length;
                    record["Sha256"] = ResourceCatalogCodec.Digest(bytes);
                    files.Add(name, bytes);
                }
                var catalogBytes = ResourceCatalogCodec.Seal(ResourceCatalogCodec.CanonicalBytes(payload));
                var document = ResourceCatalogCodec.Read(catalogBytes, "catalog", (string)payload["Platform"], (string)payload["UnityCompatibility"], "app-v1");
                var path = Path.Combine(_root, "source", document.CatalogDigest);
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                foreach (var file in files)
                {
                    File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, file.Key)), file.Value);
                }

                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, "resource-catalog.json")), catalogBytes);
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, ".ylr-complete.json")), ResourceCatalogCodec.CanonicalBytes(new JObject
                {
                    ["Version"] = 1,
                    ["ProjectGuid"] = new string('1', 32),
                    ["ProjectId"] = document.ProjectId,
                    ["Platform"] = document.Platform,
                    ["BuildId"] = document.BuildId,
                    ["CatalogDigest"] = document.CatalogDigest
                }));
                return ResourceLocalSnapshot.OpenChecked(path, "catalog", document.CatalogDigest, document.Platform, "app-v1", document.UnityCompatibility);
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
