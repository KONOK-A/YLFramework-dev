using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Foundation.Tests;
using Object = UnityEngine.Object;
using SaveManager = YLFramework.Save.SaveManager;
using YLFramework.Serialization;

namespace YLFramework.Tests
{
    public class SerializedDictionaryJsonTests
    {
        public enum SampleKey
        {
            First,
            Second
        }

        [Serializable]
        public class Record
        {
            public SerializedDictionary<string, Payload> Entries = new SerializedDictionary<string, Payload>();
        }

        [Serializable]
        public class Payload
        {
            public int Number;
        }

        [Test]
        [Category("YL.Module.SaveBcl")]
        public void DefaultJsonContractIsBareDictionaryAndReadsBack()
        {
            var source = new SerializedDictionary<string, int>();
            source.Add("a", 1);
            var json = JsonConvert.SerializeObject(source);
            Assert.That(json, Is.EqualTo("{\"a\":1}"));
            var restored = JsonConvert.DeserializeObject<SerializedDictionary<string, int>>(json);
            Assert.That(restored["a"], Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(restored), Is.EqualTo(json));
        }

        [Test]
        [Category("YL.Module.SaveBcl")]
        public void NestedRecordUsesActualSaveSettingsWithoutChangingOrCallingSaveManager()
        {
            var source = new Record();
            source.Entries.Add("first", new Payload { Number = 1 });
            source.Entries["first"] = new Payload { Number = 2 };
            source.Entries["second"] = null;
            var json = JsonConvert.SerializeObject(source, SaveManager.SerializerSettings);
            Assert.That(JToken.DeepEquals(JToken.Parse(json), JToken.Parse("{\"Entries\":{\"first\":{\"Number\":2},\"second\":null}}")), Is.True);
            var restored = JsonConvert.DeserializeObject<Record>(json, SaveManager.SerializerSettings);
            Assert.That(restored.Entries["first"].Number, Is.EqualTo(2));
            Assert.That(restored.Entries.TryGetValue("second", out var value), Is.True);
            Assert.That(value, Is.Null);
            Assert.That(JsonConvert.SerializeObject(restored, SaveManager.SerializerSettings), Is.EqualTo(json));
        }

        [Test]
        [Category("YL.Module.SaveBcl")]
        public void IntegerAndEnumKeysRoundTripAsJsonPropertyNames()
        {
            var integers = new SerializedDictionary<int, int> { { 0, 1 }, { -2, 3 } };
            var json = JsonConvert.SerializeObject(integers);
            Assert.That(json, Is.EqualTo("{\"0\":1,\"-2\":3}"));
            var restored = JsonConvert.DeserializeObject<SerializedDictionary<int, int>>(json);
            Assert.That(restored[0], Is.EqualTo(1));
            Assert.That(restored[-2], Is.EqualTo(3));
            Assert.That(JsonConvert.SerializeObject(restored), Is.EqualTo(json));
            var enums = new SerializedDictionary<SampleKey, int> { { SampleKey.Second, 4 } };
            json = JsonConvert.SerializeObject(enums);
            Assert.That(json, Is.EqualTo("{\"Second\":4}"));
            var enumCopy = JsonConvert.DeserializeObject<SerializedDictionary<SampleKey, int>>(json);
            Assert.That(enumCopy[SampleKey.Second], Is.EqualTo(4));
            Assert.That(JsonConvert.SerializeObject(enumCopy), Is.EqualTo(json));
        }

        [Test]
        [Category("YL.Module.SaveBcl")]
        public void JsonDuplicateNamesAndConvertedKeysUseLastValueWhileBadInputThrows()
        {
            var restored = JsonConvert.DeserializeObject<SerializedDictionary<string, int>>("{\"a\":1,\"a\":2}");
            Assert.That(restored.Count, Is.EqualTo(1));
            Assert.That(restored["a"], Is.EqualTo(2));
            Assert.That(restored.InvalidEntryCount, Is.Zero);
            var integers = JsonConvert.DeserializeObject<SerializedDictionary<int, int>>("{\"1\":1,\"01\":2}");
            Assert.That(integers.Count, Is.EqualTo(1));
            Assert.That(integers[1], Is.EqualTo(2));
            Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<SerializedDictionary<int, int>>("{\"bad\":1}"));
            Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<SerializedDictionary<string, int>>("{\"a\":1"));
            Assert.That(JsonConvert.SerializeObject(new SerializedDictionary<string, int>()), Is.EqualTo("{}"));
            Assert.That(JsonConvert.DeserializeObject<SerializedDictionary<string, int>>("{}").Count, Is.Zero);
        }

        [Test]
        public void JsonReadAndMutationWriteThroughToRealUnitySerialization()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var restored = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                asset.Direct = JsonConvert.DeserializeObject<SerializedDictionary<string, int>>("{\"a\":1,\"remove\":2}");
                asset.Direct["a"] = 3;
                asset.Direct.Remove("remove");
                asset.Direct.Add("b", 4);
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(asset), restored);
                Assert.That(JsonConvert.SerializeObject(restored.Direct), Is.EqualTo("{\"a\":3,\"b\":4}"));
                restored.Direct.Clear();
                Assert.That(JsonConvert.SerializeObject(restored.Direct), Is.EqualTo("{}"));
            }
            finally
            {
                Object.DestroyImmediate(asset);
                Object.DestroyImmediate(restored);
            }
        }

        [Test]
        public void UnityDuplicateRowsExportOnlyValidProjectionWithoutCleaningRawData()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"Direct\":{\"datas\":[{\"Key\":\"a\",\"Value\":1},{\"Key\":\"a\",\"Value\":2}]}}", asset);
                Assert.That(JsonConvert.SerializeObject(asset.Direct), Is.EqualTo("{\"a\":1}"));
                Assert.That(asset.Direct.InvalidEntryCount, Is.EqualTo(1));
                Assert.That(JsonUtility.ToJson(asset), Does.Contain("{\"Key\":\"a\",\"Value\":2}"));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
