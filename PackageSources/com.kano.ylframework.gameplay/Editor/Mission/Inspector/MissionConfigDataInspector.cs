using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Editor.Common;
using YLFramework.Mission;


namespace YLFramework.Editor.Mission
{
    /// <summary>任务条目 Inspector；普通资产面板只读，专用窗口通过序列化属性和字段绘制器编辑。</summary>
    [CustomEditor(typeof(MissionConfigData))]
    public class MissionConfigDataInspector : UnityEditor.Editor
    {

        private static MissionConfigDataInspector _instance;

        /// <summary>供任务配置窗口复用的按需创建 Inspector 实例，选择缓存需由窗口释放。</summary>
        public static MissionConfigDataInspector Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = CreateInstance<MissionConfigDataInspector>();
                }

                return _instance;
            }
        }

        private MissionConfigData _currentMissionConfigData;

        private SerializedObject _missionConfigSerializedObject = null;

        private List<SerializedProperty> _serializedProperties;

        private Vector2 _currentPosition;

        //private SerializedProperty m_Script = null;


        /// <summary>在普通资产 Inspector 中以禁用状态绘制默认内容。</summary>
        public override void OnInspectorGUI()
        {

            EditorGUI.BeginDisabledGroup(true);
            base.OnInspectorGUI();
            EditorGUI.EndDisabledGroup();

        }


        /// <summary>绘制窗口内的可编辑详情，应用字段变更，并同步 ID 选择、配表缓存及必要的树重载。</summary>
        internal void OnInspectorGUI(MissionConfigData data, ConfigsWindow window, MissionsConfig missionsConfig)
        {
            if (data == null)
            {
                ReleaseSelection();
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("空");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();

                return;
            }

            BindSelection(data);

            _currentPosition = GUILayout.BeginScrollView(_currentPosition);

            _missionConfigSerializedObject.Update();


            foreach (var property in _serializedProperties)
            {

                //if (property.displayName == "Script") continue;

                if (property.name == "parent" && property.objectReferenceValue == null)
                {
                    property.objectReferenceValue = missionsConfig;
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
                    _missionConfigSerializedObject.ApplyModifiedProperties();

                    if (property.name == "id")
                    {
                        window.SetSelection(property.intValue);
                    }

                    //EditorUtility.SetDirty(currentMissionConfigData);

                    if (_currentMissionConfigData.parent != null)
                    {
                        _currentMissionConfigData.parent.ClearCache();
                    }

                    if (customPropertyGui != null && customPropertyGui.ReloadTreeViewOnValueChange)
                    {
                        window.MissionListTree.Reload();
                    }
                }

            }

            GUILayout.EndScrollView();
        }

        private Dictionary<string, CustomPropertyGui> _drawers = new();
        /// <summary>为当前选择创建独立的任务领域绘制器，避免复用旧序列化字段上下文。</summary>
        private static Dictionary<string, CustomPropertyGui> CreateDrawers() => new()
        {
            ["id"] = new CustomMissionConfigDataIdGui(),
            ["parent"] = new CustomMissionConfigDataParentGui(),
            ["icon"] = new YLFramework.Editor.Domain.DomainResourceIconGui(),
            ["conditions"] = new CustomMissionConfigDataCompleteConditionGui(),
            ["missionTypeClassFullName"] = new CustomMissionConfigDataClassGui()
        };

        /// <summary>选择变化时先释放旧缓存，再为新条目创建 SerializedObject、属性列表和绘制器。</summary>
        internal void BindSelection(MissionConfigData data)
        {
            if (_currentMissionConfigData == data && _missionConfigSerializedObject != null)
            {
                return;
            }

            ReleaseSelection();
            if (!data)
            {
                return;
            }

            _currentMissionConfigData = data;
            _missionConfigSerializedObject = new SerializedObject(data);
            _serializedProperties = _missionConfigSerializedObject.GetAllSerializedProperty();
            _drawers = CreateDrawers();
        }

        /// <summary>释放当前 SerializedObject，并清除选择、属性特性及绘制器缓存。</summary>
        internal void ReleaseSelection()
        {
            _missionConfigSerializedObject?.Dispose();
            _missionConfigSerializedObject = null;
            _currentMissionConfigData = null;
            _serializedProperties = null;
            _attributes.Clear();
            _drawers.Clear();
        }
        /// <summary>Inspector 停用时释放当前序列化选择。</summary>
        private void OnDisable() => ReleaseSelection();

        private Dictionary<SerializedProperty, Dictionary<Type, Attribute>> _attributes = new Dictionary<SerializedProperty, Dictionary<Type, Attribute>>();

        /// <summary>读取并按属性及特性类型缓存字段特性，未找到的结果也缓存。</summary>
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
