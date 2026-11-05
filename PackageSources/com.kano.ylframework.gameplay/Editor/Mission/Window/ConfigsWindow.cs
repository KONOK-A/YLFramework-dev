using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using static UnityEditor.Progress;
using YLFramework.Editor.Common;
using YLFramework.Mission;

namespace YLFramework.Editor.Mission
{
    /// <summary>任务配表分栏编辑窗口，左侧管理任务和参数列表，右侧编辑单个源定义。</summary>
    public class ConfigsWindow : SplitWindow
    {

        /// <summary>从菜单取得并显示任务配表窗口。</summary>
        [MenuItem("Window/YLFramework/MissionSystem", false, 10)]
        static void Open()
        {
            GetWindow<ConfigsWindow>().Show();
        }

        #region 字段

        private const string SelectGuidKey = "YLFramework.MissionSystem.SELECT_GUID_KEY";

        private GUIContent _currentMissionConfigContent;

        private string _searchStr;

        [SerializeField]
        private TreeViewStateGroup treeViewStateGroup = new TreeViewStateGroup();

#if UNITY_6000_2_OR_NEWER
        private TreeViewState<int> MissionListState => treeViewStateGroup.Get(CurrentMissionConfig.GetHashCode());
#else
        private TreeViewState MissionListState => treeViewStateGroup.Get(CurrentMissionConfig.GetHashCode());
#endif


        /// <summary>由窗口持有并在切表或停用时释放的任务树。</summary>
        internal MissionListTree MissionListTree;

        [SerializeField]
        private TreeViewStateGroup treeViewStateGroupParam = new TreeViewStateGroup();

#if UNITY_6000_2_OR_NEWER
        private TreeViewState<int> ParamListState => treeViewStateGroupParam.Get(CurrentMissionConfig.GetHashCode());
#else
        private TreeViewState ParamListState => treeViewStateGroupParam.Get(CurrentMissionConfig.GetHashCode());
#endif


        /// <summary>由窗口持有并在切表或停用时释放的参数树。</summary>
        internal ParamListTree ParamListTree;


        /// <summary>当前编辑的源任务配表。</summary>
        internal MissionsConfig CurrentMissionConfig;

        private float _timer = 0;

        /// <summary>窗口保留的任务显示 ID 状态。</summary>
        internal int CurrentShowMissionId = 0;

        /// <summary>窗口保留的参数显示索引状态。</summary>
        internal int CurrentShowParamIndex = 0;

        /// <summary>窗口保留的默认任务选择标记，-1 表示未指定。</summary>
        internal static int DefaultSelectMissionId = -1;


        private GUIContent[] _leftToolbarContents = null;

        private int _leftToolbarSelected = 0;

        private GUIStyle _leftToolbarStyle = null;

        private float _lastRepaintCount = 0;

        private int _currentId;
        /// <summary>任务选择切换保护的 GUI 事件计数，不是任务 ID 的修改次数。</summary>
        public int idChangeCount;

        private int _currentParameterId;
        /// <summary>参数选择切换保护的 GUI 事件计数。</summary>
        public int parameterIdChangeCount;

        #endregion


        #region 属性

        /// <summary>通过 EditorPrefs 保存的当前任务配表 GUID。</summary>
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

        /// <summary>初始化任务窗口标题和悬停说明。</summary>
        private void Awake()
        {
            titleContent = new GUIContent("任务配置窗口", "任务配置窗口");
        }

        /// <summary>以编辑器更新次数节流窗口重绘。</summary>
        private void Update()
        {
            _lastRepaintCount++;
            if (_lastRepaintCount > 15)
            {
                Repaint();
                _lastRepaintCount = 0;
            }

        }

        /// <summary>未绑定配表时限频尝试恢复选择，否则绘制分栏内容或空状态。</summary>
        protected override void OnGUI()
        {
            if (Time.realtimeSinceStartup - _timer > 1 && CurrentMissionConfig == null)
            {
                CurrentMissionConfig = GetCurrentConfig();
                _timer = Time.realtimeSinceStartup;
            }

            if (CurrentMissionConfig == null)
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

        /// <summary>优先恢复已保存 GUID 对应配表，失效时选择扫描到的首张配表并保存其 GUID。</summary>
        internal MissionsConfig GetCurrentConfig()
        {
            if (!string.IsNullOrEmpty(SelectGuid))
            {
                string assetsPath = AssetDatabase.GUIDToAssetPath(SelectGuid);
                MissionsConfig config = AssetDatabase.LoadAssetAtPath<MissionsConfig>(assetsPath);

                if (config != null)
                {
                    return config;
                }
            }

            List<MissionsConfig> allConfigs = MissionsConfigController.GetAllConfigs();

            if (allConfigs == null || allConfigs.Count == 0)
            {
                return null;
            }

            SelectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(allConfigs[0]));
            return allConfigs[0];
        }


