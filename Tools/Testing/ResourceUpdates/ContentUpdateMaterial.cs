using UnityEngine;
using UnityEngine.Scripting;

namespace YLFramework.Testing
{
    [Preserve]
    public sealed class ContentUpdateMaterial : ScriptableObject
    {
        public string Value;
        public TextAsset Dependency;
        public TextAsset OrdinaryDependency;
    }
}
