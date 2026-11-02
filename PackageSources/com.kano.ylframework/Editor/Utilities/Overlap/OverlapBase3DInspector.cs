using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Common;
using YLFramework.Overlap;

namespace YLFramework.Editor.Overlap
{
    /// <summary>编辑三维检测器的序列化配置，并展示组件已有的命中结果，不执行新的物理查询。</summary>
    [CustomEditor(typeof(OverlapBase3D), true)]
    public class OverlapBase3DInspector : UnityEditor.Editor
    {
        // Fix编码
        private List<SerializedProperty> _properties = null;

        private OverlapBase3D _overlap;

        private bool _showResults;

        // Fix编码

        /// <summary>启用 Inspector 时缓存可见顶层属性及当前三维检测器。</summary>
        protected virtual void OnEnable()
        {
            _properties = serializedObject.GetAllSerializedProperty();
            //mScripts = serializedObject.FindProperty("m_Script");

            _overlap = serializedObject.targetObject as OverlapBase3D;
        }


        /// <summary>应用 Inspector 的序列化属性编辑并绘制结果，不负责保存资产或场景文件。</summary>
        public override void OnInspectorGUI()
        {

            serializedObject.Update();

            foreach (var property in _properties)
            {
                EditorGUI.BeginDisabledGroup(property.IsScript());
                EditorGUILayout.PropertyField(property);
                EditorGUI.EndDisabledGroup();
            }

            serializedObject.ApplyModifiedProperties();

            DrawResult(ref _showResults, _overlap.results);

        }

        /// <summary>只读展示三维命中信息，按钮仅用于 Ping 对应碰撞体。</summary>
        /// <param name="resultFoldout">由调用方保存并接收变化的结果折叠状态。</param>
        /// <param name="results">检测组件维护的有效结果列表，本方法不复制或清理它。</param>
        public static void DrawResult(ref bool resultFoldout, List<OverlapHit<Collider>> results)
        {
            resultFoldout = EditorGUILayout.Foldout(resultFoldout, "Results");
            if (resultFoldout)
            {
                var oldColor = GUI.color;
                GUI.color = Color.gray;

                if (results.Count == 0)
                {
                    EditorGUILayout.LabelField("Empty!");
                }

                foreach (var item in results)
                {
                    EditorGUILayout.LabelField("name", item.collider ? item.collider.name : "Missing Collider");
                    //EditorGUIUtility.PingObject(item.collider);
                    EditorGUILayout.Vector3Field("point", item.point);

                    if (GUILayout.Button("select"))
                    {
                        EditorGUIUtility.PingObject(item.collider);
                    }
                }
                GUI.color = oldColor;
            }
        }
    }
}
