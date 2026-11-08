using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    [Category("YL.Special.ResourceUpdates")]
    public partial class ResourceContentBuildTests
    {
        private const string App = "fixture-v1";
        private const string KeyId = "g2-test-v1";
        private const string NextKeyId = "g2-test-v2";
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        // 仅用于自有测试夹具，不是可用于发行的密钥材料。
        private static readonly byte[] _testKey = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        private static readonly byte[] _otherKey = Enumerable.Range(33, 32).Select(x => (byte)x).ToArray();

        [Test]
        public void ContentBuildExposesDeliveryFactsWithoutAmbiguousLegacyOverloads()
        {
            Assert.That(Field("contentEncryption").FieldType, Is.EqualTo(typeof(ResourceContentEncryption)));
            Assert.That(Field("contentKeyId").FieldType, Is.EqualTo(typeof(string)));
            foreach (var name in new[] { "ContentEncryption", "ContentKeyId", "DeliveryPath", "DeliveryFiles", "DeliveryBytes",
                         "EncodedDeliveryBytes", "ReusedDeliveryBytes", "ContentEncodingMilliseconds" })
            {
                Property(typeof(ResourceBuildResult), name);
            }

            BuildMethod();
            Method(typeof(ResourceBundleBuilder), "ExecuteWithContentKey", typeof(ResourceProject), typeof(BuildTarget), typeof(bool),
                typeof(CancellationToken), typeof(Func<string, AssetBundleBuild[], BuildAssetBundleOptions, BuildTarget, AssetBundleManifest>),
                typeof(IResourceContentKeyProvider));
            PublishMethod();
            PublishExecuteMethod();
            Assert.That(typeof(ResourceBundleBuilder).GetMethods(Members).Count(x => x.Name == "Execute"), Is.EqualTo(1));
            Assert.That(typeof(ResourcePublisher).GetMethods(Members).Count(x => x.Name == "Execute"), Is.EqualTo(1));
            Assert.That(typeof(ResourcePublisher).GetMethods(Members).Count(x => x.Name == "Publish"), Is.EqualTo(1));
        }

        [Test]
        public void NoneKeepsRawDeliveryVersionOneAndDoesNotRequestAnyKey()
        {
            using var fixture = new ContentBuildFixture();
            var first = fixture.BuildPlain();
            var originalCatalog = ResourceCatalogCodec.Write(ResourceCatalogBuilder.Create(first, App));
            fixture.Configure(ResourceContentEncryption.None, "dormant key id is not a secret or a build input");
            using var keys = fixture.Keys(throws: true);
            Assert.That(fixture.Workspace(), Is.EqualTo(first.WorkspacePath));
            var next = fixture.Build(keys);
            Assert.That(next.BuildSettingsFingerprint, Is.EqualTo(first.BuildSettingsFingerprint));
            Assert.That(Value<ResourceContentEncryption>(next, "ContentEncryption"), Is.EqualTo(ResourceContentEncryption.None));
            Assert.That(DeliveryPath(next), Is.EqualTo(next.WorkspacePath));
            CollectionAssert.AreEqual(Facts(next.Files), Facts(DeliveryFiles(next)));
            CollectionAssert.AreEqual(originalCatalog, ResourceCatalogCodec.Write(ResourceCatalogBuilder.Create(next, App)));
            Assert.That(Value<long>(next, "EncodedDeliveryBytes"), Is.Zero);
            Assert.That(Value<long>(next, "ReusedDeliveryBytes"), Is.Zero);
            var root = Path.GetDirectoryName(next.WorkspacePath);
            Assert.That((int)JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, ".ylr-workspace.json"))))["Version"], Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { next.WorkspacePath }, Directory.GetDirectories(global::YLFramework.Testing.PackageTestPaths.Physical(root)));
            var release = fixture.Publish(next, keys);
            Assert.That(release.Catalog.CatalogDigest, Is.EqualTo(ResourceCatalogCodec.Digest(originalCatalog)));
            using var signingKey = new RSACryptoServiceProvider(2048);
            var signed = ResourceReleaseSigner.Create(release, "g2-legacy", 1, "signing-test-only", signingKey, DateTimeOffset.UtcNow.AddDays(1));
            Assert.That((string)ResourceReleaseSignature.Verify(signed, "signing-test-only", signingKey.ExportParameters(false))["CatalogDigest"],
                Is.EqualTo(release.Catalog.CatalogDigest));
            Assert.That(keys.Copies, Is.Zero, "None must not probe even an explicitly supplied provider.");
            Assert.That(keys.Disposed, Is.False);
        }

        [TestCase("absent")]
        [TestCase("missing")]
        [TestCase("throwing")]
        public void EncryptedBuildRequiresThisOperationsKeyBeforeAnyNativeOrWorkspaceWrite(string providerState)
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys(missing: providerState == "missing", throws: providerState == "throwing");
            var workspace = fixture.Workspace();
            var invoked = false;
            var error = Assert.Throws<ResourceException>(() => fixture.Execute(providerState == "absent" ? null : keys,
                (path, builds, options, target) =>
                {
                    invoked = true;
                    return null;
                }));
            Assert.That(error.Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            Assert.That(error.ToString(), Does.Not.Contain("g2-sensitive-provider-material"));
            Assert.That(invoked, Is.False);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(workspace))), Is.False);
            keys.AssertReleasedCopies();
            Assert.That(keys.Disposed, Is.False);
        }

        [TestCase("")]
        [TestCase("bad key")]
        [TestCase("../other")]
        public void InvalidContentKeyIdIsRejectedWithoutClaimingAWorkspace(string keyId)
        {
            using var fixture = new ContentBuildFixture();
            var plainWorkspace = fixture.Workspace();
            fixture.Configure(keyId: keyId);
            using var keys = fixture.Keys();
            var error = Assert.Catch(() => fixture.Build(keys));
            Assert.That(error, Is.InstanceOf<ArgumentException>().Or.InstanceOf<InvalidDataException>());
            Assert.That(keys.Copies, Is.Zero);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(plainWorkspace))), Is.False);
        }

        [Test]
        public void EncryptedBuildPreservesRawNativeBytesAndSealsOnlyDeliveryFactsIntoCatalogV1()
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var build = fixture.Build(keys);
            var delivery = DeliveryPath(build);
            var root = Path.GetDirectoryName(build.WorkspacePath);
            Assert.That(delivery, Is.Not.EqualTo(build.WorkspacePath));
            Assert.That(delivery.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal), Is.True);
            Assert.That(delivery.StartsWith(build.WorkspacePath + Path.DirectorySeparatorChar, StringComparison.Ordinal), Is.False);
            Assert.That(Value<ResourceContentEncryption>(build, "ContentEncryption"), Is.EqualTo(ResourceContentEncryption.Aes256GcmHkdfSegmentedV1));
            Assert.That(Value<string>(build, "ContentKeyId"), Is.EqualTo(KeyId));
            Assert.That((int)JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, ".ylr-workspace.json"))))["Version"], Is.EqualTo(2));
            CollectionAssert.AreEquivalent(build.Files.Select(x => x.Name), Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(build.WorkspacePath)).Select(Path.GetFileName));
            CollectionAssert.AreEqual(build.Files.Select(x => x.Name), DeliveryFiles(build).Select(x => x.Name));
            foreach (var raw in build.Files)
            {
                var delivered = DeliveryFiles(build).Single(x => x.Name == raw.Name);
                var rawBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(build.WorkspacePath, raw.Name)));
                var deliveryBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(delivery, raw.Name)));
                Assert.That(raw.Length, Is.EqualTo(rawBytes.LongLength));
                Assert.That(raw.Sha256, Is.EqualTo(ResourceCatalogCodec.Digest(rawBytes)));
                Assert.That(delivered.Length, Is.EqualTo(deliveryBytes.LongLength));
                Assert.That(delivered.Sha256, Is.EqualTo(ResourceCatalogCodec.Digest(deliveryBytes)));
                if (raw.Name.EndsWith(".manifest", StringComparison.Ordinal))
                {
                    CollectionAssert.AreEqual(rawBytes, deliveryBytes);
                }
                else
                {
                    Assert.That(System.Text.Encoding.ASCII.GetString(rawBytes, 0, 8), Is.EqualTo("UnityFS\0"));
                    Assert.That(System.Text.Encoding.ASCII.GetString(deliveryBytes, 0, 8), Is.EqualTo("YLRENC01"));
                    using var input = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(delivery, raw.Name)));
                    var header = ResourceBundleEnvelope.Inspect(input, Context(build, raw.Name));
                    Assert.That(header.KeyId, Is.EqualTo(KeyId));
                    ResourceBundleEnvelope.ValidateAll(input, Context(build, raw.Name), _testKey, default, raw.Sha256, raw.Length);
                }
            }
            foreach (var bundle in build.Bundles)
            {
                Assert.That(bundle.FileBytes, Is.EqualTo(build.Files.Single(x => x.Name == bundle.Name).Length));
            }

            var catalog = ResourceCatalogBuilder.Create(build, App);
            AssertCatalogDelivery(build, catalog);
            Assert.That(Value<long>(build, "DeliveryBytes"), Is.EqualTo(DeliveryFiles(build).Sum(x => x.Length)));
            Assert.That(Value<long>(build, "EncodedDeliveryBytes"), Is.EqualTo(ContentBytes(build)));
            Assert.That(Value<long>(build, "ReusedDeliveryBytes"), Is.Zero);
            Assert.That(Value<double>(build, "ContentEncodingMilliseconds"), Is.GreaterThanOrEqualTo(0));
            keys.AssertReleasedCopies();
            Assert.That(keys.Disposed, Is.False);
        }

        [Test]
        public void ForcedNativeRebuildReusesExactCiphertextOnlyAfterAuthenticatingWithTheCurrentKey()
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var first = fixture.Build(keys);
            var raw = Facts(first.Files);
            var delivery = ResourceReleaseFixture.Snapshot(DeliveryPath(first));
            var digest = ResourceCatalogBuilder.Create(first, App).CatalogDigest;
            var previousCopies = keys.Copies;
            var second = fixture.Build(keys, force: true);
            Assert.That(second.WasUnityBuildInvoked && second.ForceRebuild, Is.True);
            CollectionAssert.AreEqual(raw, Facts(second.Files));
            CollectionAssert.AreEqual(delivery, ResourceReleaseFixture.Snapshot(DeliveryPath(second)));
            Assert.That(ResourceCatalogBuilder.Create(second, App).CatalogDigest, Is.EqualTo(digest));
            Assert.That(Value<long>(second, "EncodedDeliveryBytes"), Is.Zero);
            Assert.That(Value<long>(second, "ReusedDeliveryBytes"), Is.EqualTo(ContentBytes(second)));
            Assert.That(keys.Copies, Is.GreaterThan(previousCopies));
            keys.AssertReleasedCopies();
        }

        [Test]
        public void SameKeyIdWithDifferentMaterialCannotSilentlyReplaceAnExistingCiphertextCandidate()
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var first = fixture.Build(keys);
            var before = ResourceReleaseFixture.Snapshot(DeliveryPath(first));
            using var wrong = fixture.Keys(material: _otherKey);
            var error = Assert.Catch(() => fixture.Build(wrong));
            AssertAuthenticationFailure(error);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(DeliveryPath(first)));
            wrong.AssertReleasedCopies();
        }

        [Test]
        public void CorruptMiddleSegmentIsAHardCandidateFailureEvenWhenItsRecordedDeliveryShaMatches()
        {
            using var fixture = new ContentBuildFixture();
            fixture.ConfigureLargeText();
            using var keys = fixture.Keys();
            var build = fixture.Build(keys);
            CorruptMiddleSegment(build);
            var before = ResourceReleaseFixture.Snapshot(DeliveryPath(build));
            var error = Assert.Catch(() => fixture.Build(keys));
            AssertAuthenticationFailure(error);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(DeliveryPath(build)));
        }

        [Test]
        public void MissingDerivedFileIsNewRandomizedEncryptionRatherThanAReproducibleNonce()
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var first = fixture.Build(keys);
            var name = TextBundle(first);
            var previous = DeliveryFiles(first).Single(x => x.Name == name);
            var digest = ResourceCatalogBuilder.Create(first, App).CatalogDigest;
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DeliveryPath(first), name)));
            var second = fixture.Build(keys);
            Assert.That(DeliveryFiles(second).Single(x => x.Name == name).Sha256, Is.Not.EqualTo(previous.Sha256));
            Assert.That(ResourceCatalogBuilder.Create(second, App).CatalogDigest, Is.Not.EqualTo(digest));
            foreach (var file in DeliveryFiles(first).Where(x => x.Name != name))
            {
                Assert.That(DeliveryFiles(second).Single(x => x.Name == file.Name).Sha256, Is.EqualTo(file.Sha256));
            }
        }

        [Test]
        public void RotatingKeyIdPartitionsNewDeliveryWithoutRewritingTheOldWorkspace()
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var first = fixture.Build(keys);
            var before = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(first.WorkspacePath));
            fixture.Configure(keyId: NextKeyId);
            using var rotated = fixture.Keys(NextKeyId, _otherKey);
            var second = fixture.Build(rotated);
            Assert.That(second.WorkspacePath, Is.Not.EqualTo(first.WorkspacePath));
            Assert.That(second.ManifestBundleFile, Is.EqualTo(first.ManifestBundleFile), "Content encoding must not change the native isolation identity.");
            Assert.That(second.BuildSettingsFingerprint, Is.Not.EqualTo(first.BuildSettingsFingerprint));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(first.WorkspacePath)));
            using var input = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DeliveryPath(second), second.ManifestBundleFile)));
            Assert.That(ResourceBundleEnvelope.Inspect(input, Context(second, second.ManifestBundleFile)).KeyId, Is.EqualTo(NextKeyId));
            var raw = second.Files.Single(x => x.Name == second.ManifestBundleFile);
            ResourceBundleEnvelope.ValidateAll(input, Context(second, raw.Name), _otherKey, default, raw.Sha256, raw.Length);
        }

        [Test]
        public void CancellationAfterTheNativeBuildCannotSealACompleteEncryptedWorkspace()
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            using var cancellation = new CancellationTokenSource();
            var workspace = fixture.Workspace();
            Assert.Throws<OperationCanceledException>(() => fixture.Execute(keys, (output, builds, options, target) =>
            {
                var manifest = BuildPipeline.BuildAssetBundles(output, builds, options, target);
                cancellation.Cancel();
                return manifest;
            }, cancellation.Token));
            var marker = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Path.GetDirectoryName(workspace), ".ylr-workspace.json"))));
            Assert.That((bool)marker["Complete"], Is.False);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(workspace, Path.GetFileName(workspace)))), Is.True);
            keys.AssertReleasedCopies();
        }

        private static FieldInfo Field(string name)
        {
            var field = typeof(ResourceProject).GetField(name, Members);
            Assert.That(field, Is.Not.Null, "Missing G2 project content policy: " + name);
            return field;
        }

        private static PropertyInfo Property(Type type, string name)
        {
            var property = type.GetProperty(name, Members);
            Assert.That(property, Is.Not.Null, "Missing G2 delivery fact: " + type.Name + "." + name);
            return property;
        }

        private static T Value<T>(object value, string property) => (T)Property(value.GetType(), property).GetValue(value);
        private static string DeliveryPath(ResourceBuildResult build) => Value<string>(build, "DeliveryPath");
        private static IReadOnlyList<ResourceBuiltFile> DeliveryFiles(ResourceBuildResult build) => Value<IReadOnlyList<ResourceBuiltFile>>(build, "DeliveryFiles");
        private static string[] Facts(IEnumerable<ResourceBuiltFile> files) => files.Select(x => x.Name + ":" + x.Length + ":" + x.Sha256).ToArray();
        private static string TextBundle(ResourceBuildResult build) => build.Map.Assets.Single(x => x.AssetType == typeof(TextAsset)).BundleName;
        private static long ContentBytes(ResourceBuildResult build) => DeliveryFiles(build).Where(x => !x.Name.EndsWith(".manifest", StringComparison.Ordinal)).Sum(x => x.Length);
        private static ResourceBundleContext Context(ResourceBuildResult build, string name) => new(build.Map.ProjectId, build.Platform,
            build.UnityCompatibility, name == build.ManifestBundleFile ? "Manifest" : "Bundle", name == build.ManifestBundleFile ? "manifest" : name);

        private static MethodInfo Method(Type type, string name, params Type[] signature)
        {
            var method = type.GetMethod(name, Members, null, signature, null);
            Assert.That(method, Is.Not.Null, "Missing G2 operation: " + type.Name + "." + name);
            return method;
        }

        private static MethodInfo BuildMethod() => Method(typeof(ResourceBundleBuilder), "BuildWithContentKey", typeof(ResourceProject),
            typeof(BuildTarget), typeof(IResourceContentKeyProvider), typeof(bool), typeof(CancellationToken));
        private static MethodInfo PublishMethod() => Method(typeof(ResourcePublisher), "PublishWithContentKey", typeof(ResourceBuildResult),
            typeof(string), typeof(IResourceContentKeyProvider), typeof(CancellationToken));
        private static MethodInfo PublishExecuteMethod() => Method(typeof(ResourcePublisher), "ExecuteWithContentKey", typeof(ResourceBuildResult),
            typeof(string), typeof(CancellationToken), typeof(Action<string>), typeof(IResourceContentKeyProvider));

        private static T Invoke<T>(MethodInfo method, params object[] arguments)
        {
            try
            {
                return (T)method.Invoke(null, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static void AssertAuthenticationFailure(Exception error)
        {
            var causes = new List<Exception>();
            for (var cause = error; cause != null; cause = cause.InnerException)
            {
                causes.Add(cause);
            }

            Assert.That(causes.Any(x => x is CryptographicException || x is ResourceException resource &&
                resource.Kind == ResourceError.ContentAuthenticationFailed), Is.True, error?.ToString());
        }

        private static void AssertCatalogDelivery(ResourceBuildResult build, ResourceCatalogDocument catalog)
        {
            var json = JObject.Parse(System.Text.Encoding.UTF8.GetString(ResourceCatalogCodec.Write(catalog)));
            CollectionAssert.AreEquivalent(new[] { "SchemaVersion", "ProjectId", "Platform", "UnityCompatibility", "AppContentCompatibilityId",
                "BuildSettingsFingerprint", "ManifestFile", "Bundles", "Assets", "Groups", "SupportFiles", "BuildId" }, json.Properties().Select(x => x.Name));
            Assert.That((int)json["SchemaVersion"], Is.EqualTo(1));
            foreach (var file in DeliveryFiles(build))
            {
                var relative = file.Name == build.ManifestBundleFile ? "manifest" : file.Name == build.ManifestBundleFile + ".manifest" ? "manifest.manifest" :
                    "b_" + ResourceCatalogCodec.Digest(System.Text.Encoding.UTF8.GetBytes(file.Name.EndsWith(".manifest", StringComparison.Ordinal) ? file.Name[..^9] : file.Name))[..32] +
                    (file.Name.EndsWith(".manifest", StringComparison.Ordinal) ? ".manifest" : "");
                var record = catalog.Bundles.Cast<ResourceCatalogFile>().Concat(catalog.SupportFiles).Single(x => x.RelativeFile == relative);
                Assert.That(record.Length, Is.EqualTo(file.Length));
                Assert.That(record.Sha256, Is.EqualTo(file.Sha256));
            }
            foreach (var bundle in catalog.Bundles)
            {
                var raw = build.Bundles.Single(x => x.Name == bundle.LogicalId);
                Assert.That(bundle.UnityHash, Is.EqualTo(raw.UnityHash));
                CollectionAssert.AreEqual(raw.Dependencies, bundle.Dependencies);
            }
        }

        private sealed class ContentBuildFixture : IDisposable
        {
            internal readonly ResourceReleaseFixture Source;
            private readonly HashSet<string> _roots = new(StringComparer.Ordinal);

            internal ContentBuildFixture()
            {
                // 缺能力时先明确 RED，不先执行昂贵 native 构建或建立夹具资产。
                BuildMethod();
                Field("contentEncryption");
                Field("contentKeyId");
                Source = new ResourceReleaseFixture();
            }

            internal void Configure(ResourceContentEncryption mode = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1, string keyId = KeyId)
            {
                var project = Source.Project;
                Field("contentEncryption").SetValue(project, mode);
                Field("contentKeyId").SetValue(project, keyId);
                EditorUtility.SetDirty(project);
                AssetDatabase.SaveAssetIfDirty(project);
            }

            internal void ConfigureLargeText()
            {
                Configure();
                var project = Source.Project;
                project.compression = ResourceCompression.Uncompressed;
                EditorUtility.SetDirty(project);
                AssetDatabase.SaveAssetIfDirty(project);
                var bytes = new byte[240000];
                new System.Random(20604).NextBytes(bytes);
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Source.TextPath), Convert.ToBase64String(bytes));
                AssetDatabase.ImportAsset(Source.TextPath, ImportAssetOptions.ForceSynchronousImport);
            }

            internal TestKeys Keys(string keyId = KeyId, byte[] material = null, bool missing = false, bool throws = false) =>
                new(Source.Project.projectId, keyId, material ?? _testKey)
                {
                    Missing = missing,
                    Throw = throws
                };

            internal string Workspace()
            {
                var path = ResourceBundleBuilder.GetWorkspacePath(Source.Project, EditorUserBuildSettings.activeBuildTarget);
                var root = Path.GetDirectoryName(path);
                if (_roots.Add(root))
                {
                    Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)), Is.False, "Do not adopt a pre-existing fixture workspace.");
                }

                return path;
            }

            internal ResourceBuildResult BuildPlain()
            {
                Workspace();
                return ResourceBundleBuilder.Build(Source.Project, EditorUserBuildSettings.activeBuildTarget);
            }

            internal ResourceBuildResult Build(IResourceContentKeyProvider keys, bool force = false)
            {
                Workspace();
                return Invoke<ResourceBuildResult>(BuildMethod(), Source.Project, EditorUserBuildSettings.activeBuildTarget, keys, force, default(CancellationToken));
            }

            internal ResourceBuildResult Execute(IResourceContentKeyProvider keys,
                Func<string, AssetBundleBuild[], BuildAssetBundleOptions, BuildTarget, AssetBundleManifest> pipeline, CancellationToken token = default)
            {
                Workspace();
                var method = Method(typeof(ResourceBundleBuilder), "ExecuteWithContentKey", typeof(ResourceProject), typeof(BuildTarget), typeof(bool),
                    typeof(CancellationToken), pipeline.GetType(), typeof(IResourceContentKeyProvider));
                return Invoke<ResourceBuildResult>(method, Source.Project, EditorUserBuildSettings.activeBuildTarget, false, token, pipeline, keys);
            }

            internal ResourceRelease Publish(ResourceBuildResult build, IResourceContentKeyProvider keys, CancellationToken token = default, Action<string> checkpoint = null)
            {
                OwnRelease(build);
                return checkpoint == null ? Invoke<ResourceRelease>(PublishMethod(), build, App, keys, token) :
                    Invoke<ResourceRelease>(PublishExecuteMethod(), build, App, token, checkpoint, keys);
            }

            internal void OwnRelease(ResourceBuildResult build)
            {
                var root = ResourcePublisher.GetReleaseRoot(build);
                if (Source.ReleaseRoot == null)
                {
                    Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)), Is.False, "Do not adopt an existing release root.");
                    Source.ReleaseRoot = root;
                }
            }

            public void Dispose()
            {
                try
                {
                    Source.AssertProtected();
                    foreach (var root in _roots)
                    {
                        if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
                        {
                            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
                        }
                    }
                }
                finally
                {
                    Source.Dispose();
                }
            }
        }

        private sealed class TestKeys : IResourceContentKeyProvider, IDisposable
        {
            private readonly string _project;
            private readonly string _keyId;
            private readonly byte[] _material;
            private readonly int _mainThread = Thread.CurrentThread.ManagedThreadId;
            private readonly List<byte[]> _copies = new();
            internal int Copies => _copies.Count;
            internal bool Missing;
            internal bool Throw;
            internal bool Disposed;

            internal TestKeys(string project, string keyId, byte[] material)
            {
                this._project = project;
                this._keyId = keyId;
                this._material = material;
            }
            public bool TryCopyKey(string projectId, string requestedKey, byte[] destination)
            {
                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(_mainThread), "Application key providers run on the initiating Editor thread.");
                Assert.That(projectId, Is.EqualTo(_project));
                Assert.That(requestedKey, Is.EqualTo(_keyId));
                Assert.That(destination.Length, Is.EqualTo(32));
                Assert.That(Disposed, Is.False);
                _copies.Add(destination);
                if (Throw)
                {
                    throw new InvalidOperationException("g2-sensitive-provider-material");
                }

                if (Missing)
                {
                    return false;
                }

                Array.Copy(_material, destination, _material.Length);
                return true;
            }
            internal void AssertReleasedCopies() => Assert.That(_copies.All(x => x.All(value => value == 0)), Is.True, "The operation must clear each key copy it owns.");
            public void Dispose() => Disposed = true;
        }
    }
}
