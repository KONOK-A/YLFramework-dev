using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Buff;
using YLFramework.Editor.Common;

namespace YLFramework.Editor.Buff
{
#if UNITY_6000_2_OR_NEWER
    /// <summary>把效果定义及所属类型分组关联到一行树节点。</summary>
    public class EffectTreeViewItem : TreeViewItem<int>
#else
    /// <summary>把效果定义及所属类型分组关联到一行树节点。</summary>
    public class EffectTreeViewItem : TreeViewItem
#endif

    {
        /// <summary>本行展示的效果定义资产。</summary>
        public EffectData EffectData;

        /// <summary>本行所在的 C# 类型分组，不是业务分类。</summary>
        public GroupEffectData GroupEffectData;

        /// <summary>以效果 ID、显示名和给定深度构造数据行。</summary>
        public EffectTreeViewItem(GroupEffectData groupEffectData, EffectData data, int depth) : base(data.id, depth, data.effectName)
        {
            this.EffectData = data;
            this.GroupEffectData = groupEffectData;
        }
    }

#if UNITY_6000_2_OR_NEWER
    /// <summary>按 C# 类型分组展示效果定义，支持组内排序、拖拽及资产编辑菜单。</summary>
    public class EffectListTree : TreeView<int>
#else
    /// <summary>按 C# 类型分组展示效果定义，支持组内排序、拖拽及资产编辑菜单。</summary>
    public class EffectListTree : TreeView
#endif

