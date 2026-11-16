using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;
using YLFramework.DataAssets;
using YLFramework.Editor.DataAssets;
using YLFramework.Save;
using YLFramework.Serialization;

namespace YLFramework.Tests.DataBatch
{
    [Category("YL.Module.Data")]
    public class DataSourceOwnershipTests
    {
        [TestCase("Packages/com.kano.ylframework.data-assets/Runtime/Systems/DataAssets/ScriptableObjectData.cs", "YLFramework.DataAssets")]
        [TestCase("Packages/com.kano.ylframework.data-assets/Editor/Systems/DataAssets/ConfigsWindow.cs", "YLFramework.DataAssets.Editor")]
        [TestCase("Packages/com.kano.ylframework.localization/Runtime/Systems/Localization/LocalizeText.cs", "YLFramework.Localization")]
        [TestCase("Packages/com.kano.ylframework.localization/Editor/Systems/Localization/LocalizationReferenceAuthoring.cs", "YLFramework.Localization.Editor")]
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
                Assert.That(guid, Is.EqualTo("ff5957d84032533449f21601f372570a"));
        }
    }

    [Category("YL.Module.Data")]
    public sealed class DataAuthoringCompositionTests
    {
        [Test]
        public void AuthoredDataAndLanguageColdReadFeedOneExplicitStoreDictionary()
        {
            var folder = "Assets/__YLDataComposition_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            string dataFolder = null;
            ScriptableObjectConfigs data = null;
            try
            {
                var locale = Locale.CreateLocale("en");
                AssetDatabase.CreateAsset(locale, folder + "/en.asset");
                var collection = LocalizationEditorSettings.CreateStringTableCollection("Data-" + Guid.NewGuid().ToString("N"), folder, new[] { locale });
                var table = (StringTable)collection.GetTable(locale.Identifier);
                var entry = table.AddEntry("item.title", "daily translated item");
                var key = entry.KeyId;
                var collectionGuid = collection.SharedData.TableCollectionNameGuid;
                EditorUtility.SetDirty(table);
                EditorUtility.SetDirty(collection.SharedData);
                AssetDatabase.SaveAssetIfDirty(table);
                AssetDatabase.SaveAssetIfDirty(collection.SharedData);
                var tablePath = AssetDatabase.GetAssetPath(table);
                data = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
                var dataPath = folder + "/Data.asset";
                AssetDatabase.CreateAsset(data, dataPath);
                ScriptableObjectConfigsController.SetDataType(data, AssetDatabase.LoadAssetAtPath<MonoScript>(
                    "Assets/Tests/YLFramework/EditMode/Editor/Data/LocalizationDataFixture.cs"));
                dataFolder = ScriptableObjectConfigsController.GetDataPath(data);
                var row = (LocalizationDataFixture)ScriptableObjectConfigsController.CreateData(typeof(LocalizationDataFixture), data);
                row.id = 17;
                row.configName = "daily-item";
                row.title = new LocalizedString(collectionGuid, key);
                EditorUtility.SetDirty(row);
                ScriptableObjectConfigsController.Save(data);
                var guid = AssetDatabase.AssetPathToGUID(dataPath);
                Resources.UnloadAsset(row);
                Resources.UnloadAsset(data);
                Resources.UnloadAsset(table);
                data = AssetDatabase.LoadAssetAtPath<ScriptableObjectConfigs>(dataPath);
                row = (LocalizationDataFixture)data.configs.Single();
                table = AssetDatabase.LoadAssetAtPath<StringTable>(tablePath);
                Assert.That(row.parent, Is.SameAs(data));
                Assert.That(AssetDatabase.AssetPathToGUID(dataPath), Is.EqualTo(guid));
                Assert.That(row.title.TableReference.TableCollectionNameGuid, Is.EqualTo(collectionGuid));
                var dictionary = new SerializedDictionary<string, string> { { row.configName, table.GetEntry(row.title.TableEntryReference.KeyId).Value } };
                var output = Path.Combine(global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput(), "data-authoring-" + Guid.NewGuid().ToString("N"));
                var settings = SaveManager.DefaultSerializerSettings;
                using (var store = new SaveStore(output, settings))
                {
                    var slot = store.CreateSaveItem(0);
                    store.SaveObject(dictionary, "authored", slot.SaveId);
                    store.ClearCache();
                    var restored = store.LoadObject<SerializedDictionary<string, string>>("authored", slot.SaveId);
                    Assert.That(restored, Is.Not.SameAs(dictionary));
                    Assert.That(restored["daily-item"], Is.EqualTo("daily translated item"));
                }
                Assert.That(SaveManager.DefaultSerializerSettings, Is.SameAs(settings));
            }
            finally
            {
                if (data)
                {
                    Undo.ClearUndo(data);
                    foreach (var row in data.configs)
                        if (row)
                            Undo.ClearUndo(row);
                }
                if (!string.IsNullOrEmpty(dataFolder))
                    AssetDatabase.DeleteAsset(dataFolder);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
