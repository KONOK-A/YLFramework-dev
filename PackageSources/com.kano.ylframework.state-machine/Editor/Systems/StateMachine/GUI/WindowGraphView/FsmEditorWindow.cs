using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace YLFramework.Editor.StateMachine
{

    /// <summary>状态机图编辑窗口，组合参数侧栏、层级导航、GraphView 和运行时状态预览。</summary>
    public class FsmEditorWindow : UnityEditor.EditorWindow
    {

        #region 字段

        private static FsmEditorWindow _instance;

        private float _percentOfParam = 0.3f; // 参数所占用的比例

        private VisualElement _left = null;
        private VisualElement _middle = null;
        private VisualElement _right = null;

        private Rect _paramResizeArea;

        const float ResizeWidth = 5;

        private bool _isResizingParamArea = false; // 是否正在调整参数区域

        private Rect _stateRect;

        private FsmStateGraphView _graphGui = null;

        private ParamLayer _paramLayer = null;

        private IMGUIContainer _stateTopContainer;
        private IMGUIContainer _stateBottomContainer;

        private Vector3 _splitLineStart;
        private Vector3 _splitLineEnd;

        private string _parentLayer;

        private int _lastRepaintCount = 0;

        #endregion

        #region 属性

        /// <summary>参数侧栏占窗口宽度的比例，供布局及新版本连线坐标修正使用。</summary>
        public float PercentOfParam => _percentOfParam;

        #endregion

        /// <summary>设置窗口标题及图标，图标加载异常时保留纯文本标题。</summary>
        private void Awake()
        {
            try
            {
                this.titleContent = new GUIContent("YL.AssetFSM", EditorGUIUtility.IconContent("AnimatorController Icon").image);
            }
            catch (System.Exception)
            {
                this.titleContent = new GUIContent("YL.AssetFSM");
            }
        }

        /// <summary>窗口启用时去重注册 Undo/Redo 后的视图刷新。</summary>
        private void OnEnable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        /// <summary>撤销或重做后清理配置查询缓存并重建节点，避免图继续持有旧数据。</summary>
        private void OnUndoRedo()
        {
            var controller = Context.Instance.RuntimeFsmController;
            if (controller)
            {
                controller.ClearCache();
            }

            _graphGui?.RefreshNodes();
            Repaint();
        }


        /// <summary>重建左右布局、IMGUI 容器和图视图，并注册分栏拖动输入。</summary>
        private void InitGui()
        {
            rootVisualElement.Clear();

            ClearInspetor();

            rootVisualElement.style.flexDirection = FlexDirection.Row;

            _left = new VisualElement();

            IMGUIContainer paramContainer = new IMGUIContainer();
            paramContainer.onGUIHandler += () =>
            {
                if (_paramLayer == null)
                {
                    _paramLayer = new ParamLayer(this);
                }

                _paramLayer.OnGUI(paramContainer.layout);
                _paramLayer.ProcessEvents();
            };
            paramContainer.StretchToParentSize();
            _left.Add(paramContainer);

            _middle = new VisualElement();
            _right = new VisualElement();


            _stateTopContainer = new IMGUIContainer();
            _stateTopContainer.onGUIHandler += () =>
            {
                DoGraphToolbar(_stateTopContainer.layout);
            };
            _right.Add(_stateTopContainer);

            _graphGui = new FsmStateGraphView();
            _graphGui.Window = this;
            //graphGUI.StretchToParentSize();
            _right.Add(_graphGui);

            _stateBottomContainer = new IMGUIContainer();
            _stateBottomContainer.onGUIHandler += () =>
            {
                DrawButtom(_stateBottomContainer.layout);
            };
            _right.Add(_stateBottomContainer);

            rootVisualElement.Add(_left);
            rootVisualElement.Add(_middle);
            rootVisualElement.Add(_right);


            _middle.RegisterCallback<MouseDownEvent>((e) =>
            {
                if (e != null && e.button == 0)
                {
                    _isResizingParamArea = true;
                }
            });

            rootVisualElement.RegisterCallback<MouseUpEvent>(OnMouseUpEvent);

        }

        /// <summary>仅当活动选择是本模块的节点或过渡代理时清空 Inspector，不取消其他对象选择。</summary>
        private void ClearInspetor()
        {
            if (Selection.activeObject == null)
            {
                return;
            }

            if (Selection.activeObject.GetType() == typeof(FsmStateInspectorHelper) ||
                    Selection.activeObject.GetType() == typeof(FsmTransitionInspectorHelper))
            {
                Selection.activeObject = null;
            }

        }

        /// <summary>定期请求窗口重绘，并让图同步配置切换与视口状态。</summary>
        private void Update()
        {
            _lastRepaintCount++;
            if (_lastRepaintCount >= 10)
            {
                Repaint();
                _lastRepaintCount = 0;
            }

            if (_graphGui != null)
            {
                _graphGui.Update();
            }
        }

        /// <summary>更新分栏布局、绘制背景与配置诊断，首次绘制时建立界面。</summary>
        private void OnGUI()
        {
            wantsMouseEnterLeaveWindow = true;
            wantsMouseMove = true;

            _stateRect.Set(this.position.width * _percentOfParam + 2, 20, this.position.width * (1 - _percentOfParam), position.height - 15);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(_stateRect, EditorGUIUtility.isProSkin ? new Color(0.17f, 0.17f, 0.17f) : new Color(0.7f, 0.7f, 0.7f));
            }

            if (_left == null)
            {
                InitGui();
            }

            ParamAreaResize();

            _left.style.width = this.position.width * _percentOfParam - ResizeWidth / 2;
            _left.style.height = this.position.height;

            _middle.style.width = ResizeWidth;
            _middle.style.height = this.position.height;

            _right.style.width = this.position.width * (1 - _percentOfParam) - ResizeWidth / 2;
            _right.style.height = this.position.height;


            _stateTopContainer.style.width = _right.style.width;
            _stateTopContainer.style.height = 20;

            _graphGui.style.width = _right.style.width;
            _graphGui.style.height = this.position.height - 35;


            _stateBottomContainer.style.width = _right.style.width;
            _stateBottomContainer.style.height = 15;

            _splitLineStart.Set(this.position.width * _percentOfParam + 2, 0, 0);
            _splitLineEnd.Set(this.position.width * _percentOfParam + 2, this.position.height, 0);
            Handles.color = Color.black;
            Handles.DrawAAPolyLine(2, _splitLineStart, _splitLineEnd);
            var controller = Context.Instance.RuntimeFsmController;
            if (controller && !controller.ValidateConfiguration(out string error))
            {
                EditorGUI.HelpBox(new Rect(position.width * _percentOfParam + 10, 25, Mathf.Max(100, _stateRect.width - 20), 42), $"Asset '{controller.name}': {error}", MessageType.Error);
            }




            if (Event.current.type == EventType.MouseLeaveWindow && Event.current.button == 0)
            {
                _isResizingParamArea = false;
            }


            _instance = this;
        }


        /// <summary>绘制分栏调整指示，并在拖动时限制侧栏比例与最小宽度。</summary>
        private void ParamAreaResize()
        {
            _paramResizeArea.Set(this.position.width * _percentOfParam - ResizeWidth / 2, 0, ResizeWidth, position.height);

            EditorGUIUtility.AddCursorRect(_paramResizeArea, MouseCursor.ResizeHorizontal);

            if (_paramResizeArea.Contains(Event.current.mousePosition))
            {
                Vector3 start = _paramResizeArea.center - new Vector2(-1, _paramResizeArea.height / 2);
                Vector3 end = _paramResizeArea.center + new Vector2(1, _paramResizeArea.height / 2);
                Handles.color = _isResizingParamArea ? Color.green : Color.white;
                Handles.DrawLine(start, end);
            }

            if (_isResizingParamArea)
            {
                _percentOfParam = Mathf.Clamp(Event.current.mousePosition.x / position.width, 0.1f, 0.5f);
                if (this.position.width * _percentOfParam < 150)
                {
                    _percentOfParam = 150 / this.position.width;
                }
            }

        }

        /// <summary>绘制浏览层级面包屑；路径父层变化后重建图节点，不改变执行层级。</summary>
        private void DoGraphToolbar(Rect toolbarRect)
        {
            Handles.color = new Color(0, 0, 0, 0.4f);
            Handles.DrawLine(new Vector3(toolbarRect.x, 0), new Vector3(toolbarRect.x + toolbarRect.width, 0));

            GUILayout.BeginArea(toolbarRect);
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUI.BeginChangeCheck();
            BreadCrumb(0, Context.Instance.Layers.Count + 1, "Base Layer");
            if (EditorGUI.EndChangeCheck() && Context.Instance.Layers.Count > 0)
            {
                Context.Instance.RemoveLayer(0, Context.Instance.Layers.Count);
            }

            for (int i = 0; i < Context.Instance.Layers.Count; i++)
            {
                EditorGUI.BeginChangeCheck();
                BreadCrumb(i + 1, Context.Instance.Layers.Count + 1, Context.Instance.Layers[i]);
                if (EditorGUI.EndChangeCheck())
                {
                    Context.Instance.RemoveLayer(i + 1, Context.Instance.Layers.Count - 1 - i);
                }
            }

            if (_parentLayer != Context.Instance.LayerParent && _graphGui != null)
            {
                _graphGui.RefreshNodes();
                _parentLayer = Context.Instance.LayerParent;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>根据面包屑文字与图标计算布局宽度，测量后恢复原内容图标。</summary>
        private static Rect GetBreadcrumbLayoutRect(GUIContent content, GUIStyle style)
        {
            Texture image = content.image;
            content.image = null;
            Vector2 vector = style.CalcSize(content);
            content.image = image;
            if (image != null)
            {
                vector.x += vector.y;
            }

            return GUILayoutUtility.GetRect(content, style, GUILayout.MaxWidth(vector.x));
        }

        /// <summary>绘制一个层级导航项，并以切换控件向外层传递点击变化。</summary>
        private void BreadCrumb(int index, int maxCount, string name)
        {
            GUIStyle style = StateStyles.Named(index == 0 ? "GUIEditor.BreadcrumbLeft" : "GUIEditor.BreadcrumbMid", EditorStyles.miniButton);
            GUIStyle guiStyle = StateStyles.Named(index == 0 ? "GUIEditor.BreadcrumbLeftBackground" : "GUIEditor.BreadcrumbMidBackground", EditorStyles.helpBox);
            GUIContent content = new GUIContent(name);
            Rect breadcrumbLayoutRect = GetBreadcrumbLayoutRect(content, style);
            if (Event.current.type == EventType.Repaint)
            {
                guiStyle.Draw(breadcrumbLayoutRect, GUIContent.none, 0);
            }

            GUI.Toggle(breadcrumbLayoutRect, index == maxCount - 1, content, style);
        }


        /// <summary>绘制宿主及配置资产的定位按钮，点击仅在编辑器中高亮对应对象。</summary>
        private void DrawButtom(Rect rect)
        {
            rect.Set(0, 0, rect.width, rect.height);
            GUILayout.BeginArea(rect);
            GUILayout.BeginHorizontal();
            if (Context.Instance.FsmController != null)
            {
                if (GUILayout.Button(Context.Instance.FsmController.name, "ShurikenLabel"))
                {
                    EditorGUIUtility.PingObject(Context.Instance.FsmController.gameObject);
                }
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button(AssetDatabase.GetAssetPath(Context.Instance.RuntimeFsmController), "ShurikenLabel"))
            {
                EditorGUIUtility.PingObject(Context.Instance.RuntimeFsmController);
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }


        /// <summary>左键释放时结束分栏宽度调整。</summary>
        private void OnMouseUpEvent(MouseUpEvent e)
        {
            if (e != null && e.button == 0)
            {
                _isResizingParamArea = false;
            }

        }


        /// <summary>解除 Undo 回调，清除本模块的活动检查代理并撤销窗口通知引用。</summary>
        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            ClearInspetor();
            _instance = null;
        }

        /// <summary>在最近一次绘制的图窗口显示短通知，没有窗口引用时不处理。</summary>
        /// <param name="message">要显示的提示文本。</param>
        public static void ShowNotification(string message)
        {
            if (_instance == null)
            {
                return;
            }

            _instance.ShowNotification(new GUIContent(message));
        }

    }
}
