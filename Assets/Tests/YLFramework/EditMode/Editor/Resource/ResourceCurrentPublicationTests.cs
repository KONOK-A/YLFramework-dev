using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Testing;
using YLFramework.Testing.EditorFixtures;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceCurrentPublicationTests
    {
        private const string Source = "Assets/Tests/YLFramework/Fixtures/";

        [Test]
        [Category("YL.UPM")]
        [Category("YL.UPM.Generators")]
        public void ExplicitAcceptanceProbeIsAbsentFromOrdinaryPlayerCompilation()
        {
            const string probeAssembly = "YLFramework.Tests.ResourceCurrent.Player";
            var editor = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Editor);
            Assert.That(editor.Count(assembly => assembly.name == probeAssembly), Is.EqualTo(1));
            var player = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies);
            Assert.That(player.Any(assembly => assembly.name == probeAssembly), Is.False,
                "An ordinary Player must not compile an acceptance probe without its explicit build symbol.");
        }

        [Test]
        [Category("YL.UPM")]
        [Category("YL.UPM.Generators")]
        public void FrameworkPrefabsAndScenesDoNotContainResourceBootstrapComponents()
        {
            var paths = new[]
            {
                "Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab",
                "Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/EventSystem.prefab",
                Source + "Core/YLCoreSmokeModule/YLCoreSmokeScene.unity",
                Source + "Core/YLCoreResourceModule/YLCoreSmokeScene.unity"
            };
            foreach (var path in paths)
            {
                Assert.That(File.ReadAllText(PackageTestPaths.Physical(path)), Does.Not.Contain("ResourceStartup").And.Not.Contain("ResourceBootstrap"), path);
                foreach (string guid in AssetDatabase.GetDependencies(path).Select(AssetDatabase.AssetPathToGUID))
                {
                    var dependency = AssetDatabase.GUIDToAssetPath(guid);
                    if (dependency.EndsWith(".cs", StringComparison.Ordinal))
                        Assert.That(Path.GetFileNameWithoutExtension(dependency), Does.Not.Contain("ResourceStartup").And.Not.Contain("ResourceBootstrap"), path);
                }
            }
            var methods = typeof(ResourceSystem).Assembly.GetTypes().Where(type => type.Namespace?.StartsWith("YLFramework.Resource", StringComparison.Ordinal) == true)
                .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
            Assert.That(methods.Where(method => method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>() != null)
                .All(method => method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>().loadType == RuntimeInitializeLoadType.SubsystemRegistration), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("YL.UPM.Generators")]
        public void OfficialSettingsRoundTripPreservesEntryLabelSourceAndMetadataBytes(bool controlledStartup)
        {
            AssertOwnedHost();
            ResourceAddressablesTestContent.BuildForConsumers();
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            settings.DisableCatalogUpdateOnStartup = controlledStartup;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            var path = AssetDatabase.GetAssetPath(settings);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var bytes = File.ReadAllBytes(path);
            var meta = File.ReadAllBytes(path + ".meta");
            var entries = Entries(settings);
            var labels = settings.GetLabels().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            Assert.That(entries, Is.Not.Empty);
            Resources.UnloadAsset(settings);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            settings = AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>(path);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(settings.DisableCatalogUpdateOnStartup, Is.EqualTo(controlledStartup));
            Assert.That(Entries(settings), Is.EqualTo(entries));
            Assert.That(settings.GetLabels().OrderBy(value => value, StringComparer.Ordinal), Is.EqualTo(labels));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            Assert.That(File.ReadAllBytes(path + ".meta"), Is.EqualTo(meta));
        }

        [Test]
        [Category("YL.UPM.Generators")]
        public void ResourceAuthorWindowUsesOfficialSettingsWithoutPrivateBuildRecipeFields()
        {
            AssertOwnedHost();
            var fields = typeof(ResourceWorkbenchWindow).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Single(field => field.Name == "settings").FieldType, Is.EqualTo(typeof(AddressableAssetSettings)));
            Assert.That(fields.Any(field => field.FieldType == typeof(ResourceProject) || field.FieldType == typeof(ResourceBuildRecipe)), Is.False);
            ResourceAddressablesTestContent.BuildForConsumers();
            var window = ScriptableObject.CreateInstance<ResourceWorkbenchWindow>();
            var copy = ScriptableObject.CreateInstance<ResourceWorkbenchWindow>();
            try
            {
                using var serialized = new SerializedObject(window);
                foreach (var field in fields.Where(field => field.GetCustomAttribute<SerializeField>() != null))
                {
                    var property = serialized.FindProperty(field.Name);
                    Assert.That(property, Is.Not.Null, field.Name);
                    if (field.FieldType == typeof(AddressableAssetSettings))
                        property.objectReferenceValue = AddressableAssetSettingsDefaultObject.GetSettings(false);
                    else if (field.FieldType == typeof(string))
                        property.stringValue = "current-" + field.Name;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var json = EditorJsonUtility.ToJson(window);
                EditorJsonUtility.FromJsonOverwrite(json, copy);
                Assert.That(EditorJsonUtility.ToJson(copy), Is.EqualTo(json));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        [Category("YL.UPM.Generators")]
        public void ResourceCurrentPlayerProbeHasNoTestOnlyPlayerDependencies()
        {
            var definition = JObject.Parse(File.ReadAllText(Source + "ResourceCurrent/YLFramework.Tests.ResourceCurrent.Player.asmdef"));
            Assert.That(((JArray)definition["references"]).Values<string>(), Is.EquivalentTo(new[] { "YLFramework.Runtime", "Unity.Addressables" }));
            Assert.That(((JArray)definition["defineConstraints"]).Values<string>(), Is.EqualTo(new[] { "UNITY_EDITOR || YL_RESOURCE_CURRENT_ACCEPTANCE" }));
            Assert.That((bool)definition["autoReferenced"], Is.False);
            Assert.That(((JArray)definition["precompiledReferences"]).Count, Is.Zero);
            Assert.That(((JArray)definition["versionDefines"]).Count, Is.Zero);
        }

        [UnityTest]
        [Category("YL.Special.ResourceCurrent")]
        [Category("YL.NativeGraphics")]
        public IEnumerator OfficialContentLoadsInTwoIndependentOrdinaryPlayersWithIsolatedOwnership()
        {
            AssertOwnedHost();
            ResourceAddressablesTestContent.BuildForConsumers();
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            var name = "__YLCurrentOfficial-" + Guid.NewGuid().ToString("N");
            var root = "Assets/" + name;
            var folderGuid = AssetDatabase.CreateFolder("Assets", name);
            Assert.That(folderGuid, Is.Not.Empty);
            AddressableAssetGroup group = null;
            try
            {
                group = settings.CreateGroup(name, false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
                schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
                schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
                foreach (var value in new[] { "first", "second" })
                {
                    var path = root + "/" + value + ".txt";
                    File.WriteAllText(path, "current resource " + value);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group).SetAddress("resource-current/" + value);
                }
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                var evidence = Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT");
                Assert.That(Directory.Exists(evidence), Is.True);
                var report = ResourceEditorOperations.BuildContent(settings, Path.Combine(evidence, "current-official-" + Guid.NewGuid().ToString("N") + ".bin"));
                var hash = StandaloneFixturePlayer.ContentHash(report);
                StandaloneFixturePlayer.BuildAndRun(evidence, root, "YLFramework.Runtime", report, _ =>
                {
                    var type = Type.GetType("YLFramework.Tests.ResourceBuild.ResourceCurrentPlayerProbe, YLFramework.Tests.ResourceCurrent.Player", true);
                    var probe = new GameObject("OfficialCurrentPlayer").AddComponent(type);
                    type.GetField("firstAddress").SetValue(probe, "resource-current/first");
                    type.GetField("secondAddress").SetValue(probe, "resource-current/second");
                    type.GetField("contentHash").SetValue(probe, hash);
                }, new[] { "YL_RESOURCE_CURRENT_ACCEPTANCE" });
                for (var run = 1; run <= 2; run++)
                {
                    var result = JObject.Parse(File.ReadAllText(Path.Combine(evidence, "StandalonePlayer-" + run + ".json")));
                    Assert.That((bool)result["resourceLoaded"], Is.True);
                    Assert.That((bool)result["independentHeldAfterScopeClose"], Is.True);
                    Assert.That((string)result["probeAssembly"], Is.EqualTo("YLFramework.Tests.ResourceCurrent.Player"));
                    foreach (var field in new[] { "assemblies", "probeReferences" })
                    {
                        var names = result[field].Values<string>().ToArray();
                        Assert.That(names, Is.Not.Empty, field);
                        Assert.That(names.Any(IsEditorOrTestDependency), Is.False, field);
                    }
                }

                var player = File.ReadAllText(Path.Combine(evidence, "standalone-player-location.txt"));
                var shipped = Directory.GetFiles(Path.Combine(player, "P_Data/Managed"), "*.dll").Select(Path.GetFileNameWithoutExtension).ToArray();
                Assert.That(shipped, Does.Contain("YLFramework.Tests.ResourceCurrent.Player"));
                Assert.That(shipped.Any(IsEditorOrTestDependency), Is.False, "The executable must not ship Editor, NUnit or TestRunner assemblies.");
            }
            finally
            {
                Assert.That(AssetDatabase.AssetPathToGUID(root), Is.EqualTo(folderGuid), "Do not remove a replaced output folder.");
                if (group)
                {
                    Assert.That(group.Settings, Is.SameAs(settings));
                    settings.RemoveGroup(group);
                    AssetDatabase.SaveAssetIfDirty(settings);
                }
                Assert.That(AssetDatabase.DeleteAsset(root), Is.True);
            }
            yield break;
        }

        private static bool IsEditorOrTestDependency(string name) => name.StartsWith("UnityEditor", StringComparison.Ordinal) ||
            name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0 || name.StartsWith("nunit", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("YLFramework.Tests.Fixtures", StringComparison.Ordinal) || name.StartsWith("YLFramework.Core.Tests", StringComparison.Ordinal);

        private static string[] Entries(AddressableAssetSettings settings) => settings.groups.Where(group => group)
            .SelectMany(group => group.entries).Select(entry => entry.guid + ":" + entry.address + ":" + string.Join(",", entry.labels.OrderBy(value => value, StringComparer.Ordinal)))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

        private static void AssertOwnedHost()
        {
            var owner = JObject.Parse(File.ReadAllText(".yl-owner.json"));
            Assert.That((string)owner["kind"], Is.EqualTo("Host"));
            Assert.That(Application.isBatchMode, Is.True, "Never build or change scenes in a user's Editor.");
        }
    }
    [Category("YL.Special.ResourceCurrent")]
    [Category("YL.NativeGraphics")]
    public class ResourceEventWorkbenchTests
    {
        private const string SecretKey = "test-key-sentinel";
        private const string SecretOwner = "test-owner-sentinel";
        private const string SecretUrl = "https://fixture.invalid/?token=url-sentinel";
        private const string SecretCause = "test-inner-exception-sentinel";
        private static readonly Guid DomainToken = Guid.NewGuid();
        private bool _oldEnabled, _oldFlag;
        private EnterPlayModeOptions _oldOptions;
        private Guid _domain;
        private ResourceSystem _previousService;
        private ulong _previousFingerprint;
        private ResourceWorkbenchWindow _window;

        [SetUp]
        public void Prepare()
        {
            Assert.That((string)JObject.Parse(File.ReadAllText(".yl-owner.json"))["kind"], Is.EqualTo("Host"));
            Assert.That(Application.isBatchMode, Is.True);
            Assert.That(Resources.FindObjectsOfTypeAll<ResourceWorkbenchWindow>(), Is.Empty);
            _oldEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _oldOptions = EditorSettings.enterPlayModeOptions;
            _oldFlag = SessionState.GetBool(ResourceEventHistory.NextPlayFlag, false);
            _domain = DomainToken;
            ResourceAddressablesTestContent.BuildForConsumers();
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = _oldOptions | EnterPlayModeOptions.DisableDomainReload;
            SessionState.EraseBool(ResourceEventHistory.NextPlayFlag);
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            if (_window)
                _window.Close();
            if (Application.isPlaying)
                yield return new ExitPlayMode();
            EditorSettings.enterPlayModeOptions = _oldOptions;
            EditorSettings.enterPlayModeOptionsEnabled = _oldEnabled;
            SessionState.SetBool(ResourceEventHistory.NextPlayFlag, _oldFlag);
        }

        [UnityTest]
        public IEnumerator FirstOpenShowsReleasedProviderFailureBeforeAndAfterStopWithoutCapture()
        {
            ResourceWorkbenchWindow.ArmNextPlay();
            yield return new EnterPlayMode(false);
            Assert.That(DomainToken, Is.EqualTo(_domain));
            yield return OpenAfterFailure(false);
            yield return OpenAfterFailure(true);
            yield return new ExitPlayMode();
            Assert.That(ResourceEventHistory.Count, Is.Zero);
            yield return new EnterPlayMode(false);
            Assert.That(DomainToken, Is.EqualTo(_domain));
            yield return VerifyDefaultOff();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ArmedPlayWithoutServiceConsumesFlagBeforeFollowingPlay()
        {
            ResourceWorkbenchWindow.ArmNextPlay();
            yield return new EnterPlayMode(false);
            Assert.That(ResourceSystem.Current, Is.Null);
            yield return new ExitPlayMode();
            yield return new EnterPlayMode(false);
            yield return VerifyDefaultOff();
            yield return new ExitPlayMode();
        }

        private static IEnumerator VerifyDefaultOff()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(() => starting.IsDone);
            var system = starting.TakeResult().Resources;
            Assert.That(ResourceEventHistory.IsRecording, Is.False);
            using var rejected = system.CreateScope("off").LoadAsync<TextAsset>("");
            Assert.That(rejected.IsDone, Is.True);
            Assert.That(ResourceEventHistory.Read(), Is.Empty);
            using var stop = startup.StopAsync();
            yield return Wait(() => stop.IsDone);
            stop.TakeResult();
        }

        private IEnumerator OpenAfterFailure(bool stopFirst)
        {
            var startup = new ResourceStartup(new ResourceConfiguration(diagnosticHistoryCapacity: 4), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(() => starting.IsDone);
            var system = starting.TakeResult().Resources;
            Assert.That(ResourceEventHistory.IsRecording, Is.True);
            Assert.That(ResourceEventHistory.Read(), Is.Empty, "Starting another service clears the previous service's events.");
            if (_previousService != null)
            {
                using var stale = _previousService.AcquireAsync<TextAsset>(SecretKey);
                Assert.That(stale.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(ResourceEventHistory.Count, Is.Zero, "A stopped service must not insert rows into its successor.");
            }
            using var scope = system.CreateScope(SecretOwner);
            using var provider = new EventProvider();
            int snapshotId = 0;
            int expectedSnapshots = stopFirst ? 0 : 1;
            using (var request = scope.LoadAsync<TextAsset>(SecretKey))
            {
                if (!stopFirst)
                {
                    var snapshot = system.GetDiagnostics();
                    snapshotId = snapshot.Requests.Single(item => item.Key == SecretKey && item.Operation == nameof(ResourceScope.LoadAsync)).Id;
                    Assert.That(snapshotId, Is.Positive);
                }
                yield return Wait(() => request.IsDone);
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(request.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
                Assert.That(request.Error.ToString(), Does.Contain(SecretCause));
            }
            Assert.That(provider.Failures, Is.EqualTo(1));
            Assert.That(system.GetDiagnosticsHistory().Count, Is.EqualTo(expectedSnapshots));
            if (stopFirst)
            {
                using var stop = startup.StopAsync();
                yield return Wait(() => stop.IsDone);
                stop.TakeResult();
                Assert.That(ResourceSystem.Current, Is.Null);
            }
            OpenAndRepaintWorkbench();
            yield return Wait(() => _window.DisplayedEvents.Any(value => value.Contains("ScopeLoad") && value.Contains("LoadFailed")));
            var row = ResourceEventHistory.Read().Single(item => item.Source == ResourceEventSource.ScopeLoad && item.Kind == ResourceEventKind.Terminal);
            Assert.That(typeof(ResourceEventRow).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .All(field => field.FieldType.IsPrimitive || field.FieldType.IsEnum || Nullable.GetUnderlyingType(field.FieldType)?.IsEnum == true),
                Is.True, "Rows must contain only fixed-size values.");
            if (!stopFirst)
                Assert.That(row.DiagnosticId, Is.EqualTo(snapshotId));
            Assert.That(row.KeyFingerprint, Is.Not.Zero);
            Assert.That(row.OwnerFingerprint, Is.Not.Zero);
            Assert.That(row.KeyFingerprint, Is.Not.EqualTo(_previousFingerprint));
            _previousFingerprint = row.KeyFingerprint;
            _previousService = system;
            Assert.That(row.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(0));
            var line = _window.DisplayedEvents.Single(value => value.Contains("ScopeLoad") && value.Contains("Terminal"));
            if (!stopFirst)
                Assert.That(line, Does.Contain($"Snapshot DiagnosticId {snapshotId} "));
            Assert.That(line, Does.Contain(row.KeyFingerprint.ToString("X16")).And.Contain(row.OwnerFingerprint.ToString("X16")).And.Contain(" ms"));
            foreach (var secret in new[] { SecretKey, SecretOwner, SecretUrl, SecretCause })
                Assert.That(string.Join(" ", _window.DisplayedEvents), Does.Not.Contain(secret));
            Assert.That(system.GetDiagnosticsHistory().Count, Is.EqualTo(expectedSnapshots));
            _window.Close();
            if (!stopFirst)
            {
                Assert.That(ResourceEventHistory.IsRecording, Is.True);
                OpenAndRepaintWorkbench();
                yield return Wait(() => _window.DisplayedEvents.Contains(line));
                _window.Close();
                yield return VerifyCapacityAndOwnership(system, scope, provider);
                using var stop = startup.StopAsync();
                yield return Wait(() => stop.IsDone);
                stop.TakeResult();
            }
        }

        private void OpenAndRepaintWorkbench()
        {
            ResourceWorkbenchWindow.OpenWorkbench();
            _window = Resources.FindObjectsOfTypeAll<ResourceWorkbenchWindow>().Single();
            _window.SendEvent(new Event { type = EventType.Layout });
            _window.SendEvent(new Event { type = EventType.Repaint });
        }

        private static IEnumerator VerifyCapacityAndOwnership(ResourceSystem system, ResourceScope scope, EventProvider provider)
        {
            using var load = system.AcquireAsync<TextAsset>(provider.GoodKey);
            yield return Wait(() => load.IsDone);
            using var lease = load.TakeResult();
            Assert.That(lease.Asset, Is.SameAs(provider.Asset));
            ResourceEventHistory.Clear();
            for (int i = 0; i < 257; i++)
            {
                using var rejected = scope.LoadAsync<TextAsset>("");
                Assert.That(rejected.IsDone, Is.True);
            }
            var rows = ResourceEventHistory.Read();
            Assert.That(rows.Select(row => row.Sequence), Is.EqualTo(Enumerable.Range(2, 256).Select(i => (long)i)));
            Assert.That(rows.All(row => row.KeyFingerprint == 0 && row.DiagnosticId == 0 && row.Kind == ResourceEventKind.Terminal), Is.True);
            Assert.That(ResourceEventHistory.Dropped, Is.EqualTo(1));
            using var pending = system.AcquireAsync<TextAsset>(provider.PendingKey);
            yield return Wait(() => provider.Pending);
            ResourceEventHistory.Clear();
            provider.CompletePending();
            yield return Wait(() => pending.IsDone);
            using (var completed = pending.TakeResult())
                Assert.That(completed.Asset, Is.SameAs(provider.Asset));
            Assert.That(ResourceEventHistory.Read(), Is.Empty);
            Assert.That(ResourceEventHistory.Dropped, Is.Zero);
            using (var rejected = scope.LoadAsync<TextAsset>(""))
                Assert.That(rejected.IsDone, Is.True);
            ResourceEventHistory.SetRecording(false);
            using (var rejected = scope.LoadAsync<TextAsset>(""))
                Assert.That(rejected.IsDone, Is.True);
            Assert.That(ResourceEventHistory.Read().Length, Is.EqualTo(1));
            Assert.That(lease.Asset, Is.SameAs(provider.Asset));
            Assert.That(provider.GoodReleases, Is.Zero);
            lease.Dispose();
            yield return Wait(() => provider.GoodReleases == 1);
            ResourceEventHistory.SetRecording(true);
        }

        private static IEnumerator Wait(Func<bool> complete)
        {
            var limit = EditorApplication.timeSinceStartup + 30;
            while (!complete() && EditorApplication.timeSinceStartup < limit)
                yield return null;
            Assert.That(complete(), Is.True, "Timed out waiting for a resource or workbench repaint.");
        }

        private sealed class EventProvider : ResourceProviderBase, IDisposable
        {
            private readonly ResourceLocationMap _locator;
            private ProvideHandle _pending;
            internal bool Pending;
            internal int Failures, GoodReleases;
            internal readonly TextAsset Asset = new("independently owned");
            internal string GoodKey => ProviderId + "/good";
            internal string PendingKey => ProviderId + "/pending";

            internal EventProvider()
            {
                m_ProviderId = "workbench/" + Guid.NewGuid().ToString("N");
                _locator = new ResourceLocationMap(ProviderId);
                foreach (var key in new[] { SecretKey, GoodKey, PendingKey })
                    _locator.Add(key, new ResourceLocationBase(key, key == SecretKey ? SecretUrl : key, ProviderId, typeof(TextAsset)));
                Addressables.ResourceManager.ResourceProviders.Add(this);
                Addressables.AddResourceLocator(_locator);
            }

            public override void Provide(ProvideHandle handle)
            {
                if (handle.Location.InternalId == SecretUrl)
                {
                    Failures++;
                    if (ResourceManager.ExceptionHandler != null)
                        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(SecretCause));
                    handle.Complete<TextAsset>(null, false, new Exception("fixture failure", new Exception(SecretCause)));
                }
                else if (handle.Location.InternalId == PendingKey)
                {
                    Pending = true;
                    _pending = handle;
                }
                else
                    handle.Complete(Asset, true, null);
            }

            internal void CompletePending()
            {
                Pending = false;
                _pending.Complete(Asset, true, null);
                _pending = default;
            }

            public override void Release(IResourceLocation location, object asset)
            {
                if (location.InternalId == GoodKey)
                    GoodReleases++;
            }

            public void Dispose()
            {
                if (Pending)
                    CompletePending();
                Addressables.RemoveResourceLocator(_locator);
                Addressables.ResourceManager.ResourceProviders.Remove(this);
                UnityEngine.Object.DestroyImmediate(Asset);
            }
        }
    }
}
