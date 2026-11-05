using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{

    /// <summary>编辑过渡的自动切换与 OR 条件组，将可重排列表绑定到所属配置的序列化属性。</summary>
    [CustomEditor(typeof(FsmTransitionInspectorHelper))]
    public class FsmTransitionInspector : UnityEditor.Editor
    {

        private ReorderableList _reorderableList;

        private Rect _conditionLeftRect;
        private Rect _conditionRightRect;
        private Rect _popRect;

        private GUIContent _addConditionGroupLabel = null;

        private GUIContent _autoSwitchLabel = null;

        private static Dictionary<ParameterType, FsmConditionInspector> _conditionInspector = new Dictionary<ParameterType, FsmConditionInspector>();

        private Dictionary<GroupCondition, ReorderableList> _reorderables = new Dictionary<GroupCondition, ReorderableList>();
        private SerializedObject _controllerObject;
        private FsmTransitionData _boundTransition;

        private bool _autoSwitch;

        /// <summary>去重注册 Undo 刷新回调，并为当前选择建立序列化列表绑定。</summary>
        private void OnEnable()
        {
            Undo.undoRedoPerformed -= RebindAfterUndo;
            Undo.undoRedoPerformed += RebindAfterUndo;
            BindLists();
        }

        /// <summary>移除 Undo 回调并释放当前持有的 SerializedObject。</summary>
        private void OnDisable()
        {
            Undo.undoRedoPerformed -= RebindAfterUndo;
            _controllerObject?.Dispose();
            _controllerObject = null;
        }

        /// <summary>按端点名称重新取得反序列化后的过渡对象，再重建列表，避免沿用旧对象和属性句柄。</summary>
        private void RebindAfterUndo()
        {
            var helper = target as FsmTransitionInspectorHelper;
            if (helper && helper.controller && helper.transition != null)
            {
                helper.controller.ClearCache();
                helper.transition = helper.controller.GetTransitionData(helper.transition.fromStateName, helper.transition.toStateName);
            }
            BindLists();
            Repaint();
        }

        /// <summary>按过渡在所属配置中的当前下标取得序列化属性，过渡已移除时返回 null。</summary>
        private SerializedProperty TransitionProperty(FsmTransitionInspectorHelper helper)
        {
            int index = helper.controller.transitions.IndexOf(helper.transition);
            return index < 0 ? null : _controllerObject.FindProperty("transitions").GetArrayElementAtIndex(index);
        }

        /// <summary>以所属配置作为序列化及 Undo 目标，重建基础条件列表并清空附加组列表缓存。</summary>
        private void BindLists()
        {
            FsmTransitionInspectorHelper helper = target as FsmTransitionInspectorHelper;
            _reorderableList = null;
            _reorderables.Clear();
            _boundTransition = helper ? helper.transition : null;
            if (helper == null || !helper.controller || helper.transition == null)
            {
                return;
            }

            if (_controllerObject == null || _controllerObject.targetObject != helper.controller)
            {
                _controllerObject?.Dispose();
                _controllerObject = new SerializedObject(helper.controller);
            }
            _controllerObject.Update();
            var transitionProperty = TransitionProperty(helper);
            if (transitionProperty == null)
            {
                return;
            }

            _reorderableList = new ReorderableList(_controllerObject, transitionProperty.FindPropertyRelative("conditions"), true, true, true, true);
            _reorderableList.drawHeaderCallback += OnDrawHeaderCallback;
            _reorderableList.onAddCallback += this.OnAddCallback;
            _reorderableList.onRemoveCallback += this.OnRemoveCallback;
            _reorderableList.drawElementCallback += this.DrawItem;
            _reorderableList.onReorderCallback += ApplyReorder;

            _autoSwitch = helper.transition.autoSwitch;
        }

        /// <summary>提交序列化重排并标脏，再重取过渡和列表绑定以匹配新的对象身份。</summary>
        private void ApplyReorder(ReorderableList list)
        {
            _controllerObject.ApplyModifiedProperties();
            var helper = (FsmTransitionInspectorHelper)target;
            helper.controller.Save();
            RebindAfterUndo();
        }

        /// <summary>刷新当前属性路径并绘制自动切换和各条件组，运行期禁用编辑，过渡已删除时仅显示提示。</summary>
        public override void OnInspectorGUI()
        {
            FsmTransitionInspectorHelper helper = target as FsmTransitionInspectorHelper;
            if (helper == null || !helper.controller || helper.transition == null)
            {
                BindLists();
                return;
            }
            if (_controllerObject == null || _controllerObject.targetObject != helper.controller || _boundTransition != helper.transition)
            {
                BindLists();
            }

            _controllerObject.Update();
            var transitionProperty = TransitionProperty(helper);
            if (transitionProperty == null)
            {
                _reorderableList = null;
                _reorderables.Clear();
                EditorGUILayout.HelpBox("This transition has been removed.", MessageType.Info);
                return;
            }
            if (_reorderableList == null)
            {
                BindLists();
            }
            // 数组删除/重排后重新取得句柄，保留列表本身的选中项和拖拽状态。
            _reorderableList.serializedProperty = transitionProperty.FindPropertyRelative("conditions");

            EditorGUI.BeginDisabledGroup(EditorApplication.isPlaying);

            if (_autoSwitchLabel == null)
            {
                _autoSwitchLabel = new GUIContent("AutoSwitch", "当条件为空时,是否自动切换?当前过渡的条件为空时可用!");
            }

            EditorGUI.BeginDisabledGroup(!helper.transition.IsEmpty);
            Rect rect = GUILayoutUtility.GetRect(0f, 20, GUILayout.ExpandWidth(expand: true));
            GUI.Label(rect, _autoSwitchLabel);
            rect.Set(rect.width - 20, rect.y, 20, 20);
            if (helper.transition.IsEmpty)
            {
                FsmTransitionFactory.SetAutoSwitch(helper.controller, helper.transition, GUI.Toggle(rect, helper.transition.autoSwitch, string.Empty));
            }
            else
            {
                GUI.Toggle(rect, false, string.Empty);
            }

            if (_autoSwitch != helper.transition.autoSwitch)
            {
                helper.controller.Save();
                _autoSwitch = helper.transition.autoSwitch;
            }
            GUILayout.Space(10);
            EditorGUI.EndDisabledGroup();

            _reorderableList.DoLayoutList();

            GUILayout.Space(10);

            for (int i = 0; i < helper.transition.groupConditions.Count; i++)
            {
                GroupCondition condition = helper.transition.groupConditions[i];
                if (condition == null)
                {
                    continue;
                }

                ReorderableList list = GetReorderableList(condition);
                if (list != null)
                {
                    list.DoLayoutList();
                    GUILayout.Space(10);
                }
            }




            if (_addConditionGroupLabel == null)
            {
                _addConditionGroupLabel = new GUIContent("Add a set of conditions", "添加一组条件");
            }

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (helper.transition.groupConditions.Count > 0)
            {
                GUILayout.Label("注:当有多组条件时,其中一组满足,状态就会切换!", "CN StatusWarn");
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(_addConditionGroupLabel, GUILayout.Width(260), GUILayout.Height(25)))
            {
                FsmConditionFactory.AddGroup(helper.controller, helper.transition);
                _controllerObject.Update();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUI.EndDisabledGroup();
            _controllerObject.ApplyModifiedProperties();

        }

        /// <summary>显示过渡来源和目标名称，不修改端点。</summary>
        protected override void OnHeaderGUI()
        {
            //base.OnHeaderGUI();
            FsmTransitionInspectorHelper helper = target as FsmTransitionInspectorHelper;
            if (helper == null || helper.transition == null)
            {
                return;
            }

            GUILayout.BeginHorizontal();

            GUILayout.Label(EditorGUIUtility.IconContent("icons/processed/unityeditor/animations/animatorstatetransition icon.asset"), GUILayout.Width(30), GUILayout.Height(30));
            GUILayout.Label(string.Format("{0} -> {1}", helper.transition.fromStateName, helper.transition.toStateName));
            GUILayout.EndHorizontal();

            var rect = EditorGUILayout.BeginHorizontal();

            Handles.color = Color.black;
            Handles.DrawLine(new Vector2(rect.x, rect.y), new Vector3(rect.x + rect.width, rect.y));

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();

        }


        /// <summary>按参数类型延迟建立共享绘制策略，Trigger 使用不绘制额外值控件的基类策略。</summary>
        private static void InitConditionInspectors()
        {

            if (_conditionInspector.Count == 0)
            {
                _conditionInspector.Add(ParameterType.Bool, new FsmBoolConditionInspector());
                _conditionInspector.Add(ParameterType.Float, new FsmFloatConditionInspector());
                _conditionInspector.Add(ParameterType.Int, new FsmIntConditionInspector());
                _conditionInspector.Add(ParameterType.Trigger, new FsmConditionInspector()); // 不需要做任何绘制
            }

        }

        /// <summary>向基础组追加默认条件，并重新取得可能因数组变化而失效的属性句柄。</summary>
        private void OnAddCallback(ReorderableList list)
        {

            FsmTransitionInspectorHelper helper = target as FsmTransitionInspectorHelper;
            if (helper == null)
            {
                return;
            }
            //Debug.Log("添加条件");
            FsmConditionFactory.CreateCondition(helper.controller, helper.transition);
            _controllerObject.Update();
            list.serializedProperty = TransitionProperty(helper).FindPropertyRelative("conditions");
        }

        /// <summary>移除基础组选中条件，再刷新所属序列化对象和列表属性。</summary>
        private void OnRemoveCallback(ReorderableList list)
        {

            FsmTransitionInspectorHelper helper = target as FsmTransitionInspectorHelper;
            if (helper == null)
            {
                return;
            }

            FsmConditionFactory.DeleteCondition(helper.controller, helper.transition, list.index);
            _controllerObject.Update();
            list.serializedProperty = TransitionProperty(helper).FindPropertyRelative("conditions");
        }

        /// <summary>将基础组指定行委托给通用条件绘制流程。</summary>
        private void DrawItem(Rect rect, int index, bool isActive, bool isFocused)
        {

            FsmTransitionInspectorHelper helper = target as FsmTransitionInspectorHelper;
            if (helper == null)
            {
                return;
            }

            var conditon = helper.transition.conditions[index];

            DrawItemExcute(rect, conditon);
        }

        /// <summary>绘制参数选择和对应类型的比较输入；引用缺失时显示诊断而不删除条件。</summary>
        private void DrawItemExcute(Rect rect, FsmConditionData condition)
        {
            FsmTransitionInspectorHelper helper = target as FsmTransitionInspectorHelper;
            if (helper == null)
            {
                return;
            }

            _conditionLeftRect.Set(rect.x, rect.y, rect.width / 2, rect.height);
            _conditionRightRect.Set(rect.x + rect.width / 2, rect.y, rect.width / 2, rect.height);

            if (helper.controller.parameters.Count > 0)
            {
                //tempContent.text = conditon.parameterName;
                if (EditorGUI.DropdownButton(_conditionLeftRect, new GUIContent(condition.parameterName), FocusType.Keyboard))
                {
                    PopupWindow.Show(rect, new FsmSelectParamWindow(rect.width / 2, condition, helper.controller));


                }
            }

            InitConditionInspectors();

            FsmParameterData parameter = helper.controller.GetParameterData(condition.parameterName);

            if (parameter == null)
            {
                EditorGUI.LabelField(_conditionRightRect, "缺少参数!");
            }
            else
            {

                if (_conditionInspector.ContainsKey(parameter.parameterType))
                {
                    if (_conditionInspector[parameter.parameterType] != null)
                    {
                        _conditionInspector[parameter.parameterType].OnGUI(_conditionRightRect, condition, helper.controller);
                    }
                    else
                    {
                        Debug.LogErrorFormat("未查询到对应的绘制方式:{0}", parameter.parameterType);
                    }
                }
            }

        }

        /// <summary>绘制基础条件组的列表标题。</summary>
        private void OnDrawHeaderCallback(Rect rect)
        {
            GUI.Label(rect, "Conditions");
        }

        /// <summary>取得或建立附加组的序列化列表，并在每次读取时刷新数组路径。</summary>
        /// <param name="condition">当前过渡中的附加条件组。</param>
        /// <returns>可重排列表；组、所属配置或过渡已失效时为 null。</returns>
        public ReorderableList GetReorderableList(GroupCondition condition)
        {
            if (condition == null)
            {
                return null;
            }

            FsmTransitionInspectorHelper helper = target as FsmTransitionInspectorHelper;
            if (helper == null || !helper.controller || helper.transition == null)
            {
                return null;
            }

            int groupIndex = helper.transition.groupConditions.IndexOf(condition);
            var transitionProperty = TransitionProperty(helper);
            if (groupIndex < 0 || transitionProperty == null)
            {
                return null;
            }

            var property = transitionProperty.FindPropertyRelative(nameof(FsmTransitionData.groupConditions)).GetArrayElementAtIndex(groupIndex).FindPropertyRelative("conditions");
            if (!_reorderables.ContainsKey(condition))
            {
                ReorderableList list = new ReorderableList(_controllerObject, property, true, true, true, true);
                list.drawHeaderCallback += (rect) =>
                {

                    GUI.Label(rect, "Conditions");

                    rect.Set(rect.width - 10, rect.y + 1, 25, 25);

                    if (GUI.Button(rect, EditorGUIUtility.IconContent("d__Menu"), "IconButton"))
                    {
                        ShowConditionMenu(condition);
                    }


                };
                list.onAddCallback += (a) =>
                {
                    FsmConditionFactory.AddGroupCondition(helper.controller, condition);
                    _controllerObject.Update();
                    GetReorderableList(condition);
                };

                list.onRemoveCallback += (a) =>
                {
                    FsmConditionFactory.RemoveGroupCondition(helper.controller, condition, a.index);
                    _controllerObject.Update();
                    GetReorderableList(condition);
                };

                list.drawElementCallback += (rect, index, c, d) =>
                {
                    DrawItemExcute(rect, condition.conditions[index]);
                };
                list.onReorderCallback += ApplyReorder;
                _reorderables.Add(condition, list);
            }

            _reorderables[condition].serializedProperty = property;
            return _reorderables[condition];
        }


        /// <summary>显示附加组删除菜单，删除后清空列表缓存并刷新序列化数据。</summary>
        private void ShowConditionMenu(GroupCondition condition)
        {
            FsmTransitionInspectorHelper helper = target as FsmTransitionInspectorHelper;
            if (helper == null)
            {
                return;
            }

            var genricMenu = new GenericMenu();

            genricMenu.AddItem(new GUIContent("Remove"), false, () =>
            {
                FsmConditionFactory.RemoveGroup(helper.controller, helper.transition, condition);
                _reorderables.Clear();
                _controllerObject.Update();
            });

            genricMenu.ShowAsContext();
        }

    }

}
