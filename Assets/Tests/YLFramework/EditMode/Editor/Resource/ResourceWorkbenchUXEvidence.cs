using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Editor.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    // 仅供本轮明确授权的非空GUI体验与证据；产品程序集不引用此类型。
    public static class ResourceWorkbenchUXEvidence
    {
        public const string Root = "Assets/Tests/YLFramework/Fixtures/WorkbenchUX2To5-20261017";
        public static string EvidenceRoot => Path.Combine(Path.GetTempPath(), "kilo", "workbench-ux2to5-20261017");
        public static ResourceProject Project => AssetDatabase.LoadAssetAtPath<ResourceProject>(Root + "/WorkbenchProject.asset");
        public static ResourceEditorBuildReport AnalysisBuild;
        private static Action<ResourceBuildStage> _stageCapture;
        private static readonly List<ResourceBuildStage> _stages = new();
        private static DateTime _captureStarted;

        public static void BeginStageCapture(string runName)
        {
            if (_stageCapture != null)
            {
                throw new InvalidOperationException("A GUI evidence capture is already active.");
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(runName, "^ux3-[a-z0-9-]+$"))
            {
                throw new ArgumentException("Use an owned run basename.");
            }

            var intent = Path.Combine(EvidenceRoot, runName + "-intent.json");
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(intent)))
            {
                throw new InvalidOperationException("This GUI run already exists; read its result, do not restart it.");
            }

            _captureStarted = DateTime.UtcNow;
            _stages.Clear();
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(Project));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(intent), JsonConvert.SerializeObject(new
            {
                status = "QueuedForLiveGui",
                atUtc = _captureStarted,
                projectGuid = guid,
                projectId = Project.projectId,
                evidenceOnly = "Not an execution or recovery ledger; a canceled/no-report action is not invented as a result."
            }, Formatting.Indented));
            _stageCapture = stage =>
            {
                if (stage.ProjectGuid == guid)
                {
                    _stages.Add(stage);
                }
            };
            ResourceBuildProgress.StageReached += _stageCapture;
            AssemblyReloadEvents.beforeAssemblyReload += StopStageCapture;
        }

        public static void StopStageCapture()
        {
            if (_stageCapture != null)
            {
                ResourceBuildProgress.StageReached -= _stageCapture;
            }

            _stageCapture = null;
            AssemblyReloadEvents.beforeAssemblyReload -= StopStageCapture;
        }

        public static string RecordCurrentGuiReport(string runName)
        {
            if (_stageCapture == null || !System.Text.RegularExpressions.Regex.IsMatch(runName, "^ux3-[a-z0-9-]+$") ||
                !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(EvidenceRoot, runName + "-intent.json"))))
            {
                throw new InvalidOperationException("No active owned capture for this run.");
            }

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(Project));
            var window = Resources.FindObjectsOfTypeAll<ResServiceEditorWindow>().Single(w =>
                AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(typeof(ResServiceEditorWindow).GetField("_project", flags).GetValue(w) as ResourceProject)) == guid);
            var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", flags).GetValue(window);
            var buildView = view.GetType().GetField("_build", flags).GetValue(view);
            var report = buildView.GetType().GetProperty("CurrentReport", flags).GetValue(buildView) as ResourceEditorBuildReport;
            if (report == null || report.StartedAtUtc < _captureStarted || (report.Build?.Map.ProjectGuid ?? report.ProjectGuid) != guid)
            {
                throw new InvalidOperationException("No new original report for this owned GUI run; do not reconstruct one from directories.");
            }

            var output = Path.Combine(EvidenceRoot, runName + "-result.json");
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)))
            {
                throw new InvalidOperationException("The GUI result is already recorded.");
            }

            var result = JsonConvert.SerializeObject(new
            {
                report.StartedAtUtc,
                observedAtUtc = DateTime.UtcNow,
                report.Operation,
                report.ProjectGuid,
                report.ProjectId,
                report.Platform,
                report.AppContentCompatibilityId,
                report.ForceRebuild,
                GenerationRequested = report.IsGenerationRequested,
                report.ReferenceDirectory,
                report.Invocation,
                buildError = report.BuildError?.ToString(),
                publicationError = report.PublicationError?.ToString(),
                generationError = report.GenerationError?.ToString(),
                report.ElapsedMilliseconds,
                report.PreflightMilliseconds,
                report.PublicationMilliseconds,
                report.GenerationMilliseconds,
                build = report.Build == null ? null : new
                {
                    report.Build.WorkspacePath,
                    report.Build.InputFingerprint,
                    report.Build.ContentEncryption,
                    UnityBuildInvoked = report.Build.WasUnityBuildInvoked,
                    report.Build.IsCacheHit,
                    bundles = report.Build.Bundles.Count,
                    report.Build.BundleFileBytes,
                    report.Build.DeliveryBytes
                },
                release = report.Release == null ? null : new
                {
                    report.Release.DirectoryPath,
                    report.Release.Catalog.ProjectId,
                    report.Release.Catalog.CatalogDigest,
                    report.Release.Catalog.AppContentCompatibilityId,
                    report.Release.CopiedBytes,
                    report.Release.IsRepeat
                },
                stages = _stages.Select(x => new { phase = x.Phase.ToString(), x.AtUtc, x.ProjectGuid, x.ProjectId, x.Platform }).ToArray(),
                pixelsPerPoint = EditorGUIUtility.pixelsPerPoint,
                proSkin = EditorGUIUtility.isProSkin,
                logicalRect = window.position.ToString(),
                report.Summary
            }, Formatting.Indented);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(output), result);
            StopStageCapture();
            return output;
        }

        public static string BuildAnalysisFixture()
        {
            var project = Project;
            var workspace = ResourceBundleBuilder.GetWorkspacePath(project, EditorUserBuildSettings.activeBuildTarget);
            var runPath = Path.Combine(EvidenceRoot, "ux2-build-run.json");
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(runPath)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(workspace))))
            {
                throw new InvalidOperationException("Analysis run already exists; inspect the same run, do not repeat it.");
            }

            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(runPath), JsonConvert.SerializeObject(new
            {
                status = "Running",
                startedAtUtc = DateTime.UtcNow,
                ProjectId = project.projectId,
                workspace
            }));
            AnalysisBuild = ResourceEditorOperations.BuildOnly(project, "workbench-ux-app-v1");
            var result = JsonConvert.SerializeObject(new
            {
                status = AnalysisBuild.BuildError == null ? "Passed" : "Failed",
                AnalysisBuild.StartedAtUtc,
                observedCompletionAtUtc = DateTime.UtcNow,
                AnalysisBuild.ElapsedMilliseconds,
                error = AnalysisBuild.BuildError?.ToString(),
                workspace,
                source = "A single real None BuildOnly for exact UX2 analysis; GUI build flow is separately covered in UX3",
                build = AnalysisBuild.Build == null ? null : new
                {
                    AnalysisBuild.Build.Platform,
                    AnalysisBuild.Build.ContentEncryption,
                    AnalysisBuild.Build.InputFingerprint,
                    AnalysisBuild.Build.BuildSettingsFingerprint,
                    AnalysisBuild.Build.BuildPipelineMilliseconds,
                    AnalysisBuild.Build.BundleFileBytes,
                    AnalysisBuild.Build.DeliveryBytes,
                    bundles = AnalysisBuild.Build.Bundles.Select(x => new { x.Name, x.FileBytes, x.SourceBytes, x.Dependencies, x.Dependents }),
                    assets = AnalysisBuild.Build.Map.Assets.Select(x => new { x.SourcePath, x.BundleName, address = x.Address.ToString(), x.Source })
                }
            }, Formatting.Indented);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(runPath), result);
            return result;
        }

        public static string CreateRetainedFixture()
        {
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root + ".meta")) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(EvidenceRoot)))
            {
                throw new InvalidOperationException("Owned UX2-5 fixture/evidence already exists; do not recreate or adopt it.");
            }

            if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(EvidenceRoot))))
            {
                throw new DirectoryNotFoundException("Evidence parent is missing.");
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("A user scene is dirty; no scene switch is allowed.");
                }
            }

            if (ResourceSystemIsActive() || AssetBundle.GetAllLoadedAssetBundles().Any())
            {
                throw new InvalidOperationException("An external resource session or Bundle is active.");
            }

            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(EvidenceRoot));
            var protectedPaths = new[] { ".gitignore", ".kilo/kilo.jsonc", "AGENTS.md", ".kilo/plans/1791960935353-ui-panel-redesign.md",
                "Assets/Tests/YLFramework/Fixtures/Core/YLCoreResourceModule/ResourceProject.asset", "Assets/StreamingAssets.meta" };
            var protection = protectedPaths.Where(File.Exists).Select(path => new { path, sha256 = ResourceCatalogCodecTests.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path))) }).ToArray();
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(EvidenceRoot, "protected-files-before.json")), JsonConvert.SerializeObject(protection, Formatting.Indented));
            var directories = new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "AssetBundles/MainModule", "Packages", "ProjectSettings",
                "Assets/Plugins/NuGet", "Assets/Tests/YLFramework/Fixtures/WorkbenchUX1-20261017" };
            var snapshots = directories.ToDictionary(x => x, x => ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(x))));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(EvidenceRoot, "protected-trees-before.json")), JsonConvert.SerializeObject(snapshots, Formatting.Indented));
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", Path.GetFileName(Root));
            AssetDatabase.CreateFolder(Root, "Content");
            AssetDatabase.CreateFolder(Root + "/Content", "Sprites");
            var content = Root + "/Content";
            var shared = new Texture2D(8, 8) { name = "UX shared texture" };
            shared.SetPixels(Enumerable.Repeat(new Color(.18f, .55f, .82f), 64).ToArray());
            shared.Apply();
            AssetDatabase.CreateAsset(shared, content + "/Shared.asset");
            foreach (var name in new[] { "CardA", "CardB" })
            {
                var material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = shared };
                AssetDatabase.CreateAsset(material, content + "/" + name + ".mat");
                var go = new GameObject(name);
                try
                {
                    go.AddComponent<MeshRenderer>().sharedMaterial = material;
                    PrefabUtility.SaveAsPrefabAsset(go, content + "/" + name + ".prefab");
                }
                finally
                {
                    Object.DestroyImmediate(go);
                }
            }
            for (var i = 1; i <= 8; i++)
            {
                var path = content + "/message-" + i.ToString("00") + ".json";
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "{\"message\":\"Workbench UX2-5 owned fixture " + i + "\"}");
                AssetDatabase.ImportAsset(path);
            }
            foreach (var name in new[] { "Menu", "Icons", "Manual" })
            {
                var path = content + "/Sprites/" + name + ".png";
                var texture = new Texture2D(32, 16);
                texture.SetPixels(Enumerable.Range(0, 512).Select(i => i % 32 < 16 ? Color.cyan : Color.yellow).ToArray());
                texture.Apply();
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Multiple;
                importer.spritesheet = new[] { new SpriteMetaData { name = name + "Left", rect = new Rect(0, 0, 16, 16), pivot = Vector2.one / 2 },
                    new SpriteMetaData { name = name + "Right", rect = new Rect(16, 0, 16, 16), pivot = Vector2.one / 2 } };
                importer.SaveAndReimport();
            }
            var originalActive = SceneManager.GetActiveScene();
            var ownedScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(go, ownedScene);
                go.GetComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(content + "/CardA.mat");
                EditorSceneManager.SaveScene(ownedScene, content + "/OwnedPreview.unity");
            }
            finally
            {
                EditorSceneManager.CloseScene(ownedScene, true);
                SceneManager.SetActiveScene(originalActive);
            }
            var atlas = new UnityEngine.U2D.SpriteAtlas();
            UnityEditor.U2D.SpriteAtlasExtensions.SetIncludeInBuild(atlas, false);
            UnityEditor.U2D.SpriteAtlasExtensions.Add(atlas, new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(content + "/Sprites/Manual.png") });
            AssetDatabase.CreateAsset(atlas, content + "/Manual.spriteatlas");
            var project = ResourceEditorOperations.CreateProject(Root + "/WorkbenchProject.asset");
            project.projectId = "yl-workbench-ux2to5-20261017";
            project.displayName = "Workbench UX2-5 · owned";
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(content) });
            ResourceGroup Group(string id, ResourcePacking packing, params string[] paths) => new ResourceGroup
            {
                id = id,
                packing = packing,
                selections = paths.Select(path => new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(path) }).ToList()
            };
            project.groups.Add(Group("cards", ResourcePacking.PerAsset, content + "/CardA.prefab", content + "/CardB.prefab"));
            project.groups.Add(Group("messages", ResourcePacking.Together, Enumerable.Range(1, 8).Select(i => content + "/message-" + i.ToString("00") + ".json").ToArray()));
            project.groups.Add(Group("scenes", ResourcePacking.Together, content + "/OwnedPreview.unity"));
            project.groups.Add(Group("atlases", ResourcePacking.Together, content + "/Manual.spriteatlas"));
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            var config = ResourceAtlasGenerator.CreateConfiguration(project, content, "GeneratedAtlases");
            foreach (var name in new[] { "Menu", "Icons" })
            {
                config.rules.Add(new ResourceAtlasRule
                {
                    id = name.ToLowerInvariant(),
                    groupId = "atlases",
                    address = "atlases/" + name.ToLowerInvariant(),
                    maxTextureSize = 128,
                    sources = new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(content + "/Sprites/" + name + ".png") }
                });
            }

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssetIfDirty(config);
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = project;
            recipe.target = EditorUserBuildSettings.activeBuildTarget;
            recipe.appContentCompatibilityId = "workbench-ux-app-v1";
            recipe.action = ResourceBuildAction.BuildAndPublish;
            AssetDatabase.CreateAsset(recipe, Root + "/WorkbenchRecipe.asset");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/ux2to5-fixture-owner.json"), JsonConvert.SerializeObject(new
            {
                purpose = "UX2-5 retained non-production GUI fixture; excluded by the existing Tests export boundary",
                createdAtUtc = DateTime.UtcNow,
                projectId = project.projectId,
                projectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)),
                recipe = Root + "/WorkbenchRecipe.asset",
                app = recipe.appContentCompatibilityId,
                authorizedOperations = "own Atlas generate/obsolete rejection/delete; own Build/Publisher/export/signing; own C02 workspace and exact project/version shells; read-only Store queries",
                evidenceRoot = EvidenceRoot
            }, Formatting.Indented));
            AssetDatabase.ImportAsset(Root + "/ux2to5-fixture-owner.json");
            return AssetDatabase.GetAssetPath(project);
        }

        private static bool ResourceSystemIsActive() => YLFramework.Resource.ResourceSystem.Current != null;
    }
}
