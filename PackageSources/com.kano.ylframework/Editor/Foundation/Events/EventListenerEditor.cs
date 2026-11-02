using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Events;

namespace YLFramework.Editor.Foundation
{
    /// <summary>以 Unity 原生序列化展示事件中心，保持派生字段、多选、Undo 与 Prefab 语义。</summary>
    [CustomEditor(typeof(EventListener), true)]
    [CanEditMultipleObjects]
    public sealed class EventListenerEditor : UnityEditor.Editor
    {
        private const string ExpandedKey = "YLFramework.EventListenerEditor.Expanded";
        private const string TabKey = "YLFramework.EventListenerEditor.Tab";
        private static readonly string[] _tabs = { "鼠标事件", "碰撞事件", "Trigger事件" };
        private static readonly (string Name, GUIContent Label)[][] _eventFields =
        {
            new[]
            {
                ("pointerEnter", new GUIContent("指针进入")), ("pointerExit", new GUIContent("指针离开")),
                ("pointerClick", new GUIContent("指针点击")), ("pointerDown", new GUIContent("指针按下")),
                ("pointerUp", new GUIContent("指针抬起")), ("beginDrag", new GUIContent("开始拖拽")),
                ("drag", new GUIContent("拖拽中")), ("endDrag", new GUIContent("结束拖拽"))
            },
            new[]
            {
                ("collisionEnter", new GUIContent("碰撞进入")), ("collisionStay", new GUIContent("碰撞保持")),
                ("collisionExit", new GUIContent("碰撞离开")), ("collisionEnter2D", new GUIContent("碰撞进入2D")),
                ("collisionStay2D", new GUIContent("碰撞保持2D")), ("collisionExit2D", new GUIContent("碰撞离开2D"))
            },
            new[]
            {
                ("triggerEnter", new GUIContent("Trigger进入")), ("triggerStay", new GUIContent("Trigger保持")),
                ("triggerExit", new GUIContent("Trigger离开")), ("triggerEnter2D", new GUIContent("Trigger进入2D")),
                ("triggerStay2D", new GUIContent("Trigger保持2D")), ("triggerExit2D", new GUIContent("Trigger离开2D"))
            }
        };
        private static readonly HashSet<string> _eventPropertyPaths = new HashSet<string>(
            _eventFields.SelectMany(group => group).Select(entry => entry.Name));
        private bool _isExpanded;
        private int _tab;

        /// <summary>从 Editor 会话状态恢复折叠和页签选择，不将界面状态写入组件或资源。</summary>
        private void OnEnable()
        {
            _isExpanded = SessionState.GetBool(ExpandedKey, true);
            _tab = Mathf.Clamp(SessionState.GetInt(TabKey, 0), 0, _tabs.Length - 1);
        }

        /// <summary>按页签绘制事件及其余可见字段，通过 SerializedObject 更新和应用序列化修改。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            _isExpanded = EditorGUILayout.Foldout(_isExpanded, "Unity事件中心", true);
            SessionState.SetBool(ExpandedKey, _isExpanded);
            if (_isExpanded)
            {
                _tab = GUILayout.Toolbar(_tab, _tabs);
                SessionState.SetInt(TabKey, _tab);
                foreach (var entry in _eventFields[_tab])
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(entry.Name), entry.Label, true);
                }
            }
            foreach (var property in AdditionalProperties())
            {
                using (property)
                {
                    EditorGUILayout.PropertyField(property, true);
                }
            }
            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>枚举非事件区的顶层可见属性，保留物理层过滤及派生组件的序列化字段。</summary>
        /// <returns>独立的属性副本；调用方负责在绘制后释放每个副本，内部遍历器由本方法释放。</returns>
        private IEnumerable<SerializedProperty> AdditionalProperties()
        {
            using (var iterator = serializedObject.GetIterator())
            {
                if (!iterator.NextVisible(true))
                {
                    yield break;
                }

                do
                {
                    if (iterator.propertyPath != "m_Script" && !_eventPropertyPaths.Contains(iterator.propertyPath))
                    {
                        yield return iterator.Copy();
                    }
                } while (iterator.NextVisible(false));
            }
        }
    }
}
