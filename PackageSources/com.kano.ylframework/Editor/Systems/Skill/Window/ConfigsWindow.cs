using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Editor.Common;
using YLFramework.Skill;

namespace YLFramework.Editor.Skill
{
    /// <summary>技能配表分栏窗口，左侧选择源技能定义，右侧编辑单条数据。</summary>
    public class ConfigsWindow : SplitWindow
    {

        /// <summary>从菜单取得并显示技能配表窗口。</summary>
        [MenuItem("Window/YLFramework/SkillSystem", false, 10)]
        static void Open()
        {
            GetWindow<ConfigsWindow>().Show();
        }

        #region 字段

        private const string SelectGuidKey = "YLFramework.Skill.SelectedConfig";

        //private Rect left;

        private GUIContent _currentSkillConfigContent;

        private string _searchStr;

#if UNITY_6000_2_OR_NEWER
        private TreeViewState<int> SkillListState => treeViewStateGroup.Get(CurrentSkillConfig.GetHashCode());

#else
        private TreeViewState SkillListState => treeViewStateGroup.Get(CurrentSkillConfig.GetHashCode());
#endif


        [SerializeField]
        private TreeViewStateGroup treeViewStateGroup = new TreeViewStateGroup();

        /// <summary>由窗口负责在切表或停用时释放的技能树。</summary>
        internal SkillListTree SkillListTree;

        /// <summary>当前编辑的源技能配表。</summary>
        internal SkillsConfig CurrentSkillConfig;

        private float _timer = 0;

        /// <summary>窗口保留的技能显示 ID 状态。</summary>
        internal int CurrentShowSkillId = 0;

        /// <summary>窗口保留的默认技能选择标记，-1 表示未指定。</summary>
        internal static int DefaultSelectSkillId = -1;

        private Vector2 _rightScrollPosition;

        private float _lastRepaintCount;


        private int _currentId;
        /// <summary>详情选择切换保护的 GUI 事件计数，不是技能 ID 的修改次数。</summary>
        public int idChangeCount;

        #endregion


        #region 属性

        /// <summary>通过 EditorPrefs 保存的当前技能配表 GUID。</summary>
        internal static string SelectGuid
        {
            get
            {
                return EditorPrefs.GetString(SelectGuidKey);
            }
            set
            {
                EditorPrefs.SetString(SelectGuidKey, value);
            }

        }

        #endregion


        #region 生命周期

        /// <summary>设置技能窗口标题和悬停说明。</summary>
        private void Awake()
        {
            titleContent = new GUIContent("技能配置窗口", "技能配置窗口");
        }

        /// <summary>按编辑器更新次数节流窗口重绘。</summary>
        private void Update()
        {
            _lastRepaintCount++;
            if (_lastRepaintCount > 15)
            {
                Repaint();
                _lastRepaintCount = 0;
            }
        }

        /// <summary>未绑定配表时限频恢复选择，随后绘制分栏或无配表提示。</summary>
        protected override void OnGUI()
        {
            if (Time.realtimeSinceStartup - _timer > 1 && CurrentSkillConfig == null)
            {
                CurrentSkillConfig = GetCurrentConfig();
                _timer = Time.realtimeSinceStartup;
            }

            if (CurrentSkillConfig == null)
            {
                EmptyGui();
            }
            else
            {
                base.OnGUI();
            }
        }


        #endregion


        #region 方法

        /// <summary>恢复已保存 GUID 对应的源配表；失效时选择扫描到的首张配表并保存选择。</summary>
        internal SkillsConfig GetCurrentConfig()
        {
            if (!string.IsNullOrEmpty(SelectGuid))
            {
                string assetsPath = AssetDatabase.GUIDToAssetPath(SelectGuid);
                SkillsConfig config = AssetDatabase.LoadAssetAtPath<SkillsConfig>(assetsPath);

                if (config != null)
                {
                    return config;
                }
            }

            List<SkillsConfig> configs = SkillsConfigController.GetAllConfigs();

            if (configs == null || configs.Count == 0)
            {
                return null;
            }

            SelectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(configs[0]));
            return configs[0];
        }


