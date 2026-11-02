using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public static class ResourceWorkbenchFinalEvidence
    {
        public const string Root = "Assets/Tests/YLFramework/Fixtures/WorkbenchFinalUI-20261018";
        public static string EvidenceRoot => Path.Combine(Path.GetTempPath(), "kilo", "workbench-final-ui-20261018-1153");

        public static string Begin()
        {
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(EvidenceRoot)))
            {
                throw new InvalidOperationException("Evidence already exists; do not overwrite.");
            }

            if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(EvidenceRoot))))
            {
                throw new DirectoryNotFoundException("Evidence parent missing.");
            }

            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(EvidenceRoot));
            Write("owner", new
            {
                createdUtc = DateTime.UtcNow,
                scope = "Final Workbench A-E display, input, layout and read-only confirmation; no build/publication/deployment or runtime initialization",
                fixture = Root
            });
            var trees = new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "AssetBundles/MainModule", "Packages", "ProjectSettings", "Assets/Plugins/NuGet",
                "Assets/Tests/YLFramework/Fixtures/WorkbenchUX1-20261017", "Assets/Tests/YLFramework/Fixtures/WorkbenchUX2To5-20261017", "Assets/Tests/YLFramework/Fixtures/WorkbenchPolish-20261018",
                "AssetBundles/YLFramework/yl-workbench-ux2to5-20261017", "AssetBundles/YLFramework/yl-workbench-polish-20261018" };
            Write("protected-before", trees.ToDictionary(x => x, x => ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(x)))));
            Write("environment-before", new
            {
                utc = DateTime.UtcNow,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                dark = EditorGUIUtility.isProSkin,
                dpi = EditorGUIUtility.pixelsPerPoint,
                selection = Selection.assetGUIDs,
                focus = EditorWindow.focusedWindow ? EditorWindow.focusedWindow.GetType().FullName : null,
                scenes = Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Select(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i)).Select(s => new { s.path, s.isDirty }).ToArray()
            });
            return EvidenceRoot;
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
                throw new InvalidOperationException("Evidence already exists: " + name);
            }

            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), JsonConvert.SerializeObject(value, Formatting.Indented));
        }

        public static string Capture(string name, string purpose)
        {
            var window = Resources.FindObjectsOfTypeAll<ResourcePolishGuiWindow>().Single();
            Write(name, new
            {
                utc = DateTime.UtcNow,
                phase = "B: current final-UI batch",
                purpose,
                width = window.position.width,
                height = window.position.height,
                dark = EditorGUIUtility.isProSkin,
                dpi = EditorGUIUtility.pixelsPerPoint,
                RootHeight = window.rootHeight,
                entries = window.Entries.Select(x => new { x.Style, x.Rect.x, x.Rect.y, x.Rect.width, x.Rect.height, x.Text })
            });
            return Path.Combine(EvidenceRoot, name + ".json");
        }

        public static string CreateFixture()
        {
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root + ".meta")))
            {
                throw new InvalidOperationException("Owned fixture exists; inspect, never recreate.");
            }

            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", Path.GetFileName(Root));
            AssetDatabase.CreateFolder(Root, "Content");
            foreach (var folder in new[] { "Menu", "Icons", "Extra" })
            {
                AssetDatabase.CreateFolder(Root + "/Content", folder);
                var path = Root + "/Content/" + folder + "/source.png";
                var texture = new Texture2D(8, 8);
                try
                {
                    texture.SetPixels(Enumerable.Repeat(folder == "Menu" ? Color.red : Color.blue, 64).ToArray());
                    texture.Apply();
                    File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), texture.EncodeToPNG());
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.SaveAndReimport();
            }
            var project = ResourceEditorOperations.CreateProject(Root + "/FinalUIProject.asset");
            project.projectId = "yl-workbench-final-ui-20261018";
            project.displayName = "Workbench 最终 UI 核对";
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(Root + "/Content") });
            project.groups.Add(new ResourceGroup { id = "atlases", packing = ResourcePacking.Together });
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            var config = ResourceAtlasGenerator.CreateConfiguration(project, Root + "/Content", "GeneratedAtlases");
            config.rules.Add(new ResourceAtlasRule { id = "menu", groupId = "atlases", address = "atlases/menu", sources = new UnityEngine.Object[] { AssetDatabase.LoadMainAssetAtPath(Root + "/Content/Menu/source.png") } });
            config.rules.Add(new ResourceAtlasRule
            {
                id = "icons",
                groupId = "atlases",
                address = "atlases/icons",
                grouping = ResourceAtlasGrouping.PerFolder,
                sources = new UnityEngine.Object[] { AssetDatabase.LoadMainAssetAtPath(Root + "/Content/Icons/source.png"), AssetDatabase.LoadMainAssetAtPath(Root + "/Content/Extra/source.png") }
            });
            EditorUtility.SetDirty(config);
            ResourceAtlasFields.Save(project, config);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/final-ui-owner.json"), JsonConvert.SerializeObject(new
            {
                createdUtc = DateTime.UtcNow,
                root = Root,
                evidence = EvidenceRoot,
                projectId = project.projectId,
                projectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)),
                purpose = "Owned UI editing/Undo/save only; generated output stays absent. No build/publish/deploy."
            }, Formatting.Indented));
            AssetDatabase.ImportAsset(Root + "/final-ui-owner.json");
            return AssetDatabase.GetAssetPath(project);
        }
    }
}
