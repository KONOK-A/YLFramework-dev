using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.Core
{
    /// <summary>Module 拥有的独立 UI 表现会话。复用 View/Model/事件/释放，不承担业务所有权。</summary>
    /// <remarks>首次打开先执行 View 的 OnLoaded，再执行 OnOpen；有效 Single 重开只执行 OnOpen，不重建会话。</remarks>
    public abstract class Panel : View
    {
        [SerializeField] private PanelConfig settings = PanelConfig.Default;
        private PanelConfig _effective;
        internal bool DefersEvents;
        internal object OpeningSession;
        internal PanelManager.PanelOperation InitialOpen;
        internal readonly List<CorePrefabLease> SourcePins = new List<CorePrefabLease>();

        /// <summary>当前有效配置的值副本；关闭后不能通过它修改旧会话。</summary>
        public PanelConfig EffectiveSettings => _effective;
        /// <summary>当前会话固定的真实根；不会因 Module 重新绑定而迁移。</summary>
        public UIRootContext Root
        {
            get; private set;
        }
        /// <summary>本 Panel 自身 Canvas，不借用祖先 Panel 的 Canvas。</summary>
        public Canvas Canvas
        {
            get; private set;
        }
        /// <summary>由根统一生成的只读顺序，不是业务 Priority。</summary>
        public int SortingOrder => Canvas ? Canvas.sortingOrder : 0;
        /// <summary>当前有效会话实际可见；Hide、根暂停或完整覆盖时为 false。</summary>
        public bool IsVisible => PanelManager.IsVisible(this);
        /// <summary>标准 UGUI 输入是否被本会话的可见性或同根更高模态阻挡；不接管游戏 ActionMap。</summary>
        public bool IsInputBlocked => PanelManager.IsInputBlocked(this);
        /// <summary>有效导航焦点候选，不等于 EventSystem 的实际选择。</summary>
        public bool HasFocus => this && IsLoaded && Session != null && PanelManager.GetFocusedPanel(Root) == this;
        /// <summary>同根最高的有效可见且参与 Top 的 Panel；模态阻挡本身不排除 Top。</summary>
        public bool IsTop() => this && IsLoaded && Session != null && PanelManager.GetTopPanel(Root) == this;

        internal PanelConfig Defaults => settings;
        /// <summary>在初始化时固定实际根及本会话配置，不改写 Prefab 默认值。</summary>
        internal void Configure(UIRootContext root, PanelConfig value)
        {
            Root = root;
            _effective = value;
        }
        internal void CommitSettings(PanelConfig value) => _effective = value;

        /// <summary>供派生面板准备实例级组件或事件声明；此时尚不能假定已绑定 Module 会话。</summary>
        protected virtual void Awake()
        {
        }
        /// <summary>每次有效打开的数据钩子。仅此调用中借读参数，不可关闭当前 Panel/owner 或重入本 Module 的 Panel 打开。</summary>
        protected virtual void OnOpen(LoadViewArgs args)
        {
        }

        /// <summary>在参数借读区间内执行打开钩子，并用当前会话身份标记受限调用阶段。</summary>
        internal void Open(LoadViewArgs args)
        {
            var previous = OpeningSession;
            OpeningSession = Session;
            try
            {
                args.BeginRead();
                try
                {
                    OnOpen(args);
                }
                finally
                {
                    args.EndRead();
                }
            }
            finally
            {
                OpeningSession = previous;
            }
        }

        /// <summary>保留会话和事件，仅撤销显示意图；不重新初始化、不触发 OnOpen。</summary>
        public void Hide() => PanelManager.SetVisible(this, false);
        /// <summary>恢复显示意图，仍受根、覆盖和模态规则约束；不能复活已结束会话。</summary>
        public void Show() => PanelManager.SetVisible(this, true);
        /// <summary>仅在同 Layer、Modal 分组和 Priority 内置顶。</summary>
        public void BringToFront() => PanelManager.BringToFront(this);
        /// <summary>原子修改五个显示字段；不改变根、owner、来源、实例模式或缓存策略，不调用 OnOpen。</summary>
        public void UpdateDisplay(PanelOverrides changes) => PanelManager.UpdateDisplay(this, changes);

        /// <summary>拒绝 OnOpen 对当前会话追加显示命令，避免覆盖本次打开尚未提交的意图。</summary>
        internal void EnsureDisplayAllowed()
        {
            if (OpeningSession != null && ReferenceEquals(OpeningSession, Session))
            {
                throw new InvalidOperationException("OnOpen 期间不能调用当前 Panel 的显示命令；请使用打开 overrides。");
            }
        }
        /// <summary>在业务加载回调前准备独立 Canvas、射线入口及协调器状态；已有对象只认真实祖先根。</summary>
        internal override void BeforeOnLoaded()
        {
            base.BeforeOnLoaded();
            if (!Root)
            {
                Configure(PanelManager.RequireAttachedRoot(this), settings);
            }

            _effective.Validate();
            Canvas = GetComponent<Canvas>();
            if (!Canvas)
            {
                Canvas = gameObject.AddComponent<Canvas>();
            }

            if (!GetComponent<GraphicRaycaster>())
            {
                gameObject.AddComponent<GraphicRaycaster>();
            }

            Canvas.overrideSorting = true;
            PanelManager.Prepare(this);
        }

        /// <summary>生成顺序变化通知。自身准备阶段的异常使打开失败，其他会话的观察异常单独报告。</summary>
        protected virtual void OnSortingOrderChange(int sortingOrder)
        {
        }
        /// <summary>统一协调后实际变为不可见的通知，不是 Hide 命令，也不替代 OnViewDisabled。</summary>
        protected virtual void Invisible()
        {
        }
        /// <summary>统一协调后实际变为可见的通知，不是 Show 命令，也不是每次打开钩子。</summary>
        protected virtual void Visible()
        {
        }
        internal void NotifySortingOrder() => OnSortingOrderChange(SortingOrder);
        /// <summary>把协调器确认的可见性变化转发到对应业务钩子。</summary>
        internal void NotifyVisible(bool value)
        {
            if (value)
            {
                Visible();
            }
            else
            {
                Invisible();
            }
        }
        /// <summary>清除已结束会话的根、配置和打开阶段标记，不销毁可复用的物理组件。</summary>
        internal void EndPanelSession()
        {
            Root = null;
            _effective = default;
            DefersEvents = false;
            OpeningSession = null;
        }
    }
}
