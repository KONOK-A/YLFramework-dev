using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Audio;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceContentRuntimeTests
    {
        private const string Key = "YL.E04.FormalRuntime.";
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        [Serializable]
        private sealed class Layout
        {
            public SceneSetup[] Scenes;
        }

        [OneTimeSetUp]
        public void BuildAndPublishFormalEncryptedVersions()
        {
            if (SessionState.GetBool(Key + "owned", false))
            {
                return;
            }

            if (Application.isPlaying)
            {
                throw new InvalidOperationException("Start this owned fixture in EditMode.");
            }

            YLFramework.Tests.Resource.ResourceSessionTests.EnsureRestorableScenes();
            SessionState.SetString(Key + "layout", JsonUtility.ToJson(new Layout { Scenes = EditorSceneManager.GetSceneManagerSetup() }));
            using var fixture = new ResourceReleaseFixture();
            AddConsumers(fixture);
            fixture.Project.groups.RemoveAll(x => x.id == "A" || x.id == Path.GetFileNameWithoutExtension(fixture.TextPath));
            fixture.Project.groups.Add(new ResourceGroup
            {
                id = "Mixed",
                packing = ResourcePacking.PerAsset,
                selections = new List<ResourceSelection> {
                new() { guid = AssetDatabase.AssetPathToGUID(fixture.Folder + "/Content/A.prefab") },
                new() { guid = AssetDatabase.AssetPathToGUID(fixture.TextPath) } }
            });
            typeof(ResourceProject).GetField("contentEncryption").SetValue(fixture.Project, ResourceContentEncryption.Aes256GcmHkdfSegmentedV1);
            typeof(ResourceProject).GetField("contentKeyId").SetValue(fixture.Project, "e04-test-v1");
            EditorUtility.SetDirty(fixture.Project);
            AssetDatabase.SaveAssetIfDirty(fixture.Project);
            Assert.That(ResourceEditorOperations.PrepareAtlases(fixture.Project), Is.EqualTo(1));
            var workspace = Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(fixture.Project, EditorUserBuildSettings.activeBuildTarget));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace)), Is.False, "Do not adopt a previous test workspace.");
            SessionState.SetString(Key + "workspace", workspace);
            using var keys = new TestKeys();
            var first = Build(fixture.Project, keys);
            var release = Publish(first, keys);
            fixture.ReleaseRoot = release.RootPath;
            fixture.RetainRelease = true;
            SessionState.SetString(Key + "root", release.RootPath);
            SessionState.SetString(Key + "workspace", Path.GetDirectoryName(first.WorkspacePath));
            SessionState.SetString(Key + "v1", release.DirectoryPath);
            SessionState.SetString(Key + "project", first.Map.ProjectId);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.TextPath), "second formal encrypted version");
            AssetDatabase.ImportAsset(fixture.TextPath, ImportAssetOptions.ForceSynchronousImport);
            var next = Publish(Build(fixture.Project, keys), keys);
            SessionState.SetString(Key + "v2", next.DirectoryPath);
            SessionState.SetString(Key + "cache", Path.Combine(Path.GetTempPath(), "kilo", "e4-" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scenePath = "Temp/E04FormalRuntime-" + Guid.NewGuid().ToString("N") + ".unity";
            Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
            SessionState.SetString(Key + "scene", scenePath);
            SessionState.SetBool(Key + "owned", true);
        }

        private static ResourceBuildResult Build(ResourceProject project, IResourceContentKeyProvider keys) => (ResourceBuildResult)Invoke(
            typeof(ResourceBundleBuilder), "BuildWithContentKey", project, EditorUserBuildSettings.activeBuildTarget, keys, false, default(System.Threading.CancellationToken));

        private static ResourceRelease Publish(ResourceBuildResult result, IResourceContentKeyProvider keys) => (ResourceRelease)Invoke(
            typeof(ResourcePublisher), "PublishWithContentKey", result, "app-v1", keys, default(System.Threading.CancellationToken));

        private static object Invoke(Type type, string method, params object[] arguments)
        {
            var found = type.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(found, Is.Not.Null, "Missing formal product operation: " + method);
            try
            {
                return found.Invoke(null, arguments);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static void AddConsumers(ResourceReleaseFixture fixture)
        {
            var folder = fixture.Folder + "/Content";
            var atlasSprites = folder + "/AtlasSprites.png";
            Assert.That(AssetDatabase.CopyAsset(folder + "/Sprites.png", atlasSprites), Is.True);
            var atlas = ResourceAtlasTests.AddAtlas(fixture, spritePath: atlasSprites);
            var audio = folder + "/Owned.wav";
            using (var file = File.Create(global::YLFramework.Testing.PackageTestPaths.Physical(audio)))
            using (var writer = new BinaryWriter(file))
            {
                const int Samples = 44100;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + Samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(44100);
                writer.Write(88200);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(Samples * 2);
                for (var i = 0; i < Samples; i++)
                {
                    writer.Write((short)(Math.Sin(i * 0.07) * 1000));
                }
            }
            AssetDatabase.ImportAsset(audio, ImportAssetOptions.ForceSynchronousImport);
            var importer = (AudioImporter)AssetImporter.GetAtPath(audio);
            var settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.Streaming;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
            var nextAudio = folder + "/Next.wav";
            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(audio), global::YLFramework.Testing.PackageTestPaths.Physical(nextAudio));
            AssetDatabase.ImportAsset(nextAudio, ImportAssetOptions.ForceSynchronousImport);
            var nextImporter = (AudioImporter)AssetImporter.GetAtPath(nextAudio);
            nextImporter.defaultSampleSettings = settings;
            nextImporter.SaveAndReimport();
            var cold = folder + "/Cold.bytes";
            var coldBytes = new byte[8 * 1024 * 1024];
            new System.Random(88461).NextBytes(coldBytes);
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(cold), coldBytes);
            AssetDatabase.ImportAsset(cold, ImportAssetOptions.ForceSynchronousImport);
            foreach (var path in new[] { atlas, audio, nextAudio, cold })
            {
                fixture.Project.groups.Add(new ResourceGroup
                {
                    id = Path.GetFileName(path),
                    selections = new List<ResourceSelection> { new() { guid = AssetDatabase.AssetPathToGUID(path) } }
                });
            }
        }

        private static ResourceLocalSnapshot Snapshot(bool next = false)
        {
            var path = SessionState.GetString(Key + (next ? "v2" : "v1"), "");
            return ResourceLocalSnapshot.Open(path, SessionState.GetString(Key + "project", ""), Path.GetFileName(path), "StandaloneWindows64", "app-v1");
        }

        private static ResourceAddress Address(string address) => new(SessionState.GetString(Key + "project", ""), address);
        private static int NativeCount(ResourceSystem system)
        {
            var snapshot = system.GetDiagnosticsSnapshot();
            return snapshot.NativeBundleLoadCount - snapshot.NativeBundleUnloadCount;
        }
        private static ResourceConfiguration Configuration(TestKeys keys, long maximum = 128L * 1024 * 1024) =>
            new(new[] { Snapshot().CreateCatalog() }, decryption: new ResourceDecryptionOptions(keys, maximum));
        private static ResourceContentBudget Budget(ResourceSystem system)
        {
            var field = typeof(ResourceSystem).GetField("_contentBudget", Hidden);
            Assert.That(field, Is.Not.Null, "The original resource subject must own its content budget.");
            return (ResourceContentBudget)field.GetValue(system);
        }

        [UnityTest]
        public IEnumerator InitializationAuthenticatesOnlyTheSelectedKeyBeforeSuccessAndJoinsOnce()
        {
            yield return new EnterPlayMode();
            using var keys = new TestKeys();
            var config = Configuration(keys);
            using var first = ResourceSystem.InitializeAsync(config);
            using var joined = ResourceSystem.InitializeAsync(config);
            yield return first;
            yield return joined;
            var system = first.TakeResult();
            Assert.That(joined.TakeResult(), Is.SameAs(system));
            Assert.That(keys.Copies, Is.EqualTo(1), "F3 probes one selected project/key pair once, not historical keys or per caller.");
            Assert.That(system.IsAvailable, Is.True);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            Assert.That(Budget(system).AllocatedBytes + Budget(system).ReservedBytes, Is.Zero);
        }

        [UnityTest]
        public IEnumerator MissingInitializationKeyCannotBecomeReadyOrLoadNative()
        {
            yield return new EnterPlayMode();
            using var keys = new TestKeys { Missing = true };
            using var initialize = ResourceSystem.InitializeAsync(Configuration(keys));
            yield return initialize;
            Assert.That(initialize.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(initialize.Error.Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator SharedAsyncProducerSurvivesOneConsumerCancellationAndReadyReentry()
        {
            yield return new EnterPlayMode();
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(Configuration(keys));
            yield return initialize;
            var system = initialize.TakeResult();
            using var canceled = system.AcquireAsync<GameObject>(Address("A.prefab"));
            using var survivor = system.AcquireAsync<GameObject>(Address("A.prefab"));
            canceled.Cancel();
            yield return survivor;
            using var lease = survivor.TakeResult();
            Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            using var ready = system.Acquire<GameObject>(Address("A.prefab"));
            Assert.That(ready.Asset, Is.SameAs(lease.Asset));
            Assert.That(system.GetDiagnosticsSnapshot().RunningBundleCount, Is.Zero);
            Assert.That(Budget(system).ReservedBytes, Is.Zero);
            Assert.That(Budget(system).LiveInputs, Is.EqualTo(NativeCount(system)));
            Assert.That(Budget(system).AllocatedBytes, Is.EqualTo(Budget(system).LiveInputs * ResourceDecryptStream.DataBufferBytes));
        }

        [UnityTest]
        public IEnumerator ClosureBudgetFailureIsAtomicAndRequiresAnExplicitRetryAfterRelease()
        {
            yield return new EnterPlayMode();
            yield return ClosureBudgetBody();
        }

        private static IEnumerator ClosureBudgetBody()
        {
            var snapshot = Snapshot();
            var root = snapshot.Document.Assets.Single(x => x.Address == "A.prefab").BundleId;
            var closure = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string id)
            {
                if (!closure.Add(id))
                {
                    return;
                }
                foreach (var dependency in snapshot.Document.Bundles.Single(x => x.LogicalId == id).Dependencies)
                {
                    Visit(dependency);
                }
            }
            Visit(root);
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(Configuration(keys, closure.Count * ResourceDecryptStream.DataBufferBytes));
            yield return initialize;
            var system = initialize.TakeResult();
            var text = system.Acquire<TextAsset>(Address("entry.txt"));
            var before = NativeCount(system);
            using (var rejected = system.AcquireAsync<GameObject>(Address("A.prefab")))
            {
                yield return rejected;
                Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.ContentBufferUnavailable));
            }
            Assert.That(NativeCount(system), Is.EqualTo(before));
            Assert.That(Budget(system).ReservedBytes, Is.Zero);
            var sync = Assert.Throws<ResourceException>(() => system.Acquire<GameObject>(Address("A.prefab")));
            Assert.That(sync.Kind, Is.EqualTo(ResourceError.ContentBufferUnavailable));
            text.Dispose();
            using var retry = system.AcquireAsync<GameObject>(Address("A.prefab"));
            yield return retry;
            using var held = retry.TakeResult();
            Assert.That(held.Asset, Is.Not.Null);
            Assert.That(Budget(system).LiveInputs, Is.EqualTo(closure.Count));
            Assert.That(Budget(system).PeakBytes, Is.LessThanOrEqualTo(Budget(system).MaximumBytes));
        }

        [UnityTest]
        public IEnumerator FaultedDependencyBlocksReadyBatchIdleAndSceneButNotAnUnrelatedBundle()
        {
            yield return new EnterPlayMode();
            yield return FaultedDependencyBody();
        }

        private static IEnumerator FaultedDependencyBody()
        {
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(Configuration(keys));
            yield return initialize;
            var system = initialize.TakeResult();
            using var a = system.Acquire<GameObject>(Address("A.prefab"));
            using var b = system.Acquire<GameObject>(Address("B.prefab"));
            using var cache = system.GetInstanceCache(a, InstanceCachePolicy.Persistent);
            var instance = cache.Instantiate();
            instance.Dispose();
            var snapshot = Snapshot();
            var descriptor = snapshot.Document.Bundles.Single(x => x.LogicalId == snapshot.Document.Assets.Single(x => x.Address == "A.prefab").BundleId);
            Assert.That(descriptor.Dependencies, Is.Not.Empty);
            using var dependency = system.AcquireBundle(snapshot.Document.ProjectId, descriptor.Dependencies[0]);
            var input = (ResourceBundleInput)(object)dependency.Record.Input;
            var decrypt = (ResourceDecryptStream)input.Stream;
            // 注入已确认首fault通知，不让Unity执行失败读取，更不重演Scene native崩溃。
            var latch = typeof(ResourceDecryptStream).GetMethod("Latch", Hidden);
            Assert.That(latch, Is.Not.Null);
            latch.Invoke(decrypt, new object[] { new CryptographicException("owned fault-notification test") });
            yield return null;
            Assert.That(a.Asset, Is.Not.Null, "Previously delivered objects are not forcibly destroyed.");
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<GameObject>(Address("B.prefab"))).Kind,
                Is.EqualTo(ResourceError.ContentAuthenticationFailed));
            Assert.Throws<ResourceException>(() => cache.Instantiate());
            using var batch = system.AcquireGroupAsync(snapshot.Document.ProjectId, "Mixed");
            yield return batch;
            Assert.That(batch.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            using var scene = system.LoadSceneAsync(Address("Owned.unity"), LoadSceneMode.Additive);
            yield return scene;
            Assert.That(scene.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(scene.HasNativeStarted, Is.False);
            using var independent = system.Acquire<TextAsset>(Address("entry.txt"));
            Assert.That(independent.Asset.text, Does.Contain("first release"));
        }

        [UnityTearDown]
        public IEnumerator EndOnlyTheOwnedSession()
        {
            if (Application.isPlaying)
            {
                AudioKit.Shutdown();
                typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.path.EndsWith("/Content/Owned.unity", StringComparison.Ordinal))
                    {
                        var unload = SceneManager.UnloadSceneAsync(scene);
                        while (unload != null && !unload.isDone)
                        {
                            yield return null;
                        }
                    }
                }
                var deadline = Time.realtimeSinceStartup + 30;
                while (AssetBundle.GetAllLoadedAssetBundles().Any() && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
                yield return new ExitPlayMode();
            }
        }

        [OneTimeTearDown]
        public void RestoreOnlyTheOwnedFixture()
        {
            var layout = JsonUtility.FromJson<Layout>(SessionState.GetString(Key + "layout", "{}"));
            if (layout?.Scenes != null)
            {
                EditorSceneManager.RestoreSceneManagerSetup(layout.Scenes);
            }

            foreach (var suffix in new[] { "root", "workspace", "cache" })
            {
                var path = SessionState.GetString(Key + suffix, "");
                if (!string.IsNullOrEmpty(path) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path), true);
                }
            }
            var scene = SessionState.GetString(Key + "scene", "");
            if (!string.IsNullOrEmpty(scene) && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(scene)))
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(scene));
            }

            foreach (var suffix in new[] { "root", "workspace", "cache", "v1", "v2", "project", "scene", "layout", "tlsOverride" })
            {
                SessionState.EraseString(Key + suffix);
            }

            SessionState.EraseBool(Key + "owned");
        }

        private sealed class TestKeys : IResourceContentKeyProvider, IDisposable
        {
            private readonly int _ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            internal int Copies;
            internal bool Missing;
            internal Action OnCopy;
            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                Assert.That(System.Threading.Thread.CurrentThread.ManagedThreadId, Is.EqualTo(_ownerThread), "Native and IO callbacks must not call the application provider.");
                Assert.That(keyId, Is.EqualTo("e04-test-v1"));
                Copies++;
                OnCopy?.Invoke();
                if (Missing)
                {
                    return false;
                }

                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (byte)(93 + i);
                }

                return true;
            }
            public void Dispose()
            {
            }
        }
    }
}
