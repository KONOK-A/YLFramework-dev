using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Attributes;
using YLFramework.Equipment;

namespace YLFramework.Editor.Equipment
{
    /// <summary>编辑装备定义及派生字段，按模式选择专用绘制器，并在修改后维护缓存与资产命名。</summary>
    [CustomEditor(typeof(EquipmentConfigData), true)]
    public sealed class EquipmentConfigDataInspector : UnityEditor.Editor
    {
        private readonly Dictionary<string, CustomPropertyGui> _drawers = new()
        {
            ["parent"] = new CustomEquipmentConfigDataParentGui(),
            ["id"] = new CustomEquipmentConfigDataIdGui(),
            ["icon"] = new CustomEquipmentConfigDataIconGui(),
            ["equipmentClassFullName"] = new CustomEquipmentConfigDataClassGui(),
            ["projectName"] = new CustomEquipmentConfigDataProjectNameGui(),
            ["assetName"] = new CustomEquipmentConfigDataAssetNameGui()
        };
        /// <summary>仅在未运行且未切换 PlayMode 时应用序列化编辑，清除所属配表缓存并保存当前定义。</summary>
        public override void OnInspectorGUI()
        {
            if (!target)
            {
                return;
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                serializedObject.Update();
                var iterator = serializedObject.GetIterator();
                var enter = true;
                while (iterator.NextVisible(enter))
                {
                    enter = false;
                    var property = iterator.Copy();
                    if (_drawers.TryGetValue(property.name, out var drawer))
                    {
                        drawer.OnPropertyGui(property);
                    }
                    else
                    {
                        using (new EditorGUI.DisabledScope(property.name == "m_Script"))
                        {
                            EditorGUILayout.PropertyField(property, true);
                        }
                    }
                }
                if (serializedObject.ApplyModifiedProperties())
                {
                    var data = (EquipmentConfigData)target;
                    if (data.parent)
                    {
                        data.parent.ClearCache();
                    }

                    data.Rename();
                    EditorUtility.SetDirty(data);
                    AssetDatabase.SaveAssetIfDirty(data);
                }
            }
        }
    }
}
