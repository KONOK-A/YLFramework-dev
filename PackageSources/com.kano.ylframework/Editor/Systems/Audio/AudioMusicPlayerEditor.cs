using System;
using UnityEditor;
using UnityEngine;
using YLFramework.Audio;

namespace YLFramework.Editor.Audio
{
    /// <summary>编辑音乐组件的素材与可选过渡配置，不在检查过程中启动或停止音乐。</summary>
    [CustomEditor(typeof(AudioMusicPlayer)), CanEditMultipleObjects]
    public sealed class AudioMusicPlayerEditor : UnityEditor.Editor
    {
        private bool _showAdvanced;

        /// <summary>绘制音乐配置，通过序列化提交保留 Undo 和 Prefab Override，再逐对象校验已启用的选项。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            AudioInspectorUtility.Script(serializedObject);
            AudioInspectorUtility.Field(serializedObject, "clip");
            AudioInspectorUtility.Field(serializedObject, "volume", "Volume Multiplier");
            AudioInspectorUtility.Field(serializedObject, "loop");
            AudioInspectorUtility.Field(serializedObject, "playOnAwake", "Play On First Start");
            var configured = false;
            foreach (var selected in targets)
            {
                using (var serialized = new SerializedObject(selected))
                {
                    configured |= AudioInspectorUtility.HasRouteOrFade(serialized) || serialized.FindProperty("crossFadeOnPlay").boolValue ||
                        serialized.FindProperty("crossFadeDuration").floatValue != 1f || !serialized.FindProperty("crossFadeIgnoreTimeScale").boolValue ||
                        serialized.FindProperty("crossFadeCurve").intValue != (int)AudioCrossfadeCurve.EqualPower;
                }
            }
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, configured ? "Advanced (Configured)" : "Advanced", true);
            if (configured && !_showAdvanced)
            {
                EditorGUILayout.HelpBox("Advanced values are configured. Expand Advanced to inspect routing, fades and transitions; folding does not reset values.", MessageType.Info);
            }

            if (_showAdvanced)
            {
                AudioInspectorUtility.Route(serializedObject);
                AudioInspectorUtility.Fade(serializedObject);
                AudioInspectorUtility.Field(serializedObject, "crossFadeOnPlay", "Crossfade On Play");
                if (AudioInspectorUtility.ShowWhen(serializedObject.FindProperty("crossFadeOnPlay")))
                {
                    AudioInspectorUtility.Field(serializedObject, "crossFadeDuration", "Transition Seconds");
                    AudioInspectorUtility.Field(serializedObject, "crossFadeIgnoreTimeScale", "Use Unscaled Time");
                    AudioInspectorUtility.Field(serializedObject, "crossFadeCurve", "Transition Curve");
                }
            }
            serializedObject.ApplyModifiedProperties();
            foreach (var selected in targets)
            {
                using (var serialized = new SerializedObject(selected))
                {
                    AudioInspectorUtility.RouteWarnings(serialized);
                    var clip = serialized.FindProperty("clip").objectReferenceValue as AudioClip;
                    if (!clip)
                    {
                        AudioInspectorUtility.Message(serialized, "Assign a Clip to play. An empty Clip does not stop existing music.", MessageType.Warning);
                    }

                    AudioInspectorUtility.Validate(serialized, () =>
                    {
                        var options = AudioInspectorUtility.Options(serialized);
                        options.Validate(false);
                        if (clip)
                        {
                            options.ValidateClip(clip);
                        }

                        if (!serialized.FindProperty("crossFadeOnPlay").boolValue)
                        {
                            return;
                        }

                        var duration = serialized.FindProperty("crossFadeDuration").floatValue;
                        if (!AudioSoundOptions.IsFinite(duration) || duration < 0f)
                        {
                            throw new ArgumentOutOfRangeException("crossFadeDuration");
                        }

                        if (!Enum.IsDefined(typeof(AudioCrossfadeCurve), serialized.FindProperty("crossFadeCurve").intValue))
                        {
                            throw new ArgumentOutOfRangeException("crossFadeCurve");
                        }
                    });
                }
            }
        }
    }
}
