using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using YLFramework.Serialization;

namespace YLFramework.Foundation.Tests
{
    public class SerializedDictionaryContractTests
    {
        [Test]
        public void StandardInterfacesRejectIllegalWritesAndDistinguishMissingFromDefault()
        {
            var dictionary = new SerializedDictionary<string, string>();
            IDictionary<string, string> mutable = dictionary;
            IReadOnlyDictionary<string, string> readOnly = dictionary;
            mutable.Add("", null);
            Assert.That(dictionary.TryAdd("", "replacement"), Is.False);
            Assert.Throws<ArgumentException>(() => mutable.Add("", "replacement"));
            Assert.Throws<ArgumentNullException>(() => mutable.Add(null, "value"));
            Assert.Throws<ArgumentNullException>(() => dictionary.TryAdd(null, "value"));
            Assert.Throws<ArgumentNullException>(() => dictionary.ContainsKey(null));
            Assert.Throws<ArgumentNullException>(() => dictionary.Remove((string)null));
            Assert.Throws<ArgumentNullException>(() => dictionary[null] = "value");
            Assert.Throws<ArgumentNullException>(() => dictionary.TryGetValue(null, out _));
            Assert.Throws<ArgumentNullException>(() =>
            {
                var ignored = dictionary[null];
            });
            Assert.Throws<KeyNotFoundException>(() =>
            {
                var ignored = mutable["missing"];
            });
            Assert.That(dictionary.TryGetValue("missing", out var missing), Is.False);
            Assert.That(missing, Is.Null);
            Assert.That(dictionary.TryGetValue("", out var present), Is.True);
            Assert.That(present, Is.Null);
            Assert.That(readOnly[""], Is.Null);
            Assert.That(dictionary.ContainsValue(null), Is.True);
            Assert.That(dictionary.Count, Is.EqualTo(1));
            Assert.That(((ICollection<KeyValuePair<string, string>>)mutable).IsReadOnly, Is.False);
        }

        [Test]
        public void ConstructorCopiesSourceAndRejectsNullOrDuplicateKeys()
        {
            var source = new Dictionary<int, string> { [0] = "original" };
            var copy = new SerializedDictionary<int, string>(source);
            source[0] = "external";
            Assert.That(copy[0], Is.EqualTo("original"));
            copy[0] = "local";
            Assert.That(source[0], Is.EqualTo("external"));
            Assert.Throws<ArgumentNullException>(() => new SerializedDictionary<int, string>(null));
            Assert.Throws<ArgumentException>(() => new SerializedDictionary<int, string>(new[]
            {
                new KeyValuePair<int, string>(0, "first"), new KeyValuePair<int, string>(0, "second")
            }));
        }

