using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Editor.DataAssets;
using YLFramework.Editor.Localization;
using YLFramework.Localization;
using YLFramework.Save;
using YLFramework.Serialization;

namespace YLFramework.Tests.DataBatch
{
    [Category("YL.Module.Data")]
    public class DataSourceOwnershipTests
    {
        [TestCase("Packages/com.kano.ylframework.data-assets/Runtime/Systems/DataAssets/ScriptableObjectData.cs", "YLFramework.DataAssets")]
        [TestCase("Packages/com.kano.ylframework.data-assets/Editor/Systems/DataAssets/ConfigsWindow.cs", "YLFramework.DataAssets.Editor")]
        [TestCase("Packages/com.kano.ylframework.localization/Runtime/Systems/Localization/LocalizeString.cs", "YLFramework.Localization")]
        [TestCase("Packages/com.kano.ylframework.localization/Editor/Systems/Localization/LocalizationConfigsWindow.cs", "YLFramework.Localization.Editor")]
        [TestCase("Packages/com.kano.ylframework.save/Runtime/Systems/Save/SaveManager.cs", "YLFramework.Save")]
        [TestCase("Assets/Tests/YLFramework/EditMode/Systems/Save/SaveFacadeContractsTests.cs", "YLFramework.Save.Tests.EditMode")]
        [TestCase("Assets/Tests/YLFramework/Fixtures/Save/Runtime/SavePlayerProbe.cs", "YLFramework.Save.Acceptance")]
        [TestCase("Assets/Tests/YLFramework/EditMode/Systems/DataAssets/DataAssetIdentityTests.cs", "YLFramework.Tests.EditMode")]
        [TestCase("Assets/Tests/YLFramework/PlayMode/Systems/DataAssets/DataAssetResourceTests.cs", "YLFramework.Tests.PlayMode")]
        [TestCase("Assets/Tests/YLFramework/EditMode/Editor/DataAssets/DataAssetAuthoringTests.cs", "YLFramework.Tests.EditMode")]
        [TestCase("Assets/Tests/YLFramework/EditMode/Editor/Data/DataDomainReloadSessionTests.cs", "YLFramework.Tests.EditMode")]
        public void CurrentSourcesCompileIntoTheirDeclaredAssemblyAndKeepTheirAssetIdentity(string path, string expected)
        {
            string physical = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var owner = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(assembly => assembly.sourceFiles.Any(source =>
                Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(source)) == physical));
            Assert.That(owner.name, Is.EqualTo(expected), path);
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(guid, Does.Match("^[0-9a-f]{32}$"), path);
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(path));
            var metadata = Regex.Match(File.ReadAllText(physical + ".meta"), @"(?m)^guid: ([0-9a-f]{32})\s*$");
            Assert.That(metadata.Success, Is.True, path + ".meta");
            Assert.That(metadata.Groups[1].Value, Is.EqualTo(guid));
            if (path == "Packages/com.kano.ylframework.save/Runtime/Systems/Save/SaveManager.cs")
            {
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo("ff5957d84032533449f21601f372570a"));
            }
        }
    }

    [Category("YL.Module.Data")]
    public sealed class DataAuthoringCompositionTests
    {
        [Test]
        public void AuthoredDataAndLanguageColdReadFeedOneExplicitStoreDictionary()
        {
            string folder = "Assets/__YLDataComposition_" + Guid.NewGuid().ToString("N");
            string[] absentParents = new[] { "Assets/YLFrameworkData/DataAssets", "Assets/YLFrameworkData/Localization", "Assets/YLFrameworkData" }
                .Where(path => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) &&
                    !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))).ToArray();
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)), Is.Not.Empty);
            string dataFolder = null, languageFolder = null;
            var data = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            var localization = ScriptableObject.CreateInstance<MultiLanguageConfigs>();
            bool passed = false;
            try
            {
                string dataPath = folder + "/Data.asset", localizationPath = folder + "/Languages.asset";
                AssetDatabase.CreateAsset(data, dataPath);
                AssetDatabase.CreateAsset(localization, localizationPath);
                dataFolder = ScriptableObjectConfigsController.GetDataPath(data);
                languageFolder = LocalizationAuthoring.GetLanguageFolder(localization);
                foreach (string path in new[] { dataFolder, languageFolder })
                {
                    Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) ||
                        File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.False, path);
                }
                ScriptableObjectConfigsController.SetDataType(data,
                    AssetDatabase.LoadAssetAtPath<MonoScript>("Packages/com.kano.ylframework.data-assets/Runtime/Systems/DataAssets/ScriptableObjectData.cs"));
                var row = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), data);
                row.id = 17;
                row.configName = "daily-item";
                EditorUtility.SetDirty(row);
                var language = LocalizationAuthoring.CreateLanguage(localization, SystemLanguage.English);
                LocalizationAuthoring.AddEntry(localization, row.id, MultiLanguageType.String);
                language.GetLanguageInfo(row.id).str = "daily translated item";
                EditorUtility.SetDirty(language);
                ScriptableObjectConfigsController.Save(data);
                AssetDatabase.SaveAssetIfDirty(language);
                AssetDatabase.SaveAssetIfDirty(localization);
                string dataGuid = AssetDatabase.AssetPathToGUID(dataPath), localizationGuid = AssetDatabase.AssetPathToGUID(localizationPath);
                Resources.UnloadAsset(row);
                Resources.UnloadAsset(data);
                Resources.UnloadAsset(language);
                Resources.UnloadAsset(localization);
                data = AssetDatabase.LoadAssetAtPath<ScriptableObjectConfigs>(dataPath);
                localization = AssetDatabase.LoadAssetAtPath<MultiLanguageConfigs>(localizationPath);
                row = data.configs.Single();
                Assert.That(row.parent, Is.SameAs(data));
                Assert.That(data.typeScriptGuid, Is.EqualTo("a1743beab98e80e47b2c8b4917852add"));
                Assert.That(AssetDatabase.AssetPathToGUID(dataPath), Is.EqualTo(dataGuid));
                Assert.That(AssetDatabase.AssetPathToGUID(localizationPath), Is.EqualTo(localizationGuid));
                string translated = localization.GetLanguageConfigData(SystemLanguage.English).GetLanguageInfo(row.id).str;
                var dictionary = new SerializedDictionary<string, string> { { row.configName, translated } };
                string storeRoot = Path.Combine(global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput(), "data-authoring-" + Guid.NewGuid().ToString("N"));
                Assert.That(Directory.Exists(storeRoot) || File.Exists(storeRoot), Is.False, storeRoot);
                var settings = SaveManager.DefaultSerializerSettings;
                using (var store = new SaveStore(storeRoot, settings))
                {
                    var slot = store.CreateSaveItem(0);
                    store.SaveObject(dictionary, "authored", slot.SaveId);
                    store.ClearCache();
                    var restored = store.LoadObject<SerializedDictionary<string, string>>("authored", slot.SaveId);
                    Assert.That(restored, Is.Not.SameAs(dictionary));
                    Assert.That(restored.Single().Key, Is.EqualTo("daily-item"));
                    Assert.That(restored["daily-item"], Is.EqualTo("daily translated item"));
                }
                Assert.That(SaveManager.DefaultSerializerSettings, Is.SameAs(settings));
                passed = true;
            }
            finally
            {
                if (passed)
                {
                    if (data)
                    {
                        Undo.ClearUndo(data);
                        foreach (var row in data.configs)
                            if (row)
                                Undo.ClearUndo(row);
                    }
                    if (localization)
                    {
                        Undo.ClearUndo(localization);
                        foreach (var language in localization.Languages)
                            if (language)
                                Undo.ClearUndo(language);
                    }
                    foreach (string path in new[] { dataFolder, languageFolder, folder })
                        if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                            AssetDatabase.DeleteAsset(path);
                    foreach (string path in absentParents)
                        if (AssetDatabase.IsValidFolder(path) && !Directory.EnumerateFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(path)).Any())
                            AssetDatabase.DeleteAsset(path);
                }
                else
                    TestContext.Out.WriteLine("Preserved failed Data composition: " + folder);
            }
        }
    }
}
