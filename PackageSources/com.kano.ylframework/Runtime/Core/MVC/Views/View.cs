using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Core.RoslynAnalyzers;
using YLFramework.Events;
using ApplicationTool = YLFramework.Foundation.ApplicationTool;

namespace YLFramework.Core
{
    /// <summary>由 Module 显式纳管的表现组件，以会话区分同一 Unity 实例的多次加载与退出。</summary>
    /// <remarks>
    /// Model 绑定仅借用已完成的注册，不取得数据所有权。
    /// 在 Awake 中通过 RegisterEvent 声明监听；仅加载完成且启用时订阅，停用或退出时退订，重新启用会建立新一轮订阅。
    /// </remarks>
    [DisallowMultipleComponent]
    public abstract class View : MonoBehaviour
    {

        #region 字段

        internal bool IsSessionLoaded = false;

        private Module _module;
        internal object Session;
        internal object LastSession;
        internal int RegisteredId;
        internal bool IsInitializing;
        internal bool IsRemoving;
        internal bool UsesBackend;
        internal GameObject BackendInstance;
        internal CoreInstanceLease BackendLease;
        internal GameObject DirectSource;
        internal View BackendRoot;
        internal object BackendRootSession;
        internal int SceneHandle;
        internal bool IsAttaching;
        internal bool DiscardOnExit;
        private Model _boundModel;
        private Module _modelOwner;
        private object _modelRegistration;
        internal bool IsUnbinding;
        private bool _hasRegisteredEvents;
        private long _eventEpoch;
        private List<IUnregister> _eventSubscriptions;

        #endregion

        #region 属性

        /// <summary>
        /// 当前注册使用的 Unity 实例 ID；无会话时直接读取组件实例 ID，不作为跨实例的业务 ID。
        /// </summary>
        public int Id => Session != null ? RegisteredId : GetInstanceID();

        /// <summary>
        /// 当前表现会话的所属模块；场景层级或父对象不会自动决定归属，解除注册后为 null。
        /// </summary>
        public Module Module
        {
            get
            {
                return _module;
            }
            internal set
            {
                _module = value;
            }
        }


        /// <summary>
        /// 预制体名称
        /// </summary>
        internal string AssetName
        {
            get; set;
        }

        /// <summary>
        /// 当前有效的主要Model绑定；解绑或Model注册身份失效后为null，不取得Model所有权。
        /// </summary>
        public Model Model => _boundModel != null &&
            ReferenceEquals(_boundModel.Module, _modelOwner) &&
            ReferenceEquals(_boundModel.Registration, _modelRegistration) &&
            !_boundModel.IsInitializing && !_boundModel.IsExiting &&
            ReferenceEquals(ModelManager.GetModel(_modelOwner, _boundModel.GetType(), _boundModel.Id), _boundModel)
            ? _boundModel : null;

        internal bool HasValidModelBinding => _boundModel == null || Model != null;

        /// <summary>当前表现会话已完成加载；退出期间为false。</summary>
        public bool IsLoaded => IsSessionLoaded;

        /// <summary>绑定已完成注册的Model，可以显式借用其他Module的数据，不自动注册或移除。</summary>
        protected void BindModel(Model model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (Session == null || (!IsSessionLoaded && !IsInitializing) || IsRemoving || IsUnbinding)
            {
                throw new InvalidOperationException("Cannot bind a Model outside an active View session.");
            }

            if (model.Module == null || model.Registration == null || model.IsInitializing || model.IsExiting ||
                !ReferenceEquals(ModelManager.GetModel(model.Module, model.GetType(), model.Id), model))
            {
                throw new InvalidOperationException("Model must have a completed registration.");
            }

            if (_boundModel != null)
            {
                if (ReferenceEquals(Model, model))
                {
                    return;
                }

                throw new InvalidOperationException("Unbind the previous Model before binding another registration.");
            }
            _boundModel = model;
            _modelOwner = model.Module;
            _modelRegistration = model.Registration;
        }

        /// <summary>先结束框架绑定，再尝试业务解绑；不改变Model生命周期。</summary>
        protected void UnbindModel()
        {
            if (IsUnbinding || _boundModel == null)
            {
                return;
            }

            var previous = _boundModel;
            var owner = Module;
            _boundModel = null;
            _modelOwner = null;
            _modelRegistration = null;
            IsUnbinding = true;
            if (owner != null)
            {
                owner.ViewTransactions++;
            }

            try
            {
                OnModelUnbound(previous);
            }
            catch
            {
                DiscardOnExit = true;
                throw;
            }
            finally
            {
                IsUnbinding = false;
                if (owner != null)
                {
                    owner.ViewTransactions--;
                }
            }
        }

