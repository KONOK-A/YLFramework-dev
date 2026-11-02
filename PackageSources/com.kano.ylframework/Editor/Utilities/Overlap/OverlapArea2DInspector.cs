using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.Editor.Common;
using YLFramework.Overlap;

namespace YLFramework.Editor.Overlap
{

    /// <summary>编辑由相邻端点定义的二维矩形检测区域，支持局部坐标点或 Transform 模式，并展示已有结果。</summary>
    [CustomEditor(typeof(OverlapArea2D))]
    public class OverlapArea2DInspector : UnityEditor.Editor
    {
        // Fix编码

        private List<SerializedProperty> _property;

        private SerializedProperty _mode;
        private SerializedProperty _positions;
        //private SerializedProperty mScript;

        private ReorderableList _reorderableList = null;

        private Transform _transform = null;

        private bool _showPositions = false;

        private OverlapArea2D _overlapArea2D;

        private bool _showResults;

        /// <summary>缓存当前检测器及其属性，为局部端点列表创建绘制、选择和添加回调。</summary>
        private void OnEnable()
        {
            _overlapArea2D = serializedObject.targetObject as OverlapArea2D;
            _transform = _overlapArea2D.transform;

            _property = serializedObject.GetAllSerializedProperty();
            _mode = serializedObject.FindProperty("mode");
            _positions = serializedObject.FindProperty("positions");

            //mScript = serializedObject.FindProperty("m_Script");

            _reorderableList = new ReorderableList(serializedObject, _positions, true, false, true, true);
            _reorderableList.headerHeight = 1;
            _reorderableList.drawElementCallback += OnDrawElementCallback;
            _reorderableList.onSelectCallback += OnSelectCallback;
            _reorderableList.onAddCallback += OnAddCallback;
        }

        /// <summary>按区域模式筛选端点属性，应用配置修改并借读组件的检测结果。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();


            foreach (var item in _property)
            {

                if (item.name == "positions" && _mode.enumValueIndex != 0)
                {
                    continue;
                }

                if (item.name == "transforms" && _mode.enumValueIndex != 1)
                {
                    continue;
                }

                if (item.name == "positions")
                {
                    _showPositions = EditorGUILayout.Foldout(_showPositions, "Positions");
                    if (_showPositions)
                    {
                        _reorderableList.DoLayoutList();
                    }

                    continue;
                }

                EditorGUI.BeginDisabledGroup(item.IsScript());
                EditorGUILayout.PropertyField(item);
                EditorGUI.EndDisabledGroup();
            }

            serializedObject.ApplyModifiedProperties();

            OverlapBase2DInspector.DrawResult(ref _showResults, _overlapArea2D.results);
        }

        /// <summary>绘制指定局部端点的坐标字段，并提交序列化缓冲。</summary>
        private void OnDrawElementCallback(Rect rect, int index, bool isActive, bool isFocused)
        {
            SerializedProperty property = _positions.GetArrayElementAtIndex(index);
            Vector3 vector3 = property.vector3Value;
            property.vector3Value = EditorGUI.Vector3Field(rect, string.Empty, vector3);
            property.serializedObject.ApplyModifiedProperties();
        }

        /// <summary>列表选择变化时请求 Scene 重绘，更新端点操作柄的选中颜色。</summary>
        private void OnSelectCallback(ReorderableList list)
        {
            if (SceneView.lastActiveSceneView)
            {
                SceneView.lastActiveSceneView.Repaint();
            }
        }

        /// <summary>新增端点，有既有端点时沿末段外推；修改留给外层 Inspector 应用。</summary>
        private void OnAddCallback(ReorderableList list)
        {

            int count = list.count;

            list.serializedProperty.InsertArrayElementAtIndex(list.count);

            SerializedProperty current = list.serializedProperty.GetArrayElementAtIndex(count);

            if (count == 1)
            {
                SerializedProperty last = list.serializedProperty.GetArrayElementAtIndex(count - 1);
                current.vector3Value = last.vector3Value + Vector3.right;
            }
            else if (count > 1)
            {
                SerializedProperty last1 = list.serializedProperty.GetArrayElementAtIndex(count - 2);
                SerializedProperty last2 = list.serializedProperty.GetArrayElementAtIndex(count - 1);
                Vector3 dir = last2.vector3Value - last1.vector3Value;
                if (dir == Vector3.zero)
                {
                    dir = Vector3.right;
                }

                current.vector3Value = last2.vector3Value + dir.normalized;
            }

        }


        /// <summary>为坐标模式的各局部端点绘制世界操作柄，写回并应用属性，拖动时同步列表选择。</summary>
        private void OnSceneGUI()
        {

            if (_mode.enumValueIndex != 0)
            {
                return;
            }

            for (int i = 0; i < _positions.arraySize; i++)
            {
                SerializedProperty property = _positions.GetArrayElementAtIndex(i);
                float size = HandleUtility.GetHandleSize(property.vector3Value) * 0.1f;

                Handles.color = _reorderableList.index == i ? Color.red : Color.green;

                EditorGUI.BeginChangeCheck();

                // 区域端点按局部坐标保存；只在操作柄交互时转为世界坐标，写回时必须逆变换。
                Vector3 world = _transform.localToWorldMatrix.MultiplyPoint(property.vector3Value);

                world = Handles.FreeMoveHandle(world, size, Vector3.one * 0.1f, Handles.CubeHandleCap);


                property.vector3Value = _transform.worldToLocalMatrix.MultiplyPoint(world);
                property.serializedObject.ApplyModifiedProperties();

                if (EditorGUI.EndChangeCheck())
                {
                    _reorderableList.index = i;
                }

            }

        }


    }

}
