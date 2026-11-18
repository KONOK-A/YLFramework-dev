using UnityEditor;

namespace YLFramework.Editor.Resource
{
    [CustomEditor(typeof(ResourceBuildRecipe))]
    internal sealed class ResourceBuildRecipeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Legacy migration input only. Use official Addressables Settings, Groups and Profiles for new content builds.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(true))
            {
                DrawDefaultInspector();
            }
        }
    }

    [CustomEditor(typeof(ResourceProject))]
    internal sealed class ResourceProjectEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Legacy migration input only. This asset is not a current Addressables authoring authority.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(true))
            {
                DrawDefaultInspector();
            }
        }
    }
}
