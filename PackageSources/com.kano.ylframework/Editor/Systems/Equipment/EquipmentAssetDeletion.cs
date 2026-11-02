using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Equipment;

namespace YLFramework.Editor.Equipment
{
    /// <summary>用可撤销的内存记录把装备独立文件的删除接入 Unity Undo 与 Redo。</summary>
    // Unity 的对象 Undo 不包含独立文件删除；用同组内存记录保留文件及 meta 原始身份。
    [InitializeOnLoad]
    internal static class EquipmentAssetDeletion
    {
        /// <summary>恢复现有删除记录的处理状态，订阅 Undo 同步，并在编辑器退出时释放记录。</summary>
        static EquipmentAssetDeletion()
        {
            foreach (var state in Resources.FindObjectsOfTypeAll<EquipmentDeletionRecord>())
            {
                state.IsApplied = state.deleted;
            }

            Undo.undoRedoPerformed += Synchronize;
            EditorApplication.quitting += () =>
            {
                Undo.undoRedoPerformed -= Synchronize;
                foreach (var state in Resources.FindObjectsOfTypeAll<EquipmentDeletionRecord>())
                {
                    UnityEngine.Object.DestroyImmediate(state);
                }
            };
        }

        /// <summary>记录文件、元数据与内存状态，将配表解除引用和文件删除纳入同一 Undo 组。</summary>
        internal static void Delete(EquipmentConfigData data, EquipmentsConfig root, string path)
        {
            var state = ScriptableObject.CreateInstance<EquipmentDeletionRecord>();
            state.hideFlags = HideFlags.HideAndDontSave;
            state.name = "Equipment file deletion";
            state.root = root;
            state.path = path;
            state.guid = AssetDatabase.AssetPathToGUID(path);
            state.assetBytes = File.ReadAllBytes(path);
            state.metaBytes = File.ReadAllBytes(path + ".meta");
            // 磁盘字节保留原文件身份；内存快照另存未落盘字段，撤销后按原脏状态恢复而不强制保存条目。
            state.memory = EditorJsonUtility.ToJson(data);
            state.wasDirty = EditorUtility.IsDirty(data);
            state.indices = Enumerable.Range(0, root.Equipments.Count).Where(i => ReferenceEquals(root.Equipments[i], data)).ToArray();
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.RegisterCompleteObjectUndo(state, "Delete equipment");
            Undo.RecordObject(root, "Delete equipment");
            state.deleted = true;
            root.Equipments.RemoveAll(item => ReferenceEquals(item, data));
            Undo.FlushUndoRecordObjects();
            if (!AssetDatabase.DeleteAsset(path))
            {
                state.IsApplied = !File.Exists(path);
                Undo.RevertAllDownToGroup(group);
                root.ClearCache();
                throw new IOException("Could not delete equipment asset: " + path);
            }
            state.IsApplied = true;
            Undo.CollapseUndoOperations(group);
            root.ClearCache();
            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssetIfDirty(root);
        }

        /// <summary>按 Undo 恢复的删除状态尝试同步文件，冲突或恢复失败只记录异常，不整体回滚。</summary>
        private static void Synchronize()
        {
            foreach (var state in Resources.FindObjectsOfTypeAll<EquipmentDeletionRecord>())
            {
                if (!state.root || state.IsApplied == state.deleted)
                {
                    continue;
                }

                try
                {
                    if (state.deleted)
                    {
                        if (!File.Exists(state.path))
                        {
                            continue;
                        }

                        RequireOriginal(state);
                        if (!AssetDatabase.DeleteAsset(state.path))
                        {
                            throw new IOException("Could not redo equipment deletion: " + state.path);
                        }
                    }
                    else
                    {
                        if (File.Exists(state.path))
                        {
                            continue;
                        }

                        if (!Directory.Exists(Path.GetDirectoryName(state.path)))
                        {
                            throw new IOException("Equipment owner directory was removed outside Undo: " + state.path);
                        }

                        if (File.Exists(state.path + ".meta") && !File.ReadAllBytes(state.path + ".meta").SequenceEqual(state.metaBytes))
                        {
                            throw new IOException("Foreign equipment meta occupies the deleted path: " + state.path);
                        }

                        var current = AssetDatabase.GUIDToAssetPath(state.guid);
                        if (!string.IsNullOrEmpty(current) && current != state.path)
                        {
                            throw new IOException("Equipment GUID moved outside Undo: " + current);
                        }

                        File.WriteAllBytes(state.path, state.assetBytes);
                        File.WriteAllBytes(state.path + ".meta", state.metaBytes);
                        AssetDatabase.ImportAsset(state.path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        var restored = AssetDatabase.LoadAssetAtPath<EquipmentConfigData>(state.path);
                        if (!restored || AssetDatabase.AssetPathToGUID(state.path) != state.guid)
                        {
                            throw new IOException("Could not restore equipment identity: " + state.path);
                        }

                        EditorJsonUtility.FromJsonOverwrite(state.memory, restored);
                        if (state.wasDirty)
                        {
                            EditorUtility.SetDirty(restored);
                        }
                        else
                        {
                            EditorUtility.ClearDirty(restored);
                        }

                        foreach (var index in state.indices)
                        {
                            if (index < state.root.Equipments.Count && !state.root.Equipments[index])
                            {
                                state.root.Equipments[index] = restored;
                            }
                        }
                    }
                    state.root.ClearCache();
                    EditorUtility.SetDirty(state.root);
                    AssetDatabase.SaveAssetIfDirty(state.root);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                }
                finally
                {
                    state.IsApplied = state.deleted;
                }
            }
        }
        /// <summary>重做删除前核对原 GUID、资产与 meta 字节，拒绝删除已被外部替换的文件。</summary>
        private static void RequireOriginal(EquipmentDeletionRecord state)
        {
            if (AssetDatabase.AssetPathToGUID(state.path) != state.guid || !File.ReadAllBytes(state.path).SequenceEqual(state.assetBytes) ||
                !File.Exists(state.path + ".meta") || !File.ReadAllBytes(state.path + ".meta").SequenceEqual(state.metaBytes))
            {
                throw new IOException("Equipment file changed outside Undo; preserving it: " + state.path);
            }
        }
    }
}
