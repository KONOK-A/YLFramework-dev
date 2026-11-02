using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    // 本批独立取证；不恢复构建报告，不持有资源或永久Editor订阅。
    public static class ResourceWorkbenchPolishEvidence
    {
        public const string Root = "Assets/Tests/YLFramework/Fixtures/WorkbenchPolish-20261018";
        public static string EvidenceRoot => Path.Combine(Path.GetTempPath(), "kilo", "workbench-polish-20261018");
        public const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        public static ResourceProject Project => AssetDatabase.LoadAssetAtPath<ResourceProject>(Root + "/WorkbenchProject.asset");

        public static string Begin()
        {
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(EvidenceRoot)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root)))
            {
                throw new InvalidOperationException("本批范围已存在；只读取已有记录，不覆盖。");
            }

            if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(EvidenceRoot))))
            {
                throw new DirectoryNotFoundException("Evidence parent missing.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Unity busy.");
            }

            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("User scene dirty.");
                }
            }

            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(EvidenceRoot));
            var trees = new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "AssetBundles/MainModule", "Packages", "ProjectSettings", "Assets/Plugins/NuGet",
                "Assets/Tests/YLFramework/Fixtures/WorkbenchUX1-20261017", "Assets/Tests/YLFramework/Fixtures/WorkbenchUX2To5-20261017",
                "AssetBundles/YLFramework/yl-workbench-ux2to5-20261017" };
            Write("protected-before", trees.ToDictionary(x => x, x => ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(x)))));
            Write("environment-before", new
            {
                utc = DateTime.UtcNow,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                unity = Application.unityVersion,
                target = EditorUserBuildSettings.activeBuildTarget.ToString(),
                dark = EditorGUIUtility.isProSkin,
                dpi = EditorGUIUtility.pixelsPerPoint,
                selection = Selection.assetGUIDs,
                focus = EditorWindow.focusedWindow ? EditorWindow.focusedWindow.GetType().FullName : null
            });
            return EvidenceRoot;
        }

        public static ResourcePolishGuiWindow OpenBaseline(int task = 0, int mode = 0, int width = 1080, int height = 720)
        {
            if (Resources.FindObjectsOfTypeAll<ResourcePolishGuiWindow>().Length != 0)
            {
                throw new InvalidOperationException("Use the existing owned window.");
            }

            var old = AssetDatabase.LoadAssetAtPath<ResourceProject>("Assets/Tests/YLFramework/Fixtures/WorkbenchUX2To5-20261017/WorkbenchProject.asset");
            if (!old || EditorUtility.IsDirty(old))
            {
                throw new InvalidOperationException("Old fixture unavailable or edited; do not restore it.");
            }

            var window = ScriptableObject.CreateInstance<ResourcePolishGuiWindow>();
            window.ShowUtility();
            window.position = new Rect(40, 40, width, height);
            window.SetProject(old);
            SetPage(window, task, mode);
            window.Focus();
            return window;
        }

        public static void SetPage(EditorWindow window, int task, int mode = 0)
        {
            var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", Flags).GetValue(window);
            view.GetType().GetField("_tab", Flags).SetValue(view, task);
            var layout = typeof(ResServiceEditorWindow).GetField("layout", Flags).GetValue(window);
            layout.GetType().GetField("settings", Flags).SetValue(layout, false);
            layout.GetType().GetField("task", Flags).SetValue(layout, task);
            if (task == 0)
            {
                var definition = view.GetType().GetField("_definition", Flags).GetValue(view);
                definition.GetType().GetField("_mode", Flags).SetValue(definition, mode);
            }
            if (task == 4)
            {
                var diagnostics = view.GetType().GetField("_diagnostics", Flags).GetValue(view);
                diagnostics.GetType().GetField("_panel", Flags).SetValue(diagnostics, mode);
            }
            window.Repaint();
        }

        public static string Capture(string name)
        {
            var window = Resources.FindObjectsOfTypeAll<ResourcePolishGuiWindow>().Single();
            Write(name, new
            {
                utc = DateTime.UtcNow,
                window = window.position.ToString(),
                width = window.position.width,
                height = window.position.height,
                dpi = EditorGUIUtility.pixelsPerPoint,
                dark = EditorGUIUtility.isProSkin,
                RootHeight = window.rootHeight,
                entries = window.Entries.Select(x => new { x.Style, x.Rect.x, x.Rect.y, x.Rect.width, x.Rect.height, x.Text }),
                root = window.layoutTrace
            });
            return Path.Combine(EvidenceRoot, name + ".json");
        }

        public static string CreateFixture()
        {
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root + ".meta")))
            {
                throw new InvalidOperationException("Fixture already exists.");
            }

            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", Path.GetFileName(Root));
            AssetDatabase.CreateFolder(Root, "Content");
            AssetDatabase.CreateFolder(Root + "/Content", "Messages");
            for (var i = 1; i <= 4; i++)
            {
                var path = Root + "/Content/Messages/message-" + i + ".json";
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "{\"message\":\"Workbench polish owned " + i + "\"}");
                AssetDatabase.ImportAsset(path);
            }
            foreach (var folder in new[] { "Notes", "Extra" })
            {
                AssetDatabase.CreateFolder(Root + "/Content", folder);
                var path = Root + "/Content/" + folder + "/note.json";
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "{\"message\":\"Owned " + folder + " for explicit GUI addition\"}");
                AssetDatabase.ImportAsset(path);
            }
            var project = ResourceEditorOperations.CreateProject(Root + "/WorkbenchProject.asset");
            project.projectId = "yl-workbench-polish-20261018";
            project.displayName = "Workbench 易用性体验";
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(Root + "/Content") });
            project.groups.Add(new ResourceGroup
            {
                id = "messages",
                packing = ResourcePacking.Together,
                selections = new List<ResourceSelection>
                { new() { guid = AssetDatabase.AssetPathToGUID(Root + "/Content/Messages") } }
            });
            project.groups.Add(new ResourceGroup
            {
                id = "optional",
                packing = ResourcePacking.PerAsset,
                selections = new List<ResourceSelection>
                { new() { guid = AssetDatabase.AssetPathToGUID(Root + "/Content/Notes/note.json") } }
            });
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            AssetDatabase.CreateFolder(Root, "BlockedReferences");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/BlockedReferences/owned-canary.txt"), "Owned conflict for partial-success GUI. Do not replace with generated source.");
            AssetDatabase.ImportAsset(Root + "/BlockedReferences/owned-canary.txt");
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = project;
            recipe.target = EditorUserBuildSettings.activeBuildTarget;
            recipe.appContentCompatibilityId = "polish-app-v1";
            recipe.action = ResourceBuildAction.BuildAndPublish;
            recipe.generateReferences = true;
            recipe.referenceDirectory = Root + "/BlockedReferences";
            AssetDatabase.CreateAsset(recipe, Root + "/PartialFailureRecipe.asset");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/polish-fixture-owner.json"), JsonConvert.SerializeObject(new
            {
                createdUtc = DateTime.UtcNow,
                projectId = project.projectId,
                guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)),
                app = "polish-app-v1",
                root = Root,
                evidence = EvidenceRoot,
                authorization = "Non-production GUI None build/publish and controlled code-generation conflict; only this identity's outputs are owned. Old UX1/UX2-5 fixtures and R1/R2 are read-only."
            }, Formatting.Indented));
            AssetDatabase.ImportAsset(Root + "/polish-fixture-owner.json");
            return AssetDatabase.GetAssetPath(project);
        }

        public static void Write(string name, object value)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9-]+$"))
            {
                throw new ArgumentException(nameof(name));
            }

            var path = Path.Combine(EvidenceRoot, name + ".json");
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
            {
                throw new InvalidOperationException("Evidence exists; use another explicit checkpoint name.");
            }

            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), JsonConvert.SerializeObject(value, Formatting.Indented));
        }
    }

    public sealed class ResourcePolishGuiWindow : ResServiceEditorWindow
    {
        public sealed class Entry
        {
            public string Style;
            public Rect Rect;
            public string Text;
        }
        public List<Entry> Entries = new();
        [global::UnityEngine.Serialization.FormerlySerializedAs("RootHeight")]
        public float rootHeight;
        [global::UnityEngine.Serialization.FormerlySerializedAs("LayoutTrace")]
        public string layoutTrace;
        private void OnGUI()
        {
            typeof(ResServiceEditorWindow).GetMethod("OnGUI", ResourceWorkbenchPolishEvidence.Flags).Invoke(this, null);
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            var cache = typeof(GUILayoutUtility).GetProperty("current", ResourceWorkbenchPolishEvidence.Flags)?.GetValue(null) ??
                typeof(GUILayoutUtility).GetField("current", ResourceWorkbenchPolishEvidence.Flags).GetValue(null);
            var top = cache.GetType().GetField("topLevel", ResourceWorkbenchPolishEvidence.Flags).GetValue(cache);
            rootHeight = ((Rect)top.GetType().GetField("rect", ResourceWorkbenchPolishEvidence.Flags).GetValue(top)).height;
            Entries.Clear();
            Walk(top, Vector2.zero);
            layoutTrace = string.Join("\n", Entries.Select(x => x.Style + " " + x.Rect + " " + x.Text));
        }
        private void Walk(object entry, Vector2 offset)
        {
            var flags = ResourceWorkbenchPolishEvidence.Flags;
            var type = entry.GetType();
            var style = type.GetProperty("style", flags)?.GetValue(entry) as GUIStyle;
            var content = type.GetField("m_Content", flags)?.GetValue(entry) as GUIContent;
            var rect = (Rect)type.GetField("rect", flags).GetValue(entry);
            if ((bool?)type.GetField("resetCoords", flags)?.GetValue(entry) == true)
            {
                offset += rect.position;
            }

            Entries.Add(new Entry { Style = style?.name ?? "", Rect = new Rect(rect.position + offset, rect.size), Text = content?.text });
            if (type.GetField("entries", flags)?.GetValue(entry) is IEnumerable children)
            {
                foreach (var child in children)
                {
                    Walk(child, offset);
                }
            }
        }
    }
}