    {

        private const string EffectDraggedDataKey = "YLFramework.Buff.EffectList.DraggedItemId";


        private bool _isContextClickItem;

        private GUIStyle _styleLabel;

        private GUIStyle _styleDropdown;

        private Dictionary<int, GUIContent> _desContents = new Dictionary<int, GUIContent>();

        private Dictionary<int, GUIContent> _typeContents = new Dictionary<int, GUIContent>();

        private Dictionary<int, GUIContent> _displayContents = new Dictionary<int, GUIContent>();

        private float _lastRefreshTime;

#if UNITY_6000_2_OR_NEWER
        /// <summary>用给定状态和多列表头建立效果树，并订阅列头排序变化。</summary>
        public EffectListTree(TreeViewState<int> state , MultiColumnHeaderState multiColumnHeaderState) : base(state, new MultiColumnHeader(multiColumnHeaderState))
#else
        /// <summary>用给定状态和多列表头建立效果树，并订阅列头排序变化。</summary>
        public EffectListTree(TreeViewState state, MultiColumnHeaderState multiColumnHeaderState) : base(state, new MultiColumnHeader(multiColumnHeaderState))
#endif

        {
            showBorder = true;
            rowHeight = 25;
            showAlternatingRowBackgrounds = true;

            multiColumnHeader.sortingChanged += SortChanged;

        }

        /// <summary>按列头状态对各分组的列表排序，然后保存项目设置并重建树。</summary>
        private void SortChanged(MultiColumnHeader multiColumnHeader)
        {
            bool asc = false;

            if (multiColumnHeader.sortedColumnIndex >= 0 && multiColumnHeader.sortedColumnIndex < 3)
            {
                asc = multiColumnHeader.IsSortedAscending(multiColumnHeader.sortedColumnIndex);
            }

            foreach (var groupEffectData in BuffSystemSettings.Instance.AllEffectDataWithType)
            {
                switch (multiColumnHeader.sortedColumnIndex)
                {
                    case 0:
                        // 根据名称排序
                        groupEffectData.effectDatas.Sort((x, y) =>
                        {
                            return !asc ? x.effectName.CompareTo(y.effectName) : y.effectName.CompareTo(x.effectName);
                        });
                        break;
                    case 1:
                        // 根据id排序
                        groupEffectData.effectDatas.Sort((x, y) =>
                        {
                            return !asc ? x.id.CompareTo(y.id) : y.id.CompareTo(x.id);
                        });
                        break;

                    case 2:
                        // 根据类型排序
                        groupEffectData.effectDatas.Sort((x, y) =>
                        {
                            return !asc ? x.type.CompareTo(y.type) : y.type.CompareTo(x.type);
                        });
                        break;
                }

            }

            BuffSystemSettings.Save();

            Reload();
        }
#if UNITY_6000_2_OR_NEWER
        /// <summary>从当前效果分组构造根节点和数据行。</summary>
        protected override TreeViewItem<int> BuildRoot()
#else
        /// <summary>从当前效果分组构造根节点和数据行。</summary>
        protected override TreeViewItem BuildRoot()
#endif

        {
            return CreateView();
        }


        /// <summary>创建效果列表的默认多列表头状态。</summary>
        internal static MultiColumnHeaderState CreateDefaultMultiColumnHeaderState()
        {
            MultiColumnHeaderState state = new MultiColumnHeaderState(GetColumns());

            return state;
        }
        /// <summary>配置名称、ID、业务类型与描述列的宽度和排序能力。</summary>
        private static MultiColumnHeaderState.Column[] GetColumns()
        {
            var retVal = new MultiColumnHeaderState.Column[] {
                new MultiColumnHeaderState.Column(),
                new MultiColumnHeaderState.Column(),
                new MultiColumnHeaderState.Column(),
                new MultiColumnHeaderState.Column(),
                //new MultiColumnHeaderState.Column()
            };
            retVal[0].headerContent = new GUIContent("名称", "名称");
            retVal[0].minWidth = 50;
            retVal[0].width = 320;
            retVal[0].maxWidth = 1000;
            retVal[0].headerTextAlignment = TextAlignment.Left;
            retVal[0].canSort = true;
            retVal[0].autoResize = true;


            retVal[1].headerContent = new GUIContent("ID", "ID");
            retVal[1].minWidth = 30;
            retVal[1].width = 80;
            retVal[1].maxWidth = 1000;
            retVal[1].headerTextAlignment = TextAlignment.Left;
            retVal[1].canSort = true;
            retVal[1].autoResize = true;


            retVal[2].headerContent = new GUIContent("类型", "类型,可根据类型查询效果!");
            retVal[2].minWidth = 30;
            retVal[2].width = 100;
            retVal[2].maxWidth = 1000;
            retVal[2].headerTextAlignment = TextAlignment.Left;
            retVal[2].canSort = true;
            retVal[2].autoResize = true;

            retVal[3].headerContent = new GUIContent("描述", "描述!");
            retVal[3].minWidth = 30;
            retVal[3].width = 180;
            retVal[3].maxWidth = 1000;
            retVal[3].headerTextAlignment = TextAlignment.Left;
            retVal[3].canSort = false;
            retVal[3].autoResize = true;

            return retVal;
        }

#if UNITY_6000_2_OR_NEWER
        /// <summary>按分组顺序建立树；跨分组遇到重复效果 ID 时只展示首项。</summary>
        public TreeViewItem<int> CreateView()
#else
        /// <summary>按分组顺序建立树；跨分组遇到重复效果 ID 时只展示首项。</summary>
        public TreeViewItem CreateView()
#endif

        {

#if UNITY_6000_2_OR_NEWER

            TreeViewItem<int> root = new TreeViewItem<int>(0, -1);
#else
            TreeViewItem root = new TreeViewItem(0, -1);
#endif


            List<int> ids = new List<int>();

            foreach (GroupEffectData groupEffectData in BuffSystemSettings.Instance.AllEffectDataWithType)
            {
                if (groupEffectData == null || groupEffectData.IsEmpty())
                {
                    continue;
                }

                string groupName = groupEffectData.GroupName;
#if UNITY_6000_2_OR_NEWER
                TreeViewItem<int> child = new TreeViewItem<int>(groupName.GetHashCode(), 0, groupName);
#else
                TreeViewItem child = new TreeViewItem(groupName.GetHashCode(), 0, groupName);
#endif

                foreach (EffectData data in groupEffectData.effectDatas)
                {

                    if (data == null)
                    {
                        continue;
                    }

                    // 过滤掉重复的数据
                    if (ids.Contains(data.id))
                    {
                        continue;
                    }

                    ids.Add(data.id);

                    EffectTreeViewItem d = new EffectTreeViewItem(groupEffectData, data, 1);
                    child.AddChild(d);
                }

                root.AddChild(child);
            }

            return root;
        }

        /// <summary>移除登记列表中的空项，并返回当前最大效果 ID 加一。</summary>
        private int GenerateId()
        {
            int id = 0;


            for (int i = BuffSystemSettings.Instance.AllEffectDatas.Count - 1; i >= 0; i--)
            {
                EffectData item = BuffSystemSettings.Instance.AllEffectDatas[i];

                if (!item)
                {
                    BuffSystemSettings.Instance.AllEffectDatas.RemoveAt(i);
                    continue;
                }

                if (item.id > id)
                {
                    id = item.id;
                }
            }


            return ++id;
        }

#if UNITY_6000_2_OR_NEWER
        /// <summary>允许多选效果行，以执行批量复制、删除或同组拖拽。</summary>
        protected override bool CanMultiSelect(TreeViewItem<int> item)
#else
        /// <summary>允许多选效果行，以执行批量复制、删除或同组拖拽。</summary>
        protected override bool CanMultiSelect(TreeViewItem item)
#endif

        {
            return true;
        }

        /// <summary>为空白区展示创建菜单，避免与本次条目右键菜单重复弹出。</summary>
        protected override void ContextClicked()
        {
            base.ContextClicked();

            if (_isContextClickItem)
            {
                _isContextClickItem = false;
                return;
            }

            GenericMenu menu = new GenericMenu();
            ShowCreateEffectMenu(menu);
        }

        /// <summary>按右键节点提供创建入口，效果数据行还提供批量删除和复制。</summary>
        protected override void ContextClickedItem(int id)
        {
            base.ContextClickedItem(id);
            _isContextClickItem = true;

            EffectTreeViewItem item = FindItem(id, rootItem) as EffectTreeViewItem;

            GenericMenu menu = new GenericMenu();

            if (item != null)
            {
                menu.AddItem(new GUIContent("删除"), false, () =>
                {
                    foreach (var item in GetSelection())
                    {
                        EffectTreeViewItem tree = FindItem(item, rootItem) as EffectTreeViewItem;
                        if (tree == null)
                        {
                            continue;
                        }

                        DeleteEffect(tree.EffectData.id);
                    }

                    AssetDatabase.SaveAssets();
                    Reload();
                });

                menu.AddItem(new GUIContent("复制"), false, () =>
                {
                    List<int> ints = new List<int>();

                    foreach (var item in GetSelection())
                    {
                        EffectTreeViewItem tree = FindItem(item, rootItem) as EffectTreeViewItem;
                        if (tree == null)
                        {
                            continue;
                        }

                        ints.Add(CopyEffect(tree.EffectData));
                    }

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    Reload();

                    SetSelection(ints);
                });

                menu.AddSeparator(string.Empty);
            }


            ShowCreateEffectMenu(menu);
        }

#if UNITY_6000_2_OR_NEWER
        /// <summary>沿用 TreeView 的行构建与搜索流程。</summary>
        protected override IList<TreeViewItem<int>> BuildRows(TreeViewItem<int> root)
#else
        /// <summary>沿用 TreeView 的行构建与搜索流程。</summary>
        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
#endif

        {
            return base.BuildRows(root);
        }

        /// <summary>创建指定效果数据类型，再交给实例入口登记为效果资产。</summary>
        private int CreateEffect(Type type)
        {

            EffectData effect = ScriptableObject.CreateInstance(type) as EffectData;

            return CreateEffect(effect);
        }

        /// <summary>创建并登记效果资产后赋予显示名与新 ID，由菜单调用方保存最终字段。</summary>
        private int CreateEffect(EffectData effect, string preName = null)
        {
            string path = "Assets/YLFrameworkData/Buff/Effects";

            EffectsDataController.CreateAsset(effect, path, true, preName);

            effect.effectName = effect.name;
            effect.id = GenerateId();
            EditorUtility.SetDirty(effect);
            return effect.id;
        }

        /// <summary>复制效果定义，并通过创建入口分配新名称与 ID。</summary>
        private int CopyEffect(EffectData effect)
        {
            EffectData data = GameObject.Instantiate(effect);
            return CreateEffect(data, data.effectName);
        }

        /// <summary>为基础效果与派生类型建立创建菜单，完成创建后保存并定位新行。</summary>
        private void ShowCreateEffectMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("创建Effect/基础效果"), false, () =>
            {
                int id = CreateEffect(typeof(EffectData));

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Reload();
                SetSelection(new int[] { id });
                FrameItem(id);
            });