        [Test]
        public void PairOperationsCopyAndLiveViewsUseValidOrderedProjection()
        {
            var dictionary = new SerializedDictionary<string, int>();
            var keys = dictionary.Keys;
            var values = dictionary.Values;
            ICollection<KeyValuePair<string, int>> pairs = dictionary;
            pairs.Add(new KeyValuePair<string, int>("a", 1));
            dictionary["b"] = 2;
            dictionary["a"] = 3;
            Assert.That(keys, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(values, Is.EqualTo(new[] { 3, 2 }));
            Assert.That(keys.IsReadOnly && values.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => keys.Add("c"));
            Assert.Throws<NotSupportedException>(() => values.Remove(3));
            Assert.Throws<NotSupportedException>(() => keys.Clear());
            Assert.That(keys.Contains("a"), Is.True);
            Assert.That(values.Contains(3), Is.True);
            Assert.That(pairs.Contains(new KeyValuePair<string, int>("a", 1)), Is.False);
            Assert.That(pairs.Remove(new KeyValuePair<string, int>("a", 1)), Is.False);
            var target = new KeyValuePair<string, int>[3];
            pairs.CopyTo(target, 1);
            Assert.That(target.Skip(1), Is.EqualTo(new[]
            {
                new KeyValuePair<string, int>("a", 3), new KeyValuePair<string, int>("b", 2)
            }));
            var keyTarget = new string[2];
            keys.CopyTo(keyTarget, 0);
            Assert.That(keyTarget, Is.EqualTo(new[] { "a", "b" }));
            Assert.Throws<ArgumentNullException>(() => pairs.CopyTo(null, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => pairs.CopyTo(target, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => values.CopyTo(new int[2], 3));
            Assert.Throws<ArgumentException>(() => pairs.CopyTo(target, 2));
            Assert.That(pairs.Remove(new KeyValuePair<string, int>("a", 3)), Is.True);
            Assert.That(keys, Is.EqualTo(new[] { "b" }));
            Assert.That(values, Is.EqualTo(new[] { 2 }));
            Assert.That(((IEnumerable)dictionary).Cast<KeyValuePair<string, int>>(), Is.EqualTo(dictionary.ToArray()));
            dictionary.Clear();
            Assert.That(keys.Count, Is.Zero);
            Assert.That(values.Count, Is.Zero);
        }

        [Test]
        public void MutationInvalidatesEnumeratorsEvenBeforeFirstMoveNextButRejectedWritesDoNot()
        {
            var dictionary = new SerializedDictionary<string, int>();
            dictionary.Add("a", 1);
            var pairs = dictionary.GetEnumerator();
            var keys = dictionary.Keys.GetEnumerator();
            var values = dictionary.Values.GetEnumerator();
            Assert.That(dictionary.TryAdd("a", 9), Is.False);
            Assert.That(dictionary.Remove("missing"), Is.False);
            Assert.That(pairs.MoveNext(), Is.True);
            dictionary["a"] = 2;
            Assert.Throws<InvalidOperationException>(() => pairs.MoveNext());
            Assert.Throws<InvalidOperationException>(() => keys.MoveNext());
            Assert.Throws<InvalidOperationException>(() => values.MoveNext());
            var empty = new SerializedDictionary<int, int>();
            var enumerator = empty.GetEnumerator();
            empty.Add(0, 0);
            Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
        }

        [Test]
        public void ExhaustedEnumeratorsStillDetectSubsequentMutation()
        {
            var dictionary = new SerializedDictionary<string, int>();
            var pairs = dictionary.GetEnumerator();
            var keys = dictionary.Keys.GetEnumerator();
            var values = dictionary.Values.GetEnumerator();
            Assert.That(pairs.MoveNext(), Is.False);
            Assert.That(keys.MoveNext(), Is.False);
            Assert.That(values.MoveNext(), Is.False);
            dictionary.Add("a", 1);
            Assert.Throws<InvalidOperationException>(() => pairs.MoveNext());
            Assert.Throws<InvalidOperationException>(() => keys.MoveNext());
            Assert.Throws<InvalidOperationException>(() => values.MoveNext());
        }

        [Test]
        public void RawNullAndDuplicateRowsArePreservedAndRemovePreventsResurrection()
        {
            var dictionary = new SerializedDictionary<string, int>();
            var raw = CreateRawRows(dictionary,
                null,
                new KeyValuePair<string, int>(null, 9),
                new KeyValuePair<string, int>("a", 1),
                new KeyValuePair<string, int>("a", 2),
                new KeyValuePair<string, int>("b", 3));
            Assert.That(dictionary.Count, Is.EqualTo(2));
            Assert.That(dictionary.InvalidEntryCount, Is.EqualTo(3));
            Assert.That(dictionary.Values, Is.EqualTo(new[] { 1, 3 }));
            Assert.That(raw.Count, Is.EqualTo(5));
            dictionary["a"] = 4;
            Assert.That(raw[2].GetType().GetField("Value").GetValue(raw[2]), Is.EqualTo(4));
            Assert.That(raw[3].GetType().GetField("Value").GetValue(raw[3]), Is.EqualTo(2));
            Assert.That(dictionary.ContainsValue(2), Is.False);
            Assert.That(dictionary.Remove(new KeyValuePair<string, int>("a", 2)), Is.False);
            Assert.That(dictionary.Remove("a"), Is.True);
            Assert.That(dictionary.ContainsKey("a"), Is.False);
            Assert.That(raw.Count, Is.EqualTo(3));
            Assert.That(dictionary.InvalidEntryCount, Is.EqualTo(2));
            dictionary.Clear();
            Assert.That(raw, Is.Empty);
            Assert.That(dictionary.InvalidEntryCount, Is.Zero);
            Assert.That(new SerializedDictionary<string, int>().Count, Is.Zero);
        }

        [Test]
        public void DestroyedUnityObjectKeyIsNotTreatedAsClrNull()
        {
            var key = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var dictionary = new SerializedDictionary<UnityEngine.Object, int>();
            Object.DestroyImmediate(key);
            Assert.That(key == null, Is.True);
            Assert.That(dictionary.TryAdd(key, 1), Is.True);
            Assert.That(dictionary[key], Is.EqualTo(1));
            Assert.That(dictionary.InvalidEntryCount, Is.Zero);
            Assert.That(dictionary.Remove(key), Is.True);
        }

        [Test]
        public void IndexerWritesSurviveRealUnityJsonRoundTrip()
        {
            var source = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var restored = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                source.Direct.Add("a", 1);
                source.Direct["a"] = 2;
                source.Direct["b"] = 3;
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), restored);
                Assert.That(restored.Direct["a"], Is.EqualTo(2));
                Assert.That(restored.Direct["b"], Is.EqualTo(3));
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(restored);
            }
        }

