using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YLFramework.Core
{
    /// <summary>让独立门禁挂载层代理目标控件的布局，并用内层坐标节点抵消占位带来的坐标变化。</summary>
    /// <remarks>外层向原父布局提供目标尺寸，内层只穿透布局；绑定或层级变化后停止旧一轮同步。</remarks>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    internal sealed class PanelInputGateLayout : UIBehaviour, ILayoutElement, ILayoutGroup, ILayoutIgnorer
    {
        private const DrivenTransformProperties RectProperties = DrivenTransformProperties.Anchors |
            DrivenTransformProperties.AnchoredPosition3D | DrivenTransformProperties.SizeDelta;
        private static readonly PropertyInfo _drivenProperties = typeof(RectTransform).GetProperty("drivenProperties",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private readonly List<Component> _ignorers = new List<Component>();
        private readonly List<Component> _elements = new List<Component>();
        private RectTransform _target;
        private RectTransform _slot;
        private RectTransform _frame;
        private PanelInputGateLayout _coordinates;
        private PanelInputGate _binding;
        private Transform _initialParent;
        private Transform _synchronizationParent;
        private ulong _synchronizationBinding;
        private bool _isInitialized;
        private bool _isPassThrough;
        private bool _wasAttached;
        private bool _isSynchronizing;
        private bool _isRetiring;
        private bool _previousIgnore = true;

        /// <summary>初始化外层占位，要求内层已绑定同一目标，并确认可只读取得布局驱动字段。</summary>
        internal void Initialize(RectTransform target, RectTransform frame)
        {
            if (_drivenProperties == null || _drivenProperties.PropertyType != typeof(DrivenTransformProperties) ||
                _drivenProperties.GetGetMethod(true) == null)
            {
                throw new InvalidOperationException("无法只读访问 RectTransform.drivenProperties；请核对 Unity 版本及 IL2CPP accessor 保留。");
            }

            _target = target;
            _binding = target.GetComponent<PanelInputGate>();
            _frame = frame;
            _slot = (RectTransform)transform;
            _coordinates = frame.GetComponent<PanelInputGateLayout>();
            if (!_coordinates || !_coordinates._isPassThrough || _coordinates._target != target)
            {
                throw new InvalidOperationException("Panel 输入门禁需要先初始化 frame 的布局穿透组件。");
            }

            _initialParent = target.parent;
            _isPassThrough = false;
            _wasAttached = false;
            _isInitialized = true;
            Synchronize();
        }

        /// <summary>初始化内层坐标穿透节点，不让它作为目标控件的尺寸代理参与父布局。</summary>
        internal void InitializePassThrough(RectTransform target)
        {
            this._target = target;
            _binding = target.GetComponent<PanelInputGate>();
            _frame = (RectTransform)transform;
            _slot = _frame.parent as RectTransform;
            _initialParent = target.parent;
            _isPassThrough = true;
            _wasAttached = false;
            _isInitialized = true;
            Synchronize();
        }

        /// <summary>检查目标与两层挂载结构，并在同步期间拒绝绑定版本或父级已经变化的旧操作。</summary>
        private bool IsValid()
        {
            if (!_isInitialized || !_target || !_slot || !_frame || _frame.parent != _slot)
            {
                return false;
            }

            if (_isSynchronizing && (!_binding || _binding.BindingVersion != _synchronizationBinding || _slot.parent != _synchronizationParent))
            {
                return false;
            }

            if (_target.parent == _frame)
            {
                _wasAttached = true;
                return true;
            }
            // 首次挂接前只准备自有坐标；挂接后绝不追随外部重挂的业务对象。
            return !_wasAttached && _target.parent == _initialParent;
        }

        private bool IsMeasurable => !_isPassThrough && IsValid() && _target.parent == _frame && _target.gameObject.activeInHierarchy;
        /// <summary>目标最小宽度的代理值；不具备度量条件时为 0。</summary>
        public float minWidth => IsMeasurable ? LayoutUtility.GetMinWidth(_target) : 0;
        /// <summary>供原父布局分配空间的目标首选宽度。</summary>
        public float preferredWidth => IsMeasurable ? LayoutUtility.GetPreferredWidth(_target) : 0;
        /// <summary>目标参与水平剩余空间分配的弹性权重。</summary>
        public float flexibleWidth => IsMeasurable ? LayoutUtility.GetFlexibleWidth(_target) : 0;
        /// <summary>目标最小高度的代理值。</summary>
        public float minHeight => IsMeasurable ? LayoutUtility.GetMinHeight(_target) : 0;
        /// <summary>供原父布局分配空间的目标首选高度。</summary>
        public float preferredHeight => IsMeasurable ? LayoutUtility.GetPreferredHeight(_target) : 0;
        /// <summary>目标参与垂直剩余空间分配的弹性权重。</summary>
        public float flexibleHeight => IsMeasurable ? LayoutUtility.GetFlexibleHeight(_target) : 0;

        /// <summary>目标上启用的 ILayoutElement 中的最高优先级；无可度量元素时为 0。</summary>
        public int layoutPriority
        {
            get
            {
                if (!IsMeasurable)
                {
                    return 0;
                }

                _target.GetComponents(typeof(ILayoutElement), _elements);
                int priority = int.MinValue;
                bool found = false;
                foreach (var element in _elements)
                {
                    if (element is Behaviour behaviour && !behaviour.isActiveAndEnabled)
                    {
                        continue;
                    }

                    priority = Math.Max(priority, ((ILayoutElement)element).layoutPriority);
                    found = true;
                }
                return found ? priority : 0;
            }
        }

        /// <summary>代理目标的布局忽略决策；穿透节点、失效目标或未激活目标不参与父布局。</summary>
        public bool ignoreLayout
        {
            get
            {
                if (!IsMeasurable)
                {
                    return true;
                }

                _target.GetComponents(typeof(ILayoutIgnorer), _ignorers);
                foreach (var ignorer in _ignorers)
                {
                    if (!((ILayoutIgnorer)ignorer).ignoreLayout)
                    {
                        return false;
                    }
                }

                return _ignorers.Count != 0;
            }
        }

        internal void Synchronize() => Synchronize(false);
        /// <summary>标记挂载壳退役，留待可安全搬离子节点的生命周期入口清理。</summary>
        internal void RetireMount() => _isRetiring = true;

        /// <summary>同步自有占位和坐标节点，避开父布局当前驱动的字段，不直接重写目标的非驱动布局。</summary>
        private void Synchronize(bool prepareHorizontal)
        {
            if (_isRetiring || _isSynchronizing || !IsValid())
            {
                return;
            }

            _synchronizationBinding = _binding ? _binding.BindingVersion : 0;
            _synchronizationParent = _slot.parent;
            _isSynchronizing = true;
            bool changed = false;
            try
            {
                if (_isPassThrough)
                {
                    var parent = _slot.parent as RectTransform;
                    var size = parent ? parent.rect.size : Vector2.zero;
                    var pivot = parent ? parent.pivot : new Vector2(.5f, .5f);
                    var scale = _slot.localScale;
                    var inverse = new Vector3(Inverse(scale.x), Inverse(scale.y), Inverse(scale.z));
                    if (!_frame.anchorMin.Equals(Vector2.zero))
                    {
                        _frame.anchorMin = Vector2.zero;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_frame.anchorMax.Equals(Vector2.zero))
                    {
                        _frame.anchorMax = Vector2.zero;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_frame.pivot.Equals(pivot))
                    {
                        _frame.pivot = pivot;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_frame.sizeDelta.Equals(size))
                    {
                        _frame.sizeDelta = size;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_frame.localRotation.Equals(Quaternion.identity))
                    {
                        _frame.localRotation = Quaternion.identity;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_frame.localScale.Equals(inverse))
                    {
                        _frame.localScale = inverse;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    var position = -Vector3.Scale(_slot.localPosition, inverse);
                    if (!_frame.localPosition.Equals(position))
                    {
                        _frame.localPosition = position;
                        changed = true;
                    }
                    return;
                }

                bool ignored = ignoreLayout;
                if (_previousIgnore != ignored)
                {
                    _previousIgnore = ignored;
                    changed = true;
                }
                var layout = ParentLayout();
                if (layout && !ignored)
                {
                    // 横算在原父清除上轮 tracker 之前执行，不能沿用旧驱动 flags。
                    var preserve = prepareHorizontal ? DrivenTransformProperties.None : ReadDriven(layout);
                    changed |= CopyRect(_target, _slot, RectProperties & ~preserve);
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_slot.pivot.Equals(_target.pivot))
                    {
                        _slot.pivot = _target.pivot;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_slot.localScale.Equals(_target.localScale))
                    {
                        _slot.localScale = _target.localScale;
                        changed = true;
                    }
                }
                else
                {
                    var parent = _slot.parent as RectTransform;
                    var pivot = parent ? parent.pivot : new Vector2(.5f, .5f);
                    if (!_slot.anchorMin.Equals(Vector2.zero))
                    {
                        _slot.anchorMin = Vector2.zero;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_slot.anchorMax.Equals(Vector2.one))
                    {
                        _slot.anchorMax = Vector2.one;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_slot.pivot.Equals(pivot))
                    {
                        _slot.pivot = pivot;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_slot.sizeDelta.Equals(Vector2.zero))
                    {
                        _slot.sizeDelta = Vector2.zero;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_slot.anchoredPosition3D.Equals(Vector3.zero))
                    {
                        _slot.anchoredPosition3D = Vector3.zero;
                        changed = true;
                    }
                    if (!IsValid())
                    {
                        return;
                    }

                    if (!_slot.localScale.Equals(Vector3.one))
                    {
                        _slot.localScale = Vector3.one;
                        changed = true;
                    }
                }
                if (!IsValid())
                {
                    return;
                }

                if (!_slot.localRotation.Equals(Quaternion.identity))
                {
                    _slot.localRotation = Quaternion.identity;
                    changed = true;
                }
                if (IsValid())
                {
                    _coordinates.Synchronize();
                }
            }
            finally
            {
                _isSynchronizing = false;
                if (changed)
                {
                    SetDirty();
                }
            }
        }

        /// <summary>计算坐标抵消所需的有限倒数，零或不可表示的倒数按 0 处理。</summary>
        private static float Inverse(float value)
        {
            if (value == 0)
            {
                return 0;
            }

            float inverse = 1f / value;
            return float.IsInfinity(inverse) || float.IsNaN(inverse) ? 0 : inverse;
        }

        /// <summary>只取得占位直接父级上正在工作的布局组，不跨层猜测布局驱动者。</summary>
        private LayoutGroup ParentLayout()
        {
            var layout = _slot.parent ? _slot.parent.GetComponent<LayoutGroup>() : null;
            return layout && layout.isActiveAndEnabled ? layout : null;
        }

        /// <summary>仅在占位确由指定布局组驱动时读取驱动位，避免沿用其他驱动者的字段集合。</summary>
        private DrivenTransformProperties ReadDriven(LayoutGroup layout) => _slot.drivenByObject == layout
            ? (DrivenTransformProperties)_drivenProperties.GetValue(_slot) : DrivenTransformProperties.None;

        /// <summary>横向度量前准备占位，不继承父布局尚未清除的上轮驱动位。</summary>
        public void CalculateLayoutInputHorizontal() => Synchronize(true);
        /// <summary>纵向度量前按本轮已有驱动信息同步占位。</summary>
        public void CalculateLayoutInputVertical() => Synchronize(false);
        /// <summary>将父布局本轮横向驱动的结果回传到目标控件。</summary>
        public void SetLayoutHorizontal() => ApplyLayout(false);
        /// <summary>将父布局本轮纵向驱动的结果回传到目标控件。</summary>
        public void SetLayoutVertical() => ApplyLayout(true);

        /// <summary>按布局类型和当前轴筛选驱动字段，再把占位结果复制回目标，保留目标的自布局结果。</summary>
        private void ApplyLayout(bool vertical)
        {
            if (_isPassThrough)
            {
                Synchronize();
                return;
            }
            if (_isSynchronizing || !IsValid() || _target.parent != _frame)
            {
                return;
            }

            var layout = ParentLayout();
            if (!layout || ignoreLayout)
            {
                Synchronize();
                return;
            }
            var driven = ReadDriven(layout);
            if (layout is GridLayoutGroup)
            {
                driven &= DrivenTransformProperties.Anchors | DrivenTransformProperties.SizeDelta |
                    (vertical ? DrivenTransformProperties.AnchoredPosition : DrivenTransformProperties.None);
            }
            else if (layout is HorizontalOrVerticalLayoutGroup)
            {
                driven &= DrivenTransformProperties.Anchors | (vertical
                    ? DrivenTransformProperties.AnchoredPositionY | DrivenTransformProperties.SizeDeltaY
                    : DrivenTransformProperties.AnchoredPositionX | DrivenTransformProperties.SizeDeltaX);
            }
            // Grid 纵控确实重写两轴；线性布局纵控不能重放横向值覆盖已经执行的 CSF。
            _synchronizationBinding = _binding ? _binding.BindingVersion : 0;
            _synchronizationParent = _slot.parent;
            _isSynchronizing = true;
            try
            {
                _coordinates.Synchronize();
                if (!IsValid())
                {
                    return;
                }

                CopyRect(_slot, _target, driven);
                if (IsValid())
                {
                    _coordinates.Synchronize();
                }
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        /// <summary>仅复制指定驱动位对应的矩形字段，每次原生写入后复核挂载身份，返回是否发生变化。</summary>
        private bool CopyRect(RectTransform source, RectTransform destination, DrivenTransformProperties properties)
        {
            bool changed = false;
            var value = Select(destination.anchorMin, source.anchorMin, properties,
                DrivenTransformProperties.AnchorMinX, DrivenTransformProperties.AnchorMinY);
            if (!destination.anchorMin.Equals(value))
            {
                destination.anchorMin = value;
                changed = true;
            }
            if (!IsValid())
            {
                return changed;
            }

            value = Select(destination.anchorMax, source.anchorMax, properties,
                DrivenTransformProperties.AnchorMaxX, DrivenTransformProperties.AnchorMaxY);
            if (!destination.anchorMax.Equals(value))
            {
                destination.anchorMax = value;
                changed = true;
            }
            if (!IsValid())
            {
                return changed;
            }

            value = Select(destination.sizeDelta, source.sizeDelta, properties,
                DrivenTransformProperties.SizeDeltaX, DrivenTransformProperties.SizeDeltaY);
            if (!destination.sizeDelta.Equals(value))
            {
                destination.sizeDelta = value;
                changed = true;
            }
            if (!IsValid())
            {
                return changed;
            }

            value = Select(destination.anchoredPosition, source.anchoredPosition, properties,
                DrivenTransformProperties.AnchoredPositionX, DrivenTransformProperties.AnchoredPositionY);
            if (!destination.anchoredPosition.Equals(value))
            {
                destination.anchoredPosition = value;
                changed = true;
            }
            if (!IsValid())
            {
                return changed;
            }

            if ((properties & DrivenTransformProperties.AnchoredPositionZ) != 0)
            {
                var position = destination.localPosition;
                float z = source.localPosition.z;
                if (!position.z.Equals(z))
                {
                    position.z = z;
                    destination.localPosition = position;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>按两轴驱动位合成向量，未被驱动的分量保留目标当前值。</summary>
        private static Vector2 Select(Vector2 current, Vector2 input, DrivenTransformProperties properties,
            DrivenTransformProperties x, DrivenTransformProperties y)
        {
            if ((properties & x) != 0)
            {
                current.x = input.x;
            }

            if ((properties & y) != 0)
            {
                current.y = input.y;
            }

            return current;
        }

        /// <summary>在布局重建之外标记当前自有节点待重建，避免在重建栈中重复登记。</summary>
        private void SetDirty()
        {
            var rect = _isPassThrough ? _frame : _slot;
            if (rect && !CanvasUpdateRegistry.IsRebuildingLayout())
            {
                LayoutRebuilder.MarkLayoutForRebuild(rect);
            }
        }

        /// <summary>启用时继续未完成的退役，正常挂载则同步并请求布局更新。</summary>
        protected override void OnEnable()
        {
            base.OnEnable();
            if (_isRetiring)
            {
                Retire();
            }
            else
            {
                Synchronize();
                SetDirty();
            }
        }
        /// <summary>退出布局参与时通知布局系统重新计算，不在此搬移业务子节点。</summary>
        protected override void OnDisable()
        {
            SetDirty();
            base.OnDisable();
        }
        /// <summary>父级变化后重新同步自有坐标，并标记新的布局计算需求。</summary>
        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            Synchronize();
            SetDirty();
        }
        /// <summary>外部尺寸变化时同步；自身同步引发的回调不再次进入同一轮计算。</summary>
        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            if (_isSynchronizing)
            {
                return;
            }

            Synchronize();
            SetDirty();
        }
        /// <summary>子节点变化后检查挂载关系，并通知布局系统重算。</summary>
        private void OnTransformChildrenChanged()
        {
            Synchronize();
            SetDirty();
        }
        /// <summary>补齐本帧布局同步，或继续处理此前延后的挂载壳退役。</summary>
        private void LateUpdate()
        {
            if (_isRetiring)
            {
                Retire();
            }
            else
            {
                Synchronize();
            }
        }

        /// <summary>先把存活业务子节点移出旧壳，只在本轮确认壳内不再承载业务对象时销毁自有节点。</summary>
        private void Retire()
        {
            if (_isPassThrough || !_slot)
            {
                return;
            }

            var children = new List<Transform>();
            if (_frame)
            {
                for (int i = 0; i < _frame.childCount; i++)
                {
                    children.Add(_frame.GetChild(i));
                }
            }

            foreach (var child in children)
            {
                if (_slot && child && child.parent == _frame)
                {
                    child.SetParent(_slot.parent, false);
                }
            }

            if (!_slot)
            {
                return;
            }

            children.Clear();
            for (int i = 0; i < _slot.childCount; i++)
            {
                if (_slot.GetChild(i) != _frame)
                {
                    children.Add(_slot.GetChild(i));
                }
            }

            foreach (var child in children)
            {
                if (_slot && child && child.parent == _slot)
                {
                    child.SetParent(_slot.parent, true);
                }
            }

            if (!_slot)
            {
                return;
            }
            // 父级回调可向旧壳再次放入业务节点；本轮只处理快照，下帧重试，不能连带销毁。
            if ((_frame && _frame.childCount != 0) || _slot.childCount > (_frame && _frame.parent == _slot ? 1 : 0))
            {
                return;
            }

            Destroy(_slot.gameObject);
            _slot = null;
        }
    }
}
