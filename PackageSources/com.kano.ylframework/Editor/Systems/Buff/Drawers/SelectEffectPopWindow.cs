using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR

using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Buff;


namespace YLFramework.Editor.Buff
{
#if UNITY_6000_2_OR_NEWER
    /// <summary>把效果定义关联到选择器行，并提供 ID、名称与描述的文本匹配。</summary>
    public class SelectEffectDataTreeViewItem : TreeViewItem<int>
#else
    /// <summary>把效果定义关联到选择器行，并提供 ID、名称与描述的文本匹配。</summary>
    public class SelectEffectDataTreeViewItem : TreeViewItem
#endif

    {


        /// <summary>本行可供选择的效果定义资产。</summary>
        public EffectData EffectData;


        /// <summary>以效果 ID 与显示名构造平面数据行。</summary>
        public SelectEffectDataTreeViewItem(EffectData effectData) : base(effectData.id, 0, effectData.effectName)
        {
            this.EffectData = effectData;
        }


        /// <summary>按 ID 文本、名称或描述执行区分大小写的子串匹配。</summary>
        public bool DoesItemMatchSearch(string search)
        {

            if (EffectData.id.ToString().Contains(search))
            {
                return true;
            }

            if (EffectData.effectName.Contains(search))
            {
                return true;
            }

            if (EffectData.description.Contains(search))
            {
                return true;
            }

            return false;

        }

    }

#if UNITY_6000_2_OR_NEWER
    /// <summary>在弹窗中搜索效果定义，单击后把资产引用写入目标序列化字段。</summary>
    public class SelectEffectDataListTree : TreeView<int>
#else
    /// <summary>在弹窗中搜索效果定义，单击后把资产引用写入目标序列化字段。</summary>
    public class SelectEffectDataListTree : TreeView
#endif

