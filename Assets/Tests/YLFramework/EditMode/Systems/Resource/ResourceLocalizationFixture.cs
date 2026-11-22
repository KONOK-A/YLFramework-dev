#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.Util;

namespace YLFramework.Tests.Resource
{
    // An Editor-only prepare hook for an isolated test host, never an automatic project initializer.
    public sealed class ResourceLocalizationFixture : ScriptableObject, IObjectInitializationDataProvider
    {
        private const string Root = "Assets/YLAddressablesLocalizationFixture";
        private const string BaseRoot = "Assets/YLAddressablesOwnershipContent";
        private const string SettingsName = "YL Addressables Localization Coexistence";
        private const string GateTypeName = "YLFramework.Tests.Resource.ResourceLocalizationInitializationGate";
        public string Name => "YL Localization coexistence initialization gate";

        public ObjectInitializationData CreateObjectInitializationData()
        {
            // Resolve only a public test type; no dependency from PlayMode to an Editor assembly.
            var gate = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(GateTypeName, false))
                .Where(type => type != null).SingleOrDefault();
            if (gate == null || !typeof(IInitializableObject).IsAssignableFrom(gate))
                throw new InvalidOperationException("Include ResourceLocalizationCoexistenceTests.cs in the runtime test assembly before preparation.");
            return ObjectInitializationData.CreateSerializedInitializationData(gate, "YL.LocalizationCoexistence.Gate");
        }

        /// <summary>Run after ResourceAddressablesTestContent.Build in a fresh isolated host, before entering PlayMode.</summary>
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Prepare the coexistence content before entering PlayMode.");
            var addressables = AddressableAssetSettingsDefaultObject.Settings;
            if (!addressables || AssetDatabase.GetAssetPath(addressables) != BaseRoot + "/Settings.asset")
                throw new InvalidOperationException("Run ResourceAddressablesTestContent.Build first; do not modify external Addressables settings.");
            if (LocalizationEditorSettings.ActiveLocalizationSettings)
                throw new InvalidOperationException("Use a fresh isolated host; existing Localization settings are not overwritten.");
            if (AssetDatabase.IsValidFolder(Root))
                throw new InvalidOperationException("The coexistence fixture output already exists; do not overwrite unknown assets.");
            var englishAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(BaseRoot + "/Message.txt");
            if (!englishAsset || englishAsset.text != "official addressables content")
                throw new InvalidOperationException("The official ownership/message content has not been prepared.");

            AssetDatabase.CreateFolder("Assets", "YLAddressablesLocalizationFixture");
            AssetDatabase.CreateFolder(Root, "Resources");
            var settings = CreateInstance<LocalizationSettings>();
            settings.name = SettingsName;
            settings.GetStartupLocaleSelectors().Clear();
            settings.GetStartupLocaleSelectors().Add(new SpecificLocaleSelector { LocaleId = new LocaleIdentifier("en") });
            AssetDatabase.CreateAsset(settings, Root + "/Resources/" + SettingsName + ".asset");
            LocalizationEditorSettings.ActiveLocalizationSettings = settings;
            LocalizationSettings.Instance = settings;
            LocalizationSettings.InitializeSynchronously = false;

            var english = CreateLocale("en", "English");
            var chinese = CreateLocale("zh-Hans", "Chinese");
            var locales = new[] { english, chinese };
            var strings = LocalizationEditorSettings.CreateStringTableCollection("YL_AA_Coexistence_Strings", Root, locales);
            foreach (var locale in locales)
            {
                var table = (StringTable)strings.GetTable(locale.Identifier);
                table.AddEntry("greeting", locale == english ? "Hello from Localization" : "\u4f60\u597d\uff0c\u5171\u5b58");
                LocalizationEditorSettings.SetPreloadTableFlag(table, true);
                EditorUtility.SetDirty(table);
            }
            EditorUtility.SetDirty(strings.SharedData);
            EditorUtility.SetDirty(strings);

            File.WriteAllText(Root + "/ChineseMessage.txt", "\u4e2d\u6587\u672c\u5730\u5316\u8d44\u4ea7", new UTF8Encoding(false));
            AssetDatabase.ImportAsset(Root + "/ChineseMessage.txt", ImportAssetOptions.ForceSynchronousImport);
            var chineseAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/ChineseMessage.txt");
            if (!chineseAsset) throw new InvalidOperationException("Chinese localized TextAsset import failed.");
            var assets = LocalizationEditorSettings.CreateAssetTableCollection("YL_AA_Coexistence_Assets", Root, locales);
            assets.AddAssetToTable(english.Identifier, "message", englishAsset);
            assets.AddAssetToTable(chinese.Identifier, "message", chineseAsset);
            foreach (var table in assets.AssetTables)
            {
                LocalizationEditorSettings.SetPreloadTableFlag(table, true);
                EditorUtility.SetDirty(table);
            }
            EditorUtility.SetDirty(assets.SharedData);
            EditorUtility.SetDirty(assets);

            var owner = addressables.FindAssetEntry(AssetDatabase.AssetPathToGUID(BaseRoot + "/Message.txt"));
            if (owner == null || owner.address != "ownership/message")
                throw new InvalidOperationException("Adding the localized asset must preserve the external ownership/message address.");
            var initializer = CreateInstance<ResourceLocalizationFixture>();
            AssetDatabase.CreateAsset(initializer, Root + "/InitializationGate.asset");
            initializer.CreateObjectInitializationData();
            if (!addressables.AddInitializationObject(initializer))
                throw new InvalidOperationException("Could not register the fixture's official initialization object.");
            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(addressables);
            AssetDatabase.SaveAssets();
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
            if (result == null || !string.IsNullOrEmpty(result.Error))
                throw new InvalidOperationException(result?.Error ?? "Localization coexistence Addressables build returned no result.");

            // The official Game View locale menu initializes active settings on EnteredPlayMode.
            // Bootstrap this owned real asset explicitly in the test instead, without global EditorPrefs changes.
            LocalizationEditorSettings.ActiveLocalizationSettings = null;
            LocalizationSettings.Instance = null;
            Resources.UnloadAsset(settings);
        }

        private static Locale CreateLocale(string code, string name)
        {
            var locale = Locale.CreateLocale(code);
            locale.name = name;
            AssetDatabase.CreateAsset(locale, Root + "/" + name + ".asset");
            LocalizationEditorSettings.AddLocale(locale);
            return locale;
        }
    }
}
#endif
