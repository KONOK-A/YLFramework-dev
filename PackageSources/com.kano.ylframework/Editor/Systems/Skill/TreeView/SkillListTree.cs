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

    /// <summary>保存技能图标资源标识与对应 GUI 内容的缓存项。</summary>
    internal class IconGuiContent
    {
        /// <summary>图标资源模块名称。</summary>
        public string ProjectName;
        /// <summary>模块内图标名称。</summary>
        public string IconName;
        /// <summary>对应图标的 GUI 内容。</summary>
        public GUIContent Content;
    }

    /// <summary>以技能 ID 构建可搜索、重排的平面列表，提供定义创建、复制和删除菜单。</summary>
    /// <remarks>基类负责列表重排的 Undo，窗口负责释放树订阅；不会因此为所有资产操作提供撤销或回滚。</remarks>
    public class SkillListTree : ReorderableTreeView<SkillConfigData>
    {

        private SkillsConfig _skillsConfig;

        private bool _isContextClickItem;

        private Rect _iconRect;
        private Rect _nameRect;
        private Rect _idRect;

        private Dictionary<int, IconGuiContent> _contents = new Dictionary<int, IconGuiContent>();

        private GUIStyle _labelStyle;

        private Dictionary<int, GUIContent> _displayNameContents = new Dictionary<int, GUIContent>();


        /// <summary>当前树绑定的源技能配表。</summary>
        public SkillsConfig SkillsConfig => _skillsConfig;

        /// <summary>绑定配表及其当前技能列表并设置行高，创建后由调用方重载。</summary>
#if UNITY_6000_2_OR_NEWER
        public SkillListTree(TreeViewState<int> state, SkillsConfig skillsConfig) : base(skillsConfig, () => skillsConfig.skills, state)
#else
        public SkillListTree(TreeViewState state, SkillsConfig skillsConfig) : base(skillsConfig, () => skillsConfig.skills, state)
#endif

        {
            this._skillsConfig = skillsConfig;
            showBorder = true;
            rowHeight = 25;
        }

        /// <summary>跳过空条目，以技能 ID 建立平面行，名称和 ID 用于搜索。</summary>
#if UNITY_6000_2_OR_NEWER
        protected override void OnBuildRoot(TreeViewItem<int> root, IList<SkillConfigData> datas)
#else
        protected override void OnBuildRoot(TreeViewItem root, IList<SkillConfigData> datas)
#endif

        {

            foreach (var item in datas)
            {
                if (item == null)
                {
                    continue;
                }
#if UNITY_6000_2_OR_NEWER
                TreeViewItem<int> child = new TreeViewItem<int>(item.id, 0, string.Format("{0}-{1}", item.skillName, item.id));
#else
                TreeViewItem child = new TreeViewItem(item.id, 0, string.Format("{0}-{1}", item.skillName, item.id));
#endif
                root.AddChild(child);
            }
        }


        /// <summary>按行 ID 查询技能定义并绘制图标、带描述的名称和 ID。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            if (_skillsConfig == null)
            {
                return;
            }

            SkillConfigData info = _skillsConfig.GetSkillConfigData(args.item.id);

            if (info == null)
            {
                return;
            }

            _iconRect.Set(0, args.rowRect.y, args.rowRect.height, args.rowRect.height);
            _nameRect.Set(args.rowRect.height, args.rowRect.y, args.rowRect.width - args.rowRect.height - 70, args.rowRect.height);

            _idRect.Set(args.rowRect.width - 70, args.rowRect.y, 70, args.rowRect.height);

            EditorGUI.LabelField(_iconRect, EditorAssetTool.GetTextureGuiContent(info.icon.projectName, info.icon.iconName));

            if (!_displayNameContents.ContainsKey(info.id))
            {
                _displayNameContents.Add(info.id, new GUIContent());
            }

            _displayNameContents[info.id].text = GetDisplayName(info);
            _displayNameContents[info.id].tooltip = info.description;

            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(EditorStyles.label);
                _labelStyle.richText = true;
            }

            EditorGUI.LabelField(_nameRect, _displayNameContents[info.id], _labelStyle);

            GUI.Label(_idRect, string.Format("id:{0}", info.id), "AnimationTimelineTick");

            GUI.Box(args.rowRect, string.Empty, "CN Box");
        }

        /// <summary>允许选择多个技能行以执行批量菜单操作。</summary>
#if UNITY_6000_2_OR_NEWER
        protected override bool CanMultiSelect(TreeViewItem<int> item)
#else
        protected override bool CanMultiSelect(TreeViewItem item)
