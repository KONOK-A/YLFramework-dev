using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Foundation;
using SaveManager = YLFramework.Save.SaveManager;
using YLFramework.Serialization;

namespace YLFramework.Tests
{
    // 当前 Unity Test Runner 使用 NUnit 2；同步 EditMode 用例在主线程顺序执行。
    public class SerializedDictionarySaveTests
    {
        public sealed class SettingsRecord
        {
            public SerializedDictionary<string, Payload> Entries = new SerializedDictionary<string, Payload>();
        }

        [Test]
        public void NestedRecordUsesActualSaveSettingsWithoutChangingOrCallingSaveManager()
        {
            var source = new SettingsRecord();
            source.Entries.Add("first", new Payload { Number = 1 });
            source.Entries["first"] = new Payload { Number = 2 };
            source.Entries["second"] = null;
            var json = JsonConvert.SerializeObject(source, SaveManager.SerializerSettings);
            Assert.That(JToken.DeepEquals(JToken.Parse(json), JToken.Parse("{\"Entries\":{\"first\":{\"Number\":2},\"second\":null}}")), Is.True);
            var restored = JsonConvert.DeserializeObject<SettingsRecord>(json, SaveManager.SerializerSettings);
            Assert.That(restored.Entries["first"].Number, Is.EqualTo(2));
            Assert.That(restored.Entries.TryGetValue("second", out var value), Is.True);
            Assert.That(value, Is.Null);
            Assert.That(JsonConvert.SerializeObject(restored, SaveManager.SerializerSettings), Is.EqualTo(json));
        }

        [Serializable]
        public class Record
        {
            public SerializedDictionary<string, int> Direct = new SerializedDictionary<string, int>();
            public SerializedDictionary<int, Payload> Nested = new SerializedDictionary<int, Payload>();
        }

        [Serializable]
        public class Payload
        {
            public int Number;
        }

        [Test]
        public void SaveManagerDiskRoundTripRestoresBareDictionariesAndUnitySerializableRows()
        {
            const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
            var rootField = typeof(SaveManager).GetField("_storageRootOverride", Flags);
            var storeField = typeof(SaveManager).GetField("_store", Flags);
            Assert.That(rootField, Is.Not.Null);
            Assert.That(storeField, Is.Not.Null);

            var originalRoot = rootField.GetValue(null);
            var originalStore = storeField.GetValue(null);
            var settings = SaveManager.SerializerSettings;
            var originalSettings = CaptureSettings(settings);
            var originalFormat = SaveManager.SerializationFormat;
            var output = Environment.GetEnvironmentVariable("YL_SAVE_TEST_OUTPUT") ??
                Path.Combine(Directory.GetCurrentDirectory(), "Temp", "Extensions.Data.Save");
            var tempRoot = Path.Combine(output, "legacy-cases", Guid.NewGuid().ToString("N"));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(tempRoot)), Is.False);
            var ownsRoot = false;
            var passed = false;
            var detached = false;

            try
            {
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(tempRoot));
                ownsRoot = true;
                // 暂存真实 owner，不关闭、不读取真实存档；测试只拥有下面的新临时 owner。
                storeField.SetValue(null, null);
                detached = true;
                SaveManager.SetStorageRoot(tempRoot);
                Assert.That(SaveManager.StorageRoot, Is.EqualTo(tempRoot));
                Assert.That(storeField.GetValue(null), Is.Null);

                var slot = SaveManager.CreateSaveItem();
                var source = new SerializedDictionary<string, int> { { "a", 1 }, { "remove", 3 } };
                source["a"] = 2;
                source.Add("b", 4);
                Assert.That(source.Remove("remove"), Is.True);
                var record = new Record { Direct = source };
                record.Nested.Add(7, new Payload { Number = 5 });
                record.Nested[7] = new Payload { Number = 6 };
                record.Nested.Add(-2, new Payload { Number = 8 });

