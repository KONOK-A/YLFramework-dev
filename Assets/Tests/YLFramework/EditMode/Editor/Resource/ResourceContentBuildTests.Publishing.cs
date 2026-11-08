using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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
    public partial class ResourceContentBuildTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void CatalogCreationRechecksBothRawAndDeliverySnapshots(bool delivery)
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var build = fixture.Build(keys);
            File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(delivery ? DeliveryPath(build) : build.WorkspacePath, TextBundle(build))), "owned corruption");
            Assert.Throws<InvalidDataException>(() => ResourceCatalogBuilder.Create(build, App));
        }

        [Test]
        public void UnknownDeliveryContentIsNotAdoptedOrOverwrittenOnAnotherBuild()
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var build = fixture.Build(keys);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DeliveryPath(build), "foreign.txt")), "do not adopt");
            var before = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(build.WorkspacePath));
            Assert.Throws<InvalidOperationException>(() => fixture.Build(keys));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(build.WorkspacePath)));
        }

        [Test]
        public void EncryptedPublicationContainsOnlyDeliveryV1FilesAndSupportsIndependentNativeInputs()
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var build = fixture.Build(keys);
            var rawBefore = ResourceReleaseFixture.Snapshot(build.WorkspacePath);
            var stageVerified = false;
            var release = fixture.Publish(build, keys, checkpoint: step =>
            {
                if (step != "StageVerified")
                {
                    return;
                }

                stageVerified = true;
                Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty, "Staged native owners must be unloaded before inputs are released.");
                var stage = Directory.GetDirectories(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.Source.ReleaseRoot, ".staging"))).Single();
                foreach (var name in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(stage)))
                {
                    using var exclusive = File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            });
            Assert.That(stageVerified, Is.True);
            AssertCatalogDelivery(build, release.Catalog);
            CollectionAssert.AreEquivalent(release.Catalog.Bundles.Select(x => x.RelativeFile).Concat(release.Catalog.SupportFiles.Select(x => x.RelativeFile))
                .Concat(new[] { "resource-catalog.json", ".ylr-complete.json" }), Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(release.DirectoryPath)).Select(Path.GetFileName));
            foreach (var file in DeliveryFiles(build))
            {
                var published = ResourceCatalogBuilder.PublishedFile(build, file.Name);
                CollectionAssert.AreEqual(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DeliveryPath(build), file.Name))), File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(release.DirectoryPath, published))));
            }
            AssertNativeSnapshot(release, build);
            CollectionAssert.AreEqual(rawBefore, ResourceReleaseFixture.Snapshot(build.WorkspacePath));
            var indexBefore = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(release.RootPath, "release-index.json")));
            var versionBefore = ResourceReleaseFixture.Snapshot(release.DirectoryPath);
            var repeat = fixture.Publish(build, keys);
            Assert.That(repeat.IsRepeat, Is.True);
            Assert.That(repeat.CopiedBytes, Is.Zero);
            CollectionAssert.AreEqual(indexBefore, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(release.RootPath, "release-index.json"))));
            CollectionAssert.AreEqual(versionBefore, ResourceReleaseFixture.Snapshot(release.DirectoryPath));
            keys.AssertReleasedCopies();
            Assert.That(keys.Disposed, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepeatPublicationRevalidatesTheCurrentKeyEvenForAnAlreadyBuiltAndPublishedResult(bool wrong)
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var build = fixture.Build(keys);
            var first = fixture.Publish(build, keys);
            var before = ResourceReleaseFixture.Snapshot(first.RootPath);
            using var rejected = fixture.Keys(material: wrong ? _otherKey : _testKey, missing: !wrong);
            var error = Assert.Catch(() => fixture.Publish(build, rejected));
            if (wrong)
            {
                AssertAuthenticationFailure(error);
            }
            else
            {
                Assert.That(error, Is.InstanceOf<ResourceException>());
                Assert.That(((ResourceException)error).Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            }
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(first.RootPath));
            rejected.AssertReleasedCopies();
        }

        [Test]
        public void PublicationFullyAuthenticatesMiddleSegmentsInsteadOfAcceptingOnlyShaAndFirstLastProbes()
        {
            using var fixture = new ContentBuildFixture();
            fixture.ConfigureLargeText();
            using var keys = fixture.Keys();
            var first = fixture.Publish(fixture.Build(keys), keys);
            ChangeText(fixture, "\nnext generation");
            var build = fixture.Build(keys);
            CorruptMiddleSegment(build);
            var before = ResourceReleaseFixture.Snapshot(first.DirectoryPath);
            var index = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(first.RootPath, "release-index.json")));
            AssertAuthenticationFailure(Assert.Catch(() => fixture.Publish(build, keys)));
            AssertUncommitted(first, before, index);
            foreach (var stage in Directory.GetDirectories(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(first.RootPath, ".staging"))))
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(stage, ".ylr-complete.json"))), Is.False);
            }
        }

        [TestCase("Bundle")]
        [TestCase("Manifest")]
        public void PublicationCrossChecksAuthenticatedPlainIdentityAgainstTheIndependentRawSnapshot(string role)
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var first = fixture.Publish(fixture.Build(keys), keys);
            ChangeText(fixture, "\nindependent-raw-check");
            var build = fixture.Build(keys);
            var name = role == "Manifest" ? build.ManifestBundleFile : TextBundle(build);
            ReplaceContent(build, name, Encoding.ASCII.GetBytes("UnityFS\0authenticated but not this raw build"), replaceRaw: false);
            var before = ResourceReleaseFixture.Snapshot(first.DirectoryPath);
            var index = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(first.RootPath, "release-index.json")));
            Assert.Throws<InvalidDataException>(() => fixture.Publish(build, keys));
            AssertUncommitted(first, before, index);
        }

        [TestCase("Bundle")]
        [TestCase("Manifest")]
        public void StagedNativeReadbackIsRequiredEvenWhenDeliveryAndRawAreMutuallyAuthenticated(string role)
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var first = fixture.Publish(fixture.Build(keys), keys);
            ChangeText(fixture, "\nrequires-stage-native-check");
            var build = fixture.Build(keys);
            using var other = new ContentBuildFixture();
            var foreign = other.BuildPlain();
            var name = role == "Manifest" ? build.ManifestBundleFile : TextBundle(build);
            var foreignName = role == "Manifest" ? foreign.ManifestBundleFile : TextBundle(foreign);
            // 双层length/SHA/context及AEAD都自洽，只有独立stage native身份/内容读回能发现这个过期BuildResult。
            ReplaceContent(build, name, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(foreign.WorkspacePath, foreignName))), replaceRaw: true);
            var raw = build.Files.Single(x => x.Name == name);
            using (var verified = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DeliveryPath(build), name))))
            {
                ResourceBundleEnvelope.ValidateAll(verified, Context(build, name), _testKey, default, raw.Sha256, raw.Length);
            }

            var reachedStage = false;
            var before = ResourceReleaseFixture.Snapshot(first.DirectoryPath);
            var index = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(first.RootPath, "release-index.json")));
            Assert.Throws<InvalidDataException>(() => fixture.Publish(build, keys, checkpoint: step =>
            {
                if (step == "CatalogWritten")
                {
                    reachedStage = true;
                }

                if (step == "StageVerified")
                {
                    Assert.Fail("A different native Bundle must not be sealed as this verified build.");
                }
            }));
            Assert.That(reachedStage, Is.True, "This fixture must reach stage verification, not merely fail its delivery SHA check.");
            AssertUncommitted(first, before, index);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
        }

        [TestCase("CatalogWritten", true)]
        [TestCase("StageVerified", false)]
        [TestCase("BeforeIndex", false)]
        public void EncryptedCancellationAndFaultsKeepTheOldIndexAndUseTheOriginalKeyFreeRecovery(string fault, bool cancel)
        {
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var first = fixture.Publish(fixture.Build(keys), keys);
            ChangeText(fixture, "\nnext encrypted release");
            var next = fixture.Build(keys);
            var before = ResourceReleaseFixture.Snapshot(first.DirectoryPath);
            var index = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(first.RootPath, "release-index.json")));
            using var cancellation = new CancellationTokenSource();
            var error = Assert.Catch(() => fixture.Publish(next, keys, cancellation.Token, step =>
            {
                if (step != fault)
                {
                    return;
                }

                if (cancel)
                {
                    cancellation.Cancel();
                }
                else
                {
                    throw new IOException("Owned G2 fault at " + fault);
                }
            }));
            Assert.That(error, cancel ? Is.InstanceOf<OperationCanceledException>() : Is.InstanceOf<IOException>());
            AssertUncommitted(first, before, index);
            var copies = keys.Copies;
            keys.Throw = true;
            var recovered = ResourcePublisher.Recover(fixture.Source.Project, EditorUserBuildSettings.activeBuildTarget, App);
            Assert.That(recovered.Catalog.CatalogDigest, Is.EqualTo(first.Catalog.CatalogDigest));
            Assert.That(keys.Copies, Is.EqualTo(copies));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(first.RootPath, ".ylr-publish-journal.json"))), Is.False);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
        }

        [Test]
        public void EditorOperationsReportMissingKeysAndForwardAnExplicitProviderThroughBuildAndPublication()
        {
            var buildMethod = Method(typeof(ResourceEditorOperations), "BuildOnly", typeof(ResourceProject), typeof(string), typeof(bool),
                typeof(CancellationToken), typeof(IResourceContentKeyProvider));
            var publishMethod = Method(typeof(ResourceEditorOperations), "PublishBuild", typeof(ResourceBuildResult), typeof(string), typeof(bool),
                typeof(string), typeof(CancellationToken), typeof(IResourceContentKeyProvider));
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            var workspace = fixture.Workspace();
            using var missing = fixture.Keys(missing: true);
            var failed = Invoke<ResourceEditorBuildReport>(buildMethod, fixture.Source.Project, App, false, default(CancellationToken), missing);
            Assert.That(failed.Build, Is.Null);
            Assert.That(failed.BuildError, Is.InstanceOf<ResourceException>());
            Assert.That(((ResourceException)failed.BuildError).Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(workspace))), Is.False);
            using var keys = fixture.Keys();
            var built = Invoke<ResourceEditorBuildReport>(buildMethod, fixture.Source.Project, App, false, default(CancellationToken), keys);
            Assert.That(built.BuildError, Is.Null, built.Summary);
            Assert.That(built.Build, Is.Not.Null, built.Summary);
            fixture.OwnRelease(built.Build);
            var published = Invoke<ResourceEditorBuildReport>(publishMethod, built.Build, App, false, "Assets/YLFrameworkData", default(CancellationToken), keys);
            Assert.That(published.PublicationError, Is.Null, published.Summary);
            Assert.That(published.Release, Is.Not.Null, published.Summary);
            Assert.That(published.IsGenerationRequested, Is.False);
            keys.AssertReleasedCopies();
            missing.AssertReleasedCopies();
        }

        [Test]
        public void EnvironmentKeyAdapterUsesOnlyAnExplicitBoundSecretNameAndNeverEchoesItsValue()
        {
            var method = Method(typeof(ResourceEditorOperations), "ContentKeyFromEnvironment", typeof(string), typeof(string), typeof(string));
            var variable = "YL_G2_TEST_CONTENT_" + Guid.NewGuid().ToString("N");
            var destination = new byte[32];
            try
            {
                var provider = Invoke<IResourceContentKeyProvider>(method, "g2-env-fixture", KeyId, variable);
                Assert.That(provider.TryCopyKey("g2-env-fixture", KeyId, destination), Is.False);
                Environment.SetEnvironmentVariable(variable, Convert.ToBase64String(_testKey));
                Assert.That(provider.TryCopyKey("other-project", KeyId, destination), Is.False);
                Assert.That(provider.TryCopyKey("g2-env-fixture", NextKeyId, destination), Is.False);
                Assert.That(provider.TryCopyKey("g2-env-fixture", KeyId, destination), Is.True);
                CollectionAssert.AreEqual(_testKey, destination);
                foreach (var value in new[] { "g2-not-base64-sensitive-material", Convert.ToBase64String(new byte[31]), Convert.ToBase64String(new byte[33]) })
                {
                    Array.Clear(destination, 0, destination.Length);
                    Environment.SetEnvironmentVariable(variable, value);
                    var error = Assert.Catch(() => provider.TryCopyKey("g2-env-fixture", KeyId, destination));
                    Assert.That(error.ToString(), Does.Not.Contain(value));
                    Assert.That(destination.All(x => x == 0), Is.True);
                }
            }
            finally
            {
                Array.Clear(destination, 0, destination.Length);
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        [Test]
        public void SigningEncryptedBytesUsesTheActualInspectedFilesWithoutASeparateLegacyClientConfirmation()
        {
            var method = Method(typeof(ResourceReleaseSigner), "Create", typeof(ResourceRelease), typeof(string), typeof(long), typeof(string),
                typeof(RSA), typeof(DateTimeOffset));
            Assert.That(typeof(ResourceReleaseSigner).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(candidate => candidate.Name == "Create").SelectMany(candidate => candidate.GetParameters()).Any(parameter => parameter.ParameterType == typeof(bool)), Is.False);
            using var fixture = new ContentBuildFixture();
            fixture.Configure();
            using var keys = fixture.Keys();
            var release = fixture.Publish(fixture.Build(keys), keys);
            var before = ResourceReleaseFixture.Snapshot(release.DirectoryPath);
            fixture.Configure(ResourceContentEncryption.None, "");
            using var rsa = new RSACryptoServiceProvider(2048);
            var expires = DateTimeOffset.UtcNow.AddDays(1);
            var bytes = Invoke<byte[]>(method, release, "g2-e04", 2L, "signing-test-only", rsa, expires);
            var payload = ResourceReleaseSignature.Verify(bytes, "signing-test-only", rsa.ExportParameters(false));
            CollectionAssert.AreEquivalent(new[] { "ProjectId", "Platform", "UnityCompatibility", "AppContentCompatibilityId", "Channel", "ReleaseSequence",
                "CatalogDigest", "CatalogLength", "BuildId", "CompleteLength", "CompleteSha256", "IssuedUnixSeconds", "ExpiresUnixSeconds" }, payload.Properties().Select(x => x.Name));
            Assert.That((string)payload["CatalogDigest"], Is.EqualTo(release.Catalog.CatalogDigest));
            Assert.That((string)payload["Channel"], Is.EqualTo("g2-e04"));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(release.DirectoryPath));
        }

        private static void ChangeText(ContentBuildFixture fixture, string suffix)
        {
            File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Source.TextPath), suffix);
            AssetDatabase.ImportAsset(fixture.Source.TextPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void AssertUncommitted(ResourceRelease first, string[] versionBefore, byte[] indexBefore)
        {
            CollectionAssert.AreEqual(indexBefore, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(first.RootPath, "release-index.json"))));
            CollectionAssert.AreEqual(versionBefore, ResourceReleaseFixture.Snapshot(first.DirectoryPath));
        }

        private static void CorruptMiddleSegment(ResourceBuildResult build)
        {
            var name = TextBundle(build);
            var cipher = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DeliveryPath(build), name)));
            var payload = 12 + BitConverter.ToInt32(cipher, 8);
            Assert.That(cipher.Length - payload, Is.GreaterThan(3 * 65536), "The chosen corruption must not touch the first or last segment.");
            cipher[payload + 65536 + 23] ^= 1;
            ReplaceDeliveryFile(build, name, cipher);
            using var input = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DeliveryPath(build), name)));
            using (var probe = ResourceBundleEnvelope.OpenDecrypt(input, Context(build, name), _testKey, true))
            {
                Assert.That(probe.Length, Is.GreaterThan(2 * 65536));
            }

            Assert.Throws<IOException>(() => ResourceBundleEnvelope.ValidateAll(input, Context(build, name), _testKey));
        }

        private static void ReplaceContent(ResourceBuildResult build, string name, byte[] rawBytes, bool replaceRaw)
        {
            using var raw = new MemoryStream(rawBytes);
            using var encrypted = new MemoryStream();
            ResourceBundleEnvelope.Encrypt(raw, encrypted, Context(build, name), KeyId, _testKey);
            ReplaceDeliveryFile(build, name, encrypted.ToArray());
            if (!replaceRaw)
            {
                return;
            }

            var previous = build.Files.Single(x => x.Name == name);
            var replacement = FileFact(name, rawBytes);
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(build.WorkspacePath, name)), rawBytes);
            RewriteRecordedDigest(build, previous, replacement);
            build.Files = build.Files.Select(x => x.Name == name ? replacement : x).ToList().AsReadOnly();
            build.Bundles = build.Bundles.Select(x => x.Name == name ? new ResourceBuiltBundle(build.Map.Bundles.Single(b => b.Name == name),
                x.UnityHash, x.Dependencies.ToArray(), replacement.Length) : x).ToList().AsReadOnly();
        }

        private static ResourceBuiltFile FileFact(string name, byte[] bytes) => new(new ResourceBuildWorkspace.FileDigest
        {
            Name = name,
            Length = bytes.LongLength,
            Sha256 = ResourceCatalogCodec.Digest(bytes)
        });

        private static void ReplaceDeliveryFile(ResourceBuildResult build, string name, byte[] bytes)
        {
            var previous = DeliveryFiles(build).Single(x => x.Name == name);
            var replacement = FileFact(name, bytes);
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DeliveryPath(build), name)), bytes);
            RewriteRecordedDigest(build, previous, replacement);
            var files = DeliveryFiles(build).Select(x => x.Name == name ? replacement : x).ToList().AsReadOnly();
            Property(typeof(ResourceBuildResult), "DeliveryFiles").GetSetMethod(true).Invoke(build, new object[] { files });
            Property(typeof(ResourceBuildResult), "DeliveryBytes").GetSetMethod(true).Invoke(build, new object[] { files.Sum(x => x.Length) });
        }

        private static void RewriteRecordedDigest(ResourceBuildResult build, ResourceBuiltFile before, ResourceBuiltFile after)
        {
            var path = Path.Combine(Path.GetDirectoryName(build.WorkspacePath), ".ylr-workspace.json");
            var record = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            var digests = record.Descendants().OfType<JValue>().Where(x => x.Type == JTokenType.String && (string)x == before.Sha256).ToArray();
            Assert.That(digests.Length, Is.GreaterThan(0), "The explicit v2 workspace must register the candidate delivery identity.");
            foreach (var digest in digests)
            {
                var property = (JProperty)digest.Parent;
                var owner = (JObject)property.Parent;
                var lengthName = property.Name.Replace("Sha256", "Length");
                if (lengthName != property.Name && owner[lengthName] != null)
                {
                    owner[lengthName] = after.Length;
                }

                digest.Value = after.Sha256;
            }
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), record.ToString(Formatting.Indented));
        }

        private static void AssertNativeSnapshot(ResourceRelease release, ResourceBuildResult build)
        {
            var inputs = new List<(AssetBundle Bundle, Stream Input)>();
            var bundles = new Dictionary<string, AssetBundle>(StringComparer.Ordinal);
            AssetBundle Load(string relative, string rawName)
            {
                var verified = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(release.DirectoryPath, relative)));
                Stream input = verified;
                try
                {
                    input = ResourceBundleEnvelope.OpenDecrypt(verified, Context(build, rawName), _testKey);
                    var bundle = AssetBundle.LoadFromStream(input, 0, 32768);
                    Assert.That(bundle, Is.Not.Null);
                    inputs.Add((bundle, input));
                    return bundle;
                }
                catch
                {
                    input.Dispose();
                    if (!ReferenceEquals(input, verified))
                    {
                        verified.Dispose();
                    }
                    throw;
                }
            }
            try
            {
                var root = Load(release.Catalog.ManifestFile, build.ManifestBundleFile);
                var manifest = root.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                CollectionAssert.AreEqual(build.Bundles.Select(x => x.Name), manifest.GetAllAssetBundles().OrderBy(x => x, StringComparer.Ordinal));
                while (bundles.Count < release.Catalog.Bundles.Count)
                {
                    var batch = release.Catalog.Bundles.Where(x => !bundles.ContainsKey(x.LogicalId) && x.Dependencies.All(bundles.ContainsKey)).ToArray();
                    Assert.That(batch, Is.Not.Empty);
                    foreach (var entry in batch)
                    {
                        var bundle = Load(entry.RelativeFile, entry.LogicalId);
                        Assert.That(bundle.name, Is.EqualTo(entry.NativeName));
                        Assert.That(manifest.GetAssetBundleHash(entry.NativeName).ToString(), Is.EqualTo(entry.UnityHash));
                        CollectionAssert.AreEqual(entry.Dependencies, manifest.GetDirectDependencies(entry.NativeName).OrderBy(x => x, StringComparer.Ordinal));
                        bundles.Add(entry.LogicalId, bundle);
                    }
                }
                var text = release.Catalog.Assets.Single(x => x.AssetType == typeof(TextAsset).FullName);
                Assert.That(bundles[text.BundleId].LoadAsset<TextAsset>(text.LoadName).text, Is.EqualTo("first release content"));
                var prefabs = release.Catalog.Assets.Where(x => x.AssetType == typeof(GameObject).FullName).Select(x =>
                    bundles[x.BundleId].LoadAsset<GameObject>(x.LoadName)).ToArray();
                Assert.That(prefabs.Length, Is.EqualTo(2));
                Assert.That(prefabs[0].GetComponent<MeshRenderer>().sharedMaterial.mainTexture,
                    Is.SameAs(prefabs[1].GetComponent<MeshRenderer>().sharedMaterial.mainTexture));
                foreach (var sprite in release.Catalog.Assets.Where(x => x.Kind == "SubAsset"))
                {
                    Assert.That(bundles[sprite.BundleId].LoadAssetWithSubAssets<Sprite>(sprite.LoadName).Count(x => x.name == sprite.SubAssetName), Is.EqualTo(1));
                }

                var scene = release.Catalog.Assets.Single(x => x.Kind == "Scene");
                CollectionAssert.Contains(bundles[scene.BundleId].GetAllScenePaths(), scene.LoadName);
            }
            finally
            {
                for (var i = inputs.Count - 1; i >= 0; i--)
                {
                    if (inputs[i].Bundle)
                    {
                        inputs[i].Bundle.Unload(true);
                    }

                    inputs[i].Input.Dispose();
                }
            }
        }
    }
}
