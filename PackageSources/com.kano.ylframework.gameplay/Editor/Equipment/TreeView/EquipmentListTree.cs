using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Editor.Common;
using YLFramework.Equipment;
#if UNITY_6000_2_OR_NEWER
using TreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
using TreeViewItem = UnityEditor.IMGUI.Controls.TreeViewItem<int>;
#endif

namespace YLFramework.Editor.Equipment
{
    /// <summary>展示装备定义并映射树行与真实列表槽位，提供创建、复制、删除和重排入口。</summary>
    /// <remarks>拖拽重排沿用基类的 Undo 与标脏流程，不在本树中立即保存配表文件。</remarks>
    public sealed class EquipmentListTree : ReorderableTreeView<EquipmentConfigData>
    {
        /// <summary>此树维护的共享配表资产。</summary>
        public EquipmentsConfig EquipmentsConfig
        {
            get;
        }
        private readonly Dictionary<int, int> _indices = new();
        private readonly Dictionary<int, EquipmentConfigData> _targets = new();
        private int[] _rowIds = Array.Empty<int>();
        private int[] _businessIds = Array.Empty<int>();
        private string[] _names = Array.Empty<string>();
        private bool[] _alive = Array.Empty<bool>();
        /// <summary>绑定配表当前列表并建立初始行；窗口停用或切表时须 Dispose 以解除基类订阅。</summary>
        public EquipmentListTree(TreeViewState state, EquipmentsConfig root) : base(root, () => root.Equipments, state)
        {
            EquipmentsConfig = root;
            rowHeight = 28;
            showBorder = true;
            Reload();
        }

        /// <summary>按每个列表槽位建立树行，同时缓存其索引、定义引用与显示数据。</summary>
        protected override void OnBuildRoot(TreeViewItem root, IList<EquipmentConfigData> datas)
        {
            _indices.Clear();
            _targets.Clear();
            _rowIds = new int[datas.Count];
            _businessIds = new int[datas.Count];
            _names = new string[datas.Count];
            _alive = new bool[datas.Count];
            // 行标识不用可编辑的业务 ID；空槽或重复引用另分配保留值，使这些槽位仍能独立显示和重排。
            var reserved = new HashSet<int>(datas.Where(data => data).Select(data => data.GetInstanceID()));
            var fallback = int.MinValue;
            for (var i = 0; i < datas.Count; i++)
            {
                var data = datas[i];
                var id = data ? data.GetInstanceID() : 0;
                if (id == 0 || _indices.ContainsKey(id))
                {
                    while (reserved.Contains(fallback) || _indices.ContainsKey(fallback))
                    {
                        fallback++;
                    }

                    id = fallback++;
                }
                _indices[id] = i;
                _targets[id] = data;
                _rowIds[i] = id;
                _alive[i] = data;
                _businessIds[i] = data ? data.id : 0;
                _names[i] = data ? data.equipmentName : null;
                root.AddChild(new TreeViewItem(id, 0, data ? data.id + "  " + data.equipmentName : "(Missing)"));
            }
        }
        /// <summary>在绘制前检查行数、存活状态、业务 ID 与名称变化，必要时重建显示映射。</summary>
        public override void OnGUI(Rect rect)
        {
            var datas = EquipmentsConfig.Equipments;
            var changed = datas.Count != _rowIds.Length;
            for (var i = 0; !changed && i < datas.Count; i++)
            {
                changed = _alive[i] != (bool)datas[i] || _businessIds[i] != (datas[i] ? datas[i].id : 0) || _names[i] != (datas[i] ? datas[i].equipmentName : null);
            }

            if (changed)
            {
                Reload();
            }

            base.OnGUI(rect);
        }
        /// <summary>将树行标识映射为真实列表槽位，未知行返回 -1。</summary>
        protected override int GetItemIndex(int id) => _indices.TryGetValue(id, out var index) ? index : -1;
        /// <summary>绘制装备图标、显示文本和描述提示，失效条目沿用基类行展示。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {
            var data = EquipmentsConfig.Equipments[GetItemIndex(args.item.id)];
            if (!data)
            {
                base.RowGUI(args);
                return;
            }
            var icon = data.icon == null ? GUIContent.none : EditorAssetTool.GetTextureGuiContent(data.icon.projectName, data.icon.iconName);
            GUI.Label(new Rect(args.rowRect.x, args.rowRect.y, 26, 26), icon);
            GUI.Label(new Rect(args.rowRect.x + 30, args.rowRect.y, args.rowRect.width - 30, args.rowRect.height), new GUIContent(args.item.displayName, data.description));
        }
        /// <summary>先使配表查询缓存失效，再沿用基类的树重建流程。</summary>
        protected override void RefreshDatas()
        {
            EquipmentsConfig.ClearCache();
            base.RefreshDatas();
        }
        /// <summary>实际重排结束后使配表 ID 查询缓存失效。</summary>
        protected override void OnDragEnd() => EquipmentsConfig.ClearCache();

        /// <summary>在空白区显示定义创建菜单。</summary>
        protected override void ContextClicked() => ShowMenu(false);
        /// <summary>在行上显示带当前选择复制与删除操作的菜单。</summary>
        protected override void ContextClickedItem(int id) => ShowMenu(true);
        /// <summary>按当前选择快照建立复制、删除及符合配表类型限制的创建菜单。</summary>
        private void ShowMenu(bool selected)
        {
            var menu = new GenericMenu();
            if (selected)
            {
                var rows = GetSelection().Where(_targets.ContainsKey).Select(id => _targets[id]).Where(d => d).ToArray();
                menu.AddItem(new GUIContent("复制"), false, () =>
                {
                    foreach (var row in rows)
                    {
                        EquipmentsConfigController.CopyEquipmentConfigData(row, EquipmentsConfig);
                    }
                    Reload();
                });
                menu.AddItem(new GUIContent("删除"), false, () =>
                {
                    foreach (var row in rows)
                    {
                        EquipmentsConfigController.DeleteEquipmentConfigData(row, EquipmentsConfig);
                    }
                    Reload();
                });
                menu.AddSeparator("");
            }
            foreach (var type in GetAllEquipmentConfigTypes(EquipmentsConfig.GetDataType()))
            {
                var selectedType = type;
                menu.AddItem(new GUIContent("创建装备/" + selectedType.FullName), false, () =>
                {
                    var id = EquipmentsConfigController.CreateEquipmentConfigData(selectedType, EquipmentsConfig);
                    Reload();
                    SelectEquipment(id);
                });
            }
            menu.ShowAsContext();
        }
        /// <summary>取得符合指定配置基类的非抽象、非开放泛型类型，去重后按类型全名排序。</summary>
        public static List<Type> GetAllEquipmentConfigTypes(Type parent) => TypeCache.GetTypesDerivedFrom<EquipmentConfigData>().Append(parent)
            .Where(t => parent.IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters).Distinct().OrderBy(t => t.FullName).ToList();
        /// <summary>按业务 ID 选择首个匹配槽位，未找到时清空选择。</summary>
        public void SelectEquipment(int id)
        {
            var index = EquipmentsConfig.Equipments.FindIndex(d => d && d.id == id);
            SetSelection(index < 0 ? Array.Empty<int>() : new[] { _rowIds[index] });
        }
        /// <summary>首个所选行中仍有效且仍被配表引用的定义；没有这样的首行时返回 null。</summary>
        public EquipmentConfigData Selected => GetSelection().Count != 0 && _targets.TryGetValue(GetSelection()[0], out var data) && data && EquipmentsConfig.Equipments.Any(item => ReferenceEquals(item, data)) ? data : null;
    }
}