            //  查找项目中所有继承自
            List<Type> effects = GetAllBuffTypes();

            List<string> temps = new List<string>();

            if (effects.Count > 0)
            {
                menu.AddSeparator("创建Effect/");
            }

            foreach (var item in effects)
            {
                string name = null;

                EffectName effectName = item.GetCustomAttribute<EffectName>();

                if (effectName != null && !temps.Contains(effectName.Name))
                {
                    name = string.Format("创建Effect/{0}", effectName.Name);
                    temps.Add(effectName.Name);
                }
                else
                {
                    name = string.Format("创建Effect/{0}", item.Name);
                    if (!string.IsNullOrEmpty(item.Namespace))
                    {
                        name = string.Format("{0}-{1}", name, item.Namespace);
                    }
                }


                GUIContent content = new GUIContent(name);

                menu.AddItem(content, false, () =>
                {
                    int id = CreateEffect(item);

                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    Reload();
                    SetSelection(new int[] { id });
                    FrameItem(id);
                });
            }


            menu.ShowAsContext();
        }

        /// <summary>先从项目设置撤销效果引用，再请求删除对应资产文件。</summary>
        private void DeleteEffect(int id)
        {
            EffectData data = BuffSystemSettings.Instance.GetEffectDataById(id);
            if (data == null)
            {
                return;
            }
            // 移除
            BuffSystemSettings.Instance.RemoveEffectData(data);

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(data));

        }

        /// <summary>扫描 EffectData 的派生脚本类型，不包含基础类型，也不过滤抽象类型。</summary>
        public static List<Type> GetAllBuffTypes()
        {
            List<Type> states = new List<Type>();

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

                if (type.IsSubclassOf(typeof(EffectData)))
                {
                    states.Add(type);
                }
            }

            return states;
        }


        /// <summary>按当前可见列逐格绘制效果行或分组行。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            for (int i = 0; i < args.GetNumVisibleColumns(); ++i)
            {
                CellGui(args.GetCellRect(i), args.item, args.GetColumn(i), ref args);
            }
        }

