using System;
using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.Core
{
    /// <summary>以框架自有 CanvasGroup 阻挡单个 Selectable，已有业务组时使用独立布局挂载层。</summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    internal sealed class PanelInputGate : MonoBehaviour
    {
        private Selectable _target;
        private PanelInputState _owner;
        private CanvasGroup _group;
        private RectTransform _slot;
        private RectTransform _frame;
        private PanelInputGateLayout _layout;
        private bool _isInstallingSelf;
        private bool _isCreatingMount;
        internal ulong BindingVersion
        {
            get; private set;
        }

        /// <summary>取得当前会话的门禁绑定并准备自有组；原生回调改变绑定时返回 false。</summary>
        internal bool Bind(PanelInputState state, Selectable selectable)
        {
            _target = selectable;
            if (!ReferenceEquals(_owner, state))
            {
                _owner = state;
                BindingVersion++;
            }
            if (_isCreatingMount)
            {
                return false;
            }

            if (_slot && _target.transform.parent != _frame)
            {
                ReleaseMount();
            }

            if (!_group)
            {
                if (_isInstallingSelf)
                {
                    _group = GetComponent<CanvasGroup>();
                }
                else if (GetComponent<CanvasGroup>())
                {
                    CreateMount();
                }
                else
                {
                    // AddComponent 的原生回调可关闭并重开同一实例；新会话识别在建的自有组，不再 Add。
                    _isInstallingSelf = true;
                    try
                    {
                        _group = gameObject.AddComponent<CanvasGroup>();
                    }
                    finally
                    {
                        _isInstallingSelf = false;
                    }
                }
            }
            if (!IsBound(state) || _isCreatingMount)
            {
                return false;
            }

            if (!_group && !_isInstallingSelf)
            {
                throw new InvalidOperationException("无法创建框架自有 CanvasGroup 门禁。");
            }

            _layout?.Synchronize();
            return IsBound(state);
        }

        /// <summary>确认调用方仍持有这份物理门禁，且其原会话尚未结束。</summary>
        private bool IsBound(PanelInputState state) => ReferenceEquals(_owner, state) && state.IsCurrent;

        /// <summary>同步布局后仅改自有组的交互开关；绑定或阻挡意图已变时不提交旧值。</summary>
        internal bool Apply(PanelInputState state, bool blocked)
        {
            if (!IsBound(state) || _isCreatingMount)
            {
                return false;
            }

            _layout?.Synchronize();
            if (!IsBound(state) || state.IsBlocked != blocked)
            {
                return false;
            }

            if (_group)
            {
                _group.interactable = !blocked;
            }

            return IsBound(state);
        }

        /// <summary>解除匹配会话并开放自有组，不销毁组或改写业务 CanvasGroup。</summary>
        internal void Release(PanelInputState state)
        {
            if (!ReferenceEquals(_owner, state))
            {
                return;
            }

            _owner = null;
            BindingVersion++;
            if (_group)
            {
                _group.interactable = true;
            }
            // 资源随物理实例保留；会话结束不延迟 Destroy 后再于同帧争用唯一组件槽。
        }

        /// <summary>创建隐藏的占位与坐标穿透节点，令独立门禁参与布局而不接管业务组。</summary>
        private void CreateMount()
        {
            var initial = _owner;
            _isCreatingMount = true;
            try
            {
                var rect = (RectTransform)_target.transform;
                var host = new GameObject("YL.PanelInputGate", typeof(RectTransform));
                host.SetActive(false);
                host.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
                _slot = (RectTransform)host.transform;
                _group = host.AddComponent<CanvasGroup>();
                var coordinates = new GameObject("Coordinates", typeof(RectTransform));
                coordinates.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
                _frame = (RectTransform)coordinates.transform;
                _frame.SetParent(_slot, false);
                coordinates.AddComponent<PanelInputGateLayout>().InitializePassThrough(rect);
                _layout = host.AddComponent<PanelInputGateLayout>();
                _layout.Initialize(rect, _frame);
                // 先备齐自有资源再接入业务层级；原生父级回调重开时只接管绑定，不重复安装。
                _slot.SetParent(rect.parent, false);
                if (!_target || _owner == null || !_owner.IsCurrent)
                {
                    return;
                }

                if (_slot.parent != rect.parent)
                {
                    _slot.SetParent(rect.parent, false);
                }

                _slot.SetSiblingIndex(rect.GetSiblingIndex());
                _layout.Synchronize();
                host.SetActive(true);
                if (!_target || _owner == null || !_owner.IsCurrent)
                {
                    return;
                }

                rect.SetParent(_frame, false);
                if (_target && _target.transform.parent == _frame)
                {
                    _layout.Synchronize();
                }
            }
            finally
            {
                _isCreatingMount = false;
                if (_owner != null && _owner.IsCurrent && _group)
                {
                    _group.interactable = !_owner.IsBlocked;
                }

                if (_owner != null && _owner.IsCurrent && !ReferenceEquals(_owner, initial))
                {
                    PanelManager.LateUpdate();
                }
            }
        }

        /// <summary>切层时连同门禁占位一起移动，未装占位时直接重挂面板，均保留局部布局。</summary>
        internal static void Reparent(Panel panel, Transform parent)
        {
            var gate = panel.GetComponent<PanelInputGate>();
            if (gate)
            {
                gate.BindingVersion++;
            }

            if (!gate || !gate._slot || panel.transform.parent != gate._frame)
            {
                panel.transform.SetParent(parent, false);
                return;
            }
            gate._slot.SetParent(parent, false);
            if (gate && gate._layout && panel && panel.transform.parent == gate._frame)
            {
                gate._layout.Synchronize();
            }
        }

        /// <summary>业务对象被外部重挂时请求重新协调，自身安装到坐标节点的回调不重复处理。</summary>
        private void OnTransformParentChanged()
        {
            if (_owner == null || (_frame && transform.parent == _frame))
            {
                return;
            }

            PanelManager.LateUpdate();
        }

        /// <summary>放下旧挂载引用并开放旧组，让旧布局壳延后退役，不把旧壳重新绑定到业务对象。</summary>
        private void ReleaseMount()
        {
            var previous = _layout;
            var previousGroup = _group;
            _slot = null;
            _frame = null;
            _layout = null;
            _group = null;
            if (previousGroup)
            {
                previousGroup.interactable = true;
            }

            if (previous)
            {
                previous.RetireMount();
            }
        }

        /// <summary>撤销绑定并标记挂载壳退役，不在销毁回调中强行重挂业务 Transform。</summary>
        private void OnDestroy()
        {
            _owner = null;
            BindingVersion++;
            if (_group)
            {
                _group.interactable = true;
            }
            // OnDestroy 中业务 Transform 仍可能 truthy 但已禁止重挂；让存活的自有壳在下一帧清理。
            if (_layout)
            {
                _layout.RetireMount();
            }
        }
    }
}
