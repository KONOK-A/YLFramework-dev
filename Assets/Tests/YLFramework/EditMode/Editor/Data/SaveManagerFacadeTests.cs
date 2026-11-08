using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YLFramework.Foundation;
using YLFramework.Save;
using YLFramework.Serialization;

namespace YLFramework.Tests.Extensions.Data.Save
{
    [Category("YL.Module.SaveBcl")]
    [Category("YL.Module.Data")]
    public class SaveManagerFacadeTests
    {
        public class Record
        {
            public SerializedDictionary<string, int> Direct = new SerializedDictionary<string, int>();
            public SerializedDictionary<int, Payload> Nested = new SerializedDictionary<int, Payload>();
        }
        public class Payload
        {
            public int Number;
        }

        [Test]
        public void ExplicitObjectKeysAndSerializedDictionaryDiskContractUseOneDefaultOwner()
        {
            WithRoot(root =>
            {
                var store = SaveManager.Store;
                var item = store.CreateSaveItem();
                var data = new Record { Direct = { { "a", 2 }, { "b", 4 } }, Nested = { { 7, new Payload { Number = 6 } } } };
                var settings = SaveManager.DefaultSerializerSettings;
                store.SaveObject(data, "record", item.SaveId);
                store.SaveObject(data.Direct, "dictionary", item.SaveId);
                store.SaveObject(data, "Record", item.SaveId);
                Assert.That(store.LoadObject<Record>("record", item.SaveId), Is.SameAs(data));
                Assert.That(store.LoadObject<Record>("Record", item.SaveId), Is.SameAs(data));
                var cached = store.LoadObject<Record>("record", item.SaveId);
                Assert.That(cached, Is.SameAs(data));
                Assert.That(JToken.DeepEquals(JToken.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/0/dictionary.json")))), JToken.Parse("{\"a\":2,\"b\":4}")), Is.True);
                Assert.That(JToken.DeepEquals(JToken.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/0/record.json")))), JToken.Parse("{\"Direct\":{\"a\":2,\"b\":4},\"Nested\":{\"7\":{\"Number\":6}}}")), Is.True);
                data.Direct["a"] = -1;
                data.Nested[7].Number = -1;
                SaveManager.SetStorageRoot(root);
                store = SaveManager.Store;
                var restored = store.LoadObject<Record>("record", item.SaveId);
                Assert.That(restored, Is.Not.SameAs(data));
                Assert.That(restored.Direct["a"], Is.EqualTo(2));
                Assert.That(restored.Nested[7].Number, Is.EqualTo(6));
                Assert.That(store.LoadObject<SerializedDictionary<string, int>>("dictionary", item.SaveId)["b"], Is.EqualTo(4));
                Assert.That(store.LoadObject<Record>("Record", item.SaveId).Direct["a"], Is.EqualTo(2));
                Assert.That(store.GetSaveItem(0), Is.Not.SameAs(item));
                Assert.That(store.GetAllSaveItems().Count, Is.EqualTo(1));
                Assert.That(SaveManager.DefaultSerializerSettings, Is.SameAs(settings));
                Assert.That(store.RenameSaveItem(0, "named"), Is.True);
                Assert.That(store.SetSaveItemNote(0, "note"), Is.True);
                Assert.That(store.GetAllSaveItems().OrderBy(slot => slot.DisplayName ?? string.Empty).First().DisplayName, Is.EqualTo("named"));
                Assert.That(store.GetAllSaveItems().OrderBy(slot => slot.SaveId).First().Note, Is.EqualTo("note"));
            });
        }

        [Test]
        public void AsyncDefaultStoreCapturesExplicitRootAndModelsBeforeDispatch()
        {
            WithRoot(root =>
            {
                var store = SaveManager.Store;
                var item = store.CreateSaveItemAsync(6).GetAwaiter().GetResult();
                var payload = new Payload { Number = 81 };
                store.SaveObjectAsync(payload, "player", 6).GetAwaiter().GetResult();
                SaveManager.Close();
                store = SaveManager.Store;
                Assert.That(store.LoadObjectAsync<Payload>("player", 6).GetAwaiter().GetResult().Number, Is.EqualTo(81));
                store.SaveGlobalSettingAsync(new Payload { Number = 4 }, "options").GetAwaiter().GetResult();
                Assert.That(store.LoadGlobalSetting<Payload>("options").Number, Is.EqualTo(4));
                store.DeleteObject("player", item.SaveId);
                var missing = store.LoadObject<Payload>("player", item.SaveId);
                Assert.That(missing, Is.Null);
                store.DeleteSaveItemAsync(6).GetAwaiter().GetResult();
                Assert.That(store.GetAllSaveItems().Count, Is.EqualTo(0));
            });
        }

        private static void WithRoot(Action<string> check)
        {
            const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
            var storeField = typeof(SaveManager).GetField("_store", Flags);
            var rootField = typeof(SaveManager).GetField("_storageRootOverride", Flags);
            var previousStore = storeField.GetValue(null);
            var previousRoot = rootField.GetValue(null);
            var output = Environment.GetEnvironmentVariable("YL_SAVE_TEST_OUTPUT") ?? Path.Combine(Directory.GetCurrentDirectory(), "Temp", "Extensions.Data.Save");
            var root = Path.Combine(output, "facade-cases", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(root));
            var passed = false;
            storeField.SetValue(null, null);
            try
            {
                SaveManager.SetStorageRoot(root);
                check(root);
                passed = true;
            }
            finally
            {
                SaveManager.Close();
                rootField.SetValue(null, previousRoot);
                storeField.SetValue(null, previousStore);
                if (passed)
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
                }
                else
                {
                    Console.WriteLine("Preserved failed facade fixture: " + root);
                }
            }
            Assert.That(storeField.GetValue(null), Is.SameAs(previousStore));
            Assert.That(rootField.GetValue(null), Is.EqualTo(previousRoot));
        }
    }
}