#endif

        {
            return true;
        }

        /// <summary>空白处右键显示技能创建菜单，避免条目右键重复触发菜单。</summary>
        protected override void ContextClicked()
        {
            base.ContextClicked();

            if (_isContextClickItem)
            {
                _isContextClickItem = false;
                return;
            }

            GenericMenu menu = new GenericMenu();
            CreateSkill(menu);
        }

        /// <summary>为当前选择提供删除、复制定义和创建技能菜单。</summary>
        protected override void ContextClickedItem(int id)
        {
            base.ContextClickedItem(id);
            _isContextClickItem = true;

            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("删除"), false, () =>
            {
                IList<int> selects = GetSelection();

                foreach (var item in selects)
                {
                    DeleteSkill(item);
                }

                AssetDatabase.SaveAssets();
                Reload();
            });

            menu.AddItem(new GUIContent("复制"), false, () =>
            {
                IList<int> selects = GetSelection();

                List<int> ids = new List<int>();

                foreach (var item in selects)
                {
                    int id = CopySkill(item);
                    if (id == -1)
                    {
                        continue;
                    }

                    ids.Add(id);
                }

                AssetDatabase.SaveAssets();
                Reload();

                SetSelection(ids);
            });

            menu.AddSeparator(string.Empty);
            CreateSkill(menu);
        }

        /// <summary>根据配表绑定的数据基类扫描候选，创建菜单项并显示菜单。</summary>
        private void CreateSkill(GenericMenu menu)
        {
            Type parent = _skillsConfig.GetDataType();

            //Debug.Log(parent.FullName);

            // 扫描的是配置数据类型，技能运行脚本的绑定由条目绘制器另行处理。
            List<Type> skills = ScriptBindController.GetAllSkillConfigDataTypes(parent);

            foreach (var item in skills)
            {
                string name = string.Format("创建技能/{0}", item.Name);
                if (!string.IsNullOrEmpty(item.Namespace))
                {
                    name = string.Format("{0}-{1}", name, item.Namespace);
                }

                if (item == typeof(SkillConfigData))
                {
                    name = "创建技能/基础配置";
                }

                GUIContent content = new GUIContent(name);

                menu.AddItem(content, false, () =>
                {
                    int id = SkillsConfigController.CreateSkillConfigData(item, _skillsConfig);
                    SetSelection(new int[] { id });
                });
            }


            menu.ShowAsContext();
        }

        /// <summary>经资产所有权边界移除技能条目，再清缓存并重载行。</summary>
        private void DeleteSkill(int id)
        {
            if (_skillsConfig == null)
            {
                return;
            }

            SkillConfigData data = _skillsConfig.GetSkillConfigData(id);
            if (data == null)
            {
                return;
            }

            YLFramework.Editor.Domain.DomainAssetOwnership.DeleteEntry(_skillsConfig, _skillsConfig.skills, data, SkillsConfigController.GetSkillConfigDataPath(_skillsConfig));
            _skillsConfig.ClearCache();
            Reload();
        }

        /// <summary>复制源技能定义，创建新 ID 条目后分配副本名称；找不到源条目时返回 -1。</summary>
        private int CopySkill(int id)
        {
            if (_skillsConfig == null)
            {
                return -1;
            }

            SkillConfigData data = _skillsConfig.GetSkillConfigData(id);
            if (data == null)
            {
                return -1;
            }

            SkillConfigData skillConfigData = GameObject.Instantiate(data);
            int newId = SkillsConfigController.CreateSkillConfigData(skillConfigData, _skillsConfig);
            skillConfigData.skillName = SkillsConfigController.GetCopyName(data.skillName, _skillsConfig);
            return newId;
        }


        /// <summary>重载行并清除源配表的技能查找缓存。</summary>
        protected override void RefreshDatas()
        {
            base.RefreshDatas();
            if (_skillsConfig != null)
            {
                _skillsConfig.ClearCache();
            }
        }

        /// <summary>将技能行 ID 映射回当前序列化列表索引以支持重排。</summary>
        protected override int GetItemIndex(int id) => _skillsConfig.skills.FindIndex(data => data && data.id == id);

        /// <summary>组合技能名称与单行描述，供列表富文本标签显示。</summary>
        private string GetDisplayName(SkillConfigData skillConfigData)
        {
            if (string.IsNullOrEmpty(skillConfigData.description))
            {
                return skillConfigData.skillName;
            }

            return string.Format("{0}(<color=grey>{1}</color>)", skillConfigData.skillName, skillConfigData.description.Replace("\n", string.Empty));
        }

    }
}
