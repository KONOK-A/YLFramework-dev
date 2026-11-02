using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

#if UNITY_6000_2_OR_NEWER
using TreeView = UnityEditor.IMGUI.Controls.TreeView<int>;
using TreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
using TreeViewItem = UnityEditor.IMGUI.Controls.TreeViewItem<int>;
#endif

namespace YLFramework.Editor.Common
{
    /// <summary>
    /// 构建及重排平面列表的TreeView，按索引移动以保留空项和重复引用。
    /// 窗口停用时须Dispose；getter应返回owner当前的序列化列表，以跟随Undo替换。
    /// </summary>
    /// <typeparam name="T">按引用身份维护的列表元素类型，允许空项和重复引用。</typeparam>
    /// <remarks>实际重排记录 owner 的 Undo 并标脏；资产保存仍由宿主编辑器负责。</remarks>
    public class ReorderableTreeView<T> : TreeView, IDisposable where T : class
    {
        private static readonly string _dragKey = typeof(ReorderableTreeView<T>).FullName;
        private readonly ScriptableObject _owner;
        private readonly Func<IList<T>> _getData;
        private T[] _rowData;
        private bool _isDisposed;

        /// <summary>一次内部列表拖拽的上下文，不代表 Unity 资产或文件拖放。</summary>
        private sealed class DragData
        {
            /// <summary>独占本次拖拽上下文的树实例。</summary>
            public ReorderableTreeView<T> Source;
            /// <summary>按源顺序排列的实际列表索引。</summary>
            public int[] Indices;
            /// <summary>起拖时的元素引用快照，不接管元素的销毁责任。</summary>
            public T[] Snapshot;
        }

        /// <summary>以owner当前的序列化列表建立平面树，创建后调用Reload。</summary>
        /// <param name="owner">实际序列化该列表、接收 Undo 记录的对象。</param>
        /// <param name="getData">每次返回 owner 当前列表的函数，不能返回 null。</param>
        /// <param name="state">由宿主保存的树展开、选择等界面状态。</param>
        public ReorderableTreeView(ScriptableObject owner, Func<IList<T>> getData, TreeViewState state)
            : this(owner, getData, state, null)
        {
        }

        /// <summary>保留原作者工具的多列表头入口；数据与Undo责任仍归owner。</summary>
        /// <param name="owner">实际序列化该列表、接收 Undo 记录的对象。</param>
        /// <param name="getData">取得 owner 当前列表的函数，不能返回 null。</param>
        /// <param name="state">由宿主保存的树界面状态。</param>
        /// <param name="header">可选多列表头，null 表示不使用。</param>
        public ReorderableTreeView(ScriptableObject owner, Func<IList<T>> getData, TreeViewState state,
            MultiColumnHeader header) : base(state, header)
        {
            if (!owner)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            this._owner = owner;
            this._getData = getData ?? throw new ArgumentNullException(nameof(getData));
            if (getData() == null)
            {
                throw new ArgumentException("列表getter不能返回null。", nameof(getData));
            }

            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private IList<T> Data => _getData() ?? throw new InvalidOperationException("列表getter返回了null。");

        /// <summary>刷新与当前列表不一致的行再绘制；owner 已销毁时释放本树，释放后不再绘制。</summary>
        /// <param name="rect">宿主分配的绘制区域。</param>
        public override void OnGUI(Rect rect)
        {
            if (_isDisposed)
            {
                return;
            }

            if (!_owner)
            {
                Dispose();
                return;
            }
            RefreshChangedRows();
            base.OnGUI(rect);
        }

        /// <summary>经派生类构建平面行，并保存本次构建所对应的元素引用快照。</summary>
        /// <returns>深度为 -1 的隐藏根节点。</returns>
        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem(0, -1) { children = new List<TreeViewItem>() };
            var datas = Data;
            OnBuildRoot(root, datas);
            _rowData = datas.ToArray();
            return root;
        }

