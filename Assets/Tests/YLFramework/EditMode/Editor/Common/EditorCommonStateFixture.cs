using UnityEngine;
using YLFramework.Editor.Common;

namespace YLFramework.Tests
{
    public sealed class EditorCommonStateFixture : ScriptableObject
    {
        [SerializeReference]
        public TreeViewStateGroup group;

        [SerializeReference]
        public TreeViewStateInfo info;
    }
}
