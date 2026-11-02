using System;
using System.Collections.Generic;

namespace YLFramework.Events
{
    /// <summary>为一轮事件分发提供强类型调用方式，避免通过反射调用委托。</summary>
    internal interface IEventInvocation<TCallback> where TCallback : Delegate
    {
        /// <summary>使用本轮捕获的参数调用一个监听。</summary>
        void Invoke(TCallback callback);
    }

    /// <summary>以独立注册身份管理监听数组，供不同参数签名共享快照和一次性分发规则。</summary>
    /// <remarks>注册和注销时复制数组；发布只读取捕获的数组，不借池，也不为每次通知创建闭包。</remarks>
    internal sealed class EventRegistrations<TCallback> where TCallback : Delegate
    {
        private readonly EventChannelBase _owner;
        private Entry[] _entries = Array.Empty<Entry>();

        /// <summary>将注册集合绑定到负责生命周期检查和变更通知的事件通道。</summary>
        internal EventRegistrations(EventChannelBase owner) => _owner = owner;
        /// <summary>当前条目数；普通多播按目标展开，Once 的多播整体计为一条。</summary>
        internal int Count => _entries.Length;

        /// <summary>追加一次独立注册；空回调返回空句柄，重复委托不去重。</summary>
        internal IUnregister Register(TCallback callback, bool once = false)
        {
            _owner.ThrowIfDisposed();
            if (callback == null)
            {
                return EmptyUnregister.Instance;
            }

            var registration = new Registration(this, once);
            var targets = once ? new Delegate[] { callback } : callback.GetInvocationList();
            var next = new Entry[_entries.Length + targets.Length];
            Array.Copy(_entries, next, _entries.Length);
            for (var i = 0; i < targets.Length; i++)
            {
                next[_entries.Length + i] = new Entry((TCallback)targets[i], registration);
            }

            _entries = next;
            _owner.NotifyListenersChanged();
            return registration;
        }

