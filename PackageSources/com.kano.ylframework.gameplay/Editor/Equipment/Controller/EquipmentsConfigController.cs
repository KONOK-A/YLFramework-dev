using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Domain;
using YLFramework.Editor.Common;
using YLFramework.Equipment;

namespace YLFramework.Editor.Equipment
{
    /// <summary>维护装备配表的定义资产、全项目 ID 分配及显式目录刷新，不创建运行装备。</summary>
    public static class EquipmentsConfigController
    {
        /// <summary>扫描并返回项目内所有可加载的装备配表，不修改或保存资产。</summary>
        public static List<EquipmentsConfig> GetAllEquipmentsConfig() => AssetDatabase.FindAssets("t:" + typeof(EquipmentsConfig).FullName)
            .Select(g => AssetDatabase.LoadAssetAtPath<EquipmentsConfig>(AssetDatabase.GUIDToAssetPath(g))).Where(c => c).ToList();

        /// <summary>查询项目内全部装备配表是否已有指定 ID 的有效定义。</summary>
        public static bool ContainsId(int id) => GetAllEquipmentsConfig().Any(c => c.Equipments.Any(d => d && d.id == id));
        /// <summary>取得配表绑定的数据类型，未绑定时使用基础类型，失效绑定抛错。</summary>
        public static Type GetDataType(EquipmentsConfig root) => EquipmentAuthoring.GetDataType(root);

        /// <summary>按已保存的 Assets 主配表 GUID 取得定义目录，不自动创建目录。</summary>
        public static string GetEquipmentConfigDataPath(EquipmentsConfig root)
        {
            if (!root || !AssetDatabase.IsMainAsset(root))
            {
                throw new InvalidOperationException("装备配表必须是已保存的主资产。");
            }

            var path = AssetDatabase.GetAssetPath(root);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("只允许编辑 Assets 内的装备配表。");
            }

            return "Assets/YLFrameworkData/Equipment/" + AssetDatabase.AssetPathToGUID(path);
        }

        /// <summary>从本表最大 ID 与起始基数之后查找全项目未占用的 ID，整数溢出时抛错。</summary>
        public static int GenerateId(EquipmentsConfig root)
        {
            var next = root.Equipments.Where(d => d).Select(d => d.id).Append(root.startId).Max();
            var occupied = new HashSet<int>(GetAllEquipmentsConfig().SelectMany(c => c.Equipments).Where(d => d).Select(d => d.id));
            do
            {
                next = checked(next + 1);
            } while (occupied.Contains(next));
            return next;
        }

        /// <summary>创建符合配表类型限制的具体数据实例，再交给实例入口保存并登记。</summary>
        /// <param name="type">可实例化且符合配表数据基类约束的配置类型。</param>
        /// <param name="root">已保存的目标配表。</param>
        /// <returns>分配给新定义的全项目未占用 ID。</returns>
        public static int CreateEquipmentConfigData(Type type, EquipmentsConfig root)
        {
            if (type == null || !root.GetDataType().IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
            {
                throw new InvalidOperationException("无效的装备配置类型: " + type);
            }

            var data = (EquipmentConfigData)ScriptableObject.CreateInstance(type);
            try
            {
                return CreateEquipmentConfigData(data, root);
            }
            catch
            {
                if (data && !EditorUtility.IsPersistent(data))
                {
                    UnityEngine.Object.DestroyImmediate(data);
                }
                throw;
            }
        }

        /// <summary>把尚未持久化的独立定义写为本根目录下的资产，登记 Undo、配表引用并保存。</summary>
        /// <param name="data">待保存且符合配表数据类型的实例。</param>
        /// <param name="root">位于 Assets 内且已保存为主资产的配表。</param>
        /// <returns>新分配的装备 ID。</returns>
        /// <remarks>资产创建、列表登记与保存依次执行，异常不承诺整体回滚。</remarks>
        public static int CreateEquipmentConfigData(EquipmentConfigData data, EquipmentsConfig root)
        {
            var folder = GetEquipmentConfigDataPath(root);
            if (!data || EditorUtility.IsPersistent(data))
            {
                throw new InvalidOperationException("创建入口要求尚未保存的独立装备定义。");
            }

            if (!root.GetDataType().IsInstanceOfType(data))
            {
                throw new InvalidOperationException("装备类型不符合本配表数据类型。");
            }

            var id = GenerateId(root);
            EquipmentAuthoring.EnsureFolder(folder);
            data.id = id;
            data.parent = root;
            data.name = id.ToString();
            if (string.IsNullOrEmpty(data.equipmentName))
            {
                data.equipmentName = "装备:" + id;
            }

            AssetDatabase.CreateAsset(data, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + id + ".asset"));
            Undo.RegisterCreatedObjectUndo(data, "Create equipment");
            Undo.RecordObject(root, "Add equipment");
            root.Equipments.Add(data);
            root.ClearCache();
            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssetIfDirty(data);
            AssetDatabase.SaveAssetIfDirty(root);
            return id;
        }

