using System;
using UnityEngine;
using YLFramework.Serialization;

namespace YLFramework.Foundation.Tests
{
    [Serializable]
    public class StringIntDictionary : SerializedDictionary<string, int>
    {
    }

    [Serializable]
    public class DictionaryTestValue
    {
        public int Number;
        public UnityEngine.Object Reference;
    }

    public class SerializedDictionaryTestAsset : ScriptableObject
    {
        public SerializedDictionary<string, int> Direct = new SerializedDictionary<string, int>();
        public StringIntDictionary Derived = new StringIntDictionary();
        public SerializedDictionary<int, DictionaryTestValue> Nested = new SerializedDictionary<int, DictionaryTestValue>();
        public SerializedDictionary<UnityEngine.Object, int> ObjectKeys = new SerializedDictionary<UnityEngine.Object, int>();
    }
}
