using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using YLFramework.Events;
using YLFramework.Singletons;
using UpdateManager = YLFramework.Update.UpdateManager;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Core
{
    /// <summary>协调 UI 请求与显示策略，不拥有 Module，也不维护第二套资源或实例池。</summary>
    public partial class PanelManager
    {
        private static PanelManager _instance;
        private IUnregister _lateSubscription;
        private bool _isReleased;
        private bool _cullCoveredPanels = true;
        private GameObject _eventSystem;
        private bool _hasWarnedInputConfiguration;
        private PanelState _pendingSelectionRelease;
        private GameObject _releasedSelection;
        private EventSystem _releasedSelectionSystem;
        private long _sequence;
        private readonly HashSet<UIRootContext> _registeredRoots = new HashSet<UIRootContext>();
        private readonly Dictionary<Panel, PanelState> _states = new Dictionary<Panel, PanelState>();

        /// <summary>一个精确 Panel 会话的显示意图和派生状态，不以可复用组件引用代替会话身份。</summary>
        private sealed class PanelState
        {
            internal Panel Panel;
            internal object Session;
            internal UIRootContext Root;
            internal long Sequence;
            internal long IntentSequence;
            internal int Order;
            internal bool WantsVisible = true;
            internal bool IsActive;
            internal bool IsVisible;
            internal bool IsBlocked = true;
            internal PanelInputState Input;
            internal GameObject Source;
            internal readonly HashSet<string> SourceNames = new HashSet<string>(StringComparer.Ordinal);
            internal List<CorePrefabLease> SourcePins => Panel.SourcePins;
        }

        /// <summary>登记执行会话重置与退出入口，使静态协调状态不跨运行沿用。</summary>
        static PanelManager()
        {
            SingletonSessionState.RegisterSessionReset(ResetSession);
            Application.quitting += OnExecutionEnded;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
        }
        /// <summary>停止执行会话并释放协调器当前持有的状态。</summary>
        private static void OnExecutionEnded()
        {
            SingletonSessionState.StopExecution();
            ResetSession();
        }
#if UNITY_EDITOR
        /// <summary>在退出 Play Mode 时走与应用退出相同的会话收尾。</summary>
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                OnExecutionEnded();
            }
        }
#endif
        /// <summary>限制协调器由内部惰性入口创建，不作为场景组件或业务实例构造。</summary>
        private PanelManager()
        {
        }
        internal static PanelManager Instance => _instance ?? (_instance = new PanelManager());
        /// <summary>撤销旧协调器、帧订阅和自有输入门禁；Module/View 及资源释放仍走各自生命周期。</summary>
        private static void ResetSession()
        {
            var manager = _instance;
            _instance = null;
            if (manager != null)
            {
                manager._isReleased = true;
                manager._lateSubscription?.Unregister();
                manager._lateSubscription = null;
                manager.ReleaseOwnedInput();
                foreach (var state in new List<PanelState>(manager._states.Values))
                {
                    state.Input.Restore(true);
                }
                manager._states.Clear();
                manager._registeredRoots.Clear();
            }
            CanvasCreated = null;
        }

        /// <summary>仅在有根或面板需要协调时保留 LateUpdate 订阅，并隔离旧协调器回调。</summary>
        private void SyncLateSubscription()
        {
            if (_isReleased || (_states.Count == 0 && _registeredRoots.Count == 0))
            {
                _lateSubscription?.Unregister();
                _lateSubscription = null;
                return;
            }
            if (_lateSubscription == null)
            {
                _lateSubscription = UpdateManager.AddLateUpdateEvent(() =>
                {
                    if (!_isReleased && ReferenceEquals(_instance, this))
                    {
                        Reconcile();
                    }
                });
            }
        }

        /// <summary>是否启用同根完整覆盖停绘。关闭不影响 Modal 阻挡或 View 会话。</summary>
        public static bool CullCoveredPanels
        {
            get => _instance == null || _instance._cullCoveredPanels;
            set
            {
                var manager = Instance;
                manager._cullCoveredPanels = value;
                manager.Reconcile();
            }
        }
        /// <summary>宿主默认根准备过程中的通知；本轮成功准备后不再重复，自定义根登记不触发。</summary>
        public static Action<Canvas> CanvasCreated;

        /// <summary>检查记录是否仍对应当前受管会话，不把组件复用后的新会话视为原状态。</summary>
        private bool IsCurrent(PanelState state) => !_isReleased && state.Panel && state.Session != null &&
            ReferenceEquals(state.Panel.Session, state.Session) && !state.Panel.IsRemoving && state.Panel.Module != null &&
            state.Panel.Module.State != ModuleState.Closing && _states.TryGetValue(state.Panel, out var current) && ReferenceEquals(current, state);
        /// <summary>在会话有效之外检查加载、启用、根结构及资源责任，作为显示与输入的共同前提。</summary>
        private bool IsActive(PanelState state) => IsCurrent(state) && state.Panel.IsLoaded && state.Panel.isActiveAndEnabled &&
            state.Root && state.Root.HasValidStructure && state.Root.IsAvailable && AreResourcesValid(state);
        /// <summary>按当前配置与会话排序序号比较两个面板，不直接比较 Canvas 的派生顺序。</summary>
        private static int Compare(PanelState a, PanelState b)
            => PanelConfig.Compare(a.Panel.EffectiveSettings, a.Sequence, b.Panel.EffectiveSettings, b.Sequence);
        /// <summary>报告单次观察通知的异常，让调用方继续派发其他会话的协调结果。</summary>
        private static void Notify(Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }
        /// <summary>先清空已接管的来源列表，再逐一释放并汇总错误；调用方须先完成实例处理。</summary>
        internal static void ReleaseSources(List<CorePrefabLease> sources)
        {
            if (sources == null)
            {
                return;
            }

            var owned = sources.ToArray();
            sources.Clear();
            var errors = new List<Exception>();
            foreach (var source in owned)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            ViewManager.ThrowCleanupErrors(errors);
        }
    }
}
