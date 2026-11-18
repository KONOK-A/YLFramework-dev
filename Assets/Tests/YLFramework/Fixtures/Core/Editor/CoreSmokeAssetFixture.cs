using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Core.Tests;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Core.Editor.Tests
{
    public static class CoreSmokeAssetFixture
    {
        public static void CreateOrValidate()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Create fixtures in Edit Mode only.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Require(!SceneManager.GetSceneAt(i).isDirty, "Refusing to touch fixtures while any open scene is dirty.");
            }

            string root = YLCoreSmokeModule.FixtureRoot;
            var settings = RequireSettings();
            ResourceEditorOperations.ValidateBuild(settings);

            EnsureFolder(root);
            string viewPath = root + "/" + nameof(YLCoreSmokeView) + ".prefab";
            string panelPath = root + "/" + nameof(YLCoreSmokePanel) + ".prefab";
            string tablePath = root + "/" + YLCoreSmokeModule.Table + ".json";
            CreateOrValidatePrefab<YLCoreSmokeView>(viewPath, false);
            CreateOrValidatePrefab<YLCoreSmokePanel>(panelPath, true);
            if (!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(tablePath)))
            {
                Require(string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(tablePath)), "Unresolved existing table asset.");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(tablePath), YLCoreSmokeModule.Json);
                AssetDatabase.ImportAsset(tablePath, ImportAssetOptions.ForceSynchronousImport);
            }
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(tablePath);
            Require(text && text.text == YLCoreSmokeModule.Json, "Existing fixture JSON does not match; it was not overwritten.");
            string[] failureTables = { YLCoreSmokeModule.EmptyTable, YLCoreSmokeModule.BlankTable, YLCoreSmokeModule.InvalidTable };
            foreach (string name in failureTables)
            {
                string path = root + "/" + name + ".json";
                if (!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    Require(string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)), "Unresolved existing table asset.");
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), name == YLCoreSmokeModule.EmptyTable ? "[]" : name == YLCoreSmokeModule.BlankTable ? " " : "invalid JSON");
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                }
                Require(AssetDatabase.LoadAssetAtPath<TextAsset>(path), "Missing startup JSON fixture: " + name);
            }
            CreateOrValidateScene(YLCoreSmokeModule.ScenePath, true);
            CreateOrValidateScene(YLCoreSmokeModule.HarnessPath, false);

            Register(settings, "views", viewPath);
            Register(settings, "panels", panelPath);
            Register(settings, "text", new[] { tablePath }.Concat(failureTables.Select(name => root + "/" + name + ".json")).ToArray());
            Register(settings, "scenes", YLCoreSmokeModule.ScenePath, YLCoreSmokeModule.HarnessPath);
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        /// <summary>只返回新行为选项；内容事实始终由已准备的官方 Settings 和正式内容决定。</summary>
        public static ResourceConfiguration CreateConfiguration()
        {
            RequireSettings();
            return new ResourceConfiguration();
        }

        /// <summary>供 owned test host 的无参 prepare 调用；不接管用户主工程。</summary>
        public static void BuildContent()
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var ownerPath = Path.Combine(project, ".yl-owner.json");
            Require(File.Exists(ownerPath), "Formal fixture preparation requires an owned test host.");
            var owner = JsonUtility.FromJson<HostOwner>(File.ReadAllText(ownerPath));
            Require(owner != null && owner.schemaVersion == 1 && owner.purpose == "YLTesting.Directory" && owner.kind == "Host" &&
                string.Equals(Path.GetFullPath(owner.path), project, StringComparison.OrdinalIgnoreCase), "Invalid test host owner.");
            var directory = Path.Combine(project, "Temp", "YLCoreSmokeContent", Guid.NewGuid().ToString("N"));
            for (var cursor = new DirectoryInfo(directory); cursor != null; cursor = cursor.Parent)
            {
                Require(!cursor.Exists || (cursor.Attributes & FileAttributes.ReparsePoint) == 0, "Fixture state archives cannot traverse linked paths.");
            }
            Directory.CreateDirectory(directory);
            Debug.Log(BuildContentTo(Path.Combine(directory, "addressables_content_state.bin")).Summary);
        }

        /// <summary>实际正式构建，并拒绝覆盖本轮 Player 内容状态归档。</summary>
        public static ResourceEditorBuildReport BuildContentTo(string contentStateArchivePath)
        {
            CreateOrValidate();
            return ResourceEditorOperations.BuildContent(RequireSettings(), contentStateArchivePath);
        }

        private static AddressableAssetSettings RequireSettings()
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            Require(settings, "Prepare existing official default Addressables settings before the Core fixture; no parallel authoring configuration is created.");
            return settings;
        }

        private static void Register(AddressableAssetSettings settings, string name, params string[] paths)
        {
            var groupName = YLCoreSmokeModule.Project + "-" + name;
            var matches = settings.groups.Where(group => group && group.Name == groupName).ToArray();
            Require(matches.Length <= 1, "Ambiguous official fixture group: " + groupName);
            var existing = new System.Collections.Generic.List<AddressableAssetEntry>();
            settings.GetAllAssets(existing, false);
            foreach (var path in paths)
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                var key = YLCoreSmokeModule.Project + "/" + Path.GetFileNameWithoutExtension(path);
                Require(!string.IsNullOrEmpty(guid), "Missing real source GUID: " + path);
                Require(!existing.Any(entry => entry.guid != guid && (entry.address == key || entry.labels.Contains(key))) &&
                    !existing.Any(entry => entry.guid == guid && (entry.address != key || matches.Length == 0 || entry.parentGroup != matches[0])),
                    "Official fixture source/key is already owned by another entry: " + key);
            }
            if (matches.Length == 1)
            {
                var group = matches[0];
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                Require(!group.ReadOnly && schema && schema.IncludeInBuild && schema.IncludeAddressInCatalog && schema.IncludeGUIDInCatalog &&
                    group.entries.Count == paths.Length && group.entries.All(entry => paths.Contains(entry.AssetPath) &&
                        entry.address == YLCoreSmokeModule.Project + "/" + Path.GetFileNameWithoutExtension(entry.AssetPath)),
                    "Existing official fixture group does not match; it was not overwritten: " + groupName);
                return;
            }
            var groupPath = settings.GroupFolder + "/" + groupName + ".asset";
            Require(!File.Exists(groupPath) && !File.Exists(groupPath + ".meta") && !Directory.Exists(groupPath),
                "Unknown official group output is occupied: " + groupPath);
            foreach (var type in new[] { typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema) })
            {
                var schemaPath = settings.GroupSchemaFolder + "/" + groupName + "_" + type.Name + ".asset";
                Require(!File.Exists(schemaPath) && !File.Exists(schemaPath + ".meta"), "Unknown schema output is occupied: " + schemaPath);
            }
            var created = settings.CreateGroup(groupName, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var bundled = created.GetSchema<BundledAssetGroupSchema>();
            bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            bundled.IncludeInBuild = bundled.IncludeAddressInCatalog = bundled.IncludeGUIDInCatalog = true;
            created.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;
            foreach (var path in paths)
            {
                settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), created)
                    .SetAddress(YLCoreSmokeModule.Project + "/" + Path.GetFileNameWithoutExtension(path));
            }
            foreach (var schema in created.Schemas)
            {
                AssetDatabase.SaveAssetIfDirty(schema);
            }
            AssetDatabase.SaveAssetIfDirty(created);
        }

        [Serializable]
        private sealed class HostOwner
        {
            public int schemaVersion;
            public string purpose;
            public string kind;
            public string path;
        }

        private static void CreateOrValidatePrefab<T>(string path, bool panel) where T : Component
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!prefab)
            {
                Require(!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)),
                    "Existing prefab asset has an unexpected type: " + path);
                Scene previous = SceneManager.GetActiveScene();
                Scene temporary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                try
                {
                    SceneManager.SetActiveScene(temporary);
                    var obj = panel ? new GameObject(typeof(T).Name, typeof(RectTransform)) : new GameObject(typeof(T).Name);
                    obj.AddComponent<T>();
                    prefab = PrefabUtility.SaveAsPrefabAsset(obj, path);
                    Require(prefab, "Could not save fixture prefab " + path);
                }
                finally
                {
                    if (previous.IsValid() && previous.isLoaded)
                    {
                        SceneManager.SetActiveScene(previous);
                    }

                    EditorSceneManager.CloseScene(temporary, true);
                }
            }
            Require(prefab.name == typeof(T).Name && prefab.activeSelf && prefab.GetComponent<T>() && prefab.transform.childCount == 0,
                "Existing fixture prefab root does not match: " + path);
            Require(!prefab.GetComponent<Canvas>(), "Fixture prefab must not contain a Canvas: " + path);
            if (panel)
            {
                Require(prefab.GetComponent<RectTransform>(), "Panel root must be a RectTransform.");
            }
        }

        private static void CreateOrValidateScene(string path, bool entry)
        {
            Scene previous = SceneManager.GetActiveScene();
            Scene temporary = default;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Require(SceneManager.GetSceneAt(i).path != path, "Close the fixture scene before validating it: " + path);
            }

            try
            {
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    Require(AssetDatabase.LoadAssetAtPath<SceneAsset>(path), "Existing scene has an unexpected type.");
                    temporary = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                }
                else
                {
                    Require(string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)), "Unresolved existing scene asset.");
                    temporary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    SceneManager.SetActiveScene(temporary);
                    if (entry)
                    {
                        new GameObject(nameof(YLCoreSmokeSceneEntry)).AddComponent<YLCoreSmokeSceneEntry>();
                    }

                    Require(EditorSceneManager.SaveScene(temporary, path), "Could not save fixture scene.");
                }
                var roots = temporary.GetRootGameObjects();
                Require(entry ? roots.Length == 1 && roots[0].GetComponent<YLCoreSmokeSceneEntry>() &&
                    roots[0].activeSelf && roots[0].transform.childCount == 0 && roots[0].GetComponents<Component>().Length == 2 : roots.Length == 0,
                    "Existing fixture scene does not match; it was not overwritten: " + path);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded)
                {
                    SceneManager.SetActiveScene(previous);
                }

                if (temporary.IsValid() && temporary.isLoaded)
                {
                    EditorSceneManager.CloseScene(temporary, true);
                }
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            Require(path.StartsWith(YLCoreSmokeModule.FixtureRoot, StringComparison.Ordinal) ||
                YLCoreSmokeModule.FixtureRoot.StartsWith(path + "/", StringComparison.Ordinal), "Folder outside fixture ancestry.");
            EnsureFolder(parent);
            Require(!string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path))), "Could not create " + path);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
