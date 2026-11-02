using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{

    /// <summary>单个节点的图视图，负责网格定位、选择代理、默认状态操作及运行标记，不承载状态脚本。</summary>
    public class FsmNodeView : Node
    {

        /// <summary>此视图直接编辑的配置节点数据。</summary>
        public FsmStateNodeData Data
        {
            get; private set;
        }


        private FsmStateGraphView _graphView = null;

        private GUIStyle _labelStyle = null;

        private float _runingTimer;


        /// <summary>绑定节点与所属图，建立自绘内容，并在脱离面板时取消尚未执行的选择回调。</summary>
        /// <param name="data">要显示的节点数据。</param>
        /// <param name="graphView">拥有此节点的图视图。</param>
        public FsmNodeView(FsmStateNodeData data, FsmStateGraphView graphView)
        {
            this.Data = data;
            this._graphView = graphView;
            Clear();
            focusable = true;

            VisualElement image = new VisualElement();
            image.pickingMode = PickingMode.Ignore;
            image.style.width = 200;
            image.style.height = 40;
            IMGUIContainer container = new IMGUIContainer();
            container.pickingMode = PickingMode.Ignore;
            container.focusable = false;
            container.onGUIHandler += () =>
            {

                if (Context.Instance.RuntimeFsmController == null)
                {
                    return;
                }

                if (_labelStyle == null)
                {
                    _labelStyle = new GUIStyle(GUI.skin.label);
                    _labelStyle.alignment = TextAnchor.MiddleCenter;
                    _labelStyle.fontSize = 15;
                    _labelStyle.padding.top = -5;
                }

                GUI.Label(container.layout, string.Empty, GetStateStyle(selected));
                GUI.Label(container.layout, Data.DisplayName, _labelStyle);

                DrawRuning(container.layout);
            };

            container.StretchToParentSize();

            //image.name = "node-border";
            image.style.borderRightWidth = 0;
            image.style.borderLeftWidth = 0;
            image.style.borderTopWidth = 0;
            image.style.borderBottomWidth = 0;

            image.Add(container);
            Add(image);

            RegisterCallback<MouseDownEvent>(OnMouseDownEvent);
            RegisterCallback<DetachFromPanelEvent>(evt => EditorApplication.delayCall -= DelayCallSelection);
        }

        /// <summary>将位置截断到二十像素网格并固定节点尺寸，数据变化时记录配置 Undo 并标脏。</summary>
        /// <param name="newPos">请求的新位置和尺寸，尺寸会替换为默认值。</param>
        public override void SetPosition(Rect newPos)
        {

            newPos.Set((int)(newPos.x / 20) * 20, (int)(newPos.y / 20) * 20, FsmConst.StateNodeWith, FsmConst.StateNodeHeight);

            // 视图有两像素边缘偏移，配置记录的仍是未偏移的网格位置。
            base.SetPosition(new Rect(newPos.x - 2, newPos.y - 2, newPos.width, newPos.height));

            if (Data.rect == newPos)
            {
                return;
            }

            if (Context.Instance.RuntimeFsmController)
            {
                Undo.RecordObject(Context.Instance.RuntimeFsmController, "Move FSM State");
            }

            Data.rect = newPos;
            if (Context.Instance.RuntimeFsmController != null)
            {
                Context.Instance.RuntimeFsmController.Save();
            }
        }


        /// <summary>按内置节点身份提供连线、同层默认状态和删除操作；结构删除统一走图的选中项入口。</summary>
        /// <param name="evt">待填充的节点菜单事件。</param>
        public void ShowMenu(ContextualMenuPopulateEvent evt)
        {

            bool isAny = Data.IsAnyState;
            bool isEntry = Data.IsEntryState;

            if (!isEntry && !Data.IsUpState)
            {
                evt.menu.AppendAction("Make Transition", (a) =>
                {
                    _graphView.StartMakeTransition(this);
                });
            }
            else
            {
                evt.menu.AppendAction("Make Transition", null, DropdownMenuAction.Status.Disabled);
            }

            if (!isEntry && !isAny && !Data.IsUpState)
            {

                if (Data.isDefaultState)
                {
                    evt.menu.AppendAction("Set as Layer Default State", null, DropdownMenuAction.Status.Disabled);
                }
                else
                {
                    evt.menu.AppendAction("Set as Layer Default State", (a) =>
                    {
                        //SetDefaultState(node);
                        SetDefaultState();
                        _graphView.RefreshNodes();
                    });
                }

                evt.menu.AppendAction("Delete", (a) =>
                {
                    DeleteStates();
                });
            }


        }


        /// <summary>删除当前图选择，而非仅删除菜单所在节点，以复用统一 Undo 组合与关联边清理。</summary>
        private void DeleteStates()
        {

            _graphView.DeleteSelectedElements();
        }

        /// <summary>记录配置 Undo，取消当前显示层的旧默认并将自身设为默认，最后标脏。</summary>
        private void SetDefaultState()
        {
            Undo.RecordObject(Context.Instance.RuntimeFsmController, "Set FSM Default State");
            List<FsmStateNodeData> states = Context.Instance.GetCurrentShowStateNodeData();
            if (states != null)
            {
                foreach (var item in states)
                {
                    item.isDefaultState = false;
                }
            }
            this.Data.isDefaultState = true;
            Context.Instance.RuntimeFsmController.Save();
        }


        /// <summary>按默认、内置及容器身份选择节点配色和形状，并区分选中态。</summary>
        private GUIStyle GetStateStyle(bool selected)
        {
            if (Data.isDefaultState)
            {

                if (Data.isSubStateMachine)
                {
                    return StateStyles.Get(selected ? Style.OrangeOnHEX : Style.OrangeHEX);
                }

                return StateStyles.Get(selected ? Style.OrangeOn : Style.Orange);
            }
            else if (Data.IsEntryState)
            {
                if (Data.isSubStateMachine)
                {
                    return StateStyles.Get(selected ? Style.GreenOnHEX : Style.GreenHEX);
                }

                return StateStyles.Get(selected ? Style.GreenOn : Style.Green);
            }
            else if (Data.IsAnyState)
            {
                if (Data.isSubStateMachine)
                {
                    return StateStyles.Get(selected ? Style.MintOnHEX : Style.MintHEX);
                }

                return StateStyles.Get(selected ? Style.MintOn : Style.Mint);
            }
            else if (Data.IsUpState)
            {
                return StateStyles.Get(selected ? Style.RedOnHEX : Style.RedHEX);
            }

            else
            {
                if (Data.isSubStateMachine)
                {
                    return StateStyles.Get(selected ? Style.NormalOnHEX : Style.NormalHEX);
                }

                return StateStyles.Get(selected ? Style.NormalOn : Style.Normal);
            }

        }

        /// <summary>选中后去重安排延迟 Inspector 绑定，避免当前图选择事件尚未完成时切换活动对象。</summary>
        public override void OnSelected()
        {
            base.OnSelected();
            EditorApplication.delayCall -= DelayCallSelection;
            EditorApplication.delayCall += DelayCallSelection;
        }

        /// <summary>仅在节点仍挂接面板且属于当前配置时更新检查代理，跳过删除或切图后的旧回调。</summary>
        private void DelayCallSelection()
        {
            if (panel == null || !Context.Instance.RuntimeFsmController || !Context.Instance.RuntimeFsmController.states.Contains(Data))
            {
                return;
            }

            FsmStateInspectorHelper.Instance.Inspect(Context.Instance.RuntimeFsmController, Data, _graphView);
        }

        /// <summary>取消待执行的延迟选择，并清除节点检查代理中的目标。</summary>
        public override void OnUnselected()
        {
            EditorApplication.delayCall -= DelayCallSelection;
            base.OnUnselected();
            FsmStateInspectorHelper.Instance.Inspect(Context.Instance.RuntimeFsmController, null, _graphView);
        }


        /// <summary>当前运行节点与此节点同名时绘制循环运行条，仅用于显示而非状态进度。</summary>
        private void DrawRuning(Rect rect)
        {

            FsmStateNode currentState = GetCurrentState();

            if (currentState == null)
            {
                return;
            }

            if (Event.current.type != EventType.Repaint || (!(currentState.Data.name == Data.name)))
            {
                return;
            }

            rect.Set(15, 28, rect.width - 30, 20);

            _runingTimer = Time.realtimeSinceStartup % 1;

            GUIStyle guiStyle = "MeLivePlayBackground";
            GUIStyle guiStyle2 = "MeLivePlayBar";
            rect = guiStyle.margin.Remove(rect);
            Rect rect2 = guiStyle.padding.Remove(rect);

            rect2.width *= _runingTimer;

            guiStyle2.Draw(rect2, isHover: false, isActive: false, on: false, hasKeyboardFocus: false);
            guiStyle.Draw(rect, isHover: false, isActive: false, on: false, hasKeyboardFocus: false);
        }

        /// <summary>运行期按当前配置和浏览父层查询宿主当前节点，未绑定宿主时返回 null。</summary>
        private FsmStateNode GetCurrentState()
        {
            if (!Application.isPlaying)
            {
                return null;
            }

            if (Context.Instance.FsmController == null)
            {
                return null;
            }

            if (Context.Instance.FsmController.RuntimeFsmController == null)
            {
                return null;
            }

            if (Context.Instance.RuntimeFsmController == null)
            {
                return null;
            }

            int index = Context.Instance.FsmController.RuntimeFsmController.IndexOf(Context.Instance.RuntimeFsmController);
            FsmStateNode currentStateNode = Context.Instance.FsmController.GetCurrentStateInfo(index, Context.Instance.LayerParent);

            return currentStateNode;

        }


        /// <summary>单击完成连线目标及检查选择，双击容器进入其浏览层，双击 Up 节点返回上层。</summary>
        /// <param name="evt">节点接收到的鼠标按下事件。</param>
        public void OnMouseDownEvent(MouseDownEvent evt)
        {
            if (evt != null && evt.pressedButtons == 1 && evt.clickCount == 1)
            {

                if (_graphView.IsMakingTransition)
                {
                    _graphView.StopMakeTransition(this);
                }

                //graphView.selector.DragStateNodeStart();
                DelayCallSelection();
            }

            if (evt != null && evt.pressedButtons == 1 && evt.clickCount == 2)
            {
                if (Data.isSubStateMachine)
                {
                    Context.Instance.AddLayer(Data.name);
                    _graphView.RefreshNodes();
                }

                if (Data.IsUpState)
                {
                    Context.Instance.RemoveLast(Data.Parent);
                    _graphView.RefreshNodes();
                }

            }

        }

    }

}