        /// <summary>清理业务自己的Model监听；previousModel可能已退出。</summary>
        protected virtual void OnModelUnbound(Model previousModel)
        {
        }

        #endregion

        #region 事件

        /// <summary>已加载会话移除时的实例内通知；发生在解绑及索引撤销前，退出后清空，不沿用到下一会话。</summary>
        [Tooltip("View被移除时触发")]
        public Action ViewRemoved;

        #endregion

        #region 生命周期

        /// <summary>保留派生类的 Unity 启动扩展点，不在此根据层级自动纳管 View。</summary>
        [MustCallBase("在重写Start方法时,请通过 base.Start() 调用父类中的Start方法!")]
        protected virtual void Start()
        {
        }

        /// <summary>运行期销毁时结束仍有效的表现会话；初始化中的销毁交由持有事务的加载入口回滚。</summary>
        private void OnDestroy()
        {
            if (!ApplicationTool.IsPlaying)
            {
                return;
            }
            // 同步初始化事务持有取得实例，并在每个提交边界检查存活及完成回滚。
            if (IsInitializing || IsAttaching)
            {
                return;
            }

            if (Module != null && Session != null)
            {
                ViewManager.RemoveView(Module, this, Session, true);
            }
        }


        /// <summary>启用时尝试恢复已加载会话的事件；早于加载发生时暂不订阅。</summary>
        private void OnEnable()
        {
            RegisterEvents();
        }

        /// <summary>停用时结束当前订阅轮次，不移除表现会话或 Model 绑定。</summary>
        private void OnDisable()
        {
            UnregisterEvents();
        }

        #endregion

        #region 方法

        /// <summary>Loaded且active时调用；首次激活可能早于加载，业务上下文应在此处使用。</summary>
        protected virtual void OnViewEnabled()
        {
        }

        /// <summary>暂停已开放的表现事件，不结束owner或Model绑定；与OnViewEnabled配对。</summary>
        protected virtual void OnViewDisabled()
        {
        }

        /// <summary>
        /// 结束当前表现会话，不移除Model；框架取得的健康实例归还缓存，其他实例仅隐藏。
        /// </summary>
        public void Close()
        {
            if (this is Panel panel && panel.OpeningSession != null && ReferenceEquals(panel.OpeningSession, Session))
            {
                throw new InvalidOperationException("OnOpen 期间不能关闭当前 Panel。");
            }

            if (Session == null || Module == null)
            {
                return;
            }

            ViewManager.RemoveView(Module, this, Session);
        }

        /// <summary>
        /// 会话和实例收尾已提交后调用，可开启新会话；若移除广播已复用该组件，旧完成钩子不再派发。
        /// </summary>
        protected virtual void OnClosed()
        {
        }
        internal void NotifyClosed() => OnClosed();
        internal void DetachModel() => UnbindModel();
        internal void StopEvents() => UnregisterEvents();
        internal void ResumeEvents() => RegisterEvents();


        /// <summary>
        /// 初始化中的旧版参数回调，先于 LoadViewArgs 回调执行；返回后仍须通过会话有效性检查。
        /// </summary>
        /// <param name="param">加载View时传递的参数</param>
        public virtual void OnLoaded(params object[] param)
        {
        }


        /// <summary>仅在本回调期间借读参数；不得保存容器、修改或再次传入加载入口。</summary>
        public virtual void OnLoaded(LoadViewArgs args)
        {
        }

        /// <summary>
        /// 正常退出或初始化失败回滚时执行的业务清理钩子；此时已停止表现事件，但归属尚未撤销。
        /// </summary>
        public virtual void OnRemove()
        {

        }


        internal virtual void BeforeOnLoaded()
        {
        }

        #endregion


        #region 事件注册

        private readonly List<(EventKey Key, Type Signature, Func<Func<bool>, IUnregister> Subscribe)> _eventDeclarations = new();

        /// <summary>
        /// 声明 string 键的无参监听，供表现会话启用时订阅。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent(string eventName, Action callback)
        {
            DeclareEvent(eventName, callback, (target, current) => EventBus.Default.Register(eventName, () =>
            {
                if (current())
                {
                    target();
                }
            }));
        }

        /// <summary>
        /// 声明 int 键的无参监听，供表现会话启用时订阅。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent(int eventId, Action callback)
        {
            DeclareEvent(eventId, callback, (target, current) => EventBus.Default.Register(eventId, () =>
            {
                if (current())
                {
                    target();
                }
            }));
        }

        /// <summary>
        /// 声明 string 键的单参监听，参数类型须与该键的通道签名一致。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1>(string eventName, Action<T1> callback)
        {
            DeclareEvent(eventName, callback, (target, current) => EventBus.Default.Register<T1>(eventName, p1 =>
            {
                if (current())
                {
                    target(p1);
                }
            }));
        }