        [TestCase("Direct")]
        [TestCase("Derived")]
        public void SerializedPropertySameCountEditInvalidatesExistingIndex(string field)
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                var dictionary = field == "Direct" ? asset.Direct : asset.Derived;
                dictionary.Add("a", 1);
                Assert.That(dictionary["a"], Is.EqualTo(1));
                var serialized = new SerializedObject(asset);
                var row = serialized.FindProperty(field + ".datas").GetArrayElementAtIndex(0);
                row.FindPropertyRelative("Key").stringValue = "b";
                row.FindPropertyRelative("Value").intValue = 2;
                Assert.That(serialized.ApplyModifiedPropertiesWithoutUndo(), Is.True);
                Assert.That(dictionary.ContainsKey("a"), Is.False);
                Assert.That(dictionary["b"], Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void UnityOverwriteSameCountInvalidatesExistingIndex()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                asset.Direct.Add("a", 1);
                Assert.That(asset.Direct["a"], Is.EqualTo(1));
                var enumerator = asset.Direct.GetEnumerator();
                var keys = asset.Direct.Keys;
                var values = asset.Direct.Values;
                var keyEnumerator = keys.GetEnumerator();
                var valueEnumerator = values.GetEnumerator();
                JsonUtility.FromJsonOverwrite("{\"Direct\":{\"datas\":[{\"Key\":\"b\",\"Value\":2}]}}", asset);
                Assert.That(asset.Direct.ContainsKey("a"), Is.False);
                Assert.That(asset.Direct["b"], Is.EqualTo(2));
                Assert.That(keys, Is.EqualTo(new[] { "b" }));
                Assert.That(values, Is.EqualTo(new[] { 2 }));
                Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
                Assert.Throws<InvalidOperationException>(() => keyEnumerator.MoveNext());
                Assert.Throws<InvalidOperationException>(() => valueEnumerator.MoveNext());
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [UnityTest]
        public IEnumerator SerializedPropertyKeyValueEditAndUndoRedoRefreshProjection()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                asset.Direct.Add("a", 1);
                Assert.That(asset.Direct["a"], Is.EqualTo(1));
                Undo.IncrementCurrentGroup();
                var serialized = new SerializedObject(asset);
                var row = serialized.FindProperty("Direct.datas").GetArrayElementAtIndex(0);
                row.FindPropertyRelative("Key").stringValue = "b";
                row.FindPropertyRelative("Value").intValue = 2;
                serialized.ApplyModifiedProperties();
                Undo.FlushUndoRecordObjects();
                Assert.That(asset.Direct.ContainsKey("a"), Is.False);
                Assert.That(asset.Direct["b"], Is.EqualTo(2));
                Undo.PerformUndo();
                yield return null;
                Assert.That(asset.Direct["a"], Is.EqualTo(1));
                Assert.That(asset.Direct.ContainsKey("b"), Is.False);
                Undo.PerformRedo();
                yield return null;
                Assert.That(asset.Direct.ContainsKey("a"), Is.False);
                Assert.That(asset.Direct["b"], Is.EqualTo(2));
            }
            finally
            {
                Undo.ClearUndo(asset);
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void UnityDuplicateRowsSurviveSerializationAndCanBeRepairedWithoutRefresh()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var clone = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"Direct\":{\"datas\":[{\"Key\":\"a\",\"Value\":1},{\"Key\":\"a\",\"Value\":2}]}}", asset);
                Assert.That(asset.Direct["a"], Is.EqualTo(1));
                Assert.That(asset.Direct.InvalidEntryCount, Is.EqualTo(1));
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(asset), clone);
                Assert.That(clone.Direct.Count, Is.EqualTo(1));
                Assert.That(clone.Direct.InvalidEntryCount, Is.EqualTo(1));
                var serialized = new SerializedObject(clone);
                var rows = serialized.FindProperty("Direct.datas");
                Assert.That(rows.arraySize, Is.EqualTo(2));
                rows.GetArrayElementAtIndex(1).FindPropertyRelative("Key").stringValue = "b";
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(clone.Direct.Keys, Is.EqualTo(new[] { "a", "b" }));
                Assert.That(clone.Direct["b"], Is.EqualTo(2));
                Assert.That(clone.Direct.InvalidEntryCount, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(asset);
                Object.DestroyImmediate(clone);
            }
        }

        [Test]
        public void RuntimeEditsSurviveAssetSaveUnloadReloadAndInstantiate()
        {
            var path = "Assets/__SerializedDictionaryTest_" + Guid.NewGuid().ToString("N") + ".asset";
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            SerializedDictionaryTestAsset restored = null;
            SerializedDictionaryTestAsset clone = null;
            try
            {
                AssetDatabase.CreateAsset(asset, path);
                asset.Direct.Add("remove", 1);
                asset.Direct.Add("a", 1);
                asset.Direct["a"] = 2;
                asset.Direct["b"] = 3;
                asset.Direct.Remove("remove");
                asset.Derived.Add("clear", 1);
                asset.Derived.Clear();
                asset.Derived.Add("new", 4);
                asset.Nested.Add(0, new DictionaryTestValue { Number = 5, Reference = asset });
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssetIfDirty(asset);
                Resources.UnloadAsset(asset);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                restored = AssetDatabase.LoadAssetAtPath<SerializedDictionaryTestAsset>(path);
                Assert.That(ReferenceEquals(restored, asset), Is.False);
                Assert.That(restored.Direct.Keys, Is.EqualTo(new[] { "a", "b" }));
                Assert.That(restored.Direct.Values, Is.EqualTo(new[] { 2, 3 }));
                Assert.That(restored.Derived.Keys, Is.EqualTo(new[] { "new" }));
                Assert.That(restored.Derived["new"], Is.EqualTo(4));
                Assert.That(restored.Nested[0].Number, Is.EqualTo(5));
                Assert.That(restored.Nested[0].Reference, Is.EqualTo(restored));
                clone = Object.Instantiate(restored);
                Assert.That(clone.Direct["a"], Is.EqualTo(2));
                clone.Direct.Clear();
                Assert.That(restored.Direct.Count, Is.EqualTo(2));
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(clone), restored);
                Assert.That(restored.Direct.Count, Is.Zero);
            }
            finally
            {
                if (clone != null)
                {
                    Object.DestroyImmediate(clone);
                }

                AssetDatabase.DeleteAsset(path);
                if (asset != null)
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        [Test]
        public void PreMigrationAssetRetainsDataAndReferencesAfterNamespaceChange()
        {
            const string Path = "Assets/YLFramework/Tests/Fixtures/NamespaceMigration/SerializedDictionaryBefore.asset";
            Assert.That(AssetDatabase.AssetPathToGUID(Path), Is.EqualTo("c920a96e550145e47b63934ef3cc76ea"));
            var asset = AssetDatabase.LoadAssetAtPath<SerializedDictionaryTestAsset>(Path);
            Assert.That(asset, Is.Not.Null);
            try
            {
                Assert.That(asset.Direct.GetType().Namespace, Is.EqualTo("YLFramework.Serialization"));
                Assert.That(asset.Direct.Keys, Is.EqualTo(new[] { "alpha", "omega" }));
                Assert.That(asset.Direct.Values, Is.EqualTo(new[] { 7, 11 }));
                Assert.That(asset.Derived["persisted"], Is.EqualTo(13));
                Assert.That(asset.Nested[42].Number, Is.EqualTo(17));
                Assert.That(asset.Nested[42].Reference, Is.EqualTo(asset));
                Assert.That(asset.ObjectKeys[asset], Is.EqualTo(19));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(asset))),
                    Is.EqualTo("da82f70412bbaab439cd654edba33760"));
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
        }

        [Test]
        public void FoundationIdentityPreservesGuidAndUnitySerializedFieldLayoutWithoutPublicEntryType()
        {
            var type = typeof(SerializedDictionary<string, int>);
            Assert.That(type.Namespace, Is.EqualTo("YLFramework.Serialization"));
            Assert.That(type.Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            Assert.That(AssetDatabase.AssetPathToGUID("Assets/YLFramework/Runtime/Foundation/Serialization/SerializedDictionary.cs"),
                Is.EqualTo("1418cb45915d1504e859f117b32ade29"));
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => !f.IsNotSerialized && (f.IsPublic || f.IsDefined(typeof(SerializeField), false))).ToArray();
            Assert.That(fields.Select(f => f.Name), Is.EqualTo(new[] { "datas" }));
            Assert.That(fields[0].FieldType.GetGenericTypeDefinition(), Is.EqualTo(typeof(List<>)));
            var entry = fields[0].FieldType.GetGenericArguments()[0];
            Assert.That(entry.FullName, Does.StartWith("YLFramework.Serialization.DictionaryItem`2"));
            Assert.That(entry.IsVisible, Is.False);
            Assert.That(entry.GetFields().Select(f => f.Name), Is.EqualTo(new[] { "Key", "Value" }));
            Assert.That(entry.GetField("Key").FieldType, Is.EqualTo(typeof(string)));
            Assert.That(entry.GetField("Value").FieldType, Is.EqualTo(typeof(int)));
            var references = type.Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
            Assert.That(references, Does.Not.Contain("YLFramework.Foundation.Runtime"));
            Assert.That(references, Does.Not.Contain("YLFramework.Core.Runtime"));
            var allowedReferences = new[] { "netstandard", "mscorlib", "System", "Microsoft.CSharp", "Microsoft.Win32.Primitives",
                "UnityEngine", "Newtonsoft.Json", "BouncyCastle.Cryptography" };
            Assert.That(references.All(name => allowedReferences.Contains(name) ||
                name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                name.StartsWith("UnityEditor.", StringComparison.Ordinal)),
                Is.True, string.Join(", ", references));
            // 中立源码闭包另行验证；主体只接受批准的资源依赖，Player 必须保持 Editor 隔离。
            var player = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                .Single(a => a.name == "YLFramework.Runtime");
            var playerReferences = player.assemblyReferences.Select(a => a.name)
                .Concat(player.compiledAssemblyReferences.Select(Path.GetFileNameWithoutExtension)).ToArray();
            Assert.That(playerReferences.All(name => allowedReferences.Contains(name) ||
                name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("UnityEngine.", StringComparison.Ordinal)),
                Is.True, string.Join(", ", playerReferences));
        }

        [Test]
        public void UnitySerializationKeepsFakeNullObjectKeysAndPreservesDuplicateRows()
        {
            var source = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            var restored = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                CreateRawRows(source.ObjectKeys,
                    new KeyValuePair<UnityEngine.Object, int>(null, 9),
                    new KeyValuePair<UnityEngine.Object, int>(source, 1),
                    new KeyValuePair<UnityEngine.Object, int>(source, 2));
                Assert.That(source.ObjectKeys.InvalidEntryCount, Is.EqualTo(2));
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), restored);
                Assert.That(restored.ObjectKeys.Count, Is.EqualTo(2));
                Assert.That(restored.ObjectKeys[source], Is.EqualTo(1));
                Assert.That(restored.ObjectKeys.InvalidEntryCount, Is.EqualTo(1));
                var restoredNullKey = restored.ObjectKeys.Keys.First();
                Assert.That(ReferenceEquals(restoredNullKey, null), Is.False);
                Assert.That(restoredNullKey == null, Is.True);
                Assert.That(restored.ObjectKeys[restoredNullKey], Is.EqualTo(9));
                var serialized = new SerializedObject(restored);
                Assert.That(serialized.FindProperty("ObjectKeys.datas").arraySize, Is.EqualTo(3));
                restored.ObjectKeys.Remove(source);
                Assert.That(restored.ObjectKeys.Count, Is.EqualTo(1));
                Assert.That(restored.ObjectKeys.InvalidEntryCount, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(restored);
            }
        }

        // 条目已内部化：仅在测试内通过字段布局注入 Unity inline 无法表达的 CLR null 行。
        private static IList CreateRawRows<TKey, TValue>(SerializedDictionary<TKey, TValue> dictionary,
            params KeyValuePair<TKey, TValue>?[] entries)
        {
            var field = typeof(SerializedDictionary<TKey, TValue>).GetField("datas", BindingFlags.Instance | BindingFlags.NonPublic);
            var raw = (IList)Activator.CreateInstance(field.FieldType);
            var entryType = field.FieldType.GetGenericArguments()[0];
            foreach (var pair in entries)
            {
                if (!pair.HasValue)
                {
                    raw.Add(null);
                    continue;
                }
                var entry = Activator.CreateInstance(entryType);
                entryType.GetField("Key").SetValue(entry, pair.Value.Key);
                entryType.GetField("Value").SetValue(entry, pair.Value.Value);
                raw.Add(entry);
            }
            field.SetValue(dictionary, raw);
            return raw;
        }

        [Test]
        public void ValueOnlyEditInvalidatesExistingEnumeratorWithoutReordering()
        {
            var asset = ScriptableObject.CreateInstance<SerializedDictionaryTestAsset>();
            try
            {
                asset.Direct.Add("a", 1);
                var enumerator = asset.Direct.GetEnumerator();
                var serialized = new SerializedObject(asset);
                serialized.FindProperty("Direct.datas").GetArrayElementAtIndex(0)
                    .FindPropertyRelative("Value").intValue = 2;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(asset.Direct["a"], Is.EqualTo(2));
                Assert.That(asset.Direct.Keys, Is.EqualTo(new[] { "a" }));
                Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
