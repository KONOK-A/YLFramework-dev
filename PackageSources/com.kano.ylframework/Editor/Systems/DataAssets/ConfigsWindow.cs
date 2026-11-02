using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Editor.Common;

namespace YLFramework.Editor.DataAssets
{
    /// <summary>配置根的双栏作者窗口，左侧维护条目顺序，右侧显示条目检查器及根诊断。</summary>
    public sealed class ConfigsWindow : SplitWindow
    {
        [SerializeField] private ScriptableObjectConfigs current;
        [SerializeField] private TreeViewState treeState = new TreeViewState();
        private ScriptableObjectListTree _tree;
        private UnityEditor.Editor _dataEditor;
        private ScriptableObjectData _selected;
        private Vector2 _rightScroll;
        private string _search;
        /// <summary>按工程路径隔离当前根的本机偏好键，不写入配置资产。</summary>
        private static string PreferenceKey => "YLFramework.DataAssets.SelectedRoot." + Hash128.Compute(Application.dataPath);

        /// <summary>新建独立作者窗口，由窗口恢复本工程记住的根引用。</summary>
        [MenuItem("Window/YLFramework/Data Assets")]
        public static void Open() => CreateWindow<ConfigsWindow>().Show();

        /// <summary>打开现有或新建的窗口，并将其编辑目标切换到指定根。</summary>
        public static void Open(ScriptableObjectConfigs root)
        {
            var window = GetWindow<ConfigsWindow>();
            window.SetCurrentConfigs(root);
            window.Show();
        }

        /// <summary>恢复根偏好并订阅 Undo 与工程变化，用于同步窗口展示而非自动保存。</summary>
        private void OnEnable()
        {
            titleContent = new GUIContent("Data Assets");
            minSize = new Vector2(640, 300);
            if (!current)
            {
                current = AssetDatabase.LoadAssetAtPath<ScriptableObjectConfigs>(AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(PreferenceKey, string.Empty)));
            }

            Undo.undoRedoPerformed += Refresh;
            EditorApplication.projectChanged += Refresh;
        }

