using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Common;
using YLFramework.Overlap;

namespace YLFramework.Editor.Overlap
{
    /// <summary>编辑二维检测器的序列化配置，并借读组件维护的检测结果，不在绘制时重新检测。</summary>
    [CustomEditor(typeof(OverlapBase2D), true)]
    public class OverlapBase2DInspector : UnityEditor.Editor
    {

        private List<SerializedProperty> _properties = null;


        private OverlapBase2D _overlap;

        private bool _showResults;

        // Fix编码

        /// <summary>启用 Inspector 时缓存可见顶层属性及当前二维检测器。</summary>
        protected virtual void OnEnable()
        {
            _properties = serializedObject.GetAllSerializedProperty();

            _overlap = serializedObject.targetObject as OverlapBase2D;
        }


        /// <summary>绘制属性并应用序列化修改，随后显示已有结果；不负责保存资产或场景文件。</summary>
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

        /// <summary>只读显示二维命中信息；定位按钮仅 Ping 碰撞体，不修改结果或对象选择。</summary>
        /// <param name="resultFoldout">由调用方保存并接收变化的结果折叠状态。</param>
        /// <param name="results">检测组件维护的有效结果列表，本方法不复制或清理它。</param>
        public static void DrawResult(ref bool resultFoldout, List<OverlapHit<Collider2D>> results)
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
