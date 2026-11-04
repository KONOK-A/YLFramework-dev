using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using YLFramework.DataAssets;

namespace YLFramework.Editor.DataAssets
{
    /// <summary>配置条目列表支持的排序依据。</summary>
    public enum DataAssetSort
    {
        /// <summary>按条目的业务 ID 排序。</summary>
        Id,
        /// <summary>按配置名称进行 Ordinal 排序。</summary>
        Name
    }

    /// <summary>描述根或单个列表槽位的作者数据问题，不执行数据修复。</summary>
    public readonly struct DataAssetDiagnostic
    {
        /// <summary>问题所在的零基列表槽位；根级问题使用 -1。</summary>
        public int Row
        {
            get;
        }
        /// <summary>用于定位问题的条目；根级问题或缺失引用时为空。</summary>
        public ScriptableObjectData Asset
        {
            get;
        }
        /// <summary>供作者窗口和检查器显示的问题说明。</summary>
        public string Message
        {
            get;
        }
        /// <summary>保存问题槽位、关联条目及说明的诊断快照。</summary>
        internal DataAssetDiagnostic(int row, ScriptableObjectData asset, string message)
        {
            Row = row;
            Asset = asset;
            Message = message;
        }
    }

    /// <summary>集中执行配置根的显式作者操作，按根身份和资产位置限制修改及物理删除范围。</summary>
    public static class ScriptableObjectConfigsController
    {
        /// <summary>扫描 Assets 下的配置根，按资产路径进行 Ordinal 排序，不登记到运行时管理器。</summary>
        public static List<ScriptableObjectConfigs> GetAllConfigs()
        {
            var result = new List<ScriptableObjectConfigs>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + typeof(ScriptableObjectConfigs).FullName, new[] { "Assets" }))
            {
                var root = AssetDatabase.LoadAssetAtPath<ScriptableObjectConfigs>(AssetDatabase.GUIDToAssetPath(guid));
                if (root)
                {
                    result.Add(root);
                }
            }
            return result.OrderBy(root => AssetDatabase.GetAssetPath(root), StringComparer.Ordinal).ToList();
        }

        /// <summary>解析根绑定的条目基类；未绑定时返回默认基类，失效或不合法绑定则报错。</summary>
        public static Type GetDataType(this ScriptableObjectConfigs root)
        {
            if (!root)
            {
                throw new ArgumentNullException(nameof(root));
            }

            if (string.IsNullOrEmpty(root.typeScriptGuid))
            {
                return typeof(ScriptableObjectData);
            }

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(root.typeScriptGuid));
            var type = script ? script.GetClass() : null;
            if (type == null || !typeof(ScriptableObjectData).IsAssignableFrom(type) || type.ContainsGenericParameters)
            {
                throw new InvalidOperationException($"DataAssets '{root.name}': invalid typeScriptGuid '{root.typeScriptGuid}'.");
            }

            return type;
        }

        /// <summary>取得兼容根绑定的可实例化条目类型，包含适用的基类并按完整类型名排序。</summary>
        public static IReadOnlyList<Type> GetDataTypes(ScriptableObjectConfigs root)
        {
            var parent = root.GetDataType();
            var types = TypeCache.GetTypesDerivedFrom<ScriptableObjectData>()
                .Where(type => parent.IsAssignableFrom(type) && !type.IsAbstract && !type.ContainsGenericParameters).ToList();
            if (!parent.IsAbstract && !parent.ContainsGenericParameters && !types.Contains(parent))
            {
                types.Add(parent);
            }

            return types.OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        }

        /// <summary>更新条目基类绑定并记录 Undo；若会排除已有条目则拒绝，成功后标脏但不保存。</summary>
        /// <param name="root">已保存于 Assets 下的配置主资产。</param>
        /// <param name="script">条目基类脚本；空引用表示清除绑定并采用默认基类。</param>
        public static void SetDataType(ScriptableObjectConfigs root, MonoScript script)
        {
            RequireRoot(root);
            var type = script ? script.GetClass() : typeof(ScriptableObjectData);
            if (type == null || !typeof(ScriptableObjectData).IsAssignableFrom(type) || type.ContainsGenericParameters)
            {
                throw new ArgumentException("Select a ScriptableObjectData script.", nameof(script));
            }

            if (root.configs != null && root.configs.Any(item => item && !type.IsInstanceOfType(item)))
            {
                throw new InvalidOperationException("The selected type would exclude existing entries; move or remove those references first.");
            }

            var guid = script ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script)) : string.Empty;
            if (script && string.IsNullOrEmpty(guid))
            {
                throw new ArgumentException("The type script must be a persistent asset.", nameof(script));
            }

            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(root, "Change DataAssets type");
            root.typeScriptGuid = guid;
            EditorUtility.SetDirty(root);
        }

        /// <summary>按已保存根的 GUID 计算独立条目目录，根移动或改名不会改变该目录身份。</summary>
        /// <returns>条目目录路径；此方法不创建目录。</returns>
        public static string GetDataPath(ScriptableObjectConfigs root)
        {
            var path = RequireRoot(root);
            return "Assets/YLFrameworkData/DataAssets/" + AssetDatabase.AssetPathToGUID(path);
        }

        /// <summary>创建兼容绑定的新条目，以未占用正 ID 持久化并加入根列表；不注册创建 Undo。</summary>
        /// <param name="type">非抽象、无开放泛型参数的具体条目类型。</param>
        /// <param name="root">接收新条目并拥有其资产的配置根。</param>
        public static ScriptableObjectData CreateData(Type type, ScriptableObjectConfigs root)
        {
            RequireType(root, type);
            var id = root.GenerateId();
            var data = (ScriptableObjectData)ScriptableObject.CreateInstance(type);
            return PersistNew(root, data, id, "Config " + id);
        }

        /// <summary>复制根列表中的条目，以新 ID 和不冲突的副本名称走新条目持久化流程。</summary>
        /// <param name="root">接收副本的配置根，源条目必须在其列表内。</param>
        /// <param name="source">要复制的条目，副本保留其具体类型及可复制数据。</param>
        public static ScriptableObjectData CopyData(ScriptableObjectConfigs root, ScriptableObjectData source)
        {
            RequireMember(root, source);
            RequireType(root, source.GetType());
            var id = root.GenerateId();
            var copyName = GetCopyName(source.configName, root);
            var copy = Object.Instantiate(source);
            return PersistNew(root, copy, id, copyName);
        }

        /// <summary>将新条目保存为独立资产并连接根列表，仅在文件尚未创建时销毁失败的临时对象。</summary>
        private static ScriptableObjectData PersistNew(ScriptableObjectConfigs root, ScriptableObjectData data, int id, string displayName)
        {
            var created = false;
            try
            {
                var folder = GetDataPath(root);
                EnsureFolder(folder);
                data.id = id;
                data.name = id.ToString();
                data.configName = displayName;
                data.parent = root;
                var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + id + ".asset");
                AssetDatabase.CreateAsset(data, path);
                created = true;
                if (root.configs == null)
                {
                    root.configs = new List<ScriptableObjectData>();
                }

                root.configs.Add(data);
                root.ClearCache();
                EditorUtility.SetDirty(root);
                EditorUtility.SetDirty(data);
                return data;
            }
            finally
            {
                if (!created && data)
                {
                    Object.DestroyImmediate(data);
                }
            }
        }

        /// <summary>去掉原名称末尾的数字副本后缀，再选择列表中尚未使用的最小正数后缀。</summary>
        public static string GetCopyName(string name, ScriptableObjectConfigs root)
        {
            var stem = Regex.Replace(name ?? string.Empty, @"\s*\([0-9]+\)$", string.Empty).TrimEnd();
            for (var number = 1; number < int.MaxValue; number++)
            {
                var candidate = stem + " (" + number + ")";
                if (root.configs == null || !root.configs.Any(item => item && StringComparer.Ordinal.Equals(item.configName, candidate)))
                {
                    return candidate;
                }
            }
            throw new InvalidOperationException("No copy name is available.");
        }

        /// <summary>仅改变条目字段，不移动文件或GUID；已有引用保持稳定。</summary>
        /// <param name="root">拥有条目的配置根。</param>
        /// <param name="data">需要修改且由根拥有的条目。</param>
        /// <param name="id">新的根内唯一 ID，重复时在修改前报错。</param>
        /// <param name="displayName">新的配置名称；变更记录 Undo 并标脏，不自动保存。</param>
        public static void RenameData(ScriptableObjectConfigs root, ScriptableObjectData data, int id, string displayName)
        {
            RequireOwned(root, data);
            if (root.configs.Any(item => item && !ReferenceEquals(item, data) && item.id == id))
            {
                throw new InvalidOperationException($"DataAssets '{root.name}': duplicate ID {id}.");
            }

            if (data.id == id && data.configName == displayName)
            {
                return;
            }

            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(data, "Edit DataAssets identity");
            data.id = id;
            data.configName = displayName;
            root.ClearCache();
            EditorUtility.SetDirty(data);
        }

        /// <summary>结合父根、列表成员身份和资产位置判断所有权，不依据其他资产是否也引用该条目。</summary>
        /// <returns>属于根文件的非主对象，或根 GUID 目录直属的独立主资产时为真。</returns>
        public static bool IsOwned(ScriptableObjectConfigs root, ScriptableObjectData data)
        {
            if (!root || !data || data.parent != root || root.configs == null || !root.configs.Contains(data))
            {
                return false;
            }

            var rootPath = AssetDatabase.GetAssetPath(root);
            if (!IsWritableAssetPath(rootPath))
            {
                return false;
            }

            var path = AssetDatabase.GetAssetPath(data);
            // AddObjectToAsset后、首次保存前IsSubAsset仍可能为false；同文件、真实持久成员且非主对象才归本根。
            if (path == rootPath)
            {
                return AssetDatabase.Contains(data) && !AssetDatabase.IsMainAsset(data);
            }

            return AssetDatabase.IsMainAsset(data) &&
                StringComparer.Ordinal.Equals(Path.GetDirectoryName(path)?.Replace('\\', '/'), GetDataPath(root));
        }

        /// <summary>取消无副作用；根拥有的独立主资产物理删除不可撤销，UI必须明确确认。根内子资产使用Undo销毁。</summary>
        /// <param name="root">拥有目标条目的配置根。</param>
        /// <param name="data">已验证所有权的待删条目，不允许物理删除外部引用资产。</param>
        /// <param name="confirmed">是否已获得本次删除确认；为假时立即返回。</param>
        /// <returns>已执行删除时为真；取消时为假，实际删除失败可能抛出异常。</returns>
        public static bool DeleteData(ScriptableObjectConfigs root, ScriptableObjectData data, bool confirmed)
        {
            if (!confirmed)
            {
                return false;
            }

            RequireOwned(root, data);
            var path = AssetDatabase.GetAssetPath(data);
            if (path == AssetDatabase.GetAssetPath(root))
            {
                Undo.IncrementCurrentGroup();
                var group = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(root, "Delete DataAssets child");
                root.configs.RemoveAll(item => ReferenceEquals(item, data));
                Undo.DestroyObjectImmediate(data);
                Undo.CollapseUndoOperations(group);
            }
            else
            {
                RequireSingleObjectFile(data);
                if (!AssetDatabase.DeleteAsset(path))
                {
                    throw new IOException("Could not delete DataAssets child: " + path);
                }

                root.configs.RemoveAll(item => ReferenceEquals(item, data));
            }
            root.ClearCache();
            EditorUtility.SetDirty(root);
            return true;
        }

        /// <summary>只移除指定槽引用，永不删除被引用资产；快照不匹配时拒绝陈旧窗口操作。</summary>
        /// <param name="root">需要移除引用的配置根，修改记录 Undo 并标脏。</param>
        /// <param name="row">目标零基列表槽位。</param>
        /// <param name="expected">调用时预期的槽位对象引用，允许为缺失引用。</param>
        public static void RemoveReference(ScriptableObjectConfigs root, int row, ScriptableObjectData expected)
        {
            RequireRoot(root);
            if (root.configs == null || row < 0 || row >= root.configs.Count || !ReferenceEquals(root.configs[row], expected))
            {
                throw new InvalidOperationException("The DataAssets row changed; refresh before removing it.");
            }

            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(root, "Unlink DataAssets entry");
            root.configs.RemoveAt(row);
            root.ClearCache();
            EditorUtility.SetDirty(root);
        }

        /// <summary>确认后先校验删除范围，再删除根拥有的独立条目和根文件；物理删除不支持 Undo。</summary>
        /// <param name="root">待删除的已保存配置根；存在非本根拥有的子资产时拒绝删除。</param>
        /// <param name="confirmed">是否已获得删除确认，为假时不改动资产。</param>
        /// <returns>根文件删除结果；按顺序执行，不提供失败后的事务回滚。</returns>
        public static bool DeleteRoot(ScriptableObjectConfigs root, bool confirmed)
        {
            if (!confirmed)
            {
                return false;
            }

            var rootPath = RequireRoot(root);
            // 不注册递归删除监听；仅显式命令处理已验证的本根条目，外部引用和目录内未知文件保留。
            var owned = root.configs == null ? Array.Empty<ScriptableObjectData>() :
                root.configs.Where(item => IsOwned(root, item) && AssetDatabase.IsMainAsset(item)).Distinct().ToArray();
            foreach (var item in AssetDatabase.LoadAllAssetsAtPath(rootPath))
            {
                if (item && !ReferenceEquals(item, root) && (!(item is ScriptableObjectData data) || !IsOwned(root, data)))
                {
                    throw new InvalidOperationException("The root contains unowned sub-assets. Move them out before deleting the root file.");
                }
            }

            foreach (var item in owned)
            {
                RequireSingleObjectFile(item);
            }

            foreach (var item in owned)
            {
                DeleteData(root, item, true);
            }

            ScriptableObjectManager.RemoveConfig(root);
            return AssetDatabase.DeleteAsset(rootPath);
        }

        /// <summary>稳定排序有效条目并将缺失项置后，保留重复引用；顺序变化时记录 Undo 并标脏。</summary>
        /// <param name="root">需要排序的配置根。</param>
        /// <param name="field">按 ID 或配置名称排序。</param>
        /// <param name="ascending">是否升序；相同键和缺失项保持原相对顺序。</param>
        public static void Sort(ScriptableObjectConfigs root, DataAssetSort field, bool ascending)
        {
            RequireRoot(root);
            if (root.configs == null || root.configs.Count < 2)
            {
                return;
            }

            var sorted = root.configs.Select((item, index) => new { item, index }).ToList();
            sorted.Sort((a, b) =>
            {
                if (!a.item || !b.item)
                {
                    return a.item ? -1 : b.item ? 1 : a.index.CompareTo(b.index);
                }

                var value = field == DataAssetSort.Id ? a.item.id.CompareTo(b.item.id) : StringComparer.Ordinal.Compare(a.item.configName, b.item.configName);
                if (!ascending)
                {
                    value = -value;
                }

                return value == 0 ? a.index.CompareTo(b.index) : value;
            });
            if (sorted.Select(item => item.item).SequenceEqual(root.configs))
            {
                return;
            }

            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(root, "Sort DataAssets entries");
            root.configs = sorted.Select(item => item.item).ToList();
            root.ClearCache();
            EditorUtility.SetDirty(root);
        }

        /// <summary>收集类型绑定、空引用、重复 ID、父根及条目类型问题，不修改列表或修复资产。</summary>
        /// <returns>带原列表槽位的诊断；根为空时返回空集合，不代表存在有效根。</returns>
        public static IReadOnlyList<DataAssetDiagnostic> Validate(ScriptableObjectConfigs root)
        {
            var errors = new List<DataAssetDiagnostic>();
            if (!root)
            {
                return errors;
            }

            Type type = null;
            try
            {
                type = root.GetDataType();
            }
            catch (InvalidOperationException error)
            {
                errors.Add(new DataAssetDiagnostic(-1, null, error.Message));
            }
            if (root.configs == null)
            {
                errors.Add(new DataAssetDiagnostic(-1, null, "Missing entry list."));
                return errors;
            }
            var ids = new Dictionary<int, int>();
            for (var row = 0; row < root.configs.Count; row++)
            {
                var item = root.configs[row];
                if (!item)
                {
                    errors.Add(new DataAssetDiagnostic(row, null, "Missing entry reference."));
                    continue;
                }
                if (ids.TryGetValue(item.id, out var previous))
                {
                    errors.Add(new DataAssetDiagnostic(row, item, $"duplicate ID {item.id}; first at row {previous}."));
                }
                else
                {
                    ids.Add(item.id, row);
                }

                if (item.parent != root)
                {
                    errors.Add(new DataAssetDiagnostic(row, item, "Parent does not match this root; reference is read-only here."));
                }

                if (type != null && !type.IsInstanceOfType(item))
                {
                    errors.Add(new DataAssetDiagnostic(row, item, "Entry does not match the bound type."));
                }
            }
            return errors;
        }

        /// <summary>只保存本根及本根拥有的条目，不触发全局SaveAssets。</summary>
        public static void Save(ScriptableObjectConfigs root)
        {
            RequireRoot(root);
            if (root.configs != null)
            {
                foreach (var item in root.configs)
                {
                    if (IsOwned(root, item))
                    {
                        AssetDatabase.SaveAssetIfDirty(item);
                    }
                }
            }

            AssetDatabase.SaveAssetIfDirty(root);
        }

        /// <summary>仅按路径前缀限定 Assets 范围，不检查文件系统或版本控制的可写状态。</summary>
        private static bool IsWritableAssetPath(string path) => !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal);

        /// <summary>要求目标为 Assets 下已保存的根主资产，并返回其路径。</summary>
        private static string RequireRoot(ScriptableObjectConfigs root)
        {
            if (!root)
            {
                throw new ArgumentNullException(nameof(root));
            }

            var path = AssetDatabase.GetAssetPath(root);
            if (!IsWritableAssetPath(path) || !AssetDatabase.IsMainAsset(root))
            {
                throw new InvalidOperationException("Save a YL DataAssets root as a main asset under Assets before editing it.");
            }

            return path;
        }

        /// <summary>要求根可编辑且候选类型是兼容绑定的具体非开放泛型类型。</summary>
        private static void RequireType(ScriptableObjectConfigs root, Type type)
        {
            RequireRoot(root);
            if (type == null || !root.GetDataType().IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
            {
                throw new ArgumentException("Select a concrete type compatible with the root binding.", nameof(type));
            }
        }

        /// <summary>要求目标条目仍在当前根列表中，不将列表成员身份等同于资产所有权。</summary>
        private static void RequireMember(ScriptableObjectConfigs root, ScriptableObjectData data)
        {
            RequireRoot(root);
            if (!data || root.configs == null || !root.configs.Contains(data))
            {
                throw new ArgumentException("The entry does not belong to this list.", nameof(data));
            }
        }

        /// <summary>在列表成员校验后进一步要求根拥有条目，拒绝编辑或删除外部引用。</summary>
        private static void RequireOwned(ScriptableObjectConfigs root, ScriptableObjectData data)
        {
            RequireMember(root, data);
            if (!IsOwned(root, data))
            {
                throw new InvalidOperationException("This root does not own the target asset. Unlink the reference instead.");
            }
        }

        /// <summary>拒绝物理删除还包含其他对象的条目文件，避免连带删除非目标对象。</summary>
        private static void RequireSingleObjectFile(ScriptableObjectData data)
        {
            if (AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(data)).Any(item => item && !ReferenceEquals(item, data)))
            {
                throw new InvalidOperationException("The child file contains other objects; unlink it instead of deleting the file.");
            }
        }

        /// <summary>递归创建缺失的资产目录，创建失败时报告目标路径。</summary>
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path))))
            {
                throw new IOException("Could not create DataAssets folder: " + path);
            }
        }
    }
}
