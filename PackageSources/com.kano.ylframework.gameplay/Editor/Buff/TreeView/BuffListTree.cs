using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Buff;
using YLFramework.Editor.Common;


namespace YLFramework.Editor.Buff
{


    /// <summary>展示单个 Buff 配表的定义，并把创建、复制、删除及拖拽重排接入作者工具。</summary>
    /// <remarks>拖拽沿用基类的 Undo 与标脏，不在此立即保存；创建、复制和删除各自按对应入口处理保存。</remarks>
    public class BuffListTree : ReorderableTreeView<BuffConfigData>
    {

        private BuffsConfig _buffsConfig;

        private bool _isContextClickItem;

        private Rect _iconRect;
        private Rect _nameRect;
        private Rect _idRect;


        private Dictionary<int, GUIContent> _nameContents = new Dictionary<int, GUIContent>();


        private GUIStyle _styleName;


        /// <summary>此树当前维护的共享配表资产。</summary>
        public BuffsConfig BuffsConfig => _buffsConfig;
        /// <summary>把 Buff 业务 ID 映射为配表列表的首个匹配槽位。</summary>
        protected override int GetItemIndex(int id) => _buffsConfig.buffs.FindIndex(data => data && data.id == id);


#if UNITY_6000_2_OR_NEWER
        /// <summary>为配表建立可重排的定义树；使用结束后须 Dispose 以解除基类订阅。</summary>
        public BuffListTree(TreeViewState<int> state, BuffsConfig buffsConfig) : base(buffsConfig, () => buffsConfig.buffs, state)
#else
        /// <summary>为配表建立可重排的定义树；使用结束后须 Dispose 以解除基类订阅。</summary>
        public BuffListTree(TreeViewState state, BuffsConfig buffsConfig) : base(buffsConfig, () => buffsConfig.buffs, state)
#endif

        {
            this._buffsConfig = buffsConfig;
            showBorder = true;
            rowHeight = 25;
        }

#if UNITY_6000_2_OR_NEWER
        /// <summary>按定义列表顺序构建平面行，跳过空项并使用业务 ID 作为行标识。</summary>
        protected override void OnBuildRoot(TreeViewItem<int> root, IList<BuffConfigData> datas)
#else
        /// <summary>按定义列表顺序构建平面行，跳过空项并使用业务 ID 作为行标识。</summary>
        protected override void OnBuildRoot(TreeViewItem root, IList<BuffConfigData> datas)
#endif

        {

            for (int i = 0; i < datas.Count; i++)
            {
                BuffConfigData info = datas[i];
                if (info == null)
                {
                    continue;
                }

#if UNITY_6000_2_OR_NEWER
                TreeViewItem<int> child = new TreeViewItem<int>(info.id, 0, string.Format("{0}-{1}", info.buffName, info.id));
#else
                TreeViewItem child = new TreeViewItem(info.id, 0, string.Format("{0}-{1}", info.buffName, info.id));
#endif

                root.AddChild(child);
            }

        }

        /// <summary>绘制 Buff 图标、名称、描述提示与 ID，不编辑定义内容。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            if (_buffsConfig == null)
            {
                return;
            }

            BuffConfigData info = _buffsConfig.GetBuffData(args.item.id);

            if (info == null)
            {
                return;
            }

            _iconRect.Set(0, args.rowRect.y, args.rowRect.height, args.rowRect.height);
            _nameRect.Set(args.rowRect.height, args.rowRect.y, args.rowRect.width - args.rowRect.height - 70, args.rowRect.height);

            _idRect.Set(args.rowRect.width - 70, args.rowRect.y, 70, args.rowRect.height);

            GUIContent iconContent = EditorAssetTool.GetTextureGuiContent(info.icon.projectName, info.icon.iconName);

            EditorGUI.LabelField(_iconRect, iconContent);

            if (!_nameContents.ContainsKey(info.id))
            {
                _nameContents.Add(info.id, new GUIContent());
            }

            if (string.IsNullOrEmpty(info.description))
            {
                _nameContents[info.id].text = info.buffName;
                _nameContents[info.id].tooltip = string.Empty;
            }
            else
            {
                string des = info.description.Replace('\n', '\0');
                _nameContents[info.id].text = string.Format("{0}(<color=grey>{1}</color>)", info.buffName, des);
                _nameContents[info.id].tooltip = info.description;
            }

            if (_styleName == null)
            {

                _styleName = GUI.skin.label;
                _styleName.richText = true;
            }

            EditorGUI.LabelField(_nameRect, _nameContents[info.id], _styleName);

            GUI.Label(_idRect, string.Format("id:{0}", info.id), "AnimationTimelineTick");

            GUI.Box(args.rowRect, string.Empty, "CN Box");
        }

#if UNITY_6000_2_OR_NEWER
        /// <summary>允许同时选择多行以执行批量复制、删除或重排。</summary>
        protected override bool CanMultiSelect(TreeViewItem<int> item)
#else
        /// <summary>允许同时选择多行以执行批量复制、删除或重排。</summary>
        protected override bool CanMultiSelect(TreeViewItem item)
#endif

        {
            return true;
        }

