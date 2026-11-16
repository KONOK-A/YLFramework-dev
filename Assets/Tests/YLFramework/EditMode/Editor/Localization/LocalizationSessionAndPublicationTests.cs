using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Localization;
using YLFramework.Testing.EditorFixtures;
using YLFramework.Tests.Localization;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Data
{
    [Category("YL.Special.Session")]
    public class LocalizationSessionAndPublicationTests
    {
        private static readonly string _domainToken = Guid.NewGuid().ToString("N");
        private LocalizationFixture _fixture;
        private LocalizationSession _session;
        private string _folder;
        private LocalizationSettings _previousSettings;
        private AddressableAssetSettings _previousAddressables;
        private bool _ownsPublication;
        private bool _createdDefaultFolder;

        [SetUp]
        public void RequireOwnedHost()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlaying, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
            Assert.That(Directory.Exists(global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput()), Is.True);
        }

        [UnityTest]
        public IEnumerator TwoActualPlaySessionsCloseOnlyTheirLocalizationOwners()
        {
            var token = _domainToken;
            for (var round = 0; round < 2; round++)
            {
                yield return new EnterPlayMode(false);
                Assert.That(_domainToken, Is.EqualTo(token));
                _fixture = new LocalizationFixture();
                _session = new LocalizationSession();
                string value = null;
                _session.BindString(new LocalizedString(_fixture.Strings, "name"), result => value = result);
                for (var frame = 0; frame < 5; frame++)
                    yield return null;
                Assert.That(value, Is.EqualTo("Sword"));
                yield return new ExitPlayMode();
                Assert.That(_session.IsDisposed, Is.True);
                _fixture.Dispose();
                _fixture = null;
                _session = null;
            }
        }

        [UnityTest, Timeout(1200000)]
        public IEnumerator FormalPublicationLoadsTheMovedTypesInEditorAndActualStandalonePlayers()
        {
            _folder = "Assets/__YLLocale_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            AssetDatabase.CreateFolder(_folder, "Addressables");
            _previousSettings = LocalizationEditorSettings.ActiveLocalizationSettings;
            _previousAddressables = AddressableAssetSettingsDefaultObject.Settings;
            _createdDefaultFolder = !AssetDatabase.IsValidFolder("Assets/AddressableAssetsData");
            if (_createdDefaultFolder)
                AssetDatabase.CreateFolder("Assets", "AddressableAssetsData");
            _ownsPublication = true;
            var addressables = AddressableAssetSettings.Create(_folder + "/Addressables", "Settings", true, true);
            AddressableAssetSettingsDefaultObject.Settings = addressables;
            var settings = ScriptableObject.CreateInstance<LocalizationSettings>();
            AssetDatabase.CreateAsset(settings, _folder + "/LocalizationSettings.asset");
            LocalizationEditorSettings.ActiveLocalizationSettings = settings;
            var english = Locale.CreateLocale("en");
            var french = Locale.CreateLocale("fr");
            AssetDatabase.CreateAsset(english, _folder + "/English.asset");
            AssetDatabase.CreateAsset(french, _folder + "/French.asset");
            LocalizationEditorSettings.AddLocale(english);
            LocalizationEditorSettings.AddLocale(french);
            var locales = new[] { english, french };
            var strings = LocalizationEditorSettings.CreateStringTableCollection("PublishedText-" + Guid.NewGuid().ToString("N"), _folder, locales);
            foreach (var locale in locales)
            {
                var table = (StringTable)strings.GetTable(locale.Identifier);
                table.AddEntry("greeting", locale == english ? "hello {count}" : "salut {count}").IsSmart = true;
                EditorUtility.SetDirty(table);
                AssetDatabase.SaveAssetIfDirty(table);
            }
            EditorUtility.SetDirty(strings.SharedData);
            AssetDatabase.SaveAssetIfDirty(strings.SharedData);
            AssetDatabase.SaveAssetIfDirty(strings);
            var assets = LocalizationEditorSettings.CreateAssetTableCollection("PublishedAssets-" + Guid.NewGuid().ToString("N"), _folder, locales);
            var texture = new Texture2D(8, 8);
            texture.SetPixels(Enumerable.Repeat(Color.cyan, 64).ToArray());
            texture.Apply();
            AssetDatabase.CreateAsset(texture, _folder + "/Icon.asset");
            var sprite = Sprite.Create(texture, new Rect(0, 0, 8, 8), Vector2.zero);
            sprite.name = "PublishedIcon";
            AssetDatabase.AddObjectToAsset(sprite, texture);
            AssetDatabase.SaveAssetIfDirty(texture);
            var font = Font.CreateDynamicFontFromOSFont("Arial", 16);
            AssetDatabase.CreateAsset(font, _folder + "/Font.fontsettings");
            foreach (var locale in locales)
            {
                assets.AddAssetToTable(locale.Identifier, "icon", sprite);
                assets.AddAssetToTable(locale.Identifier, "font", font);
            }
            foreach (var table in assets.AssetTables)
                AssetDatabase.SaveAssetIfDirty(table);
            EditorUtility.SetDirty(assets.SharedData);
            AssetDatabase.SaveAssetIfDirty(assets.SharedData);
            AssetDatabase.SaveAssetIfDirty(assets);
            AssetDatabase.SaveAssetIfDirty(settings);
            foreach (var group in addressables.groups.Where(group => group))
            {
                foreach (var schema in group.Schemas)
                    if (schema)
                        AssetDatabase.SaveAssetIfDirty(schema);
                AssetDatabase.SaveAssetIfDirty(group);
            }
            AssetDatabase.SaveAssetIfDirty(addressables);
            AddressableAssetSettings.BuildPlayerContent(out var content);
            Assert.That(content.Error, Is.Null.Or.Empty);
            Assert.That(Directory.Exists(Addressables.BuildPath), Is.True);
            Assert.That(Directory.GetFiles(Addressables.BuildPath, "*.bundle", SearchOption.AllDirectories), Is.Not.Empty);
            var output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            File.WriteAllText(Path.Combine(output, "addressables-build.json"),
                "{\"status\":\"Built\",\"contentSystem\":\"Unity Localization/Addressables\",\"backend\":\"IL2CPP\"}");
            StandaloneFixturePlayer.BuildAndRun(output, _folder, "YLFramework.Localization", null, scene =>
            {
                var type = Type.GetType("YLFramework.Tests.Localization.LocalizationPlayerProbe, YLFramework.Localization.Acceptance", true);
                var host = new GameObject("OfficialLocalizationPlayerProbe");
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                var probe = host.AddComponent(type);
                type.GetField("text").SetValue(probe, new LocalizedString(strings.SharedData.TableCollectionNameGuid, "greeting"));
                type.GetField("picture").SetValue(probe, new LocalizedSprite { TableReference = assets.SharedData.TableCollectionNameGuid, TableEntryReference = "icon" });
                type.GetField("font").SetValue(probe, new LocalizedFont { TableReference = assets.SharedData.TableCollectionNameGuid, TableEntryReference = "font" });
                type.GetField("english").SetValue(probe, english);
                type.GetField("french").SetValue(probe, french);
                host.SetActive(true);
            }, new[] { "YL_LOCALIZATION_ACCEPTANCE" }, scriptingBackend: ScriptingImplementation.IL2CPP, developmentBuild: false);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator CleanupOwnedFixture()
        {
            if (EditorApplication.isPlaying)
                yield return new ExitPlayMode();
            _session?.Dispose();
            _session = null;
            _fixture?.Dispose();
            _fixture = null;
            if (_ownsPublication)
            {
                LocalizationEditorSettings.ActiveLocalizationSettings = _previousSettings;
                if (_previousAddressables)
                    AddressableAssetSettingsDefaultObject.Settings = _previousAddressables;
                else
                {
                    EditorBuildSettings.RemoveConfigObject(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName);
                    typeof(AddressableAssetSettingsDefaultObject).GetField("s_DefaultSettingsObject", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                        .SetValue(null, null);
                }
                if (TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed)
                {
                    AssetDatabase.DeleteAsset(_folder);
                    if (_createdDefaultFolder)
                        AssetDatabase.DeleteAsset("Assets/AddressableAssetsData");
                }
            }
            _ownsPublication = false;
            _folder = null;
        }
    }
}
