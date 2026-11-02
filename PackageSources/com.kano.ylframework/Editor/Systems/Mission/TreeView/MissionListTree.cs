using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Editor.Common;
using YLFramework.Mission;

namespace YLFramework.Editor.Mission
{

    /// <summary>按任务 ID 构建可搜索、重排的平面列表，并提供条目创建、复制、粘贴与删除菜单。</summary>
    /// <remarks>列表重排的 Undo 由基类记录，窗口负责释放树的订阅；资产创建和文件删除不因此获得统一 Undo。</remarks>
    public class MissionListTree : ReorderableTreeView<MissionConfigData>
    {
        private const string MissionCopyFlag = "YLFramework.Mission.Objects.v1:";

        private MissionsConfig _missionsConfig;

        private bool _isContextClickItem;

        private Rect _iconRect;
        private Rect _nameRect;
        private Rect _idRect;

        private Dictionary<int, GUIContent> _nameContents = new Dictionary<int, GUIContent>();

        private GUIStyle _nameStyle;

        /// <summary>当前树绑定的源任务配表。</summary>
        public MissionsConfig MissionsConfig => _missionsConfig;
        /// <summary>将任务行 ID 映射到当前序列化列表槽位，供基类重排使用。</summary>
        protected override int GetItemIndex(int id) => _missionsConfig.missions.FindIndex(data => data && data.id == id);

        /// <summary>绑定配表及其当前任务列表，设置行高；创建后由调用方 Reload。</summary>
#if UNITY_6000_2_OR_NEWER
        public MissionListTree(TreeViewState<int> state, MissionsConfig missionsConfig) : base(missionsConfig, () => missionsConfig.missions, state)
#else
        public MissionListTree(TreeViewState state, MissionsConfig missionsConfig) : base(missionsConfig, () => missionsConfig.missions, state)
#endif

        {
            this._missionsConfig = missionsConfig;
            showBorder = true;
            rowHeight = 25;
        }

        /// <summary>跳过空条目，以任务 ID 建立平面行，并将名称、ID 与说明用于搜索。</summary>
#if UNITY_6000_2_OR_NEWER
        protected override void OnBuildRoot(TreeViewItem<int> root, IList<MissionConfigData> datas)
#else
        protected override void OnBuildRoot(TreeViewItem root, IList<MissionConfigData> datas)
#endif

        {
            for (int i = 0; i < datas.Count; i++)
            {
                MissionConfigData info = datas[i];
                if (info == null)
                {
                    continue;
                }
#if UNITY_6000_2_OR_NEWER
                TreeViewItem<int> child = new TreeViewItem<int>(info.id, 0, string.Format("{0}{1}{2}", info.missionName, info.id, info.description));
#else
                TreeViewItem child = new TreeViewItem(info.id, 0, string.Format("{0}{1}{2}", info.missionName, info.id, info.description));
#endif

                root.AddChild(child);
            }
        }

        /// <summary>重载行并清除配表的任务和参数查找缓存。</summary>
        protected override void RefreshDatas()
        {
            base.RefreshDatas();

            if (_missionsConfig != null)
            {
                _missionsConfig.ClearCache();
            }
        }


        /// <summary>按行 ID 查询任务定义，绘制图标、带说明的名称及 ID。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            MissionConfigData info = _missionsConfig.GetMissionConfigData(args.item.id);

            if (info == null)
            {
                return;
            }

            _iconRect.Set(0, args.rowRect.y, args.rowRect.height, args.rowRect.height);
            _nameRect.Set(args.rowRect.height, args.rowRect.y, args.rowRect.width - args.rowRect.height - 70, args.rowRect.height);

            _idRect.Set(args.rowRect.width - 70, args.rowRect.y, 70, args.rowRect.height);

            GUIContent iconContent = EditorAssetTool.GetTextureGuiContent(info.icon.projectName, info.icon.iconName);

            EditorGUI.LabelField(_iconRect, iconContent);

            if (_nameStyle == null)
            {
                _nameStyle = new GUIStyle(GUI.skin.label);
                _nameStyle.richText = true;
            }

            EditorGUI.LabelField(_nameRect, GetMissionName(info), _nameStyle);

            GUI.Label(_idRect, string.Format("id:{0}", info.id), "AnimationTimelineTick");

            GUI.Box(args.rowRect, string.Empty, "CN Box");
        }


