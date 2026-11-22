#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace YLFramework.Testing
{
    /// <summary>One-shot development-host converter. The input inventory is never a runtime catalog.</summary>
    public static class ResourceAddressablesMigration
    {
        public static void Execute()
        {
            try
            {
                JObject request = ReadOwnedRequest();
                string previewPath = Path.Combine((string)request["output"], "preview.json");
                if (Hash(previewPath) != (string)request["previewSha256"])
                    throw new InvalidOperationException("The frozen Preview SHA changed.");
                var preview = JObject.Parse(File.ReadAllText(previewPath));
                bool verify = (string)request["action"] == "Verify";
                JObject result = Convert(preview, verify);
                result["schemaVersion"] = 1;
                result["purpose"] = "YLResource.AddressablesMigrationCandidate";
                result["hostId"] = request["hostId"].DeepClone();
                result["inputHash"] = request["inputHash"].DeepClone();
                result["previewSha256"] = request["previewSha256"].DeepClone();
                result["unityVersion"] = Application.unityVersion;
                result["addressablesVersion"] = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AddressableAssetSettings).Assembly)?.version;
                using var stream = new FileStream(Path.Combine((string)request["output"], "candidate.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(result.ToString());
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        public static JObject Convert(JObject preview, bool verifyOnly)
        {
            AssertOwnedProject();
            if ((int?)preview["schemaVersion"] != 1 || (string)preview["purpose"] != "YLResource.AddressablesMigrationPreview" ||
                (string)preview["status"] != "ReadyForCandidate" || ((JArray)preview["blockers"]).Count != 0 ||
                ((JArray)preview["entries"]).Count == 0)
                throw new InvalidOperationException("Only a nonempty, unblocked, frozen Preview can enter a candidate host.");
            for (int index = 0; index < SceneManager.sceneCount; index++)
                if (SceneManager.GetSceneAt(index).isDirty) throw new InvalidOperationException("A dirty scene prevents migration.");
            foreach (var input in ((JArray)preview["inputs"]).OfType<JObject>())
                if (Hash(LocalPath((string)input["path"])) != (string)input["sha256"])
                    throw new InvalidOperationException("A staged migration source changed during import: " + input["path"]);
            foreach (string folder in ((JArray)preview["folders"] ?? new JArray()).Values<string>())
            {
                var expected = new HashSet<string>(((JArray)preview["inputs"]).Select(input => (string)input["path"]).Where(path => path.StartsWith(folder + "/", StringComparison.Ordinal)), StringComparer.Ordinal);
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var actual = Directory.EnumerateFiles(LocalPath(folder), "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/')).ToArray();
                foreach (string path in actual) LocalPath(path);
                if (!expected.SetEquals(actual)) throw new InvalidOperationException("The staged old Folder expansion changed membership: " + folder);
            }
            foreach (var project in ((JArray)preview["projects"]).OfType<JObject>().Where(_ => !verifyOnly))
            {
                Object value = AssetDatabase.LoadMainAssetAtPath((string)project["path"]);
                AssertIdentity(value, project);
                if (value.GetType().AssemblyQualifiedName != (string)project["type"])
                    throw new InvalidOperationException("The preserved Legacy DTO is missing or changed type: " + project["path"]);
            }

            string settingsPath = (string)preview["settingsPath"];
            string physicalSettings = LocalPath(settingsPath);
            var settings = AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>(settingsPath);
            if (!verifyOnly && (bool)preview["settingsExpectedAbsent"] != !File.Exists(physicalSettings))
                throw new InvalidOperationException("The official Settings target no longer has the frozen input state.");
            if (File.Exists(physicalSettings) && !settings) throw new InvalidOperationException("The Settings target is not an official Settings asset.");
            string configurationDirectory = Path.GetDirectoryName(physicalSettings);
            if (!settings && !verifyOnly && Directory.Exists(configurationDirectory) && Directory.EnumerateFileSystemEntries(configurationDirectory).Any())
                throw new InvalidOperationException("New Settings require an empty configuration namespace; existing unknown or external configuration is preserved.");
            if (settings && settings.groups.Any(group => !group || group.Schemas.Any(schema => !schema)))
                throw new InvalidOperationException("Existing official Groups/Schemas have missing dependencies; expand the explicit candidate input closure.");
            JObject originalSettings = settings ? SerializedFields(settings) : null;
            string[] originalLabels = settings ? settings.GetLabels().ToArray() : Array.Empty<string>();
            var existing = new List<AddressableAssetEntry>();
            if (settings) settings.GetAllAssets(existing, false);
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            var sourceGuids = new HashSet<string>(StringComparer.Ordinal);
            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            var compressions = new Dictionary<string, BundledAssetGroupSchema.BundleCompressionMode>(StringComparer.Ordinal);
            var entries = ((JArray)preview["entries"]).OfType<JObject>().ToArray();
            foreach (var item in entries)
            {
                string path = (string)item["path"];
                LocalPath(path);
                Object asset = AssetDatabase.LoadMainAssetAtPath(path);
                AssertIdentity(asset, item);
                if (asset.GetType().AssemblyQualifiedName != (string)item["type"])
                    throw new InvalidOperationException("The exported main-asset type changed: " + path);
                if (AssetDatabase.AssetPathToGUID((string)item["selectionPath"]) != (string)item["selectionGuid"])
                    throw new InvalidOperationException("The original Folder/selection GUID changed: " + path);
                if (!sourceGuids.Add((string)item["guid"]) || keys.ContainsKey((string)item["address"]))
                    throw new InvalidOperationException("Duplicate source identity or canonical address.");
                keys.Add((string)item["address"], (string)item["guid"]);
                foreach (var subAsset in ((JArray)item["subAssets"]).OfType<JObject>())
                {
                    var matches = AssetDatabase.LoadAllAssetsAtPath(path).Where(value => MatchesIdentity(value, subAsset)).ToArray();
                    if (matches.Length != 1 || matches[0].name != (string)subAsset["name"] || matches[0].GetType().AssemblyQualifiedName != (string)subAsset["type"])
                        throw new InvalidOperationException("A subasset GUID/localId/name/type changed: " + path);
                }
                string groupName = (string)item["group"];
                string packing = (string)item["packing"];
                if (packing != "Together" && packing != "PerAsset") throw new InvalidOperationException("Unsupported packing intent.");
                if (groups.TryGetValue(groupName, out string previous) && previous != packing)
                    throw new InvalidOperationException("Conflicting official group packing.");
                groups[groupName] = packing;
                var compression = Compression((string)item["compression"]);
                if (compressions.TryGetValue(groupName, out var previousCompression) && previousCompression != compression)
                    throw new InvalidOperationException("Conflicting official group compression.");
                compressions[groupName] = compression;
                foreach (var other in existing)
                {
                    if ((other.address == (string)item["address"] || other.guid == (string)item["address"] || other.labels.Contains((string)item["address"])) && other.guid != (string)item["guid"])
                        throw new InvalidOperationException("An external Addressables address/GUID/label conflicts with the canonical key: " + item["address"]);
                    if (other.guid == (string)item["guid"] && (other.address != (string)item["address"] || other.parentGroup.Name != groupName))
                        throw new InvalidOperationException("A source GUID is already owned by a different official entry: " + path);
                }
                var implicitEntry = settings ? settings.FindAssetEntry((string)item["guid"], true) : null;
                if (implicitEntry != null && !existing.Contains(implicitEntry))
                    throw new InvalidOperationException("An external Folder entry already owns a migrating source: " + path);
                ValidateMissingScripts(asset, path);
            }
            foreach (var item in entries)
                if (keys.ContainsKey((string)item["label"]) || existing.Any(entry => entry.address == (string)item["label"] || entry.guid == (string)item["label"]))
                    throw new InvalidOperationException("A collection Label collides with a single-asset key.");
            foreach (var pair in groups)
            {
                var group = settings ? settings.FindGroup(pair.Key) : null;
                if (!group)
                {
                    if (settings)
                        foreach (string target in new[]
                        {
                            settings.GroupFolder + "/" + pair.Key + ".asset",
                            settings.GroupSchemaFolder + "/" + pair.Key + "_BundledAssetGroupSchema.asset",
                            settings.GroupSchemaFolder + "/" + pair.Key + "_ContentUpdateGroupSchema.asset"
                        })
                        {
                            // Unity's own settings folder properties use platform separators on Windows;
                            // canonicalize those SDK-produced paths, not untrusted Preview input paths.
                            string physical = LocalPath(target.Replace('\\', '/'));
                            if (File.Exists(physical) || Directory.Exists(physical) || File.Exists(physical + ".meta"))
                                throw new InvalidOperationException("An unknown official Group/Schema output already exists: " + target);
                        }
                    continue;
                }
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (!schema || schema.BundleMode != Packing(pair.Value) || schema.Compression != compressions[pair.Key] ||
                    group.entries.Any(entry => !sourceGuids.Contains(entry.guid)))
                    throw new InvalidOperationException("A foreign or incompatible official Group would be modified: " + pair.Key);
            }

            if ((bool?)preview["useAsDefault"] == true && AddressableAssetSettingsDefaultObject.Settings && AddressableAssetSettingsDefaultObject.Settings != settings)
                throw new InvalidOperationException("A different external default Settings must not be replaced.");
            // Every consumer is checked before the first write. Missing old fields never become defaults.
            ProcessConsumers(preview, verifyOnly, false);
            if (verifyOnly)
            {
                VerifySettings(settings, entries);
                return new JObject { ["status"] = "ConfigurationVerified", ["runtimeVerified"] = false, ["previewId"] = preview["previewId"].DeepClone() };
            }

            if (!settings)
            {
                string folder = Path.GetDirectoryName(settingsPath).Replace('\\', '/');
                EnsureFolder(folder);
                settings = AddressableAssetSettings.Create(folder, Path.GetFileNameWithoutExtension(settingsPath), true, true);
                if (!settings) throw new InvalidOperationException("Official Settings creation failed.");
            }
            settings.DisableCatalogUpdateOnStartup = true;
            foreach (var pair in groups)
            {
                var group = settings.FindGroup(pair.Key);
                if (!group)
                    group = settings.CreateGroup(pair.Key, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                if (!group || group.Name != pair.Key) throw new InvalidOperationException("Official group name was normalized or collided.");
                group.GetSchema<BundledAssetGroupSchema>().BundleMode = Packing(pair.Value);
                group.GetSchema<BundledAssetGroupSchema>().Compression = compressions[pair.Key];
                foreach (var item in entries.Where(value => (string)value["group"] == pair.Key))
                {
                    var entry = settings.FindAssetEntry((string)item["guid"]) ?? settings.CreateOrMoveEntry((string)item["guid"], group, false, false);
                    if (entry == null) throw new InvalidOperationException("Official Entry creation failed.");
                    entry.SetAddress((string)item["address"], false);
                    entry.SetLabel((string)item["label"], true, true, false);
                }
                EditorUtility.SetDirty(group);
                foreach (var schema in group.Schemas) { EditorUtility.SetDirty(schema); AssetDatabase.SaveAssetIfDirty(schema); }
                AssetDatabase.SaveAssetIfDirty(group);
            }
            if ((bool?)preview["useAsDefault"] == true)
            {
                var current = AddressableAssetSettingsDefaultObject.GetSettings(false);
                if (current && current != settings) throw new InvalidOperationException("A different external default Settings must not be replaced.");
                if (!current)
                {
                    EnsureFolder("Assets/AddressableAssetsData");
                    AddressableAssetSettingsDefaultObject.Settings = settings;
                }
            }
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            if (originalSettings != null)
            {
                JObject currentSettings = SerializedFields(settings);
                foreach (var field in originalSettings.Properties())
                {
                    // Addressables recomputes its serialized hash from the groups and labels checked by this migration.
                    if (field.Name == "m_GroupAssets" || field.Name == "m_LabelTable" || field.Name == "m_DisableCatalogUpdateOnStart" || field.Name == "m_currentHash") continue;
                    if (!JToken.DeepEquals(field.Value, currentSettings[field.Name]))
                        throw new InvalidOperationException("An unrelated official Settings field changed: " + field.Name);
                }
                if (originalLabels.Any(label => !settings.GetLabels().Contains(label)))
                    throw new InvalidOperationException("An existing external Label was removed.");
            }
            ProcessConsumers(preview, false, true);
            VerifySettings(settings, entries);
            return new JObject
            {
                ["status"] = "CandidatePrepared", ["runtimeVerified"] = false,
                ["previewId"] = preview["previewId"].DeepClone(), ["entryCount"] = entries.Length,
                ["note"] = "Official configuration and serialized identities were checked. This is not a runtime content-load proof or a source Apply."
            };
        }

        private static void VerifySettings(AddressableAssetSettings settings, JObject[] entries)
        {
            if (!settings || !settings.DisableCatalogUpdateOnStartup) throw new InvalidOperationException("Missing Settings or unsafe automatic Catalog policy.");
            foreach (var item in entries)
            {
                var entry = settings.FindAssetEntry((string)item["guid"]);
                if (entry == null || entry.address != (string)item["address"] || entry.parentGroup.Name != (string)item["group"] ||
                    !entry.labels.Contains((string)item["label"]) || entry.parentGroup.GetSchema<BundledAssetGroupSchema>().BundleMode != Packing((string)item["packing"]) ||
                    entry.parentGroup.GetSchema<BundledAssetGroupSchema>().Compression != Compression((string)item["compression"]))
                    throw new InvalidOperationException("Official Entry/Label/Group validation failed: " + item["path"]);
            }
        }

        private static void ProcessConsumers(JObject preview, bool verifyOutput, bool write)
        {
            var rewrites = ((JArray)preview["rewrites"]).OfType<JObject>().ToArray();
            foreach (var file in ((JArray)preview["consumers"]).OfType<JObject>().GroupBy(value => (string)value["assetPath"]))
            {
                string path = file.Key;
                LocalPath(path);
                Scene openedScene = default;
                Object[] objects;
                if (path.EndsWith(".unity", StringComparison.Ordinal))
                {
                    openedScene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    objects = openedScene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Component>(true)).Cast<Object>().ToArray();
                }
                else if (path.EndsWith(".prefab", StringComparison.Ordinal))
                    objects = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<Component>(true).Cast<Object>().ToArray();
                else objects = AssetDatabase.LoadAllAssetsAtPath(path);
                try
                {
                    if (objects.Any(value => !value)) throw new InvalidOperationException("Missing Script in a consumer asset: " + path);
                    foreach (var expected in file)
                    {
                        var targets = objects.Where(value => MatchesIdentity(value, expected)).ToArray();
                        if (targets.Length != 1 || targets[0].GetType().AssemblyQualifiedName != (string)expected["type"])
                            throw new InvalidOperationException("Consumer GUID/localId/type is missing or ambiguous: " + path);
                        Object target = targets[0];
                        if (EditorUtility.IsDirty(target)) throw new InvalidOperationException("A dirty consumer asset cannot be rewritten: " + path);
                        var fields = rewrites.Where(value => (string)value["guid"] == (string)expected["guid"] && (long)value["localId"] == (long)expected["localId"]).ToArray();
                        using var serialized = new SerializedObject(target);
                        foreach (var pair in (JObject)expected["properties"])
                        {
                            if (pair.Key.EndsWith(".m_FileID", StringComparison.Ordinal) || pair.Key.EndsWith(".m_PathID", StringComparison.Ordinal)) continue;
                            using var property = serialized.FindProperty(pair.Key);
                            if (property == null || property.propertyType.ToString() != (string)pair.Value["propertyType"] || property.type != (string)pair.Value["type"])
                                throw new InvalidOperationException("An exported consumer property/type disappeared: " + path + "/" + pair.Key);
                            var rewrite = fields.SingleOrDefault(value => (string)value["propertyPath"] == pair.Key);
                            JToken expectedValue = verifyOutput && rewrite != null ? rewrite["newValue"] : pair.Value["value"];
                            AssertProperty(property, pair.Value, expectedValue, path);
                        }
                        if (!write || fields.Length == 0) continue;
                        JObject before = JObject.Parse(EditorJsonUtility.ToJson(target));
                        foreach (var field in fields)
                        {
                            using var property = serialized.FindProperty((string)field["propertyPath"]);
                            if (property == null || property.propertyType != SerializedPropertyType.String || property.stringValue != (string)field["oldValue"])
                                throw new InvalidOperationException("A consumer field changed before its rewrite: " + path);
                            property.stringValue = (string)field["newValue"];
                        }
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        JObject after = JObject.Parse(EditorJsonUtility.ToJson(target));
                        foreach (var field in fields)
                        {
                            string selector = ((string)field["propertyPath"]).Replace(".Array.data[", "[");
                            JToken oldToken = before.SelectToken(selector) ?? before["MonoBehaviour"]?.SelectToken(selector);
                            if (oldToken == null) throw new InvalidOperationException("Unknown serialized JSON field layout: " + selector);
                            oldToken.Replace(field["newValue"].DeepClone());
                        }
                        if (!JToken.DeepEquals(before, after)) throw new InvalidOperationException("An unselected consumer field changed: " + path);
                        EditorUtility.SetDirty(target);
                        if (!openedScene.IsValid() && !path.EndsWith(".prefab", StringComparison.Ordinal)) AssetDatabase.SaveAssetIfDirty(target);
                    }
                    bool hasWrites = rewrites.Any(value => (string)value["path"] == path);
                    if (write && hasWrites && openedScene.IsValid())
                    {
                        if (!EditorSceneManager.SaveScene(openedScene, path)) throw new InvalidOperationException("Candidate scene save failed: " + path);
                    }
                    else if (write && hasWrites && path.EndsWith(".prefab", StringComparison.Ordinal))
                    {
                        PrefabUtility.SavePrefabAsset(AssetDatabase.LoadAssetAtPath<GameObject>(path), out bool saved);
                        if (!saved) throw new InvalidOperationException("Candidate prefab save failed: " + path);
                    }
                }
                finally { if (openedScene.IsValid()) EditorSceneManager.CloseScene(openedScene, true); }
            }
        }

        private static void AssertProperty(SerializedProperty property, JToken exported, JToken value, string path)
        {
            bool matches;
            switch (property.propertyType)
            {
                case SerializedPropertyType.Generic:
                    matches = !(bool?)exported["isArray"] ?? true;
                    if ((bool?)exported["isArray"] == true) matches = property.isArray && property.arraySize == (int)exported["arraySize"];
                    break;
                case SerializedPropertyType.String: matches = property.stringValue == (string)value; break;
                case SerializedPropertyType.Integer: matches = property.longValue == (long)value; break;
                case SerializedPropertyType.Enum: matches = property.intValue == (int)value; break;
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.LayerMask: matches = property.intValue == (int)value; break;
                case SerializedPropertyType.Boolean: matches = property.boolValue == (bool)value; break;
                case SerializedPropertyType.Float: matches = property.type == "float" ? property.floatValue.Equals((float)value) : property.doubleValue.Equals((double)value); break;
                case SerializedPropertyType.ObjectReference:
                case SerializedPropertyType.ExposedReference:
                    Object reference = property.propertyType == SerializedPropertyType.ObjectReference ? property.objectReferenceValue : property.exposedReferenceValue;
                    matches = (bool?)value["isNull"] == true ? !reference : MatchesIdentity(reference, (JObject)value);
                    break;
                default:
                    // Composite numeric properties are validated through their exported scalar children.
                    matches = property.hasVisibleChildren && property.propertyType != SerializedPropertyType.ManagedReference;
                    if (!matches) throw new InvalidOperationException("Unsupported consumer property cannot be proven unchanged: " + path + "/" + property.propertyPath);
                    break;
            }
            if (!matches) throw new InvalidOperationException("An exported consumer value changed: " + path + "/" + property.propertyPath);
        }

        private static bool MatchesIdentity(Object value, JObject expected)
        {
            if (!value) return false;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id))
                return guid == (string)expected["guid"] && id == (long)expected["localId"] &&
                    (expected["globalId"] == null || GlobalObjectId.GetGlobalObjectIdSlow(value).ToString() == (string)expected["globalId"]);
            var global = GlobalObjectId.GetGlobalObjectIdSlow(value);
            return global.assetGUID.ToString() == (string)expected["guid"] && global.targetObjectId == (ulong)expected["localId"] &&
                global.targetPrefabId == ((ulong?)expected["targetPrefabId"] ?? 0);
        }

        private static void AssertIdentity(Object value, JObject expected)
        {
            if (!MatchesIdentity(value, expected)) throw new InvalidOperationException("Exported GUID/localId is not the imported source identity: " + expected["path"]);
        }

        private static void ValidateMissingScripts(Object asset, string path)
        {
            if (asset is GameObject root && root.GetComponentsInChildren<Component>(true).Any(component => !component))
                throw new InvalidOperationException("Missing Script in a migrating prefab: " + path);
            if (!path.EndsWith(".unity", StringComparison.Ordinal)) return;
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                if (scene.isDirty || scene.GetRootGameObjects().SelectMany(value => value.GetComponentsInChildren<Component>(true)).Any(component => !component))
                    throw new InvalidOperationException("Dirty scene or Missing Script in a migrating scene: " + path);
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        private static BundledAssetGroupSchema.BundlePackingMode Packing(string value) => value == "Together"
            ? BundledAssetGroupSchema.BundlePackingMode.PackTogether : BundledAssetGroupSchema.BundlePackingMode.PackSeparately;

        private static BundledAssetGroupSchema.BundleCompressionMode Compression(string value) => value switch
        {
            "Lz4" => BundledAssetGroupSchema.BundleCompressionMode.LZ4,
            "Lzma" => BundledAssetGroupSchema.BundleCompressionMode.LZMA,
            "Uncompressed" => BundledAssetGroupSchema.BundleCompressionMode.Uncompressed,
            _ => throw new InvalidOperationException("Unsupported exported compression intent.")
        };

        private static JObject SerializedFields(Object value)
        {
            JObject result = JObject.Parse(EditorJsonUtility.ToJson(value));
            return (JObject)(result["MonoBehaviour"] ?? result);
        }

        private static void EnsureFolder(string path)
        {
            LocalPath(path);
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path)))) throw new InvalidOperationException("Candidate folder creation failed: " + path);
        }

        private static string LocalPath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') ||
                relative.Split('/').Any(segment => segment == ".." || segment == "." || segment.Length == 0 || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)))
                throw new InvalidOperationException("A canonical project-relative path is required.");
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string result = Path.GetFullPath(Path.Combine(root, relative));
            if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Candidate path escapes the host.");
            for (string current = result; current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked candidate paths are refused.");
            return result;
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static void AssertOwnedProject()
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var owner = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-owner.json")));
            string workspace = Directory.GetParent(project)?.Parent?.FullName;
            var workspaceOwner = JObject.Parse(File.ReadAllText(Path.Combine(workspace ?? "", ".yl-workspace.json")));
            if (!Application.isBatchMode || (string)owner["purpose"] != "YLTesting.Directory" || (string)owner["kind"] != "Host" ||
                (string)owner["path"] != project || (string)workspaceOwner["purpose"] != "YLTesting.Workspace" ||
                (string)owner["workspaceId"] != (string)workspaceOwner["id"] || (string)workspaceOwner["repository"] == project)
                throw new InvalidOperationException("Migration writes require a separate tool-owned batch host, never the source Editor.");
        }

        private static JObject ReadOwnedRequest()
        {
            AssertOwnedProject();
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-ylResourceMigrationRequest");
            if (index < 0 || index + 1 >= arguments.Length) throw new InvalidOperationException("An explicit migration request is required.");
            string path = Path.GetFullPath(arguments[index + 1]);
            var request = JObject.Parse(File.ReadAllText(path));
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var owner = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-owner.json")));
            var inputs = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-inputs.json")));
            string output = Path.GetFullPath((string)request["output"] ?? "");
            var resultOwner = JObject.Parse(File.ReadAllText(Path.Combine(output, ".yl-owner.json")));
            string workspace = Directory.GetParent(project)?.Parent?.FullName;
            if ((int?)request["schemaVersion"] != 1 || (string)request["purpose"] != "YLResource.AddressablesMigration" ||
                ((string)request["action"] != "Preview" && (string)request["action"] != "Verify") ||
                (string)request["expectedUnityVersion"] != Application.unityVersion ||
                (string)request["expectedAddressablesVersion"] != UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AddressableAssetSettings).Assembly)?.version ||
                (string)request["hostId"] != (string)owner["id"] || (string)request["inputHash"] != (string)inputs["inputHash"] ||
                (string)inputs["hostId"] != (string)owner["id"] || (string)resultOwner["kind"] != "Result" ||
                (string)resultOwner["id"] != (string)request["resultId"] || (string)resultOwner["workspaceId"] != (string)owner["workspaceId"] ||
                !output.StartsWith(Path.Combine(workspace ?? "", "Results") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                path != Path.Combine(output, "request.json")) throw new InvalidOperationException("Candidate request ownership/input identity mismatch.");
            return request;
        }
    }
}
#endif