        /// <summary>在空白区提供创建菜单，并避开同次右键已经处理过的条目菜单。</summary>
        protected override void ContextClicked()
        {
            base.ContextClicked();

            if (_isContextClickItem)
            {
                _isContextClickItem = false;
                return;
            }

            GenericMenu menu = new GenericMenu();
            CreateBuff(menu);
        }

        /// <summary>为当前选择提供删除、复制和创建入口，完成后刷新树与资产状态。</summary>
        protected override void ContextClickedItem(int id)
        {
            base.ContextClickedItem(id);
            _isContextClickItem = true;

            if (_buffsConfig == null)
            {
                return;
            }

            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("删除"), false, () =>
            {
                foreach (var item in state.selectedIDs)
                {
                    DeleteBuff(item);
                }

                // 保存
                EditorUtility.SetDirty(_buffsConfig);
                AssetDatabase.SaveAssets();
                Reload();
            });

            menu.AddItem(new GUIContent("复制"), false, () =>
            {
                List<int> temp = new List<int>();

                foreach (var item in state.selectedIDs)
                {
                    BuffConfigData data = _buffsConfig.GetBuffData(item);
                    int copyId = CopyBuff(data);
                    if (copyId != -1)
                    {
                        temp.Add(copyId);
                    }
                }
                SetSelection(temp);
                Reload();
                if (temp.Count > 0)
                {
                    FrameItem(temp[temp.Count - 1]); // 定位
                }
            });

            menu.AddSeparator(string.Empty);
            CreateBuff(menu);
        }

        /// <summary>按配表绑定的数据基类列出创建菜单，创建后选中并定位新条目。</summary>
        private void CreateBuff(GenericMenu menu)
        {
            //  查找项目中所有继承自
            List<Type> buffs = GetAllBuffTypes(_buffsConfig.GetDataType());

            foreach (var item in buffs)
            {
                string name = string.Format("创建Buff/{0}", item.Name);
                if (!string.IsNullOrEmpty(item.Namespace))
                {
                    name = string.Format("{0}-{1}", name, item.Namespace);
                }

                if (item == typeof(BuffConfigData))
                {
                    name = "创建Buff/基础配置";
                }

                GUIContent content = new GUIContent(name);

                menu.AddItem(content, false, () =>
                {
                    int id = BuffsConfigController.CreateBuffConfigData(item, _buffsConfig);
                    Reload();
                    if (id != -1)
                    {
                        SetSelection(new int[] { id });
                        FrameItem(id);
                    }
                });
            }


            menu.ShowAsContext();
        }

        /// <summary>按资产归属删除条目或仅解除外部引用，再清除配表缓存并刷新树。</summary>
        private void DeleteBuff(int id)
        {
            if (_buffsConfig == null)
            {
                return;
            }

            BuffConfigData data = _buffsConfig.GetBuffData(id);
            if (data == null)
            {
                return;
            }

            YLFramework.Editor.Domain.DomainAssetOwnership.DeleteEntry(_buffsConfig, _buffsConfig.buffs, data, BuffsConfigController.GetBuffConfigDataPath(_buffsConfig));
            _buffsConfig.ClearCache();
            Reload();

        }

        /// <summary>复制定义后通过创建入口分配新 ID，再设置本表内不重名的显示名。</summary>
        private int CopyBuff(BuffConfigData buff)
        {
            if (buff == null)
            {
                return -1;
            }

            BuffConfigData data = GameObject.Instantiate(buff);

            BuffsConfigController.CreateBuffConfigData(data, _buffsConfig);

            data.buffName = GetBuffName(buff.buffName);

            EditorUtility.SetDirty(data);

            return data.id;
        }

        /// <summary>从原名称的数字后缀继续寻找本表内未占用的复制名称。</summary>
        private string GetBuffName(string buffName)
        {

            int count = 1;

            if (Regex.IsMatch(buffName, "\\([0-9]+\\)$"))
            {
                for (int i = buffName.Length - 1; i >= 0; i--)
                {
                    if (buffName[i] == '(')
                    {
                        string c = buffName.Substring(i + 1, buffName.Length - i - 2);
                        count = int.Parse(c);

                        buffName = buffName.Substring(0, i);
                        break;
                    }
                }
            }

            buffName = buffName.TrimEnd();

            string name = string.Empty;

            do
            {
                name = string.Format("{0} ({1})", buffName, count);
                count++;
            } while (IsContainsBuffName(name));

            return name;
        }

        /// <summary>检查当前配表中是否已有相同的 Buff 显示名。</summary>
        private bool IsContainsBuffName(string buffName)
        {

            foreach (var item in _buffsConfig.buffs)
            {
                if (item.buffName == buffName)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>返回给定配置基类及扫描到的派生脚本类型，不过滤抽象类型。</summary>
        public static List<Type> GetAllBuffTypes(Type parent)
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

        /// <summary>沿用基类重建树的流程，并使配表的 ID 查询缓存失效。</summary>
        protected override void RefreshDatas()
        {
            base.RefreshDatas();

            if (_buffsConfig)
            {
                _buffsConfig.ClearCache();
            }
        }

    }
}