        /// <summary>复用按任务 ID 缓存的内容，将描述作为名称附注与悬停说明。</summary>
        private GUIContent GetMissionName(MissionConfigData info)
        {
            if (!_nameContents.ContainsKey(info.id))
            {
                _nameContents.Add(info.id, new GUIContent());
            }

            if (string.IsNullOrEmpty(info.description))
            {
                _nameContents[info.id].text = info.missionName;
            }
            else
            {
                _nameContents[info.id].text = string.Format("{0}<color=grey>({1})</color>", info.missionName, info.description.Replace("\n", string.Empty));
            }

            _nameContents[info.id].tooltip = info.description;

            return _nameContents[info.id];
        }

        /// <summary>允许选择多个任务行以执行批量菜单操作。</summary>
#if UNITY_6000_2_OR_NEWER
        protected override bool CanMultiSelect(TreeViewItem<int> item)
#else
        protected override bool CanMultiSelect(TreeViewItem item)
#endif

        {
            return true;
        }

        /// <summary>空白处右键显示创建与粘贴菜单，避免同次条目右键重复弹出菜单。</summary>
        protected override void ContextClicked()
        {
            base.ContextClicked();

            if (_isContextClickItem)
            {
                _isContextClickItem = false;
                return;
            }

            GenericMenu menu = new GenericMenu();
            CreateMission(menu);
        }

        /// <summary>为当前选择显示删除、复制、副本创建和粘贴菜单。</summary>
        protected override void ContextClickedItem(int id)
        {
            base.ContextClickedItem(id);
            _isContextClickItem = true;

            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("删除"), false, () =>
            {
                foreach (var item in GetSelection())
                {
                    DeleteMission(item);
                }
            });

            menu.AddItem(new GUIContent("Duplicate"), false, () =>
            {
                IList<int> ids = GetSelection();

                //SetSelection(Array.Empty<int>());

                List<int> newIds = new List<int>();

                foreach (var item in ids)
                {
                    MissionConfigData data = _missionsConfig.GetMissionConfigData(item);
                    int i = CopyMission(data);
                    if (i == -1)
                    {
                        continue;
                    }

                    newIds.Add(i);
                }

                SetSelection(newIds);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            });

            menu.AddItem(new GUIContent("Copy"), false, CopySelection);



            CreateMission(menu);
        }

        /// <summary>沿用基类的行构建及搜索行为。</summary>
#if UNITY_6000_2_OR_NEWER
        protected override IList<TreeViewItem<int>> BuildRows(TreeViewItem<int> root)
#else
        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