        /// <summary>默认每个槽位一行；派生类可沿用原编辑器的行构建和RowGUI。</summary>
        /// <param name="root">接收行节点的隐藏根。</param>
        /// <param name="datas">owner 当前的原始列表，不是独立副本。</param>
        protected virtual void OnBuildRoot(TreeViewItem root, IList<T> datas)
        {
            for (var i = 0; i < datas.Count; i++)
            {
                var item = datas[i];
                var text = item is UnityEngine.Object obj ? (obj ? obj.name : "(Missing)") : item?.ToString() ?? "(Empty)";
                root.AddChild(new TreeViewItem(i + 1, 0, text));
            }
        }

        /// <summary>只允许深度为零的列表行参加多选。</summary>
        /// <param name="item">待判断节点。</param>
        /// <returns>节点是否属于列表行层级。</returns>
        protected override bool CanMultiSelect(TreeViewItem item) => item.depth == 0;

        /// <summary>仅允许从本树当前已选中的有效行发起可写列表的拖拽，刷新过期行的当次请求会被拒绝。</summary>
        /// <param name="args">Unity 提供的起拖行与选择集合。</param>
        /// <returns>当前行身份及全部真实索引均有效时为 true。</returns>
        protected override bool CanStartDrag(CanStartDragArgs args)
        {
            if (_isDisposed || !_owner || RefreshChangedRows())
            {
                return false;
            }

            return !Data.IsReadOnly && args.draggedItem != null &&
                args.draggedItem.depth == 0 && args.draggedItemIDs != null &&
                ReferenceEquals(FindItem(args.draggedItem.id, rootItem), args.draggedItem) &&
                GetSelection().Contains(args.draggedItem.id) && args.draggedItemIDs.Contains(args.draggedItem.id) &&
                GetIndices(args.draggedItemIDs) != null;
        }

        /// <summary>为当前选中行准备独占上下文，成功后启动编辑器拖拽。</summary>
        /// <param name="args">Unity 提供的待拖拽行 ID。</param>
        protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
        {
            if (PrepareDrag(args.draggedItemIDs))
            {
                DragAndDrop.StartDrag("Reorder items");
            }
        }

        /// <summary>将有效行 ID 映射为有序索引，并保存来源实例与当前列表快照。</summary>
        private bool PrepareDrag(IList<int> ids)
        {
            if (_isDisposed || !_owner || RefreshChangedRows() || Data.IsReadOnly)
            {
                return false;
            }

            var indices = GetIndices(ids);
            if (indices == null)
            {
                return false;
            }

            var data = new DragData { Source = this, Indices = indices, Snapshot = Data.ToArray() };
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData(_dragKey, data);
            return true;
        }

        /// <summary>只接受本树发起且列表快照未变化的根层插入；悬停仅预览，确认放置才执行重排。</summary>
        /// <param name="args">目标父节点、插入位置及是否提交放置。</param>
        /// <returns>有效插入为 Move，外来、过期或不可用的拖拽为 Rejected。</returns>
        protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
        {
            if (_isDisposed || !_owner || args.parentItem != rootItem || rootItem.children == null ||
                args.insertAtIndex < 0 || args.insertAtIndex > rootItem.children.Count ||
                DragAndDrop.objectReferences.Length != 0 || (DragAndDrop.paths != null && DragAndDrop.paths.Length != 0))
            {
                return DragAndDropVisualMode.Rejected;
            }

            var drag = DragAndDrop.GetGenericData(_dragKey) as DragData;
            if (drag == null || !ReferenceEquals(drag.Source, this))
            {
                return DragAndDropVisualMode.Rejected;
            }

            var datas = Data;
            // 同一对象可出现在多个槽位，故按整列引用和长度核对，不能只检查待移动对象是否仍存在。
            if (datas.IsReadOnly || datas.Count != drag.Snapshot.Length ||
                Enumerable.Range(0, datas.Count).Any(i => !ReferenceEquals(datas[i], drag.Snapshot[i])))
            {
                return DragAndDropVisualMode.Rejected;
            }

            var insertion = args.insertAtIndex == rootItem.children.Count ? datas.Count :
                GetItemIndex(rootItem.children[args.insertAtIndex].id);
            if (insertion < 0 || insertion > datas.Count)
            {
                return DragAndDropVisualMode.Rejected;
            }

            if (!args.performDrop)
            {
                return DragAndDropVisualMode.Move;
            }

            ClearOwnedDrag();
            var moves = drag.Indices.Select(i => datas[i]).ToArray();
            var reordered = new List<T>(datas);
            for (var i = drag.Indices.Length - 1; i >= 0; i--)
            {
                reordered.RemoveAt(drag.Indices[i]);
            }

            // 插入位置来自移除前的列表，先减去其前方已抽走的槽位数，才能落到原定边界。
            var destination = insertion - drag.Indices.Count(i => i < insertion);
            reordered.InsertRange(destination, moves);
            if (!Enumerable.Range(0, datas.Count).Any(i => !ReferenceEquals(datas[i], reordered[i])))
            {
                return DragAndDropVisualMode.Move;
            }

            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(_owner, "Reorder list");
            datas.Clear();
            foreach (var item in reordered)
            {
                datas.Add(item);
            }

            EditorUtility.SetDirty(_owner);
            Reload();
            var movedIndices = new HashSet<int>(Enumerable.Range(destination, moves.Length));
            SetSelection(rootItem.children.Where(item => movedIndices.Contains(GetItemIndex(item.id))).Select(item => item.id).ToArray());
            OnDragEnd();
            return DragAndDropVisualMode.Move;
        }

