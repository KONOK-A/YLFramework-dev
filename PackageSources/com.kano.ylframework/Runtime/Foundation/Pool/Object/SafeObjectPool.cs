using System;
using YLFramework.Singletons;

namespace YLFramework.Pooling
{
    /// <summary>
    /// IOfPool 的类型化单例入口，缓存与真实所有权由统一内核负责。
    /// IsRecycled 仅为领域可观察状态，不是池身份或租用代次。
    /// </summary>
    public class SafeObjectPool<T> : IPool<T>, IDisposable, ISingleton where T : class, IOfPool, new()
    {
        private readonly ObjectPool<T> _pool;
        /// <summary>后续冷创建使用的领域工厂；改动不会重新初始化已登记对象。</summary>
        protected IObjectFactory<T> ObjectFactory;
        private static WeakReference<SafeObjectPool<T>> _sessionPool;

        /// <summary>为当前封闭类型登记会话清理，使禁用 Domain Reload 时也能关闭旧池。</summary>
        static SafeObjectPool()
        {
            SingletonSessionState.RegisterSessionReset(ResetSession);
        }

        /// <summary>摘除会话弱引用并关闭原池，借出对象仍须向原池归还。</summary>
        private static void ResetSession()
        {
            // HasInstance 可先触发 SingletonProperty 注册；关闭不依赖其静态引用尚未摘除。
            var original = _sessionPool;
            _sessionPool = null;
            if (original != null && original.TryGetTarget(out var pool))
            {
                pool.Dispose();
            }
        }

        /// <summary>记录本轮单例的弱引用，供会话重置关闭准确的原实例。</summary>
        void ISingleton.OnSingletonInit() => _sessionPool = new WeakReference<SafeObjectPool<T>>(this);

        /// <summary>按需取得当前类型的池单例；旧池关闭后的新入口不接纳旧池的借出对象。</summary>
        public static SafeObjectPool<T> Instance => SingletonProperty<SafeObjectPool<T>>.Instance;

        /// <summary>装配默认无参工厂与领域生命周期回调，缓存和借用身份交由普通对象池管理。</summary>
        protected SafeObjectPool()
        {
            ObjectFactory = new DefaultObjectFactory<T>();
            _pool = new ObjectPool<T>(Create, OnAllocate, Reset, OnDiscard);
        }

        /// <summary>当前空闲数量，不包含借出及转换期对象。</summary>
        public int CurStoreCount => _pool.CurStoreCount;

        /// <summary>空闲容量上限，默认 20，非正值不限；缩容不重复 OnRecycle。</summary>
        public int MaxPoolSize
        {
            get => _pool.MaxPoolSize; set => _pool.MaxPoolSize = value;
        }

        /// <summary>替换后续冷创建的工厂，不改变已借用对象的生命周期责任。</summary>
        public void SetObjectFactory(IObjectFactory<T> factory)
        {
            _pool.SetFactoryMethod(Create);
            ObjectFactory = factory;
        }

        /// <summary>使用委托配置后续冷创建。</summary>
        public void SetFactoryMethod(Func<T> factoryMethod) => SetObjectFactory(new CustomObjectFactory<T>(factoryMethod));

        /// <summary>通过当前领域工厂冷创建对象；未配置工厂时抛出异常，不负责执行取得回调。</summary>
        /// <returns>工厂结果，允许为 null。</returns>
        protected virtual T Create()
        {
            if (ObjectFactory == null)
            {
                throw new InvalidOperationException($"[Pool] {typeof(T).FullName} 尚未设置对象工厂。");
            }

            return ObjectFactory.Create();
        }

        /// <summary>冷/热取得均执行 OnAllocate，成功后才交付对象。</summary>
        public virtual T Allocate() => _pool.Allocate();

        /// <summary>工厂返回 null 时为 false，工厂及回调异常仍传播。</summary>
        public bool TryAllocate(out T item) => _pool.TryAllocate(out item);

        /// <summary>
        /// 只归还本池借出对象。false 仅表示未缓存，不代表可继续使用或重试；
        /// 外来对象抛错，重复/转换期归还拒绝，失败对象永久隔离。
        /// </summary>
        public virtual bool Recycle(T item) => _pool.Recycle(item);

        /// <summary>结束借用并执行 OnRecycle，但不缓存；不重复清理已归还对象。</summary>
        public void Discard(T item) => _pool.Discard(item);

        /// <summary>按目标空闲数量预热，默认执行 OnRecycle，返回实际新增缓存数。</summary>
        /// <param name="count">期望的空闲总数，不是本次创建数量；受正容量上限裁剪。</param>
        /// <returns>本次实际新增的缓存数。</returns>
        public int Prewarm(int count) => _pool.Prewarm(count);

        /// <summary>Raw 只登记及标记空闲，不执行 OnAllocate/OnRecycle。</summary>
        /// <param name="count">期望的空闲总数，不是本次创建数量；受正容量上限裁剪。</param>
        /// <param name="mode">新对象入缓存前是否执行领域回收清理。</param>
        /// <returns>本次实际新增的缓存数。</returns>
        public int Prewarm(int count, PoolPrewarmMode mode) => _pool.Prewarm(count, mode, PrepareRaw);

        /// <summary>摘除空闲快照后逐项清理；附加回调错误聚合报告，不触碰借出对象。</summary>
        public virtual void Clear(Action<T> itemClearCallback = null) => _pool.Clear(itemClearCallback);

        /// <summary>初始化空闲容量及目标数预热，非正容量不限。</summary>
        /// <param name="maxSize">空闲缓存上限，非正值表示不限。</param>
        /// <param name="initialCount">期望的空闲总数；已有缓存计入目标，非正数不补齐。</param>
        public void Init(int maxSize, int initialCount)
        {
            MaxPoolSize = maxSize;
            Prewarm(initialCount);
        }

        /// <summary>为 Raw 预热的新对象设置空闲标记，不调用领域生命周期回调。</summary>
        protected virtual void PrepareRaw(T item) => item.IsRecycled = true;

        /// <summary>先清除回收标记，再通知领域对象准备本次借用。</summary>
        protected virtual void OnAllocate(T item)
        {
            item.IsRecycled = false;
            item.OnAllocate();
        }

        /// <summary>在回收标记为 true 时执行领域清理，并在回调结束或失败后恢复该标记。</summary>
        protected virtual void Reset(T item)
        {
            item.IsRecycled = true;
            try
            {
                item.OnRecycle();
            }
            finally
            {
                item.IsRecycled = true;
            }
        }

        /// <summary>永久退出缓存时只设置回收标记，不补调 OnRecycle 或 Dispose。</summary>
        protected virtual void OnDiscard(T item) => item.IsRecycled = true;

        /// <summary>
        /// 关闭原实例后，仅摘除仍指向自己的静态入口。旧引用不能关闭新单例；
        /// 旧借用须归还原池，只清理不缓存，新静态池不会接纳旧借用。
        /// </summary>
        public virtual void Dispose()
        {
            try
            {
                _pool.Dispose();
            }
            finally
            {
                ObjectFactory = null;
                if (_sessionPool != null && _sessionPool.TryGetTarget(out var original) && ReferenceEquals(original, this))
                {
                    _sessionPool = null;
                }

                if (SingletonProperty<SafeObjectPool<T>>.TryGetInstance(out var current) && ReferenceEquals(current, this))
                {
                    SingletonProperty<SafeObjectPool<T>>.Dispose();
                }
            }
        }
    }
}
