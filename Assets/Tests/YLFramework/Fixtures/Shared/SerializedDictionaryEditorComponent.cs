using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Serialization;

namespace YLFramework.Tests
{
    public enum DictionaryEditorKey
    {
        First,
        Second
    }

    [Serializable]
    public class EditorStringIntDictionary : SerializedDictionary<string, int>
    {
    }

    public class SerializedDictionaryEditorComponent : MonoBehaviour
    {
        public SerializedDictionary<string, int> Direct = new SerializedDictionary<string, int>();
        public EditorStringIntDictionary Derived = new EditorStringIntDictionary();
        public List<SerializedDictionary<string, int>> Listed = new List<SerializedDictionary<string, int>>();
        public SerializedDictionary<DictionaryEditorKey, int> EnumKeys = new SerializedDictionary<DictionaryEditorKey, int>();
    }
}
