using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.Audio;

namespace YLFramework.Editor.Audio
{
    /// <summary>编辑 AudioEmitter 的素材候选和播放配置，保留隐藏字段并显示逐对象诊断。</summary>
    [CustomEditor(typeof(AudioEmitter)), CanEditMultipleObjects]
    public sealed class AudioEmitterEditor : UnityEditor.Editor
    {
        private bool _showAdvanced;
        private ReorderableList _weightedClips;

        /// <summary>配置加权列表的绘制和增删回调，多选时禁用拖动以保护不同长度的列表。</summary>
        private void OnEnable()
        {
            _weightedClips = new ReorderableList(serializedObject, serializedObject.FindProperty("weightedClips"), targets.Length == 1, true, true, true);
            _weightedClips.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Weighted Clips (0 Excludes an Entry)");
            _weightedClips.elementHeight = EditorGUIUtility.singleLineHeight + 4f;
            _weightedClips.drawElementCallback = (rect, index, active, focused) =>
            {
                var entry = _weightedClips.serializedProperty.GetArrayElementAtIndex(index);
                rect.y += 2f;
                rect.height = EditorGUIUtility.singleLineHeight;
                var clipRect = new Rect(rect.x, rect.y, rect.width * .65f, rect.height);
                var weightRect = new Rect(clipRect.xMax + 4f, rect.y, rect.width - clipRect.width - 4f, rect.height);
                EditorGUI.PropertyField(clipRect, entry.FindPropertyRelative("clip"), GUIContent.none);
                EditorGUI.PropertyField(weightRect, entry.FindPropertyRelative("weight"), GUIContent.none);
            };
            _weightedClips.onAddCallback = list => AudioInspectorUtility.AddWeightedClip(serializedObject);
            _weightedClips.onRemoveCallback = list =>
            {
                var index = list.index;
                AudioInspectorUtility.RemoveWeightedClip(serializedObject, index);
                list.index = list.count == 0 ? -1 : Mathf.Clamp(index - 1, 0, list.count - 1);
            };
        }

        /// <summary>绘制基础与高级配置，通过序列化提交保留 Undo 和 Prefab Override，再只读检查各对象。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            AudioInspectorUtility.Script(serializedObject);
            AudioInspectorUtility.Field(serializedObject, "useWeightedRandom", "Use Weighted Selection");
            var weighted = serializedObject.FindProperty("useWeightedRandom");
            if (AudioInspectorUtility.ShowWhen(weighted))
            {
                _weightedClips.DoLayoutList();
                EditorGUILayout.HelpBox("New entries start at weight 1. Weight 0 excludes an entry; switching selection mode preserves both lists.", MessageType.Info);
                if (targets.Length > 1)
                {
                    EditorGUILayout.HelpBox("Multi-selection supports editing, adding and removing entries. Reorder one object at a time to preserve mixed-length lists.", MessageType.Info);
                }
            }
            if (weighted.hasMultipleDifferentValues || !weighted.boolValue)
            {
                AudioInspectorUtility.Field(serializedObject, "clips");
            }

            AudioInspectorUtility.Field(serializedObject, "volume", "Volume Multiplier");
            AudioInspectorUtility.Field(serializedObject, "loop");
            AudioInspectorUtility.Field(serializedObject, "playOnAwake", "Play On First Start");
            AudioInspectorUtility.Field(serializedObject, "positionMode");
            var mode = serializedObject.FindProperty("positionMode");
            if (AudioInspectorUtility.ShowWhen(mode, (int)AudioEmitter.PositionMode.FollowTransform))
            {
                AudioInspectorUtility.Field(serializedObject, "followTarget");
            }

            var configured = false;
            foreach (var selected in targets)
            {
                using (var serialized = new SerializedObject(selected))
                {
                    var options = AudioInspectorUtility.Options(serialized);
                    configured |= AudioInspectorUtility.HasRouteOrFade(serialized) || options.Pitch != 1f || options.Priority != 0 ||
                        !serialized.FindProperty("useGlobalPlaySoundMode").boolValue || options.MinDistance != 1f || options.MaxDistance != 50f ||
                        options.RolloffMode != AudioRolloffMode.Linear || serialized.FindProperty("onStarted").FindPropertyRelative("m_PersistentCalls.m_Calls").arraySize > 0;
                }
            }
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, configured ? "Advanced (Configured)" : "Advanced", true);
            if (configured && !_showAdvanced)
            {
                EditorGUILayout.HelpBox("Advanced values or events are configured. Expand Advanced to inspect them; folding does not reset values.", MessageType.Info);
            }

