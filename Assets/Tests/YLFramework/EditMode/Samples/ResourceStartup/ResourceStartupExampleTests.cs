using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Core;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Samples.Resource;
using YLFramework.Samples.Resource.Editor;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    [Category("YL.Resource.Addressables.Samples.Imported")]
    public class ResourceStartupExampleTests
    {
        private const string Key = "YL.Resource.AA.SampleRegression.";
        private const string Empty = "Assets/YLTestingProfile/Empty.unity";
        private static ResourceSystem _lastSystem;

        [Serializable]
        private sealed class SavedScene
        {
            public string Path;
            public bool Loaded;
            public bool Active;
        }
        [Serializable]
        private sealed class Scenes
        {
            public SavedScene[] Items;
        }
        [Serializable]
        private sealed class Input
        {
            public string Path;
            public string Sha256;
            public string Guid;
        }
        [Serializable]
        private sealed class Inputs
        {
            public Input[] Items;
        }
        [Serializable]
        private sealed class DirectoryOwner
        {
            public int schemaVersion;
            public string purpose;
            public string kind;
            public string path;
        }

        private static string Folder => SessionState.GetString(Key + "folder", "");
        private static string Bootstrap => Folder + "/Bootstrap.unity";
        private static string Business => Folder + "/Content/Business.unity";

        [OneTimeSetUp]
        public void BuildOnlyTheOfficialFormalExample()
        {
            if (SessionState.GetBool(Key + "owned", false))
            {
                VerifyOwnedFolder();
                return;
            }
            RequireOwner(Directory.GetParent(Application.dataPath).FullName, "Host");
            UseSavedAuthoringAnchor();
            EnsureRestorableScenes();
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.False, "Do not silently change the caller's domain reload policy.");
            var settings = Settings();
            ResourceEditorOperations.ValidateBuild(settings);
            Assert.That(settings.ActivePlayModeDataBuilder, Is.InstanceOf<BuildScriptPackedPlayMode>(),
                "Prepare the official Use Existing Build Play mode before this formal-content fixture.");
            Assert.That(File.Exists(Empty), Is.True, "Use the existing saved host scene, not a runtime-created shutdown escape.");
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            Assert.That(ResourceStartupExample.PendingShutdownCount, Is.Zero);
            SessionState.SetString(Key + "scenes", JsonUtility.ToJson(new Scenes
            {
                Items = EditorSceneManager.GetSceneManagerSetup().Where(scene => !string.IsNullOrEmpty(scene.path))
                    .Select(scene => new SavedScene { Path = scene.path, Loaded = scene.isLoaded, Active = scene.isActive }).ToArray()
            }));
            SessionState.SetString(Key + "protected", ProtectedDigest());
            var selected = Selection.activeObject;
            SessionState.SetString(Key + "selection", selected ? GlobalObjectId.GetGlobalObjectIdSlow(selected).ToString() : "");
            var groups = settings.groups.Where(group => group).Select(group => group.Guid).ToArray();
            var folder = ResourceStartupExampleEditor.Create(settings);
            SessionState.SetString(Key + "folder", folder);
            SessionState.SetString(Key + "guid", AssetDatabase.AssetPathToGUID(folder));
            var created = settings.groups.Where(group => group && !groups.Contains(group.Guid)).ToArray();
            Assert.That(created.Length, Is.EqualTo(1));
            SessionState.SetString(Key + "group", AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(created[0])));
            SessionState.SetString(Key + "groupIdentity", GroupIdentity(created[0]));
            SessionState.SetBool(Key + "owned", true);
            var example = Example();
            ResourceStartupExampleEditor.Validate(example, settings);
            Assert.That(example.gameObject.scene.path, Is.EqualTo(Bootstrap));
            Assert.That(example.Startup, Is.Null);
            var data = AssetDatabase.LoadAssetAtPath<ResourceStartupExampleData>(folder + "/Content/Data.asset");
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data, out string dataGuid, out long dataId), Is.True);
            SessionState.SetString(Key + "dataGuid", dataGuid);
            SessionState.SetString(Key + "dataId", dataId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SessionState.SetString(Key + "sample", JsonUtility.ToJson(example));
            var paths = new[] { folder + ".meta", folder + "/Content.meta" }.Concat(new[]
            {
                "Bootstrap.unity", "Content/Business.unity", "Content/ScopedPrefab.prefab", "Content/Data.asset", "Content/Message.txt", "Content/Preview.png", "Content/Cue.wav"
            }.SelectMany(path => new[] { folder + "/" + path, folder + "/" + path + ".meta" })).ToArray();
            SessionState.SetString(Key + "inputs", JsonUtility.ToJson(new Inputs
            {
                Items = paths.Select(path => new Input { Path = path, Sha256 = Hash(path), Guid = AssetDatabase.AssetPathToGUID(path) }).ToArray()
            }));
            var output = Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT");
            Assert.That(Directory.Exists(output), Is.True, "Run in the declared owned host/output, never the user's main project.");
            RequireOwner(output, "Result");
            var evidence = Path.Combine(output, "ResourceStartupFormalProducer-" + Guid.NewGuid().ToString("N"));
            NoLinks(evidence);
            Directory.CreateDirectory(evidence);
            SessionState.SetString(Key + "evidence", evidence);
            var built = ResourceContentBuildExample.Build(settings, Path.Combine(evidence, "addressables_content_state.bin"));
            Assert.That(built.IsContentUpdate, Is.False);
            Assert.That(File.Exists(built.Result.OutputPath), Is.True);
            AssertInputsUnchanged();
            foreach (var item in InputImages())
            {
                var relative = item.Path == folder + ".meta" ? "Root.meta" : item.Path.Substring(folder.Length + 1);
                var destination = Path.Combine(evidence, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                Assert.That(File.Exists(destination), Is.False);
                File.Copy(item.Path, destination, false);
                Assert.That(Hash(destination), Is.EqualTo(item.Sha256));
            }
            Assert.That(ResourceSystem.Current, Is.Null, "Formal authoring and build must not initialize the sample.");
        }

        [Test]
        public void FormalEditorOutputColdReadPreservesAssetReferencesGuidsAndCallerInputs()
        {
            OpenBootstrap();
            var example = Example();
            var data = AssetDatabase.LoadAssetAtPath<ResourceStartupExampleData>(Folder + "/Content/Data.asset");
            var scriptGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(example)));
            EnsureRestorableScenes();
            EditorSceneManager.OpenScene(Empty, OpenSceneMode.Single);
            Assert.That((bool)example, Is.False);
            Resources.UnloadAsset(data);
            Assert.That((bool)data, Is.False, "Cold-read the actual compiled sample data after releasing the Editor's own reference.");
            OpenBootstrap();
            var restored = Example();
            Assert.That(ReferenceEquals(restored, example), Is.False);
            Assert.That(JsonUtility.ToJson(restored), Is.EqualTo(SessionState.GetString(Key + "sample", "")));
            Assert.That(restored.Startup, Is.Null);
            Assert.That(restored.bindCoreResourceProvider, Is.True);
            Assert.That(restored.reusePrefab, Is.False);
            Assert.That(restored.audioProjects, Is.Empty);
            Assert.That(restored.contentProjectId, Does.StartWith("startup-example-"));
            Assert.That(restored.startupCatalogId, Is.Empty.Or.Null, "A locally authored example must not enable remote update implicitly.");
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(restored))), Is.EqualTo(scriptGuid));
            var loadedData = AssetDatabase.LoadAssetAtPath<ResourceStartupExampleData>(Folder + "/Content/Data.asset");
            Assert.That(loadedData.text, Is.EqualTo("Scope-owned borrowed data"));
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(loadedData, out string guid, out long id), Is.True);
            Assert.That(guid, Is.EqualTo(SessionState.GetString(Key + "dataGuid", "")));
            Assert.That(id.ToString(System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(SessionState.GetString(Key + "dataId", "")));
            var entries = new List<AddressableAssetEntry>();
            Settings().GetAllAssets(entries, false);
            foreach (var reference in new AssetReference[] { restored.message, restored.data, restored.prefab, restored.scene, restored.preview, restored.audioCue })
            {
                var entry = entries.Single(item => item.guid == reference.AssetGUID);
                Assert.That(entry.AssetPath, Does.StartWith(Folder + "/Content/"));
                Assert.That(entry.address, Does.StartWith("startup-example-"));
            }
            ResourceStartupExampleEditor.Validate(restored, Settings());
            AssertInputsUnchanged();
            Assert.That(ProtectedDigest(), Is.EqualTo(SessionState.GetString(Key + "protected", "")));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SceneSwitchGuardRejectsDirtyOrUnsavedNonemptyCallerWithoutSaving(bool dirty)
        {
            OpenBootstrap();
            var original = EditorSceneManager.GetSceneManagerSetup();
            var before = AssetDatabase.GetAllAssetPaths().OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var settings = EditorJsonUtility.ToJson(Settings());
            var own = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(own);
                var retained = new GameObject("MustNotDiscardCallerContent");
                if (dirty)
                {
                    EditorSceneManager.MarkSceneDirty(own);
                }
                else
                {
                    Assert.That(own.isDirty, Is.False, "Exercise the clean unsaved-content guard without clearing caller dirtiness.");
                }
                Assert.Throws<InvalidOperationException>(OpenBootstrap);
                Assert.That(retained && own.isLoaded, Is.True);
                Assert.That(own.path, Is.Empty);
                Assert.That(own.rootCount, Is.EqualTo(1));
                if (dirty)
                {
                    Assert.Throws<InvalidOperationException>(() => ResourceStartupExampleEditor.Create(Settings()));
                }
                Assert.That(EditorJsonUtility.ToJson(Settings()), Is.EqualTo(settings));
                CollectionAssert.AreEqual(before, AssetDatabase.GetAllAssetPaths().OrderBy(path => path, StringComparer.Ordinal));
                AssertInputsUnchanged();
                Assert.That(ResourceSystem.Current, Is.Null);
            }
            finally
            {
                EditorSceneManager.CloseScene(own, true);
                EditorSceneManager.RestoreSceneManagerSetup(original);
            }
        }

        [Test]
        public void PublicAsyncSurfaceDoesNotSerializeRuntimeOwnersOrCompletionAsConfiguration()
        {
            OpenBootstrap();
            var example = Example();
            var type = typeof(ResourceStartupExample);
            var start = type.GetMethod(nameof(ResourceStartupExample.StartExampleAsync), new[] { typeof(CancellationToken) });
            Assert.That(start.ReturnType, Is.EqualTo(typeof(Task<string>)));
            Assert.That(start.GetParameters().Single().IsOptional, Is.True);
            Assert.That(type.GetMethod(nameof(ResourceStartupExample.StopExampleAsync)).ReturnType, Is.EqualTo(typeof(Task)));
            Assert.That(type.GetMethod(nameof(ResourceStartupExample.RetryPendingShutdownsAsync)).IsStatic, Is.True);
            Assert.That(type.GetMethod(nameof(ResourceStartupExample.RetryPendingShutdownsAsync)).ReturnType, Is.EqualTo(typeof(Task)));
            Assert.That(type.GetProperty(nameof(ResourceStartupExample.LastFailure)).PropertyType, Is.EqualTo(typeof(Exception)));
            using var serialized = new SerializedObject(example);
            foreach (var name in new[] { "Startup", "Result", "LastFailure", "PendingShutdownCount", "PendingShutdownDetails" })
            {
                Assert.That(serialized.FindProperty(name), Is.Null, name);
            }
            Assert.That(example.Startup, Is.Null);
            Assert.That(ResourceSystem.Current, Is.Null);
            AssertInputsUnchanged();
        }

        [UnityTest]
        public IEnumerator BusinessFreezesReferencesAndProviderOptionsThenRestartsAfterOrderlyStop()
        {
            PreparePlay();
            yield return new EnterPlayMode();
            yield return RunBusinessAndRestart();
            yield return new ExitPlayMode();
        }

        private static IEnumerator RunBusinessAndRestart()
        {
            Assert.That(Application.isPlaying, Is.True);
            var example = Example();
            var message = example.message;
            var data = example.data;
            var prefab = example.prefab;
            var scene = example.scene;
            var project = Settings().FindAssetEntry(message.AssetGUID).address.Split('/')[0];
            example.audioProjects = new[] { project };
            var run = example.StartExampleAsync();
            Assert.That(run.IsCompleted, Is.False);
            example.message = null;
            example.data = null;
            example.prefab = null;
            example.scene = null;
            var projectInput = example.contentProjectId;
            example.contentProjectId = "edited-after-start";
            example.bindCoreResourceProvider = false;
            example.audioProjects[0] = "edited-after-start";
            example.reusePrefab = true;
            yield return Wait(() => run.IsCompleted, "formal sample business");
            var text = run.GetAwaiter().GetResult();
            Assert.That(text, Does.StartWith("Hello from official Addressables / Scope-owned borrowed data / "));
            Assert.That(text, Does.Contain(" / retained="));
            Assert.That(text, Does.Contain(" / core=ok / audio=ok / image=ok / instance=fresh"));
            Assert.That(example.Result, Does.StartWith("Running: "));
            _lastSystem = example.Startup.Resources;
            Assert.That(_lastSystem, Is.SameAs(ResourceSystem.Current));
            CollectionAssert.AreEquivalent(new[] { "Core.ApplicationScenes", "ResourceStartupExample/" + example.GetInstanceID() },
                _lastSystem.GetDiagnostics().Scopes.Select(scope => scope.Owner));
            Assert.That(_lastSystem.GetDiagnostics().LeaseCount, Is.GreaterThan(0));
            Assert.That(example.Startup.AudioProviders.Keys, Is.EquivalentTo(new[] { project }));
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(example.Startup.CoreProvider));
            Assert.That(example.Startup.CoreProvider, Is.Not.Null);
            Assert.That(SceneManager.GetSceneByPath(Bootstrap).isLoaded && SceneManager.GetSceneByPath(Business).isLoaded, Is.True);
            Assert.That(SceneManager.GetSceneByPath(Business).GetRootGameObjects().Any(item => item.name.StartsWith("ScopedPrefab", StringComparison.Ordinal)), Is.True);
            AssertOfficialProvider(message.AssetGUID, typeof(TextAsset));
            example.message = message;
            example.data = data;
            example.prefab = prefab;
            example.scene = scene;
            example.contentProjectId = projectInput;
            example.bindCoreResourceProvider = true;
            example.audioProjects = new[] { project };
            var stop = example.StopExampleAsync();
            yield return Wait(() => stop.IsCompleted, "actual first sample shutdown");
            stop.GetAwaiter().GetResult();
            AssertClosed(_lastSystem);
            Assert.That(SceneManager.GetSceneByPath(Bootstrap).isLoaded, Is.True);
            Assert.That(SceneManager.GetSceneByPath(Business).isLoaded, Is.False);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            var first = _lastSystem;
            var next = example.StartExampleAsync();
            yield return Wait(() => next.IsCompleted, "same explicit AssetReferences in a second real session");
            var reused = next.GetAwaiter().GetResult();
            Assert.That(reused, Does.StartWith("Hello from official Addressables / Scope-owned borrowed data / "));
            Assert.That(reused, Does.Contain(" / instance=prewarmed, reset and reused"));
            _lastSystem = example.Startup.Resources;
            Assert.That(_lastSystem, Is.Not.SameAs(first));
            stop = example.StopExampleAsync();
            yield return Wait(() => stop.IsCompleted, "actual second sample shutdown");
            stop.GetAwaiter().GetResult();
            AssertClosed(_lastSystem);
        }

        [UnityTest]
        public IEnumerator SameAssetReferencesConsumeTwoExplicitOfficialContentRevisions()
        {
            OpenBootstrap();
            var messagePath = Folder + "/Content/Message.txt";
            SessionState.SetString(Key + "originalMessage", Convert.ToBase64String(File.ReadAllBytes(messagePath)));
            PreparePlay();
            yield return new EnterPlayMode();
            yield return VerifyContentRevision("Hello from official Addressables");
            yield return new ExitPlayMode();

            BuildSecondRevision();
            PreparePlay();
            yield return new EnterPlayMode();
            yield return VerifyContentRevision("Hello from explicit official revision two");
            yield return new ExitPlayMode();
            RestoreOriginalMessage();
            AssertInputsUnchanged();
        }

        private static void BuildSecondRevision()
        {
            EnsureRestorableScenes();
            var messagePath = Folder + "/Content/Message.txt";
            var original = InputImages().Single(item => item.Path == messagePath);
            Assert.That(Hash(messagePath), Is.EqualTo(original.Sha256));
            File.WriteAllText(messagePath, "Hello from explicit official revision two", new UTF8Encoding(false));
            AssetDatabase.ImportAsset(messagePath, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.AssetPathToGUID(messagePath), Is.EqualTo(original.Guid));
            var archive = Path.Combine(SessionState.GetString(Key + "evidence", ""), "revision-two-content-state.bin");
            var built = ResourceContentBuildExample.Build(Settings(), archive);
            Assert.That(File.Exists(built.Result.OutputPath), Is.True);
        }

        // UTF restores the outer iterator position after reload, not its captured locals.
        private static IEnumerator VerifyContentRevision(string expected)
        {
            Assert.That(Application.isPlaying, Is.True);
            var messageGuid = InputImages().Single(item => item.Path == Folder + "/Content/Message.txt").Guid;
            var example = Example();
            Assert.That(example.message.AssetGUID, Is.EqualTo(messageGuid));
            var run = example.StartExampleAsync();
            yield return Wait(() => run.IsCompleted, "real official content revision: " + expected);
            Assert.That(run.GetAwaiter().GetResult(), Does.StartWith(expected + " / Scope-owned borrowed data / "));
            _lastSystem = example.Startup.Resources;
            AssertOfficialProvider(messageGuid, typeof(TextAsset));
            var stop = example.StopExampleAsync();
            yield return Wait(() => stop.IsCompleted, "actual revision shutdown");
            stop.GetAwaiter().GetResult();
            AssertClosed(_lastSystem);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
        }

        [UnityTest]
        public IEnumerator CancellationInsideActualOfficialProvideDrainsLateWorkWithoutPublishingRunning()
        {
            PreparePlay();
            yield return new EnterPlayMode();
            yield return CancelDuringOfficialProvide();
            yield return new ExitPlayMode();
        }

        private static IEnumerator CancelDuringOfficialProvide()
        {
            Assert.That(Application.isPlaying, Is.True);
            var example = Example();
            using var cancellation = new CancellationTokenSource();
            var previous = Addressables.InternalIdTransformFunc;
            var triggered = false;
            var wasStillPending = false;
            Task<string> run = null;
            Func<IResourceLocation, string> hook = location =>
            {
                var value = previous == null ? location.InternalId : previous(location);
                var system = example.Startup?.Resources;
                if (!triggered && location.Data is AssetBundleRequestOptions && system != null && system.GetDiagnostics().ScopeCount != 0)
                {
                    triggered = true;
                    _lastSystem = system;
                    wasStillPending = run != null && !run.IsCompleted;
                    cancellation.Cancel();
                }
                return value;
            };
            Addressables.InternalIdTransformFunc = hook;
            try
            {
                run = example.StartExampleAsync(cancellation.Token);
                yield return Wait(() => run.IsCompleted, "cancellation plus the original owner's actual cleanup");
                Assert.That(triggered && wasStillPending, Is.True, "Cancel from the real official bundle-provider start, not before any native work.");
                Assert.That(run.IsCanceled, Is.True, run.Exception?.ToString());
                Assert.That(example.Result, Does.StartWith("Stopped: owned cleanup completed."));
                Assert.That(example.Result, Does.Not.StartWith("Running:"));
                Assert.That(example.Startup.Resources, Is.Null);
                AssertClosed(_lastSystem);
                Assert.That(CoreResourceAccess.Provider, Is.Null);
                Assert.That(SceneManager.GetSceneByPath(Bootstrap).isLoaded, Is.True);
                Assert.That(ResourceStartupExample.PendingShutdownCount, Is.Zero);
            }
            finally
            {
                Assert.That(Addressables.InternalIdTransformFunc, Is.SameAs(hook), "Do not overwrite an unrelated transform owner.");
                Addressables.InternalIdTransformFunc = previous;
            }
        }

        [UnityTest]
        public IEnumerator DestroyedOwnerRetainsBusyCleanupUntilTheRealIndependentLeaseReturns()
        {
            PreparePlay();
            yield return new EnterPlayMode();
            yield return VerifyDestroyedOwnerCleanup();
            yield return new ExitPlayMode();
        }

        private static IEnumerator VerifyDestroyedOwnerCleanup()
        {
            Assert.That(Application.isPlaying, Is.True);
            var example = Example();
            var run = example.StartExampleAsync();
            yield return Wait(() => run.IsCompleted, "sample running before owner destruction");
            run.GetAwaiter().GetResult();
            _lastSystem = example.Startup.Resources;
            using var acquired = _lastSystem.AcquireAsync<TextAsset>(example.message.AssetGUID);
            yield return Wait(() => acquired.IsDone, "independent real sample consumer");
            using var temporary = acquired.TakeResult();
            using var retained = temporary.Retain("ResourceStartupExampleTests/external-owner");
            temporary.Dispose();
            var owner = example.gameObject;
            LogAssert.Expect(LogType.Error, new Regex("Resource startup example did not complete:"));
            Object.Destroy(owner);
            yield return Wait(() => ResourceStartupExample.PendingShutdownCount == 1 &&
                ResourceStartupExample.PendingShutdownDetails.Contains("Shutdown incomplete"), "destroyed owner retains its failed cleanup responsibility");
            Assert.That((bool)owner, Is.False);
            Assert.That(ResourceStartupExample.PendingShutdownDetails, Does.Contain("ResourceStartupExampleTests/external-owner"));
            Assert.That(retained.IsValid && retained.Asset, Is.True, "The destroyed application owner must not steal an independent consumer's lease.");
            Assert.That(ResourceSystem.Current, Is.SameAs(_lastSystem));
            Assert.That(_lastSystem.State, Is.EqualTo(ResourceSystemState.ShuttingDown));
            CollectionAssert.AreEquivalent(new[] { "Core.ApplicationScenes" }, _lastSystem.GetDiagnostics().Scopes.Select(scope => scope.Owner),
                "The sample scope is closed; Busy preserves the application's internal scene scope until retry.");
            Assert.That(SceneManager.GetSceneByPath(Business).isLoaded, Is.False);
            Assert.That(SceneManager.GetSceneByPath(Bootstrap).isLoaded, Is.True);
            retained.Dispose();
            var retry = ResourceStartupExample.RetryPendingShutdownsAsync();
            Assert.That(ResourceStartupExample.PendingShutdownCount, Is.EqualTo(1), "A retry request is not already completed ownership cleanup.");
            yield return Wait(() => retry.IsCompleted, "retained plain owner actually stops after lease return");
            retry.GetAwaiter().GetResult();
            Assert.That(ResourceStartupExample.PendingShutdownCount, Is.Zero);
            AssertClosed(_lastSystem);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
        }

        [UnityTearDown]
        public IEnumerator ExitOnlyAfterOwnedRuntimeCleanup()
        {
            if (!SessionState.GetBool(Key + "owned", false))
            {
                yield break;
            }
            var cleanup = CleanupRuntime();
            Exception failure = null;
            while (true)
            {
                bool next = false;
                try
                {
                    next = cleanup.MoveNext();
                }
                catch (Exception error)
                {
                    failure = error;
                }
                if (failure != null || !next)
                {
                    break;
                }
                yield return cleanup.Current;
            }
            (cleanup as IDisposable)?.Dispose();
            if (failure != null)
            {
                SessionState.SetString(Key + "cleanupFailure", failure.ToString());
            }
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }
            RestoreOriginalMessage();
            OpenBootstrap();
            AssertInputsUnchanged();
            var text = SessionState.GetString(Key + "cleanupFailure", "");
            SessionState.EraseString(Key + "cleanupFailure");
            Assert.That(text, Is.Empty, "Native cleanup did not finish: " + text);
        }

        private static IEnumerator CleanupRuntime()
        {
            if (!EditorApplication.isPlaying)
            {
                yield break;
            }
            foreach (var example in Object.FindObjectsOfType<ResourceStartupExample>().Where(item => item.gameObject.scene.path == Bootstrap))
            {
                var stop = example.StopExampleAsync();
                var until = Time.realtimeSinceStartupAsDouble + 30;
                while (!stop.IsCompleted && Time.realtimeSinceStartupAsDouble < until)
                {
                    yield return null;
                }
                Assert.That(stop.IsCompleted, Is.True, "Keep the real owner until cleanup completes.");
                stop.GetAwaiter().GetResult();
            }
            var retry = ResourceStartupExample.RetryPendingShutdownsAsync();
            var deadline = Time.realtimeSinceStartupAsDouble + 30;
            while (!retry.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }
            Assert.That(retry.IsCompleted, Is.True);
            retry.GetAwaiter().GetResult();
            Assert.That(ResourceStartupExample.PendingShutdownCount, Is.Zero);
            Assert.That(ResourceSystem.Current, Is.Null);
            if (_lastSystem != null)
            {
                AssertClosed(_lastSystem);
            }
        }

        [OneTimeTearDown]
        public void CleanupOnlyTheExactOwnedFormalExample()
        {
            if (!SessionState.GetBool(Key + "owned", false))
            {
                return;
            }
            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(ResourceStartupExample.PendingShutdownCount, Is.Zero);
            RestoreOriginalMessage();
            AssertInputsUnchanged();
            EnsureRestorableScenes();
            var saved = JsonUtility.FromJson<Scenes>(SessionState.GetString(Key + "scenes", ""));
            if (saved.Items.Length == 0)
            {
                EditorSceneManager.OpenScene(Empty, OpenSceneMode.Single);
            }
            else
            {
                EditorSceneManager.RestoreSceneManagerSetup(saved.Items.Select(scene => new SceneSetup
                {
                    path = scene.Path,
                    isLoaded = scene.Loaded,
                    isActive = scene.Active
                }).ToArray());
            }
            VerifyOwnedFolder();
            var settings = Settings();
            Assert.That(EditorUtility.IsDirty(settings), Is.False);
            var groupGuid = SessionState.GetString(Key + "group", "");
            var group = settings.groups.Single(item => item && AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(item)) == groupGuid);
            Assert.That(GroupIdentity(group), Is.EqualTo(SessionState.GetString(Key + "groupIdentity", "")), "Never delete changed/foreign schema or entry identities.");
            Assert.That(group.entries.All(item => item.AssetPath.StartsWith(Folder + "/Content/", StringComparison.Ordinal)), Is.True);
            var foreign = Entries(settings, groupGuid);
            settings.RemoveGroup(group);
            AssetDatabase.SaveAssetIfDirty(settings);
            CollectionAssert.AreEqual(foreign, Entries(settings, null));
            Assert.That(AssetDatabase.DeleteAsset(Folder), Is.True);
            if (GlobalObjectId.TryParse(SessionState.GetString(Key + "selection", ""), out var selected))
            {
                Selection.activeObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(selected);
            }
            Assert.That(ProtectedDigest(), Is.EqualTo(SessionState.GetString(Key + "protected", "")));
            foreach (var name in new[] { "folder", "guid", "group", "groupIdentity", "sample", "scenes", "selection", "protected", "inputs", "dataGuid", "dataId", "evidence" })
            {
                SessionState.EraseString(Key + name);
            }
            SessionState.EraseBool(Key + "owned");
        }

        private static ResourceStartupExample Example() => Object.FindObjectsOfType<ResourceStartupExample>().Single(example => example.gameObject.scene.path == Bootstrap);
        private static AddressableAssetSettings Settings()
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            Assert.That(settings, Is.Not.Null, "Prepare official default Settings before the sample fixture.");
            return settings;
        }
        private static void PreparePlay()
        {
            RequireOwner(Directory.GetParent(Application.dataPath).FullName, "Host");
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.False);
            OpenBootstrap();
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            Assert.That(ResourceStartupExample.PendingShutdownCount, Is.Zero);
            _lastSystem = null;
        }
        private static void OpenBootstrap()
        {
            EnsureRestorableScenes();
            VerifyOwnedFolder();
            if (SceneManager.sceneCount != 1 || SceneManager.GetActiveScene().path != Bootstrap)
            {
                EditorSceneManager.OpenScene(Bootstrap, OpenSceneMode.Single);
            }
        }
        private static void EnsureRestorableScenes()
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.isDirty || string.IsNullOrEmpty(scene.path) && scene.rootCount != 0)
                {
                    throw new InvalidOperationException("Save caller scenes explicitly; no dirty or unsaved nonempty scene may be closed: " + scene.name);
                }
            }
        }

        internal static void UseSavedAuthoringAnchor()
        {
            var active = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(active.path))
            {
                return;
            }
            RequireOwner(Directory.GetParent(Application.dataPath).FullName, "Host");
            var anchor = SceneManager.GetSceneByPath(Empty);
            Assert.That(Application.isBatchMode && SceneManager.sceneCount == 2 && active.IsValid() && active.isLoaded &&
                !active.isDirty && active.rootCount == 0 && anchor.IsValid() && anchor.isLoaded && !anchor.isDirty && anchor.rootCount == 0,
                Is.True, "Only the owned UTF empty runner beside the saved preparation scene can be closed.");
            // An untitled UTF scene blocks additive authoring. Keep the saved anchor and close only the runner.
            Assert.That(SceneManager.SetActiveScene(anchor), Is.True);
            Assert.That(EditorSceneManager.CloseScene(active, true), Is.True);
            Assert.That(SceneManager.sceneCount == 1 && SceneManager.GetActiveScene().handle == anchor.handle, Is.True);
        }
        private static void VerifyOwnedFolder()
        {
            Assert.That(Folder, Does.StartWith("Assets/YLResourceStartupExample"));
            Assert.That(AssetDatabase.AssetPathToGUID(Folder), Is.EqualTo(SessionState.GetString(Key + "guid", "")));
        }
        private static IEnumerator Wait(Func<bool> condition, string stage)
        {
            var until = Time.realtimeSinceStartupAsDouble + 30;
            while (!condition() && Time.realtimeSinceStartupAsDouble < until)
            {
                yield return null;
            }
            Assert.That(condition(), Is.True, stage);
        }
        private static void AssertClosed(ResourceSystem system)
        {
            Assert.That(system, Is.Not.Null);
            var report = system.GetDiagnostics();
            Assert.That(report.State, Is.EqualTo(ResourceSystemState.Closed));
            Assert.That(report.LeaseCount, Is.Zero);
            Assert.That(report.ScopeCount, Is.Zero);
            Assert.That(report.RequestCount, Is.Zero);
            Assert.That(report.PendingWorkCount, Is.Zero);
            Assert.That(report.DrainingHandleCount, Is.Zero);
            Assert.That(report.Errors, Is.Empty);
            Assert.That(ResourceSystem.Current, Is.Null);
        }
        private static void AssertOfficialProvider(string key, Type type)
        {
            var locations = new List<IResourceLocation>();
            foreach (var locator in Addressables.ResourceLocators)
            {
                if (locator.Locate(key, type, out var matches))
                {
                    locations.AddRange(matches);
                }
            }
            var selected = locations.Distinct().Single();
            Assert.That(Addressables.ResourceManager.ResourceProviders.Single(provider => provider.ProviderId == selected.ProviderId),
                Is.InstanceOf<BundledAssetProvider>(), "The saved sample consumes its real official build, not an Editor substitute.");
        }
        private static Input[] InputImages() => JsonUtility.FromJson<Inputs>(SessionState.GetString(Key + "inputs", "")).Items;
        private static void RestoreOriginalMessage()
        {
            var encoded = SessionState.GetString(Key + "originalMessage", "");
            if (encoded == "")
            {
                return;
            }
            Assert.That(EditorApplication.isPlaying, Is.False);
            EnsureRestorableScenes();
            VerifyOwnedFolder();
            var path = Folder + "/Content/Message.txt";
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(InputImages().Single(item => item.Path == path).Guid));
            File.WriteAllBytes(path, Convert.FromBase64String(encoded));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            AssertInputsUnchanged();
            var archive = Path.Combine(SessionState.GetString(Key + "evidence", ""), "restored-content-state-" + Guid.NewGuid().ToString("N") + ".bin");
            ResourceContentBuildExample.Build(Settings(), archive);
            SessionState.EraseString(Key + "originalMessage");
        }
        private static void AssertInputsUnchanged()
        {
            foreach (var item in InputImages())
            {
                Assert.That(Hash(item.Path), Is.EqualTo(item.Sha256), item.Path);
                Assert.That(AssetDatabase.AssetPathToGUID(item.Path), Is.EqualTo(item.Guid), item.Path);
            }
        }
        private static string GroupIdentity(AddressableAssetGroup group) => group.Guid + "\n" + string.Join("\n", group.Schemas
            .Select(schema => schema.GetType().FullName + "=" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(schema)))) + "\n" +
            string.Join("\n", group.entries.Select(entry => entry.guid + "=" + entry.address).OrderBy(value => value, StringComparer.Ordinal));
        private static string[] Entries(AddressableAssetSettings settings, string except) => settings.groups.Where(group => group &&
            AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(group)) != except).SelectMany(group => group.entries.Select(entry =>
                group.Guid + "|" + entry.guid + "|" + entry.address + "|" + string.Join(",", entry.labels.OrderBy(label => label, StringComparer.Ordinal))))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        private static string Hash(string path)
        {
            using var input = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
        private static string ProtectedDigest()
        {
            var values = new List<string>();
            foreach (var item in new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "Packages/com.kano.ylframework/Prefabs", "AssetBundles/MainModule", "Packages", "ProjectSettings" })
            {
                var path = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(item));
                NoLinks(path);
                values.Add(item + "=" + Directory.Exists(path));
                if (!Directory.Exists(path))
                {
                    continue;
                }
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories).OrderBy(file => file, StringComparer.Ordinal))
                {
                    NoLinks(file);
                    values.Add(item + "/" + file.Substring(path.Length + 1).Replace('\\', '/') + "=" + Hash(file));
                }
            }
            foreach (var item in new[] { "AGENTS.md", ".kilo/kilo.jsonc", ".kilo/plans/1791960935353-ui-panel-redesign.md" })
            {
                var path = global::YLFramework.Testing.PackageTestPaths.Physical(item);
                NoLinks(path);
                values.Add(item + "=" + (File.Exists(path) ? Hash(path) : "absent"));
            }
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", values)))).Replace("-", "").ToLowerInvariant();
        }
        private static void RequireOwner(string path, string kind)
        {
            NoLinks(path);
            var ownerPath = Path.Combine(path, ".yl-owner.json");
            NoLinks(ownerPath);
            Assert.That(File.Exists(ownerPath), Is.True);
            var owner = JsonUtility.FromJson<DirectoryOwner>(File.ReadAllText(ownerPath));
            Assert.That(owner.schemaVersion == 1 && owner.purpose == "YLTesting.Directory" && owner.kind == kind &&
                Path.GetFullPath(owner.path).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase), Is.True);
        }
        private static void NoLinks(string path)
        {
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    Assert.That((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, Is.True);
                }
            }
        }
    }
}
