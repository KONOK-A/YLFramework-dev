using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Tests.EquipmentAcceptance
{
    // 独立 Equipment 作者构建的设置保护；只恢复本轮明确拥有的修改。
    public static class EquipmentSettings
    {
        private const string Key = "YLFramework.Equipment.Acceptance.Settings";
        private static readonly string[] _fields = { "scriptingBackend", "managedStrippingLevel", "scriptingDefineSymbols" };
        [Serializable]
        private sealed class Map
        {
            public string field;
            public string[] keys;
            public string[] texts;
            public int[] numbers;
        }
        [Serializable]
        private sealed class Snapshot
        {
            public string output;
            public string playerJson;
            public bool playerDirty;
            public bool reloadEnabled;
            public int reload;
            public Map[] maps;
            public Map[] expected;
            public SceneSetup[] scenes;
            public int[] selection;
        }
        public static bool Active => SessionState.GetString(Key, "").Length != 0;
        private static UnityEngine.Object Player => (UnityEngine.Object)typeof(PlayerSettings).GetMethod("InternalGetPlayerSettingsObject", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        private static void Require(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }
        private static Map[] ReadMaps(SerializedObject serialized) => _fields.Select(field =>
        {
            var property = serialized.FindProperty(field);
            var items = Enumerable.Range(0, property.arraySize).Select(property.GetArrayElementAtIndex).ToArray();
            return new Map
            {
                field = field,
                keys = items.Select(p => p.FindPropertyRelative("first").stringValue).ToArray(),
                texts = field == "scriptingDefineSymbols" ? items.Select(p => p.FindPropertyRelative("second").stringValue).ToArray() : null,
                numbers = field == "scriptingDefineSymbols" ? null : items.Select(p => p.FindPropertyRelative("second").intValue).ToArray()
            };
        }).ToArray();

        public static void Capture(string output)
        {
            Require(!Active && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !BuildPipeline.isBuildingPlayer, "Settings gate is not idle.");
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                Require(!SceneManager.GetSceneAt(i).isDirty, "Do not discard a dirty scene.");
            }

            var full = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(output));
            var parent = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Temp/EquipmentAcceptance")) + Path.DirectorySeparatorChar;
            Require(full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(full)), "Expected a new Equipment-owned output directory.");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(full));
            var player = Player;
            using var serialized = new SerializedObject(player);
            var maps = ReadMaps(serialized);
            var snapshot = new Snapshot
            {
                output = full,
                playerJson = EditorJsonUtility.ToJson(player),
                playerDirty = EditorUtility.IsDirty(player),
                maps = maps,
                expected = maps,
                scenes = EditorSceneManager.GetSceneManagerSetup(),
                selection = Selection.instanceIDs,
                reloadEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                reload = (int)EditorSettings.enterPlayModeOptions
            };
            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/ProjectSettings.asset"), global::YLFramework.Testing.PackageTestPaths.Physical(full + "/ProjectSettings.before.asset"));
            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/EditorSettings.asset"), global::YLFramework.Testing.PackageTestPaths.Physical(full + "/EditorSettings.before.asset"));
            var json = JsonConvert.SerializeObject(snapshot);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(full + "/settings-before.json"), json);
            SessionState.SetString(Key, json);
            SessionState.SetString(Key + ".Paths", "[]");
        }

        public static void OwnPath(string path)
        {
            if (!Active)
            {
                return;
            }

            Require(path.StartsWith("Assets/EquipmentExcelFixture-", StringComparison.Ordinal) || path.StartsWith("Assets/DomainExcelFixture-", StringComparison.Ordinal) ||
                path.StartsWith("Assets/YLFrameworkData/Equipment/", StringComparison.Ordinal) || path.StartsWith("Assets/Tests/YLFramework/Fixtures/Extensions/Equipment", StringComparison.Ordinal), "Not an Equipment-owned fixture path.");
            var paths = JsonConvert.DeserializeObject<List<string>>(SessionState.GetString(Key + ".Paths", "[]"));
            if (!paths.Contains(path))
            {
                paths.Add(path);
            }

            SessionState.SetString(Key + ".Paths", JsonConvert.SerializeObject(paths));
        }
        public static string[] FilterSaves(string[] paths)
        {
            if (!Active)
            {
                return paths;
            }

            var owned = JsonConvert.DeserializeObject<string[]>(SessionState.GetString(Key + ".Paths", "[]"));
            return paths.Where(path => owned.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal))).ToArray();
        }
        private static Snapshot BeforeChange()
        {
            var snapshot = JsonConvert.DeserializeObject<Snapshot>(SessionState.GetString(Key, ""));
            Require(snapshot != null, "Missing Equipment settings gate.");
            VerifyOtherSettings(snapshot);
            using var serialized = new SerializedObject(Player);
            Require(SameExpected(snapshot.expected, ReadMaps(serialized)), "A platform map changed outside this gate.");
            return snapshot;
        }
        private static Map WithoutStandalone(Map map)
        {
            var keep = Enumerable.Range(0, map.keys.Length).Where(i => map.keys[i] != "Standalone").ToArray();
            return new Map
            {
                field = map.field,
                keys = keep.Select(i => map.keys[i]).ToArray(),
                texts = map.texts == null ? null : keep.Select(i => map.texts[i]).ToArray(),
                numbers = map.numbers == null ? null : keep.Select(i => map.numbers[i]).ToArray()
            };
        }
        private static void Expected(Snapshot snapshot, params (string Field, string Value)[] owned)
        {
            VerifyOtherSettings(snapshot);
            using var serialized = new SerializedObject(Player);
            var actual = ReadMaps(serialized);
            var fields = owned.Select(item => item.Field).ToArray();
            Require(SameExpected(snapshot.expected.Select(m => fields.Contains(m.field) ? WithoutStandalone(m) : m).ToArray(),
                actual.Select(m => fields.Contains(m.field) ? WithoutStandalone(m) : m).ToArray()), "Unowned platform entries changed during the setter.");
            foreach (var item in owned)
            {
                var map = actual.Single(m => m.field == item.Field);
                var index = Array.IndexOf(map.keys, "Standalone");
                Require(index >= 0, "Missing owned Standalone setting.");
                Require(map.texts == null ? map.numbers[index].ToString() == item.Value :
                    map.texts[index].Split(';').OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(item.Value.Split(';').OrderBy(s => s, StringComparer.Ordinal)), "Unexpected value written to Standalone.");
            }
            snapshot.expected = actual;
            SessionState.SetString(Key, JsonConvert.SerializeObject(snapshot));
        }
        private static void VerifyOtherSettings(Snapshot snapshot)
        {
            var before = JObject.Parse(snapshot.playerJson);
            var observed = JObject.Parse(EditorJsonUtility.ToJson(Player));
            foreach (var field in _fields)
            {
                ((JObject)before["PlayerSettings"]).Remove(field);
                ((JObject)observed["PlayerSettings"]).Remove(field);
            }
            Require(JToken.DeepEquals(before, observed), "Unowned PlayerSettings memory changed; preserve it.");
            Require(EditorSettings.enterPlayModeOptionsEnabled == snapshot.reloadEnabled && (int)EditorSettings.enterPlayModeOptions == snapshot.reload, "Unowned Enter Play settings changed; preserve them.");
        }
        private static bool SameExpected(Map[] expected, Map[] actual)
        {
            if (expected.Length != actual.Length)
            {
                return false;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                var a = expected[i];
                var b = actual[i];
                if (a.field != b.field || !a.keys.SequenceEqual(b.keys))
                {
                    return false;
                }

                if (a.numbers != null)
                {
                    if (!a.numbers.SequenceEqual(b.numbers))
                    {
                        return false;
                    }
                    continue;
                }
                for (var n = 0; n < a.keys.Length; n++)
                {
                    if (a.keys[n] == "Standalone")
                    {
                        // MCP 重载会重排本次宏操作涉及的 Standalone 符号；只允许同一集合。
                        if (!a.texts[n].Split(';').OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(b.texts[n].Split(';').OrderBy(s => s, StringComparer.Ordinal)))
                        {
                            return false;
                        }
                    }
                    else if (a.texts[n] != b.texts[n])
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        public static void EnableExcel() => EnableSymbol("YL_DOMAIN_EXCEL");
        public static void EnableAcceptance() => EnableSymbol("YL_EQUIPMENT_ACCEPTANCE");
        private static void EnableSymbol(string symbol)
        {
            var snapshot = BeforeChange();
            var symbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone).Split(';').Where(s => s.Length != 0).ToList();
            if (!symbols.Contains(symbol))
            {
                symbols.Add(symbol);
            }

            PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone, string.Join(";", symbols));
            Expected(snapshot, ("scriptingDefineSymbols", string.Join(";", symbols)));
        }
        public static void SetBackend(bool il2cpp)
        {
            var snapshot = BeforeChange();
            Require(PlayerSettings.GetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone) == Il2CppCompilerConfiguration.Release, "Do not change an unowned IL2CPP compiler configuration.");
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, il2cpp ? ManagedStrippingLevel.Low : ManagedStrippingLevel.Disabled);
            Expected(snapshot, ("scriptingBackend", ((int)(il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x)).ToString()),
                ("managedStrippingLevel", ((int)(il2cpp ? ManagedStrippingLevel.Low : ManagedStrippingLevel.Disabled)).ToString()));
        }
        public static void Restore()
        {
            var snapshot = JsonConvert.DeserializeObject<Snapshot>(SessionState.GetString(Key, ""));
            Require(snapshot != null && !EditorApplication.isPlaying && !BuildPipeline.isBuildingPlayer, "No idle settings gate to restore.");
            var player = Player;
            VerifyOtherSettings(snapshot);
            using (var serialized = new SerializedObject(player))
            {
                Require(SameExpected(snapshot.expected, ReadMaps(serialized)), "A platform map changed outside this gate.");
            }

            var currentScenes = EditorSceneManager.GetSceneManagerSetup();
            if (JsonConvert.SerializeObject(snapshot.scenes) != JsonConvert.SerializeObject(currentScenes))
            {
                var owned = JsonConvert.DeserializeObject<string[]>(SessionState.GetString(Key + ".Paths", "[]"));
                Require(currentScenes.All(scene => snapshot.scenes.Any(original => original.path == scene.path) || owned.Any(path => scene.path == path || scene.path.StartsWith(path + "/", StringComparison.Ordinal))), "An unowned scene was opened; preserve it.");
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    Require(!SceneManager.GetSceneAt(i).isDirty, "Do not overwrite an unowned dirty scene.");
                }
            }
            RestoreBytes(snapshot.output, "ProjectSettings", _fields, Array.Empty<string>(), true);
            RestoreBytes(snapshot.output, "EditorSettings", Array.Empty<string>(), new[] { "m_EnterPlayModeOptionsEnabled", "m_EnterPlayModeOptions" }, true);
            using (var serialized = new SerializedObject(player))
            {
                foreach (var map in snapshot.maps)
                {
                    var property = serialized.FindProperty(map.field);
                    property.arraySize = map.keys.Length;
                    for (var i = 0; i < map.keys.Length; i++)
                    {
                        var item = property.GetArrayElementAtIndex(i);
                        item.FindPropertyRelative("first").stringValue = map.keys[i];
                        if (map.texts != null)
                        {
                            item.FindPropertyRelative("second").stringValue = map.texts[i];
                        }
                        else
                        {
                            item.FindPropertyRelative("second").intValue = map.numbers[i];
                        }
                    }
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)snapshot.reload;
            EditorSettings.enterPlayModeOptionsEnabled = snapshot.reloadEnabled;
            Require(JToken.DeepEquals(JObject.Parse(snapshot.playerJson), JObject.Parse(EditorJsonUtility.ToJson(player))), "Player memory did not restore.");
            RestoreBytes(snapshot.output, "ProjectSettings", _fields, Array.Empty<string>(), false);
            RestoreBytes(snapshot.output, "EditorSettings", Array.Empty<string>(), new[] { "m_EnterPlayModeOptionsEnabled", "m_EnterPlayModeOptions" }, false);
            if (snapshot.playerDirty)
            {
                EditorUtility.SetDirty(player);
            }
            else
            {
                EditorUtility.ClearDirty(player);
            }

            if (JsonConvert.SerializeObject(snapshot.scenes) != JsonConvert.SerializeObject(EditorSceneManager.GetSceneManagerSetup()))
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    Require(!SceneManager.GetSceneAt(i).isDirty, "Do not overwrite an unowned dirty scene.");
                }

                EditorSceneManager.RestoreSceneManagerSetup(snapshot.scenes);
            }
            Selection.instanceIDs = snapshot.selection;
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(snapshot.output + "/settings-restored.json"), JsonConvert.SerializeObject(new
            {
                restored = true,
                playerMemory = true,
                sparseMaps = true,
                utc = DateTime.UtcNow
            }));
            SessionState.EraseString(Key);
            SessionState.EraseString(Key + ".Paths");
        }
        private static void RestoreBytes(string output, string name, string[] maps, string[] scalars, bool validateOnly)
        {
            var path = "ProjectSettings/" + name + ".asset";
            var backup = output + "/" + name + ".before.asset";
            var originalBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(backup));
            if (originalBytes.SequenceEqual(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path))))
            {
                return;
            }

            var original = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(backup));
            var current = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var normalized = current;
            foreach (var field in scalars)
            {
                var pattern = "(?m)^  " + field + ":.*$";
                var value = Regex.Match(original, pattern).Value;
                Require(value.Length != 0 && Regex.Matches(normalized, pattern).Count == 1, "Unexpected scalar layout.");
                normalized = Regex.Replace(normalized, pattern, _ => value);
            }
            foreach (var field in maps)
            {
                var pattern = "(?ms)^  " + field + ":.*?(?=^  [^ ]|\\z)";
                var before = Regex.Match(original, pattern).Value;
                var after = Regex.Match(normalized, pattern).Value;
                const string Standalone = "(?m)^    Standalone: [^\\r\\n]*\\r?\\n";
                string Others(string block)
                {
                    var value = Regex.Replace(block, Standalone, "");
                    return Regex.IsMatch(value, "\\A  " + field + ":(?: \\{\\})?\\r?\\n?\\z") ? "  " + field + ":\n" : value;
                }
                Require(before.Length != 0 && Others(before) == Others(after), "Unowned disk platform settings changed.");
                normalized = Regex.Replace(normalized, pattern, _ => before);
            }
            Require(normalized == original && File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)) == current, "Unattributed settings bytes changed; do not overwrite.");
            if (!validateOnly)
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(output + "/" + name + ".owned-side-effect.asset"), File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), originalBytes);
            }
        }
    }
    internal sealed class EquipmentSaveFence : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths) => EquipmentSettings.FilterSaves(paths);
    }
}