        /// <summary>在没有任务配表时显示提示和创建入口。</summary>
        private void EmptyGui()
        {

            GUILayout.BeginHorizontal();

            GUILayout.FlexibleSpace();

            GUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            GUILayout.Label("未查询到任务配置文件!");
            if (GUILayout.Button("立即创建"))
            {
                CreateNew();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

        }

        /// <summary>在分栏左侧区域绘制配表选择、搜索和任务或参数列表。</summary>
        protected override void DrawGuiLeft(Rect rect)
        {
            base.DrawGuiLeft(rect);
            GUILayout.BeginArea(rect);
            DrawLeft(rect);
            GUILayout.EndArea();
        }

        /// <summary>在分栏右侧区域绘制当前选择详情。</summary>
        protected override void DrawGuiRight(Rect rect)
        {
            base.DrawGuiRight(rect);
            GUILayout.BeginArea(rect);
            DrawRight();
            GUILayout.EndArea();
        }

        /// <summary>处理配表切换和搜索，按工具栏选项创建、显示或释放对应列表树。</summary>
        private void DrawLeft(Rect left)
        {
            if (_currentMissionConfigContent == null)
            {
                _currentMissionConfigContent = new GUIContent();
            }

            if (CurrentMissionConfig == null)
            {
                return;
            }

            _currentMissionConfigContent.text = CurrentMissionConfig.GetDisplayName();

            _currentMissionConfigContent.tooltip = AssetDatabase.GetAssetPath(CurrentMissionConfig);

            GUILayout.BeginHorizontal();

            EditorGUI.BeginChangeCheck();

            _searchStr = EditorGUI.TextField(new Rect(left.x, left.y, left.width * 0.5f, 20), string.Empty, _searchStr, "SearchTextField");

            if (EditorGUI.EndChangeCheck())
            {
                if (string.IsNullOrEmpty(_searchStr))
                {
                    if (_leftToolbarSelected == 0 && MissionListTree.state.selectedIDs.Count > 0)
                    {
                        EditorApplication.delayCall += () =>
                        {
                            MissionListTree.FrameItem(MissionListTree.state.selectedIDs[0]);
                        };
                    }
                    else if (_leftToolbarSelected == 1 && ParamListTree.state.selectedIDs.Count > 0)
                    {
                        EditorApplication.delayCall += () =>
                        {
                            ParamListTree.FrameItem(ParamListTree.state.selectedIDs[0]);
                        };
                    }
                }
            }

            GUI.Box(new Rect(left.x + 1, left.y, left.width - 2, left.height), string.Empty, "CN Box");

            if (EditorGUI.DropdownButton(new Rect(left.x + left.width * 0.5f, left.y, left.width * 0.5f + 1, 20), _currentMissionConfigContent, FocusType.Passive, "ToolbarDropDownLeft"))
            {
                GUI.FocusControl(null);
                //return;

                GenericMenu menu = new GenericMenu();

                List<MissionsConfig> allConfigs = MissionsConfigController.GetAllConfigs();

                foreach (var item in allConfigs)
                {
                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(item));
                    bool isOn = SelectGuid == guid;

                    menu.AddItem(new GUIContent(item.GetDisplayName()), isOn, () =>
                    {
                        SelectGuid = guid;
                        EditorGUIUtility.PingObject(item);

                        if (!isOn)
                        {
                            SetCurrentConfigs(item);
                        }
                    });
                }

                menu.AddSeparator(string.Empty);

                menu.AddItem(new GUIContent("新建任务配表"), false, () =>
                {
                    CreateNew();
                });

                menu.ShowAsContext();
            }

            GUILayout.EndHorizontal();


            if (_leftToolbarContents == null || _leftToolbarContents.Length == 0)
            {
                _leftToolbarContents = new GUIContent[] { new GUIContent("任务列表"), new GUIContent("参数列表") };
            }

            if (_leftToolbarStyle == null)
            {
                _leftToolbarStyle = new GUIStyle("toolbarbuttonLeft");
            }

            _leftToolbarSelected = GUI.Toolbar(new Rect(left.x, left.y + 20, left.width, 20), _leftToolbarSelected, _leftToolbarContents);

            if (_leftToolbarSelected == 0)
            {
                if (MissionListTree == null)
                {
                    MissionListTree = new MissionListTree(MissionListState, CurrentMissionConfig);
                    MissionListTree.Reload();
                }

                MissionListTree.searchString = _searchStr;

                left.Set(left.x, left.y + 39, left.width + 1, left.height - 39);

                MissionListTree.OnGUI(left);

                if (MissionListTree != null && MissionListTree.MissionsConfig != CurrentMissionConfig)
                {
                    //missionListTree.SetSelection(Array.Empty<int>());
                    MissionListTree.Dispose();
                    MissionListTree = null;
                }

            }
            else if (_leftToolbarSelected == 1)
            {
                if (ParamListTree == null)
                {
                    ParamListTree = new ParamListTree(ParamListState, CurrentMissionConfig);
                    ParamListTree.Reload();
                }

                ParamListTree.searchString = _searchStr;

                left.Set(left.x, left.y + 39, left.width, left.height - 39);

                ParamListTree.OnGUI(left);

                if (ParamListTree != null && ParamListTree.MissionsConfig != CurrentMissionConfig)
                {
                    ParamListTree.Dispose();
                    ParamListTree = null;
                }
            }
        }

