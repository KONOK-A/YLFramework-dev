using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Domain;
using YLFramework.Editor.Resource;
using YLFramework.Editor.Common;
using YLFramework.Equipment;

namespace YLFramework.Editor.Equipment
{
    /// <summary>为装备作者工具解析数据类型与资源地址，并维护所属资产的编辑操作。</summary>
    internal static class EquipmentAuthoring
    {
        /// <summary>解析配表绑定的具体数据类型；空绑定使用基础类型，失效绑定抛错。</summary>
        internal static Type GetDataType(this EquipmentsConfig root)
        {
            if (string.IsNullOrEmpty(root.typeScriptGuid))
            {
                return typeof(EquipmentConfigData);
            }

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(root.typeScriptGuid));
            var type = script ? script.GetClass() : null;
            if (type == null || !typeof(EquipmentConfigData).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
            {
                throw new InvalidOperationException($"装备配表数据类型失效: {root.name}, GUID={root.typeScriptGuid}");
            }

            return type;
        }
        /// <summary>按 ID 重命名所属配表拥有的条目并清缓存，不改动外部借用资产的名称。</summary>
        internal static void Rename(this EquipmentConfigData data)
        {
            if (!data.parent || data.name == data.id.ToString())
            {
                return;
            }

            DomainAssetOwnership.RenameEntry(data.parent, data, data.id.ToString(), EquipmentsConfigController.GetEquipmentConfigDataPath(data.parent));
            data.parent.ClearCache();
        }
        /// <summary>校验 Assets 相对目录的层级写法，并通过 AssetDatabase 逐层补建目录。</summary>
        internal static void EnsureFolder(string path)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new ArgumentException("Expected an Assets folder.", nameof(path));
            }

            var current = "Assets";
            foreach (var part in path.Substring(7).Split('/'))
            {
                if (part.Length == 0 || part == "." || part == "..")
                {
                    throw new ArgumentException("Invalid asset folder.", nameof(path));
                }

                var next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, part);
                }

                current = next;
            }
        }
        /// <summary>按目标对象和属性路径重新建立序列化包装，应用菜单选择的字符串值。</summary>
        internal static void SetString(SerializedProperty property, string value)
        {
            var target = property.serializedObject.targetObject;
            var path = property.propertyPath;
            if (!target)
            {
                return;
            }

            using var serialized = new SerializedObject(target);
            serialized.FindProperty(path).stringValue = value;
            serialized.ApplyModifiedProperties();
        }
        /// <summary>绘制资源项目字段，并列出项目内 ResourceProject 的项目标识供选择。</summary>
        internal static void DrawProject(SerializedProperty property)
        {
            EditorGUILayout.PropertyField(property);
            if (!EditorGUILayout.DropdownButton(new GUIContent("选择 YL 资源项目"), FocusType.Passive))
            {
                return;
            }

            var menu = new GenericMenu();
            foreach (var guid in AssetDatabase.FindAssets("t:ResourceProject"))
            {
                var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(guid));
                if (!project)
                {
                    continue;
                }

                var id = project.projectId;
                menu.AddItem(new GUIContent(id), id == property.stringValue, () => SetString(property, id));
            }
            menu.ShowAsContext();
        }
        /// <summary>从唯一匹配项目的正式构建映射中按资产类型列出地址，不在此验证 Prefab 装备组件。</summary>
        internal static void DrawAddress(SerializedProperty project, SerializedProperty address, Type type)
        {
            EditorGUILayout.PropertyField(address);
            if (!EditorGUILayout.DropdownButton(new GUIContent("选择正式资源地址"), FocusType.Passive))
            {
                return;
            }

            var menu = new GenericMenu();
            try
            {
                var projects = AssetDatabase.FindAssets("t:ResourceProject").Select(g => AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(g)))
                    .Where(p => p && p.projectId == project.stringValue).ToArray();
                if (projects.Length != 1)
                {
                    throw new InvalidOperationException("请选择唯一的 YL ResourceProject: " + project.stringValue);
                }

                foreach (var entry in ResourceBuildMap.Create(projects[0]).Assets.Where(a => type.IsAssignableFrom(a.AssetType)))
                {
                    var path = entry.Address.Path;
                    menu.AddItem(new GUIContent(path), path == address.stringValue, () => SetString(address, path));
                }
                menu.ShowAsContext();
            }
            catch (Exception error)
            {
                EditorUtility.DisplayDialog("装备资源地址", error.Message, "关闭");
            }
        }
    }
}
