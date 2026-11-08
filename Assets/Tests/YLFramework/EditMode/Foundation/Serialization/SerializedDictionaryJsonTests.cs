using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Foundation.Tests;
using Object = UnityEngine.Object;
using YLFramework.Serialization;

namespace YLFramework.Tests
{
    [Category("YL.Module.UnityJson")]
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
        public void JsonNetTreeMutationAndCloneRoundTripWithoutSharingChildren()
        {
            var tree = JObject.Parse("{\"score\":10,\"items\":[true,null],\"obsolete\":1}");
            tree["score"] = tree.Value<int>("score") + 1;
            tree.Remove("obsolete");
            ((JArray)tree["items"]).Add(new JObject { ["name"] = "player" });
            tree["optional"] = JValue.CreateNull();
            var expected = JToken.Parse("{\"score\":11,\"items\":[true,null,{\"name\":\"player\"}],\"optional\":null}");
            Assert.That(JToken.DeepEquals(tree, expected), Is.True);
            var copy = tree.DeepClone();
            copy["items"][2]["name"] = "copy";
            Assert.That((string)tree["items"][2]["name"], Is.EqualTo("player"));
            Assert.That(JToken.DeepEquals(tree, JToken.Parse(tree.ToString(Formatting.None))), Is.True);
        }

        [TestCase("{}", JTokenType.Object)]
        [TestCase("[]", JTokenType.Array)]
        [TestCase("\"text\"", JTokenType.String)]
        [TestCase("9223372036854775807", JTokenType.Integer)]
        [TestCase("-9223372036854775808", JTokenType.Integer)]
        [TestCase("1.25", JTokenType.Float)]
        [TestCase("true", JTokenType.Boolean)]
        [TestCase("null", JTokenType.Null)]
        public void JsonNetTreePreservesContainerScalarAndNullKinds(string json, JTokenType kind)
        {
            var value = JToken.Parse(json);
            Assert.That(value.Type, Is.EqualTo(kind));
            Assert.That(JToken.DeepEquals(value, JToken.Parse(value.ToString(Formatting.None))), Is.True);
        }

        [Test]
        public void JsonNetTreePreservesUnicodeEscapesAndExplicitNullWithoutDeletingTheProperty()
        {
            var tree = JObject.Parse("{\"text\":\"\\u4e2d\\u6587\\n\\\"\\\\\"}");
            Assert.That((string)tree["text"], Is.EqualTo("\u4e2d\u6587\n\"\\"));
            tree["text"] = JValue.CreateNull();
            Assert.That(tree.ContainsKey("text"), Is.True);
            Assert.That(tree["text"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(tree.Remove("text"), Is.True);
            Assert.That(tree.ContainsKey("text"), Is.False);
        }

        [Test]
        public void JsonNetDuplicateNamesAreExplicitlyReplaceableOrRejected()
        {
            const string json = "{\"value\":1,\"value\":2}";
            Assert.That(JObject.Parse(json).Value<int>("value"), Is.EqualTo(2));
            Assert.Throws<JsonReaderException>(() => JObject.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            }));
        }

        [TestCase("{\"value\":1")]
        [TestCase("[1,2")]
        [TestCase("{\"value\":}")]
        [TestCase("{} trailing")]
        public void InvalidJsonCannotBeMistakenForAnEmptyOrPartiallySuccessfulTree(string json)
        {
            Assert.Throws<JsonReaderException>(() => JToken.Parse(json));
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