        /// <summary>解除窗口订阅，记住根 GUID 并释放树和条目检查器。</summary>
        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Refresh;
            EditorApplication.projectChanged -= Refresh;
            if (current)
            {
                EditorPrefs.SetString(PreferenceKey, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(current)));
            }

            ReleaseEditors();
        }

        /// <summary>释放旧目标的编辑器状态后切换根，并重置树选择状态；不登记运行时配置。</summary>
        public void SetCurrentConfigs(ScriptableObjectConfigs root)
        {
            ReleaseEditors();
            current = root;
            treeState = new TreeViewState();
            Repaint();
        }

        /// <summary>释放本窗口的树订阅并清除当前条目检查器，不销毁配置资产。</summary>
        private void ReleaseEditors()
        {
            _tree?.Dispose();
            _tree = null;
            SetSelected(null);
        }

        /// <summary>切换条目检查器并结束旧文本编辑焦点，仅销毁窗口自行创建的 Editor 实例。</summary>
        private void SetSelected(ScriptableObjectData data)
        {
            if (_selected == data && _dataEditor)
            {
                return;
            }

            GUIUtility.keyboardControl = 0;
            EditorGUIUtility.editingTextField = false;
            if (_dataEditor)
            {
                DestroyImmediate(_dataEditor);
            }

            _dataEditor = null;
            _selected = data;
            if (_selected)
            {
                _dataEditor = UnityEditor.Editor.CreateEditor(_selected);
            }
        }

        /// <summary>使根缓存失效，按对象引用恢复树选择，并清除已离开列表的条目展示。</summary>
        private void Refresh()
        {
            if (current)
            {
                current.ClearCache();
            }

            _tree?.RefreshKeepingSelection();
            if (_selected && (!current || current.configs == null || !current.configs.Contains(_selected)))
            {
                SetSelected(null);
            }

            Repaint();
        }

        /// <summary>处理未选根和缺失列表的入口，再为有效目标延迟建立树并绘制双栏。</summary>
        protected override void OnGUI()
        {
            if (!current)
            {
                EditorGUILayout.HelpBox("Select a YL configuration root or create one. The Excel adapter is optional.", MessageType.Info);
                var root = (ScriptableObjectConfigs)EditorGUILayout.ObjectField("Root", null, typeof(ScriptableObjectConfigs), false);
                if (root)
                {
                    SetCurrentConfigs(root);
                }

                if (GUILayout.Button("Create Root"))
                {
                    CreateRoot();
                }

                return;
            }
            if (current.configs == null)
            {
                EditorGUILayout.HelpBox("The entry list is missing. Repair it explicitly before opening the tree.", MessageType.Error);
                if (GUILayout.Button("Initialize Empty List"))
                {
                    Undo.RegisterCompleteObjectUndo(current, "Repair DataAssets list");
                    current.configs = new System.Collections.Generic.List<ScriptableObjectData>();
                    EditorUtility.SetDirty(current);
                }
                return;
            }
            if (_tree == null)
            {
                _tree = new ScriptableObjectListTree(current, treeState);
                _tree.Selected = SetSelected;
                _tree.Changed = Refresh;
            }
            base.OnGUI();
        }

        /// <summary>绘制根切换、创建、显式保存及搜索入口，并显示条目树。</summary>
        protected override void DrawGuiLeft(Rect rect)
        {
            GUILayout.BeginArea(rect);
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(string.IsNullOrEmpty(current.displayName) ? current.name : current.displayName, EditorStyles.toolbarDropDown))
            {
                var menu = new GenericMenu();
                foreach (var root in ScriptableObjectConfigsController.GetAllConfigs())
                {
                    var target = root;
                    menu.AddItem(new GUIContent(root.name), root == current, () => SetCurrentConfigs(target));
                }
                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("Create Root"), false, CreateRoot);
                menu.ShowAsContext();
            }
            if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(24)))
            {
                _tree.ShowCreateMenu();
            }

            if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(42)))
            {
                ScriptableObjectConfigsController.Save(current);
            }

            GUILayout.EndHorizontal();
            _search = EditorGUILayout.TextField(_search ?? string.Empty, EditorStyles.toolbarSearchField);
            _tree.searchString = _search;
            GUILayout.EndArea();
            _tree.OnGUI(new Rect(rect.x, rect.y + 43, rect.width, Mathf.Max(0, rect.height - 43)));
        }

        /// <summary>显示选中条目的检查器；未选条目时显示根诊断及需要确认的根删除入口。</summary>
        protected override void DrawGuiRight(Rect rect)
        {
            GUILayout.BeginArea(rect);
            _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll);
            if (_selected && _dataEditor)
            {
                _dataEditor.OnInspectorGUI();
            }
            else
            {
                EditorGUILayout.LabelField(current.name, EditorStyles.boldLabel);
                ScriptableObjectConfigsInspector.DrawDiagnostics(current);
                if (GUILayout.Button("Select Root"))
                {
                    Selection.activeObject = current;
                }

                if (GUILayout.Button("Delete Root..."))
                {
                    if (EditorUtility.DisplayDialog("Delete DataAssets Root", "Delete this root and its owned standalone entries? This operation cannot be undone. External assets and unknown files are preserved.", "Delete", "Cancel"))
                    {
                        if (ScriptableObjectConfigsController.DeleteRoot(current, true))
                        {
                            SetCurrentConfigs(null);
                        }

                        GUIUtility.ExitGUI();
                    }
                }
            }
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        /// <summary>按用户选择路径创建根资产并绑定窗口，此创建流程不注册 Undo。</summary>
        private void CreateRoot()
        {
            var path = EditorUtility.SaveFilePanelInProject("Create DataAssets Root", "DataConfigs", "asset", "Choose the root asset path.");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var root = CreateInstance<ScriptableObjectConfigs>();
            AssetDatabase.CreateAsset(root, path);
            SetCurrentConfigs(root);
            EditorGUIUtility.PingObject(root);
        }
    }
}
