using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Equipment;

namespace YLFramework.Editor.Equipment
{
    /// <summary>为普通装备类提供作者侧类型筛选、脚本定位与 GUID 绑定刷新。</summary>
    public static class ScriptBindController
    {
        /// <summary>脚本域加载后延迟刷新绑定，避免在初始化回调内直接遍历修改资产。</summary>
        [InitializeOnLoadMethod]
        private static void Initialize() => EditorApplication.delayCall += RefreshBindings;

        /// <summary>判断类型是否为可用 public 无参构造创建的普通 IEquipment 类，排除 Unity 对象与开放泛型。</summary>
        public static bool IsEquipmentType(Type type) => type != null && type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters &&
            typeof(IEquipment).IsAssignableFrom(type) && !typeof(UnityEngine.Object).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null;

        /// <summary>查找与给定非空类型完全匹配的脚本 GUID，未找到时返回空字符串。</summary>
        public static string GetScriptGuid(Type type) => AssetDatabase.FindAssets(type.Name + " t:MonoScript")
            .FirstOrDefault(g =>
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(g));
                return script && script.GetClass() == type;
            }) ?? "";

        /// <summary>仅在未运行且未切换 PlayMode 时按已有 GUID 刷新 Script 定义的类型全名，逐项记录 Undo 并保存。</summary>
        public static void RefreshBindings()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            foreach (var root in EquipmentsConfigController.GetAllEquipmentsConfig())
            {
                foreach (var data in root.Equipments)
                {
                    if (!data || data.equipmentType != EquipmentType.Script || string.IsNullOrEmpty(data.equipmentClassGuid))
                    {
                        continue;
                    }

                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(data.equipmentClassGuid));
                    var type = script ? script.GetClass() : null;
                    if (!IsEquipmentType(type) || data.equipmentClassFullName == type.FullName)
                    {
                        continue;
                    }

                    Undo.RecordObject(data, "Refresh equipment binding");
                    data.equipmentClassFullName = type.FullName;
                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssetIfDirty(data);
                }
            }
        }
    }
}
