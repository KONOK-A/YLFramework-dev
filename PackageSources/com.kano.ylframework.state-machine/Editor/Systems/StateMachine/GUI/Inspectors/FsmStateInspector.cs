using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>检查节点名称与脚本绑定；以配置资产记录修改，不把状态脚本作为组件绘制。</summary>
    [CustomEditor(typeof(FsmStateInspectorHelper))]
    public class FsmStateInspector : UnityEditor.Editor
    {
        //private string stateName;

        private ReorderableList _reorderableList;


        internal static Rect PopupRect;

        private GUIContent _btnAddStateScript = new GUIContent("Add State Script");

        private GUIStyle _projectBrowserHeaderBgMiddle = null;

        private GUIStyle _headerStyle = null;

        private GUIStyle _prefixLabel = null;

        private GUIContent _scriptGuiContent = new GUIContent();

        private Vector2 _scroll;

        /// <summary>检查启用时的选择代理；脚本列表由实际 Inspector 绘制流程读取。</summary>
        private void OnEnable()
        {
            FsmStateInspectorHelper helper = target as FsmStateInspectorHelper;
            if (helper == null)
            {
                return;
            }

            //reorderableList = new ReorderableList(helper.node.StateScripts, typeof(FSMStateScriptInfo), true, true, true, true);
            //reorderableList.drawHeaderCallback += OnDrawHeaderCallback;
            //reorderableList.onAddCallback += this.OnAddCallback;
            //reorderableList.onRemoveCallback += this.OnRemoveCallback;
            //reorderableList.drawElementCallback += this.DrawItem;
        }

        /// <summary>按 GUID 显示绑定脚本和缺失诊断，提供脚本添加与移除入口；运行期和内置节点禁用编辑。</summary>
        public override void OnInspectorGUI()
        {
            //base.OnInspectorGUI();

            if (_projectBrowserHeaderBgMiddle == null)
            {
                _projectBrowserHeaderBgMiddle = new GUIStyle(StateStyles.Named("AC BoldHeader", EditorStyles.boldLabel));
            }

            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(StateStyles.Named("IconButton", EditorStyles.miniButton));
            }

            if (_prefixLabel == null)
            {
                _prefixLabel = new GUIStyle(StateStyles.Named("PrefixLabel", EditorStyles.label));
                _prefixLabel.richText = true;
            }

            FsmStateInspectorHelper helper = target as FsmStateInspectorHelper;
            if (helper == null || !helper.controller || helper.node == null || !helper.controller.states.Contains(helper.node))
            {
                return;
            }

            bool disabled = EditorApplication.isPlaying || helper.node.IsAnyState || helper.node.IsEntryState || helper.node.IsUpState;

            EditorGUI.BeginDisabledGroup(disabled);

            //GUILayout.Space(-10);
            //scroll = GUILayout.BeginScrollView(scroll);
            Vector2 mousePosition = Event.current.mousePosition;
            helper.node.RefreshStateScripts(helper.controller);
            foreach (var item in helper.node.stateScripts)
            {

                string path = AssetDatabase.GUIDToAssetPath(item.guid);
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null)
                {
                    EditorGUILayout.HelpBox($"State '{helper.node.name}'.StateScripts: missing MonoScript GUID '{item.guid}', type '{item.className}'.", MessageType.Error);
                    continue;
                }
                Type type = script.GetClass();
                if (type == null)
                {
                    continue;
                }

                var r = EditorGUILayout.BeginHorizontal(GUILayout.Height(25));
                r.x = 0;
                r.width += 30;
                GUI.Box(r, string.Empty, _projectBrowserHeaderBgMiddle);
                GUILayout.Space(-10);
                GUILayout.Label(EditorGUIUtility.IconContent("d_cs Script Icon"), GUILayout.Width(20), GUILayout.Height(20));

                string displayName = string.Empty;

                if (type.IsSubclassOf(typeof(FsmState)))
                {
                    displayName = type.Name;
                    _scriptGuiContent.tooltip = string.Empty;
                }
                else
                {
                    displayName = string.Format("{0}<color=yellow>(Script Missing)</color>", type.Name);
                    _scriptGuiContent.tooltip = "脚本丢失,请检查该脚本是否继承自FSMState!";
                }

                _scriptGuiContent.text = displayName;


                GUILayout.Label(_scriptGuiContent, _prefixLabel, GUILayout.Height(20));

                GUILayout.FlexibleSpace();

                GUILayout.BeginVertical();

                GUILayout.Space(5);

                if (GUILayout.Button(EditorGUIUtility.IconContent("d__Menu"), _headerStyle, GUILayout.Width(25), GUILayout.Height(25)))
                {
                    ShowMenu(script);
                }

                GUILayout.EndVertical();

                GUILayout.EndHorizontal();

                if (Event.current.type == EventType.MouseUp && Event.current.button == 1 && r.Contains(mousePosition))
                {
                    ShowMenu(script);
                    Event.current.Use();
                }

                if (Event.current.type == EventType.MouseUp && Event.current.button == 0 && r.Contains(mousePosition))
                {

                    EditorGUIUtility.PingObject(script);
                    Event.current.Use();
                }
            }


            GUILayout.Space(30);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            Rect rect = GUILayoutUtility.GetRect(_btnAddStateScript, GUI.skin.button, GUILayout.Width(260), GUILayout.Height(25));

            if (GUI.Button(rect, _btnAddStateScript))
            {
                PopupRect = new Rect(rect);
                PopupRect.height = 300;

                PopupWindow.Show(rect, new FsmSelectStateWindow(PopupRect, helper.controller, helper.node));
            }

            GUILayout.FlexibleSpace();



            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            //if (Event.current.type == EventType.Layout)
            //{

            //}

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (helper.controller != null)
            {
                helper.controller.Save();
            }

            //GUILayout.EndScrollView();

            EditorGUI.EndDisabledGroup();
        }

        /// <summary>绘制节点名称，改名成功后同步图索引；失效选择不继续绘制。</summary>
        protected override void OnHeaderGUI()
        {
            //base.OnHeaderGUI();
            FsmStateInspectorHelper helper = target as FsmStateInspectorHelper;
            if (helper == null || !helper.controller || helper.node == null || !helper.controller.states.Contains(helper.node))
            {
                return;
            }

            string name = null;
            EditorGUI.BeginChangeCheck();
            {
                EditorGUILayout.Space();
                EditorGUILayout.BeginHorizontal();

                GUILayout.Label(EditorGUIUtility.IconContent("icons/processed/unityeditor/animations/animatorstate icon.asset"), GUILayout.Width(30), GUILayout.Height(30));

                EditorGUILayout.LabelField("Name:", GUILayout.Width(60));

                bool disabled = EditorApplication.isPlaying
                    || helper.node.IsAnyState || helper.node.IsEntryState || helper.node.IsUpState;

                EditorGUI.BeginDisabledGroup(disabled);
                name = EditorGUILayout.DelayedTextField(helper.node.DisplayName);
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.EndHorizontal();
            }
            if (EditorGUI.EndChangeCheck())
            {
                string oldName = helper.node.name;
                bool success = FsmStateNodeFactory.Rename(helper.controller, helper.node, name);
                if (success)
                {
                    helper.grap.RenameState(oldName, name);
                }
            }
            EditorGUILayout.Space();
            var rect = EditorGUILayout.BeginHorizontal();

            Handles.color = Color.black;
            Handles.DrawLine(new Vector2(rect.x, rect.y), new Vector3(rect.x + rect.width, rect.y));

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
        }



        /// <summary>显示脚本编辑与解绑菜单；解绑记录配置 Undo 并标脏，不删除脚本文件。</summary>
        private void ShowMenu(MonoScript script)
        {
            FsmStateInspectorHelper helper = target as FsmStateInspectorHelper;
            if (helper == null)
            {
                return;
            }

            var genricMenu = new GenericMenu();

            genricMenu.AddItem(new GUIContent("Remove Script"), false, () =>
            {
                Undo.RecordObject(helper.controller, "Remove FSM State Script");
                helper.node.RemoveStateScript(script);
                helper.controller.Save();
            });

            genricMenu.AddItem(new GUIContent("Edit Script"), false, () =>
            {
                AssetDatabase.OpenAsset(script);
            });

            genricMenu.ShowAsContext();
        }



        //private void DrawItem(Rect rect, int index, bool isActive, bool isFocused)
        //{


        //    if (index < 0 || index >= reorderableList.list.Count) return;
        //    FSMStateScriptInfo info = reorderableList.list[index] as FSMStateScriptInfo;
        //    if (info == null) return;
        //    info.className = GUI.TextField(rect, info.className);
        //    if (string.IsNullOrEmpty(info.className))
        //    {
        //        EditorGUI.BeginDisabledGroup(true);
        //        GUI.Label(rect, "请输入类的全名(含命名空间)!");
        //        EditorGUI.EndDisabledGroup();
        //    }
        //}

        //private void OnDrawHeaderCallback(Rect rect)
        //{
        //    GUI.Label(rect, "State Scripts");
        //}

    }


}
