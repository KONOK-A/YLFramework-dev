using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Audio;
using YLFramework.Resource;

namespace YLFramework.Editor.Audio
{
    /// <summary>通过序列化属性编辑音频素材组库，支持多对象与 Undo，不抽选或加载素材。</summary>
    [CustomEditor(typeof(AudioClipLibrary)), CanEditMultipleObjects]
    public sealed class AudioClipLibraryEditor : UnityEditor.Editor
    {
        /// <summary>绘制素材组和迁移入口，提交字段修改后显示只读配置诊断。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            AudioInspectorUtility.Script(serializedObject);
            EditorGUILayout.HelpBox("Groups select material, not control channels. Names match exactly (case-sensitive). Uniform ignores Weight; Weighted excludes Weight 0. ExternalProvider requires AudioGroupPlayer.SetProvider in code.", MessageType.Info);
            if (targets.Length > 1)
            {
                EditorGUILayout.HelpBox("Only shared row indices are shown. Add appends independently to each selected library; Remove affects that index where present. Edit unequal rows one library at a time.", MessageType.Info);
            }

            var groups = serializedObject.FindProperty("groups");
            for (var i = 0; i < groups.arraySize; i++)
            {
                var group = groups.GetArrayElementAtIndex(i);
                EditorGUILayout.BeginHorizontal();
                group.isExpanded = EditorGUILayout.Foldout(group.isExpanded, "Group " + i, true);
                if (GUILayout.Button("Remove Group", GUILayout.Width(110f)))
                {
                    RemoveGroup(serializedObject, i);
                    EditorGUILayout.EndHorizontal();
                    return;
                }
                EditorGUILayout.EndHorizontal();
                if (!group.isExpanded)
                {
                    continue;
                }

                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(group.FindPropertyRelative("name"));
                    var selection = group.FindPropertyRelative("selection");
                    EditorGUILayout.PropertyField(selection);
                    var entries = group.FindPropertyRelative("entries");
                    for (var j = 0; j < entries.arraySize; j++)
                    {
                        var entry = entries.GetArrayElementAtIndex(j);
                        EditorGUILayout.BeginHorizontal();
                        entry.isExpanded = EditorGUILayout.Foldout(entry.isExpanded, "Entry " + j, true);
                        if (GUILayout.Button("Remove Entry", GUILayout.Width(110f)))
                        {
                            RemoveEntry(serializedObject, i, j);
                            EditorGUILayout.EndHorizontal();
                            return;
                        }
                        EditorGUILayout.EndHorizontal();
                        if (!entry.isExpanded)
                        {
                            continue;
                        }

                        using (new EditorGUI.IndentLevelScope())
                        {
                            var source = entry.FindPropertyRelative("source");
                            EditorGUILayout.PropertyField(source);
                            if (AudioInspectorUtility.ShowWhen(source, (int)AudioClipSource.Clip))
                            {
                                EditorGUILayout.PropertyField(entry.FindPropertyRelative("clip"));
                            }

                            if (source.hasMultipleDifferentValues || source.intValue != (int)AudioClipSource.Clip)
                            {
                                EditorGUILayout.PropertyField(entry.FindPropertyRelative("key"));
                            }

                            if (AudioInspectorUtility.ShowWhen(source, (int)AudioClipSource.Resource))
                            {
                                EditorGUILayout.PropertyField(entry.FindPropertyRelative("projectId"));
                            }

                            if (source.hasMultipleDifferentValues || source.intValue == (int)AudioClipSource.AssetBundle ||
                                !string.IsNullOrEmpty(entry.FindPropertyRelative("bundleName").stringValue))
                            {
                                using (new EditorGUI.DisabledScope(true))
                                {
                                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("bundleName"), new GUIContent("Legacy Bundle Name"));
                                }
                            }

                            if (AudioInspectorUtility.ShowWhen(selection, (int)AudioClipSelectionMode.Weighted))
                            {
                                EditorGUILayout.PropertyField(entry.FindPropertyRelative("weight"));
                            }
                        }
                    }
                    if (GUILayout.Button("Add Entry"))
                    {
                        AddEntry(serializedObject, i);
                        return;
                    }
                }
            }
            if (GUILayout.Button("Add Group"))
            {
                AddGroup(serializedObject);
            }

            serializedObject.ApplyModifiedProperties();
            if (targets.Length == 1 && GUILayout.Button("迁移旧资源定位到新配置副本"))
            {
                AudioClipLibraryMigration.Open((AudioClipLibrary)target);
            }

            foreach (var selected in targets)
            {
                foreach (var warning in GetWarnings((AudioClipLibrary)selected))
                {
                    EditorGUILayout.HelpBox(selected.name + ": " + warning, MessageType.Warning);
                }
            }
        }

        /// <summary>在各库自己的列表末尾追加空的均匀选择组，提交后刷新多对象视图。</summary>
        /// <param name="selection">待编辑的一个或多个素材库。</param>
        internal static void AddGroup(SerializedObject selection)
        {
            selection.ApplyModifiedProperties();
            Undo.SetCurrentGroupName("Add Audio Clip Group");
            foreach (var target in selection.targetObjects)
            {
                using (var serialized = new SerializedObject(target))
                {
                    var groups = serialized.FindProperty("groups");
                    var index = groups.arraySize;
                    groups.InsertArrayElementAtIndex(index);
                    var group = groups.GetArrayElementAtIndex(index);
                    group.FindPropertyRelative("name").stringValue = "";
                    group.FindPropertyRelative("selection").intValue = (int)AudioClipSelectionMode.Uniform;
                    group.FindPropertyRelative("entries").ClearArray();
                    group.isExpanded = true;
                    serialized.ApplyModifiedProperties();
                }
            }

            selection.Update();
        }

        /// <summary>为指定组逐库追加独立初始化的条目，不继承末项的地址、引用或权重。</summary>
        /// <param name="selection">待编辑的一个或多个素材库。</param>
        /// <param name="groupIndex">目标组索引；不存在该组的库不处理。</param>
        internal static void AddEntry(SerializedObject selection, int groupIndex)
        {
            selection.ApplyModifiedProperties();
            Undo.SetCurrentGroupName("Add Audio Clip Entry");
            foreach (var target in selection.targetObjects)
            {
                using (var serialized = new SerializedObject(target))
                {
                    var groups = serialized.FindProperty("groups");
                    if (groupIndex < 0 || groupIndex >= groups.arraySize)
                    {
                        continue;
                    }

                    var entries = groups.GetArrayElementAtIndex(groupIndex).FindPropertyRelative("entries");
                    var index = entries.arraySize;
                    entries.InsertArrayElementAtIndex(index);
                    var entry = entries.GetArrayElementAtIndex(index);
                    entry.FindPropertyRelative("source").intValue = (int)AudioClipSource.Clip;
                    entry.FindPropertyRelative("clip").objectReferenceValue = null;
                    entry.FindPropertyRelative("key").stringValue = "";
                    entry.FindPropertyRelative("projectId").stringValue = "";
                    entry.FindPropertyRelative("bundleName").stringValue = "";
                    entry.FindPropertyRelative("weight").floatValue = 1f;
                    entry.isExpanded = true;
                    serialized.ApplyModifiedProperties();
                }
            }

            selection.Update();
        }

        /// <summary>通过共用删除入口移除各库存在的指定组。</summary>
        /// <param name="selection">待编辑的一个或多个素材库。</param>
        /// <param name="index">待删除的组索引。</param>
        internal static void RemoveGroup(SerializedObject selection, int index) => RemoveEntry(selection, -1, index);

        /// <summary>按各库的实际长度删除组或条目，以序列化提交记录 Undo 并刷新共享视图。</summary>
        /// <param name="selection">待编辑的一个或多个素材库。</param>
        /// <param name="groupIndex">条目所属组；负数表示直接删除组。</param>
        /// <param name="index">目标列表中的待删除索引；越界对象不处理。</param>
        internal static void RemoveEntry(SerializedObject selection, int groupIndex, int index)
        {
            selection.ApplyModifiedProperties();
            Undo.SetCurrentGroupName(groupIndex < 0 ? "Remove Audio Clip Group" : "Remove Audio Clip Entry");
            foreach (var target in selection.targetObjects)
            {
                using (var serialized = new SerializedObject(target))
                {
                    var list = serialized.FindProperty("groups");
                    if (groupIndex >= 0)
                    {
                        if (groupIndex >= list.arraySize)
                        {
                            continue;
                        }

                        list = list.GetArrayElementAtIndex(groupIndex).FindPropertyRelative("entries");
                    }
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

        /// <summary>只读检查组名、候选、权重与地址形状，并提示旧定位迁移和外部 Provider 接入要求。</summary>
        /// <param name="library">待检查的素材库。</param>
        /// <param name="groupName">可选的精确组名；null 时检查整个库。</param>
        /// <remarks>不随机抽选、不加载素材，也不确认 Provider 或 ResourceSystem 的运行时就绪状态。</remarks>
        internal static List<string> GetWarnings(AudioClipLibrary library, string groupName = null)
        {
            var warnings = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (library.groups == null)
            {
                warnings.Add("No selectable groups.");
                return warnings;
            }
            var matched = false;
            foreach (var group in library.groups)
            {
                if (groupName != null && (group == null || group.name != groupName))
                {
                    continue;
                }

                matched = true;
                if (group == null)
                {
                    warnings.Add("Null group is not selectable.");
                    continue;
                }
                var label = "Group '" + group.name + "': ";
                if (string.IsNullOrWhiteSpace(group.name))
                {
                    warnings.Add(label + "Name must not be empty or whitespace.");
                }
                else if (!names.Add(group.name))
                {
                    warnings.Add(label + "Duplicate group name prevents selection.");
                }

                if ((uint)group.selection > (uint)AudioClipSelectionMode.Weighted)
                {
                    warnings.Add(label + "Unknown Selection mode prevents selection.");
                }

                var candidate = false;
                if (group.entries != null)
                {
                    foreach (var entry in group.entries)
                    {
                        if (entry == null)
                        {
                            continue;
                        }

                        if ((uint)entry.source > (uint)AudioClipSource.Resource)
                        {
                            warnings.Add(label + "Unknown Source prevents selection.");
                            continue;
                        }
                        var weighted = group.selection == AudioClipSelectionMode.Weighted;
                        if (weighted && (!AudioSoundOptions.IsFinite(entry.weight) || entry.weight < 0f))
                        {
                            warnings.Add(label + "Every weight must be finite and nonnegative, including excluded entries.");
                        }

                        var selectable = false;
                        if (entry.source == AudioClipSource.Clip)
                        {
                            selectable = entry.clip;
                        }
                        else if (!string.IsNullOrWhiteSpace(entry.key))
                        {
                            if (AudioClipLibraryMigration.IsLegacy(entry))
                            {
                                warnings.Add(label + "Legacy Resources/AssetBundle entry requires explicit ProjectId/address migration to a new copy; BundleName is never inferred.");
                            }
                            else if (entry.source == AudioClipSource.Resource)
                            {
                                try
                                {
                                    _ = new ResourceAddress(entry.projectId, entry.key);
                                    selectable = string.IsNullOrEmpty(entry.bundleName);
                                }
                                catch (ArgumentException) { }
                                if (!selectable)
                                {
                                    warnings.Add(label + "Resource requires a valid ProjectId, logical address and no legacy BundleName. Initialize ResourceSystem before playback.");
                                }
                            }
                            else
                            {
                                selectable = true;
                                warnings.Add(label + "ExternalProvider is selectable only after SetProvider in code; no global provider is registered automatically.");
                            }
                        }
                        if (!selectable)
                        {
                            warnings.Add(label + "An entry has no selectable Clip or valid address; it is skipped.");
                        }

                        candidate |= selectable && (!weighted || AudioSoundOptions.IsFinite(entry.weight) && entry.weight > 0f);
                    }
                }

                if (!candidate)
                {
                    warnings.Add(label + "No selectable entries with a positive selection weight. Play preserves existing playback.");
                }
            }
            if (groupName != null && !matched)
            {
                warnings.Add("Group '" + groupName + "' not found (names match exactly).");
            }
            else if (library.groups.Count == 0)
            {
                warnings.Add("No selectable groups.");
            }

            return warnings;
        }
    }
}
