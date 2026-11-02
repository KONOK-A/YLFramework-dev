using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    /// <summary>本批显式取证入口。读取不构建、不生成、不接管已有运行会话；只写 Modern 与新增证据文件。</summary>
    [InitializeOnLoad]
    public static class ResourceMultiDirectoryEvidence
    {
        public const string FixtureRoot = "Assets/Tests/YLFramework/Fixtures/ResourceMultiDirectory-20261018";
        public const string Root = FixtureRoot + "/Modern";
        public const string EvidenceRoot = "C:/Users/Kano/AppData/Local/Temp/kilo/resource-multidirectory-20261018-a";
        public const string ProjectId = "yl-resource-multidirectory-modern-20261018";
        public const string AppContentCompatibilityId = "multidirectory-modern-v1";
        public const string Message = "多目录自有取证文本：共享依赖与逻辑地址。\n";
        public const string PrefabAddress = "prefabs/CrossRootA.prefab";
        public const string OtherPrefabAddress = "prefabs/CrossRootB.prefab";
        public const string TextAddress = "text/selected.txt";
        public const string SceneAddress = "scenes/Representative.unity";
        public const string AtlasAddress = "atlases/multi-root";
        private const string Key = "YL.ResourceMultiDirectory.Evidence.";
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly Color32 _pixel = new(20, 80, 140, 255);
        private static readonly Vector2 _textureScale = new(.5f, .75f);
        private static readonly Vector2 _textureOffset = new(.125f, .25f);
        private static RuntimeRun _run;
        private static ResourceStartup _startup;
        private static ResourceSystem _owned;
        private static ResourceScope _scope;
        private static ResourceRequest<ResourceStartup> _starting;
        private static ResourceRequest<bool> _stopping;
        private static SceneRequest _loadingScene;
        private static SceneLease _sceneLease;
        private static ResourceRequest<bool> _unloadingScene;
        private static InstanceCache _cache;
        private static InstanceLease _instance;
        private static readonly List<Sprite> _clones = new();
        private static int _releaseFrame;
        private static int _sceneCompletedFrame = -1;

        public static ResourceProject Project => AssetDatabase.LoadAssetAtPath<ResourceProject>(Root + "/ModernProject.asset");
        public static ResourceBuildRecipe Recipe => AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(Root + "/ModernRecipe.asset");

        public static string RecordLastTests(string checkpoint)
        {
            var path = CheckpointPath("tests-" + checkpoint);
            var active = typeof(UnityEditor.TestTools.TestRunner.Api.TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (active == null)
            {
                return "无法确认测试终态，未记录结果。";
            }

            if ((bool)active.Invoke(null, null))
            {
                return "测试仍在执行，未记录终态。";
            }

            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("com.IvanMurzak.Unity.MCP.Editor.API.Tool_Tests", false)).FirstOrDefault(x => x != null);
            var collector = type?.GetField("_resultCollector", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            if (collector == null)
            {
                throw new InvalidOperationException("当前工具没有保留这次测试结果，不启动替代run。");
            }

            var fields = collector.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.Name == "_summary" || f.Name == "_results" || f.Name == "startTime").ToDictionary(f => f.Name, f => f.GetValue(collector));
            var json = JsonConvert.SerializeObject(fields, Formatting.Indented, new JsonSerializerSettings { Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() } });
            var parsed = JObject.Parse(json);
            if (parsed["_summary"]?["Status"] == null || parsed["_results"] is not JArray results || results.Count == 0)
            {
                throw new InvalidOperationException("没有完整叶结果，不把发现数量或空结果当成通过。");
            }

            WriteNew(path, new
            {
                CapturedUtc = DateTime.UtcNow,
                Results = parsed
            });
            // 工具仅接收路径字符串，避免反射序列化JToken的Parent/Root循环。
            return path;
        }

        private sealed class RuntimeRun
        {
            public string Directory;
            public string State;
            public string CatalogDigest;
            public string SourceDirectory;
            public string Platform;
            public string RuntimeScene;
            public bool ExpectAtlas;
            public SceneSetup[] OriginalScenes;
            public DateTime StartedAtUtc;
            public DateTime DeadlineUtc;
            public int Sequence;
            public string Error;
            public bool ScenesRestored;
            public bool StartupStopped;
            public bool Terminal;
            public bool Passed;
            public JObject Observations = new();
        }

        static ResourceMultiDirectoryEvidence()
        {
            var saved = SessionState.GetString(Key + "run", "");
            if (saved.Length == 0)
            {
                return;
            }

            _run = JsonConvert.DeserializeObject<RuntimeRun>(saved);
            if (_run != null && !_run.Terminal)
            {
                Attach();
            }
        }

        /// <summary>只创建并保存本批数据。首次 None 构建前没有 Recipe、Atlas 配置或任何生成输出。</summary>
        public static string CreateModernFixture()
        {
            RequireIdle();
            RequireBatchOwner();
            RejectDirtyScenes();
            _ = SourceDirectoryMethod();
            RequireAbsent(Root);
            var shader = Shader.Find("Unlit/Texture");
            if (!shader)
            {
                throw new InvalidOperationException("缺少既有 Unlit/Texture Shader，未创建夹具。 ");
            }

            var original = EditorSceneManager.GetSceneManagerSetup();
            Folder(FixtureRoot, "Modern");
            foreach (var name in new[] { "Art", "Text", "GamePrefabs", "GameScenes", "UI", "Generated", "Runs" })
            {
                Folder(Root, name);
            }

            Folder(Root + "/Art", "Textures");
            Folder(Root + "/Art", "Materials");
            Folder(Root + "/UI", "Sprites");
            WriteNew(Path.Combine(EvidenceRoot, "modern-fixture-intent.json"), new
            {
                atUtc = DateTime.UtcNow,
                root = Root,
                originalScenes = original,
                scope = "仅新 Modern；不是旧发布基线，不修改 Legacy 或既有保护快照。"
            });
            try
            {
                // 单独的无脚本场景承接临时 GameObject，避免给用户原场景留下 dirty 标记。
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Texture(Root + "/Art/Textures/Shared.png", 16, 8, _pixel, false);
                Texture(Root + "/Art/Textures/ArtIcon.png", 8, 8, new Color32(220, 40, 20, 255), true);
                Texture(Root + "/UI/Sprites/UiIcon.png", 12, 8, new Color32(20, 180, 60, 255), true);
                var material = new Material(shader)
                {
                    name = "SharedMaterial",
                    mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Art/Textures/Shared.png"),
                    mainTextureScale = _textureScale,
                    mainTextureOffset = _textureOffset
                };
                AssetDatabase.CreateAsset(material, Root + "/Art/Materials/SharedMaterial.mat");
                foreach (var name in new[] { "CrossRootA", "CrossRootB" })
                {
                    var go = new GameObject(name);
                    try
                    {
                        go.AddComponent<MeshRenderer>().sharedMaterial = material;
                        if (!PrefabUtility.SaveAsPrefabAsset(go, Root + "/GamePrefabs/" + name + ".prefab"))
                        {
                            throw new IOException("无法保存自有 Prefab：" + name);
                        }
                    }
                    finally
                    {
                        Object.DestroyImmediate(go);
                    }
                }
                Text(Root + "/Text/selected.txt", Message);
                Text(Root + "/Text/unselected.txt", "未选择的自有文本，不应进入 Catalog 或 Bundle。\n");
                var marker = new GameObject("ModernSceneMarker");
                marker.transform.position = new Vector3(3, 2, 1);
                marker.AddComponent<MeshRenderer>().sharedMaterial = material;
                if (!EditorSceneManager.SaveScene(scene, Root + "/GameScenes/Representative.unity"))
                {
                    throw new IOException("无法保存自有代表场景。 ");
                }

                var project = ResourceEditorOperations.CreateProject(Root + "/ModernProject.asset");
                project.projectId = ProjectId;
                project.displayName = "多目录资源取证（自有）";
                project.compression = ResourceCompression.Lz4;
                project.contentEncryption = ResourceContentEncryption.None;
                SetSources(project, (Root + "/Art/Textures", "textures"), (Root + "/Art/Materials", "materials"),
                    (Root + "/Text", "text"), (Root + "/GamePrefabs", "prefabs"),
                    (Root + "/GameScenes", "scenes"), (Root + "/UI/Sprites", "ui"));
                var main = ResourceEditorOperations.AddGroup(project, "main");
                ResourceEditorOperations.AddSelections(project, main, new[] { AssetDatabase.LoadMainAssetAtPath(Root + "/GamePrefabs/CrossRootA.prefab"),
                    AssetDatabase.LoadMainAssetAtPath(Root + "/Text/selected.txt") });
                project.groups[main].selections.Single(x => x.guid == AssetDatabase.AssetPathToGUID(Root + "/Text/selected.txt")).aliases = new[] { "message" };
                var other = ResourceEditorOperations.AddGroup(project, "other");
                ResourceEditorOperations.AddSelections(project, other, new[] { AssetDatabase.LoadMainAssetAtPath(Root + "/GamePrefabs/CrossRootB.prefab") });
                var scenes = ResourceEditorOperations.AddGroup(project, "scenes");
                ResourceEditorOperations.AddSelections(project, scenes, new[] { AssetDatabase.LoadMainAssetAtPath(Root + "/GameScenes/Representative.unity") });
                EditorUtility.SetDirty(project);
                ResourceEditorOperations.SaveProject(project);
                Text(Root + "/modern-owner.json", JsonConvert.SerializeObject(new
                {
                    createdAtUtc = DateTime.UtcNow,
                    root = Root,
                    projectId = ProjectId,
                    projectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)),
                    app = AppContentCompatibilityId,
                    evidence = EvidenceRoot,
                    scope = "自有多目录、独立生成范围及空场景 Runtime。无自动构建/生成，不接管原游戏或其他会话。"
                }, Formatting.Indented));
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(original);
            }
            WriteNew(Path.Combine(EvidenceRoot, "modern-fixture-created.json"), new
            {
                atUtc = DateTime.UtcNow,
                root = Root,
                project = AssetDatabase.GetAssetPath(Project),
                projectId = ProjectId,
                app = AppContentCompatibilityId,
                automaticAtlases = false,
                recipe = false,
                buildExecuted = false,
                sources = JObject.Parse(JsonUtility.ToJson(Project))["sourceDirectories"]
            });
            return AssetDatabase.GetAssetPath(Project);
        }

        /// <summary>显式新增配方；不执行配方，也不请求常量源码生成。</summary>
        public static string SetupRecipe()
        {
            RequireIdle();
            var project = RequireModernProject();
            var path = Root + "/ModernRecipe.asset";
            RequireAbsent(path);
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = project;
            recipe.target = EditorUserBuildSettings.activeBuildTarget;
            recipe.appContentCompatibilityId = AppContentCompatibilityId;
            recipe.action = ResourceBuildAction.BuildAndPublish;
            recipe.generateReferences = false;
            recipe.referenceDirectory = Root + "/GeneratedReferences";
            AssetDatabase.CreateAsset(recipe, path);
            WriteNew(Path.Combine(EvidenceRoot, "modern-recipe-registered.json"), new
            {
                atUtc = DateTime.UtcNow,
                path,
                target = recipe.target.ToString(),
                app = AppContentCompatibilityId,
                executed = false
            });
            return path;
        }

        /// <summary>显式登记一个跨源根 Together 规则；生成父目录不是任一源根。此方法不调用 Generate。</summary>
        public static string SetupAtlas()
        {
            RequireIdle();
            var project = RequireModernProject();
            if (project.automaticAtlases)
            {
                throw new InvalidOperationException("已有图集登记，拒绝重复创建。 ");
            }

            var method = typeof(ResourceAtlasGenerator).GetMethod("CreateConfiguration", new[] { typeof(ResourceProject), typeof(string), typeof(string) });
            if (method == null)
            {
                throw new MissingMethodException("需要 CreateConfiguration(project, generationParentPath, folderName) 正式入口。 ");
            }

            var configuration = (ResourceAtlasConfiguration)Invoke(method, project, Root + "/Generated", "OwnedAtlases");
            configuration.rules.Add(new ResourceAtlasRule
            {
                id = "multi-root",
                groupId = "main",
                address = AtlasAddress,
                readable = true,
                maxTextureSize = 128,
                filterMode = FilterMode.Point,
                srgb = false,
                sources = new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Art/Textures/ArtIcon.png"),
                    AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/UI/Sprites/UiIcon.png") }
            });
            EditorUtility.SetDirty(configuration);
            AssetDatabase.SaveAssetIfDirty(configuration);
            WriteNew(Path.Combine(EvidenceRoot, "modern-atlas-registered.json"), new
            {
                atUtc = DateTime.UtcNow,
                configuration = AssetDatabase.GetAssetPath(configuration),
                generationParent = Root + "/Generated",
                address = AtlasAddress,
                sources = configuration.rules.Single().sources.Select(AssetDatabase.GetAssetPath).ToArray(),
                generated = false
            });
            return AssetDatabase.GetAssetPath(configuration);
        }

        // 并行实现期间不在测试程序集静态引用新类型；缺失正式 API 时明确失败，不降级成另一套产品实现。
        internal static void SetSources(ResourceProject project, params (string Path, string Prefix)[] directories)
        {
            var method = SourceDirectoryMethod();
            var itemType = typeof(ResourceProject).GetField("sourceDirectories", Flags).FieldType.GetGenericArguments().Single();
            var items = Array.CreateInstance(itemType, directories.Length);
            for (var i = 0; i < directories.Length; i++)
            {
                items.SetValue(JsonUtility.FromJson(JsonConvert.SerializeObject(new
                {
                    guid = AssetDatabase.AssetPathToGUID(directories[i].Path),
                    addressPrefix = directories[i].Prefix
                }), itemType), i);
            }

            Invoke(method, project, items);
        }

        private static MethodInfo SourceDirectoryMethod()
        {
            var field = typeof(ResourceProject).GetField("sourceDirectories", Flags);
            if (field == null || !field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(List<>))
            {
                throw new MissingFieldException("需要 SourceDirectories: List<ResourceSourceDirectory> 当前模型。 ");
            }

            var item = field.FieldType.GetGenericArguments().Single();
            var enumerable = typeof(IEnumerable<>).MakeGenericType(item);
            return typeof(ResourceEditorOperations).GetMethod("SetSourceDirectories", new[] { typeof(ResourceProject), enumerable }) ??
                throw new MissingMethodException("需要支持 Undo 的 SetSourceDirectories 正式入口。 ");
        }

        private static object Invoke(MethodInfo method, params object[] arguments)
        {
            try
            {
                return method.Invoke(null, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        public static ResourcePolishGuiWindow OpenGui(int task = 0, int mode = 0, int width = 1080, int height = 720)
        {
            RequireIdle();
            var project = RequireModernProject();
            if (EditorUtility.InstanceIDToObject(SessionState.GetInt(Key + "gui", 0)))
            {
                throw new InvalidOperationException("请复用本批已打开的取证窗口。 ");
            }

            var window = ScriptableObject.CreateInstance<ResourcePolishGuiWindow>();
            window.ShowUtility();
            window.position = new Rect(40, 40, width, height);
            window.SetProject(project);
            typeof(ResServiceEditorWindow).GetField("app", Flags).SetValue(window, AppContentCompatibilityId);
            typeof(ResServiceEditorWindow).GetField("generate", Flags).SetValue(window, false);
            typeof(ResServiceEditorWindow).GetField("referenceDirectory", Flags).SetValue(window, Root + "/GeneratedReferences");
            SessionState.SetInt(Key + "gui", window.GetInstanceID());
            SessionState.SetString(Key + "guiOpened", DateTime.UtcNow.ToString("O"));
            ResourceWorkbenchPolishEvidence.SetPage(window, task, mode);
            window.Focus();
            return window;
        }

        public static void SetGuiPage(int task, int mode = 0)
        {
            var window = OwnedGui();
            window.Entries.Clear();
            window.rootHeight = 0;
            ResourceWorkbenchPolishEvidence.SetPage(window, task, mode);
        }

        /// <summary>只记录真实 Repaint 布局和当前原始报告；没有报告不推断构建成功。</summary>
        public static string CaptureGui(string checkpoint)
        {
            var window = OwnedGui();
            if (window.Entries.Count == 0 || window.rootHeight <= 0)
            {
                throw new InvalidOperationException("窗口尚未真实 Repaint，不能记录布局。 ");
            }

            var report = GuiReport(window);
            var path = CheckpointPath("modern-gui-" + checkpoint);
            WriteNew(path, new
            {
                atUtc = DateTime.UtcNow,
                windowOpenedAtUtc = SessionState.GetString(Key + "guiOpened", ""),
                logicalRect = window.position.ToString(),
                dpi = EditorGUIUtility.pixelsPerPoint,
                dark = EditorGUIUtility.isProSkin,
                RootHeight = window.rootHeight,
                entries = window.Entries.Select(x => new { x.Style, x.Rect.x, x.Rect.y, x.Rect.width, x.Rect.height, x.Text }).ToArray(),
                report = ReportData(report),
                provenance = report == null ? "没有原始构建报告，不从目录重建结果。" : "当前窗口持有的正式 ResourceEditorBuildReport。"
            });
            return path;
        }

        public static bool SendGuiClick(string caption)
        {
            var window = OwnedGui();
            var entries = window.Entries.Where(x => x.Text == caption && x.Rect.width > 0 && x.Rect.height > 0 &&
                new Rect(Vector2.zero, window.position.size).Contains(x.Rect.center)).ToArray();
            if (entries.Length != 1)
            {
                throw new InvalidOperationException("真实布局中未找到唯一可见控件：" + caption);
            }

            var position = entries[0].Rect.center;
            window.Focus();
            var down = window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = position });
            var up = window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = position });
            window.Entries.Clear();
            window.rootHeight = 0;
            window.Repaint();
            return down || up;
        }

        public static bool SendGuiKey(KeyCode keyCode, EventModifiers modifiers = EventModifiers.None)
        {
            var window = OwnedGui();
            window.Focus();
            var handled = window.SendEvent(new Event { type = EventType.KeyDown, keyCode = keyCode, modifiers = modifiers });
            window.Entries.Clear();
            window.rootHeight = 0;
            window.Repaint();
            return handled;
        }

        public static string RecordGuiBuild(string checkpoint, DateTime notBeforeUtc)
        {
            var report = GuiReport(OwnedGui());
            if (report == null || report.StartedAtUtc < notBeforeUtc || report.ProjectId != ProjectId ||
                report.ProjectGuid != AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(RequireModernProject())))
            {
                throw new InvalidOperationException("没有该次操作的新正式报告，拒绝把旧报告或文件存在当作成功。 ");
            }

            var path = CheckpointPath("modern-build-" + checkpoint);
            WriteNew(path, ReportData(report));
            return path;
        }

        private static ResourcePolishGuiWindow OwnedGui()
        {
            var window = EditorUtility.InstanceIDToObject(SessionState.GetInt(Key + "gui", 0)) as ResourcePolishGuiWindow;
            if (!window || !ReferenceEquals(typeof(ResServiceEditorWindow).GetField("_project", Flags).GetValue(window), Project))
            {
                throw new InvalidOperationException("本批取证窗口已关闭或切换项目，不操作其他窗口。 ");
            }

            return window;
        }

        private static ResourceEditorBuildReport GuiReport(ResourcePolishGuiWindow window)
        {
            var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", Flags).GetValue(window);
            if (view == null)
            {
                return null;
            }

            var build = view.GetType().GetField("_build", Flags).GetValue(view);
            return (ResourceEditorBuildReport)build.GetType().GetProperty("CurrentReport", Flags).GetValue(build);
        }

        private static object ReportData(ResourceEditorBuildReport report) => report == null ? null : new
        {
            report.StartedAtUtc,
            report.ProjectGuid,
            report.ProjectId,
            report.Platform,
            report.AppContentCompatibilityId,
            report.Operation,
            report.Invocation,
            GenerationRequested = report.IsGenerationRequested,
            report.ReferenceDirectory,
            buildError = report.BuildError?.ToString(),
            publicationError = report.PublicationError?.ToString(),
            generationError = report.GenerationError?.ToString(),
            report.ElapsedMilliseconds,
            report.Summary,
            build = report.Build == null ? null : new
            {
                report.Build.WorkspacePath,
                report.Build.InputFingerprint,
                UnityBuildInvoked = report.Build.WasUnityBuildInvoked,
                report.Build.IsCacheHit,
                report.Build.ContentEncryption,
                assets = report.Build.Map.Assets.Select(x => new { x.SourcePath, address = x.Address.Path, x.BundleName }).ToArray(),
                bundles = report.Build.Map.Bundles.Select(x => new { x.Name, x.AssetPaths, x.Dependencies, x.IsShared, x.IsScene }).ToArray()
            },
            release = report.Release == null ? null : new
            {
                report.Release.DirectoryPath,
                report.Release.Catalog.CatalogDigest,
                report.Release.Catalog.BuildId
            }
        };

        /// <summary>明确摘要的单次真实 Bundle 消费；返回 run 目录，后续只读该目录，不重复 Begin 来轮询。</summary>
        public static string BeginRuntime(string catalogDigest, bool expectAtlas)
        {
            RequireIdle();
            RequireBatchOwner();
            RejectDirtyScenes();
            var project = RequireModernProject();
            if (_startup != null || _owned != null)
            {
                throw new InvalidOperationException("上一 run 仍保留自有责任，不能以新 run 隐藏收尾失败。 ");
            }

            var release = ResourcePublisher.ReadVersion(project, EditorUserBuildSettings.activeBuildTarget, AppContentCompatibilityId, catalogDigest);
            if (release.Catalog.Assets.Any(x => x.Address == AtlasAddress) != expectAtlas)
            {
                throw new InvalidOperationException("显式版本的 Atlas 状态与本次期望不符。 ");
            }

            if (!release.Catalog.Assets.Any(x => x.Kind == "Scene" && x.Address == SceneAddress))
            {
                throw new InvalidOperationException("显式版本缺少本批代表场景。 ");
            }

            var id = "runtime-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
            var directory = Path.Combine(EvidenceRoot, id);
            RequireAbsent(directory);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(directory));
            Folder(Root + "/Runs", id);
            _run = new RuntimeRun
            {
                Directory = directory,
                State = "WaitingForPlay",
                CatalogDigest = catalogDigest,
                SourceDirectory = release.DirectoryPath,
                Platform = release.Catalog.Platform,
                ExpectAtlas = expectAtlas,
                RuntimeScene = Root + "/Runs/" + id + "/EmptyRuntime.unity",
                OriginalScenes = EditorSceneManager.GetSceneManagerSetup(),
                StartedAtUtc = DateTime.UtcNow,
                DeadlineUtc = DateTime.UtcNow.AddSeconds(45)
            };
            _sceneCompletedFrame = -1;
            WriteNew(Path.Combine(directory, "intent.json"), _run);
            SaveRun();
            Attach();
            var preparationScene = default(Scene);
            try
            {
                preparationScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (!EditorSceneManager.SaveScene(preparationScene, _run.RuntimeScene))
                {
                    throw new IOException("无法保存本次自有空场景。 ");
                }

                RecordState("WaitingForPlay");
                EditorApplication.isPlaying = true;
            }
            catch (Exception error)
            {
                AddError(error);
                RestoreAndFinish(preparationScene);
                throw;
            }
            return directory;
        }

        /// <summary>同一 run 的只读状态。Terminal=true 才是终态；Passed 还要求收尾和原场景恢复均成功。</summary>
        public static string ReadRuntimeRun(string runDirectory)
        {
            var full = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(runDirectory)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(EvidenceRoot)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("runtime-", StringComparison.Ordinal) || !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(full, "intent.json"))))
            {
                throw new ArgumentException("必须读取 BeginRuntime 返回的本批 run 目录。", nameof(runDirectory));
            }

            var terminal = Path.Combine(full, "terminal.json");
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(terminal)))
            {
                return File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(terminal));
            }

            var last = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(full), "state-*.json").OrderBy(x => x, StringComparer.Ordinal).LastOrDefault();
            return File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(last ?? Path.Combine(full, "intent.json")));
        }

        public static string CancelRuntime()
        {
            if (_run == null || _run.Terminal)
            {
                return "没有进行中的自有 run。";
            }

            AddError(new OperationCanceledException("调用方取消本次取证。"));
            if (_run.State == "WaitingForPlay" || _run.State == "Releasing" || _run.State == "Stopping" || _run.State == "LeavingPlay")
            {
                return "已记录取消；继续等待本次转换或已有自有收尾，不重复创建关闭请求。";
            }

            BeginCleanup();
            return _run.Directory;
        }

        private static void Attach()
        {
            EditorApplication.playModeStateChanged -= OnPlayState;
            EditorApplication.playModeStateChanged += OnPlayState;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Detach()
        {
            EditorApplication.playModeStateChanged -= OnPlayState;
            EditorApplication.update -= Tick;
        }

        private static void OnPlayState(PlayModeStateChange state)
        {
            if (_run == null || _run.Terminal)
            {
                return;
            }

            try
            {
                if (state == PlayModeStateChange.EnteredPlayMode)
                {
                    if (_run.State != "WaitingForPlay" || RuntimeInUse() || AssetBundle.GetAllLoadedAssetBundles().Any())
                    {
                        Block("Play 中出现其他资源责任，未创建 Startup，也不退出该会话。");
                        return;
                    }
                    if (SceneManager.sceneCount != 1 || SceneManager.GetActiveScene().path != _run.RuntimeScene ||
                        SceneManager.GetActiveScene().GetRootGameObjects().Length != 0)
                    {
                        Block("Play 并非本次保存的唯一空场景，拒绝接管。");
                        return;
                    }
                    var snapshot = ResourceLocalSnapshot.Open(_run.SourceDirectory, ProjectId, _run.CatalogDigest, _run.Platform, AppContentCompatibilityId);
                    _startup = new ResourceStartup(new ResourceConfiguration(new[] { snapshot.CreateCatalog() }));
                    _starting = _startup.StartAsync();
                    Transition("Starting");
                }
                else if (state == PlayModeStateChange.ExitingPlayMode && _run.State != "LeavingPlay")
                {
                    AddError(new InvalidOperationException("外部提前退出 Play，不能把会话失效报告为成功收尾。"));
                }
                else if (state == PlayModeStateChange.EnteredEditMode)
                {
                    RestoreAndFinish();
                }
            }
            catch (Exception error)
            {
                AddError(error);
                BeginCleanup();
            }
        }

        private static void Tick()
        {
            if (_run == null || _run.Terminal)
            {
                return;
            }

            try
            {
                if (DateTime.UtcNow > _run.DeadlineUtc)
                {
                    AddError(new TimeoutException("本次有限阶段超时：" + _run.State));
                    if (_run.State == "WaitingForPlay" && !EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        RestoreAndFinish();
                    }
                    else if (_run.State == "Stopping" || _run.State == "Releasing" || _run.State == "LeavingPlay")
                    {
                        Block("收尾超时，未强制关闭其他责任或全局卸载。");
                    }
                    else
                    {
                        BeginCleanup();
                    }

                    return;
                }
                if (_run.State == "WaitingForPlay" || _run.State == "LeavingPlay")
                {
                    return;
                }
                // 预期的 Edit->Play 重载发生在 WaitingForPlay；运行中重编译不能重新认领丢失的 Startup。
                if (_startup == null)
                {
                    Block("运行中发生程序集重载，Startup 身份已丢失；不认领 ResourceSystem.Current。");
                    return;
                }
                if (_run.State == "Starting")
                {
                    if (!_starting.IsDone)
                    {
                        return;
                    }

                    _starting.TakeResult();
                    _starting.Dispose();
                    _starting = null;
                    _owned = _startup.Resources;
                    if (!_startup.OwnsResourceSystem || !ReferenceEquals(_owned, ResourceSystem.Current))
                    {
                        Block("Startup 没有创建并持有当前会话，不关闭借用/外部会话。");
                        return;
                    }
                    _scope = _owned.CreateScope();
                    if (_run.Error != null)
                    {
                        BeginCleanup();
                        return;
                    }
                    VerifyAssets();
                    _loadingScene = _owned.LoadSceneAsync(Address(SceneAddress), LoadSceneMode.Additive, true, 30);
                    Transition("SceneLoading");
                }
                else if (_run.State == "SceneLoading")
                {
                    if (!_loadingScene.IsDone)
                    {
                        return;
                    }

                    _sceneLease = _loadingScene.TakeResult();
                    var scene = _sceneLease.Scene;
                    Require(scene.path.Equals(Root + "/GameScenes/Representative.unity", StringComparison.OrdinalIgnoreCase), "Scene 路径不符。 ");
                    Require(_sceneLease.Record.CatalogDigest == _run.CatalogDigest, "Scene 并非来自确切发布快照。 ");
                    var marker = scene.GetRootGameObjects().Single(x => x.name == "ModernSceneMarker");
                    Require(marker.transform.position == new Vector3(3, 2, 1), "Scene 标记实值不符。 ");
                    VerifyMaterial(marker.GetComponent<MeshRenderer>().sharedMaterial);
                    Observe("scene", new
                    {
                        path = scene.path,
                        scene.handle,
                        _sceneLease.Record.LoadSequence,
                        _sceneLease.Record.CatalogDigest,
                        roots = scene.GetRootGameObjects().Select(x => x.name).ToArray(),
                        markerPosition = marker.transform.position.ToString()
                    });
                    _unloadingScene = _owned.UnloadSceneAsync(_sceneLease);
                    Transition("SceneUnloading");
                }
                else if (_run.State == "SceneUnloading")
                {
                    if (!_unloadingScene.IsDone)
                    {
                        return;
                    }

                    Require(_unloadingScene.TakeResult() && _sceneLease.Record.IsUnloaded, "Scene 未真实卸载。 ");
                    _unloadingScene.Dispose();
                    _unloadingScene = null;
                    _sceneLease.Dispose();
                    _sceneLease = null;
                    _loadingScene.Dispose();
                    _loadingScene = null;
                    Observe("sceneUnloaded", true);
                    BeginCleanup();
                }
                else if (_run.State == "Releasing")
                {
                    if (_loadingScene != null)
                    {
                        // 取消不等于 native 撤销；只等待这个请求留下的准确 SceneRecord，不按路径猜身份。
                        if (_loadingScene.HasNativeStarted && !_loadingScene.IsNativeCompleted)
                        {
                            return;
                        }

                        if (_loadingScene.HasNativeStarted)
                        {
                            // native completed 后，正式服务还需要帧末身份结算；不能把暂时空的 Scenes 当成无责任。
                            if (_sceneCompletedFrame < 0)
                            {
                                _sceneCompletedFrame = Time.frameCount;
                                return;
                            }
                            if (Time.frameCount <= _sceneCompletedFrame + 1)
                            {
                                return;
                            }
                        }
                        var records = _loadingScene.Scenes.Where(x => !x.IsUnloaded).ToArray();
                        if (records.Length != 0)
                        {
                            if (records.Any(x => x.IsAmbiguous))
                            {
                                Block("Scene 身份有歧义，不卸载他人候选。");
                                return;
                            }
                            if (_unloadingScene == null)
                            {
                                _unloadingScene = _owned.UnloadSceneAsync(records.Single());
                            }
                        }
                        if (_unloadingScene != null)
                        {
                            if (!_unloadingScene.IsDone)
                            {
                                return;
                            }

                            Require(_unloadingScene.TakeResult(), "自有 Scene 收尾失败。 ");
                            _unloadingScene.Dispose();
                            _unloadingScene = null;
                        }
                        if (_loadingScene.Scenes.Any(x => !x.IsUnloaded))
                        {
                            return;
                        }

                        _loadingScene.Dispose();
                        _loadingScene = null;
                    }
                    if (Time.frameCount <= _releaseFrame)
                    {
                        return;
                    }

                    Require(_clones.All(x => !x), "GetSprite 克隆未完成销毁。 ");
                    Observe("spriteClones", new
                    {
                        created = _clones.Count,
                        allDestroyed = _clones.All(x => !x)
                    });
                    _clones.Clear();
                    _scope?.Dispose();
                    _scope = null;
                    if (_owned != null && !ReferenceEquals(ResourceSystem.Current, _owned))
                    {
                        Block("会话身份已改变，不 Stop 或退出其他会话。");
                        return;
                    }
                    _stopping = _startup.StopAsync();
                    Transition("Stopping");
                }
                else if (_run.State == "Stopping")
                {
                    if (!_stopping.IsDone)
                    {
                        return;
                    }

                    if (_stopping.Status != ResourceRequestStatus.Succeeded)
                    {
                        var error = (Exception)_stopping.Error ?? new InvalidOperationException("Startup 关闭请求未成功。 ");
                        _stopping.Dispose();
                        _stopping = null;
                        AddError(error);
                        Block("自有 Startup 拒绝停止，保留他人责任与 Play。");
                        return;
                    }
                    Require(_stopping.TakeResult(), "Startup 未确认停止。 ");
                    _stopping.Dispose();
                    _stopping = null;
                    _run.StartupStopped = true;
                    Observe("afterStop", _owned == null ? null : (object)_owned.GetDiagnosticsSnapshot());
                    Require(!RuntimeInUse() && !AssetBundle.GetAllLoadedAssetBundles().Any(), "停止后仍有资源会话或 native Bundle，不强制退出。 ");
                    _owned = null;
                    _startup = null;
                    Transition("LeavingPlay");
                    EditorApplication.isPlaying = false;
                }
            }
            catch (Exception error)
            {
                AddError(error);
                if (_run.State == "Releasing" || _run.State == "Stopping")
                {
                    Block("自有收尾失败；未调用全局卸载或强制关闭会话。");
                }
                else
                {
                    BeginCleanup();
                }
            }
        }

        private static void VerifyAssets()
        {
            var a = _scope.Acquire<GameObject>(Address(PrefabAddress));
            a.OwnerLabel = "多目录取证 Prefab A";
            var b = _scope.Acquire<GameObject>(Address(OtherPrefabAddress));
            b.OwnerLabel = "多目录取证 Prefab B";
            var text = _scope.Acquire<TextAsset>(Address(TextAddress));
            text.OwnerLabel = "多目录取证文本";
            var alias = _scope.Acquire<TextAsset>(Address("message"));
            Require(text.Asset.text == Message && text.Asset == alias.Asset, "文本实值或 alias 身份不符。 ");
            var materialA = a.Asset.GetComponent<MeshRenderer>().sharedMaterial;
            var materialB = b.Asset.GetComponent<MeshRenderer>().sharedMaterial;
            VerifyMaterial(materialA);
            VerifyMaterial(materialB);
            Require(materialA == materialB && materialA.mainTexture == materialB.mainTexture, "跨 Group 共享依赖被重复加载。 ");
            _cache = _owned.GetInstanceCache(a, InstanceCachePolicy.Persistent);
            _instance = _cache.Instantiate();
            VerifyMaterial(_instance.GameObject.GetComponent<MeshRenderer>().sharedMaterial);
            Observe("assets", new
            {
                prefabA = a.Asset.name,
                prefabB = b.Asset.name,
                instance = _instance.GameObject.name,
                material = materialA.name,
                texture = materialA.mainTexture.name,
                width = materialA.mainTexture.width,
                height = materialA.mainTexture.height,
                pixel = ((Texture2D)materialA.mainTexture).GetPixel(0, 0).ToString(),
                scale = materialA.mainTextureScale.ToString(),
                offset = materialA.mainTextureOffset.ToString(),
                sharedMaterial = materialA == materialB,
                sharedTexture = materialA.mainTexture == materialB.mainTexture,
                text = text.Asset.text,
                aliasSameObject = text.Asset == alias.Asset
            });
            using (var missing = _owned.AcquireAsync<TextAsset>(Address("text/unselected.txt")))
            {
                Require(missing.IsDone && missing.Error?.Kind == ResourceError.AddressNotFound, "未选择文本意外可取得。 ");
            }

            if (_run.ExpectAtlas)
            {
                var atlas = _scope.Acquire<SpriteAtlas>(Address(AtlasAddress));
                atlas.OwnerLabel = "多目录取证图集";
                Require(atlas.Asset.spriteCount == 2, "图集必须真实包含两个源根的 Sprite。 ");
                VerifySprite(atlas.Asset, "ArtIcon", 8, 8, new Color32(220, 40, 20, 255));
                VerifySprite(atlas.Asset, "UiIcon", 12, 8, new Color32(20, 180, 60, 255));
            }
            var diagnostics = _owned.GetDiagnosticsSnapshot();
            Require(diagnostics.NativeBundleLoadCount > 0 && diagnostics.BundleRecordCount > 0, "没有真实 native Bundle 加载证据。 ");
            Observe("nativeBeforeRelease", diagnostics);
        }

        private static void VerifyMaterial(Material material)
        {
            Require(material && material.mainTexture is Texture2D, "Prefab/Scene 缺少真实材质或纹理引用。 ");
            var texture = (Texture2D)material.mainTexture;
            Require(texture.width == 16 && texture.height == 8 && texture.isReadable, "依赖纹理尺寸/可读性不符。 ");
            Require(((Color32)texture.GetPixel(0, 0)).Equals(_pixel), "依赖纹理像素实值不符。 ");
            Require(material.mainTextureScale == _textureScale && material.mainTextureOffset == _textureOffset, "材质 UV 实值不符。 ");
        }

        private static void VerifySprite(SpriteAtlas atlas, string name, int width, int height, Color32 pixel)
        {
            var sprite = atlas.GetSprite(name);
            Require(sprite, "图集中找不到 Sprite：" + name);
            _clones.Add(sprite);
            try
            {
                Require(sprite.rect.width == width && sprite.rect.height == height && sprite.texture, "Sprite 尺寸或图集纹理不符：" + name);
                var rect = sprite.textureRect;
                var actual = (Color32)sprite.texture.GetPixel((int)rect.x + 1, (int)rect.y + 1);
                Require(actual.Equals(pixel), "Sprite 图集像素实值不符：" + name);
                Observe("sprite-" + name, new
                {
                    sprite.name,
                    width = sprite.rect.width,
                    height = sprite.rect.height,
                    textureWidth = sprite.texture.width,
                    textureHeight = sprite.texture.height,
                    pixel = actual.ToString()
                });
            }
            finally
            {
                Object.Destroy(sprite);
            }
        }

        private static void BeginCleanup()
        {
            if (_run == null || _run.Terminal)
            {
                return;
            }

            try
            {
                _instance?.Discard();
                _instance = null;
                _cache?.Dispose();
                _cache = null;
                // Atlas 的借用纹理必须活到 GetSprite 克隆真正 Destroy；Scope 在下一阶段确认后才释放。
                _starting?.Dispose();
                _starting = null;
                _sceneLease?.Dispose();
                _sceneLease = null;
                // Dispose 取消自己的等待并放行 native 激活；保留请求以等待准确 SceneRecord 的收尾。
                _loadingScene?.Dispose();
                if (_owned == null && _startup?.Resources != null)
                {
                    _owned = _startup.Resources;
                }

                if (_startup == null)
                {
                    if (RuntimeInUse() || AssetBundle.GetAllLoadedAssetBundles().Any())
                    {
                        Block("未持有 Startup 且存在其他资源责任。");
                        return;
                    }
                    if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        RestoreAndFinish();
                        return;
                    }
                    Transition("LeavingPlay");
                    EditorApplication.isPlaying = false;
                    return;
                }
                _releaseFrame = Time.frameCount;
                Transition("Releasing");
            }
            catch (Exception error)
            {
                AddError(error);
                Block("释放本批责任时失败，未继续关闭外部会话。");
            }
        }

        private static void RestoreAndFinish(Scene unpublishedOwnedScene = default)
        {
            if (_run == null || _run.Terminal)
            {
                return;
            }

            try
            {
                if (RuntimeInUse() || AssetBundle.GetAllLoadedAssetBundles().Any())
                {
                    throw new InvalidOperationException("资源责任尚未排空，拒绝切换场景。 ");
                }

                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    var ownPreparation = unpublishedOwnedScene.IsValid() && scene == unpublishedOwnedScene && !EditorApplication.isPlaying;
                    if (!ownPreparation && scene.path != _run.RuntimeScene && !_run.OriginalScenes.Any(x => x.path == scene.path))
                    {
                        throw new InvalidOperationException("出现非本次所有的场景，拒绝覆盖：" + scene.path);
                    }

                    if (scene.isDirty && scene.path != _run.RuntimeScene && !ownPreparation)
                    {
                        throw new InvalidOperationException("原场景已变脏，拒绝恢复覆盖。 ");
                    }
                }
                EditorSceneManager.RestoreSceneManagerSetup(_run.OriginalScenes);
                var restored = EditorSceneManager.GetSceneManagerSetup();
                _run.ScenesRestored = JsonConvert.SerializeObject(restored) == JsonConvert.SerializeObject(_run.OriginalScenes);
                Require(_run.ScenesRestored, "原 Scene setup 未完整恢复。 ");
                Require(_run.StartupStopped, "没有确认本次 Startup 的正常停止。 ");
            }
            catch (Exception error)
            {
                AddError(error);
            }
            _run.Passed = _run.Error == null && _run.ScenesRestored && _run.StartupStopped;
            Finish(_run.Passed ? "Completed" : "Failed");
            _startup = null;
            _owned = null;
            _starting = null;
            _stopping = null;
            _scope = null;
            _loadingScene = null;
            _unloadingScene = null;
            _sceneLease = null;
            _cache = null;
            _instance = null;
            _clones.Clear();
        }

        private static void Block(string reason)
        {
            AddError(new InvalidOperationException(reason));
            _run.Passed = false;
            Finish("BlockedOwnershipOrCleanup");
        }

        private static void Finish(string state)
        {
            _run.Terminal = true;
            RecordState(state);
            WriteNew(Path.Combine(_run.Directory, "terminal.json"), _run);
            SaveRun();
            Detach();
        }

        private static void Transition(string state)
        {
            _run.DeadlineUtc = DateTime.UtcNow.AddSeconds(45);
            RecordState(state);
        }
        private static void Observe(string name, object value) => _run.Observations[name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
        private static void AddError(Exception error)
        {
            _run.Error = _run.Error == null ? error.ToString() : _run.Error + "\n" + error;
            SaveRun();
        }
        private static void SaveRun() => SessionState.SetString(Key + "run", JsonConvert.SerializeObject(_run));

        private static void RecordState(string state)
        {
            _run.State = state;
            Observe("currentEditor", new
            {
                atUtc = DateTime.UtcNow,
                playing = EditorApplication.isPlaying,
                resourceSession = ResourceSystem.Current != null,
                runtimeResponsibility = RuntimeInUse(),
                loadedBundles = AssetBundle.GetAllLoadedAssetBundles().Count(),
                scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).path).ToArray()
            });
            WriteNew(Path.Combine(_run.Directory, "state-" + (++_run.Sequence).ToString("D4") + ".json"), _run);
            SaveRun();
        }

        private static ResourceAddress Address(string path) => new(ProjectId, path);
        private static void Require(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static ResourceProject RequireModernProject()
        {
            RequireBatchOwner();
            var project = Project;
            if (!project || project.projectId != ProjectId || EditorUtility.IsDirty(project) || !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/modern-owner.json")))
            {
                throw new InvalidOperationException("本批 ModernProject 缺失、身份不符或未保存，不创建/修复旧夹具。 ");
            }

            var owner = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/modern-owner.json")));
            if ((string)owner["root"] != Root || (string)owner["projectId"] != ProjectId ||
                (string)owner["projectGuid"] != AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)))
            {
                throw new InvalidOperationException("ModernProject 的 owner/GUID 不符，不认领替换后的资产。 ");
            }

            return project;
        }

        private static void RequireBatchOwner()
        {
            if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(EvidenceRoot)) || !AssetDatabase.IsValidFolder(FixtureRoot))
            {
                throw new DirectoryNotFoundException("主代理登记的本批根目录缺失。 ");
            }

            var owner = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(FixtureRoot + "/multidirectory-owner.json")));
            if ((string)owner["Root"] != FixtureRoot || !string.Equals(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical((string)owner["Evidence"])), Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(EvidenceRoot)), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("本批 owner 与明确授权范围不符。 ");
            }
        }

        private static void RequireIdle()
        {
            if (_run != null && !_run.Terminal || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPaused ||
                EditorApplication.isCompiling || EditorApplication.isUpdating ||
                RuntimeInUse() || AssetBundle.GetAllLoadedAssetBundles().Any())
            {
                throw new InvalidOperationException("Unity 正忙，或已有 Runtime/Bundle 责任；拒绝接管。 ");
            }
        }

        private static bool RuntimeInUse()
        {
            // Current 不暴露 Initializing 或退出后的 draining；取证门禁只读既有权威字段，不新建或接管会话。
            var type = typeof(ResourceSystem);
            var authority = type.GetField("_authority", Flags) ?? throw new MissingFieldException(type.FullName, "authority");
            var draining = type.GetField("_draining", Flags) ?? throw new MissingFieldException(type.FullName, "draining");
            return authority.GetValue(null) != null || ((IEnumerable<ResourceSystem>)draining.GetValue(null)).Any();
        }

        private static void RejectDirtyScenes()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("用户场景未保存，拒绝切换或自动保存。 ");
                }
            }
        }

        private static void RequireAbsent(string path)
        {
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")))
            {
                throw new IOException("已有身份不接管、不覆盖：" + path);
            }
        }

        private static void Folder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent))
            {
                throw new DirectoryNotFoundException(parent);
            }

            var path = parent + "/" + name;
            RequireAbsent(path);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, name)))
            {
                throw new IOException("无法创建自有目录：" + path);
            }
        }

        private static void Text(string path, string content)
        {
            RequireAbsent(path);
            using (var file = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void Texture(string path, int width, int height, Color32 color, bool sprite)
        {
            RequireAbsent(path);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                texture.SetPixels32(Enumerable.Repeat(color, width * height).ToArray());
                texture.Apply();
                using var file = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var bytes = texture.EncodeToPNG();
                file.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = sprite ? TextureImporterType.Sprite : TextureImporterType.Default;
            if (sprite)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
            }

            importer.isReadable = true;
            importer.mipmapEnabled = false;
            importer.sRGBTexture = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.filterMode = FilterMode.Point;
            importer.SaveAndReimport();
        }

        private static string CheckpointPath(string name)
        {
            RequireBatchOwner();
            if (!Regex.IsMatch(name, "^[a-z0-9-]+$"))
            {
                throw new ArgumentException("证据名称仅允许小写 ASCII 字母、数字和连字符。", nameof(name));
            }

            return Path.Combine(EvidenceRoot, name + ".json");
        }

        private static void WriteNew(string path, object value)
        {
            if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path))))
            {
                throw new DirectoryNotFoundException(Path.GetDirectoryName(path));
            }

            using var file = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
        }
    }
}
