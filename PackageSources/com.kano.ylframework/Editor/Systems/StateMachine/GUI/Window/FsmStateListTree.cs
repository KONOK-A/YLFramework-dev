using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.StateMachine;

#if UNITY_6000_2_OR_NEWER
using TreeView = UnityEditor.IMGUI.Controls.TreeView<int>;
using TreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
using TreeViewItem = UnityEditor.IMGUI.Controls.TreeViewItem<int>;
#endif

namespace YLFramework.Editor.StateMachine
{
    /// <summary>脚本选择树的一行，持有已导入脚本资产及按需解析的类型。</summary>
    public class FsmStateTreeViewItem : TreeViewItem
    {

        /// <summary>此行代表的脚本资产。</summary>
        public MonoScript MonoScript
        {
            get; private set;
        }


        private Type _type = null;

        /// <summary>由脚本解析并缓存的类型；脚本尚未编译为类型时可为空。</summary>
        public Type Type
        {
            get
            {

                if (_type == null)
                {
                    _type = MonoScript.GetClass();
                }

                return _type;
            }
        }

        /// <summary>将树行身份及显示名称与脚本资产绑定。</summary>
        /// <param name="id">树内唯一行标识。</param>
        /// <param name="depth">行的层级深度。</param>
        /// <param name="displayName">脚本类型的显示名称。</param>
        /// <param name="script">此行代表的脚本资产。</param>
        public FsmStateTreeViewItem(int id, int depth, string displayName, MonoScript script) : base(id, depth, displayName)
        {
            MonoScript = script;
        }




    }


    /// <summary>列出可构造的普通状态脚本，单击后按 GUID 绑定到目标节点。</summary>
    public class FsmStateListTree : TreeView
    {

        #region 字段

        private RuntimeFsmController _controller;
        //private FSMConditionData condition;
        private GUIStyle _style = new GUIStyle("label");

        private FsmStateNodeData _nodeData = null;

        private FsmSelectStateWindow _editorWindow = null;

        private List<int> _selections = new List<int>();

        #endregion




        #region 重写方法
        /// <summary>从已导入的合法状态脚本建立平铺树根，类型解析失败的脚本不显示。</summary>
        /// <returns>包含候选脚本行的隐藏根节点。</returns>
        protected override TreeViewItem BuildRoot()
        {



            TreeViewItem root = new TreeViewItem(-1, -1);

            List<MonoScript> scripts = StateMachineEditorTools.GetAllStatesType();

            for (int i = 0; i < scripts.Count; i++)
            {

                Type type = scripts[i].GetClass();

                if (type == null)
                {
                    continue;
                }

                string displayName = type.Name;
                FsmStateTreeViewItem item = new FsmStateTreeViewItem(i, 0, displayName, scripts[i]);
                root.AddChild(item);
            }

            return root;
        }

        /// <summary>使用基础 TreeView 的行构建与搜索过滤行为。</summary>
        /// <param name="root">已建立的脚本树根。</param>
        /// <returns>当前可显示的行列表。</returns>
        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
        {
            return base.BuildRows(root);
        }

        /// <summary>记录配置 Undo 后尝试添加脚本绑定；失败显示通知，成功标脏并关闭弹窗。</summary>
        /// <param name="id">单击的脚本行标识。</param>
        protected override void SingleClickedItem(int id)
        {
            base.SingleClickedItem(id);

            FsmStateTreeViewItem item = FindItem(id, rootItem) as FsmStateTreeViewItem;

            if (item != null)
            {
                try
                {
                    Undo.RecordObject(_controller, "Add FSM State Script");
                    _nodeData.AddStateScript(item.MonoScript);
                }
                catch (Exception e)
                {
                    _editorWindow.editorWindow.ShowNotification(new GUIContent(e.Message));
                    return;
                }
            }
            _controller.Save();
            _editorWindow.Close();
        }

        /// <summary>绘制脚本图标、类型名和命名空间，并将鼠标悬停行设为当前选择。</summary>
        /// <param name="args">当前行的绘制参数。</param>
        protected override void RowGUI(RowGUIArgs args)
        {
            //base.RowGUI(args);

            FsmStateTreeViewItem item = args.item as FsmStateTreeViewItem;
            if (item == null)
            {
                return;
            }

            Type type = item.Type;
            if (type == null)
            {
                return;
            }

            if (args.rowRect.Contains(Event.current.mousePosition))
            {
                _selections.Clear();
                _selections.Add(item.id);
                this.SetSelection(_selections);
            }


            Rect rect = new Rect();
            rect.Set(args.rowRect.x + 20, args.rowRect.y, args.rowRect.width - 20, args.rowRect.height);

            GUI.Label(new Rect(0, args.rowRect.y - 2, 20, 20), EditorGUIUtility.IconContent("d_cs Script Icon"));

            if (string.IsNullOrEmpty(type.Namespace))
            {
                GUI.Label(rect, type.Name);
            }
            else
            {
                _style.richText = true;
                GUI.Label(rect, string.Format("{0}<color=#A4A4A4>({1})</color>", type.Name, type.Namespace), _style);
            }

            //EditorGUIUtility.IconContent("d_cs Script Icon");
        }

        //protected override void ContextClicked()
        //{
        //    base.ContextClicked();
        //}

        /// <summary>沿用基础树的选择变化处理，绑定操作仅在单击入口执行。</summary>
        /// <param name="selectedIds">当前选中的行标识。</param>
        protected override void SelectionChanged(IList<int> selectedIds)
        {
            base.SelectionChanged(selectedIds);
        }

        /// <summary>禁止多选，使一次点击只绑定一个状态脚本。</summary>
        /// <param name="item">待判断的树行。</param>
        /// <returns>始终为 false。</returns>
        protected override bool CanMultiSelect(TreeViewItem item)
        {
            return false;
        }

        #endregion

        #region 方法
        /// <summary>建立脚本选择树并绑定目标节点与弹窗。</summary>
        /// <param name="state">树的展开、搜索及选择状态。</param>
        /// <param name="controller">拥有目标节点的配置。</param>
        /// <param name="nodeData">接收脚本绑定的节点。</param>
        /// <param name="editorWindow">用于显示失败通知及关闭的弹窗。</param>
        public FsmStateListTree(TreeViewState state, RuntimeFsmController controller, FsmStateNodeData nodeData, FsmSelectStateWindow editorWindow) : base(state)
        {
            this._controller = controller;

            showBorder = true;
            showAlternatingRowBackgrounds = true;
            this._nodeData = nodeData;
            this._editorWindow = editorWindow;
        }


        #endregion
    }
}
