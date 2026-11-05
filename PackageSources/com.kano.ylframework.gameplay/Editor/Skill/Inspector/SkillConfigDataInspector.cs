using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Editor.Common;
using YLFramework.Skill;


namespace YLFramework.Editor.Skill
{

    /// <summary>技能条目及派生数据的 Inspector，普通面板只读，专用窗口使用领域绘制器编辑源定义。</summary>
    [CustomEditor(typeof(SkillConfigData), true)]
    public class SkillConfigDataInspector : UnityEditor.Editor
    {

        private static SkillConfigDataInspector _instance;

        /// <summary>供技能配置窗口按需创建并共享的 Inspector，选择缓存由窗口停用时释放。</summary>
        public static SkillConfigDataInspector Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = CreateInstance<SkillConfigDataInspector>();
                }

                return _instance;
            }
        }

        private SkillConfigData _currentSkillConfigData;
        //private Type currentSkillConfigDataType;
        private SerializedObject _currentSerializedObject;
        private List<SerializedProperty> _currentSerializeProperty;
        //private Vector2 currentPosition;

        //private SerializedProperty script = null;

        //private Dictionary<string,CustomPropertyGui> custom_property = new Dictionary<string,CustomPropertyGui>();


        /// <summary>在普通资产 Inspector 中以禁用状态绘制默认数据。</summary>
        public override void OnInspectorGUI()
        {
            EditorGUI.BeginDisabledGroup(true);

            base.OnInspectorGUI();

            EditorGUI.EndDisabledGroup();

            //if (GUILayout.Button("编辑技能"))
            //{
            //    ConfigsWindow window = EditorWindow.GetWindow<ConfigsWindow>();
            //    window.Show();

            //    SkillConfigData currentSkill = target as SkillConfigData;
            //    if (currentSkill.parent != null)
            //    {
            //        string asset_path = AssetDatabase.GetAssetPath(currentSkill.parent);
            //        ConfigsWindow.SelectGUID = AssetDatabase.AssetPathToGUID(asset_path);
            //        window.currentSkillConfig = null;
            //    }
            //    window.SetSelection(currentSkill.id);
            //}
        }

        /// <summary>绘制窗口中的技能详情，应用字段修改并更新 ID 选择、配表缓存和需要重载的列表树。</summary>
        internal void OnInspectorGUI(SkillConfigData data, ConfigsWindow window, SkillsConfig skillsConfig)
        {
            if (data != null)
            {
                BindSelection(data);

                _currentSerializedObject.Update();

                //EditorGUI.BeginDisabledGroup(true);
                //EditorGUILayout.PropertyField(script);
                //EditorGUI.EndDisabledGroup();

                for (int i = 0; i < _currentSerializeProperty.Count; i++)
                {
                    SerializedProperty property = _currentSerializeProperty[i];

                    //if (property.displayName == "Script") continue;


                    if (property.name == "parent" && property.objectReferenceValue == null)
                    {
                        property.objectReferenceValue = skillsConfig;
                    }

                    if (GetPropertyCustomAttribute<HideInInspector>(property) != null)
                    {
                        continue;
                    }

                    CustomPropertyGui customPropertyGui = GetPropertyCustomAttribute<CustomPropertyGui>(property) ?? GetDomainDrawer(property.name);
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

                        //EditorUtility.SetDirty(currentSkillConfigData);

                        if (_currentSkillConfigData.parent != null)
                        {
                            _currentSkillConfigData.parent.ClearCache();
                        }

                        if (customPropertyGui != null && customPropertyGui.ReloadTreeViewOnValueChange)
                        {
                            window.SkillListTree.Reload();
                        }
                    }
                }
            }
            else
            {
                ReleaseSelection();
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("空!");
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
            }
        }


        private Dictionary<SerializedProperty, Dictionary<Type, Attribute>> _attributes = new Dictionary<SerializedProperty, Dictionary<Type, Attribute>>();
        private Dictionary<string, CustomPropertyGui> _drawers = new();
        /// <summary>为当前技能选择创建新的领域绘制器，避免保留上次选择的字段上下文。</summary>
        private static Dictionary<string, CustomPropertyGui> CreateDrawers() => new()
        {
            ["id"] = new CustomSkillConfigDataIdGui(),
            ["parent"] = new CustomSkillConfigDataParentGui(),
            ["icon"] = new YLFramework.Editor.Domain.DomainResourceIconGui(),
            ["simultaneousSkillIds"] = new CustomSkillConfigDataSkillGui(),
            ["skillClassFullName"] = new CustomSkillConfigDataSkillClassGui()
        };
        /// <summary>按字段名取得内建技能绘制器，未登记时返回 null。</summary>
        private CustomPropertyGui GetDomainDrawer(string name) => _drawers.TryGetValue(name, out var drawer) ? drawer : null;

        /// <summary>切换选择时释放旧序列化缓存，并建立新条目的 SerializedObject、属性列表和绘制器。</summary>
        internal void BindSelection(SkillConfigData data)
        {
            if (_currentSkillConfigData == data && _currentSerializedObject != null)
            {
                return;
            }

            ReleaseSelection();
            if (!data)
            {
                return;
            }

            _currentSkillConfigData = data;
            _currentSerializedObject = new SerializedObject(data);
            _currentSerializeProperty = _currentSerializedObject.GetAllSerializedProperty();
            _drawers = CreateDrawers();
        }

        /// <summary>释放当前 SerializedObject，清除选择、属性特性和领域绘制器缓存。</summary>
        internal void ReleaseSelection()
        {
            _currentSerializedObject?.Dispose();
            _currentSerializedObject = null;
            _currentSkillConfigData = null;
            _currentSerializeProperty = null;
            _attributes.Clear();
            _drawers.Clear();
        }
        /// <summary>Inspector 停用时释放当前序列化选择。</summary>
        private void OnDisable() => ReleaseSelection();

        /// <summary>读取并按序列化属性和特性类型缓存字段特性，未命中结果也保留。</summary>
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

            var customPropertyGui = property.GetAttribute<T>();

            _attributes[property].Add(typeof(T), customPropertyGui);
            return customPropertyGui;
        }
    }

}
