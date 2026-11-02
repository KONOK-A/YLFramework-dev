using System;
using UnityEngine;

namespace YLFramework.Singletons
{
    /// <summary>
    /// 可选的单例宿主基类：支持从场景（含未激活对象）收养实例，并按所有权区分“自建宿主”与“借用组件”的释放行为。
    /// 模块必须在 <see cref="OnHostRelease"/> 中清理自身资源——宿主销毁流程会先同步调用该钩子。
    /// 该类不改变 MonoSingleton 的默认行为，仅在显式选择使用时生效。
    /// </summary>
    /// <typeparam name="T">单例类型（继承时传入自身）。</typeparam>
    public abstract class MonoSingletonHost<T> : MonoBehaviour, ISingleton where T : MonoSingletonHost<T>
    {
        private static T _instance;
        private static bool _isQuitting;
        private bool _ownsGameObject;
        private bool _isInitialized;

        /// <summary>
        /// 宿主是否已释放；释放后 HasInstance 为 false，模块应停止使用该宿主。
        /// </summary>
        protected bool IsHostReleased
        {
            get; private set;
        }

        /// <summary>
        /// 宿主 GameObject 是否由本类创建：true 时释放会销毁整个 GameObject，false（借用组件）时只销毁或禁用组件。
        /// </summary>
        protected bool OwnsHostGameObject => _ownsGameObject;

        /// <summary>
        /// 当前实例是否就是缓存的宿主。
        /// </summary>
        protected bool IsCurrentHost => ReferenceEquals(_instance, this);

        /// <summary>
        /// 缓存的宿主引用；在同步释放钩子返回前仍可见，便于模块清理逻辑访问。
        /// </summary>
        protected static T CachedHost => _instance;

        /// <summary>
        /// 自建宿主的父节点；默认 null（顶级节点，按 <see cref="PersistentHost"/> 决定是否常驻）。
        /// </summary>
        protected virtual Transform HostParent => null;

        /// <summary>
        /// 自建宿主是否跨场景常驻（仅播放模式下生效），默认 true。
        /// </summary>
        protected virtual bool PersistentHost => true;

        /// <summary>为当前宿主类型登记跨 Play 会话的引用和退出标记重置。</summary>
        static MonoSingletonHost()
        {
            SingletonSessionState.RegisterSessionReset(ResetHostSession);
        }

        /// <summary>
        /// 单例实例；首次访问时收养场景实例或创建宿主。
        /// </summary>
        public static T Instance => GetInstance();

        /// <summary>
        /// 是否持有有效（未释放）的宿主；不会触发查找或创建。
        /// </summary>
        public static bool HasInstance => _instance != null && !_instance.IsHostReleased;

        /// <summary>
        /// 尝试获取有效宿主；不会触发查找或创建。
        /// </summary>
        public static bool TryGetInstance(out T instance)
        {
            instance = HasInstance ? _instance : null;
            return instance != null;
        }

        /// <summary>
        /// 查找或创建宿主：优先复用缓存；否则收养场景中的实例，同一时刻只保留一个（多余的未释放宿主会调用 ReleaseHost）；
        /// 仍无则创建新宿主（创建方式可由 createGameObject 自定义，为 null 时新建以类型名命名的 GameObject 并挂载 T）。
        /// 非播放模式下默认返回 null，allowEditMode 为 true 时允许。
        /// </summary>
        /// <param name="allowEditMode">非播放模式下是否允许查找/创建。</param>
        /// <param name="createGameObject">自定义宿主 GameObject 的创建方式；null 时使用默认创建。</param>
        /// <param name="includeInactive">查找时是否包含未激活对象，默认 true。</param>
        /// <returns>宿主实例；不可用时返回 null。</returns>
        protected static T GetInstance(bool allowEditMode = false, Func<GameObject> createGameObject = null,
            bool includeInactive = true)
        {
            if (HasInstance)
            {
                return InitializeHost(_instance);
            }

            ReleaseDestroyedHost();
            if (!CanCreateHost(allowEditMode))
            {
                return null;
            }

            foreach (var candidate in FindObjectsByType<T>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                         FindObjectsSortMode.InstanceID))
            {
                if (candidate.IsHostReleased)
                {
                    continue;
                }

                if (_instance == null)
                {
                    _instance = candidate;
                }
                else if (candidate != _instance)
                {
                    candidate.ReleaseHost();
                }
            }
            if (!HasInstance)
            {
                var root = createGameObject == null ? new GameObject(typeof(T).Name) : createGameObject();
                var host = createGameObject == null ? root.AddComponent<T>() : root.GetComponent<T>();
                host._ownsGameObject = true;
                _instance = host;
                var parent = host.HostParent;
                if (parent != null)
                {
                    root.transform.SetParent(parent, false);
                }
                else if (Application.isPlaying && host.PersistentHost)
                {
                    DontDestroyOnLoad(root);
                }
            }
            return InitializeHost(_instance);
        }

