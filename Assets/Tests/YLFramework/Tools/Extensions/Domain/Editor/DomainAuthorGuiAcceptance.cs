using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Skill;
using YLFramework.Editor.Skill;

namespace YLFramework.Tests.DomainMain
{
    public static class DomainAuthorGuiAcceptance
    {
        private const string Key = "YLFramework.Domain.AuthorGui";
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly string[] _preferences = { "YLFramework.SkillSystem.SELECT_GUID_KEY", "YLFramework.Buff.SelectedConfig", "YLFramework.MissionSystem.SELECT_GUID_KEY" };
        [Serializable]
        private sealed class Preference
        {
            public string key;
            public string value;
            public bool exists;
        }
        [Serializable]
        private sealed class Root
        {
            public string module;
            public string path;
            public string windowType;
            public string configField;
            public string treeField;
            public int window;
        }

        private static object Main(string name, string output) => typeof(DomainMainAcceptance).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { output });

        public static void Begin()
        {
            DomainMainAcceptance.Identity();
            if (!string.IsNullOrEmpty(SessionState.GetString(Key, "")))
            {
                throw new InvalidOperationException("An author GUI Run is already active.");
            }

            string output = (string)Main("Begin", "author-gui-01");
            Main("Capture", output);
            SessionState.SetString(Key, output);
            DomainMainAcceptance.Write(output + "/preferences-before.json", _preferences.Select(p => new Preference { key = p, exists = EditorPrefs.HasKey(p), value = EditorPrefs.GetString(p, "") }).ToArray());
            SessionState.SetString(Key + ".Preferences", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(output + "/preferences-before.json")));
            DomainMainAcceptance.Write(output + "/selection-before.json", Selection.instanceIDs);
            string folder = "Assets/Tests/YLFramework/Fixtures/Extensions/Domain/Main-" + Guid.NewGuid().ToString("N");
            DomainMainAcceptance.OwnPath(folder);
            AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
            var roots = new System.Collections.Generic.List<Root>();
            foreach (string module in new[] { "Skill", "Buff", "Mission" })
            {
                string ns = "YLFramework." + module + ".";
                string editorNs = "YLFramework.Editor." + module + ".";
                string plural = module == "Skill" ? "Skills" : module == "Buff" ? "Buffs" : "Missions";
                var rootType = typeof(SkillsConfig).Assembly.GetType(ns + plural + "Config", true);
                var dataType = typeof(SkillsConfig).Assembly.GetType(ns + module + "ConfigData", true);
                var config = ScriptableObject.CreateInstance(rootType);
                string path = folder + "/" + module + ".asset";
                AssetDatabase.CreateAsset(config, path);
                var controller = typeof(SkillsConfigController).Assembly.GetType(editorNs + plural + "ConfigController", true);
                var create = controller.GetMethod("Create" + module + "ConfigData", new[] { typeof(Type), rootType });
                create.Invoke(null, new object[] { dataType, config });
                create.Invoke(null, new object[] { dataType, config });
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssetIfDirty(config);
                roots.Add(new Root
                {
                    module = module,
                    path = path,
                    windowType = editorNs + (module == "Buff" ? "BuffConfigsWindow" : "ConfigsWindow"),
                    configField = "current" + module + "Config",
                    treeField = module.ToLowerInvariant() + "ListTree"
                });
            }
            SessionState.SetString(Key + ".Roots", JsonConvert.SerializeObject(roots));
            DomainMainAcceptance.Write(output + "/roots.json", roots);
        }

        public static object Show(string module)
        {
            DomainMainAcceptance.Identity();
            var roots = JsonConvert.DeserializeObject<Root[]>(SessionState.GetString(Key + ".Roots", ""));
            foreach (var item in roots)
            {
                if (item.window == 0)
                {
                    continue;
                }

                var old = EditorUtility.InstanceIDToObject(item.window) as EditorWindow;
                if (old)
                {
                    old.Close();
                }

                item.window = 0;
            }
            var root = roots.Single(r => r.module == module);
            var type = typeof(SkillsConfigController).Assembly.GetType(root.windowType, true);
            var window = (EditorWindow)ScriptableObject.CreateInstance(type);
            root.window = window.GetInstanceID();
            type.GetField(root.configField, Members).SetValue(window, AssetDatabase.LoadMainAssetAtPath(root.path));
            window.titleContent = new GUIContent("MC26 " + module + " Author Acceptance");
            window.ShowUtility();
            window.position = new Rect(120, 100, 1040, 720);
            window.Focus();
            window.Repaint();
            SessionState.SetString(Key + ".Roots", JsonConvert.SerializeObject(roots));
            return new
            {
                root.module,
                root.window,
                x = window.position.x,
                y = window.position.y,
                width = window.position.width,
                height = window.position.height
            };
        }

        public static object Capture(string module, string name)
        {
            DomainMainAcceptance.Identity();
            string output = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(output) || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9-]+$"))
            {
                throw new InvalidOperationException("Missing GUI Run/name.");
            }

            var root = JsonConvert.DeserializeObject<Root[]>(SessionState.GetString(Key + ".Roots", "")).Single(r => r.module == module);
            var window = EditorUtility.InstanceIDToObject(root.window) as EditorWindow;
            if (!window)
            {
                throw new InvalidOperationException("Owned author window is not open.");
            }

            var tree = window.GetType().GetField(root.treeField, Members).GetValue(window) as TreeView;
            if (tree == null || tree.GetRows().Count != 2)
            {
                throw new InvalidOperationException("Real author GUI has not drawn both owned rows.");
            }

            var rect = window.position;
            var pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(new Vector2(rect.x, rect.y), (int)rect.width, (int)rect.height);
            var texture = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply();
                string path = output + "/" + module.ToLowerInvariant() + "-" + name + ".png";
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    throw new IOException("GUI screenshot already exists.");
                }

                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
            var snapshot = new
            {
                module,
                root.path,
                root.window,
                rect = new
                {
                    rect.x,
                    rect.y,
                    rect.width,
                    rect.height
                },
                rows = tree.GetRows().Select(r => new { r.id, r.displayName }),
                selected = tree.GetSelection().ToArray(),
                realEditorGui = true,
                physicalMouseClaimed = false,
                utc = DateTime.UtcNow
            };
            DomainMainAcceptance.Write(output + "/" + module.ToLowerInvariant() + "-" + name + ".json", snapshot);
            return snapshot;
        }

        public static void Finish()
        {
            DomainMainAcceptance.Identity();
            string output = SessionState.GetString(Key, "");
            var roots = JsonConvert.DeserializeObject<Root[]>(SessionState.GetString(Key + ".Roots", ""));
            foreach (var root in roots)
            {
                var window = EditorUtility.InstanceIDToObject(root.window) as EditorWindow;
                if (window)
                {
                    window.Close();
                }
            }
            foreach (var before in JsonConvert.DeserializeObject<Preference[]>(SessionState.GetString(Key + ".Preferences", "")))
            {
                if (before.exists)
                {
                    EditorPrefs.SetString(before.key, before.value);
                }
                else
                {
                    EditorPrefs.DeleteKey(before.key);
                }
            }
            Main("Restore", output);
            Selection.instanceIDs = JsonConvert.DeserializeObject<int[]>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(output + "/selection-before.json")));
            DomainMainAcceptance.Write(output + "/gui-complete.json", new
            {
                closed = true,
                ownedWindowsClosed = true,
                preferencesRestored = true,
                originalSceneAndSettingsRestored = true,
                utc = DateTime.UtcNow
            });
            foreach (string suffix in new[] { "", ".Roots", ".Preferences" })
            {
                SessionState.EraseString(Key + suffix);
            }

            SessionState.EraseString("YLFramework.Domain.MainAcceptance.Output");
            SessionState.EraseString("YLFramework.Domain.MainAcceptance.Settings");
        }
    }
}
