using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Core;
using YLFramework.Editor.Resource;
using YLFramework.Samples.Resource;
using YLFramework.Samples.Resource.Editor;
using YLFramework.Audio;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceStartupExampleTests
    {
        private const string Key = "YL.Resource.C05.Sample.";
        private const string EncryptedKeyId = "sample-test-only-v1";
        [Serializable]
        private sealed class Scenes
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

        private sealed class ConfigurationOnlyKeys : IResourceContentKeyProvider
        {
            public bool TryCopyKey(string projectId, string keyId, byte[] destination) =>
                throw new AssertionException("Selecting a sample configuration must not read the application content key.");
        }

        // 仅用于自有样例夹具，不是发行密钥或生产provider。
        private sealed class EncryptedExampleKeys : IResourceContentKeyProvider
        {
            private readonly string _project;
            internal int Copies;
            internal EncryptedExampleKeys(string project) => this._project = project;
            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                if (projectId != _project || keyId != EncryptedKeyId)
                {
                    return false;
                }

                Assert.That(destination.Length, Is.EqualTo(32));
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (byte)(117 + i);
                }

                Copies++;
                return true;
            }
        }

        private static string ProtectedDigest()
        {
            var directories = new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "Packages/com.kano.ylframework/Prefabs", "AssetBundles/MainModule", "Packages", "ProjectSettings" }
                .SelectMany(x => ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(x))).Select(file => x + file));
            var files = new[] { "AGENTS.md", ".kilo/kilo.jsonc", ".kilo/plans/1791960935353-ui-panel-redesign.md" }
                .Select(x => x + (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(x)) ? ResourceCatalogCodecTests.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(x))) : "absent"));
            return ResourceCatalogCodecTests.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", directories.Concat(files))));
        }

        [OneTimeSetUp]
        public void BuildOnlyTheFormalExample()
        {
            if (SessionState.GetBool(Key + "owned", false))
            {
                return;
            }

            YLFramework.Tests.Resource.ResourceSessionTests.EnsureRestorableScenes();
            var scenes = EditorSceneManager.GetSceneManagerSetup();
            SessionState.SetString(Key + "scenes", JsonUtility.ToJson(new Scenes
            {
                Items = scenes.Where(x => !string.IsNullOrEmpty(x.path))
                .Select(x => new SavedScene { Path = x.path, Loaded = x.isLoaded, Active = x.isActive }).ToArray()
            }));
            SessionState.SetString(Key + "protected", ProtectedDigest());
            SessionState.SetBool(Key + "soundExisted", PlayerPrefs.HasKey("KEY_AUDIO_MANAGER_SOUND_ON"));
            SessionState.SetInt(Key + "soundValue", PlayerPrefs.GetInt("KEY_AUDIO_MANAGER_SOUND_ON", 1));
            var selected = Selection.activeObject;
            SessionState.SetString(Key + "selection", selected ? GlobalObjectId.GetGlobalObjectIdSlow(selected).ToString() : "");
            SessionState.SetBool(Key + "owned", true);
            var folder = ResourceStartupExampleEditor.Create();
            SessionState.SetString(Key + "folder", folder);
            SessionState.SetString(Key + "guid", AssetDatabase.AssetPathToGUID(folder));
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(folder + "/Project.asset");
            SessionState.SetString(Key + "workspace", Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(project, EditorUserBuildSettings.activeBuildTarget)));
            var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            var release = ResourceStartupExampleEditor.Build(example);
            SessionState.SetString(Key + "release", release.RootPath);
            Assert.That(EditorSceneManager.SaveScene(example.gameObject.scene), Is.True);
            var paths = new[] { "Example.unity", "Project.asset", "Content/Data.asset", "Content/Message.txt", "Content/Tone.wav" };
            var guids = paths.Select(path => AssetDatabase.AssetPathToGUID(folder + "/" + path)).ToArray();
            var bytes = paths.Select(path => File.ReadAllBytes(folder + "/" + path)).ToArray();
            var metas = paths.Select(path => File.ReadAllBytes(folder + "/" + path + ".meta")).ToArray();
            var contentGuid = AssetDatabase.AssetPathToGUID(folder + "/Content");
            var contentMeta = File.ReadAllBytes(folder + "/Content.meta");
            var folderMeta = File.ReadAllBytes(folder + ".meta");
            var sampleSettings = JsonUtility.ToJson(example);
            var projectId = project.projectId;
            var data = AssetDatabase.LoadAssetAtPath<ResourceStartupExampleData>(folder + "/Content/Data.asset");
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data, out string dataGuid, out long dataId), Is.True);
            EditorSceneManager.OpenScene("Assets/YLTestingProfile/Empty.unity", OpenSceneMode.Single);
            Assert.That((bool)example, Is.False, "Unload the formally saved sample scene before cold reopening it.");
            Resources.UnloadAsset(data);
            Resources.UnloadAsset(project);
            Assert.That((bool)data || (bool)project, Is.False);
            EditorSceneManager.OpenScene(folder + "/Example.unity", OpenSceneMode.Single);
            var restored = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            Assert.That(ReferenceEquals(restored, example), Is.False);
            Assert.That(JsonUtility.ToJson(restored), Is.EqualTo(sampleSettings));
            Assert.That(restored.mode, Is.EqualTo(ResourceLoadMode.EditorSimulation));
            Assert.That(restored.useBuiltIn, Is.False);
            Assert.That(restored.bindCore && restored.useAudio, Is.True);
            Assert.That(restored.platform, Is.EqualTo("StandaloneWindows64"));
            Assert.That(restored.appCompatibility, Is.EqualTo("startup-sample-v1"));
            Assert.That(restored.projectId, Is.EqualTo(projectId));
            Assert.That(projectId, Does.Match("^startup-example-[a-f0-9]{8}$"));
            Assert.That(restored.catalogDigest, Is.EqualTo(release.Catalog.CatalogDigest));
            Assert.That(restored.snapshotDirectory, Is.EqualTo(release.DirectoryPath));
            Assert.That(restored.Startup, Is.Null);
            Assert.That(restored.ContentKeyProvider, Is.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(restored))), Is.EqualTo("6efb2386a5492c0449d3f50a48627a32"));
            var restoredData = AssetDatabase.LoadAssetAtPath<ResourceStartupExampleData>(folder + "/Content/Data.asset");
            Assert.That(restoredData.text, Is.EqualTo("Explicit compiled sample type"));
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(restoredData, out string restoredDataGuid, out long restoredDataId), Is.True);
            Assert.That(restoredDataGuid, Is.EqualTo(dataGuid));
            Assert.That(restoredDataId, Is.EqualTo(dataId));
            var restoredProject = AssetDatabase.LoadAssetAtPath<ResourceProject>(folder + "/Project.asset");
            Assert.That(restoredProject.projectId, Is.EqualTo(projectId));
            Assert.That(restoredProject.sourceDirectories.Single().guid, Is.EqualTo(contentGuid));
            var selections = restoredProject.groups.Single().selections;
            Assert.That(selections.Select(value => value.address), Is.EqualTo(new[] { "message", "data", "tone" }));
            Assert.That(selections.Select(value => value.guid), Is.EqualTo(new[] { guids[3], guids[2], guids[4] }));
            var output = Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT");
            Assert.That(Directory.Exists(output), Is.True, "Run the formal sample producer in the declared owned native host.");
            var evidence = Path.Combine(output, "ResourceStartupFormalProducer");
            Assert.That(Directory.Exists(evidence), Is.False);
            Directory.CreateDirectory(evidence);
            CollectionAssert.AreEqual(folderMeta, File.ReadAllBytes(folder + ".meta"));
            CollectionAssert.AreEqual(contentMeta, File.ReadAllBytes(folder + "/Content.meta"));
            File.WriteAllBytes(Path.Combine(evidence, "Root.meta"), folderMeta);
            File.WriteAllBytes(Path.Combine(evidence, "Content.meta"), contentMeta);
            for (var i = 0; i < paths.Length; i++)
            {
                Assert.That(AssetDatabase.AssetPathToGUID(folder + "/" + paths[i]), Is.EqualTo(guids[i]));
                CollectionAssert.AreEqual(bytes[i], File.ReadAllBytes(folder + "/" + paths[i]));
                CollectionAssert.AreEqual(metas[i], File.ReadAllBytes(folder + "/" + paths[i] + ".meta"));
                var destination = Path.Combine(evidence, paths[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllBytes(destination, bytes[i]);
                File.WriteAllBytes(destination + ".meta", metas[i]);
                TestContext.WriteLine("Formal sample input: {0}/{1}; GUID={2}", folder, paths[i], guids[i]);
            }
            Assert.That(ProtectedDigest(), Is.EqualTo(SessionState.GetString(Key + "protected", "")));
        }

        [UnityTearDown]
        public IEnumerator ExitOnFailure()
        {
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            RestoreEncryptedExample();
            var saved = SessionState.GetString(Key + "beforeBuiltInExample", "");
            if (!string.IsNullOrEmpty(saved))
            {
                var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
                Assert.That(example, Is.Not.Null);
                JsonUtility.FromJsonOverwrite(saved, example);
                var messagePath = SessionState.GetString(Key + "folder", "") + "/Content/Message.txt";
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(messagePath), Convert.FromBase64String(SessionState.GetString(Key + "beforeBuiltInMessage", "")));
                AssetDatabase.ImportAsset(messagePath, ImportAssetOptions.ForceSynchronousImport);
                Assert.That(EditorSceneManager.SaveScene(example.gameObject.scene), Is.True);
                CleanupBuiltInDeployment();
            }
        }

        [OneTimeTearDown]
        public void CleanupOnlyOwnedExample()
        {
            if (!SessionState.GetBool(Key + "owned", false))
            {
                return;
            }

            if (SessionState.GetBool(Key + "soundExisted", false))
            {
                PlayerPrefs.SetInt("KEY_AUDIO_MANAGER_SOUND_ON", SessionState.GetInt(Key + "soundValue", 1));
            }
            else
            {
                PlayerPrefs.DeleteKey("KEY_AUDIO_MANAGER_SOUND_ON");
            }

            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            RestoreEncryptedExample();
            CleanupBuiltInDeployment();
            Assert.That(ProtectedDigest(), Is.EqualTo(SessionState.GetString(Key + "protected", "")), "The example must not change protected assets, settings or user/parallel configuration.");
            var scenes = JsonUtility.FromJson<Scenes>(SessionState.GetString(Key + "scenes", ""));
            EditorSceneManager.RestoreSceneManagerSetup(scenes.Items.Select(x => new SceneSetup { path = x.Path, isLoaded = x.Loaded, isActive = x.Active }).ToArray());
            foreach (var key in new[] { "release", "workspace", "encryptedWorkspace" })
            {
                var path = SessionState.GetString(Key + key, "");
                if (key == "encryptedWorkspace" && !string.IsNullOrEmpty(path) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    ResourceBuildOperation.RejectLinks(path);
                    var marker = Path.Combine(path, ".ylr-workspace.json");
                    ResourceBuildOperation.RejectLinks(marker);
                    Assert.That((string)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(marker)))["ProjectGuid"],
                        Is.EqualTo(AssetDatabase.AssetPathToGUID(SessionState.GetString(Key + "folder", "") + "/Project.asset")));
                }
                if (!string.IsNullOrEmpty(path) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path), true);
                }
            }
            var folder = SessionState.GetString(Key + "folder", "");
            if (!string.IsNullOrEmpty(folder))
            {
                Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(SessionState.GetString(Key + "guid", "")));
                AssetDatabase.DeleteAsset(folder);
            }
            if (GlobalObjectId.TryParse(SessionState.GetString(Key + "selection", ""), out var selected))
            {
                Selection.activeObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(selected);
            }

            foreach (var key in new[] { "folder", "guid", "workspace", "encryptedWorkspace", "release", "scenes", "protected", "selection" })
            {
                SessionState.EraseString(Key + key);
            }

            SessionState.EraseBool(Key + "owned");
            SessionState.EraseBool(Key + "soundExisted");
            SessionState.EraseInt(Key + "soundValue");
        }

        [Test]
        public void BundleConfigurationUsesApplicationKeysWithoutReadingThemAndSimulationStaysUnchanged()
        {
            var provider = typeof(ResourceStartupExample).GetField("ContentKeyProvider");
            Assert.That(provider, Is.Not.Null, "Missing runtime-only application content key assembly on the existing sample.");
            var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            Assert.That(example, Is.Not.Null);
            var saved = JsonUtility.ToJson(example);
            var previous = provider.GetValue(example);
            try
            {
                example.mode = ResourceLoadMode.Bundles;
                provider.SetValue(example, null);
                Assert.That(example.CreateBundleConfiguration().Decryption, Is.Null, "None must keep the original key-free configuration path.");
                var keys = new ConfigurationOnlyKeys();
                provider.SetValue(example, keys);
                var configuration = example.CreateBundleConfiguration();
                Assert.That(configuration.Decryption, Is.Not.Null);
                Assert.That(configuration.Decryption.KeyProvider, Is.SameAs(keys));
                Assert.That(configuration.Decryption.MaxBufferedBytes, Is.EqualTo(ResourceDecryptionOptions.DefaultMaxBufferedBytes));
                Assert.That(configuration.ArtifactStore, Is.Null, "Key injection must not opt into a cache or update store.");
                using var serialized = new SerializedObject(example);
                Assert.That(serialized.FindProperty("ContentKeyProvider"), Is.Null);
                example.mode = ResourceLoadMode.EditorSimulation;
                Assert.That(ResourceStartupExampleEditor.CreateConfiguration(example).Decryption, Is.Null);
                Assert.That(example.Startup, Is.Null);
                Assert.That(ResourceSystem.Current, Is.Null, "Choosing keys and configuration must not initialize resources.");
            }
            finally
            {
                JsonUtility.FromJsonOverwrite(saved, example);
                provider.SetValue(example, previous);
            }
        }

        [Test]
        public void DevelopmentEnvironmentProviderIsExplicitRuntimeOnlyAndDoesNotReadTheSecretWhileAssembling()
        {
            var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(SessionState.GetString(Key + "folder", "") + "/Project.asset");
            var previous = example.ContentKeyProvider;
            var keyId = project.contentKeyId;
            var saved = JsonUtility.ToJson(example);
            var variable = "YL_RESOURCE_SAMPLE_TEST_" + Guid.NewGuid().ToString("N");
            const string InvalidSecret = "sample-only-sensitive-marker";
            try
            {
                project.contentKeyId = "development-test-only-v1";
                Environment.SetEnvironmentVariable(variable, InvalidSecret);
                ResourceStartupExampleEditor.UseDevelopmentContentKeyFromEnvironment(example, variable);
                Assert.That(example.ContentKeyProvider, Is.Not.Null);
                Assert.That(JsonUtility.ToJson(example), Is.EqualTo(saved), "Provider assembly must not change serialized sample data.");
                var destination = new byte[32];
                Assert.That(example.ContentKeyProvider.TryCopyKey("unselected-project", project.contentKeyId, destination), Is.False);
                var error = Assert.Throws<InvalidOperationException>(() => example.ContentKeyProvider.TryCopyKey(example.projectId, project.contentKeyId, destination));
                Assert.That(error.ToString(), Does.Not.Contain(InvalidSecret));
                Assert.That(ResourceSystem.Current, Is.Null);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
                project.contentKeyId = keyId;
                example.ContentKeyProvider = previous;
            }
        }

        [UnityTest]
        public IEnumerator EncryptedSampleBuildAndRunUseApplicationKeysAndReturnAllResources()
        {
            var folder = SessionState.GetString(Key + "folder", "");
            var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(folder + "/Project.asset");
            SessionState.SetString(Key + "beforeEncryptedExample", JsonUtility.ToJson(example));
            SessionState.SetString(Key + "beforeEncryptedProject", JsonUtility.ToJson(project));
            project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
            project.contentKeyId = EncryptedKeyId;
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            Assert.That(example.gameObject.scene.path, Is.EqualTo(folder + "/Example.unity"));
            Assert.That(EditorSceneManager.SaveScene(example.gameObject.scene), Is.True, "Only the explicitly owned sample scene may be saved before its build.");
            var workspace = Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(project, EditorUserBuildSettings.activeBuildTarget));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace + ".meta")), Is.False, "Do not adopt a pre-existing encrypted workspace.");
            SessionState.SetString(Key + "encryptedWorkspace", workspace);
            var keys = new EncryptedExampleKeys(example.projectId);
            example.ContentKeyProvider = keys;
            var release = ResourceStartupExampleEditor.Build(example);
            Assert.That(keys.Copies, Is.GreaterThan(0), "The original sample Build must forward the application's provider through formal build and publication.");
            foreach (var bundle in release.Catalog.Bundles)
            {
                using var input = new BinaryReader(File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(release.DirectoryPath, bundle.RelativeFile))));
                Assert.That(System.Text.Encoding.ASCII.GetString(input.ReadBytes(8)), Is.EqualTo("YLRENC01"));
            }
            example.ContentKeyProvider = null;
            example.mode = ResourceLoadMode.Bundles;
            example.bindCore = true;
            example.useAudio = false;
            Assert.That(EditorSceneManager.SaveScene(example.gameObject.scene), Is.True);
            Assert.That(ResourceSystem.Current, Is.Null);
            yield return new EnterPlayMode();
            yield return RunEncryptedExample();
            yield return new ExitPlayMode();
        }

        private static IEnumerator RunEncryptedExample()
        {
            var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            var keys = new EncryptedExampleKeys(example.projectId);
            example.ContentKeyProvider = keys;
            var configuration = ResourceStartupExampleEditor.CreateConfiguration(example);
            Assert.That(configuration.Decryption.KeyProvider, Is.SameAs(keys));
            Assert.That(configuration.ArtifactStore, Is.Null);
            var run = example.Run(configuration);
            var sawNativeBundle = false;
            try
            {
                while (run.MoveNext())
                {
                    sawNativeBundle |= AssetBundle.GetAllLoadedAssetBundles().Any();
                    yield return run.Current;
                }
            }
            finally
            {
                (run as IDisposable)?.Dispose();
                example.ContentKeyProvider = null;
            }
            Assert.That(sawNativeBundle, Is.True, "The encrypted sample must use native Bundles rather than simulation.");
            Assert.That(keys.Copies, Is.GreaterThan(0));
            Assert.That(example.Result, Is.EqualTo("Passed: Hello from ResourceStartup / Explicit compiled sample type"));
            Assert.That(example.Startup.Resources, Is.Null);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
        }

        private static void RestoreEncryptedExample()
        {
            var saved = SessionState.GetString(Key + "beforeEncryptedExample", "");
            if (string.IsNullOrEmpty(saved))
            {
                return;
            }

            var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(SessionState.GetString(Key + "folder", "") + "/Project.asset");
            Assert.That(example, Is.Not.Null);
            Assert.That(project, Is.Not.Null);
            JsonUtility.FromJsonOverwrite(SessionState.GetString(Key + "beforeEncryptedProject", ""), project);
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            JsonUtility.FromJsonOverwrite(saved, example);
            example.ContentKeyProvider = null;
            Assert.That(EditorSceneManager.SaveScene(example.gameObject.scene), Is.True);
            SessionState.EraseString(Key + "beforeEncryptedExample");
            SessionState.EraseString(Key + "beforeEncryptedProject");
        }

        [UnityTest]
        public IEnumerator SameFormalBusinessRunsSimulationAndNativeBundlesAndRejectsBadConfiguration()
        {
            yield return new EnterPlayMode();
            yield return RunBothModes();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator TwoExplicitPublishedVersionsDeployAndRunTheSameStartupBusinessAfterUnityImport()
        {
            var folder = SessionState.GetString(Key + "folder", "");
            var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(folder + "/Project.asset");
            SessionState.SetString(Key + "beforeBuiltInExample", JsonUtility.ToJson(example));
            SessionState.SetString(Key + "beforeBuiltInMessage", Convert.ToBase64String(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/Content/Message.txt"))));
            var root = Path.Combine(ResourceBuiltInDeployer.ResourcesRoot, example.projectId);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root + ".meta")), Is.False, "The native test owns only a new independent deployment.");
            SessionState.SetBool(Key + "builtInRootExisted", Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)));
            SessionState.SetString(Key + "builtInProject", example.projectId);
            SessionState.SetString(Key + "builtInGuid", AssetDatabase.AssetPathToGUID(folder + "/Project.asset"));
            var firstDigest = example.catalogDigest;
            SessionState.SetString(Key + "builtInV1", firstDigest);
            var first = ResourcePublisher.ReadVersion(project, EditorUserBuildSettings.activeBuildTarget, example.appCompatibility, firstDigest);
            var releaseBefore = ResourceReleaseFixture.Snapshot(first.RootPath);
            var plan = ResourceStartupExampleEditor.PreflightBuiltIn(example);
            SessionState.SetString(Key + "builtInWorkspace", plan.WorkspacePath);
            var deployed = ResourceStartupExampleEditor.DeployBuiltIn(example);
            CollectionAssert.AreEqual(releaseBefore, ResourceReleaseFixture.Snapshot(first.RootPath));
            var oldBefore = ResourceReleaseFixture.Snapshot(deployed.DirectoryPath);
            Assert.That(ResourceBuiltInDeployer.Deploy(plan).IsRepeat, Is.True);
            Assert.That(EditorSceneManager.SaveScene(example.gameObject.scene), Is.True, "The sample deployment explicitly changes only this owned fixture scene; save before the next build.");

            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/Content/Message.txt"), "Hello from deployed version two");
            AssetDatabase.ImportAsset(folder + "/Content/Message.txt", ImportAssetOptions.ForceSynchronousImport);
            var second = ResourceStartupExampleEditor.Build(example);
            Assert.That(second.Catalog.CatalogDigest, Is.Not.EqualTo(firstDigest));
            releaseBefore = ResourceReleaseFixture.Snapshot(second.RootPath);
            ResourceStartupExampleEditor.DeployBuiltIn(example);
            CollectionAssert.AreEqual(releaseBefore, ResourceReleaseFixture.Snapshot(second.RootPath));
            CollectionAssert.AreEqual(oldBefore, ResourceReleaseFixture.Snapshot(deployed.DirectoryPath));
            example.snapshotDirectory = "unused-developer-publication-path";
            example.mode = ResourceLoadMode.Bundles;
            example.bindCore = true;
            example.useAudio = false;
            Assert.That(EditorSceneManager.SaveScene(example.gameObject.scene), Is.True);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot), "*.meta", SearchOption.AllDirectories)
                .Any(path => path.Contains("payload~")), Is.False, "Actual Unity import must leave both strict payloads untouched.");
            Assert.That(ResourceSystem.Current, Is.Null, "Deploying and importing must not initialize ResourceSystem.");
            yield return new EnterPlayMode();
            yield return RunDeployedVersions();
            yield return new ExitPlayMode();
        }

        private static IEnumerator RunDeployedVersions()
        {
            var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            var secondDigest = example.catalogDigest;
            var digests = new[] { SessionState.GetString(Key + "builtInV1", ""), secondDigest };
            var expected = new[] { "Hello from ResourceStartup", "Hello from deployed version two" };
            for (var i = 0; i < digests.Length; i++)
            {
                example.catalogDigest = digests[i];
                var cacheRoot = Path.Combine(Path.GetTempPath(), "kilo", "c02-readonly-cache-" + Guid.NewGuid().ToString("N"));
                var configuration = i == 0 ? ResourceStartupExampleEditor.CreateConfiguration(example) :
                    ResourceConfiguration.Create(ResourceLoadMode.Bundles, new[] { example.CreateMount() }, cacheRoot);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(cacheRoot)), Is.False);
                var entry = example.CreateMount().BuiltIn.CreateCatalog(new[] { typeof(ResourceStartupExampleData) })
                    .Resolve(new ResourceAddress(example.projectId, "message"));
                Assert.That(entry.Location.Source, Is.EqualTo(ResourceSourceId.AssetBundle), "There must be no implicit simulation fallback.");
                var run = example.Run(configuration);
                var sawNativeBundle = false;
                try
                {
                    while (run.MoveNext())
                    {
                        sawNativeBundle |= AssetBundle.GetAllLoadedAssetBundles().Any();
                        yield return run.Current;
                    }
                }
                finally
                {
                    (run as IDisposable)?.Dispose();
                }
                Assert.That(sawNativeBundle, Is.True);
                Assert.That(example.Result, Is.EqualTo("Passed: " + expected[i] + " / Explicit compiled sample type"));
                Assert.That(example.Startup.Resources, Is.Null);
                Assert.That(ResourceSystem.Current, Is.Null);
                Assert.That(CoreResourceAccess.Provider, Is.Null);
                Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(cacheRoot)), Is.False, "A first startup consuming deployed built-in resources must not create a persistent store merely to mount them.");
            }
        }

        private static void CleanupBuiltInDeployment()
        {
            var project = SessionState.GetString(Key + "builtInProject", "");
            if (string.IsNullOrEmpty(project))
            {
                return;
            }

            var guid = SessionState.GetString(Key + "builtInGuid", "");
            var workspace = SessionState.GetString(Key + "builtInWorkspace", "");
            if (!string.IsNullOrEmpty(workspace) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace)))
            {
                ResourceBuiltInDeployer.Recover(project, EditorUserBuildSettings.activeBuildTarget.ToString(), guid);
                ResourceBuildOperation.RejectLinks(workspace);
                Assert.That((string)ResourceBuiltInLayout.ReadObject(Path.Combine(workspace, "ylr-owner.json~"))["ProjectGuid"], Is.EqualTo(guid));
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(workspace), true);
            }
            var root = Path.Combine(ResourceBuiltInDeployer.ResourcesRoot, project);
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
            {
                Assert.That((string)ResourceBuiltInLayout.ReadObject(Path.Combine(root, "ylr-owner.json~"))["ProjectGuid"], Is.EqualTo(guid));
                ResourceBuildOperation.RejectLinks(root);
                Assert.That(AssetDatabase.DeleteAsset("Assets/StreamingAssets/YLFrameworkResources/" + project), Is.True);
            }
            if (!SessionState.GetBool(Key + "builtInRootExisted", false) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)))
            {
                CollectionAssert.AreEquivalent(new[] { "ylr-owner.json~" }, Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceBuiltInDeployer.ResourcesRoot)).Select(Path.GetFileName));
                Assert.That(AssetDatabase.DeleteAsset("Assets/StreamingAssets/YLFrameworkResources"), Is.True);
            }
            foreach (var key in new[] { "builtInProject", "builtInGuid", "builtInWorkspace", "builtInV1", "beforeBuiltInExample", "beforeBuiltInMessage" })
            {
                SessionState.EraseString(Key + key);
            }

            SessionState.EraseBool(Key + "builtInRootExisted");
        }

        private static IEnumerator RunBothModes()
        {
            var folder = SessionState.GetString(Key + "folder", "");
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(folder + "/Project.asset");
            var example = UnityEngine.Object.FindObjectOfType<ResourceStartupExample>();
            Assert.That(example, Is.Not.Null);
            Assert.That(example.Startup, Is.Null, "Loading the sample scene must not initialize resources.");
            Assert.That(ResourceSystem.Current, Is.Null);
            AudioKit.Settings.IsSoundOn.Value = true;
            var mount = example.CreateMount();
            var simulation = ResourceEditorSimulation.CreateConfiguration(ResourceLoadMode.EditorSimulation, new[] { project }, new[] { mount });
            Assert.That(ResourceSystem.Current, Is.Null, "Configuration creation is not startup.");
            yield return example.Run(simulation);
            Assert.That(example.Result, Is.EqualTo("Passed: Hello from ResourceStartup / Explicit compiled sample type"));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);

            example.mode = ResourceLoadMode.Bundles;
            example.ContentKeyProvider = new ConfigurationOnlyKeys();
            var bundles = ResourceStartupExampleEditor.CreateConfiguration(example);
            yield return example.Run(bundles);
            Assert.That(example.Result, Is.EqualTo("Passed: Hello from ResourceStartup / Explicit compiled sample type"));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            example.ContentKeyProvider = null;

            var projectId = example.projectId;
            example.bindCore = false;
            example.useAudio = false;
            var snapshotRun = example.Run(simulation);
            try
            {
                Assert.That(snapshotRun.MoveNext(), Is.True);
                Assert.That(example.Startup.CoreProvider, Is.Null);
                Assert.That(example.Startup.AudioProviders, Is.Empty);
                example.bindCore = true;
                example.useAudio = true;
                example.projectId = "edited-after-start";
                yield return snapshotRun;
                Assert.That(example.Result, Is.EqualTo("Passed: Hello from ResourceStartup / Explicit compiled sample type"), "In-flight business must use the startup's original project and provider choices.");
            }
            finally
            {
                (snapshotRun as IDisposable)?.Dispose();
                example.projectId = projectId;
                example.bindCore = true;
                example.useAudio = true;
            }

            var missingTypes = new ResourceProjectMount(mount.ProjectId, mount.Platform, mount.AppContentCompatibilityId, mount.BuiltIn);
            Assert.That(Assert.Throws<ResourceException>(() => ResourceConfiguration.Create(ResourceLoadMode.Bundles, new[] { missingTypes })).Kind,
                Is.EqualTo(ResourceError.TypeMismatch));
            Assert.Throws<ArgumentException>(() => ResourceConfiguration.Create(ResourceLoadMode.Bundles, new[] { mount, mount }));
            var digest = example.catalogDigest;
            example.catalogDigest = new string('a', 64);
            Assert.Throws<InvalidDataException>(() => ResourceStartupExampleEditor.CreateConfiguration(example));
            example.catalogDigest = digest;
            Assert.That(ResourceSystem.Current, Is.Null, "Bundle configuration failure must never fall back to simulation.");
            var cacheRoot = Path.Combine(Path.GetTempPath(), "kilo", "c05-readonly-" + Guid.NewGuid().ToString("N"));
            _ = ResourceConfiguration.Create(ResourceLoadMode.Bundles, new[] { mount }, cacheRoot);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(cacheRoot)), Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);

            example.Begin(example.CreateBundleConfiguration());
            example.StopExample();
            var until = Time.realtimeSinceStartup + 10;
            while (example.Startup.Resources != null && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(example.Startup.Resources, Is.Null, example.Result);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
        }
    }
}
