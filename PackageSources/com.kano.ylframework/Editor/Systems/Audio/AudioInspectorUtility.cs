using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using YLFramework.Audio;

namespace YLFramework.Editor.Audio
{
    /// <summary>复用音频 Inspector 的序列化绘制、配置检查和多对象列表编辑逻辑。</summary>
    /// <remarks>普通字段绘制由调用方负责 Update 与 Apply；列表增删自行提交并刷新。临时 SerializedObject 由创建方释放。</remarks>
    internal static class AudioInspectorUtility
    {
        /// <summary>以只读方式显示脚本引用，避免在定制 Inspector 中更换组件脚本。</summary>
        /// <param name="serialized">当前检查对象的序列化视图。</param>
        internal static void Script(SerializedObject serialized)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                Field(serialized, "m_Script");
            }
        }

        /// <summary>绘制序列化字段及其子项，替换标签时保留原 Tooltip。</summary>
        /// <param name="serialized">包含目标字段的序列化视图。</param>
        /// <param name="name">字段的序列化路径。</param>
        /// <param name="label">可选显示标签；null 时使用字段默认标签。</param>
        internal static void Field(SerializedObject serialized, string name, string label = null)
        {
            var property = serialized.FindProperty(name);
            if (label == null)
            {
                EditorGUILayout.PropertyField(property, true);
            }
            else
            {
                EditorGUILayout.PropertyField(property, new GUIContent(label, property.tooltip), true);
            }
        }

        /// <summary>判断整数条件字段是否匹配；多选混合值时仍展示相关配置。</summary>
        /// <param name="property">整数或枚举条件字段。</param>
        /// <param name="value">要求匹配的序列化数值。</param>
        internal static bool ShowWhen(SerializedProperty property, int value) =>
            property.hasMultipleDifferentValues || property.intValue == value;

        /// <summary>判断布尔条件字段是否开启；多选混合值时仍展示相关配置。</summary>
        /// <param name="property">布尔条件字段。</param>
        internal static bool ShowWhen(SerializedProperty property) =>
            property.hasMultipleDifferentValues || property.boolValue;

        /// <summary>绘制输出路由，混合模式下禁改组引用，仅在用户明确操作时清除隐藏引用。</summary>
        /// <param name="serialized">包含 outputRoute 的序列化视图。</param>
        internal static void Route(SerializedObject serialized)
        {
            EditorGUILayout.LabelField("Output Routing (Optional)", EditorStyles.boldLabel);
            var route = serialized.FindProperty("outputRoute");
            var mode = route.FindPropertyRelative("Mode");
            var group = route.FindPropertyRelative("Group");
            EditorGUILayout.PropertyField(mode);
            var showGroup = mode.intValue == (int)AudioOutputRoute.RouteMode.MixerGroup;
            if (mode.hasMultipleDifferentValues)
            {
                showGroup = false;
                foreach (var target in serialized.targetObjects)
                {
                    using (var selected = new SerializedObject(target))
                    {
                        showGroup |= selected.FindProperty("outputRoute").FindPropertyRelative("Mode").intValue == (int)AudioOutputRoute.RouteMode.MixerGroup;
                    }
                }
            }
            if (showGroup)
            {
                using (new EditorGUI.DisabledScope(mode.hasMultipleDifferentValues))
                {
                    EditorGUILayout.PropertyField(group);
                }

                if (mode.hasMultipleDifferentValues)
                {
                    EditorGUILayout.HelpBox("Select a common MixerGroup Mode before editing Group across mixed routes.", MessageType.Info);
                }
            }
            else if (group.objectReferenceValue || group.objectReferenceInstanceIDValue != 0 || group.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox("A hidden Group reference is invalid for Inherit/Direct. Changing Mode does not erase it.", MessageType.Warning);
                if (GUILayout.Button("Clear Hidden Group Reference"))
                {
                    group.objectReferenceValue = null;
                }
            }
        }

        /// <summary>绘制按片段秒配置的自动首尾淡变字段及限制提示。</summary>
        /// <param name="serialized">包含 autoFade 的序列化视图。</param>
        internal static void Fade(SerializedObject serialized)
        {
            EditorGUILayout.LabelField("Automatic Fades (Clip Seconds)", EditorStyles.boldLabel);
            var fade = serialized.FindProperty("autoFade");
            EditorGUILayout.PropertyField(fade.FindPropertyRelative("FadeInSeconds"), new GUIContent("Fade In (Seconds)"));
            EditorGUILayout.PropertyField(fade.FindPropertyRelative("FadeOutSeconds"), new GUIContent("Tail Fade Out (Seconds)"));
            EditorGUILayout.HelpBox("0 disables a fade. Short clips compress both fades. Looping clips must have a zero tail fade.", MessageType.Info);
        }

        /// <summary>从序列化字段构造独立的检查用配置，不提交播放或反写组件。</summary>
        /// <param name="serialized">AudioMusicPlayer 或 AudioEmitter 的序列化视图。</param>
        internal static AudioSoundOptions Options(SerializedObject serialized)
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = serialized.FindProperty("loop").boolValue;
            options.Volume = serialized.FindProperty("volume").floatValue;
            var route = serialized.FindProperty("outputRoute");
            options.OutputRoute = new AudioOutputRoute
            {
                Mode = (AudioOutputRoute.RouteMode)route.FindPropertyRelative("Mode").intValue,
                Group = route.FindPropertyRelative("Group").objectReferenceValue as AudioMixerGroup
            };
            var fade = serialized.FindProperty("autoFade");
            options.AutoFade = new AudioAutoFade(fade.FindPropertyRelative("FadeInSeconds").floatValue,
                fade.FindPropertyRelative("FadeOutSeconds").floatValue);
            if (serialized.targetObject is AudioEmitter)
            {
                options.Pitch = serialized.FindProperty("pitch").floatValue;
                options.Priority = serialized.FindProperty("priority").intValue;
                options.PlaySoundMode = serialized.FindProperty("useGlobalPlaySoundMode").boolValue
                    ? (AudioKit.PlaySoundModes?)null
                    : (AudioKit.PlaySoundModes)serialized.FindProperty("playSoundMode").intValue;
                options.MinDistance = serialized.FindProperty("minDistance").floatValue;
                options.MaxDistance = serialized.FindProperty("maxDistance").floatValue;
                options.RolloffMode = (AudioRolloffMode)serialized.FindProperty("rolloffMode").intValue;
            }
            return options;
        }

        /// <summary>判断路由或淡变是否偏离默认值，包括隐藏或失效的组引用。</summary>
        /// <param name="serialized">包含 outputRoute 与 autoFade 的序列化视图。</param>
        internal static bool HasRouteOrFade(SerializedObject serialized)
        {
            var route = serialized.FindProperty("outputRoute");
            var group = route.FindPropertyRelative("Group");
            var fade = serialized.FindProperty("autoFade");
            return route.FindPropertyRelative("Mode").intValue != (int)AudioOutputRoute.RouteMode.Inherit ||
                group.objectReferenceValue || group.objectReferenceInstanceIDValue != 0 ||
                fade.FindPropertyRelative("FadeInSeconds").floatValue != 0f || fade.FindPropertyRelative("FadeOutSeconds").floatValue != 0f;
        }

        /// <summary>提示仍保有实例标识但对象已丢失的输出组引用，不自动清理配置。</summary>
        /// <param name="serialized">包含 outputRoute 的序列化视图。</param>
        internal static void RouteWarnings(SerializedObject serialized)
        {
            var group = serialized.FindProperty("outputRoute").FindPropertyRelative("Group");
            if (!group.objectReferenceValue && group.objectReferenceInstanceIDValue != 0)
            {
                Message(serialized, "The stored output Group reference is missing. Assign a live Group for MixerGroup, or explicitly clear the hidden reference for Inherit/Direct in Advanced.", MessageType.Error);
            }
        }

        /// <summary>执行配置检查，将参数异常转为带对象名称的 Inspector 错误提示。</summary>
        /// <param name="serialized">用于标明错误归属的序列化视图。</param>
        /// <param name="validation">只负责检查配置的回调；其他异常不在此捕获。</param>
        internal static void Validate(SerializedObject serialized, Action validation)
        {
            try
            {
                validation();
            }
            catch (ArgumentException exception)
            {
                Message(serialized, exception.Message, MessageType.Error);
            }
        }

        /// <summary>绘制带目标对象名称的诊断消息。</summary>
        /// <param name="serialized">消息所属对象的序列化视图。</param>
        /// <param name="message">诊断消息正文。</param>
        /// <param name="type">消息严重级别。</param>
        internal static void Message(SerializedObject serialized, string message, MessageType type) =>
            EditorGUILayout.HelpBox(serialized.targetObject.name + ": " + message, type);

        /// <summary>检查位置的三个分量是否均为有限值。</summary>
        /// <param name="position">待检查的位置。</param>
        internal static bool FinitePosition(Vector3 position) => AudioSoundOptions.IsFinite(position.x) &&
            AudioSoundOptions.IsFinite(position.y) && AudioSoundOptions.IsFinite(position.z);

        /// <summary>为各选中对象独立追加空 Clip、权重为 1 的条目，并通过序列化提交记录 Undo。</summary>
        /// <param name="selection">需要追加条目的多对象序列化视图。</param>
        internal static void AddWeightedClip(SerializedObject selection)
        {
            // 多选列表长度可能不同，分别追加可避免截短较长列表或继承末项的零权重。
            selection.ApplyModifiedProperties();
            Undo.SetCurrentGroupName("Add Weighted Audio Clip");
            foreach (var target in selection.targetObjects)
            {
                using (var serialized = new SerializedObject(target))
                {
                    var list = serialized.FindProperty("weightedClips");
                    var index = list.arraySize;
                    list.InsertArrayElementAtIndex(index);
                    var entry = list.GetArrayElementAtIndex(index);
                    entry.FindPropertyRelative("clip").objectReferenceValue = null;
                    entry.FindPropertyRelative("weight").floatValue = 1f;
                    serialized.ApplyModifiedProperties();
                }
            }
            selection.Update();
        }

        /// <summary>删除各选中对象存在的指定条目，通过序列化提交记录 Undo 后刷新共享视图。</summary>
        /// <param name="selection">需要删除条目的多对象序列化视图。</param>
        /// <param name="index">待删除的条目索引；越界对象不处理。</param>
        internal static void RemoveWeightedClip(SerializedObject selection, int index)
        {
            selection.ApplyModifiedProperties();
            Undo.SetCurrentGroupName("Remove Weighted Audio Clip");
            foreach (var target in selection.targetObjects)
            {
                using (var serialized = new SerializedObject(target))
                {
                    var list = serialized.FindProperty("weightedClips");
                    if (index < 0 || index >= list.arraySize)
                    {
                        continue;
                    }

                    list.DeleteArrayElementAtIndex(index);
                    serialized.ApplyModifiedProperties();
                }
            }
            selection.Update();
        }
    }
}
