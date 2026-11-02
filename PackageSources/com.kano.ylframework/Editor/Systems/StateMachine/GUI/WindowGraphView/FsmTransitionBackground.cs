using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>以 IMGUI 绘制过渡边、默认入口和运行时切换提示，同时提供边命中及检查选择。</summary>
    public class FsmTransitionBackground : ImmediateModeElement
    {
        private float EdgeDistanceMultiplier => Mathf.Clamp(_graphView.scale, 0.1f, 1);

        private float EdgeSizeMultiplier => Mathf.Clamp(_graphView.scale, 0.1f, 1);

        private float LineWidth => Mathf.Max(1f, 2f * EdgeSizeMultiplier);


        private FsmStateGraphView _graphView;

        private static Vector3 EdgeToSelfOffsetVector => new Vector3(0f, 30f, 0f);

        private static Color SelectedEdgeColor => new Color(0.42f, 0.7f, 1f, 1f);

        /// <summary>鼠标距离阈值内最近的过渡，用于悬停加粗及单击选择，不代表正在执行的过渡。</summary>
        public FsmTransitionData CloseTransition
        {
            get; internal set;
        }

        /// <summary>当前选中边的数据引用；要同步 Inspector 应通过选择方法修改。</summary>
        public FsmTransitionData Selection
        {
            get; set;
        }

        private FsmTransitionData _entryToDefault = new FsmTransitionData();

        private Vector3[] _vectors = new Vector3[2];

        private Vector3[] _points = new Vector3[2];

        private static Color DefaultTransitionColor => new Color(0.6f, 0.4f, 0f, 1f);

        private static FsmTransitionData _currentChangeTransition;
        private float _currentChangeTransitionTimer = 0;

        private float _lastRealTime;
        private float _deltaTime;

        #region 退出当前子状态
        private FsmStateNode _currentState;
        private bool _isStateExiting = false;
        private string _exitStateParent;
        private string _exitStateName;
        private string _exitStateToName;
        private float _exitStateTimer = 0;
        #endregion

        /// <summary>按当前浏览层重绘配置边及预览，合成 Entry 到默认节点的显示边，并更新最近命中过渡。</summary>
        protected override void ImmediateRepaint()
        {
            List<FsmTransitionData> transitions = Context.Instance.GetCurrentShowTransitionData();
            //if (transitions == null || transitions.Count == 0)
            //    return;

            _deltaTime = Time.realtimeSinceStartup - _lastRealTime;
            _lastRealTime = Time.realtimeSinceStartup;

            if (_deltaTime > 0.2)
            {
                _deltaTime = 0;
            }

            if (Event.current.type == EventType.Repaint)
            {
                VisualElement visualElement = base.parent;
                _graphView = visualElement as FsmStateGraphView;
                if (_graphView == null)
                {
                    throw new InvalidOperationException("FSMTransitionBackground can only be added to a GraphView");
                }

                if (transitions != null)
                {
                    foreach (var transition in transitions)
                    {
                        DrawTransiton(transition, Color.white);
                        DrawCurrentTransition(transition);
                    }
                }

                List<FsmStateNodeData> nodes = Context.Instance.GetCurrentShowStateNodeData();
                if (nodes != null)
                {
                    _entryToDefault.fromStateName = string.Empty;
                    _entryToDefault.toStateName = string.Empty;

                    foreach (var item in nodes)
                    {
                        if (item.IsEntryState)
                        {
                            _entryToDefault.fromStateName = item.name;
                        }

                        if (item.isDefaultState)
                        {
                            _entryToDefault.toStateName = item.name;
                        }
                    }

                    DrawTransiton(_entryToDefault, DefaultTransitionColor);
                }

                DrawMakeTransition();

                DrawStateExitTransition();
            }

            if (_graphView.HoverNode == null)
            {
                CloseTransition = FindClosestTransition();
            }
        }

        /// <summary>解析过渡端点，按选中与悬停状态绘制边；端点未显示时不绘制。</summary>
        private void DrawTransiton(FsmTransitionData data, Color color)
        {

            FsmNodeView fromNode = _graphView.GetNode(data.fromStateName);
            FsmNodeView toNode = _graphView.GetNode(data.toStateName);

            if (fromNode == null || toNode == null)
            {
                return;
            }

            if (data == Selection)
            {
                color = SelectedEdgeColor;
            }

            Vector3[] edgePoints = GetTransitionPoints(data);

            DrawTransiton(edgePoints, color, data == CloseTransition, true);
        }

        /// <summary>按缩放系数绘制给定线段及可选箭头，加粗仅改变显示尺寸。</summary>
        private void DrawTransiton(Vector3[] edgePoints, Color color, bool bold, bool isDrawArrow)
        {
            float arrowSize = 5f * EdgeSizeMultiplier;
            float outlineWidth = 2f * EdgeSizeMultiplier;
            float arrowLength = 13f * EdgeSizeMultiplier;
            Handles.color = color;
            float width = LineWidth;

            if (bold)
            {
                arrowSize *= 1.5f;
                width *= 1.5f;
            }

            Handles.DrawAAPolyLine(width, edgePoints[0], edgePoints[1]);
            if (isDrawArrow)
            {
                Vector3 cross = Vector3.Cross((edgePoints[0] - edgePoints[1]).normalized, Vector3.forward);
                DrawArrows(color, cross, edgePoints, isSelf: false, arrowSize, outlineWidth, arrowLength);
            }
        }

        /// <summary>使用过渡定义中的端点名称取得当前绘制线段。</summary>
        private Vector3[] GetTransitionPoints(FsmTransitionData data)
        {
            return GetTransitionPoints(data.fromStateName, data.toStateName);
        }

        /// <summary>将端点中心变换到图绘制坐标并沿垂线偏移，以区分反向边；返回复用数组。</summary>
        private Vector3[] GetTransitionPoints(string fromStateName, string toStateName)
        {
            FsmNodeView fromNode = _graphView.GetNode(fromStateName);
            FsmNodeView toNode = _graphView.GetNode(toStateName);

            if (fromNode == null || toNode == null)
            {
                return null;
            }

            float num = 5f * EdgeDistanceMultiplier;

            _points[0] = GetNodeViewPosition(fromNode);
            _points[1] = GetNodeViewPosition(toNode);

            Vector3 cross = Vector3.Cross((_points[0] - _points[1]).normalized, Vector3.forward);
            _points[0] += cross * num;
            _points[1] += cross * num;
            return _points;
        }

        /// <summary>将节点内容坐标中心转换为叠加层使用的图绘制坐标。</summary>
        private Vector3 GetNodeViewPosition(FsmNodeView node)
        {
            return _graphView.contentViewContainer.Matrix().MultiplyPoint(node.GetPosition().center);
        }

        /// <summary>在线段中部计算箭头中心及方向，并交由单箭头绘制入口输出。</summary>
        private static void DrawArrows(Color color, Vector3 cross, Vector3[] edgePoints, bool isSelf, float arrowSize, float outlineWidth, float arrowLength)
        {
            Vector3 vector = edgePoints[1] - edgePoints[0];
            Vector3 normalized = vector.normalized;
            Vector3 vector2 = vector * 0.5f + edgePoints[0];
            vector2 -= cross * 0.5f;
            float num = Mathf.Min(arrowLength * 1.5f, (vector2 - edgePoints[0]).magnitude) * 0.66f;
            int num2 = 1;

            for (int i = 0; i < num2; i++)
            {
                Color color2 = color;

                Vector3 center = vector2 + (float)((num2 == 1) ? i : (i - 1)) * num * (isSelf ? cross : normalized);
                DrawArrow(color2, cross, normalized, center, arrowSize, outlineWidth);
            }
        }

        /// <summary>仅在重绘事件中以填充三角形及轮廓线绘制单个方向箭头。</summary>
        private static void DrawArrow(Color color, Vector3 cross, Vector3 direction, Vector3 center, float arrowSize, float outlineWidth)
        {
            if (Event.current.type == EventType.Repaint)
            {
                Vector3[] array = new Vector3[4];
                array[0] = center + direction * arrowSize;
                array[1] = center - direction * arrowSize + cross * arrowSize;
                array[2] = center - direction * arrowSize - cross * arrowSize;
                array[3] = array[0];
                Handles.color = color;
                Handles.DrawAAPolyLine(outlineWidth, array);
                Handles.DrawAAConvexPolygon(array);

            }
        }

        /// <summary>在当前显示层中查找距鼠标最近且小于十像素的配置边，不包含合成的默认入口边。</summary>
        /// <returns>最近的过渡；没有命中时为 null。</returns>
        public FsmTransitionData FindClosestTransition()
        {
            FsmTransitionData result = null;
            float num = float.PositiveInfinity;
            Vector3 vector = Event.current.mousePosition;

#if UNITY_6000_2_OR_NEWER
            // 独立 GraphView 宿主没有 FsmEditorWindow 的侧栏和工具栏偏移。
            if (_graphView.Window != null)
            {
                vector.x -= _graphView.Window.position.width * _graphView.Window.PercentOfParam + 2;
                vector.y -= _graphView.Window.docked ? 44 : 46; // 20
            }
#endif

            List<FsmTransitionData> transitions = Context.Instance.GetCurrentShowTransitionData();
            if (transitions == null || transitions.Count == 0)
            {
                return null;
            }

            foreach (var transition in transitions)
            {
                Vector3[] edgePoints = GetTransitionPoints(transition);

                if (edgePoints == null)
                {
                    continue;
                }

                float num2 = float.PositiveInfinity;
                num2 = ((!(edgePoints[0] == edgePoints[1])) ? HandleUtility.DistancePointLine(vector, edgePoints[0], edgePoints[1]) : Vector3.Distance(EdgeToSelfOffsetVector + edgePoints[0], vector));
                if (num2 < num && num2 < 10f)
                {
                    num = num2;
                    result = transition;
                }
            }

            return result;
        }

        /// <summary>更新选中边并去重安排延迟 Inspector 绑定，清空选择时立即清除原检查目标。</summary>
        /// <param name="transition">要选中的过渡，null 表示取消选择。</param>
        public void OnTransitionSelection(FsmTransitionData transition)
        {
            EditorApplication.delayCall -= DelayCallSelection;
            if (this.Selection != null && transition == null)
            {
                FsmTransitionInspectorHelper.Instance.Inspect(Context.Instance.RuntimeFsmController, null);
            }

            this.Selection = transition;
            //Debug.Log("OnTransitionSelection");


            if (Selection != null)
            {
                EditorApplication.delayCall += DelayCallSelection;
            }

        }

        /// <summary>预览从来源节点到悬停普通节点或鼠标位置的连线，不创建配置过渡。</summary>
        private void DrawMakeTransition()
        {
            if (!_graphView.IsMakingTransition)
            {
                return;
            }

            _vectors[0] = GetNodeViewPosition(_graphView.MakeTransitionFrom);

            if (_graphView.HoverNode != null && !_graphView.HoverNode.Data.isBuiltInState)
            {
                _vectors[1] = GetNodeViewPosition(_graphView.HoverNode);
            }
            else
            {
                _vectors[1] = Event.current.mousePosition;
#if UNITY_6000_2_OR_NEWER
                if (_graphView.Window != null)
                {
                    _vectors[1].x -= _graphView.Window.position.width * _graphView.Window.PercentOfParam + 2;
                    _vectors[1].y -= _graphView.Window.docked ? 44 : 46; // 20
                }
#endif
            }

            DrawTransiton(_vectors, Color.white, false, true);
        }


        /// <summary>
        /// 为宿主最近记录的过渡绘制一次短暂高亮，不表示过渡仍在执行，也不改变运行时状态。
        /// </summary>
        private void DrawCurrentTransition(FsmTransitionData transition)
        {
            if (Context.Instance.FsmController == null)
            {
                return;
            }

            if (Context.Instance.FsmController.RuntimeFsmController == null)
            {
                return;
            }

            if (Context.Instance.RuntimeFsmController == null)
            {
                return;
            }

            float width = LineWidth;

            if (CloseTransition == transition)
            {
                width *= 1.5f;
            }

            Vector3[] edgePoints = GetTransitionPoints(transition);

            int index = Context.Instance.FsmController.RuntimeFsmController.IndexOf(Context.Instance.RuntimeFsmController);

            FsmTransition currentTransition = Context.Instance.FsmController.GetCurrentTransition(index);
            if (currentTransition == null)
            {
                //lastRealTime = Time.realtimeSinceStartup;
                return;
            }
            if (!transition.Key.Equals(currentTransition.Data.Key))
            {
                return;
            }

            if (_currentChangeTransition != currentTransition.Data)
            {
                _currentChangeTransition = currentTransition.Data;
                _currentChangeTransitionTimer = 0;
            }

            if (_currentChangeTransition.Key.Equals(currentTransition.Data.Key) && _currentChangeTransitionTimer >= 1)
            {
                //lastRealTime = Time.realtimeSinceStartup;
                return;
            }

            FsmStateNodeData from = Context.Instance.RuntimeFsmController.GetStateNodeData(_currentChangeTransition.fromStateName);

            if (from == null || from.Parent != Context.Instance.LayerParent)
            {
                _currentChangeTransitionTimer = 1;
                //lastRealTime = Time.realtimeSinceStartup;
                return;
            }


            _currentChangeTransitionTimer += _deltaTime * 3;

            if (edgePoints != null && edgePoints.Length >= 2)
            {
                Handles.color = SelectedEdgeColor;
                Handles.DrawAAPolyLine(width, edgePoints[0], Vector2.Lerp(edgePoints[0], edgePoints[1], _currentChangeTransitionTimer));
            }

        }

        /// <summary>检测浏览父层的当前节点由存在变为空，临时绘制到 Up 节点的退出提示并按时间清除。</summary>
        private void DrawStateExitTransition()
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            if (!Application.isPlaying)
            {
                return;
            }

            if (Context.Instance.FsmController == null)
            {
                return;
            }

            if (Context.Instance.FsmController.RuntimeFsmController == null)
            {
                return;
            }

            if (Context.Instance.RuntimeFsmController == null)
            {
                return;
            }

            float width = LineWidth;

            int index = Context.Instance.FsmController.RuntimeFsmController.IndexOf(Context.Instance.RuntimeFsmController);
            FsmStateNode stateNode = Context.Instance.FsmController.GetCurrentStateInfo(index, Context.Instance.LayerParent);

            if (_currentState != null && stateNode == null)
            {
                _isStateExiting = true;
                _exitStateParent = _currentState.Data.Parent;
                _exitStateName = _currentState.Data.name;
                _exitStateTimer = 0;

                List<FsmStateNodeData> nodes = Context.Instance.GetCurrentShowStateNodeData();

                if (nodes != null)
                {
                    foreach (var item in nodes)
                    {
                        if (item.isBuiltInState && item.builtInStateName.Equals(FsmConst.Up))
                        {
                            _exitStateToName = item.name;
                            break;
                        }
                    }
                }

            }

            if (_isStateExiting)
            {
                _exitStateTimer += Time.unscaledDeltaTime * 3;

                Handles.color = SelectedEdgeColor;
                Vector3[] edgePoints = GetTransitionPoints(_exitStateName, _exitStateToName);

                if (edgePoints != null)
                {
                    Handles.DrawAAPolyLine(width, edgePoints[0], Vector2.Lerp(edgePoints[0], edgePoints[1], _exitStateTimer));
                }

                if (_exitStateTimer >= 1 || !_exitStateParent.Equals(Context.Instance.LayerParent))
                {
                    _isStateExiting = false;
                    _exitStateParent = null;
                    _exitStateName = null;
                    _exitStateToName = null;
                    _exitStateTimer = 0;
                }
            }
            _currentState = stateNode;

        }

        /// <summary>将当前边选择交给检查代理，代理再次验证其是否仍属于当前配置。</summary>
        private void DelayCallSelection()
        {
            FsmTransitionInspectorHelper.Instance.Inspect(Context.Instance.RuntimeFsmController, Selection);
        }

    }

}