#if UNITY_6000_2_OR_NEWER

        /// <summary>绘制效果字段与业务分类菜单；分组节点只在首列显示分组名称。</summary>
        private void CellGui(Rect cellRect, TreeViewItem<int> item, int column, ref RowGUIArgs args)
#else
        /// <summary>绘制效果字段与业务分类菜单；分组节点只在首列显示分组名称。</summary>
        private void CellGui(Rect cellRect, TreeViewItem item, int column, ref RowGUIArgs args)
#endif

        {

            if (_styleLabel == null)
            {
                _styleLabel = GUI.skin.label;
                _styleLabel.richText = true;
            }

            EffectTreeViewItem effectItem = item as EffectTreeViewItem;

            if (effectItem != null)
            {
                switch (column)
                {
                    case 0:
                        cellRect.x += 30;
                        cellRect.width -= 30;
                        GUI.Label(cellRect, effectItem.EffectData.effectName, _styleLabel);
                        break;

                    case 1:
                        GUI.Label(cellRect, effectItem.EffectData.id.ToString(), _styleLabel);
                        break;

                    case 2:

                        if (!_typeContents.ContainsKey(effectItem.EffectData.id))
                        {
                            _typeContents.Add(effectItem.EffectData.id, new GUIContent());
                        }

                        _typeContents[effectItem.EffectData.id].text = BuffSystemSettings.Instance.GetType(effectItem.EffectData.type);
                        _typeContents[effectItem.EffectData.id].tooltip = BuffSystemSettings.Instance.GetType(effectItem.EffectData.type, false);

                        if (_styleDropdown == null)
                        {
                            _styleDropdown = new GUIStyle("DropDown");
                            _styleDropdown.richText = true;
                        }

                        if (EditorGUI.DropdownButton(cellRect, _typeContents[effectItem.EffectData.id], FocusType.Passive, _styleDropdown))
                        {
                            GenericMenu menu = new GenericMenu();

                            menu.AddItem(new GUIContent("0(空)"), effectItem.EffectData.type == 0, () =>
                            {
                                effectItem.EffectData.type = 0;
                                EditorUtility.SetDirty(effectItem.EffectData);
                                AssetDatabase.SaveAssets();

                                Reload();
                            });

                            if (BuffSystemSettings.Instance.AllEffectType.Count > 0)
                            {
                                menu.AddSeparator(string.Empty);
                            }

                            foreach (var type in BuffSystemSettings.Instance.AllEffectType)
                            {
                                GUIContent content = new GUIContent(BuffSystemSettings.Instance.GetType(type.type, false));

                                menu.AddItem(content, effectItem.EffectData.type == type.type, () =>
                                {
                                    effectItem.EffectData.type = type.type;
                                    EditorUtility.SetDirty(effectItem.EffectData);
                                    AssetDatabase.SaveAssets();

                                    Reload();
                                });
                            }

                            if (BuffSystemSettings.Instance.AllEffectType.Count > 0)
                            {
                                menu.AddSeparator(string.Empty);
                            }

                            menu.AddItem(new GUIContent("编辑"), false, () =>
                            {
                                OpenProjectSettings("YLFramework/Buff");
                            });

                            menu.ShowAsContext();
                        }

                        break;

                    case 3:

                        if (!_desContents.ContainsKey(effectItem.EffectData.id))
                        {
                            _desContents.Add(effectItem.EffectData.id, new GUIContent());
                        }

                        if (string.IsNullOrEmpty(effectItem.EffectData.description))
                        {
                            return;
                        }

                        _desContents[effectItem.EffectData.id].text = effectItem.EffectData.description.Replace('\n', '\0');
                        _desContents[effectItem.EffectData.id].tooltip = effectItem.EffectData.description;

                        GUI.Label(cellRect, _desContents[effectItem.EffectData.id], _styleLabel);
                        break;
                }
            }
            else
            {
                if (column == 0)
                {
                    cellRect.x += 20;
                    cellRect.width -= 20;

                    if (!_displayContents.ContainsKey(args.item.GetHashCode()))
                    {
                        _displayContents.Add(args.item.GetHashCode(), new GUIContent());
                    }

                    _displayContents[args.item.GetHashCode()].text = args.item.displayName;
                    _displayContents[args.item.GetHashCode()].tooltip = args.item.displayName;

                    GUI.Label(cellRect, _displayContents[args.item.GetHashCode()], _styleLabel);
                }
            }



        }

        /// <summary>打开指定的项目设置页。</summary>
        internal static void OpenProjectSettings(string path)
        {
            SettingsService.OpenProjectSettings(path);
        }

        /// <summary>记录点击效果的分组与父节点，并处理可能存在的 ID 列表拖拽载荷。</summary>
        protected override void SingleClickedItem(int id)
        {
            base.SingleClickedItem(id);

            EffectTreeViewItem treeViewItem = FindItem(id, rootItem) as EffectTreeViewItem;

            if (treeViewItem == null)
            {
                return;
            }

            _currentDragGroup = treeViewItem.GroupEffectData;

            _dragItemParent = treeViewItem.parent;

            // 若载荷是 ID 列表，则剔除不同父节点的项；当前 SetupDragAndDrop 使用 EffectDrag，不进入此分支。
            IList<int> draggedItemIDs = DragAndDrop.GetGenericData(EffectDraggedDataKey) as IList<int>;

            if (draggedItemIDs != null)
            {
                for (int i = draggedItemIDs.Count - 1; i >= 0; i--)
                {
#if UNITY_6000_2_OR_NEWER
                    TreeViewItem<int> item = FindItem(draggedItemIDs[i], rootItem);
#else
                    TreeViewItem item = FindItem(draggedItemIDs[i], rootItem);
#endif
                    if (item.parent != _dragItemParent)
                    {
                        draggedItemIDs.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>绘制树后限频检查失效的效果登记。</summary>
        public override void OnGUI(Rect rect)
        {
            base.OnGUI(rect);
            RefreshList();
        }

        /// <summary>限频清除登记列表的空引用，实际移除后清缓存、保存设置并重建树。</summary>
        private void RefreshList()
        {
            if (Time.realtimeSinceStartup - _lastRefreshTime < 1)
            {
                return;
            }

            _lastRefreshTime = Time.realtimeSinceStartup;

            bool remove = false;

            for (int i = BuffSystemSettings.Instance.AllEffectDatas.Count - 1; i >= 0; i--)
            {
                EffectData effectData = BuffSystemSettings.Instance.AllEffectDatas[i];
                if (effectData != null)
                {
                    continue;
                }

                remove = true;
                BuffSystemSettings.Instance.AllEffectDatas.RemoveAt(i);
            }

            if (remove)
            {
                BuffSystemSettings.Instance.ClearCache();
                BuffSystemSettings.Save();
                Reload();
            }

        }


        /// <summary>按当前树节点 ID 返回效果定义；分组或不存在的节点返回 null。</summary>
        public EffectData GetEffectData(int id)
        {
            EffectTreeViewItem item = FindItem(id, rootItem) as EffectTreeViewItem;
            if (item == null)
            {
                return null;
            }

            return item.EffectData;
        }

        #region 拖拽功能实现

        private float _startDragTime;

        private int _dragCount;
#if UNITY_6000_2_OR_NEWER
        private TreeViewItem<int> _dragItemParent; // 拖拽的parent
#else
        private TreeViewItem _dragItemParent; // 拖拽的parent
#endif
        private GroupEffectData _currentDragGroup;
        /// <summary>保留拖拽起点树、类型分组、槽位与快照，以校验同组重排的目标。</summary>
        private sealed class EffectDrag
        {
            internal EffectListTree Owner;
            internal GroupEffectData Group;
            internal int[] Indices;
            internal EffectData[] Snapshot;
        }

        /// <summary>只允许拖拽已选中的效果数据行，且所有拖拽项必须属于同一分组。</summary>
        protected override bool CanStartDrag(CanStartDragArgs args)
        {
            if (!(args.draggedItem is EffectTreeViewItem first) || !GetSelection().Contains(first.id))
            {
                return false;
            }

            return args.draggedItemIDs != null && args.draggedItemIDs.All(id => FindItem(id, rootItem) is EffectTreeViewItem item && ReferenceEquals(item.GroupEffectData, first.GroupEffectData));
        }

        /// <summary>核对起点树、分组与快照后执行组内移动，并记录 Undo、保存设置。</summary>
        protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
        {
            var drag = DragAndDrop.GetGenericData(EffectDraggedDataKey) as EffectDrag;
            if (drag == null || !ReferenceEquals(drag.Owner, this) || args.parentItem != _dragItemParent ||
                args.insertAtIndex < 0 || args.insertAtIndex > drag.Snapshot.Length ||
                !drag.Group.effectDatas.SequenceEqual(drag.Snapshot))
            {
                return DragAndDropVisualMode.Rejected;
            }

            if (!args.performDrop)
            {
                return DragAndDropVisualMode.Move;
            }

            var list = new List<EffectData>(drag.Snapshot);
            var moved = drag.Indices.Select(index => list[index]).ToArray();
            for (var i = drag.Indices.Length - 1; i >= 0; i--)
            {
                list.RemoveAt(drag.Indices[i]);
            }

            list.InsertRange(args.insertAtIndex - drag.Indices.Count(index => index < args.insertAtIndex), moved);
            if (!list.SequenceEqual(drag.Snapshot))
            {
                Undo.RecordObject(BuffSystemSettings.Instance, "Reorder Buff effects");
                // 只替换总表中属于本组的槽位；否则后续按类型重建分组时会丢失这次拖拽顺序。
                var all = BuffSystemSettings.Instance.AllEffectDatas;
                var next = 0;
                for (var i = 0; i < all.Count; i++)
                {
                    if (drag.Snapshot.Contains(all[i]))
                    {
                        all[i] = list[next++];
                    }
                }

                drag.Group.effectDatas.Clear();
                drag.Group.effectDatas.AddRange(list);
                BuffSystemSettings.Save();
                Reload();
            }
            Dispose();
            return DragAndDropVisualMode.Move;
        }

        /// <summary>把同组所选行转换为升序槽位快照，并建立本树拥有的拖拽载荷。</summary>
        protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
        {
            var items = args.draggedItemIDs.Select(id => FindItem(id, rootItem) as EffectTreeViewItem).ToArray();
            if (items.Length == 0 || items.Any(item => item == null))
            {
                return;
            }

            _currentDragGroup = items[0].GroupEffectData;
            _dragItemParent = items[0].parent;
            if (items.Any(item => !ReferenceEquals(item.GroupEffectData, _currentDragGroup)))
            {
                return;
            }

            var indices = items.Select(item => _currentDragGroup.effectDatas.IndexOf(item.EffectData)).Distinct().OrderBy(index => index).ToArray();
            if (indices.Any(index => index < 0))
            {
                return;
            }

            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData(EffectDraggedDataKey, new EffectDrag { Owner = this, Group = _currentDragGroup, Indices = indices, Snapshot = _currentDragGroup.effectDatas.ToArray() });
            DragAndDrop.StartDrag("Reorder Buff effects");
        }

        /// <summary>仅清除本树拥有的拖拽载荷，不清除其他树的拖拽，也不删除效果资产。</summary>
        public void Dispose()
        {
            if (DragAndDrop.GetGenericData(EffectDraggedDataKey) is EffectDrag drag && ReferenceEquals(drag.Owner, this))
            {
                DragAndDrop.SetGenericData(EffectDraggedDataKey, null);
            }
        }


        /// <summary>在当前拖拽父节点中查询行索引，未找到时返回 -1。</summary>
        private int GetItemIndex(int id)
        {
#if UNITY_6000_2_OR_NEWER
            TreeViewItem<int> treeViewItem = FindItem(id, _dragItemParent);
#else
            TreeViewItem treeViewItem = FindItem(id, _dragItemParent);
#endif

            if (treeViewItem == null)
            {
                return -1;
            }

            return _dragItemParent.children.IndexOf(treeViewItem);
        }

        #endregion

    }

}