        /// <summary>根据任务或参数选择数量显示详情、空状态或不支持多项编辑的提示。</summary>
        private void DrawRight()
        {
            // 判断是否为空
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

            if (_leftToolbarSelected == 0)
            {
                if (MissionListState.selectedIDs.Count == 1)
                {
                    DrawMissionData();
                }
                else if (MissionListState.selectedIDs.Count == 0)
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
            else
            {

                if (ParamListState.selectedIDs.Count == 1)
                {
                    if (ParamListTree != null)
                    {
                        DrawParameterData();
                    }

                }
                else if (ParamListState.selectedIDs.Count == 0)
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
        }

        /// <summary>在任务选择稳定后，交由共享任务条目 Inspector 绘制详情。</summary>
        private void DrawMissionData()
        {
            int id = MissionListState.selectedIDs[0];

            // 防止延迟字段的旧输入写入新选择；等非 Layout 事件再提交选择，保持布局与重绘配对。
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

            MissionConfigData data = CurrentMissionConfig.GetMissionConfigData(id);
            MissionConfigDataInspector.Instance.OnInspectorGUI(data, this, CurrentMissionConfig);
        }

        /// <summary>沿用任务详情的选择切换保护，再绘制当前参数的专用 Inspector。</summary>
        private void DrawParameterData()
        {
            int id = ParamListState.selectedIDs[0];

            if (_currentParameterId != id)
            {
                parameterIdChangeCount++;
                if (parameterIdChangeCount > 1)
                {
                    if (Event.current.type != EventType.Layout)
                    {
                        _currentParameterId = id;
                        parameterIdChangeCount = 0;
                    }

                }
                return;
            }

            MissionParameter parameter = ParamListTree.GetMissionParameter(id);
            MissionParameterInspector.OnInspectorGUI(CurrentMissionConfig, parameter, ParamListTree);
        }

        /// <summary>判断当前配表是否没有有效任务条目；本检查不以参数列表是否为空为依据。</summary>
        internal bool IsEmpty()
        {
            if (CurrentMissionConfig == null)
            {
                return true;
            }

            for (int i = 0; i < CurrentMissionConfig.missions.Count; i++)
            {
                if (CurrentMissionConfig.missions[i] != null)
                {
                    return false;
                }
            }


            return true;
        }

        /// <summary>请求选择指定任务 ID；任务树尚未创建时仅安排一次延迟尝试。</summary>
        public void SetSelection(int id)
        {
            if (MissionListTree != null)
            {
                MissionListTree.SetSelection(new int[] { id });
            }
            else
            {
                EditorApplication.delayCall += () =>
                {
                    if (MissionListTree != null)
                    {
                        MissionListTree.SetSelection(new int[] { id });
                    }
                };
            }

        }

        /// <summary>释放旧列表树并切换源配表，重置详情选择标记并保存配表 GUID。</summary>
        public void SetCurrentConfigs(MissionsConfig missionsConfig)
        {
            ReleaseTrees();
            _currentId = -1;
            _currentParameterId = -1;

            CurrentMissionConfig = missionsConfig;

            SelectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(missionsConfig));

            if (MissionListTree != null)
            {
                MissionListTree.Reload();
            }
        }

        /// <summary>释放两棵树持有的基类订阅与拖拽上下文，并清除窗口引用。</summary>
        private void ReleaseTrees()
        {
            MissionListTree?.Dispose();
            ParamListTree?.Dispose();
            MissionListTree = null;
            ParamListTree = null;
        }
        /// <summary>窗口停用时释放列表树及共享 Inspector 的序列化选择缓存。</summary>
        private void OnDisable()
        {
            ReleaseTrees();
            MissionConfigDataInspector.Instance.ReleaseSelection();
            MissionParameterInspector.ReleaseSelection();
        }

        #endregion


        #region 创建

        /// <summary>让用户选择 Assets 内目录后创建任务配表，并记录新的配表选择 GUID。</summary>
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
                MissionsConfig skillsConfig = CreateInstance<MissionsConfig>();
                skillsConfig.startId = InitIdStart();
                MissionsConfigController.CreateAsset(skillsConfig, path);

                SelectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(skillsConfig));

                if (MissionListTree != null)
                {
                    MissionListTree.Reload();
                }
            }
            else
            {
                this.ShowNotification(new GUIContent("请把资源保存到Assets目录下!"));
            }
        }

        /// <summary>取现有配表最大起始 ID 再加 1000，作为新配表的起始值，不预留全局 ID 区间。</summary>
        private int InitIdStart()
        {
            int maxIdStart = 0;

            List<MissionsConfig> allConfigs = MissionsConfigController.GetAllConfigs();

            foreach (var item in allConfigs)
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