            if (_showAdvanced)
            {
                AudioInspectorUtility.Field(serializedObject, "pitch");
                AudioInspectorUtility.Field(serializedObject, "priority", "Admission Priority (0 = Ordinary)");
                AudioInspectorUtility.Field(serializedObject, "useGlobalPlaySoundMode", "Use Global Sound Policy");
                var inherit = serializedObject.FindProperty("useGlobalPlaySoundMode");
                if (inherit.hasMultipleDifferentValues || !inherit.boolValue)
                {
                    AudioInspectorUtility.Field(serializedObject, "playSoundMode");
                }

                if (mode.hasMultipleDifferentValues || mode.intValue != (int)AudioEmitter.PositionMode.TwoDimensional)
                {
                    EditorGUILayout.LabelField("Spatial Distances (World Units)", EditorStyles.boldLabel);
                    AudioInspectorUtility.Field(serializedObject, "minDistance");
                    AudioInspectorUtility.Field(serializedObject, "maxDistance");
                    AudioInspectorUtility.Field(serializedObject, "rolloffMode");
                }
                AudioInspectorUtility.Route(serializedObject);
                AudioInspectorUtility.Fade(serializedObject);
                AudioInspectorUtility.Field(serializedObject, "onStarted");
            }
            serializedObject.ApplyModifiedProperties();
            foreach (var selected in targets)
            {
                using (var serialized = new SerializedObject(selected))
                {
                    DrawWarnings(serialized);
                }
            }
        }

        /// <summary>检查音效配置、可选 Clip 和空间目标，不随机选曲、播放或自动修复字段。</summary>
        /// <param name="serialized">单个 AudioEmitter 的序列化视图。</param>
        private static void DrawWarnings(SerializedObject serialized)
        {
            AudioInspectorUtility.RouteWarnings(serialized);
            var mode = (AudioEmitter.PositionMode)serialized.FindProperty("positionMode").intValue;
            var options = AudioInspectorUtility.Options(serialized);
            AudioInspectorUtility.Validate(serialized, () =>
            {
                if ((uint)mode > (uint)AudioEmitter.PositionMode.FollowTransform)
                {
                    throw new ArgumentOutOfRangeException("positionMode");
                }

                options.Validate(mode != AudioEmitter.PositionMode.TwoDimensional);
            });
            var weighted = serialized.FindProperty("useWeightedRandom").boolValue;
            var list = serialized.FindProperty(weighted ? "weightedClips" : "clips");
            var candidate = false;
            for (var i = 0; i < list.arraySize; i++)
            {
                var entry = list.GetArrayElementAtIndex(i);
                var clip = (weighted ? entry.FindPropertyRelative("clip") : entry).objectReferenceValue as AudioClip;
                var weight = weighted ? entry.FindPropertyRelative("weight").floatValue : 1f;
                if (!AudioSoundOptions.IsFinite(weight) || weight < 0f)
                {
                    AudioInspectorUtility.Message(serialized, "Every weight must be finite and nonnegative (entry " + i + ").", MessageType.Error);
                }

                if (!clip || weight <= 0f || !AudioSoundOptions.IsFinite(weight))
                {
                    continue;
                }

                candidate = true;
                AudioInspectorUtility.Validate(serialized, () => options.ValidateClip(clip));
            }
            if (!candidate)
            {
                AudioInspectorUtility.Message(serialized, "No live clip candidates with a positive weight. Play leaves existing playback unchanged.", MessageType.Warning);
            }

            var emitter = (AudioEmitter)serialized.targetObject;
            var target = serialized.FindProperty("followTarget").objectReferenceValue as Transform;
            if (!AudioOutputRoute.HasReference(target))
            {
                target = emitter.transform;
            }

            if (mode == AudioEmitter.PositionMode.FollowTransform && (!target || !target.gameObject.activeInHierarchy))
            {
                AudioInspectorUtility.Message(serialized, "The follow target must be live and active. Play leaves existing playback unchanged.", MessageType.Warning);
            }
            else if (mode != AudioEmitter.PositionMode.TwoDimensional &&
                !AudioInspectorUtility.FinitePosition(mode == AudioEmitter.PositionMode.FollowTransform ? target.position : emitter.transform.position))
            {
                AudioInspectorUtility.Message(serialized, "Spatial playback requires a finite world position.", MessageType.Error);
            }
        }
    }
}
