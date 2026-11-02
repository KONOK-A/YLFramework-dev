using System.Collections.Generic;
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
    /// <summary>将配置参数显示为可搜索树，单击后重绑定条件参数及初始比较方式。</summary>
    public class FsmParamListTree : TreeView
    {

        #region 字段

        private RuntimeFsmController _controller;
        private FsmConditionData _condition;

        #endregion




        #region 重写方法
        /// <summary>按配置参数顺序建立平铺候选行，不创建或修改参数。</summary>
        /// <returns>包含参数名称行的隐藏根节点。</returns>
        protected override TreeViewItem BuildRoot()
        {

            TreeViewItem root = new TreeViewItem(-1, -1);

            if (_controller != null)
            {
                for (int i = 0; i < _controller.parameters.Count; i++)
                {
                    root.AddChild(new TreeViewItem(i, 0, _controller.parameters[i].name));
                }
            }

            return root;
        }

        /// <summary>沿用基础树的可见行构建及搜索过滤行为。</summary>
        /// <param name="root">参数树的根节点。</param>
        /// <returns>当前可显示的参数行。</returns>
        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
        {
            return base.BuildRows(root);
        }

        /// <summary>以配置 Undo 提交参数选择，并按类型重置比较方式；Trigger 同时将目标值设为一。</summary>
        /// <param name="id">单击的参数行标识。</param>
        protected override void SingleClickedItem(int id)
        {
            base.SingleClickedItem(id);

            string paramterName = FindItem(id, rootItem).displayName;

            FsmParameterData p = _controller.GetParameterData(paramterName);

            if (p != null)
            {
                UnityEditor.Undo.RecordObject(_controller, "Change FSM Condition Parameter");
                _condition.parameterName = paramterName;

                switch (p.parameterType)
                {
                    case ParameterType.Float:
                    case ParameterType.Int:
                        _condition.compareType = CompareType.Greater;
                        break;
                    case ParameterType.Bool:
                        _condition.compareType = CompareType.Equal;
                        break;
                    case ParameterType.Trigger:
                        _condition.compareType = CompareType.Equal;
                        _condition.targetValue = 1;
                        break;
                }
            }
            else
            {
                Debug.LogErrorFormat("参数查询失败:{0}", paramterName);
            }

            _controller.Save();
        }

        /// <summary>绘制参数名称，并标记条件当前引用的参数行。</summary>
        /// <param name="args">当前行的绘制参数。</param>
        protected override void RowGUI(RowGUIArgs args)
        {
            base.RowGUI(args);

            if (args.label.Equals(_condition.parameterName))
            {
                GUI.Label(args.rowRect, "√");
            }

        }

        #endregion

        #region 方法
        /// <summary>绑定参数来源与目标条件，并启用树边框及交替行背景。</summary>
        /// <param name="state">树的显示与选择状态。</param>
        /// <param name="controller">参数来源及 Undo 目标配置。</param>
        /// <param name="condition">需要重绑定参数的条件。</param>
        public FsmParamListTree(TreeViewState state, RuntimeFsmController controller, FsmConditionData condition) : base(state)
        {
            this._controller = controller;
            this._condition = condition;

            showBorder = true;
            showAlternatingRowBackgrounds = true;
        }


        #endregion


    }

}
