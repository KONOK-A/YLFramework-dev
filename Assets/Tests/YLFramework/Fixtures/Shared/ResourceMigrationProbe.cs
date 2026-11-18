using System;
using UnityEngine;

namespace YLFramework.Tests.Fixtures
{
    public sealed class ResourceMigrationProbe : ScriptableObject
    {
        public enum SourceMode
        {
            Direct = 0,
            Resource = 4
        }

        [Serializable]
        public sealed class Entry
        {
            public string address;
            public string[] aliases;
            public UnityEngine.Object reference;
        }

        public SourceMode mode = SourceMode.Resource;
        public Vector3 offset = new Vector3(1, 2, 3);
        public Entry[] entries = Array.Empty<Entry>();
    }
}
