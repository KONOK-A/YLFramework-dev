using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.Events;
using UnityEditor.Localization;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YLFramework.Localization.Samples.Editor
{
    /// <summary>显式生成可运行的正式样例；仅创建新目录及新场景，不覆盖或保存已有场景。</summary>
    public static class LocalizationSampleBuilder
    {
        /// <summary>创建官方设置/表、两色 Sprite、UI 场景和 Core Panel 模板。可选字体只借用项目资产，不复制系统字体。</summary>
        public static string Create(string folder, Font englishFont = null, Font frenchFont = null)
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Create the sample outside Play Mode.");
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                    throw new InvalidOperationException("Save or close untitled scenes explicitly before creating the sample; no existing scene will be saved automatically.");
            folder = (folder ?? "").Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal) || folder.Split('/').Any(part => part == "." || part == "..") ||
                Directory.Exists(folder) || File.Exists(folder))
                throw new InvalidOperationException("Choose a new, unoccupied sample folder under Assets/.");
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent))
                throw new InvalidOperationException("The sample parent folder must already exist.");
            foreach (var font in new[] { englishFont, frenchFont })
                if (font && !EditorUtility.IsPersistent(font))
                    throw new ArgumentException("Use a project font asset with an appropriate distribution license.");
            if ((bool)englishFont != (bool)frenchFont)
                throw new ArgumentException("Choose both locale fonts, or leave both unset.");
            // 官方 Addressables 创建组/Schema 的内部流程可能 SaveAssets；先拒绝整个保存范围内的未知脏输入。
            var dirty = Resources.FindObjectsOfTypeAll<UnityEngine.Object>()
                .Where(asset => asset && EditorUtility.IsPersistent(asset) && EditorUtility.IsDirty(asset))
                .Select(AssetDatabase.GetAssetPath)
                .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)).Distinct().ToArray();
            if (dirty.Length != 0)
                throw new InvalidOperationException("Explicitly save or discard dirty persistent assets before generating the sample: " + string.Join(", ", dirty));
            var addressables = AddressableAssetSettingsDefaultObject.Settings;
            if (addressables && (EditorUtility.IsDirty(addressables) || addressables.groups.Where(item => item)
                .Any(group => EditorUtility.IsDirty(group) || group.Schemas.Any(schema => schema && EditorUtility.IsDirty(schema)))))
                throw new InvalidOperationException("Save or discard existing Addressables edits explicitly before generating the sample.");
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            var settings = LocalizationEditorSettings.ActiveLocalizationSettings;
            if (!settings)
            {
                settings = ScriptableObject.CreateInstance<LocalizationSettings>();
                AssetDatabase.CreateAsset(settings, folder + "/LocalizationSettings.asset");
                LocalizationEditorSettings.ActiveLocalizationSettings = settings;
                AssetDatabase.SaveAssetIfDirty(settings);
            }
            var english = LocaleFor("en", folder);
            var french = LocaleFor("fr", folder);
            var locales = new[] { english, french };
            var suffix = Guid.NewGuid().ToString("N");
            var strings = LocalizationEditorSettings.CreateStringTableCollection("YL_Demo_Text_" + suffix, folder, locales);
            foreach (var locale in locales)
            {
                var table = (StringTable)strings.GetTable(locale.Identifier);
                table.AddEntry("title", locale == english ? "LOCALIZATION / SESSION" : "LOCALISATION / SESSION");
                table.AddEntry("count", locale == english ? "{count:N0} items in this view" : "{count:N0} objets dans cette vue").IsSmart = true;
                EditorUtility.SetDirty(table);
                AssetDatabase.SaveAssetIfDirty(table);
            }
            EditorUtility.SetDirty(strings.SharedData);
            AssetDatabase.SaveAssetIfDirty(strings.SharedData);
            AssetDatabase.SaveAssetIfDirty(strings);
            var assets = LocalizationEditorSettings.CreateAssetTableCollection("YL_Demo_Assets_" + suffix, folder, locales);
            assets.AddAssetToTable(english.Identifier, "icon", Icon(folder + "/English.png", new Color(0.15f, 0.55f, 0.9f)));
            assets.AddAssetToTable(french.Identifier, "icon", Icon(folder + "/French.png", new Color(0.9f, 0.35f, 0.25f)));
            if (englishFont && frenchFont)
            {
                assets.AddAssetToTable(english.Identifier, "font", englishFont);
                assets.AddAssetToTable(french.Identifier, "font", frenchFont);
            }
            foreach (var table in assets.AssetTables)
                AssetDatabase.SaveAssetIfDirty(table);
            EditorUtility.SetDirty(assets.SharedData);
            AssetDatabase.SaveAssetIfDirty(assets.SharedData);
            AssetDatabase.SaveAssetIfDirty(assets);
            addressables = AddressableAssetSettingsDefaultObject.Settings;
            if (addressables)
            {
                foreach (var group in addressables.groups.Where(item => item))
                {
                    foreach (var schema in group.Schemas)
                        if (schema)
                            AssetDatabase.SaveAssetIfDirty(schema);
                    AssetDatabase.SaveAssetIfDirty(group);
                }
                AssetDatabase.SaveAssetIfDirty(addressables);
            }

            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var canvas = new GameObject("Localization Demo", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var background = canvas.AddComponent<Image>();
                background.color = new Color(0.95f, 0.97f, 0.99f);
                background.raycastTarget = false;
                var scaler = canvas.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(960, 540);
                var card = Rect("Localized Card", canvas.transform, new Vector2(560, 310), new Vector2(0, 40));
                var content = Rect("Content", card, new Vector2(540, 300), Vector2.zero);
                var gate = content.gameObject.AddComponent<CanvasGroup>();
                var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.spacing = 12;
                layout.padding = new RectOffset(20, 20, 20, 20);
                layout.childAlignment = TextAnchor.MiddleCenter;
                layout.childControlHeight = false;
                layout.childForceExpandHeight = false;
                var title = Label("Title", content, 26, englishFont);
                var titleBinding = title.gameObject.AddComponent<LocalizeText>();
                titleBinding.StringReference = new LocalizedString(strings.SharedData.TableCollectionNameGuid, "title");
                var image = Rect("Localized Icon", content, new Vector2(80, 80), Vector2.zero).gameObject.AddComponent<Image>();
                image.preserveAspect = true;
                image.gameObject.AddComponent<LocalizeSprite>().SpriteReference = new LocalizedSprite
                {
                    TableReference = assets.SharedData.TableCollectionNameGuid,
                    TableEntryReference = "icon"
                };
                var count = Label("Counter", content, 22, englishFont);
                var counterBinding = count.gameObject.AddComponent<LocalizeText>();
                counterBinding.StringReference = new LocalizedString(strings.SharedData.TableCollectionNameGuid, "count");
                if (englishFont && frenchFont)
                {
                    titleBinding.FontReference = new LocalizedFont { TableReference = assets.SharedData.TableCollectionNameGuid, TableEntryReference = "font" };
                    counterBinding.FontReference = new LocalizedFont { TableReference = assets.SharedData.TableCollectionNameGuid, TableEntryReference = "font" };
                }
                var loading = Label("Loading...", card, 20, englishFont).gameObject;
                var failure = Label("Content failed. Inspect the status and retry.", card, 20, englishFont).gameObject;
                var group = card.gameObject.AddComponent<LocalizationGroup>();
                group.Content = gate;
                group.LoadingRoot = loading;
                group.FailureRoot = failure;
                group.AutoStart = false;
                var status = Label("Content status", canvas.transform, 16, englishFont);
                status.rectTransform.anchoredPosition = new Vector2(0, -110);
                var example = canvas.AddComponent<LocalizationUIExample>();
                var serialized = new SerializedObject(example);
                serialized.FindProperty("_group").objectReferenceValue = group;
                serialized.FindProperty("_counter").objectReferenceValue = counterBinding;
                serialized.FindProperty("_status").objectReferenceValue = status;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                ButtonAt(canvas.transform, "English", -225, example.English, englishFont);
                ButtonAt(canvas.transform, "Francais", -75, example.French, englishFont);
                ButtonAt(canvas.transform, "+1 item", 75, example.Increase, englishFont);
                ButtonAt(canvas.transform, "Rebind view", 225, example.RestartBindings, englishFont);
                var events = new GameObject("EventSystem", typeof(EventSystem));
                var input = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
                if (input != null)
                    events.AddComponent(input);
                else
                    events.AddComponent<StandaloneInputModule>();

                var panelObject = UnityEngine.Object.Instantiate(card.gameObject);
                panelObject.name = "LocalizedCounterPanel";
                var panel = panelObject.AddComponent<LocalizedCounterPanel>();
                var panelData = new SerializedObject(panel);
                panelData.FindProperty("_group").objectReferenceValue = panelObject.GetComponent<LocalizationGroup>();
                panelData.FindProperty("_counter").objectReferenceValue = panelObject.GetComponentsInChildren<LocalizeText>(true).Single(item => item.name == "Counter");
                panelData.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(panelObject, folder + "/LocalizedCounterPanel.prefab");
                UnityEngine.Object.DestroyImmediate(panelObject);
                var path = folder + "/LocalizationDemo.unity";
                if (!EditorSceneManager.SaveScene(scene, path))
                    throw new InvalidOperationException("Could not save the new sample scene.");
                return path;
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid() && previous.isLoaded)
                    SceneManager.SetActiveScene(previous);
            }
        }

        private static Locale LocaleFor(string code, string folder)
        {
            var locale = LocalizationEditorSettings.GetLocale(code);
            if (locale)
                return locale;
            locale = Locale.CreateLocale(code);
            AssetDatabase.CreateAsset(locale, folder + "/" + code + ".asset");
            LocalizationEditorSettings.AddLocale(locale);
            return locale;
        }

        private static Sprite Icon(string path, Color color)
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(Enumerable.Repeat(color, 64).ToArray());
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 position)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return rect;
        }

        private static Text Label(string name, Transform parent, int size, Font font)
        {
            var label = Rect(name, parent, new Vector2(510, 44), Vector2.zero).gameObject.AddComponent<Text>();
            label.font = font ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = size;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = new Color(0.13f, 0.18f, 0.24f);
            label.text = name;
            return label;
        }

        private static void ButtonAt(Transform parent, string title, float x, UnityEngine.Events.UnityAction action, Font font)
        {
            var rect = Rect(title, parent, new Vector2(138, 44), new Vector2(x, -185));
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(0.82f, 0.9f, 0.97f);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            UnityEventTools.AddPersistentListener(button.onClick, action);
            var label = Label(title, rect, 17, font);
            label.rectTransform.sizeDelta = rect.sizeDelta;
        }

        [MenuItem("YLFramework/Localization/Samples/Create Basic UI")]
        private static void CreateFromMenu()
        {
            EditorWindow.GetWindow<LocalizationSampleWindow>("Localization UI");
        }
    }

    /// <summary>用户显式选择输出与项目字体；绘制窗口时不写入任何资产。</summary>
    public sealed class LocalizationSampleWindow : EditorWindow
    {
        private string _folder;
        private Font _english;
        private Font _french;

        private void OnEnable() => _folder = "Assets/YLLocalizationDemo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Creates a new scene, official tables/locales and Addressables entries. Existing settings are not replaced and existing scenes are not saved. Optional fonts must be project assets with appropriate licenses.", MessageType.Info);
            _folder = EditorGUILayout.TextField("New Folder", _folder);
            _english = (Font)EditorGUILayout.ObjectField("English Font (optional)", _english, typeof(Font), false);
            _french = (Font)EditorGUILayout.ObjectField("French Font (optional)", _french, typeof(Font), false);
            if (!GUILayout.Button("Create Sample Assets"))
                return;
            if (!EditorUtility.DisplayDialog("Create Localization UI", "Create new assets under " + _folder + "?", "Create", "Cancel"))
                return;
            try
            {
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(LocalizationSampleBuilder.Create(_folder, _english, _french));
            }
            catch (Exception error) { EditorUtility.DisplayDialog("Sample Creation Failed", error.Message, "Close"); }
        }
    }
}