        /// <summary>
        /// 声明 int 键的单参监听，参数类型须与该键的通道签名一致。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1>(int eventId, Action<T1> callback)
        {
            DeclareEvent(eventId, callback, (target, current) => EventBus.Default.Register<T1>(eventId, p1 =>
            {
                if (current())
                {
                    target(p1);
                }
            }));
        }

        /// <summary>
        /// 声明 string 键的双参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2>(string eventName, Action<T1, T2> callback)
        {
            DeclareEvent(eventName, callback, (target, current) => EventBus.Default.Register<T1, T2>(eventName, (p1, p2) =>
            {
                if (current())
                {
                    target(p1, p2);
                }
            }));
        }

        /// <summary>
        /// 声明 int 键的双参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2>(int eventId, Action<T1, T2> callback)
        {
            DeclareEvent(eventId, callback, (target, current) => EventBus.Default.Register<T1, T2>(eventId, (p1, p2) =>
            {
                if (current())
                {
                    target(p1, p2);
                }
            }));
        }

        /// <summary>
        /// 声明 string 键的三参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3>(string eventName, Action<T1, T2, T3> callback)
        {
            DeclareEvent(eventName, callback, (target, current) => EventBus.Default.Register<T1, T2, T3>(eventName, (p1, p2, p3) =>
            {
                if (current())
                {
                    target(p1, p2, p3);
                }
            }));
        }

        /// <summary>
        /// 声明 int 键的三参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3>(int eventId, Action<T1, T2, T3> callback)
        {
            DeclareEvent(eventId, callback, (target, current) => EventBus.Default.Register<T1, T2, T3>(eventId, (p1, p2, p3) =>
            {
                if (current())
                {
                    target(p1, p2, p3);
                }
            }));
        }

        /// <summary>
        /// 声明 string 键的四参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3, T4>(string eventName, Action<T1, T2, T3, T4> callback)
        {
            DeclareEvent(eventName, callback, (target, current) => EventBus.Default.Register<T1, T2, T3, T4>(eventName, (p1, p2, p3, p4) =>
            {
                if (current())
                {
                    target(p1, p2, p3, p4);
                }
            }));
        }

        /// <summary>
        /// 声明 int 键的四参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3, T4>(int eventId, Action<T1, T2, T3, T4> callback)
        {
            DeclareEvent(eventId, callback, (target, current) => EventBus.Default.Register<T1, T2, T3, T4>(eventId, (p1, p2, p3, p4) =>
            {
                if (current())
                {
                    target(p1, p2, p3, p4);
                }
            }));
        }

        /// <summary>
        /// 声明 string 键的五参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3, T4, T5>(string eventName, Action<T1, T2, T3, T4, T5> callback)
        {
            DeclareEvent(eventName, callback, (target, current) => EventBus.Default.Register<T1, T2, T3, T4, T5>(eventName, (p1, p2, p3, p4, p5) =>
            {
                if (current())
                {
                    target(p1, p2, p3, p4, p5);
                }
            }));
        }

        /// <summary>
        /// 声明 int 键的五参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3, T4, T5>(int eventId, Action<T1, T2, T3, T4, T5> callback)
        {
            DeclareEvent(eventId, callback, (target, current) => EventBus.Default.Register<T1, T2, T3, T4, T5>(eventId, (p1, p2, p3, p4, p5) =>
            {
                if (current())
                {
                    target(p1, p2, p3, p4, p5);
                }
            }));
        }

        /// <summary>
        /// 声明 string 键的六参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3, T4, T5, T6>(string eventName, Action<T1, T2, T3, T4, T5, T6> callback)
        {
            DeclareEvent(eventName, callback, (target, current) => EventBus.Default.Register<T1, T2, T3, T4, T5, T6>(eventName, (p1, p2, p3, p4, p5, p6) =>
            {
                if (current())
                {
                    target(p1, p2, p3, p4, p5, p6);
                }
            }));
        }

        /// <summary>
        /// 声明 int 键的六参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3, T4, T5, T6>(int eventId, Action<T1, T2, T3, T4, T5, T6> callback)
        {
            DeclareEvent(eventId, callback, (target, current) => EventBus.Default.Register<T1, T2, T3, T4, T5, T6>(eventId, (p1, p2, p3, p4, p5, p6) =>
            {
                if (current())
                {
                    target(p1, p2, p3, p4, p5, p6);
                }
            }));
        }

        /// <summary>
        /// 声明 string 键的七参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3, T4, T5, T6, T7>(string eventName, Action<T1, T2, T3, T4, T5, T6, T7> callback)
        {
            DeclareEvent(eventName, callback, (target, current) => EventBus.Default.Register<T1, T2, T3, T4, T5, T6, T7>(eventName, (p1, p2, p3, p4, p5, p6, p7) =>
            {
                if (current())
                {
                    target(p1, p2, p3, p4, p5, p6, p7);
                }
            }));
        }

