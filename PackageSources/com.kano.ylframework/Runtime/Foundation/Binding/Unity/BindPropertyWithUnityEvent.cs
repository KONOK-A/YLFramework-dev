using System;
using UnityEngine.Events;

namespace YLFramework.Binding
{
    /// <summary>
    /// 同时向代码监听者与 Inspector 配置的 UnityEvent 广播值变化的可绑定属性。
    /// 触发顺序：先执行基类的代码监听回调，再调用 <see cref="unityEvent"/>；
    /// OnRecycle 仅清除 UnityEvent 的运行时监听，保留 Inspector 配置的持久监听。
    /// 仅可在主线程使用（UnityEvent 限制）。
    /// </summary>
    /// <typeparam name="T">属性值类型。</typeparam>
    [Serializable]
    public class BindPropertyWithUnityEvent<T> : BindableProperty<T>
    {
        /// <summary>
        /// Inspector 中配置的额外值变化回调，参数为变化后的值；代码监听请使用基类的 Register。
        /// </summary>
        public UnityEvent<T> unityEvent = new();

        /// <summary>
        /// 创建值为 default 的属性。
        /// </summary>
        public BindPropertyWithUnityEvent()
        {
        }

        /// <summary>
        /// 创建带初始值的属性。
        /// </summary>
        /// <param name="defaultValue">初始值。</param>
        public BindPropertyWithUnityEvent(T defaultValue) : base(defaultValue)
        {
        }

        /// <summary>
        /// 触发值变化：先执行基类通知，再调用 Inspector 中配置的 UnityEvent（参数为当前值）。
        /// </summary>
        /// <remarks>
        /// 基类监听抛错时不会继续调用 UnityEvent；其参数在基类通知后读取，因此代码回调改值会影响传入值。
        /// </remarks>
        protected override void OnValueChange()
        {
            base.OnValueChange();
            unityEvent?.Invoke(Value);
        }

        /// <summary>
        /// 执行回收清理：先清理基类，再移除 UnityEvent 的运行时监听，不移除 Inspector 持久监听。
        /// </summary>
        /// <exception cref="NullReferenceException">unityEvent 被调用方置为 null。</exception>
        public override void OnRecycle()
        {
            base.OnRecycle();
            unityEvent.RemoveAllListeners();
        }
    }
}
