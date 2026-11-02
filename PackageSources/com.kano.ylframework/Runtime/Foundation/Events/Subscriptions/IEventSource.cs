using System;

namespace YLFramework.Events
{
    /// <summary>不依赖池或 Unity owner 的无参观察入口；泛型事件按注册顺序丢弃 payload。</summary>
    public interface IEventSource
    {
        /// <summary>注册观察者，返回本次注册的精确注销身份；null 返回空句柄。</summary>
        /// <remarks>调用方负责保存并按生命周期取消句柄；此接口本身不承诺自动解绑或异步通知。</remarks>
        IUnregister Register(Action callback);
    }

    /// <summary>空注册共用的无状态句柄，允许调用方统一执行取消。</summary>
    internal sealed class EmptyUnregister : IUnregister
    {
        /// <summary>供空回调注册复用的唯一实例。</summary>
        internal static readonly EmptyUnregister Instance = new();
        /// <summary>限制外部构造，所有空注册共享同一无状态实例。</summary>
        private EmptyUnregister()
        {
        }
        /// <inheritdoc />
        public void Unregister()
        {
        }
    }
}
