using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YLFramework.Events;
using YLFramework.Singletons;
using ApplicationTool = YLFramework.Foundation.ApplicationTool;
using UpdateManager = YLFramework.Update.UpdateManager;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Core
{
    public partial class PanelManager
    {
        /// <summary>对已有协调器执行一次 EventSystem 发现与借用检查。</summary>
        private static void RefreshEventSystem()
        {
            _instance?.RefreshInput(true);
        }

        /// <summary>先放下自有系统引用，再停用并销毁该对象；不释放借用的外部 EventSystem。</summary>
        private void ReleaseOwnedInput()
        {
            var owned = _eventSystem;
            _eventSystem = null;
            if (!owned)
            {
                return;
            }

            InputSystemBridge.DisableOwnedSystem(owned);
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(owned);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(owned);
            }
        }

        /// <summary>有可用根时优先借用外部 active 系统，否则按需创建自有系统；无需求时只撤销自有系统。</summary>
        private void RefreshInput(bool discover = false)
        {
            if (_isReleased || !ApplicationTool.IsPlaying)
            {
                return;
            }

            _registeredRoots.RemoveWhere(root => !root || !root.HasValidStructure);
            bool needed = false;
            foreach (var root in _registeredRoots)
            {
                if (root.IsAvailable)
                {
                    needed = true;
                    break;
                }
            }

            if (!needed)
            {
                ReleaseOwnedInput();
                SyncLateSubscription();
                return;
            }
            var current = EventSystem.current;
            if (current && !current.isActiveAndEnabled)
            {
                current = null;
            }

            if (_eventSystem && current && current.gameObject == _eventSystem)
            {
                var external = InputSystemBridge.FindExternalSystem(current);
                if (external)
                {
                    current = external;
                }
            }
            if (discover || !current)
            {
                var systems = UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                int active = 0;
                bool inactive = false;
                foreach (var system in systems)
                {
                    if (!system.isActiveAndEnabled)
                    {
                        inactive = true;
                        continue;
                    }
                    active++;
                    if ((!current || current.gameObject == _eventSystem) && system.gameObject != _eventSystem)
                    {
                        current = system;
                    }
                }
                if (!_hasWarnedInputConfiguration && (active > 1 || (!current && inactive)))
                {
                    _hasWarnedInputConfiguration = true;
                    Debug.LogWarning("[PanelManager] 多个 active 或仅 inactive EventSystem。保留外部对象；优先借用 active 系统，inactive 系统不会被激活。");
                }
            }
            if (current && current.gameObject != _eventSystem)
            {
                ReleaseOwnedInput();
                // 借用系统不补齐、不切换其模块，也不更改其配置与对象寿命。
                if (!_hasWarnedInputConfiguration)
                {
                    bool usable = false;
                    foreach (var module in current.GetComponents<BaseInputModule>())
                    {
#if !ENABLE_LEGACY_INPUT_MANAGER
                        if (module is StandaloneInputModule)
                            continue;
#endif
                        if (module.isActiveAndEnabled && module.IsModuleSupported())
                        {
                            usable = true;
                            break;
                        }
                    }
                    if (!usable)
                    {
                        _hasWarnedInputConfiguration = true;
                        Debug.LogWarning("[PanelManager] 借用的 EventSystem 没有可用输入模块，请由其 owner 配置；框架不会替换外部模块。");
                    }
                }
                return;
            }
            if (_eventSystem)
            {
                return;
            }

            var prefab = Resources.Load<GameObject>("YLFramework/Core/EventSystem");
            if (!prefab)
            {
                throw new InvalidOperationException("Core EventSystem prefab is unavailable.");
            }

            var created = UnityEngine.Object.Instantiate(prefab);
            try
            {
                created.name = "EventSystem(Created By YLFramework.Core)";
                InputSystemBridge.EnsureEventSystemInputModule(created.GetComponent<EventSystem>());
                UnityEngine.Object.DontDestroyOnLoad(created);
                _eventSystem = created;
                SyncLateSubscription();
            }
            catch
            {
                created.SetActive(false);
                UnityEngine.Object.Destroy(created);
                throw;
            }
        }

        /// <summary>同步各会话的物理门禁与选择状态，选择回调后仅继续处理仍有效的候选。</summary>
        private void RefreshPanelInput(List<PanelState> snapshot)
        {
            var system = EventSystem.current;
            if (_pendingSelectionRelease != null && system && !system.alreadySelecting)
            {
                var ended = _pendingSelectionRelease;
                var target = _releasedSelection;
                var targetSystem = _releasedSelectionSystem;
                _pendingSelectionRelease = null;
                _releasedSelection = null;
                _releasedSelectionSystem = null;
                if (system == targetSystem && system.currentSelectedGameObject == target &&
                    (!ended.Panel || ended.Panel.Session == null || ReferenceEquals(ended.Panel.Session, ended.Session)))
                {
                    system.SetSelectedGameObject(null);
                }
            }
            // 移出原窗口的组件先解除旧门禁绑定，再由新窗口绑定；不能由枚举顺序决定归属。
            foreach (var state in snapshot)
            {
                if (IsCurrent(state))
                {
                    state.Input.RestoreDeparted();
                }
            }

            foreach (var state in snapshot)
            {
                if (!IsCurrent(state))
                {
                    continue;
                }

                var input = state.Input;
                if (state.IsBlocked && !input.IsBlocked && system && input.Owns(system.currentSelectedGameObject))
                {
                    input.Selection = system.currentSelectedGameObject;
                    input.SelectionSystem = system;
                }
                try
                {
                    input.Apply(state.IsBlocked);
                }
                catch (Exception error)
                {
                    if (state.Panel.IsInitializing)
                    {
                        throw;
                    }
                    // 动态业务层级使物理门禁失效时结束该会话，不能让标准控件在仅逻辑阻挡下继续接收输入。
                    Debug.LogException(error);
                    state.Panel.DiscardOnExit = true;
                    if (IsCurrent(state))
                    {
                        Notify(state.Panel.Close);
                    }
                }
            }
            if (!system || !system.isActiveAndEnabled || system.alreadySelecting)
            {
                return;
            }

            bool cleared = false;
            foreach (var state in snapshot)
            {
                if (IsCurrent(state) && state.IsBlocked && state.Input.Owns(system.currentSelectedGameObject))
                {
                    system.SetSelectedGameObject(null);
                    cleared = true;
                    break;
                }
            }
            // Select/Deselect 可关闭、销毁或重开窗口。只允许仍匹配的会话恢复，且不覆盖外部或新选择。
            if (_isReleased || EventSystem.current != system || system.currentSelectedGameObject)
            {
                return;
            }

            snapshot.Sort((a, b) => Compare(b, a));
            foreach (var state in snapshot)
            {
                if (!IsActive(state) || state.IsBlocked || GetFocusedPanel(state.Root) != state.Panel)
                {
                    continue;
                }

                var input = state.Input;
                var selection = input.Selection;
                if (selection && input.SelectionSystem == system && input.Owns(selection) && selection.activeInHierarchy &&
                    selection.TryGetComponent<Selectable>(out var selectable) && selectable.isActiveAndEnabled && selectable.IsInteractable())
                {
                    input.Selection = null;
                    system.SetSelectedGameObject(selection);
                    return;
                }
                if ((cleared || state.Panel.EffectiveSettings.modal) && state.Panel.EffectiveSettings.modal && state.Panel.EffectiveSettings.participatesInTop)
                {
                    selection = input.FirstSelection();
                    if (selection)
                    {
                        system.SetSelectedGameObject(selection);
                        return;
                    }
                }
            }
        }

        /// <summary>撤销精确 Panel 记录、捕获与选择，并交还来源 pin 列表给 View 的实例收尾流程。</summary>
        internal static void ReleaseView(View view, out List<CorePrefabLease> sources, bool rollback)
        {
            sources = null;
            var manager = _instance;
            if (!(view is Panel panel))
            {
                return;
            }
            // 来源责任随 Core 实例会话保存；协调器先 reset 也不能先于 instance discard 释放它。
            sources = panel.SourcePins;
            if (sources.Count != 0)
            {
                view.DiscardOnExit = true;
            }

            if (manager == null)
            {
                return;
            }

            if (manager._states.TryGetValue(panel, out var state))
            {
                manager._states.Remove(panel);
                if (!rollback && state.Source && panel.EffectiveSettings.instanceMode == PanelInstanceMode.Single)
                {
                    manager._sequence = checked(manager._sequence + 1);
                    foreach (var pending in panel.Module.PendingViews)
                    {
                        if (pending is PanelOperation operation && operation.Sequence < manager._sequence)
                        {
                            operation.AddClosedScope(state.Source, state.Root);
                        }
                    }
                }
                var system = EventSystem.current;
                if (system && state.Input.Owns(system.currentSelectedGameObject))
                {
                    if (system.alreadySelecting)
                    {
                        manager._pendingSelectionRelease = state;
                        manager._releasedSelection = system.currentSelectedGameObject;
                        manager._releasedSelectionSystem = system;
                    }
                    else
                    {
                        system.SetSelectedGameObject(null);
                    }
                }
                state.Input.Restore(true);
            }
            manager.Reconcile();
        }

        internal static bool IsVisible(Panel panel) => _instance != null && panel &&
            _instance._states.TryGetValue(panel, out var state) && _instance.IsActive(state) && state.IsVisible;
        internal static bool IsInputBlocked(Panel panel) => !IsVisible(panel) || _instance._states[panel].IsBlocked;

        /// <summary>只在指定根的有效可见会话中，按模态、焦点或 Top 资格选取排序最高项。</summary>
        private static Panel Query(UIRootContext root, bool modal, bool focus)
        {
            var manager = _instance;
            if (manager == null || !root)
            {
                return null;
            }

            PanelState top = null;
            foreach (var state in manager._states.Values)
            {
                var settings = state.Panel.EffectiveSettings;
                if (!manager.IsActive(state) || !state.IsVisible || state.Root != root ||
                    (modal ? !settings.modal : focus ? state.IsBlocked || (!settings.participatesInTop && !settings.modal) : !settings.participatesInTop))
                {
                    continue;
                }

                if (top == null || Compare(state, top) > 0)
                {
                    top = state;
                }
            }
            return top?.Panel;
        }

        /// <summary>指定实际根中参与 Top 的最高有效可见 Panel，可包含模态；不创建 Manager 或宿主。</summary>
        public static Panel GetTopPanel(UIRootContext root) => Query(root, false, false);
        /// <summary>指定根最高的有效可见模态 Panel，包含不参与 Top 的模态。</summary>
        public static Panel GetModalPanel(UIRootContext root) => Query(root, true, false);
        /// <summary>指定根当前的有效焦点候选，不创建根或 Manager。</summary>
        public static Panel GetFocusedPanel(UIRootContext root) => Query(root, false, true);

        /// <summary>取得指定层最高的有效可见模态，供该层遮罩使用，不受 Top 参与资格限制。</summary>
        internal static Panel GetLayerModal(UIRootContext root, int layer)
        {
            if (_instance == null)
            {
                return null;
            }

            PanelState top = null;
            foreach (var state in _instance._states.Values)
            {
                if (_instance.IsActive(state) && state.IsVisible && state.Root == root && state.Panel.EffectiveSettings.modal &&
                    state.Panel.EffectiveSettings.layer == layer && (top == null || Compare(state, top) > 0))
                {
                    top = state;
                }
            }

            return top?.Panel;
        }

        /// <summary>清空并填充指定根的全部有效会话，包含隐藏和停用会话；不承诺枚举排序。</summary>
        public static void GetAllPanels(UIRootContext root, List<Panel> results)
        {
            if (results == null)
            {
                throw new ArgumentNullException(nameof(results));
            }

            results.Clear();
            var manager = _instance;
            if (manager == null || !root || !root.HasValidStructure)
            {
                return;
            }

            foreach (var state in manager._states.Values)
            {
                if (manager.IsCurrent(state) && state.Root == root && AreResourcesValid(state))
                {
                    results.Add(state.Panel);
                }
            }
        }

    }
}
