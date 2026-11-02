using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Resource
{
    // 正常 Domain Reload 的真实双 Play；不修改 ProjectSettings，也不运行原游戏场景。
    [Category("YL.Module.Resource")]
    [Category("YL.Special.ResourceSession")]
    public class ResourceSessionTests
    {
        private const string Key = "YL.Resource.8.1.Session.";
        [Serializable]
        private sealed class Scenes
        {
            public SceneRecord[] Items;
        }
        [Serializable]
        private sealed class SceneRecord
        {
            public string Path;
            public bool Loaded;
            public bool Active;
        }
        private static ResourceSystem _system;
        private static AssetLease<TextAsset> _lease;
        private static ResourceRequest<AssetLease<TextAsset>> _unclaimed;
        private static TextAsset _external;

        private static ResourceConfiguration Configuration() => new(new[]
        {
            new ResourceCatalog("exit81", new[]
            {
                new ResourceCatalogEntry("text", new ResourceAddress("exit81", "text"),
                    new ResourceLocation(ResourceSourceId.Resources, "YLResource81/Message", typeof(TextAsset)))
            })
        });

        [SetUp]
        public void SetUp()
        {
            if (SessionState.GetBool(Key + "configured", false))
            {
                return;
            }

            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.False, "This fixture does not change domain reload settings.");
            Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
            EnsureRestorableScenes();
            var snapshot = new Scenes
            {
                Items = EditorSceneManager.GetSceneManagerSetup().Where(scene => !string.IsNullOrEmpty(scene.path))
                    .Select(scene => new SceneRecord { Path = scene.path, Loaded = scene.isLoaded, Active = scene.isActive }).ToArray()
            };
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("Temp")), Is.True);
            var path = "Temp/YL.Resource81.Session." + Guid.NewGuid().ToString("N") + ".unity";
            SessionState.SetString(Key + "scenes", JsonUtility.ToJson(snapshot));
            SessionState.SetString(Key + "path", path);
            SessionState.SetBool(Key + "configured", true);
            SessionState.SetBool(Key + "quit", false);
            SessionState.SetBool(Key + "exit", false);
            var empty = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(empty, path), Is.True);
        }

        internal static void EnsureRestorableScenes()
        {
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                Assert.That(scene.isDirty, Is.False, "Do not close dirty scenes: " + scene.name);
                if (string.IsNullOrEmpty(scene.path))
                {
                    Assert.That(scene.rootCount, Is.Zero, "Only discard a clean empty runner scene.");
                }
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!SessionState.GetBool(Key + "configured", false))
            {
                yield break;
            }

            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            Application.quitting -= OnQuit;
            EditorApplication.playModeStateChanged -= OnExit;
            var snapshot = JsonUtility.FromJson<Scenes>(SessionState.GetString(Key + "scenes", ""));
            if (snapshot.Items.Length != 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(snapshot.Items.Select(scene => new SceneSetup
                {
                    path = scene.Path,
                    isLoaded = scene.Loaded,
                    isActive = scene.Active
                }).ToArray());
            }
            else
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }

            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + "path", "")));
            SessionState.EraseString(Key + "scenes");
            SessionState.EraseString(Key + "path");
            SessionState.EraseBool(Key + "configured");
            SessionState.EraseBool(Key + "quit");
            SessionState.EraseBool(Key + "exit");
        }

        private static void OnExit(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }

            using var rejected = ResourceSystem.InitializeAsync(Configuration());
            SessionState.SetBool(Key + "exit", rejected.Error?.Kind == ResourceError.SessionUnavailable &&
                ResourceSystem.Current == null && !_lease.IsValid);
            EditorApplication.playModeStateChanged -= OnExit;
        }

        private static void OnQuit()
        {
            SessionState.SetBool(Key + "quit", _system.State == ResourceSystemState.Invalidated &&
                _system.GetDiagnosticsSnapshot().RecordCount == 0 && _unclaimed.IsDisposed && !_lease.IsValid && _external);
            _lease.Dispose();
            _unclaimed.Dispose();
            Application.quitting -= OnQuit;
        }

        [UnityTest]
        public IEnumerator RealPlayExitInvalidatesOwnershipRejectsReentryAndNextPlayCanInitialize()
        {
            yield return new EnterPlayMode();
            Assert.That(ResourceSystem.Current, Is.Null);
            using (var initialized = ResourceSystem.InitializeAsync(Configuration()))
            {
                _system = initialized.TakeResult();
            }

            _external = Resources.Load<TextAsset>("YLResource81/Message");
            _lease = _system.Acquire<TextAsset>(new ResourceAddress("exit81", "text"));
            _unclaimed = _system.AcquireAsync<TextAsset>(new ResourceAddress("exit81", "text"));
            Assert.That(_unclaimed.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Application.quitting += OnQuit;
            EditorApplication.playModeStateChanged += OnExit;
            yield return new ExitPlayMode();
            Assert.That(SessionState.GetBool(Key + "exit", false), Is.True, "Actual exit must reject initialization and invalidate old leases.");
            Assert.That(SessionState.GetBool(Key + "quit", false), Is.True, "Quit must settle owned results without unloading the borrowed Resources object.");
            yield return new EnterPlayMode();
            Assert.That(ResourceSystem.Current, Is.Null);
            using (var initialized = ResourceSystem.InitializeAsync(Configuration()))
            {
                var fresh = initialized.TakeResult();
                using (var next = fresh.Acquire<TextAsset>(new ResourceAddress("exit81", "text")))
                {
                    Assert.That(next.Asset.text, Does.Contain("isolated Resources fixture"));
                }

                using var shutdown = fresh.ShutdownAsync();
                Assert.That(shutdown.TakeResult(), Is.True);
            }
            yield return new ExitPlayMode();
        }
    }

    [Category("YL.Module.Resource")]
    [Category("YL.Special.ResourceSession")]
    public class ResourceSessionSafetyTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ExitFixtureRejectsDirtyUnsavedScenesWithoutClosingOrSavingThem(bool withContent)
        {
            var previous = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            Assert.That(UnityEngine.SceneManagement.SceneManager.sceneCount, Is.GreaterThan(1));
            Assert.That(previous.path, Is.Empty, "Only replace the runner's clean empty scene.");
            Assert.That(previous.rootCount, Is.Zero);
            Assert.That(previous.isDirty, Is.False);
            var owned = previous;
            GameObject content = null;
            try
            {
                if (withContent)
                {
                    content = new GameObject("Resource81.UnsavedSafetyFixture");
                }
                else
                {
                    EditorSceneManager.MarkSceneDirty(owned);
                }

                Assert.Throws<AssertionException>(() => ResourceSessionTests.EnsureRestorableScenes());
                Assert.That(owned.isLoaded, Is.True);
                Assert.That(owned.path, Is.Empty);
                if (withContent)
                {
                    Assert.That(content, Is.Not.Null);
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(owned, true);
                var clean = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(clean);
            }
        }
    }
}