        /// <summary>在左侧分栏区域绘制配表选择、搜索和技能列表。</summary>
        protected override void DrawGuiLeft(Rect rect)
        {
            base.DrawGuiLeft(rect);

            GUILayout.BeginArea(rect);
            DrawLeft(rect);
            GUILayout.EndArea();
        }

        /// <summary>在右侧分栏区域绘制选择详情或提示。</summary>
        protected override void DrawGuiRight(Rect rect)
        {
            base.DrawGuiRight(rect);

            GUILayout.BeginArea(rect);

            DrawRight();

            GUILayout.EndArea();
        }


        /// <summary>无技能配表时显示提示和配表创建入口。</summary>
        private void EmptyGui()
        {

            GUILayout.BeginHorizontal();

            GUILayout.FlexibleSpace();

            GUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            GUILayout.Label("未查询到技能配置文件!");
            if (GUILayout.Button("立即创建"))
            {
                CreateNew();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

        }

        /// <summary>处理源配表切换与搜索，按需创建或释放技能列表树。</summary>
        private void DrawLeft(Rect rect)
        {
            if (_currentSkillConfigContent == null)
            {
                _currentSkillConfigContent = new GUIContent();
            }

            if (CurrentSkillConfig == null)
            {
                return;
            }

            _currentSkillConfigContent.text = CurrentSkillConfig.name;
            _currentSkillConfigContent.tooltip = AssetDatabase.GetAssetPath(CurrentSkillConfig);

            GUILayout.BeginHorizontal();
            //

            EditorGUI.BeginChangeCheck();

            _searchStr = EditorGUILayout.TextField(string.Empty, _searchStr, "SearchTextField", GUILayout.MinWidth(0));

            if (EditorGUI.EndChangeCheck())
            {
                if (string.IsNullOrEmpty(_searchStr) && SkillListTree.state.selectedIDs.Count > 0)
                {
                    EditorApplication.delayCall += () =>
                    {
                        SkillListTree.FrameItem(SkillListTree.state.selectedIDs[0]);
                    };
                }
            }

            if (EditorGUILayout.DropdownButton(_currentSkillConfigContent, FocusType.Passive, EditorStyles.toolbarDropDown, GUILayout.Width(this.position.width * 0.35f * 0.3f)))
            {
                GenericMenu menu = new GenericMenu();
                List<SkillsConfig> configs = SkillsConfigController.GetAllConfigs();
                foreach (var item in configs)
                {
                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(item));
                    bool isOn = SelectGuid == guid;

                    menu.AddItem(new GUIContent(item.name), isOn, () =>
                    {
                        SelectGuid = guid;

                        EditorApplication.delayCall += () =>
                        {
                            EditorGUIUtility.PingObject(item);
                        };
                        if (!isOn)
                        {
                            SetCurrentConfigs(item);
                        }
                    });
                }

                menu.AddSeparator(string.Empty);

                menu.AddItem(new GUIContent("新建技能配表"), false, () =>
                {
                    CreateNew();
                });

                menu.ShowAsContext();
            }

            GUILayout.EndHorizontal();




            if (SkillListTree == null)
            {
                SkillListTree = new SkillListTree(SkillListState, CurrentSkillConfig);
                SkillListTree.Reload();
            }

            SkillListTree.searchString = _searchStr;

            rect.Set(rect.x, rect.y + 20, rect.width, rect.height - 20);


            SkillListTree.OnGUI(rect);


            if (SkillListTree != null && SkillListTree.SkillsConfig != CurrentSkillConfig)
            {
                SkillListTree.Dispose();
                SkillListTree = null;
            }
        }

        /// <summary>按有效条目和选择数量显示详情、空状态或多选不支持提示。</summary>
        private void DrawRight()
        {

            // 判断是否为空 TODO

            if (IsEmpty())
            {
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("空!");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
                return;
            }

            if (SkillListState.selectedIDs.Count == 1)
            {
                DrawData();
            }
            else if (SkillListState.selectedIDs.Count == 0)
            {
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("空!");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
            }
            else
            {
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                Color origin = GUI.color;
                GUI.color = Color.yellow;
                GUILayout.Label("不支持同时编辑多个配置!");
                GUI.color = origin;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
            }


        }