        /// <summary>
        /// 当前是否允许创建宿主：播放中受退出状态限制，非播放中由 allowEditMode 决定。
        /// </summary>
        protected static bool CanCreateHost(bool allowEditMode = false) =>
            Application.isPlaying ? !_isQuitting && !SingletonSessionState.IsApplicationQuitting : allowEditMode;

        /// <summary>
        /// 首次取得宿主时执行 OnSingletonInit；先置初始化标记再回调，允许回调中重入 Instance；
        /// 回调失败时回滚标记并向上抛出，允许之后重试。
        /// </summary>
        private static T InitializeHost(T host)
        {
            if (!host._isInitialized)
            {
                // 先置位再回调，保证初始化过程中重入 Instance 不会递归初始化
                host._isInitialized = true;
                try
                {
                    host.OnSingletonInit();
                }
                catch
                {
                    host._isInitialized = false;
                    throw;
                }
            }
            return host;
        }

        /// <summary>
        /// 宿主初始化回调；仅在首次取得宿主时调用一次（初始化失败会回滚标记，允许重试）。
        /// </summary>
        public virtual void OnSingletonInit()
        {
        }

        /// <summary>
        /// 激活时登记：若已有其他缓存实例，则释放自己（后激活的让位于先登记者）。
        /// </summary>
        protected virtual void Awake()
        {
            if (!Application.isPlaying || IsHostReleased)
            {
                return;
            }

            ReleaseDestroyedHost();
            if (HasInstance && _instance != this)
            {
                ReleaseHost();
            }
            else
            {
                _instance = this as T;
            }
        }

        /// <summary>
        /// 释放当前宿主：标记已释放、禁用组件、调用 <see cref="OnHostRelease"/>，然后按所有权销毁
        /// （自建宿主销毁整个 GameObject；借用组件只销毁组件）。
        /// 重复调用安全；不会影响替换实例。
        /// </summary>
        /// <param name="keepBorrowedComponent">true 且宿主为借用组件时，只清空缓存，不销毁组件。</param>
        protected void ReleaseHost(bool keepBorrowedComponent = false)
        {
            if (IsHostReleased)
            {
                return;
            }

            if (keepBorrowedComponent && !_ownsGameObject)
            {
                ClearIfCurrent();
                return;
            }
            IsHostReleased = true;
            if (this != null)
            {
                enabled = false;
            }

            try
            {
                OnHostRelease();
            }
            finally
            {
                // 先清缓存引用再销毁：Destroy 可能同步触发外部回调，此时 HasInstance 应已为 false
                ClearIfCurrent();
                if (this != null)
                {
                    var target = _ownsGameObject ? (UnityEngine.Object)gameObject : this;
                    if (Application.isPlaying)
                    {
                        Destroy(target);
                    }
                    else
                    {
                        DestroyImmediate(target);
                    }
                }
            }
        }

        /// <summary>
        /// 宿主释放时的资源清理钩子；在销毁 GameObject / 组件之前同步调用，模块必须在此清理自身资源。
        /// 钩子抛异常时异常向上传播，但清引用与销毁流程仍会继续（由 finally 保证）。
        /// </summary>
        protected virtual void OnHostRelease()
        {
        }

        /// <summary>
        /// 外部销毁宿主时调用：若尚未释放则先执行 <see cref="OnHostRelease"/>，最后清空缓存引用。
        /// </summary>
        protected virtual void OnDestroy()
        {
            try
            {
                if (!IsHostReleased)
                {
                    IsHostReleased = true;
                    OnHostRelease();
                }
            }
            finally
            {
                ClearIfCurrent();
            }
        }

        /// <summary>
        /// 应用退出时标记退出状态，退出期间不再创建新宿主。
        /// </summary>
        protected virtual void OnApplicationQuit() => _isQuitting = true;

        /// <summary>
        /// 清理已被 Unity 销毁（fake-null）但缓存仍持有的宿主引用。
        /// 从未激活过的对象不会收到 OnDestroy，因此需要在访问时兜底释放。
        /// </summary>
        private static void ReleaseDestroyedHost()
        {
            // Unity 不会给从未激活过的对象发送 OnDestroy
            if (!ReferenceEquals(_instance, null) && _instance == null)
            {
                _instance.ReleaseHost();
            }
        }

        /// <summary>为新 Play 会话兜底释放已销毁宿主，并确保缓存和退出标记最终被重置。</summary>
        private static void ResetHostSession()
        {
            try
            {
                ReleaseDestroyedHost();
            }
            finally
            {
                ResetHostReference();
            }
        }

        /// <summary>仅当自身仍为当前宿主时清空缓存，避免旧对象的迟到清理移除替换实例。</summary>
        private void ClearIfCurrent()
        {
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        }

        /// <summary>
        /// 清空宿主缓存引用与退出标记，不清理模块资源；模块资源由各模块自己的会话清理逻辑负责。
        /// </summary>
        protected static void ResetHostReference()
        {
            _instance = null;
            _isQuitting = false;
        }
    }
}