        /// <summary>
        /// 声明 int 键的七参监听，参数类型及顺序共同确定签名。
        /// </summary>
        [RestrictedCall("Awake")]
        protected void RegisterEvent<T1, T2, T3, T4, T5, T6, T7>(int eventId, Action<T1, T2, T3, T4, T5, T6, T7> callback)
        {
            DeclareEvent(eventId, callback, (target, current) => EventBus.Default.Register<T1, T2, T3, T4, T5, T6, T7>(eventId, (p1, p2, p3, p4, p5, p6, p7) =>
            {
                if (current())
                {
                    target(p1, p2, p3, p4, p5, p6, p7);
                }
            }));
        }

        /// <summary>校验声明签名并保存逐目标的订阅工厂，不在声明时注册全局监听。</summary>
        private void DeclareEvent<TCallback>(EventKey key, TCallback callback, Func<TCallback, Func<bool>, IUnregister> subscribe)
            where TCallback : Delegate
        {
            key.Validate();
            if (callback == null)
            {
                return;
            }

            foreach (var declaration in _eventDeclarations)
            {
                if (declaration.Key == key && declaration.Signature != typeof(TCallback))
                {
                    throw new InvalidOperationException($"View event key {key} is declared as {declaration.Signature}, not {typeof(TCallback)}.");
                }
            }

            // 在声明时展开多播，确保每个业务回调都检查会话；不在发布路径反射分发。
            foreach (TCallback target in callback.GetInvocationList())
            {
                _eventDeclarations.Add((key, typeof(TCallback), current => subscribe(target, current)));
            }
        }


        /// <summary>为可用表现会话建立一轮精确订阅，并在本轮激活失败时撤销对应监听。</summary>
        private void RegisterEvents()
        {
            if (!IsSessionLoaded || IsRemoving || !isActiveAndEnabled || _hasRegisteredEvents || (this is Panel panel && panel.DefersEvents))
            {
                return;
            }

            var session = Session;
            long epoch = ++_eventEpoch;
            _hasRegisteredEvents = true;
            var subscriptions = new List<IUnregister>();
            _eventSubscriptions = subscriptions;
            // 退订不能撤回 EventBus 的在途快照，业务回调还须核对会话与订阅轮次，避免旧快照进入重开的 View。
            bool IsCurrent() => this && _hasRegisteredEvents && ReferenceEquals(Session, session) && _eventEpoch == epoch &&
                IsSessionLoaded && !IsRemoving && isActiveAndEnabled;
            try
            {
                foreach (var declaration in _eventDeclarations)
                {
                    subscriptions.Add(declaration.Subscribe(IsCurrent));
                }

                PanelManager.ActivateView(this);
                if (!IsCurrent())
                {
                    return;
                }

                OnViewEnabled();
            }
            catch (Exception error)
            {
                // 钩子可结束旧会话或重开订阅轮次，旧异常不能退订或污染新的监听。
                if (ReferenceEquals(Session, session) && _eventEpoch == epoch)
                {
                    DiscardOnExit = true;
                    try
                    {
                        UnregisterEvents();
                    }
                    catch (Exception cleanupError)
                    {
                        StartUpModuleRequest.AttachCleanupError(error, cleanupError);
                    }
                }
                else
                {
                    // 激活钩子可能已重开；失败只取消旧轮取得的精确身份。
                    try
                    {
                        SubscriptionCleanup.ReleaseAll(subscriptions, token => token.Unregister());
                    }
                    catch (Exception cleanupError)
                    {
                        StartUpModuleRequest.AttachCleanupError(error, cleanupError);
                    }
                }
                throw;
            }
        }


        /// <summary>先使旧轮次失效，再逐项退订及通知停用；重入建立的新轮次不接收旧停用钩子。</summary>
        private void UnregisterEvents()
        {
            if (!_hasRegisteredEvents)
            {
                return;
            }

            var session = Session;
            long epoch = ++_eventEpoch;
            _hasRegisteredEvents = false;
            var subscriptions = _eventSubscriptions;
            _eventSubscriptions = null;
            var errors = new List<Exception>();
            foreach (var subscription in subscriptions)
            {
                try
                {
                    subscription.Unregister();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            try
            {
                PanelManager.DeactivateView(this);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            try
            {
                if (ReferenceEquals(Session, session) && _eventEpoch == epoch)
                {
                    OnViewDisabled();
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            if (errors.Count != 0 && ReferenceEquals(Session, session) && _eventEpoch == epoch)
            {
                DiscardOnExit = true;
            }

            ViewManager.ThrowCleanupErrors(errors);
        }

        #endregion


    }
}