        /// <summary>将行 ID 映射为去重并按原列表顺序排列的索引，任一索引失效则整体拒绝。</summary>
        private int[] GetIndices(IList<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return null;
            }

            var indices = ids.Select(GetItemIndex).Distinct().OrderBy(i => i).ToArray();
            return indices.Any(i => i < 0 || i >= Data.Count) ? null : indices;
        }

        /// <summary>默认行与列表槽位一一对应；筛选/分组行须覆盖此方法返回真实列表索引。</summary>
        /// <param name="id">当前树的行 ID。</param>
        /// <returns>对应的原始列表索引；节点不存在时为 -1。</returns>
        protected virtual int GetItemIndex(int id)
        {
            var item = FindItem(id, rootItem);
            return item == null || rootItem.children == null ? -1 : rootItem.children.IndexOf(item);
        }

        /// <summary>沿用来源的刷新扩展点，但不再隐式删除空项、去重或改脏资产。</summary>
        protected virtual void RefreshDatas() => Reload();

        /// <summary>比较行快照与当前列表，变化时清除本树待完成的拖拽并刷新行。</summary>
        private bool RefreshChangedRows()
        {
            var datas = Data;
            var changed = _rowData == null || _rowData.Length != datas.Count;
            for (var i = 0; !changed && i < datas.Count; i++)
            {
                changed = !ReferenceEquals(_rowData[i], datas[i]);
            }

            if (!changed)
            {
                return false;
            }

            ClearOwnedDrag();
            RefreshDatas();
            return true;
        }

        /// <summary>一次实际重排完成后通知派生编辑器；悬停与原位放回不通知。</summary>
        protected virtual void OnDragEnd()
        {
        }

        /// <summary>Undo/Redo 后清除旧拖拽并重建行；owner 已销毁时释放订阅。</summary>
        private void OnUndoRedo()
        {
            if (_isDisposed)
            {
                return;
            }

            if (!_owner)
            {
                Dispose();
                return;
            }
            ClearOwnedDrag();
            RefreshDatas();
        }

        /// <summary>仅清除来源为本树的全局拖拽数据，不干扰同类型的其他窗口。</summary>
        private void ClearOwnedDrag()
        {
            if (DragAndDrop.GetGenericData(_dragKey) is DragData drag && ReferenceEquals(drag.Source, this))
            {
                DragAndDrop.SetGenericData(_dragKey, null);
            }
        }

        /// <summary>窗口停用时归还本实例的Undo订阅和拖拽上下文，不清除其他窗口的拖拽。</summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Undo.undoRedoPerformed -= OnUndoRedo;
            ClearOwnedDrag();
            _rowData = null;
        }
    }
}