        /// <summary>复制定义并在目标配表中创建新资产，分配新 ID 与表内不重名的显示名。</summary>
        public static int CopyEquipmentConfigData(EquipmentConfigData source, EquipmentsConfig root)
        {
            var copy = UnityEngine.Object.Instantiate(source);
            copy.equipmentName = GetCopyName(source.equipmentName, root);
            try
            {
                return CreateEquipmentConfigData(copy, root);
            }
            catch
            {
                if (copy && !EditorUtility.IsPersistent(copy))
                {
                    UnityEngine.Object.DestroyImmediate(copy);
                }
                throw;
            }
        }

        /// <summary>按归属删除独立文件或同根子资产，外部借用资产仅解除引用；独立文件另记录 Undo 恢复资料。</summary>
        public static void DeleteEquipmentConfigData(EquipmentConfigData data, EquipmentsConfig root)
        {
            var folder = GetEquipmentConfigDataPath(root);
            var path = AssetDatabase.GetAssetPath(data);
            if (data && path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal) && AssetDatabase.IsMainAsset(data) && AssetDatabase.LoadAllAssetsAtPath(path).Length == 1)
            {
                EquipmentAssetDeletion.Delete(data, root, path);
            }
            else
            {
                DomainAssetOwnership.DeleteEntry(root, root.Equipments, data, folder);
            }

            root.ClearCache();
        }

        /// <summary>显式扫描本根目录，保留已有顺序并按 GUID 顺序追加遗漏的本根定义，校验后记录 Undo 并保存。</summary>
        /// <remarks>不自动清除原列表中的空项或重复 ID，发现这些问题会在写回前抛错。</remarks>
        public static void RefreshEntries(EquipmentsConfig root)
        {
            var folder = GetEquipmentConfigDataPath(root);
            if (!AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            var next = new List<EquipmentConfigData>(root.Equipments);
            foreach (var guid in AssetDatabase.FindAssets("t:" + typeof(EquipmentConfigData).FullName, new[] { folder }).OrderBy(g => g, StringComparer.Ordinal))
            {
                var data = AssetDatabase.LoadAssetAtPath<EquipmentConfigData>(AssetDatabase.GUIDToAssetPath(guid));
                if (data && ReferenceEquals(data.parent, root) && !next.Contains(data))
                {
                    next.Add(data);
                }
            }
            var ids = new HashSet<int>();
            foreach (var data in next)
            {
                if (!data || !ids.Add(data.id))
                {
                    throw new InvalidOperationException("刷新装备配表发现空条目或重复 ID: " + (data ? data.id.ToString() : "Missing"));
                }
            }

            if (next.SequenceEqual(root.Equipments))
            {
                return;
            }

            Undo.RecordObject(root, "Refresh equipment entries");
            root.Equipments.Clear();
            root.Equipments.AddRange(next);
            root.ClearCache();
            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssetIfDirty(root);
        }

        /// <summary>在 Assets 目录下以唯一文件路径创建并选中资产，不登记配表关系或创建 Undo 记录。</summary>
        /// <param name="value">尚待保存的数据对象。</param>
        /// <param name="path">保存目录，不含文件名；不存在时逐层创建。</param>
        /// <param name="ping">是否在 Project 窗口高亮新资产。</param>
        public static void CreateAsset(ScriptableObject value, string path, bool ping = true)
        {
            EquipmentAuthoring.EnsureFolder(path);
            AssetDatabase.CreateAsset(value, AssetDatabase.GenerateUniqueAssetPath(path.TrimEnd('/') + "/" + value.GetType().Name + ".asset"));
            Selection.activeObject = value;
            if (ping)
            {
                EditorGUIUtility.PingObject(value);
            }
        }

        /// <summary>去掉已有数字后缀，再从一开始查找目标表内未占用的复制名称。</summary>
        public static string GetCopyName(string name, EquipmentsConfig root)
        {
            var stem = Regex.Replace(name ?? "装备", @"\s*\([0-9]+\)$", "").TrimEnd();
            var i = 1;
            while (root.Equipments.Any(d => d && d.equipmentName == stem + " (" + i + ")"))
            {
                i++;
            }

            return stem + " (" + i + ")";
        }
    }
}
