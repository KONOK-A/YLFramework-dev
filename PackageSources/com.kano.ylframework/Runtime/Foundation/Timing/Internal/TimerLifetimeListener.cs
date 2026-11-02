using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using UnityEngine;
using YLFramework.Pooling;

namespace YLFramework.Timing.Internal
{
    /// <summary>汇总同一 GameObject 的计时绑定，将 Unity 生命周期消息及调度器存活检查转为取消通知。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    internal sealed class TimerLifetimeListener : MonoBehaviour
    {
        private readonly HashSet<TimerGameObjectBinding> _bindings = new();
        private GameObject _owner;
        /// <summary>登记一项绑定并记录对象引用，供遗漏生命周期消息时检查存活状态。</summary>
        internal void Add(TimerGameObjectBinding binding)
        {
            _owner = gameObject;
            _bindings.Add(binding);
        }
        /// <summary>仅移除托管绑定记录，即使监听器已被 Unity 销毁也可用于解除责任。</summary>
        internal void Remove(TimerGameObjectBinding binding) => _bindings.Remove(binding);

        /// <summary>由调度器检查对象或监听器是否已销毁、对象是否停用，异常交回调度器收集。</summary>
        internal void CheckOwner()
        {
            if (!_owner || !this)
            {
                Notify(true, false);
            }
            else if (!_owner.activeInHierarchy)
            {
                Notify(false, false);
            }
        }

        /// <summary>处理 Unity 停用消息，将本次绑定通知中的异常记录到日志。</summary>
        private void OnDisable()
        {
            // 只禁用监听组件不代表 owner GameObject 已停用。
            if (!gameObject.activeInHierarchy)
            {
                Notify(false);
            }
        }
        /// <summary>监听器销毁时按销毁边界通知其持有的全部绑定。</summary>
        private void OnDestroy() => Notify(true);

        /// <summary>遍历绑定快照逐项通知，允许取消回调修改原集合；按调用边界记录或汇总异常。</summary>
        private void Notify(bool destroyed, bool reportErrors = true)
        {
            var snapshot = ListPool<TimerGameObjectBinding>.Allocate();
            List<Exception> errors = null;
            try
            {
                snapshot.AddRange(_bindings);
                foreach (var binding in snapshot)
                {
                    try
                    {
                        binding.OwnerEnded(destroyed);
                    }
                    catch (Exception error)
                    {
                        if (reportErrors)
                        {
                            Debug.LogException(error);
                        }
                        else
                        {
                            (errors ??= new List<Exception>()).Add(error);
                        }
                    }
                }
            }
            finally
            {
                ListPool<TimerGameObjectBinding>.Recycle(snapshot);
            }
            if (errors == null)
            {
                return;
            }

            if (errors.Count == 1)
            {
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }

            throw new AggregateException(errors);
        }
    }
}
