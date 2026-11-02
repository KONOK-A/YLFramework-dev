using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Core
{
    public partial class PanelManager
    {
        private static readonly Unity.Profiling.ProfilerMarker _reconcileProfile = new Unity.Profiling.ProfilerMarker("YL.UI.Reconcile");
        /// <summary>按根生成顺序预案并检查层与排序空间；可代入拟修改值，但不写回面板状态。</summary>
        private List<KeyValuePair<PanelState, int>> BuildOrders(Panel changed = null, PanelConfig? settings = null, long? accepted = null)
        {
            var list = new List<PanelState>();
            foreach (var state in _states.Values)
            {
                if (IsCurrent(state))
                {
                    list.Add(state);
                }
            }

            PanelConfig Settings(PanelState state) => state.Panel == changed && settings.HasValue ? settings.Value : state.Panel.EffectiveSettings;
            long Sequence(PanelState state) => state.Panel == changed && accepted.HasValue ? accepted.Value : state.Sequence;
            foreach (var state in list)
            {
                state.Root.ValidateRoot();
                state.Root.GetLayer(Settings(state).layer);
            }
            list.Sort((a, b) => a.Root != b.Root ? a.Root.GetInstanceID().CompareTo(b.Root.GetInstanceID()) : PanelConfig.Compare(Settings(a), Sequence(a), Settings(b), Sequence(b)));
            var orders = new List<KeyValuePair<PanelState, int>>(list.Count);
            UIRootContext root = null;
            long order = 0;
            foreach (var state in list)
            {
                if (state.Root != root)
                {
                    root = state.Root;
                    order = root.Canvas.sortingOrder;
                }
                // 每个模态预留前一格给遮罩，不能用业务 Priority 直接占用 Canvas 顺序空间。
                order += Settings(state).modal ? 2 : 1;
                if (order < short.MinValue || order > short.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(settings), "该根的 Panel/模态遮罩耗尽 Canvas 排序空间。");
                }

                orders.Add(new KeyValuePair<PanelState, int>(state, (int)order));
            }
            return orders;
        }

        /// <summary>在打开或重开前检查目标层与所需排序空间，不预先创建面板或遮罩。</summary>
        private void ValidateOpen(UIRootContext root, PanelConfig settings, Panel existing = null)
        {
            settings.Validate();
            root.ValidateRoot();
            root.GetLayer(settings.layer);
            if (existing)
            {
                BuildOrders(existing, settings);
                return;
            }
            long count = settings.modal ? 2 : 1;
            foreach (var state in _states.Values)
            {
                if (IsCurrent(state) && state.Root == root)
                {
                    count += state.Panel.EffectiveSettings.modal ? 2 : 1;
                }
            }

            if (root.Canvas.sortingOrder + count > short.MaxValue || root.Canvas.sortingOrder < short.MinValue)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "该根的 Panel/模态遮罩耗尽 Canvas 排序空间。");
            }
        }

        /// <summary>为初始化中的精确会话登记输入与排序状态，并让首次排序通知参与打开失败处理。</summary>
        internal static void Prepare(Panel panel)
        {
            var manager = Instance;
            PanelInputState.ValidateHierarchy(panel);
            manager.ValidateOpen(panel.Root, panel.EffectiveSettings);
            var operation = panel.InitialOpen;
            var state = new PanelState
            {
                Panel = panel,
                Session = panel.Session,
                Root = panel.Root,
                Sequence = operation != null ? operation.Sequence : checked(++manager._sequence),
                IntentSequence = operation?.Sequence ?? 0,
                Source = operation?.Prefab,
                Input = new PanelInputState(panel)
            };
            if (operation?.Name != null)
            {
                state.SourceNames.Add(operation.Name);
            }

            manager._states.Add(panel, state);
            manager._registeredRoots.Add(state.Root);
            manager.Reconcile(panel);
            if (manager.IsCurrent(state))
            {
                panel.NotifySortingOrder();
            }
        }

        /// <summary>检查显示命令的调用阶段并取得当前会话记录；结束会话不创建协调器。</summary>
        private static bool TryDisplay(Panel panel, out PanelManager manager, out PanelState state)
        {
            panel.EnsureDisplayAllowed();
            manager = _instance;
            state = null;
            return manager != null && manager._states.TryGetValue(panel, out state) && manager.IsCurrent(state);
        }
        /// <summary>预检排序后修改显示意图，不通过启停 GameObject 来结束或重开 View 事件。</summary>
        internal static void SetVisible(Panel panel, bool value)
        {
            if (!TryDisplay(panel, out var manager, out var state))
            {
                return;
            }

            manager.BuildOrders();
            state.WantsVisible = value;
            manager.Reconcile(mutationTarget: panel);
        }
        /// <summary>刷新平局序号以在原配置分组内置顶，预检失败时不消耗新序号。</summary>
        internal static void BringToFront(Panel panel)
        {
            if (!TryDisplay(panel, out var manager, out var state))
            {
                return;
            }

            long next = checked(manager._sequence + 1);
            manager.BuildOrders(panel, accepted: next);
            manager._sequence = next;
            state.Sequence = next;
            manager.Reconcile(mutationTarget: panel);
        }
        /// <summary>校验五字段显示覆盖，预检通过后提交配置与可能的切层，不执行打开数据钩子。</summary>
        internal static void UpdateDisplay(Panel panel, PanelOverrides changes)
        {
            panel.EnsureDisplayAllowed();
            changes.ValidateDisplay();
            if (!TryDisplay(panel, out var manager, out var state))
            {
                return;
            }

            var settings = changes.ApplyTo(panel.EffectiveSettings);
            manager.BuildOrders(panel, settings);
            if (!manager.CommitDisplay(state, settings, state.WantsVisible, null))
            {
                return;
            }

            manager.Reconcile(mutationTarget: panel);
        }
        /// <summary>必要时先重挂层容器，再向仍匹配的原会话提交配置、显隐意图与序号。</summary>
        private bool CommitDisplay(PanelState state, PanelConfig settings, bool visible, long? sequence)
        {
            if (!IsCurrent(state))
            {
                return false;
            }

            if (state.Panel.EffectiveSettings.layer != settings.layer)
            {
                // SetParent(false) 保留局部布局，不把 CoversRoot 当作拉伸指令。
                PanelInputGate.Reparent(state.Panel, state.Root.GetLayer(settings.layer));
                if (!IsCurrent(state))
                {
                    return false;
                }
            }
            state.Panel.CommitSettings(settings);
            state.WantsVisible = visible;
            if (sequence.HasValue)
            {
                state.Sequence = sequence.Value;
            }

            return true;
        }

        /// <summary>从会话快照派生排序、绘制、遮罩和输入，再通知变化；通知前复核身份以避开旧结果。</summary>
        private void Reconcile(Panel preparing = null, Panel mutationTarget = null)
        {
            using var profile = _reconcileProfile.Auto();
            if (_isReleased)
            {
                return;
            }

            var snapshot = new List<PanelState>(_states.Values);
            foreach (var state in snapshot)
            {
                if (!IsCurrent(state))
                {
                    continue;
                }

                if (!state.Root || !state.Root.HasValidStructure || !state.Panel.Canvas ||
                    state.Panel.GetComponentInParent<UIRootContext>(true) != state.Root || !AreResourcesValid(state))
                {
                    if (state.Panel.IsInitializing)
                    {
                        throw new InvalidOperationException("Panel 准备期间根、Canvas 或资源责任失效。");
                    }

                    Notify(state.Panel.Close);
                }
            }
            var orders = BuildOrders();
            var sorting = new List<KeyValuePair<PanelState, int>>();
            foreach (var pair in orders)
            {
                var state = pair.Key;
                if (state.Order != pair.Value)
                {
                    sorting.Add(pair);
                }

                state.Order = pair.Value;
            }
            var visibility = new List<KeyValuePair<PanelState, bool>>();
            foreach (var state in snapshot)
            {
                if (!IsCurrent(state))
                {
                    continue;
                }

                bool active = IsActive(state), visible = active && state.WantsVisible, blocked = !visible;
                foreach (var upper in snapshot)
                {
                    if (!IsActive(upper) || !upper.WantsVisible || upper.Root != state.Root || Compare(upper, state) <= 0)
                    {
                        continue;
                    }

                    if (_cullCoveredPanels && upper.Panel.EffectiveSettings.coversRoot)
                    {
                        visible = false;
                    }

                    if (upper.Panel.EffectiveSettings.modal)
                    {
                        blocked = true;
                    }
                }
                if (state.IsVisible != visible)
                {
                    visibility.Add(new KeyValuePair<PanelState, bool>(state, visible));
                }

                state.IsActive = active;
                state.IsVisible = visible;
                state.IsBlocked = blocked || !visible;
                var canvas = state.Panel.Canvas;
                // 首次启用会重建原生 Canvas 关系；显示时先启用再提交，隐藏时在禁绘前保留排序。
                if (visible)
                {
                    canvas.enabled = true;
                }

                if (!IsCurrent(state) || state.IsVisible != visible)
                {
                    continue;
                }

                if (!canvas.overrideSorting)
                {
                    canvas.overrideSorting = true;
                }

                if (!IsCurrent(state) || state.IsVisible != visible)
                {
                    continue;
                }

                if (canvas.sortingLayerID != state.Root.Canvas.sortingLayerID)
                {
                    canvas.sortingLayerID = state.Root.Canvas.sortingLayerID;
                }

                if (!IsCurrent(state) || state.IsVisible != visible)
                {
                    continue;
                }

                if (canvas.sortingOrder != state.Order)
                {
                    canvas.sortingOrder = state.Order;
                }

                if (!IsCurrent(state) || state.IsVisible != visible)
                {
                    continue;
                }

                if (!visible)
                {
                    canvas.enabled = false;
                }
            }
            foreach (var root in new List<UIRootContext>(_registeredRoots))
            {
                if (root)
                {
                    root.RefreshMasks();
                }
            }

            RefreshInput();
            RefreshPanelInput(snapshot);
            SyncLateSubscription();
            foreach (var pair in sorting)
            {
                if (pair.Key.Panel != preparing && IsCurrent(pair.Key) && pair.Key.Order == pair.Value)
                {
                    if (pair.Key.Panel == mutationTarget && pair.Key.Panel.IsInitializing)
                    {
                        pair.Key.Panel.NotifySortingOrder();
                    }
                    else
                    {
                        Notify(pair.Key.Panel.NotifySortingOrder);
                    }
                }
            }

            foreach (var pair in visibility)
            {
                if (IsCurrent(pair.Key) && pair.Key.IsVisible == pair.Value)
                {
                    if (pair.Key.Panel == preparing || (pair.Key.Panel == mutationTarget && pair.Key.Panel.IsInitializing))
                    {
                        pair.Key.Panel.NotifyVisible(pair.Value);
                    }
                    else
                    {
                        Notify(() => pair.Key.Panel.NotifyVisible(pair.Value));
                    }
                }
            }
        }
        /// <summary>View 开放事件时同步 Panel 状态，普通 View 不参与 UI 协调。</summary>
        internal static void ActivateView(View view)
        {
            if (view is Panel panel)
            {
                _instance?.Reconcile(mutationTarget: panel);
            }
        }
        /// <summary>View 暂停事件时重新评估 Panel 可用性，不据此直接删除会话。</summary>
        internal static void DeactivateView(View view)
        {
            if (view is Panel panel)
            {
                _instance?.Reconcile(mutationTarget: panel);
            }
        }
        internal static void LateUpdate() => _instance?.Reconcile();
    }
}
