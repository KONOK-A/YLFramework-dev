using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Tests.Extensions.Shared
{
    public class E0ReorderableListAsset : ScriptableObject
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Items")]
        public List<Object> items = new List<Object>();
    }
}
