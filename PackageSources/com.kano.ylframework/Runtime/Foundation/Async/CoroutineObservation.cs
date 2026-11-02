using System;
using System.Collections;
using UnityEngine;

namespace YLFramework.Async
{
    /// <summary>
    /// 借用 Coroutine 的窄观察句柄。仅主线程使用；Dispose 仅停止自己的观察 wrapper，不停止 producer。
    /// </summary>
    /// <remarks>
    /// callback 只表示 Unity 对 producer 的原生等待结束，不证明业务成功、未取消或资源已释放。
    /// producer 被停止或其 owner 销毁时是否结束等待遵循 Unity 原生行为，不补造终态通知。
    /// Unity 只允许同一 Coroutine 有一个等待者；已有等待者时观察可能未启动，须检查 IsStarted。
    /// Starter 宿主失效时不恢复观察，也不保证 callback 执行；不提供统一异步结果协议。
    /// </remarks>
    public sealed class CoroutineObservation : IDisposable
    {
        private Coroutine _wrapper;
        private Action _callback;
        private bool _isEnded;

        /// <summary>保存本次观察的完成回调，等待协程由 Observe 另行启动。</summary>
        private CoroutineObservation(Action callback) => _callback = callback;

        /// <summary>
        /// Starter 是否返回了有效观察句柄；false 明确表示未取得可运行观察，不代表 producer 已结束。
        /// 此值记录启动结果，不承诺后续宿主存活。
        /// </summary>
        public bool IsStarted
        {
            get; private set;
        }

        /// <summary>通过 CoroutineStarter 启动独立等待协程，不取得 producer 的所有权；参数无效时不创建宿主。</summary>
        /// <param name="producer">借用的协程句柄，不能为 null。</param>
        /// <param name="callback">原生等待结束后同步调用的回调，不能为 null；异常不在观察器内隔离。</param>
        public static CoroutineObservation Observe(Coroutine producer, Action callback)
        {
            if (producer == null)
            {
                throw new ArgumentNullException(nameof(producer));
            }

            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            var observation = new CoroutineObservation(callback);
            var wrapper = CoroutineStarter.Start(observation.Wait(producer));
            observation.IsStarted = wrapper != null;
            if (wrapper == null)
            {
                observation._isEnded = true;
                observation._callback = null;
            }
            else if (observation._isEnded)
            {
                // 首步可能在 Start 返回前结束观察，不能再把返回的句柄写回已结束的观察器。
                CoroutineStarter.Stop(wrapper);
            }
            else
            {
                observation._wrapper = wrapper;
            }

            return observation;
        }

        /// <summary>等待借用协程的原生等待结束，先提交观察终止并清空引用，再同步调用完成回调。</summary>
        private IEnumerator Wait(Coroutine producer)
        {
            yield return producer;
            if (_isEnded)
            {
                yield break;
            }

            _isEnded = true;
            _wrapper = null;
            var callback = _callback;
            _callback = null;
            callback();
        }

        /// <summary>幂等释放观察及 callback；不取消 producer，不创建或查找替代 Starter 宿主。</summary>
        public void Dispose()
        {
            if (_isEnded)
            {
                return;
            }

            _isEnded = true;
            _callback = null;
            var wrapper = _wrapper;
            _wrapper = null;
            CoroutineStarter.Stop(wrapper);
        }
    }
}
