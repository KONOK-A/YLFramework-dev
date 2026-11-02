using System;
using YLFramework.Pooling;
using YLFramework.Events;

namespace YLFramework.Binding
{
    /// <summary>
    /// 单源派生只读属性：源属性变化时用 selector 重新计算，并按 BindableProperty 的相同值规则通知自己的监听者。
    /// 订阅由自身持有，Dispose（或 IUnregister.Unregister / RecycleToCache）后停止更新，重复释放安全。
    /// </summary>
    /// <typeparam name="TSource">源属性值类型。</typeparam>
    /// <typeparam name="TTarget">映射后的值类型。</typeparam>
    public sealed class MappedProperty<TSource, TTarget> : IReadOnlyBindableProperty<TTarget>, IUnregister, IDisposable
    {
        private readonly BindableProperty<TTarget> _target = new();
        private readonly Func<TSource, TTarget> _selector;
        private IUnregister _sourceSubscription;
        private bool _isDisposed;

        /// <summary>
        /// 创建映射属性：立即计算初始值再订阅源属性（selector 抛异常时不会留下半初始化订阅）。
        /// 返回对象的释放责任归调用方，使用完毕后调用 <see cref="Dispose"/> 解除对源的订阅。
        /// </summary>
        /// <param name="source">源属性；不能为 null。</param>
        /// <param name="selector">源值到目标值的映射委托；不能为 null。</param>
        /// <exception cref="ArgumentNullException">source 或 selector 为 null。</exception>
        public MappedProperty(IReadOnlyBindableProperty<TSource> source, Func<TSource, TTarget> selector)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (selector == null)
            {
                throw new ArgumentNullException(nameof(selector));
            }

            _selector = selector;
            // 先计算初始值再订阅：selector 抛异常时不会留下半初始化订阅
            _target.SetValueWithoutCallback(selector(source.Value));
            _sourceSubscription = source.Register(OnSourceValueChanged);
        }

        /// <summary>
        /// 当前映射值；由 selector 在源值变化时重新计算。
        /// </summary>
        public TTarget Value => _target.Value;

        /// <summary>
        /// 是否已释放；释放后不再响应源属性变化。
        /// </summary>
        public bool IsDisposed => _isDisposed;

        /// <summary>
        /// 注册目标值变化监听，返回用于注销本次注册的句柄；语义与 BindableProperty 一致。
        /// </summary>
        public IUnregister Register(Action<TTarget> onValueChanged) => _target.Register(onValueChanged);

        /// <summary>
        /// 先用当前映射值同步回调一次，成功后再注册目标值变化监听。
        /// </summary>
        public IUnregister RegisterWithInitValue(Action<TTarget> onValueChanged) =>
            _target.RegisterWithInitValue(onValueChanged);

        /// <summary>
        /// 只监听一次目标值变化，在用户回调执行前消费注册，避免嵌套通知重复执行。
        /// </summary>
        public IUnregister RegisterOnce(Action<TTarget> onValueChanged) => _target.RegisterOnce(onValueChanged);

        /// <summary>
        /// 按委托注销普通目标值监听；未注册时为空操作，单次监听须通过其返回句柄取消。
        /// </summary>
        public void Unregister(Action<TTarget> onValueChanged) => _target.Unregister(onValueChanged);

        /// <summary>
        /// 注销全部目标值监听（不解除对源属性的订阅，源变化仍会更新内部值）。
        /// </summary>
        public void UnregisterAll() => _target.UnregisterAll();

        /// <summary>
        /// 不改变值，按当前映射值强制触发一次通知；释放后仍可通知保留的目标监听。
        /// </summary>
        public void ForceNotify() => _target.ForceNotify();

        /// <summary>
        /// 解除对源属性的订阅；重复调用安全。释放后已有目标监听保留，但不会再收到源驱动的更新。
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _sourceSubscription?.Unregister();
            _sourceSubscription = null;
        }

        /// <summary>
        /// 返回当前映射值的字符串表示；值为 null 时返回空字符串。
        /// </summary>
        public override string ToString() => _target.ToString();

        /// <summary>通过统一注销契约释放源订阅，不清除目标监听。</summary>
        void IUnregister.Unregister() => Dispose();

        /// <summary>兼容回收清理入口，等价于释放源订阅，不把映射属性归入对象池。</summary>
        void IOfPool.OnRecycle() => Dispose();

        /// <summary>通过池化接口释放源订阅；本类型不执行对象池归还。</summary>
        void IPoolType.RecycleToCache() => Dispose();

        /// <summary>兼容池接口的独立标记；不控制释放状态或源订阅。</summary>
        bool IOfPool.IsRecycled
        {
            get; set;
        }

        /// <inheritdoc />
        IUnregister IEventSource.Register(Action eventAction) => ((IEasyEvent)_target).Register(eventAction);

        /// <summary>重新计算目标值，并交由目标属性的相等策略决定是否通知。</summary>
        private void OnSourceValueChanged(TSource value)
        {
            // 防御性检查：正常 Dispose 后订阅已解除，这里兜底忽略迟到的回调
            if (_isDisposed)
            {
                return;
            }

            _target.Value = _selector(value);
        }
    }

    /// <summary>
    /// <see cref="MappedProperty{TSource,TTarget}"/> 的创建扩展。
    /// </summary>
    public static class MappedPropertyExtensions
    {
        /// <summary>
        /// 由单个源属性创建只读派生属性。调用方负责释放返回对象：调用 Dispose 或接入已有注销机制。
        /// </summary>
        /// <param name="source">源属性，不能为 null。</param>
        /// <param name="selector">源值到目标值的映射委托，不能为 null；构造时即会调用。</param>
        /// <returns>持有源订阅的映射属性。</returns>
        /// <exception cref="ArgumentNullException">source 或 selector 为 null。</exception>
        public static MappedProperty<TSource, TTarget> Map<TSource, TTarget>(
            this IReadOnlyBindableProperty<TSource> source, Func<TSource, TTarget> selector) =>
            new(source, selector);
    }
}
