using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.TestTools;
using YLFramework.Equipment;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Equipment
{
    [Category("YL.Special.EquipmentSession")]
    public class EquipmentTwoPlayTests
    {
        private SceneSetup[] _scenes;
        private string _folder;
        private bool _wasReloadEnabled;
        private EnterPlayModeOptions _reloadOptions;
        private bool _prepared;
        private Round _round;
        private static readonly string DomainToken = Guid.NewGuid().ToString("N");

        [SetUp]
        public void Prepare()
        {
            Assert.That(EditorApplication.isPlaying, Is.False);
            EnsureRestorableScenes();
            _scenes = EditorSceneManager.GetSceneManagerSetup().Where(scene => !string.IsNullOrEmpty(scene.path)).ToArray();
            _wasReloadEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _reloadOptions = EditorSettings.enterPlayModeOptions;
            var parent = "Assets/Tests/YLFramework/Fixtures/Extensions";
            Assert.That(AssetDatabase.IsValidFolder(parent), Is.True);
            var name = "EqTwoPlay-" + Guid.NewGuid().ToString("N");
            _folder = parent + "/" + name;
            AssetDatabase.CreateFolder(parent, name);
            _prepared = true;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, _folder + "/Empty.unity"), Is.True);
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            if (!_prepared) yield break;
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            try
            {
                if (_round?.Startup?.Resources != null)
                {
                    EquipmentManager.RemoveConfig(_round.Root);
                    using var stopping = _round.Startup.StopAsync();
                    yield return Wait(() => stopping.IsDone, "owned Equipment startup cleanup");
                    Assert.That(stopping.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stopping.Error?.ToString());
                }
                RemoveRound();
                EnsureRestorableScenes();
                if (_scenes.Length != 0) EditorSceneManager.RestoreSceneManagerSetup(_scenes);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            finally
            {
                EditorSettings.enterPlayModeOptions = _reloadOptions;
                EditorSettings.enterPlayModeOptionsEnabled = _wasReloadEnabled;
                if (_folder != null) AssetDatabase.DeleteAsset(_folder);
                _prepared = false;
            }
        }

        private sealed class RootProvider : ResourceProviderBase
        {
            internal int Provides;
            internal int Releases;
            internal RootProvider(string id) => m_ProviderId = id;
            public override void Provide(ProvideHandle handle)
            {
                Provides++;
                handle.Complete((EquipmentsConfig)handle.Location.Data, true, null);
            }
            public override void Release(IResourceLocation location, object asset) => Releases++;
        }

        private sealed class Round
        {
            internal ResourceStartup Startup;
            internal ResourceSystem System;
            internal EquipmentsConfig Root;
            internal EquipmentConfigData Row;
            internal RootProvider Provider;
            internal ResourceLocationMap Locator;
            internal EquipmentLifecycleTests.Host Host;
            internal EquipmentLifecycleTests.Probe Attached;
            internal EquipmentLifecycleTests.Probe Detached;
            internal EquipmentLifecycleTests.Probe Borrowed;
        }

        [UnityTest]
        public IEnumerator BothRealPlaySessionsReturnAttachedDetachedAndRootOwnership()
        {
            try
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = _reloadOptions | EnterPlayModeOptions.DisableDomainReload;
                for (var round = 0; round < 2; round++)
                {
                    string token = DomainToken;
                    var previous = _round?.System;
                    yield return new EnterPlayMode(false);
                    Assert.That(DomainToken, Is.EqualTo(token), "The real managed domain must survive EnterPlayMode.");
                    Assert.That(EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload, Is.Not.Zero);
                    RemoveRound();
                    _round = new Round();
                    yield return EnterRound(_round);
                    Assert.That(_round.System, Is.Not.SameAs(previous));
                    yield return new ExitPlayMode();
                    Assert.That(DomainToken, Is.EqualTo(token), "Exit must not manufacture a different managed-domain fixture.");
                    Assert.That(_round.Host.Equipments, Is.Empty, "round " + round);
                    Assert.That(_round.Attached.Unloads, Is.EqualTo(1));
                    Assert.That(_round.Detached.Unloads, Is.EqualTo(1));
                    Assert.That(_round.Borrowed.Unloads, Is.Zero);
                    Assert.That(_round.Borrowed.UnEquips, Is.EqualTo(1));
                    Assert.That(EquipmentManager.GetConfig("Equipment.TwoPlay"), Is.Null);
                    Assert.That(ResourceSystem.Current, Is.Null);
                    yield return AssertExitLedger(_round);
                    Assert.That(_round.Provider.Provides, Is.EqualTo(1));
                    Assert.That(_round.Provider.Releases, Is.EqualTo(1), "Only the real official operation owns the root release.");
                }
            }
            finally
            {
                EditorSettings.enterPlayModeOptions = _reloadOptions;
                EditorSettings.enterPlayModeOptionsEnabled = _wasReloadEnabled;
            }
        }

        // Constructed only after Enter; captured task/request locals never cross UTF's PC-only restore.
        private static IEnumerator EnterRound(Round state)
        {
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(EquipmentManager.GetConfig("Equipment.TwoPlay"), Is.Null);
            Assert.That(EquipmentManager.GetEquipmentInfo(1001), Is.Null);
            Assert.That(ResourceSystem.Current, Is.Null);
            state.Root = ScriptableObject.CreateInstance<EquipmentsConfig>();
            state.Root.name = "Equipment.TwoPlay";
            state.Row = ScriptableObject.CreateInstance<EquipmentConfigData>();
            state.Row.id = 1001;
            state.Row.parent = state.Root;
            state.Row.equipmentType = EquipmentType.Script;
            state.Row.equipmentClassFullName = typeof(EquipmentLifecycleTests.Probe).FullName;
            state.Root.Equipments.Add(state.Row);
            state.Provider = new RootProvider("Equipment.TwoPlay." + Guid.NewGuid().ToString("N"));
            state.Locator = new ResourceLocationMap(state.Provider.ProviderId);
            state.Locator.Add("eq-two-play/root", new ResourceLocationBase("eq-two-play/root", "eq-two-play/root", state.Provider.ProviderId, typeof(EquipmentsConfig))
                { Data = state.Root });
            Addressables.ResourceManager.ResourceProviders.Add(state.Provider);
            Addressables.AddResourceLocator(state.Locator);
            state.Startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using (var start = state.Startup.StartAsync())
            {
                yield return Wait(() => start.IsDone, "Equipment Play startup");
                Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), start.Error?.ToString());
                state.System = start.TakeResult().Resources;
            }
            var scope = state.System.CreateScope("Equipment.TwoPlay.Preload");
            using (var preload = scope.LoadAsync<EquipmentsConfig>("eq-two-play/root"))
            {
                yield return Wait(() => preload.IsDone, "official root operation");
                Assert.That(preload.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), preload.Error?.ToString());
                Assert.That(preload.TakeResult(), Is.SameAs(state.Root));
            }
            var loaded = EquipmentManager.LoadConfigAsync(state.System, new ResourceAddress("eq-two-play", "root"));
            yield return Wait(() => loaded.IsCompleted, "Equipment configuration registration");
            Assert.That(loaded.Status, Is.EqualTo(System.Threading.Tasks.TaskStatus.RanToCompletion), loaded.Exception?.ToString());
            using (var close = scope.CloseAsync())
            {
                yield return Wait(() => close.IsDone, "return only the explicit preload Scope");
                Assert.That(close.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), close.Error?.ToString());
            }
            Assert.That(EquipmentManager.GetConfig(state.Root.name), Is.SameAs(state.Root));
            Assert.That(state.Provider.Releases, Is.Zero, "The manager owns a root independently of the closed preload Scope.");
            state.Host = new EquipmentLifecycleTests.Host();
            var attached = EquipmentManager.CreateAndEquipAsync(state.Host, 1001);
            yield return Wait(() => attached.IsCompleted, "attached equipment");
            Assert.That(attached.Status, Is.EqualTo(System.Threading.Tasks.TaskStatus.RanToCompletion), attached.Exception?.ToString());
            state.Attached = (EquipmentLifecycleTests.Probe)attached.Result;
            state.Detached = (EquipmentLifecycleTests.Probe)EquipmentManager.CreateEquipment(1001);
            state.Borrowed = new EquipmentLifecycleTests.Probe { EquipmentData = state.Row };
            Assert.That(EquipmentManager.Equip(state.Host, state.Borrowed), Is.EqualTo(EquipState.Success));
        }

        private static IEnumerator AssertExitLedger(Round state)
        {
            yield return Wait(() =>
            {
                var report = state.System.GetDiagnostics();
                return report.LeaseCount + report.ScopeCount + report.RequestCount + report.PendingWorkCount + report.DrainingHandleCount == 0;
            }, "actual exit ownership drain");
            var ledger = state.System.GetDiagnostics();
            Assert.That(ledger.State, Is.EqualTo(ResourceSystemState.Invalidated));
            Assert.That(ledger.Errors, Is.Empty);
        }

        private void RemoveRound()
        {
            if (_round == null) return;
            if (_round.Locator != null) Addressables.RemoveResourceLocator(_round.Locator);
            if (_round.Provider != null) Addressables.ResourceManager.ResourceProviders.Remove(_round.Provider);
            if (_round.Row) Object.DestroyImmediate(_round.Row);
            if (_round.Root) Object.DestroyImmediate(_round.Root);
            _round = null;
        }

        private static void EnsureRestorableScenes()
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                Assert.That(scene.isDirty, Is.False, "Do not save or close a dirty external scene.");
                Assert.That(string.IsNullOrEmpty(scene.path) && scene.rootCount != 0, Is.False, "An unsaved scene with content is not restorable.");
            }
        }

        private static IEnumerator Wait(Func<bool> complete, string operation)
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!complete() && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(complete(), Is.True, operation + " timed out.");
        }
    }
}