        /// <summary>移除最后一个匹配的普通委托子序列，不按委托取消 Once，也不修改已有快照。</summary>
        internal void Unregister(TCallback callback)
        {
            if (callback == null || _entries.Length == 0)
            {
                return;
            }

            var targets = callback.GetInvocationList();
            // 与 Delegate.Remove 一致，只删除最后一个相等的普通 target 子序列。
            for (var start = _entries.Length - targets.Length; start >= 0; start--)
            {
                var matches = true;
                for (var i = 0; i < targets.Length; i++)
                {
                    if (_entries[start + i].Registration.Once || !_entries[start + i].Callback.Equals(targets[i]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                var previous = _entries;
                var next = new Entry[previous.Length - targets.Length];
                Array.Copy(previous, next, start);
                Array.Copy(previous, start + targets.Length, next, start, next.Length - start);
                _entries = next;
                for (var i = start; i < start + targets.Length; i++)
                {
                    var registration = previous[i].Registration;
                    if (!Contains(registration))
                    {
                        registration.Invalidate();
                    }
                }
                _owner.NotifyListenersChanged();
                return;
            }
        }

        /// <summary>检查当前数组中是否仍有条目归属于指定注册，决定其句柄是否仍有效。</summary>
        private bool Contains(Registration registration)
        {
            foreach (var entry in _entries)
            {
                if (ReferenceEquals(entry.Registration, registration))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按注册身份移除全部剩余条目并使句柄失效，保留已捕获的数组。</summary>
        private void Remove(Registration registration)
        {
            var count = 0;
            foreach (var entry in _entries)
            {
                if (!ReferenceEquals(entry.Registration, registration))
                {
                    count++;
                }
            }

            registration.Invalidate();
            if (count == _entries.Length)
            {
                return;
            }

            var next = count == 0 ? Array.Empty<Entry>() : new Entry[count];
            var index = 0;
            foreach (var entry in _entries)
            {
                if (!ReferenceEquals(entry.Registration, registration))
                {
                    next[index++] = entry;
                }
            }

            _entries = next;
            _owner.NotifyListenersChanged();
        }

        /// <summary>摘除全部当前监听并使旧句柄失效，不改变在途快照或通道代次。</summary>
        internal void Clear()
        {
            var previous = _entries;
            _entries = Array.Empty<Entry>();
            foreach (var entry in previous)
            {
                entry.Registration.Invalidate();
            }
        }

        /// <summary>捕获当前数组和通道代次；已关闭的通道不能开始新一轮分发。</summary>
        internal Snapshot Capture()
        {
            _owner.ThrowIfDisposed();
            return new Snapshot(_owner, _owner.Incarnation, _entries);
        }

        /// <summary>同步遍历本轮快照；嵌套分发另取当前数组，通道代次结束则停止旧轮。</summary>
        /// <param name="invocation">携带本轮事件参数的调用器。</param>
        /// <param name="continueOnError">是否逐条继续并在结束时汇总异常；否则首个异常立即向外传播。</param>
        internal void Dispatch<TInvocation>(TInvocation invocation, bool continueOnError)
            where TInvocation : struct, IEventInvocation<TCallback>
        {
            var snapshot = Capture();
            List<Exception> errors = null;
            for (var i = 0; i < snapshot.Count && snapshot.IsCurrent; i++)
            {
                if (!continueOnError)
                {
                    if (snapshot.TryGetCallback(i, out var callback))
                    {
                        invocation.Invoke(callback);
                    }

                    continue;
                }
                try
                {
                    if (snapshot.TryGetCallback(i, out var callback))
                    {
                        invocation.Invoke(callback);
                    }
                }
                catch (Exception error)
                {
                    (errors ??= new List<Exception>()).Add(error);
                }
            }
            SubscriptionCleanup.ThrowErrors(errors);
        }

        /// <summary>保存一次分发的监听数组和通道代次，隔离分发期间的注册变更。</summary>
        internal readonly struct Snapshot
        {
            private readonly EventChannelBase _owner;
            private readonly object _incarnation;
            private readonly Entry[] _entries;
            /// <summary>记录捕获时的通道身份与数组引用，不复制每个回调。</summary>
            internal Snapshot(EventChannelBase owner, object incarnation, Entry[] entries)
            {
                _owner = owner;
                _incarnation = incarnation;
                _entries = entries;
            }
            /// <summary>本轮捕获的条目数量。</summary>
            internal int Count => _entries.Length;
            /// <summary>通道是否仍处于捕获时的生命周期代次。</summary>
            internal bool IsCurrent => _owner.IsCurrent(_incarnation);
            /// <summary>取得本轮回调；普通取消不撤回快照，Once 仅在尚未消费时返回并预先移除。</summary>
            internal bool TryGetCallback(int index, out TCallback callback)
            {
                callback = null;
                if (!IsCurrent)
                {
                    return false;
                }

                var entry = _entries[index];
                var registration = entry.Registration;
                if (registration.Once)
                {
                    if (registration.IsConsumed)
                    {
                        return false;
                    }

                    // 先消费再注销；注销钩子或用户回调即使重入发布，也不会重复执行本次 Once。
                    registration.IsConsumed = true;
                    registration.Unregister();
                    if (!IsCurrent)
                    {
                        return false;
                    }
                }
                callback = entry.Callback;
                return true;
            }
        }

        /// <summary>将单个分发目标与拥有它的注册身份关联。</summary>
        internal readonly struct Entry
        {
            internal readonly TCallback Callback;
            internal readonly Registration Registration;
            internal Entry(TCallback callback, Registration registration)
            {
                Callback = callback;
                Registration = registration;
            }
        }

        /// <summary>一次注册的引用身份；取消后不再持有集合，也不会误删后续相等委托。</summary>
        internal sealed class Registration : IUnregister, IDisposable
        {
            private EventRegistrations<TCallback> _owner;
            /// <summary>是否将本次注册整体作为一次性监听。</summary>
            internal readonly bool Once;
            /// <summary>一次性监听是否已被某轮分发消费，供嵌套和旧快照共同检查。</summary>
            internal bool IsConsumed;
            /// <summary>记录所属集合及一次性策略，创建独立的取消身份。</summary>
            internal Registration(EventRegistrations<TCallback> owner, bool once)
            {
                _owner = owner;
                Once = once;
            }
            /// <summary>解除对集合的引用，使后续取消为空操作；不撤回已捕获的条目。</summary>
            internal void Invalidate() => _owner = null;
            /// <inheritdoc />
            public void Unregister() => _owner?.Remove(this);
            /// <summary>取消本次注册，等价于 Unregister。</summary>
            public void Dispose() => Unregister();
        }
    }

}