    {

        // Fix编码
        #region 字段

        private SelectEffectPopWindow _editorWindow;

        private SerializedProperty _property;

        private Dictionary<int, GUIContent> _contents = new Dictionary<int, GUIContent>();

        private List<int> _selections = new List<int>();

        #endregion


        #region 重写方法


        /// <summary>创建效果选择器的默认多列表头状态。</summary>
        internal static MultiColumnHeaderState CreateDefaultMultiColumnHeaderState()
        {
            MultiColumnHeaderState state = new MultiColumnHeaderState(GetColumns());

            return state;
        }
        /// <summary>配置选择器五列的名称、宽度和列头排序标记。</summary>
        private static MultiColumnHeaderState.Column[] GetColumns()
        {
            var retVal = new MultiColumnHeaderState.Column[] {
                new MultiColumnHeaderState.Column(),
                new MultiColumnHeaderState.Column(),
                new MultiColumnHeaderState.Column(),
                new MultiColumnHeaderState.Column(),
                new MultiColumnHeaderState.Column()
            };
            retVal[0].headerContent = new GUIContent("ID", "ID");
            retVal[0].minWidth = 50;
            retVal[0].width = 60;
            retVal[0].maxWidth = 300;
            retVal[0].headerTextAlignment = TextAlignment.Left;
            retVal[0].canSort = true;
            retVal[0].autoResize = true;


            retVal[1].headerContent = new GUIContent("名称", "名称");
            retVal[1].minWidth = 30;
            retVal[1].width = 100;
            retVal[1].maxWidth = 300;
            retVal[1].headerTextAlignment = TextAlignment.Left;
            retVal[1].canSort = true;
            retVal[1].autoResize = true;


            retVal[2].headerContent = new GUIContent("类型", "类型,可根据类型查询效果!");
            retVal[2].minWidth = 30;
            retVal[2].width = 100;
            retVal[2].maxWidth = 300;
            retVal[2].headerTextAlignment = TextAlignment.Left;
            retVal[2].canSort = true;
            retVal[2].autoResize = true;


            retVal[3].headerContent = new GUIContent("效果分类", "效果分类!");
            retVal[3].minWidth = 30;
            retVal[3].width = 150;
            retVal[3].maxWidth = 1000;
            retVal[3].headerTextAlignment = TextAlignment.Left;
            retVal[3].canSort = false;
            retVal[3].autoResize = true;

            retVal[4].headerContent = new GUIContent("描述", "描述!");
            retVal[4].minWidth = 30;
            retVal[4].width = 100;
            retVal[4].maxWidth = 1000;
            retVal[4].headerTextAlignment = TextAlignment.Left;
            retVal[4].canSort = false;
            retVal[4].autoResize = true;



            return retVal;
        }

#if UNITY_6000_2_OR_NEWER
        /// <summary>仅对效果数据行执行其 ID、名称与描述匹配。</summary>
        protected override bool DoesItemMatchSearch(TreeViewItem<int> item, string search)
#else
        /// <summary>仅对效果数据行执行其 ID、名称与描述匹配。</summary>
        protected override bool DoesItemMatchSearch(TreeViewItem item, string search)
#endif

        {
            SelectEffectDataTreeViewItem effect = item as SelectEffectDataTreeViewItem;
            if (effect == null)
            {
                return false;
            }

            return effect.DoesItemMatchSearch(search);
        }

#if UNITY_6000_2_OR_NEWER
        /// <summary>按项目设置的效果列表建立平面树，跳过空引用。</summary>
        protected override TreeViewItem<int> BuildRoot()
#else
        /// <summary>按项目设置的效果列表建立平面树，跳过空引用。</summary>
        protected override TreeViewItem BuildRoot()
#endif

        {

#if UNITY_6000_2_OR_NEWER
            TreeViewItem<int> root = new TreeViewItem<int>(-1, -1);
#else
            TreeViewItem root = new TreeViewItem(-1, -1);
#endif

            List<EffectData> effects = BuffSystemSettings.Instance.AllEffectDatas;

            foreach (var item in effects)
            {
                if (item == null)
                {
                    continue;
                }

                root.AddChild(new SelectEffectDataTreeViewItem(item));
            }

            return root;
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

        /// <summary>写入所点效果的共享资产引用，应用并保存后关闭弹窗，不复制效果资产。</summary>
        protected override void SingleClickedItem(int id)
        {
            base.SingleClickedItem(id);

            SelectEffectDataTreeViewItem item = FindItem(id, rootItem) as SelectEffectDataTreeViewItem;

            _property.objectReferenceValue = item.EffectData;
            _property.serializedObject.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            _editorWindow.Close();
        }

        /// <summary>让鼠标所在行成为当前选择，并绘制该行的所有可见列。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            //base.RowGUI(args);

            SelectEffectDataTreeViewItem item = args.item as SelectEffectDataTreeViewItem;
            if (item == null)
            {
                return;
            }

            if (args.rowRect.Contains(Event.current.mousePosition))
            {
                _selections.Clear();
                _selections.Add(item.id);
                this.SetSelection(_selections);
            }

            for (int i = 0; i < args.GetNumVisibleColumns(); ++i)
            {
                CellGui(args.GetCellRect(i), item, args.GetColumn(i), ref args);
            }
        }

        /// <summary>按列展示效果 ID、名称、业务类型、数据分类或描述。</summary>
        private void CellGui(Rect cellRect, SelectEffectDataTreeViewItem item, int column, ref RowGUIArgs args)
        {
            if (!_contents.ContainsKey(item.id))
            {
                _contents.Add(item.id, new GUIContent());
            }

            GUIContent content = _contents[item.id];


            switch (column)
            {
                case 0:

                    content.text = item.EffectData.id.ToString();
                    content.tooltip = content.text;

                    GUI.Label(cellRect, content);
                    break;

                case 1:

                    content.text = item.EffectData.effectName;
                    content.tooltip = content.text;

                    GUI.Label(cellRect, content);
                    break;
                case 2:

                    content.text = BuffSystemSettings.Instance.GetType(item.EffectData.type);
                    content.tooltip = content.text;

                    GUI.Label(cellRect, content);

                    break;

                case 3:

                    content.text = GetEffectData(item.EffectData);
                    content.tooltip = content.text;

                    GUI.Label(cellRect, content);

                    break;

                case 4:

                    content.text = item.EffectData.description.Replace('\n', '\0');
                    content.tooltip = content.text;

                    GUI.Label(cellRect, content);

                    break;

            }

        }


        /// <summary>优先使用 EffectName 显示效果数据类型，未标注时使用类型全名。</summary>
        private string GetEffectData(EffectData effect)
        {

            if (effect.GetType().GetCustomAttribute<EffectName>() != null)
            {
                return effect.GetType().GetCustomAttribute<EffectName>().Name;
            }

            return effect.GetType().FullName;
        }


        /// <summary>把选择变化交给 TreeView 基类处理，不在此提交效果引用。</summary>
        protected override void SelectionChanged(IList<int> selectedIds)
        {
            base.SelectionChanged(selectedIds);
        }
#if UNITY_6000_2_OR_NEWER
        /// <summary>限制效果选择器为单选，一次只替换一个字段引用。</summary>
        protected override bool CanMultiSelect(TreeViewItem<int> item)
#else
        /// <summary>限制效果选择器为单选，一次只替换一个字段引用。</summary>
        protected override bool CanMultiSelect(TreeViewItem item)
#endif

        {
            return false;
        }

        #endregion

        #region 方法

#if UNITY_6000_2_OR_NEWER
        /// <summary>绑定选择状态、目标字段、所属弹窗和多列表头。</summary>
        public SelectEffectDataListTree(TreeViewState<int> state, SerializedProperty property, SelectEffectPopWindow editorWindow, MultiColumnHeaderState multiColumnHeaderState) : base(state, new MultiColumnHeader(multiColumnHeaderState))
#else
        /// <summary>绑定选择状态、目标字段、所属弹窗和多列表头。</summary>
        public SelectEffectDataListTree(TreeViewState state, SerializedProperty property, SelectEffectPopWindow editorWindow, MultiColumnHeaderState multiColumnHeaderState) : base(state, new MultiColumnHeader(multiColumnHeaderState))
#endif

        {
            showBorder = true;
            showAlternatingRowBackgrounds = true;

            this._property = property;
            this._editorWindow = editorWindow;
        }


        #endregion
    }


