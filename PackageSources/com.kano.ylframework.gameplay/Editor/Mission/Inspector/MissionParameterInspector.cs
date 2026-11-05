using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Mission;

namespace YLFramework.Editor.Mission
{
    /// <summary>在任务窗口内编辑配表中的参数元素，维护序列化选择和自定义数据列表缓存。</summary>
    public class MissionParameterInspector
    {

        private static SerializedObject _serializedObject;

        private static MissionsConfig _currentMissionConfig;

        private static float _updateTime;

        private static ParamListTree _paramListTree;

        private static int _lastIndex;

        private static ReorderableList _reorderableList = null;



        /// <summary>定位配表内参数元素，绘制配置并应用序列化修改；参数改名交由参数树同步引用。</summary>
        public static void OnInspectorGUI(MissionsConfig missionsConfig, MissionParameter missionParameter, ParamListTree paramListTree)
        {
            if (missionParameter == null)
            {

                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("空");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();

                return;
            }

            int paramIndex = missionsConfig.parameters.IndexOf(missionParameter);

            MissionParameterInspector._paramListTree = paramListTree;

            if (_currentMissionConfig != missionsConfig)
            {
                _serializedObject?.Dispose();
                _currentMissionConfig = missionsConfig;
                _serializedObject = null;
                _reorderableList = null;
            }

            if (_serializedObject == null)
            {
                _serializedObject = new SerializedObject(missionsConfig);
            }

            _serializedObject.Update();

            if (_lastIndex != paramIndex)
            {
                _updateTime = Time.realtimeSinceStartup;
                _lastIndex = paramIndex;
                _reorderableList = null;
            }

            SerializedProperty param = _serializedObject.FindProperty("parameters");

            SerializedProperty property = param.GetArrayElementAtIndex(paramIndex);

            if (property == null)
            {
                return;
            }

            EditorGUI.BeginChangeCheck();

            //EditorGUILayout.PropertyField(property.FindPropertyRelative("name"));

            OnInspectorId(property.FindPropertyRelative("name"));

            EditorGUILayout.PropertyField(property.FindPropertyRelative("type"));

            EditorGUILayout.PropertyField(property.FindPropertyRelative("onlySetInProgress"));

            EditorGUILayout.PropertyField(property.FindPropertyRelative("onlySetLarger"));

            EditorGUILayout.PropertyField(property.FindPropertyRelative("description"));

            OnInspectorCustomData(property.FindPropertyRelative("customData"));


            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(missionsConfig);
            }

            _serializedObject.ApplyModifiedProperties();
        }


        /// <summary>用延迟输入编辑参数名称，选择索引变化后的首秒内不提交改名。</summary>
        private static void OnInspectorId(SerializedProperty data)
        {
            GUILayout.Space(10);

            EditorGUILayout.LabelField(string.Empty, "参数名称", "BoldLabel");

            string name = data.stringValue;

            EditorGUI.BeginChangeCheck();
            name = EditorGUILayout.DelayedTextField("name", data.stringValue);
            if (EditorGUI.EndChangeCheck() && Time.realtimeSinceStartup - _updateTime > 1)
            {
                _paramListTree.RenameParam(data.stringValue, name);
                _paramListTree.Reload();
            }
        }

        /// <summary>按当前序列化自定义数据字段创建并绘制可重排列表。</summary>
        private static void OnInspectorCustomData(SerializedProperty data)
        {
            GUILayout.Space(10);

            EditorGUILayout.LabelField(string.Empty, "自定义数据(Custom_data)", "BoldLabel");


            if (_reorderableList == null)
            {
                _reorderableList = new ReorderableList(_serializedObject, data);
                _reorderableList.headerHeight = 1;
                _reorderableList.drawElementCallback = ElementCallbackDelegate;
            }

            _reorderableList.DoLayoutList();
        }

        /// <summary>确认元素索引有效后绘制一项自定义数据。</summary>
        private static void ElementCallbackDelegate(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= _reorderableList.serializedProperty.arraySize)
            {
                return;
            }

            SerializedProperty serialized = _reorderableList.serializedProperty.GetArrayElementAtIndex(index);
            EditorGUI.PropertyField(rect, serialized);


        }

        /// <summary>释放配表 SerializedObject，并清空参数选择、树引用和重排列表。</summary>
        internal static void ReleaseSelection()
        {
            _serializedObject?.Dispose();
            _serializedObject = null;
            _currentMissionConfig = null;
            _paramListTree = null;
            _reorderableList = null;
            _lastIndex = -1;
        }

    }

}
