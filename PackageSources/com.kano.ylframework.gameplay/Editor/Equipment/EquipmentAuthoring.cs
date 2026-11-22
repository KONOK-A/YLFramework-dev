using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using YLFramework.Editor.Domain;
using YLFramework.Editor.Resource;
using YLFramework.Editor.Common;
using YLFramework.Equipment;
using YLFramework.Resource;

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
        /// <summary>绘制地址命名空间，选项来自官方 project/path 条目，不依赖旧 ResourceProject。</summary>
        internal static void DrawProject(SerializedProperty property)
        {
            EditorGUILayout.PropertyField(property);
            if (!EditorGUILayout.DropdownButton(new GUIContent("选择资源命名空间"), FocusType.Passive))
            {
                return;
            }

            try
            {
                var menu = new GenericMenu();
                var projects = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var entry in GetEntries(AddressableAssetSettingsDefaultObject.Settings))
                {
                    int separator = entry.address.IndexOf('/');
                    if (separator <= 0)
                        continue;
                    try
                    {
                        var address = new ResourceAddress(entry.address.Substring(0, separator), entry.address.Substring(separator + 1));
                        projects.Add(address.ProjectId);
                    }
                    catch (ArgumentException)
                    {
                        // 官方也允许不采用 project/path 的地址，这些不属于本选择器的命名空间。
                    }
                }
                foreach (string project in projects)
                    menu.AddItem(new GUIContent(project), project == property.stringValue, () => SetString(property, project));
                if (projects.Count == 0)
                    menu.AddDisabledItem(new GUIContent("没有 project/path 地址"));
                menu.ShowAsContext();
            }
            catch (Exception error)
            {
                EditorUtility.DisplayDialog("装备资源命名空间", error.Message, "关闭");
            }
        }
        /// <summary>从官方条目按命名空间和主资产类型列出地址，不在此验证 Prefab 装备组件。</summary>
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
                var paths = GetAddresses(AddressableAssetSettingsDefaultObject.Settings, project.stringValue, type);
                foreach (string path in paths)
                {
                    menu.AddItem(new GUIContent(path), path == address.stringValue, () => SetString(address, path));
                }
                if (paths.Length == 0)
                    menu.AddDisabledItem(new GUIContent("没有匹配的正式地址"));
                menu.ShowAsContext();
            }
            catch (Exception error)
            {
                EditorUtility.DisplayDialog("装备资源地址", error.Message, "关闭");
            }
        }

        /// <summary>只读解析正式条目；地址歧义拒绝，不修改全局配置或回退到旧构建映射。</summary>
        internal static string[] GetAddresses(AddressableAssetSettings settings, string projectId, Type type)
        {
            _ = new ResourceAddress(projectId, "selection");
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            string prefix = projectId + "/";
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in GetEntries(settings))
            {
                if (!entry.address.StartsWith(prefix, StringComparison.Ordinal) ||
                    !type.IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(entry.AssetPath)))
                    continue;
                _ = ResourceAddressablesAuthoring.FindUniqueEntry(settings, entry.address);
                string path = entry.address.Substring(prefix.Length);
                _ = new ResourceAddress(projectId, path);
                paths.Add(path);
            }
            return paths.ToArray();
        }

        private static List<AddressableAssetEntry> GetEntries(AddressableAssetSettings settings)
        {
            if (!settings)
                throw new InvalidOperationException("Create official Addressables settings before selecting an equipment address.");
            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            return entries;
        }
    }
}