    /// <summary>承载效果搜索列表，并在弹窗打开期间订阅编辑器更新以重绘。</summary>
    public class SelectEffectPopWindow : PopupWindowContent
    {


        private SerializedProperty _property;

        private SelectEffectDataListTree _listTree;
#if UNITY_6000_2_OR_NEWER
        private TreeViewState<int> _state;
#else
        private TreeViewState _state;
#endif

        private MultiColumnHeaderState _effectListMCHState;

        private string _searchStr;

        /// <summary>保留待写入效果引用的目标序列化属性。</summary>
        public SelectEffectPopWindow(SerializedProperty property)
        {
            this._property = property;
        }

        /// <summary>按需建立效果树，在搜索框下绘制效果列表。</summary>
        public override void OnGUI(Rect rect)
        {
            if (_state == null)
            {
#if UNITY_6000_2_OR_NEWER
                _state = new TreeViewState<int>();
#else
                _state = new TreeViewState();
#endif

            }


            if (_listTree == null)
            {

                var headerState = SelectEffectDataListTree.CreateDefaultMultiColumnHeaderState();// multiColumnTreeViewRect.width);
                if (MultiColumnHeaderState.CanOverwriteSerializedFields(_effectListMCHState, headerState))
                {
                    MultiColumnHeaderState.OverwriteSerializedFields(_effectListMCHState, headerState);
                }

                _effectListMCHState = headerState;

                _listTree = new SelectEffectDataListTree(_state, _property, this, _effectListMCHState);
                _listTree.Reload();
            }

            float h = rect.height;

            rect.height = 20;
            rect.y += 3;
            rect.x += 3;
            rect.width -= 6;
            _searchStr = EditorGUI.TextField(rect, string.Empty, _searchStr, "SearchTextField");

            rect.y += rect.height - 2;
            rect.height = h - 23;

            _listTree.OnGUI(rect);
            _listTree.searchString = _searchStr;
        }
        /// <summary>先撤销更新订阅，再关闭仍存在的弹窗。</summary>
        public void Close()
        {

            EditorApplication.update -= Update;

            if (editorWindow == null)
            {
                return;
            }

            editorWindow.Close();
        }


        /// <summary>弹窗打开时去重订阅编辑器更新。</summary>
        public override void OnOpen()
        {
            base.OnOpen();
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
        }

        /// <summary>弹窗关闭时撤销编辑器更新订阅。</summary>
        public override void OnClose()
        {
            base.OnClose();
            EditorApplication.update -= Update;
        }

        /// <summary>重绘弹窗，失去焦点后停止更新订阅，不在此主动关闭窗口。</summary>
        public void Update()
        {

            editorWindow.Repaint();

            if (EditorWindow.focusedWindow != editorWindow)
            {
                EditorApplication.update -= Update;
            }
        }

        /// <summary>返回效果选择弹窗的固定尺寸 500 x 200。</summary>
        public override Vector2 GetWindowSize()
        {
            return new Vector2(500, 200);
        }


    }

}

#endif