                const string RootFileName = "root-dictionary";
                const string RecordFileName = "nested-record";
                SaveManager.SaveObject(source, RootFileName, slot.SaveId);
                SaveManager.SaveObject(record, RecordFileName, slot.SaveId);
                var saveDirectory = Path.Combine(tempRoot, "SaveData");
                var slotDirectory = Path.Combine(saveDirectory, slot.SaveId.ToString());
                var rootPath = Path.Combine(slotDirectory, RootFileName + ".json");
                var recordPath = Path.Combine(slotDirectory, RecordFileName + ".json");
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(saveDirectory, "SaveSystemData.json"))), Is.True);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(rootPath)), Is.True);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(recordPath)), Is.True);
                Assert.That(JToken.DeepEquals(JToken.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(rootPath))),
                    JToken.Parse("{\"a\":2,\"b\":4}")), Is.True);
                Assert.That(JToken.DeepEquals(JToken.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(recordPath))),
                    JToken.Parse("{\"Direct\":{\"a\":2,\"b\":4},\"Nested\":{\"7\":{\"Number\":6},\"-2\":{\"Number\":8}}}")), Is.True);
                Assert.That(SaveManager.LoadObject<SerializedDictionary<string, int>>(RootFileName, slot.SaveId), Is.SameAs(source));
                Assert.That(SaveManager.LoadObject<Record>(RecordFileName, slot.SaveId), Is.SameAs(record));

                // 不再保存这些修改；若 LoadObject 错误复用原实例，下面的磁盘值断言会失败。
                source["a"] = -1;
                source.Add("memory-only", 9);
                record.Nested[7].Number = -1;
                // 同根目录切换走公开清缓存路径，同时迫使存档索引从测试磁盘重新加载。
                SaveManager.SetStorageRoot(tempRoot);
                Assert.That(storeField.GetValue(null), Is.Null);

                var restored = SaveManager.LoadObject<SerializedDictionary<string, int>>(RootFileName, slot.SaveId);
                var restoredRecord = SaveManager.LoadObject<Record>(RecordFileName, slot.SaveId);
                Assert.That(restored, Is.Not.Null);
                Assert.That(restored, Is.Not.SameAs(source));
                Assert.That(restored.Count, Is.EqualTo(2));
                Assert.That(restored["a"], Is.EqualTo(2));
                Assert.That(restored["b"], Is.EqualTo(4));
                Assert.That(restored.ContainsKey("remove"), Is.False);
                Assert.That(restored.ContainsKey("memory-only"), Is.False);
                Assert.That(restoredRecord, Is.Not.Null);
                Assert.That(restoredRecord, Is.Not.SameAs(record));
                Assert.That(restoredRecord.Direct, Is.Not.SameAs(source));
                Assert.That(restoredRecord.Direct.Count, Is.EqualTo(2));
                Assert.That(restoredRecord.Direct["a"], Is.EqualTo(2));
                Assert.That(restoredRecord.Direct["b"], Is.EqualTo(4));
                Assert.That(restoredRecord.Nested, Is.Not.SameAs(record.Nested));
                Assert.That(restoredRecord.Nested.Count, Is.EqualTo(2));
                Assert.That(restoredRecord.Nested[7], Is.Not.SameAs(record.Nested[7]));
                Assert.That(restoredRecord.Nested[7].Number, Is.EqualTo(6));
                Assert.That(restoredRecord.Nested[-2].Number, Is.EqualTo(8));
                Assert.That(SaveManager.GetSaveItemCount(), Is.EqualTo(1));
                Assert.That(SaveManager.GetSaveItemWithId(slot.SaveId), Is.Not.SameAs(slot));
                Assert.That(SaveManager.LoadObject<SerializedDictionary<string, int>>(RootFileName, slot.SaveId), Is.SameAs(restored));
                Assert.That(SaveManager.LoadObject<Record>(RecordFileName, slot.SaveId), Is.SameAs(restoredRecord));

                var unitySource = new Record { Direct = restored, Nested = restoredRecord.Nested };
                var unityJson = JsonUtility.ToJson(unitySource);
                Assert.That(JToken.DeepEquals(JObject.Parse(unityJson)["Direct"]["datas"],
                    JToken.Parse("[{\"Key\":\"a\",\"Value\":2},{\"Key\":\"b\",\"Value\":4}]")), Is.True);
                var unityRestored = JsonUtility.FromJson<Record>(unityJson);
                Assert.That(unityRestored.Direct, Is.Not.SameAs(restored));
                Assert.That(unityRestored.Direct.Count, Is.EqualTo(2));
                Assert.That(unityRestored.Direct["a"], Is.EqualTo(2));
                Assert.That(unityRestored.Direct["b"], Is.EqualTo(4));
                Assert.That(unityRestored.Nested.Count, Is.EqualTo(2));
                Assert.That(unityRestored.Nested[7].Number, Is.EqualTo(6));
                Assert.That(unityRestored.Nested[-2].Number, Is.EqualTo(8));
                passed = true;
            }
            finally
            {
                if (detached)
                {
                    SaveManager.Close();
                    rootField.SetValue(null, originalRoot);
                    storeField.SetValue(null, originalStore);
                }
                RestoreSettings(settings, originalSettings);
                SaveManager.SerializationFormat = originalFormat;
                if (ownsRoot && passed && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(tempRoot)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(tempRoot), true);
                }
                if (ownsRoot && !passed)
                {
                    Console.WriteLine("Preserved failed SerializedDictionary Save fixture: " + tempRoot);
                }
            }
            Assert.That(rootField.GetValue(null), Is.EqualTo(originalRoot));
            Assert.That(storeField.GetValue(null), Is.SameAs(originalStore));
            Assert.That(SaveManager.SerializerSettings, Is.SameAs(settings));
            Assert.That(SaveManager.SerializationFormat, Is.EqualTo(originalFormat));
            foreach (var pair in originalSettings)
            {
                Assert.That(pair.Key.GetValue(settings), Is.EqualTo(pair.Value));
            }

            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(tempRoot)), Is.False);
        }

        private static Dictionary<FieldInfo, object> CaptureSettings(JsonSerializerSettings settings)
        {
            var snapshot = new Dictionary<FieldInfo, object>();
            // 不修改实际 Save 设置，也不调用可能延迟初始化集合的 getter。
            foreach (var field in typeof(JsonSerializerSettings).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!field.IsInitOnly)
                {
                    snapshot.Add(field, field.GetValue(settings));
                }
            }
            return snapshot;
        }

        private static void RestoreSettings(JsonSerializerSettings settings, Dictionary<FieldInfo, object> snapshot)
        {
            foreach (var pair in snapshot)
            {
                pair.Key.SetValue(settings, pair.Value);
            }
        }
    }
}
