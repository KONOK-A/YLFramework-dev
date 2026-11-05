using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Events;
using YLFramework.Editor.Common;
using YLFramework.Platform2D;

namespace YLFramework.Editor.Platform2D
{

    /// <summary>编辑移动对象的世界坐标路径或 Transform 路径，并提供路径校验、事件配置和 Scene 操作柄。</summary>
    [CustomEditor(typeof(Moveable), true)]
    public class MoveableInspector : UnityEditor.Editor
    {

        private List<SerializedProperty> _serializedProperties;

        //private SerializedProperty m_Script = null;

        private SerializedProperty _positionType = null;
        private SerializedProperty _transforms = null;
        private SerializedProperty _positions = null;

        private SerializedProperty _onStartRun = null;
        private SerializedProperty _onMove = null;
        private SerializedProperty _onPause = null;
        private SerializedProperty _onStop = null;

        private ReorderableList _reorderableList = null;

        //private bool foldout = false;

        private bool _showPositions = false;

        private Moveable _moveable;

        private Vector3 _snap = new Vector3(0.1f, 0.1f, 0.01f);

        /// <summary>缓存序列化属性，为本 Inspector 新建坐标列表并挂接绘制、添加与选择回调。</summary>
        private void OnEnable()
        {
            _serializedProperties = serializedObject.GetAllSerializedProperty();
            //m_Script = serializedObject.FindProperty("m_Script");

            _positionType = serializedObject.FindProperty("positionType");
            _transforms = serializedObject.FindProperty("transforms");
            _positions = serializedObject.FindProperty("positions");

            _onStartRun = serializedObject.FindProperty("onStartRun");
            _onMove = serializedObject.FindProperty("onMove");
            _onPause = serializedObject.FindProperty("onPause");
            _onStop = serializedObject.FindProperty("onStop");

            _reorderableList = new ReorderableList(serializedObject, _positions);
            _reorderableList.headerHeight = 1;
            _reorderableList.drawElementCallback += OnDrawElementCallback;
            _reorderableList.onAddCallback += OnAddCallback;
            _reorderableList.onSelectCallback += OnSelect;
            _moveable = target as Moveable;
        }




        /// <summary>绘制通用属性、当前路径模式及事件，应用序列化修改后显示路径校验结果。</summary>
        public override void OnInspectorGUI()
        {

            serializedObject.Update();

            //EditorGUI.BeginDisabledGroup(true);
            //EditorGUILayout.PropertyField(m_Script);
            //EditorGUI.EndDisabledGroup();

            foreach (var item in _serializedProperties)
            {
                //if (item.displayName == "Script") continue;
                if (item.name == _positionType.name)
                {
                    continue;
                }

                if (item.name == _transforms.name)
                {
                    continue;
                }

                if (item.name == _positions.name)
                {
                    continue;
                }

                if (item.name == _onStartRun.name)
                {
                    continue;
                }

                if (item.name == _onMove.name)
                {
                    continue;
                }

                if (item.name == _onPause.name)
                {
                    continue;
                }

                if (item.name == _onStop.name)
                {
                    continue;
                }

                EditorGUI.BeginDisabledGroup(item.IsScript());
                EditorGUILayout.PropertyField(item);
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.PropertyField(_positionType);

            if (_positionType.enumValueIndex == 0)
            {
                _showPositions = EditorGUILayout.Foldout(_showPositions, "Positions");

                if (_showPositions)
                {
                    _reorderableList.DoLayoutList();
                }
            }
            else
            {
                EditorGUILayout.PropertyField(_transforms);
            }

            _onStartRun.isExpanded = EditorGUILayout.Foldout(_onStartRun.isExpanded, "事件");

            if (_onStartRun.isExpanded)
            {
                EditorGUILayout.PropertyField(_onStartRun);
                EditorGUILayout.PropertyField(_onMove);
                EditorGUILayout.PropertyField(_onPause);
                EditorGUILayout.PropertyField(_onStop);
            }


            serializedObject.ApplyModifiedProperties();
            if (!_moveable.ValidatePath(out string error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }
        }

        /// <summary>编辑指定世界坐标路径点，值变化时应用到移动对象。</summary>
        private void OnDrawElementCallback(Rect rect, int index, bool isActive, bool isFocused)
        {
            EditorGUI.BeginChangeCheck();

            SerializedProperty property = _reorderableList.serializedProperty.GetArrayElementAtIndex(index);
            property.vector3Value = EditorGUI.Vector3Field(rect, "", property.vector3Value);

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
            }

        }

        /// <summary>添加世界坐标路径点，起点取对象当前位置，后续点沿已有方向外推并应用。</summary>
        private void OnAddCallback(ReorderableList list)
        {
            Vector3 target = Vector3.zero;

            int size = list.serializedProperty.arraySize;

            list.serializedProperty.InsertArrayElementAtIndex(size);
            SerializedProperty property = list.serializedProperty.GetArrayElementAtIndex(size);

            if (size == 0)
            {
                target = _moveable.transform.position;
            }
            else if (size == 1)
            {
                target = list.serializedProperty.GetArrayElementAtIndex(0).vector3Value + Vector3.right;
            }
            else
            {
                SerializedProperty last2 = list.serializedProperty.GetArrayElementAtIndex(size - 2);
                SerializedProperty last1 = list.serializedProperty.GetArrayElementAtIndex(size - 1);

                target = (last1.vector3Value - last2.vector3Value).normalized + last1.vector3Value;
            }

            property.vector3Value = target;
            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>为坐标模式下选中的有效路径点显示位置操作柄，通过序列化属性提交拖动结果。</summary>
        private void OnSceneGUI()
        {

            if (_positionType.enumValueIndex != 0 || _reorderableList.index < 0 || _reorderableList.index >= _positions.arraySize)
            {
                return;
            }

            SerializedProperty property = _reorderableList.serializedProperty.GetArrayElementAtIndex(_reorderableList.index);

            if (property == null)
            {
                return;
            }

            EditorGUI.BeginChangeCheck();


            float size = HandleUtility.GetHandleSize(property.vector3Value) * 0.1f;

            //Handles.color = Color.green;
            //property.vector3Value = Handles.FreeMoveHandle(property.vector3Value, Quaternion.identity, size, snap, Handles.CubeHandleCap);

            // 路径点本身就是世界坐标，不像平台捕获区域那样需要经对象 Transform 转回局部坐标。
            property.vector3Value = Handles.PositionHandle(property.vector3Value, Quaternion.identity);

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(target);
                serializedObject.ApplyModifiedProperties();
            }


        }

        /// <summary>选择列表项后请求活动 Scene 视图重绘，以更新对应位置操作柄。</summary>
        private void OnSelect(ReorderableList list)
        {
            if (SceneView.lastActiveSceneView)
            {
                SceneView.lastActiveSceneView.Repaint();
            }
        }

    }

}
