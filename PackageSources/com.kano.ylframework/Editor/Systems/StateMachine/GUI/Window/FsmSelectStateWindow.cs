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



    /// <summary>节点脚本选择弹窗，支持绑定已有状态脚本，或创建新文件并等待编译后绑定。</summary>
    public class FsmSelectStateWindow : PopupWindowContent
    {


        #region 字段

        private RuntimeFsmController _controller;

        // 搜索框
        private SearchField _searchField;
        private Rect _searchRect;
        const float SearchHeight = 25f;

        // 标签
        private Rect _labelRect;
        const float LabelHeight = 30f;

        // 参数列表
        private FsmStateListTree _stateTree;
        private TreeViewState _stateState;
        private Rect _stateRect;

        private Rect _rect;

        private FsmStateNodeData _nodeData;

        private bool _showCreateScriptGui = false;
        private string _scriptName = string.Empty;
        private string _scriptName2;
        #endregion


        /// <summary>绑定目标配置和节点，并以传入矩形尺寸建立脚本选择弹窗。</summary>
        /// <param name="rect">弹窗尺寸来源，位置不参与窗口大小计算。</param>
        /// <param name="controller">拥有目标节点的配置。</param>
        /// <param name="nodeData">接收脚本绑定的节点。</param>
        public FsmSelectStateWindow(Rect rect, RuntimeFsmController controller, FsmStateNodeData nodeData)
        {
            //this.width = width;
            this._controller = controller;
            this._rect = rect;
            this._nodeData = nodeData;
            this._showCreateScriptGui = false;
        }


        /// <summary>返回构造时指定的弹窗宽高。</summary>
        /// <returns>弹窗尺寸。</returns>
        public override Vector2 GetWindowSize()
        {
            return new Vector2(this._rect.width, this._rect.height);
        }

        /// <summary>根据当前模式绘制已有脚本搜索页或新脚本创建页。</summary>
        /// <param name="rect">弹窗内容区域。</param>
        public override void OnGUI(Rect rect)
        {
            if (!_showCreateScriptGui)
            {
                OnGuiSearchScripts(rect);
            }
            else
            {
                OnGuiCreateScripts(rect);
            }

        }

        /// <summary>建立可搜索的状态脚本树，并提供切换到新脚本创建页的入口。</summary>
        private void OnGuiSearchScripts(Rect rect)
        {
            if (_stateTree == null)
            {
                if (_stateState == null)
                {
                    _stateState = new TreeViewState();
                }

                _stateTree = new FsmStateListTree(_stateState, _controller, this._nodeData, this);
                _stateTree.Reload();
            }

            // 搜索框
            if (_searchField == null)
            {
                _searchField = new SearchField();
            }
            _searchRect.Set(rect.x + 5, rect.y + 5, rect.width - 5, SearchHeight);
            _stateTree.searchString = _searchField.OnGUI(_searchRect, _stateTree.searchString);

            // 标签
            _labelRect.Set(rect.x, rect.y + SearchHeight, rect.width, LabelHeight);
            EditorGUI.LabelField(_labelRect, "FSMStates", GUI.skin.GetStyle("AC BoldHeader"));

            // 参数列表

            _stateRect.Set(rect.x, rect.y + SearchHeight + LabelHeight - 5, rect.width, rect.height - SearchHeight - LabelHeight - 20);
            _stateTree.OnGUI(_stateRect);

            _stateRect.Set(rect.x, _stateRect.y + _stateRect.height, rect.width, 23);
            if (GUI.Button(_stateRect, "New Scripts", "AppToolbarButtonMid"))
            {
                _showCreateScriptGui = true;
                _scriptName = string.Empty;
            }
        }

        /// <summary>绘制脚本名称输入，将名称提交及回车产生的创建请求延后到编辑器回调执行。</summary>
        private void OnGuiCreateScripts(Rect rect)
        {

            EditorGUI.BeginDisabledGroup(true);

            // 搜索框
            if (_searchField == null)
            {
                _searchField = new SearchField();
            }
            _searchRect.Set(rect.x + 5, rect.y + 5, rect.width - 5, SearchHeight);
            _stateTree.searchString = _searchField.OnGUI(_searchRect, _stateTree.searchString);

            EditorGUI.EndDisabledGroup();

            // 标签
            _labelRect.Set(rect.x, rect.y + SearchHeight, rect.width, LabelHeight);

            if (GUI.Button(_labelRect, "New Script", "AC BoldHeader"))
            {
                _showCreateScriptGui = false;
            }
            _labelRect.y -= 3;
            GUI.Label(_labelRect, EditorGUIUtility.IconContent("ArrowNavigationLeft"));

            // 参数列表

            _stateRect.Set(rect.x, rect.y + SearchHeight + LabelHeight - 5, rect.width, rect.height - SearchHeight - LabelHeight - 20);

            GUILayout.BeginArea(_stateRect);
            GUILayout.Space(10);
            GUILayout.Label("Name");
            EditorGUI.BeginChangeCheck();

            _scriptName2 = EditorGUILayout.DelayedTextField(_scriptName);

            if (EditorGUI.EndChangeCheck())
            {
                _scriptName = _scriptName2;
                EditorApplication.delayCall += CreateAndAddScript;
            }
            //GUILayout.Label(string.Format("scriptName:{0} scriptName2:{1}", scriptName, scriptName2));
            GUILayout.EndArea();

            _stateRect.Set(rect.x, rect.height - 23, rect.width, 23);
            if (GUI.Button(_stateRect, "Create And Add", "AppToolbarButtonMid"))
            {
                OnButtonClick();
            }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
            {
                EditorApplication.delayCall += CreateAndAddScript;
            }

        }

        /// <summary>在搜索页请求重绘，弹窗不再聚焦时解除此更新订阅。</summary>
        private void Update()
        {

            if (!_showCreateScriptGui && editorWindow != null)
            {
                editorWindow.Repaint();
            }

            if (EditorWindow.focusedWindow != editorWindow)
            {
                EditorApplication.update -= Update;
            }
        }


        /// <summary>弹窗打开时订阅编辑器更新以刷新脚本搜索显示。</summary>
        public override void OnOpen()
        {
            base.OnOpen();
            EditorApplication.update += Update;
        }

        /// <summary>弹窗关闭时解除本窗口的编辑器更新订阅。</summary>
        public override void OnClose()
        {
            base.OnClose();
            EditorApplication.update -= Update;
        }

        /// <summary>解除更新订阅并关闭弹窗，随后终止当前 IMGUI 绘制流程。</summary>
        public void Close()
        {
            EditorApplication.update -= Update;
            if (editorWindow == null)
            {
                return;
            }

            editorWindow.Close();
            GUIUtility.ExitGUI();
        }


        /// <summary>仅在弹窗仍聚焦时于配置目录新建脚本，保存节点选择并登记编译后的延迟绑定。</summary>
        private void CreateAndAddScript()
        {

            if (EditorWindow.focusedWindow != editorWindow)
            {
                return;
            }

            if (string.IsNullOrEmpty(_scriptName2))
            {
                return;
            }

            FsmStateGraphView.SavePrefsSelection(this._nodeData.name);

            string dir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(_controller));
            string path = string.Format("{0}/{1}.cs", dir, _scriptName2);
            bool isSuccess = FsmStateCreator.CreateFsmState(path, false, _controller, _nodeData);

            if (!isSuccess)
            {
                GUIContent content = new GUIContent(string.Format("名称:{0}不可用,请修改后重试!", _scriptName));
                this.editorWindow.ShowNotification(content);
                return;
            }

            //Close();
        }


        /// <summary>移除文本输入焦点，使延迟文本字段提交名称并走统一创建入口。</summary>
        private void OnButtonClick()
        {
            GUI.FocusControl(null);
        }

    }
}
