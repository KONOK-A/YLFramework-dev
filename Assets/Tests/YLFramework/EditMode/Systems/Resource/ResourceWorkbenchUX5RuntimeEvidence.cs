using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    [InitializeOnLoad]
    public static class ResourceWorkbenchUX5RuntimeEvidence
    {
        private const string Key = "YL.WorkbenchUX5.OwnedRuntime.";
        private static ResourceSystem _owned;
        private static AssetLease<TextAsset> _text;
        private static AssetLease<GameObject> _prefab;
        private static InstanceCache _cache;
        private static InstanceLease _instance;
        private static ResourceRequest<bool> _shutdown;
        static ResourceWorkbenchUX5RuntimeEvidence()
        {
            if (SessionState.GetBool(Key + "active", false))
            {
                Attach();
            }
        }
        private static void Attach()
        {
            EditorApplication.playModeStateChanged -= OnState;
            EditorApplication.playModeStateChanged += OnState;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }
        private static void Detach()
        {
            EditorApplication.playModeStateChanged -= OnState;
            EditorApplication.update -= Tick;
        }

        public static string Begin() => BeginCore("ux5-runtime-run", false);
        public static string BeginLayoutRegression() => BeginCore("ux5-runtime-layout-regression", true);
        private static string BeginCore(string runName, bool reuseOwnedScene)
        {
            if (SessionState.GetBool(Key + "active", false) || EditorApplication.isPlayingOrWillChangePlaymode || ResourceSystem.Current != null || AssetBundle.GetAllLoadedAssetBundles().Any())
            {
                throw new InvalidOperationException("Do not take over a Play/session/Bundle or repeat the owned runtime run.");
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("A user scene is dirty; refuse scene switching.");
                }
            }

            var run = Path.Combine(ResourceWorkbenchUXEvidence.EvidenceRoot, runName + ".json");
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(run)))
            {
                throw new InvalidOperationException("Read the existing runtime run instead of repeating it.");
            }

            var path = ResourceWorkbenchUXEvidence.Root + "/UX5Runtime.unity";
            if (!reuseOwnedScene && (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))))
            {
                throw new InvalidOperationException("Owned runtime Scene already exists; do not overwrite.");
            }

            if (reuseOwnedScene && (!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))))
            {
                throw new InvalidOperationException("Registered owned regression Scene is missing.");
            }

            var p = ResourceWorkbenchUXEvidence.Project;
            var release = ResourcePublisher.ReadVersion(p, EditorUserBuildSettings.activeBuildTarget, "workbench-ux-app-v1", "1f3c2d0ce7f14a4f230afd146dcabe3cc5ec2bf10e4c9b5756a7f003673a9b56");
            SessionState.SetString(Key + "scenes", JsonConvert.SerializeObject(EditorSceneManager.GetSceneManagerSetup()));
            SessionState.SetString(Key + "source", release.DirectoryPath);
            SessionState.SetString(Key + "project", p.projectId);
            SessionState.SetBool(Key + "active", true);
            Attach();
            SessionState.SetString(Key + "run", runName);
            if (reuseOwnedScene)
            {
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            }
            else
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, path);
            }
            Write("BeginningOwnedEmptyScenePlay", null);
            EditorApplication.isPlaying = true;
            return "Only the saved owned empty Scene will enter Play; original game Scene is not loaded. Read the same run result.";
        }

        private static void OnState(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Key + "active", false))
            {
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                try
                {
                    if (ResourceSystem.Current != null)
                    {
                        throw new InvalidOperationException("An external session appeared; do not shut it down or take it over.");
                    }

                    var snapshot = ResourceLocalSnapshot.Open(SessionState.GetString(Key + "source", ""), SessionState.GetString(Key + "project", ""),
                        "1f3c2d0ce7f14a4f230afd146dcabe3cc5ec2bf10e4c9b5756a7f003673a9b56", "StandaloneWindows64", "workbench-ux-app-v1");
                    using (var request = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { snapshot.CreateCatalog() })))
                    {
                        _owned = request.TakeResult();
                    }

                    _owned.ConfigureDiagnosticsHistory(16);
                    _text = _owned.Acquire<TextAsset>(new ResourceAddress(snapshot.Document.ProjectId, "message-01.json"));
                    _text.OwnerLabel = "WorkbenchUX5-owned-text";
                    _prefab = _owned.Acquire<GameObject>(new ResourceAddress(snapshot.Document.ProjectId, "CardA.prefab"));
                    _prefab.OwnerLabel = "WorkbenchUX5-owned-prefab";
                    _cache = _owned.GetInstanceCache(_prefab, InstanceCachePolicy.Persistent);
                    _cache.Prewarm(2);
                    _instance = _cache.Instantiate();
                    Write("ReadyForRealGuiSampling", null);
                }
                catch (Exception error)
                {
                    Write("OwnedRuntimeFailed", error.ToString());
                    if (_owned != null && ReferenceEquals(ResourceSystem.Current, _owned))
                    {
                        End();
                    }
                    else
                    {
                        EditorApplication.isPlaying = false;
                    }
                }
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                var setup = JsonConvert.DeserializeObject<SceneSetup[]>(SessionState.GetString(Key + "scenes", "[]"));
                if (setup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }

                SessionState.SetBool(Key + "active", false);
                Write("ClosedAndOriginalScenesRestored", null);
                Detach();
            }
        }

        public static string End()
        {
            if (!SessionState.GetBool(Key + "active", false))
            {
                return "No active owned runtime evidence session.";
            }

            if (_owned != null && !ReferenceEquals(ResourceSystem.Current, _owned))
            {
                throw new InvalidOperationException("Session identity changed; never shut down another authority.");
            }

            _instance?.Dispose();
            _instance = null;
            _cache?.Dispose();
            _cache = null;
            _prefab?.Dispose();
            _prefab = null;
            _text?.Dispose();
            _text = null;
            if (_owned != null)
            {
                _shutdown = _owned.ShutdownAsync();
            }

            Write("OwnedShutdownRequested", null);
            return "Owned leases/cache released; waiting only for original shutdown/native drain before leaving owned Play.";
        }
        private static void Tick()
        {
            if (_shutdown == null || !_shutdown.IsDone)
            {
                return;
            }

            var error = _shutdown.Error?.ToString();
            _shutdown.Dispose();
            _shutdown = null;
            _owned = null;
            Write("OwnedShutdownCompleted", error);
            EditorApplication.isPlaying = false;
        }
        private static void Write(string state, string error) => File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(ResourceWorkbenchUXEvidence.EvidenceRoot, SessionState.GetString(Key + "run", "ux5-runtime-run") + ".json")),
            JsonConvert.SerializeObject(new
            {
                state,
                atUtc = DateTime.UtcNow,
                error,
                scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).path).ToArray(),
                resourceSession = ResourceSystem.Current != null,
                loadedBundles = AssetBundle.GetAllLoadedAssetBundles().Count()
            }, Formatting.Indented));
    }
}
