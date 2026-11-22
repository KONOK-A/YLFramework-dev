using UnityEditor;

namespace YLFramework.Editor.Resource
{
    [CustomEditor(typeof(ResourceAtlasConfiguration))]
    internal sealed class ResourceAtlasConfigurationEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Legacy Atlas migration input only. New atlases are Unity SpriteAtlas assets registered as official Addressables entries.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(true))
            {
                DrawDefaultInspector();
            }
        }
    }
}
