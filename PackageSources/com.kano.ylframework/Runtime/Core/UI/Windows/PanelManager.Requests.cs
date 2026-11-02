using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Core
{
    public partial class PanelManager
    {
        /// <summary>单个 Panel 消费者持有的请求输入、资源解析和终态状态。</summary>
        /// <remarks>异步记录只登记在 Module.PendingViews；合流批次仅存在于提交栈，不另建在途请求权威。</remarks>
        internal sealed class PanelOperation : IPendingViewRequest
        {
            internal Module Owner;
            private object _lifecycle;
            internal readonly UIRootContext Root;
            internal readonly string Name;
            internal GameObject Prefab;
            private readonly bool _isNamed;
            internal readonly PanelRuleSnapshot Rules;
            private readonly PanelOverrides _overrides;
            private readonly Type _expected;
            internal readonly long Sequence;
            internal readonly bool IsAsync;
            private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
            private readonly ICoreResourceProvider _provider;
            private readonly List<string> _projects;
            internal LoadViewArgs Args;
            internal CorePrefabLease Lease;
            private CoreResourceRequest<CorePrefabLease> _resource;
            private readonly List<(GameObject Source, UIRootContext Root)> _closedScopes = new List<(GameObject, UIRootContext)>();
            internal PanelConfig Settings;
            private bool _isPrepared;
            internal bool IsCommitted;
            internal bool IsTerminal;
            private bool _isFailing;
            /// <summary>在失败回滚开始前封住主动取消，并把失败阶段传给同批消费者。</summary>
            internal void MarkFailing()
            {
                if (_isFailing)
                {
                    return;
                }

                _isFailing = true;
                BeginFailure?.Invoke();
            }
            private bool _isInvalidating;
            internal Action BeginCommit;
            internal Action BeginFailure;
            internal Action<float> ProgressChanged;
            internal Action<Panel, Exception, LoadViewStatus> Completed;
            internal Panel Result;
            internal object DeliveredSession;
            internal Exception Failure;

            /// <summary>保存接受时的依赖快照；异步请求另登记到 owner 并监听执行结束。</summary>
            internal PanelOperation(Module owner, string name, GameObject prefab, LoadViewArgs args, UIRootContext root,
                PanelRuleSnapshot rules, PanelOverrides overrides, Type expected, long sequence, bool asynchronous)
            {
                Owner = owner;
                _lifecycle = owner.ViewLifecycle;
                Name = name;
                Prefab = prefab;
                Args = args;
                Root = root;
                _isNamed = name != null;
                Rules = rules;
                this._overrides = overrides;
                this._expected = expected;
                Sequence = sequence;
                IsAsync = asynchronous;
                _provider = _isNamed ? CoreResourceAccess.Provider : null;
                _projects = ModuleManager.ViewResourceProjects(owner);
                if (asynchronous)
                {
                    owner.PendingViews.Add(this);
                    Application.quitting += OnExecutionEnded;
#if UNITY_EDITOR
                    EditorApplication.playModeStateChanged += OnPlayChanged;
#endif
                }
            }
            /// <summary>执行环境退出时将未完成请求标记为失效，而不是用户取消。</summary>
            private void OnExecutionEnded() => Finish(null, null, new InvalidOperationException("Panel 执行会话已结束。"), LoadViewStatus.Invalidated);
#if UNITY_EDITOR
            /// <summary>退出 Play Mode 时结束仍在途的消费者。</summary>
            private void OnPlayChanged(PlayModeStateChange state)
            {
                if (state == PlayModeStateChange.ExitingPlayMode)
                {
                    OnExecutionEnded();
                }
            }
#endif
            /// <summary>复核 owner 代次、固定根、来源与租约，以及接受后是否已关闭同一单实例范围。</summary>
            internal void Validate()
            {
                if (IsTerminal || _isInvalidating || Owner == null || !ReferenceEquals(Owner.ViewLifecycle, _lifecycle))
                {
                    throw new InvalidOperationException("Panel 请求的 Module 代次已结束。");
                }

                ViewManager.EnsureOwner(Owner);
                if (!_isNamed && !Prefab)
                {
                    throw new InvalidOperationException("直接 Panel 来源已销毁，不能退回命名加载。");
                }

                if (!Root || !Root.HasValidStructure || !Root.gameObject.scene.isLoaded)
                {
                    throw new InvalidOperationException("Panel 请求的根已失效。");
                }

                if (_isPrepared && !Prefab)
                {
                    throw new InvalidOperationException("Panel 来源 Prefab 已销毁。");
                }

                if (Lease != null && !Lease.IsValid)
                {
                    throw new InvalidOperationException("Panel 来源租约已失效。");
                }

                foreach (var closed in _closedScopes)
                {
                    if (Prefab && ReferenceEquals(closed.Source, Prefab) && closed.Root == Root)
                    {
                        throw new InvalidOperationException("Panel 请求接受后，其单实例范围已关闭。");
                    }
                }
            }
            /// <summary>只在 owner、根及可证明来源一致时判为同范围，不凭相似地址猜测资源身份。</summary>
            internal bool KnownSame(PanelOperation other)
            {
                if (Owner != other.Owner || Root != other.Root)
                {
                    return false;
                }

                if (Prefab && other.Prefab)
                {
                    return ReferenceEquals(Prefab, other.Prefab);
                }

                if (!_isNamed || !other._isNamed || !ReferenceEquals(_provider, other._provider) || !StringComparer.Ordinal.Equals(Name, other.Name) || _projects.Count != other._projects.Count)
                {
                    return false;
                }

                for (int i = 0; i < _projects.Count; i++)
                {
                    if (!StringComparer.Ordinal.Equals(_projects[i], other._projects[i]))
                    {
                        return false;
                    }
                }

                return true;
            }
            /// <summary>校验已解析 Prefab 的根类型并计算一次有效配置，后续仍复核请求依赖。</summary>
            internal void Prepare(GameObject resolved)
            {
                Validate();
                if (!_isPrepared)
                {
                    Prefab = resolved;
                    var view = Prefab.GetComponent<View>();
                    if (!view)
                    {
                        throw new InvalidOperationException("Panel 来源没有根 View 组件。");
                    }

                    if (!_expected.IsInstanceOfType(view))
                    {
                        throw new InvalidCastException($"View {view.GetType().FullName} cannot be loaded as {_expected.FullName}.");
                    }

                    if (!(view is Panel panel))
                    {
                        throw new InvalidCastException("Panel 入口需要根 Panel。");
                    }

                    // 逐字段按 Prefab 默认、Module 规则、本次覆盖的顺序合成，未指定字段继续沿用下层值。
                    Settings = _overrides.ApplyTo(Rules.Resolve(Name, Prefab).ApplyTo(panel.Defaults));
                    _isPrepared = true;
                }
                Validate();
            }
            /// <summary>直接使用 Prefab 或按项目顺序同步取租约；只有允许回退的资源错误才尝试下一来源。</summary>
            internal void ResolveSync()
            {
                Validate();
                if (!_isNamed)
                {
                    Prepare(Prefab);
                    return;
                }
                CoreResourceAccess.RequireProvider(_provider);
                Exception failure = null;
                foreach (var project in _projects)
                {
                    try
                    {
                        Lease = _provider.AcquirePrefab(project, Name);
                    }
                    catch (Exception error) when (CoreResourceAccess.CanFallback(error))
                    {
                        failure = error;
                    }
                    Validate();
                    if (Lease == null)
                    {
                        continue;
                    }

                    Prepare(Lease.Prefab);
                    return;
                }
                throw new InvalidOperationException("Panel prefab not found: " + Name, failure);
            }
            /// <summary>拒绝实例后端交付与请求类型或已解析 Prefab 根类型不一致的对象。</summary>
            internal void ValidateAcquired(View view)
            {
                if (!view || !_expected.IsInstanceOfType(view) || view.GetType() != Prefab.GetComponent<View>().GetType())
                {
                    throw new InvalidCastException("Panel 实例后端交付的根类型与已解析 Prefab/请求类型不匹配。");
                }
            }
            /// <summary>至少跨一帧解析来源并转发真实资源进度，等待期间持续检查接受时的依赖。</summary>
            private IEnumerator ResolveAsync()
            {
                yield return null;
                Validate();
                if (Prefab)
                {
                    Prepare(Prefab);
                    yield break;
                }
                CoreResourceAccess.RequireProvider(_provider);
                Exception failure = null;
                foreach (var project in _projects)
                {
                    CoreResourceRequest<CorePrefabLease> acquired;
                    try
                    {
                        acquired = _provider.AcquirePrefabAsync(project, Name);
                    }
                    catch (Exception error) when (CoreResourceAccess.CanFallback(error))
                    {
                        failure = error;
                        continue;
                    }
                    try
                    {
                        Validate();
                    }
                    catch
                    {
                        acquired?.Dispose();
                        throw;
                    }
                    _resource = acquired ?? throw new InvalidOperationException("Panel 资源请求为 null。");
                    while (_resource.Status == CoreResourceStatus.Pending)
                    {
                        Validate();
                        ProgressChanged?.Invoke(_resource.Progress);
                        yield return null;
                    }
                    Validate();
                    var status = _resource.Status;
                    if (status != CoreResourceStatus.Succeeded)
                    {
                        failure = _resource.Error ?? new CoreResourceException(CoreResourceError.LoadFailed, "Panel 资源解析失败。");
                        _resource.Dispose();
                        _resource = null;
                        if (status == CoreResourceStatus.Canceled || status == CoreResourceStatus.Invalidated || !CoreResourceAccess.CanFallback(failure))
                        {
                            throw failure;
                        }

                        continue;
                    }
                    Lease = _resource.TakeResult();
                    _resource.Dispose();
                    _resource = null;
                    Prepare(Lease.Prefab);
                    // 热资源也留出同帧消费者解析边界，所有数据回调仍晚于接受入口。
                    yield return null;
                    yield break;
                }
                throw new InvalidOperationException("Panel prefab not found: " + Name, failure);
            }
            /// <summary>驱动资源解析后进入打开仲裁，将未产生结果的错误归入失败或依赖失效终态。</summary>
            internal IEnumerator Run()
            {
                var routine = ResolveAsync();
                Exception failure = null;
                while (!IsTerminal && !_isInvalidating)
                {
                    bool more;
                    try
                    {
                        more = routine.MoveNext();
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        break;
                    }
                    if (!more)
                    {
                        break;
                    }

                    yield return routine.Current;
                }
                (routine as IDisposable)?.Dispose();
                if (IsTerminal || _isInvalidating)
                {
                    yield break;
                }

                if (failure == null)
                {
                    try
                    {
                        Instance.Execute(this);
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                }
                if (!IsTerminal && !_isInvalidating)
                {
                    Finish(null, null, failure ?? new InvalidOperationException("Panel 生产未产生终态。"),
                    StillValid() ? LoadViewStatus.Failed : LoadViewStatus.Invalidated);
                }
            }
            /// <summary>仅将当前依赖校验转换为布尔值，供异常收尾区分 Failed 与 Invalidated。</summary>
            internal bool StillValid()
            {
                try
                {
                    Validate();
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            /// <summary>仅在接受线程、尚未提交且未进入失效或失败收尾时取消本消费者。</summary>
            internal void Abort()
            {
                if (Thread.CurrentThread.ManagedThreadId != _thread)
                {
                    throw new InvalidOperationException("Panel Abort 必须在接受线程执行。");
                }

                if (IsTerminal || IsCommitted || _isInvalidating || _isFailing)
                {
                    return;
                }

                Finish(null, null, new OperationCanceledException("Panel 请求已取消。"), LoadViewStatus.Canceled);
            }
            /// <summary>owner 退出时先撤销输入责任，延后终态通知以免观察者干扰 Module 清理。</summary>
            void IPendingViewRequest.Invalidate()
            {
                if (IsTerminal)
                {
                    return;
                }

                _isInvalidating = true;
                Failure = new InvalidOperationException("Panel owner 生命周期已结束。");
                ReleaseInputs();
            }
            /// <summary>在 owner 清理边界之后发布此前保留的失效结果。</summary>
            void IPendingViewRequest.PublishInvalidation()
            {
                _isInvalidating = false;
                Finish(null, null, Failure, LoadViewStatus.Invalidated);
            }
            internal void AddClosedScope(GameObject source, UIRootContext root) => _closedScopes.Add((source, root));

            /// <summary>先断开 pending、退出监听及输入引用，再释放资源请求、租约和参数，汇总清理错误。</summary>
            internal void ReleaseInputs()
            {
                Application.quitting -= OnExecutionEnded;
#if UNITY_EDITOR
                EditorApplication.playModeStateChanged -= OnPlayChanged;
#endif
                Owner?.PendingViews.Remove(this);
                var oldArgs = Args;
                var oldLease = Lease;
                var oldRequest = _resource;
                Args = null;
                Lease = null;
                _resource = null;
                BeginCommit = null;
                BeginFailure = null;
                var errors = new List<Exception>();
                try
                {
                    oldRequest?.Dispose();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                try
                {
                    oldLease?.Dispose();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                try
                {
                    oldArgs?.Release();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                ViewManager.ThrowCleanupErrors(errors);
            }
            /// <summary>至多提交一次终态；成功通知前复核原 Session，清理失败可将本次结果改为失败。</summary>
            internal void Finish(Panel result, object session, Exception failure, LoadViewStatus status)
            {
                if (IsTerminal || _isInvalidating)
                {
                    return;
                }

                if (status == LoadViewStatus.Succeeded)
                {
                    try
                    {
                        Validate();
                        if (!result || !result.IsLoaded || !ReferenceEquals(result.Session, session) || result.Module != Owner ||
                            !Instance._states.TryGetValue(result, out var state) || !AreResourcesValid(state))
                        {
                            throw new InvalidOperationException("Panel 交付前原 Session 或资源责任已结束。");
                        }
                    }
                    catch (Exception error)
                    {
                        result = null;
                        failure = error;
                        status = LoadViewStatus.Invalidated;
                    }
                }
                IsTerminal = true;
                try
                {
                    ReleaseInputs();
                }
                catch (Exception cleanup)
                {
                    if (failure == null)
                    {
                        failure = cleanup;
                        result = null;
                        status = LoadViewStatus.Failed;
                    }
                    else
                    {
                        StartUpModuleRequest.AttachCleanupError(failure, cleanup);
                    }
                }
                Result = result;
                DeliveredSession = result ? session : null;
                Failure = failure;
                var notify = Completed;
                Completed = null;
                ProgressChanged = null;
                Owner = null;
                _lifecycle = null;
                Prefab = null;
                _closedScopes.Clear();
                notify?.Invoke(result, failure, status);
            }
        }

        /// <summary>检查实例租约、额外来源 pin 和原 Prefab 引用，不因面板组件仍存活就视为资源有效。</summary>
        private static bool AreResourcesValid(PanelState state)
        {
            if (state.Panel.BackendLease != null && !state.Panel.BackendLease.IsValid)
            {
                return false;
            }

            foreach (var pin in state.SourcePins)
            {
                if (!pin.IsValid)
                {
                    return false;
                }
            }

            return ReferenceEquals(state.Source, null) || state.Source;
        }
        internal static bool IsPreparedValid(Panel panel) => _instance != null && _instance._states.TryGetValue(panel, out var state) &&
            _instance.IsCurrent(state) && state.Root && state.Root.HasValidStructure &&
            panel.GetComponentInParent<UIRootContext>(true) == state.Root && AreResourcesValid(state);
        /// <summary>汇集已证明同源的会话地址和候选请求，拒绝它们在本规则快照中命中不同条目。</summary>
        private void ValidateRules(PanelOperation operation, PanelState existing, List<PanelOperation> candidates)
        {
            int matched = operation.Rules.Match(operation.Name, operation.Prefab);
            // 同一 Prefab 的别名可补充唯一命中，但不能为同一资源叠加多条规则。
            void Check(string name)
            {
                int other = operation.Rules.Match(name, operation.Prefab);
                if (matched >= 0 && other >= 0 && matched != other)
                {
                    throw new ArgumentException("不同地址已证明指向同一 Prefab，但命中多条 Panel 规则。");
                }

                if (other >= 0)
                {
                    matched = other;
                }
            }
            foreach (var state in _states.Values)
            {
                if (IsCurrent(state) && state.Panel.Module == operation.Owner && state.Root == operation.Root && ReferenceEquals(state.Source, operation.Prefab))
                {
                    foreach (var name in state.SourceNames)
                    {
                        Check(name);
                    }
                }
            }

            foreach (var candidate in candidates)
            {
                if (candidate.Prefab == operation.Prefab)
                {
                    Check(candidate.Name);
                }
            }
        }
        /// <summary>仲裁同范围消费者，复用有效 Single 或创建新会话，并分别交付每个消费者的结果。</summary>
        private void Execute(PanelOperation origin)
        {
            var rejected = new List<(PanelOperation Operation, Exception Error)>();
            try
            {
                origin.Validate();
                var candidates = new List<PanelOperation> { origin };
                foreach (var pending in origin.Owner.PendingViews)
                {
                    if (pending is PanelOperation candidate && !ReferenceEquals(candidate, origin) && !candidate.IsTerminal && origin.KnownSame(candidate))
                    {
                        candidates.Add(candidate);
                    }
                }

                candidates.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
                PanelState existing = null;
                foreach (var state in _states.Values)
                {
                    if (IsCurrent(state) && state.Panel.Module == origin.Owner && state.Root == origin.Root && ReferenceEquals(state.Source, origin.Prefab))
                    {
                        existing = state;
                        break;
                    }
                }

                if (!origin.IsAsync && existing == null && candidates.Count > 1)
                {
                    origin.Prepare(origin.Prefab);
                    if (origin.Settings.instanceMode == PanelInstanceMode.Single)
                    {
                        throw new InvalidOperationException("同范围 Single 异步首开仍在途，同步 LoadPanel 不接管其生产或意图。");
                    }
                }
                var valid = new List<PanelOperation>();
                foreach (var candidate in candidates)
                {
                    try
                    {
                        candidate.Prepare(origin.Prefab);
                        ValidateRules(candidate, existing, candidates);
                        ValidateOpen(candidate.Root, candidate.Settings, existing != null && candidate.Settings.instanceMode == PanelInstanceMode.Single ? existing.Panel : null);
                        valid.Add(candidate);
                    }
                    catch (Exception error)
                    {
                        if (!origin.IsAsync && ReferenceEquals(candidate, origin))
                        {
                            throw;
                        }

                        rejected.Add((candidate, error));
                    }
                }
                if (valid.Count == 0)
                {
                    return;
                }

                var mode = existing != null ? existing.Panel.EffectiveSettings.instanceMode : valid[0].Settings.instanceMode;
                foreach (var candidate in valid.ToArray())
                {
                    if (candidate.Settings.instanceMode != mode)
                    {
                        var error = new InvalidOperationException("同范围活动会话或在途请求不能混用 Single/Multiple。");
                        if (!origin.IsAsync && candidate == origin)
                        {
                            throw error;
                        }

                        rejected.Add((candidate, error));
                        valid.Remove(candidate);
                    }
                }

                if (!valid.Contains(origin))
                {
                    return;
                }

                if (mode == PanelInstanceMode.Multiple)
                {
                    // Multiple 只提交当前消费者；其他消费者仍由自己正常调度，不能被 Single 合流丢弃。
                    valid = new List<PanelOperation> { origin };
                    existing = null;
                }
                if (existing != null && (!existing.Panel.IsLoaded || existing.Panel.IsInitializing || existing.Panel.IsAttaching))
                {
                    throw new InvalidOperationException("Single Panel 仍在首开初始化，不能重开半成品会话。");
                }

                // Single 只采用序号最新的有效数据与显示意图；较早消费者仍各自保有交付结果。
                var winner = valid[valid.Count - 1];
                Panel result = existing?.Panel;
                object resultSession = existing?.Session;
                try
                {
                    if (existing != null && !AreResourcesValid(existing))
                    {
                        throw new InvalidOperationException("Single 原会话的实例或来源租约已撤销，不能运行重开数据回调。");
                    }

                    winner.BeginCommit = () =>
                    {
                        foreach (var request in valid)
                        {
                            request.IsCommitted = true;
                        }
                    };
                    winner.BeginFailure = () =>
                    {
                        foreach (var request in valid)
                        {
                            request.MarkFailing();
                        }
                    };
                    if (existing == null)
                    {
                        var productionLease = winner.Lease;
                        if (productionLease == null)
                        {
                            foreach (var request in valid)
                            {
                                if (request.Lease != null)
                                {
                                    productionLease = request.Lease;
                                    break;
                                }
                            }
                        }

                        result = ViewManager.CompleteAsync<Panel>(winner.Owner, winner.Prefab, winner.Name ?? winner.Prefab.name,
                        winner.Root.GetLayer(winner.Settings.layer), winner.Root.gameObject.scene, winner.Args, null,
                        winner.Validate, productionLease, winner);
                        resultSession = result.Session;
                        existing = _states[result];
                    }
                    else if (winner.Sequence > existing.IntentSequence)
                    {
                        if (!IsCurrent(existing))
                        {
                            throw new InvalidOperationException("Single 原会话已结束，不能重开复用引用的新 Session。");
                        }

                        winner.Owner.ViewTransactions++;
                        try
                        {
                            winner.Validate();
                            winner.BeginCommit();
                            result.Open(winner.Args);
                            winner.Validate();
                            if (!IsCurrent(existing) || !result.IsLoaded || !AreResourcesValid(existing))
                            {
                                throw new InvalidOperationException("Panel 重开使原会话或资源责任失效。");
                            }

                            if (!CommitDisplay(existing, winner.Settings, true, winner.Sequence))
                            {
                                throw new InvalidOperationException("切层原生回调结束了原 Panel Session，不能提交旧打开意图。");
                            }

                            existing.IntentSequence = winner.Sequence;
                            Reconcile();
                        }
                        finally
                        {
                            winner.Owner.ViewTransactions--;
                        }
                    }
                    foreach (var operation in valid)
                    {
                        operation.Validate();
                        if (operation.Name != null)
                        {
                            existing.SourceNames.Add(operation.Name);
                        }

                        if (operation.Lease != null && ViewManager.IsDirectLease(result.BackendLease))
                        {
                            existing.SourcePins.Add(operation.Lease);
                            operation.Lease = null;
                            // Foundation 缓存不能接管 provider pin；本 Session 可保活，但 Close 必须 Discard 后释放来源。
                            result.DiscardOnExit = true;
                        }
                        operation.IsCommitted = true;
                    }
                }
                catch (Exception error)
                {
                    var statuses = new Dictionary<PanelOperation, LoadViewStatus>();
                    bool revoked = existing != null && IsCurrent(existing) && !AreResourcesValid(existing);
                    foreach (var operation in valid)
                    {
                        statuses.Add(operation, !revoked && operation.StillValid() ? LoadViewStatus.Failed : LoadViewStatus.Invalidated);
                    }

                    foreach (var operation in valid)
                    {
                        operation.MarkFailing();
                    }

                    if (result && ReferenceEquals(result.Session, resultSession))
                    {
                        result.DiscardOnExit = true;
                        try
                        {
                            result.Close();
                        }
                        catch (Exception cleanup)
                        {
                            StartUpModuleRequest.AttachCleanupError(error, cleanup);
                        }
                    }
                    foreach (var operation in valid)
                    {
                        operation.Finish(null, null, error, statuses[operation]);
                    }

                    if (!origin.IsAsync)
                    {
                        throw;
                    }

                    return;
                }
                // 参数和事务在完成通知前收尾；每个通知前仍复核同一个原 Session，不把复用引用当成功。
                foreach (var operation in valid)
                {
                    try
                    {
                        operation.ReleaseInputs();
                    }
                    catch (Exception error)
                    {
                        operation.Finish(null, null, error, LoadViewStatus.Failed);
                    }
                }
                foreach (var operation in valid)
                {
                    operation.Finish(result, resultSession, null, LoadViewStatus.Succeeded);
                }
            }
            finally
            {
                // 非法消费者的观察通知晚于本次仲裁，不能在预校验途中重入并改变 winner/原 Session。
                foreach (var failure in rejected)
                {
                    failure.Operation.Finish(null, null, failure.Error, LoadViewStatus.Failed);
                }
            }
        }
    }
}
