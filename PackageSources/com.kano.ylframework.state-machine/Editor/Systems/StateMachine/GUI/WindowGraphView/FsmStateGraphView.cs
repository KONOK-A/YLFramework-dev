using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>当前配置父层的节点图视图，协调选择、拖动、连线预览及工厂作者操作，不执行状态逻辑。</summary>
    public class FsmStateGraphView : GraphView
    {

        /// <summary>当前显示层按节点名称建立的视图索引，不包含其他浏览层的节点。</summary>
        public Dictionary<string, FsmNodeView> StateNodes = new Dictionary<string, FsmNodeView>();

        internal FsmTransitionBackground Transition = null;


        /// <summary>是否正在从来源节点预览新连线，尚未写入过渡数据。</summary>
        public bool IsMakingTransition = false;

        /// <summary>当前预览连线的来源节点。</summary>
        public FsmNodeView MakeTransitionFrom = null;

        /// <summary>最近一次鼠标移动命中的节点，用于连线预览和边命中选择。</summary>
        public FsmNodeView HoverNode = null;

        private RuntimeFsmController _controller = null;


        /// <summary>拥有此视图的窗口，供布局相关坐标修正使用。</summary>
        public FsmEditorWindow Window
        {
            get; set;
        }

        //private FSMNodeView selectNode = null;

        /// <summary>建立网格及过渡背景，注册删除和鼠标输入，并安装平移、拖动、框选和缩放操纵器。</summary>
        public FsmStateGraphView()
        {
            styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(global::YLFramework.Editor.Common.FrameworkPackagePaths.AssetPath(
                typeof(FsmStateGraphView).Assembly, "Editor/Systems/StateMachine/Styles/FSMGraph.uss")));
            var background = new GridBackground();
            background.AddToClassList("yl-fsm-grid");
            background.AddToClassList(EditorGUIUtility.isProSkin ? "dark" : "light");
            background.StretchToParentSize();
            Insert(0, background);

            Transition = new FsmTransitionBackground();
            Transition.pickingMode = PickingMode.Ignore;
            Transition.StretchToParentSize();
            Insert(1, Transition);

            deleteSelection = (operationName, askUser) => DeleteSelectedElements();
            RegisterCallback<KeyDownEvent>(KeyDownControl, TrickleDown.TrickleDown);
            RegisterCallback<MouseDownEvent>(MouseDownControl);
            RegisterCallback<MouseMoveEvent>(UpdateHoverState);

            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            ContentZoomer zoomer = new ContentZoomer();
            zoomer.minScale = 0.1f;
            zoomer.maxScale = 3;

            this.AddManipulator(zoomer);

            RefreshView();
        }

        /// <summary>切换配置时恢复其视口平移及缩放，随后重建当前父层节点。</summary>
        private void RefreshView()
        {
            if (Context.Instance.RuntimeFsmController != null)
            {
                //Debug.LogFormat("刷新视图:position:{0} scale:{1}", Context.Instance.RuntimeFSMController.viewPosition, Context.Instance.RuntimeFSMController.viewScale);

#if UNITY_6000_2_OR_NEWER
                // 默认位置和缩放
                var pos = Context.Instance.RuntimeFsmController.viewPosition;
                contentViewContainer.style.translate = new Translate(pos.x, pos.y, pos.z);
                contentViewContainer.style.scale = new Scale((Vector2)Context.Instance.RuntimeFsmController.viewScale);
#else
                // 默认位置和缩放
                viewTransform.position = Context.Instance.RuntimeFsmController.viewPosition;
                viewTransform.scale = Context.Instance.RuntimeFsmController.viewScale;
#endif
            }
            _controller = Context.Instance.RuntimeFsmController;
            RefreshNodes();
        }

        /// <summary>将变化的视口缩放和平移写入当前配置并标脏，不记录 Undo 或立即落盘。</summary>
        private void SaveScaleAndPosition()
        {

            if (viewTransform == null || Context.Instance.RuntimeFsmController == null)
            {
                return;
            }

#if UNITY_6000_2_OR_NEWER
            Vector3 scale = contentViewContainer.resolvedStyle.scale.value;
#else
            Vector3 scale = viewTransform.scale;
#endif

            if (Context.Instance.RuntimeFsmController.viewScale != scale)
            {
                Context.Instance.RuntimeFsmController.viewScale = scale;
                Context.Instance.RuntimeFsmController.Save();
            }

#if UNITY_6000_2_OR_NEWER
            Vector3 position = contentViewContainer.resolvedStyle.translate;
#else
            Vector3 position = viewTransform.position;
#endif


            if (Context.Instance.RuntimeFsmController.viewPosition != position)
            {
                Context.Instance.RuntimeFsmController.viewPosition = position;
                Context.Instance.RuntimeFsmController.Save();
            }

        }

        /// <summary>由宿主窗口转发编辑器更新，响应当前配置变化并保存视口状态。</summary>
        public void Update()
        {
            if (_controller != Context.Instance.RuntimeFsmController)
            {
                RefreshView();
                _controller = Context.Instance.RuntimeFsmController;
            }

            SaveScaleAndPosition();
        }

        /// <summary>移除旧视图后按当前浏览父层重建节点，并消费偏好设置中的一次性选择恢复标记。</summary>
        public void RefreshNodes()
        {

            foreach (var item in StateNodes.Values)
            {
                RemoveElement(item);
            }

            StateNodes.Clear();

            List<FsmStateNodeData> datas = Context.Instance.GetCurrentShowStateNodeData();

            if (datas == null || datas.Count == 0)
            {
                return;
            }

            //selectNode = null;

            foreach (var item in datas)
            {
                if (item == null || string.IsNullOrEmpty(item.name) || StateNodes.ContainsKey(item.name))
                {
                    continue;
                }

                FsmNodeView node = new FsmNodeView(item, this);
                node.title = item.DisplayName;
                node.SetPosition(item.rect);
                this.AddElement(node);
                StateNodes.Add(item.name, node);

                if (GetPrefsSelection(item.name))
                {
                    AddToSelection(node);
                }
            }


        }

        /// <summary>仅更新图索引的名称键；节点数据与关联引用须先由节点工厂完成改名。</summary>
        /// <param name="oldName">图中既有名称。</param>
        /// <param name="newName">图中尚未占用的新名称。</param>
        public void RenameState(string oldName, string newName)
        {
            //Debug.LogFormat("重命名:old:{0} new:{1} cn:{2} co:{3}",oldName,newName, StateNodes.ContainsKey(newName), StateNodes.ContainsKey(oldName));
            if (StateNodes.ContainsKey(newName))
            {
                return;
            }

            if (!StateNodes.ContainsKey(oldName))
            {
                return;
            }

            StateNodes.Add(newName, StateNodes[oldName]);
            StateNodes.Remove(oldName);
        }


        /// <summary>按名称查询当前显示层中的节点视图。</summary>
        /// <param name="name">节点名称。</param>
        /// <returns>对应视图；名称为空或未显示时为 null。</returns>
        public FsmNodeView GetNode(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            if (StateNodes.ContainsKey(name))
            {
                return StateNodes[name];
            }

            return null;
        }


        /// <summary>处理非文本编辑场景的 Delete 键，删除图选择后阻止事件重复传播。</summary>
        private void KeyDownControl(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Delete || evt.target is TextElement ||
                (evt.target as VisualElement)?.GetFirstAncestorOfType<TextField>() != null || EditorGUIUtility.editingTextField)
            {
                return;
            }

            DeleteSelectedElements();
            evt.StopImmediatePropagation();
        }

        /// <summary>非运行期删除选中普通节点和有效边，以一个 Undo 组合并工厂操作，再重建图。</summary>
        internal void DeleteSelectedElements()
        {
            var controller = Context.Instance.RuntimeFsmController;
            if (!controller || EditorApplication.isPlaying)
            {
                return;
            }

            var states = StateNodes.Values.Where(node => node.selected && !node.Data.IsAnyState && !node.Data.IsEntryState && !node.Data.IsUpState)
                .Select(node => node.Data).Where(controller.states.Contains).ToList();
            var edge = Transition.Selection;
            bool deleteEdge = edge != null && controller.transitions.Contains(edge);
            if (states.Count == 0 && !deleteEdge)
            {
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Delete FSM Selection");
            ClearSelection();
            Transition.OnTransitionSelection(null);
            if (IsMakingTransition)
            {
                StopMakeTransition(null);
            }

            if (deleteEdge)
            {
                FsmTransitionFactory.DeleteTransition(controller, edge);
            }

            foreach (var state in states)
            {
                if (controller.states.Contains(state))
                {
                    FsmStateNodeFactory.DeleteState(controller, state);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            RefreshNodes();
        }

        /// <summary>左键单击时选中最近悬停边，并尝试结束未提交的连线预览。</summary>
        private void MouseDownControl(MouseDownEvent evt)
        {
            //BehaviourTreeView.TreeWindow.WindowRoot.InspectorView.UpdateInspector();
            if (evt.button == 0 && evt.clickCount == 1)
            {
                Transition.OnTransitionSelection(Transition.CloseTransition);
                StopMakeTransition(null);
            }
        }


        /// <summary>替换默认图菜单，空白处提供新建入口，节点处委托节点菜单；新建项在运行期禁用。</summary>
        /// <param name="evt">上下文菜单构建事件。</param>
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {

            FsmNodeView node = evt.target as FsmNodeView;

            if (node != null)
            {
                Transition.OnTransitionSelection(null);
            }

            int count = evt.menu.MenuItems().Count;
            for (int i = 0; i < count; i++)
            {
                evt.menu.RemoveItemAt(0);
            }

            RuntimeFsmController controller = Context.Instance.RuntimeFsmController;

            bool disable = controller == null || EditorApplication.isPlaying;

            DropdownMenuAction.Status status = disable ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal;

            if (node == null)
            {

                //Debug.LogFormat("鼠标位置:{0}",Event.current.mousePosition);

                evt.menu.AppendAction("Create State", CreateState, (d) => status, evt.mousePosition);
                evt.menu.AppendAction("Create Sub-State Machine", CreateSubStateMachine, (d) => status, evt.mousePosition);
            }
            else
            {
                node.ShowMenu(evt);
            }

            //base.BuildContextualMenu

        }


        /// <summary>将菜单携带的鼠标位置转换到内容坐标，创建普通状态节点。</summary>
        /// <param name="action">userData 为鼠标位置的菜单动作。</param>
        public void CreateState(DropdownMenuAction action)
        {
            Vector2 mousePosition = (Vector2)(action.userData);
            Vector2 position = contentViewContainer.WorldToLocal(mousePosition);
            CreateState(false, position);
        }

        /// <summary>将菜单鼠标位置转换到内容坐标，创建子状态机容器及内部导航节点。</summary>
        /// <param name="action">userData 为鼠标位置的菜单动作。</param>
        public void CreateSubStateMachine(DropdownMenuAction action)
        {
            Vector2 mousePosition = (Vector2)(action.userData);
            Vector2 position = contentViewContainer.WorldToLocal(mousePosition);
            CreateState(true, position);
        }


        /// <summary>复制当前浏览祖先链创建节点，按当前层已有节点判断默认状态，并为新容器补齐内置节点。</summary>
        private void CreateState(bool isSubStateMachine, Vector3 mousePosition)
        {

            Rect rect = new Rect(0, 0, FsmConst.StateNodeWith, FsmConst.StateNodeHeight);

            rect.center = mousePosition;

            List<FsmStateNodeData> states = Context.Instance.GetCurrentShowStateNodeData();

            bool isDefaultState = states == null || states.Count == 2;

            if (states != null)
            {
                bool isAllBuildIn = true;
                foreach (var item in states)
                {
                    if (!item.isBuiltInState)
                    {
                        isAllBuildIn = false;
                        break;
                    }
                }
                if (isAllBuildIn)
                {
                    isDefaultState = true;
                }
            }


            RuntimeFsmController controller = Context.Instance.RuntimeFsmController;

            List<string> parents = new List<string>();
            parents.AddRange(Context.Instance.Layers);

            FsmStateNodeData state = FsmStateNodeFactory.CreateStateNode(controller, rect, isDefaultState, isSubStateMachine, parents);

            if (isSubStateMachine)
            {
                Rect r = new Rect(0, 300, FsmConst.StateNodeWith, FsmConst.StateNodeHeight);
                CreateSubState(controller, FsmConst.EntryState, state, parents, r, FsmConst.EntryState);
                r = new Rect(0, 100, FsmConst.StateNodeWith, FsmConst.StateNodeHeight);
                CreateSubState(controller, FsmConst.AnyState, state, parents, r, FsmConst.AnyState);
                r = new Rect(600, 300, FsmConst.StateNodeWith, FsmConst.StateNodeHeight);
                CreateSubState(controller, FsmConst.Up, state, parents, r, FsmConst.Up);
            }


            RefreshNodes();
        }

        /// <summary>复制并追加新容器的祖先链，以完整路径命名并创建一个内部内置节点。</summary>
        private void CreateSubState(RuntimeFsmController controller, string name, FsmStateNodeData state, List<string> parents, Rect rect, string buildInName)
        {
            List<string> parentsTemp = new List<string>();
            parentsTemp.AddRange(parents);
            parentsTemp.Add(state.name);

            StringBuilder stringBuilder = new StringBuilder();
            for (int i = 0; i < parentsTemp.Count; i++)
            {
                stringBuilder.Append(parentsTemp[i]);
                stringBuilder.Append("/");
            }
            stringBuilder.Append(name);
            FsmStateNodeFactory.CreateStateNode(controller, stringBuilder.ToString(), rect, false, false, parentsTemp, true, buildInName);
        }


        /// <summary>登记连线来源并进入预览模式，不立即创建过渡数据。</summary>
        /// <param name="node">连线来源节点。</param>
        public void StartMakeTransition(FsmNodeView node)
        {
            IsMakingTransition = true;
            MakeTransitionFrom = node;
        }

        /// <summary>非来源节点点击时结束预览，有效端点交由工厂创建过渡；再次点击来源则保持预览。</summary>
        /// <param name="node">目标节点；null 表示取消，正常预览中不创建过渡。</param>
        public void StopMakeTransition(FsmNodeView node)
        {
            if (node == MakeTransitionFrom)
            {
                return;
            }

            IsMakingTransition = false;
            HoverNode = null;
            var from = MakeTransitionFrom;
            MakeTransitionFrom = null;
            if (node == null || from == null)
            {
                return;
            }

            RuntimeFsmController controller = Context.Instance.RuntimeFsmController;
            if (controller == null)
            {
                return;
            }

            FsmTransitionFactory.CreateTransition(controller, from.Data.name, node.Data.name);
        }

        /// <summary>在内容坐标中查找鼠标命中节点，命中后清除边悬停以避免节点下方的边抢选。</summary>
        private void UpdateHoverState(MouseMoveEvent evt)
        {
            Vector2 point = contentViewContainer.WorldToLocal(evt.mousePosition);
            HoverNode = null;
            foreach (var node in StateNodes.Values)
            {
                if (!node.GetPosition().Contains(point))
                {
                    continue;
                }

                HoverNode = node;
                Transition.CloseTransition = null;
                break;
            }
        }


        /// <summary>读取并消费配置及节点对应的一次性选择标记。</summary>
        /// <param name="name">节点名称。</param>
        /// <returns>存在待恢复选择标记时为 true。</returns>
        internal static bool GetPrefsSelection(string name)
        {
            string key = PrefsSelectionKey(name);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            bool v = EditorPrefs.GetBool(key, false);
            if (v)
            {
                EditorPrefs.DeleteKey(key);
            }

            return v;
        }

        /// <summary>记录下一次图重建需要恢复的节点选择，供脚本编译重载后继续检查。</summary>
        /// <param name="name">需要恢复选择的节点名称。</param>
        internal static void SavePrefsSelection(string name)
        {
            string key = PrefsSelectionKey(name);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            EditorPrefs.SetBool(key, true);
        }

        /// <summary>以当前配置资产 GUID 和节点名称构造选择偏好键。</summary>
        /// <param name="name">节点名称。</param>
        /// <returns>对应偏好键；没有当前配置时为空字符串。</returns>
        internal static string PrefsSelectionKey(string name)
        {
            RuntimeFsmController controller = Context.Instance.RuntimeFsmController;
            if (controller == null)
            {
                return string.Empty;
            }

            return string.Format("YL.AssetFSM:{0}:{1}", AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(controller)), name);
        }

    }
}
