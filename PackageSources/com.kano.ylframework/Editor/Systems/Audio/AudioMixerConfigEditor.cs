using UnityEditor;
using UnityEngine;
using YLFramework.Audio;

namespace YLFramework.Editor.Audio
{
    /// <summary>编辑分类 Mixer 路由资产；保存配置与应用全局路由是不同操作。</summary>
    [CustomEditor(typeof(AudioMixerConfig)), CanEditMultipleObjects]
    public sealed class AudioMixerConfigEditor : UnityEditor.Editor
    {
        /// <summary>以支持 Undo 的序列化方式提交 Mixer 与组引用，逐资产校验但不应用路由。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            AudioInspectorUtility.Script(serializedObject);
            AudioInspectorUtility.Field(serializedObject, "mixer");
            EditorGUILayout.LabelField("Category Routing", EditorStyles.boldLabel);
            AudioInspectorUtility.Field(serializedObject, "musicGroup");
            AudioInspectorUtility.Field(serializedObject, "voiceGroup");
            AudioInspectorUtility.Field(serializedObject, "soundGroup");
            EditorGUILayout.HelpBox("Mixer routing is optional. Each empty Group means Direct, not 'keep previous routing'. Applying this asset replaces all three category routes; editing the asset alone does not apply it.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
            foreach (var selected in targets)
            {
                using (var serialized = new SerializedObject(selected))
                {
                    var config = (AudioMixerConfig)selected;
                    AudioInspectorUtility.Validate(serialized, config.Validate);
                    if (!config.MusicGroup && !config.VoiceGroup && !config.SoundGroup)
                    {
                        AudioInspectorUtility.Message(serialized, "All Groups are empty. Apply clears all category Mixer routing to Direct.", MessageType.Warning);
                    }
                }
            }
        }
    }
}
