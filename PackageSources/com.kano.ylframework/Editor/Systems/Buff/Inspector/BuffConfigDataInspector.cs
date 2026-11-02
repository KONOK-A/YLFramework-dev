using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Buff;
using YLFramework.Editor.Common;


namespace YLFramework.Editor.Buff
{

    /// <summary>普通 Inspector 只读展示 Buff 定义，配表窗口通过专用入口启用字段绘制器。</summary>
    [CustomEditor(typeof(BuffConfigData), true)]
    public class BuffConfigDataInspector : UnityEditor.Editor
    {

        private static BuffConfigDataInspector _instance;

        /// <summary>供配表窗口复用的内嵌检查器实例，不绑定普通 Inspector 的 target。</summary>
        public static BuffConfigDataInspector Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = CreateInstance<BuffConfigDataInspector>();
                }

                return _instance;
            }
        }

        private BuffConfigData _currentBuffConfigData;

        private SerializedObject _currentSerializedObject;

        private List<SerializedProperty> _currentSerializeProperty;

        private Vector2 _currentPosition;

        //private SerializedProperty script = null;

        /// <summary>在普通 Inspector 中只读绘制定义，修改通过配表窗口进行。</summary>
        public override void OnInspectorGUI()
        {
            EditorGUI.BeginDisabledGroup(true);

            base.OnInspectorGUI();

            EditorGUI.EndDisabledGroup();
        }

        /// <summary>为配表窗口绑定单个定义并应用序列化编辑，同步选择和查询缓存。</summary>
        internal void OnInspectorGUI(BuffConfigData data, BuffConfigsWindow window, BuffsConfig currentBuffConfig)
        {

            if (data != null)
            {
                BindSelection(data);

                _currentSerializedObject.Update();

                _currentPosition = GUILayout.BeginScrollView(_currentPosition);

                for (int i = 0; i < _currentSerializeProperty.Count; i++)
                {

                    SerializedProperty property = _currentSerializeProperty[i];

                    if (property.name == "parent" && property.objectReferenceValue == null)
                    {
                        property.objectReferenceValue = currentBuffConfig;
                    }

                    if (GetPropertyCustomAttribute<HideInInspector>(property) != null)
                    {
                        continue;
                    }

                    CustomPropertyGui customPropertyGui = GetPropertyCustomAttribute<CustomPropertyGui>(property) ?? (_drawers.TryGetValue(property.name, out var drawer) ? drawer : null);
                    EditorGUI.BeginChangeCheck();

                    if (customPropertyGui != null)
                    {
                        customPropertyGui.OnPropertyGui(property);
                    }
                    else
                    {
                        EditorGUI.BeginDisabledGroup(property.IsScript());
                        EditorGUILayout.PropertyField(property);
                        EditorGUI.EndDisabledGroup();
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        _currentSerializedObject.ApplyModifiedProperties();

                        if (property.name == "id")
                        {
                            window.SetSelection(property.intValue);
                        }

                        if (_currentBuffConfigData.parent != null)
                        {
                            _currentBuffConfigData.parent.ClearCache();
                        }

                        if (customPropertyGui != null && customPropertyGui.ReloadTreeViewOnValueChange)
                        {
                            window.BuffListTree.Reload();
                        }
                    }
                }

                GUILayout.EndScrollView();

                _currentSerializedObject.ApplyModifiedProperties();
            }
            else
            {
                ReleaseSelection();
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(string.Format("未查询到Buff,id:{0}!", window.CurrentShowBuffId));
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
            }
        }


        private Dictionary<string, CustomPropertyGui> _drawers = new();
        /// <summary>为一次资产选择创建独立的字段绘制器，避免保留上个资产的序列化属性。</summary>
        private static Dictionary<string, CustomPropertyGui> CreateDrawers() => new()
        {
            ["id"] = new CustomBuffConfigDataIdGui(),
            ["parent"] = new CustomBuffConfigDataParentGui(),
            ["icon"] = new YLFramework.Editor.Domain.DomainResourceIconGui(),
            ["effects"] = new CustomBuffConfigDataEffectsGui(),
            ["buffClassFullName"] = new CustomBuffConfigDataClassGui()
        };

        private Dictionary<SerializedProperty, Dictionary<Type, Attribute>> _attributes = new Dictionary<SerializedProperty, Dictionary<Type, Attribute>>();
        /// <summary>选择变化时释放旧包装与缓存，再建立新定义的序列化属性列表。</summary>
        internal void BindSelection(BuffConfigData data)
        {
            if (_currentBuffConfigData == data && _currentSerializedObject != null)
            {
                return;
            }

            ReleaseSelection();
            if (!data)
            {
                return;
            }

            _currentBuffConfigData = data;
            _currentSerializedObject = new SerializedObject(data);
            _currentSerializeProperty = _currentSerializedObject.GetAllSerializedProperty();
            _drawers = CreateDrawers();
        }
        /// <summary>释放当前序列化包装，并清空属性、特性与绘制器缓存。</summary>
        internal void ReleaseSelection()
        {
            _currentSerializedObject?.Dispose();
            _currentSerializedObject = null;
            _currentBuffConfigData = null;
            _currentSerializeProperty = null;
            _attributes.Clear();
            _drawers.Clear();
        }
        /// <summary>检查器停用时解除当前资产绑定。</summary>
        private void OnDisable() => ReleaseSelection();

        /// <summary>读取并缓存属性对应字段的指定特性，未标注时也缓存空结果。</summary>
        private T GetPropertyCustomAttribute<T>(SerializedProperty property) where T : Attribute
        {
            if (!_attributes.ContainsKey(property))
            {
                _attributes.Add(property, new Dictionary<Type, Attribute>());
            }

            if (_attributes[property].ContainsKey(typeof(T)))
            {
                return _attributes[property][typeof(T)] as T;
            }

            T customPropertyGui = property.GetAttribute<T>();

            _attributes[property].Add(typeof(T), customPropertyGui);
            return customPropertyGui;
        }

    }

}