        /// <summary>选择稳定后在滚动区域内调用共享技能条目 Inspector。</summary>
        private void DrawData()
        {
            int id = SkillListState.selectedIDs[0];

            // 避免延迟字段的旧输入写入新选择；等非 Layout 事件再切换详情，保持布局与重绘配对。
            if (_currentId != id)
            {
                idChangeCount++;
                if (idChangeCount > 1)
                {
                    if (Event.current.type != EventType.Layout)
                    {
                        _currentId = id;
                        idChangeCount = 0;
                    }
                }
                return;
            }


            _rightScrollPosition = GUILayout.BeginScrollView(_rightScrollPosition);
            SkillConfigData data = CurrentSkillConfig.GetSkillConfigData(id);
            SkillConfigDataInspector.Instance.OnInspectorGUI(data, this, CurrentSkillConfig);

            GUILayout.EndScrollView();
        }

        /// <summary>判断当前配表是否缺少非空技能条目。</summary>
        internal bool IsEmpty()
        {
            if (CurrentSkillConfig == null)
            {
                return true;
            }

            for (int i = 0; i < CurrentSkillConfig.skills.Count; i++)
            {
                if (CurrentSkillConfig.skills[i] != null)
                {
                    return false;
                }
            }


            return true;
        }


        /// <summary>请求选择技能 ID；树尚未创建时仅安排一次延迟尝试。</summary>
        public void SetSelection(int id)
        {
            if (SkillListTree != null)
            {
                SkillListTree.SetSelection(new int[] { id });
            }
            else
            {
                EditorApplication.delayCall += () =>
                {
                    if (SkillListTree != null)
                    {
                        SkillListTree.SetSelection(new int[] { id });
                    }
                };
            }
        }

        /// <summary>释放旧树并切换源配表，重置详情选择与控件焦点，保存配表 GUID。</summary>
        public void SetCurrentConfigs(SkillsConfig item)
        {
            SkillListTree?.Dispose();
            SkillListTree = null;
            _currentId = -1; // 重置当前选中的id
            CurrentSkillConfig = item;
            GUI.FocusControl(null);
            if (SkillListTree != null)
            {
                SkillListTree.Reload();
            }
            SelectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(item));
        }

        /// <summary>窗口停用时释放树订阅，并清理共享技能 Inspector 的序列化选择缓存。</summary>
        private void OnDisable()
        {
            SkillListTree?.Dispose();
            SkillListTree = null;
            SkillConfigDataInspector.Instance.ReleaseSelection();
        }

        #endregion


        #region 创建

        /// <summary>在用户选定的 Assets 内目录创建技能配表，并记录新的配表选择 GUID。</summary>
        private void CreateNew()
        {
            string path = EditorUtility.OpenFolderPanel("请选择文件夹", Application.dataPath, string.Empty);

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (path.StartsWith(Application.dataPath))
            {
                path = path.Replace(Application.dataPath, string.Empty);
                path = string.Format("Assets{0}", path);
                //Debug.LogFormat("path:{0}", path);
                SkillsConfig skillsConfig = CreateInstance<SkillsConfig>();
                skillsConfig.startId = InitIdStart();

                SkillsConfigController.CreateAsset(skillsConfig, path);

                SelectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(skillsConfig));

                if (SkillListTree != null)
                {
                    SkillListTree.Reload();
                }
            }
            else
            {
                this.ShowNotification(new GUIContent("请把资源保存到Assets目录下!"));
            }
        }

        /// <summary>以现有配表最大起始 ID 加 1000 初始化新配表，不检查所有技能 ID 的全局占用。</summary>
        private int InitIdStart()
        {
            int maxIdStart = 0;

            foreach (var item in SkillsConfigController.GetAllConfigs())
            {
                if (item.startId > maxIdStart)
                {
                    maxIdStart = item.startId;
                }
            }

            return maxIdStart + 1000;
        }

        #endregion


    }

}