#endif

        {
            return base.BuildRows(root);
        }

        /// <summary>依据剪贴板标记和配表数据类型限制补齐粘贴、创建菜单并显示。</summary>
        private void CreateMission(GenericMenu menu)
        {

            if (GUIUtility.systemCopyBuffer.StartsWith(MissionCopyFlag))
            {
                menu.AddItem(new GUIContent("Paste"), false, PasteSelection);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Paste"));
            }


            menu.AddSeparator(string.Empty);

            // 候选来自脚本扫描，并受当前配表的数据基类约束。
            List<Type> buffs = GetAllMissionTypes(_missionsConfig.GetDataType());

            foreach (var item in buffs)
            {
                string name = string.Format("创建任务/{0}", item.Name);
                if (!string.IsNullOrEmpty(item.Namespace))
                {
                    name = string.Format("{0}-{1}", name, item.Namespace);
                }

                if (item == typeof(MissionConfigData))
                {
                    name = "创建任务/基础配置";
                }

                GUIContent content = new GUIContent(name);

                menu.AddItem(content, false, () =>
                {
                    int id = MissionsConfigController.CreateMissionConfigData(item, _missionsConfig);

                    SetSelection(new int[] { id });

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                });
            }

            menu.ShowAsContext();
        }

        /// <summary>把已保存的选中资产身份写入剪贴板，不在复制时序列化任务内容。</summary>
        private void CopySelection()
        {
            var objects = new List<string>();
            foreach (var id in GetSelection())
            {
                var data = _missionsConfig.GetMissionConfigData(id);
                if (data && AssetDatabase.Contains(data))
                {
                    objects.Add(GlobalObjectId.GetGlobalObjectIdSlow(data).ToString());
                }
            }
            GUIUtility.systemCopyBuffer = MissionCopyFlag + JsonConvert.SerializeObject(objects);
        }

        /// <summary>解析全部剪贴板身份后，按粘贴时的源内容逐个创建副本，保存配表并重载选择。</summary>
        /// <remarks>先校验对象身份不等于创建过程原子化；后续复制失败时不会撤销已创建的副本。</remarks>
        private void PasteSelection()
        {
            var text = GUIUtility.systemCopyBuffer;
            if (!text.StartsWith(MissionCopyFlag, StringComparison.Ordinal))
            {
                return;
            }

            var identities = JsonConvert.DeserializeObject<List<string>>(text.Substring(MissionCopyFlag.Length), new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None })
                ?? throw new InvalidOperationException("任务剪贴板没有对象身份列表。");
            var sources = new List<MissionConfigData>();
            foreach (var identity in identities)
            {
                if (!GlobalObjectId.TryParse(identity, out var id) || !(GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) is MissionConfigData data))
                {
                    throw new InvalidOperationException("任务剪贴板对象已失效: " + identity);
                }

                sources.Add(data);
            }
            var selected = new List<int>();
            foreach (var source in sources)
            {
                selected.Add(CopyMission(source));
            }

            _missionsConfig.ClearCache();
            AssetDatabase.SaveAssetIfDirty(_missionsConfig);
            SetSelection(selected);
            Reload();
        }

        /// <summary>经资产所有权边界移除任务条目，清理配表缓存并重载列表。</summary>
        private void DeleteMission(int id)
        {
            if (_missionsConfig == null)
            {
                return;
            }

            MissionConfigData data = _missionsConfig.GetMissionConfigData(id);
            if (data == null)
            {
                return;
            }

            YLFramework.Editor.Domain.DomainAssetOwnership.DeleteEntry(_missionsConfig, _missionsConfig.missions, data, MissionAuthoring.GeneratedDirectory(_missionsConfig));
            _missionsConfig.ClearCache();
            Reload();
        }

        /// <summary>复制任务数据，分配新 ID 与当前配表归属，再生成不重名的显示名称并标脏。</summary>
        private int CopyMission(MissionConfigData buff)
        {
            MissionConfigData data = GameObject.Instantiate(buff);
            int id = MissionsConfigController.CreateMissionConfigData(data, _missionsConfig);
            data.missionName = GetMissionName(buff.missionName);

            EditorUtility.SetDirty(data);

            return id;
        }

        /// <summary>保留未占用名称，否则递增括号内数字后缀直至名称可用。</summary>
        private string GetMissionName(string missionName)
        {

            if (!IsContainsMissionName(missionName))
            {
                return missionName;
            }

            int count = 1;

            if (Regex.IsMatch(missionName, "\\([0-9]+\\)$"))
            {
                for (int i = missionName.Length - 1; i >= 0; i--)
                {
                    if (missionName[i] == '(')
                    {
                        string c = missionName.Substring(i + 1, missionName.Length - i - 2);
                        count = int.Parse(c);

                        missionName = missionName.Substring(0, i);
                        break;
                    }
                }
            }

            missionName = missionName.TrimEnd();

            string name = string.Empty;

            do
            {
                name = string.Format("{0} ({1})", missionName, count);
                count++;
            } while (IsContainsMissionName(name));

            return name;
        }

        /// <summary>按名称相等比较检查当前配表中的任务是否已经使用该名称。</summary>
        private bool IsContainsMissionName(string missionName)
        {
            MissionsConfig config = _missionsConfig;
            foreach (var item in config.missions)
            {
                if (item.missionName == missionName)
                {
                    return true;
                }
            }

            return false;
        }


        /// <summary>返回指定数据基类及脚本扫描到的派生类型，不过滤抽象类或保证运行任务类型有效。</summary>
        public static List<Type> GetAllMissionTypes(Type parent)
        {
            List<Type> states = new List<Type>();

            states.Add(parent);

            string[] scripts = AssetDatabase.FindAssets("t:Script");

            foreach (var item in scripts)
            {
                string path = AssetDatabase.GUIDToAssetPath(item);
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (script == null)
                {
                    continue;
                }

                Type type = script.GetClass();

                if (type == null)
                {
                    continue;
                }

                if (type.IsSubclassOf(parent))
                {
                    states.Add(type);
                }
            }

            return states;
        }

    }
}
