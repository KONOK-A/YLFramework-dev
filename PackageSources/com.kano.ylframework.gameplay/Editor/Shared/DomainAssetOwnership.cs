using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Common;

namespace YLFramework.Editor.Domain
{
    /// <summary>领域作者工具共用的资产身份边界，不把文件路径当作subasset身份。</summary>
    internal static class DomainAssetOwnership
    {
        /// <summary>移除配表引用，仅销毁同文件子资产或删除指定目录下独占的主资产文件，然后保存配表。</summary>
        internal static void DeleteEntry<T>(ScriptableObject owner, IList<T> values, T data, string folder) where T : ScriptableObject
        {
            var ownerPath = OwnerPath(owner);
            var index = values.IndexOf(data);
            if (index < 0)
            {
                return;
            }

            var path = AssetDatabase.GetAssetPath(data);
            Undo.RecordObject(owner, "Remove domain entry");
            values.RemoveAt(index);
            // 子资产销毁进入 Undo；独立文件走 DeleteAsset，不具备同等的撤销保证。
            if (AssetDatabase.IsSubAsset(data) && path == ownerPath)
            {
                Undo.DestroyObjectImmediate(data);
            }
            else if (OwnedFile(data, path, folder) && !AssetDatabase.DeleteAsset(path))
            {
                values.Insert(index, data);
                throw new InvalidOperationException("不能删除领域条目资产: " + path);
            }
            // 外部借用对象只解引用，绝不删除它的文件或同文件中的其他对象。
            EditorUtility.SetDirty(owner);
            AssetDatabase.SaveAssetIfDirty(owner);
        }

        /// <summary>修改并保存同文件子资产名称，或重命名专属目录内的独占主资产；外部借用对象保持原样。</summary>
        internal static void RenameEntry(ScriptableObject owner, ScriptableObject data, string name, string folder)
        {
            var ownerPath = OwnerPath(owner);
            var path = AssetDatabase.GetAssetPath(data);
            if (AssetDatabase.IsSubAsset(data) && path == ownerPath)
            {
                Undo.RecordObject(data, "Rename domain entry");
                data.name = name;
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssetIfDirty(data);
            }
            else if (OwnedFile(data, path, folder))
            {
                // 文件重命名直接交给 AssetDatabase，不沿用上方子资产的 Undo 记录。
                var error = AssetDatabase.RenameAsset(path, AssetNameTools.GetName(name, folder));
                if (!string.IsNullOrEmpty(error))
                {
                    throw new InvalidOperationException(error);
                }
            }
        }

        /// <summary>只认指定目录下且文件内仅有自身的主资产，避免按路径误删其他子资产。</summary>
        private static bool OwnedFile(ScriptableObject data, string path, string folder) =>
            AssetDatabase.IsMainAsset(data) && path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal) &&
            AssetDatabase.LoadAllAssetsAtPath(path).Length == 1;

        /// <summary>确认配表是 Assets 内已保存的主资产，并返回供子资产归属比较使用的路径。</summary>
        private static string OwnerPath(ScriptableObject owner)
        {
            if (!owner || !AssetDatabase.IsMainAsset(owner))
            {
                throw new InvalidOperationException("领域配表必须是已保存的主资产。");
            }

            var path = AssetDatabase.GetAssetPath(owner);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("只允许修改Assets内的领域配表。");
            }

            return path;
        }
    }
}
