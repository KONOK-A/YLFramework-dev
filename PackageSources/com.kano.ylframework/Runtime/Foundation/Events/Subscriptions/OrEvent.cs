using System;
using System.Collections.Generic;

namespace YLFramework.Events
{
    /// <summary>
    /// 组合事件：任一 source 事件触发时调用自身的监听回调。
    /// 首个监听加入后才连接 source，末个监听离开时断开，下一次监听可以重新连接。
    /// 首次连接尚未完成时的同步注册属于该轮临时责任：连接失败时回滚全部临时注册并释放本次连接，
    /// 保留原始失败原因（必要时附清理错误）；明确断连后新建的轮次不受旧轮回滚影响。
    /// 普通取消保持已经开始的监听快照；Dispose 永久关闭并截断在途快照。
    /// source 描述保留到 Dispose；同一个 source 重复追加按次数连接，不去重。
    /// 取消失败的 source 句柄仍保留，可再次 Dispose 显式重试。
    /// </summary>
    /// <remarks>源的 Register 可立即同步通知，因此 Register 或 Or 返回前，组合监听就可能执行并重入；重复监听不去重。</remarks>
    public class OrEvent : IEventSource, IUnregisterList, IDisposable
    {
        /// <inheritdoc />
        public List<IUnregister> UnregisterList { get; } = new();

        private readonly List<Source> _sources = new();
        private readonly ListenerChannel _listeners;
        private ConnectionRound _round;
        private bool _isDisposed;

        /// <summary>创建尚未连接 source 的组合事件。</summary>
        public OrEvent() => _listeners = new ListenerChannel(this);

        /// <summary>当前是否有监听。</summary>
        public bool HasListener => _listeners.HasListener;

        /// <summary>当前监听数量，重复监听独立计数。</summary>
        public int ListenerCount => _listeners.ListenerCount;

        /// <summary>在监听数归零时通知组合事件断开当前连接轮次。</summary>
        private sealed class ListenerChannel : EventChannel
        {
            private readonly OrEvent _owner;

            /// <summary>将实例监听通道绑定到负责源连接生命周期的组合事件。</summary>
            public ListenerChannel(OrEvent owner) => _owner = owner;

            /// <summary>监听变更提交后检查空通道，避免在无人观察时继续持有源订阅。</summary>
            protected override void OnListenersChanged()
            {
                if (!HasListener && _owner._round != null)
                {
                    _owner.Disconnect(_owner._round);
                }
            }
        }

        /// <summary>保留一次追加的源描述，使重复追加与失败撤销各有独立身份。</summary>
        private sealed class Source
        {
            /// <summary>实际提供观察入口的源事件。</summary>
            public readonly IEventSource Event;
            /// <summary>该描述是否仍参与当前和后续连接。</summary>
            public bool IsActive = true;
            /// <summary>保存一次追加的源事件，不在此处注册监听。</summary>
            public Source(IEventSource source) => Event = source;
        }

        /// <summary>隔离一次连接过程的临时注册与源连接，防止旧轮回滚影响重入建立的新轮。</summary>
        private sealed class ConnectionRound
        {
            /// <summary>该轮是否仍允许源通知转发。</summary>
            public bool IsActive = true;
            /// <summary>初次连接是否尚未全部完成，期间新监听仍属于可回滚责任。</summary>
            public bool IsConnecting;
            /// <summary>该轮尚由连接流程持有的源连接。</summary>
            public readonly List<Connection> Connections = new();
            /// <summary>连接尚未完成期间产生的临时监听，失败时整体回滚，成功后转为正式监听。</summary>
            public readonly List<IUnregister> Provisional = new();
        }

        /// <summary>管理一个源描述在某一轮的订阅，屏蔽旧通知并保留失败的取消责任。</summary>
        private sealed class Connection : IUnregister
        {
            private readonly OrEvent _owner;
            private readonly ConnectionRound _round;
            /// <summary>本连接所属的独立源描述。</summary>
            public readonly Source Source;
            private IUnregister _token;
            private bool _isActive = true;
            private bool _isCanceling;

            /// <summary>本连接是否仍允许通知转发，不代表底层句柄一定已成功取消。</summary>
            public bool IsActive => _isActive;

            /// <summary>记录组合事件、连接轮次和源描述，等待源注册完成后接收句柄。</summary>
            public Connection(OrEvent owner, ConnectionRound round, Source source)
            {
                _owner = owner;
                _round = round;
                Source = source;
            }

            /// <summary>仅当连接、源描述和轮次均存活时同步转发到组合监听，回调异常直接向源传播。</summary>
            public void Notify()
            {
                // source 的旧快照不能转发到重连后的新轮次；普通 listener 取消不截断 channel 快照。
                if (_isActive && Source.IsActive && _round.IsActive && ReferenceEquals(_owner._round, _round) && !_owner._isDisposed)
                {
                    _owner._listeners.Trigger();
                }
            }

            /// <summary>接收源注册返回的句柄；若同步回调已经终结连接，则立即取消这个迟到句柄。</summary>
            public void Attach(IUnregister token)
            {
                _token = token ?? EmptyUnregister.Instance;
                _owner.UnregisterList.Add(this);
                // Register 可同步通知并重入关闭；迟到句柄只取消原来的连接。
                if (!_isActive || !Source.IsActive || !_round.IsActive || !ReferenceEquals(_owner._round, _round) || _owner._isDisposed)
                {
                    Unregister();
                }
            }

            /// <summary>先停止转发再尝试取消底层句柄，成功后解除持有，失败时保留责任供后续清理。</summary>
            public void Unregister()
            {
                _isActive = false;
                if (_isCanceling || _token == null)
                {
                    return;
                }

                if (_owner.UnregisterList.Contains(this))
                {
                    _owner.ReleaseConnections(new List<IUnregister> { this });
                    return;
                }
                _isCanceling = true;
                try
                {
                    _token.Unregister();
                    _token = null;
                    _owner.UnregisterList.Remove(this);
                    _round.Connections.Remove(this);
                }
                finally
                {
                    _isCanceling = false;
                }
            }
        }

        /// <summary>
        /// 追加一个 source 事件，任一 source 触发都会调用本事件的监听回调。
        /// </summary>
        /// <param name="source">参与组合的事件。</param>
        /// <returns>自身，便于链式组合。</returns>
        public OrEvent Or(IEventSource source)
        {
            ThrowIfDisposed();
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var descriptor = new Source(source);
            _sources.Add(descriptor);
            if (_round != null)
            {
                try
                {
                    ConnectSource(descriptor, _round);
                }
                catch (Exception error)
                {
                    descriptor.IsActive = false;
                    _sources.Remove(descriptor);
                    // 同步通知可能已经重连；失败追加的描述在所有轮次中都需撤销，不能误撤其他描述。
                    var connections = new List<IUnregister>();
                    foreach (var token in UnregisterList)
                    {
                        if (token is Connection connection && connection.IsActive && ReferenceEquals(connection.Source, descriptor))
                        {
                            connections.Add(connection);
                        }
                    }

                    try
                    {
                        ReleaseConnections(connections);
                    }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException(error, cleanupError);
                    }
                    throw;
                }
            }
            return this;
        }

        /// <summary>
        /// 注册事件监听，返回用于注销本次注册的句柄。
        /// </summary>
        /// <param name="eventAction">事件监听回调，为 null 时忽略。</param>
        /// <returns>用于注销本次注册的句柄。</returns>
        public IUnregister Register(Action eventAction)
        {
            return Register(eventAction, false);
        }

        /// <summary>注册只执行一次的监听，Once 重入规则由共享 channel 保证。</summary>
        public IUnregister RegisterOnce(Action eventAction) => Register(eventAction, true);

        /// <summary>加入监听并在需要时建立首轮源连接，首次连接失败时回滚同轮的全部临时注册。</summary>
        private IUnregister Register(Action callback, bool once)
        {
            ThrowIfDisposed();
            var token = once ? _listeners.RegisterOnce(callback) : _listeners.Register(callback);
            if (callback == null)
            {
                return token;
            }

            var pending = _round;
            if (pending != null)
            {
                // 连接尚未完成期间的注册是同一轮的临时责任；失败时必须一并回滚。
                if (pending.IsConnecting)
                {
                    pending.Provisional.Add(token);
                }

                return token;
            }
            if (!HasListener)
            {
                return token;
            }

            var round = new ConnectionRound { IsConnecting = true };
            round.Provisional.Add(token);
            _round = round;
            try
            {
                foreach (var source in _sources.ToArray())
                {
                    if (!round.IsActive || !ReferenceEquals(_round, round))
                    {
                        break;
                    }

                    ConnectSource(source, round);
                }
                // 连接成功：临时责任转为正式监听，本轮后续注册不再属于回滚范围。
                round.IsConnecting = false;
                round.Provisional.Clear();
                return token;
            }
            catch (Exception error)
            {
                var errors = new List<Exception> { error };
                round.IsActive = false;
                round.IsConnecting = false;
                if (ReferenceEquals(_round, round))
                {
                    _round = null;
                }
                // 同一未完成连接轮的临时注册整体回滚；明确断连后新建的轮次不在其中。
                foreach (var provisional in round.Provisional)
                {
                    try
                    {
                        provisional.Unregister();
                    }
                    catch (Exception cleanupError)
                    {
                        errors.Add(cleanupError);
                    }
                }
                round.Provisional.Clear();
                try
                {
                    Disconnect(round);
                }
                catch (Exception cleanupError)
                {
                    errors.Add(cleanupError);
                }
                SubscriptionCleanup.ThrowErrors(errors);
                throw;
            }
        }

        /// <summary>为指定轮次连接一个源，兼顾 Register 同步通知、注册失败和迟到句柄的收尾。</summary>
        private void ConnectSource(Source source, ConnectionRound round)
        {
            if (!source.IsActive)
            {
                return;
            }

            var connection = new Connection(this, round, source);
            round.Connections.Add(connection);
            IUnregister token;
            try
            {
                token = source.Event.Register(connection.Notify);
            }
            catch
            {
                connection.Unregister();
                round.Connections.Remove(connection);
                throw;
            }
            try
            {
                connection.Attach(token);
            }
            catch
            {
                // 迟到 token 的取消已经尝试过；责任留在公共清理列表，不在回滚中隐式重试。
                round.Connections.Remove(connection);
                throw;
            }
        }

        /// <summary>先终结指定轮次的转发身份，再分离并释放该轮源连接，不触碰重入建立的新轮。</summary>
        private void Disconnect(ConnectionRound round)
        {
            round.IsActive = false;
            if (ReferenceEquals(_round, round))
            {
                _round = null;
            }

            var connections = new List<IUnregister>(round.Connections);
            round.Connections.Clear();
            ReleaseConnections(connections);
        }

        /// <summary>分离本轮取消责任并逐项尝试，将失败连接归还公共清理列表以便显式重试。</summary>
        private void ReleaseConnections(List<IUnregister> connections)
        {
            // 同一责任只归还一次；整轮尝试期间不暴露给重入 Dispose，避免隐式重试或丢失。
            foreach (var connection in connections)
            {
                UnregisterList.Remove(connection);
            }

            try
            {
                SubscriptionCleanup.ReleaseAll(connections, connection => connection.Unregister());
            }
            finally
            {
                foreach (var failed in connections)
                {
                    if (!UnregisterList.Contains(failed))
                    {
                        UnregisterList.Add(failed);
                    }
                }
            }
        }

        /// <summary>拒绝在永久关闭后追加源或建立新监听。</summary>
        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(OrEvent));
            }
        }

        /// <summary>
        /// 先同步调用一次回调，成功后才注册监听；首次调用抛异常时不留下本次订阅。
        /// </summary>
        /// <param name="eventAction">事件监听回调。</param>
        /// <returns>用于注销本次注册的句柄。</returns>
        public IUnregister RegisterWithACall(Action eventAction)
        {
            ThrowIfDisposed();
            eventAction?.Invoke();
            return Register(eventAction);
        }

        /// <summary>
        /// 移除最后一个匹配的普通委托子序列；只有监听数归零才断开当前 source 连接，Once 须用句柄取消。
        /// </summary>
        /// <param name="eventAction">要注销的监听回调。</param>
        public void Unregister(Action eventAction)
        {
            _listeners.Unregister(eventAction);
        }

        /// <summary>清空监听并断开 source；已开始的普通监听快照保持。</summary>
        public void ClearListeners() => _listeners.ClearListeners();

        /// <summary>
        /// 永久关闭并截断在途通知，尝试全部 source 取消；失败责任保留，重复调用可重试。
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                if (_round != null)
                {
                    _round.IsActive = false;
                }

                _round = null;
                _sources.Clear();
                _listeners.Dispose();
            }
            SubscriptionCleanup.ReleaseAll(UnregisterList, token => token?.Unregister());
        }
    }

    /// <summary>
    /// OrEvent 的扩展入口。
    /// </summary>
    public static class OrEventExtensions
    {
        /// <summary>
        /// 组合两个事件，任一触发都会调用返回的 OrEvent 的监听回调。
        /// </summary>
        /// <param name="self">第一个事件。</param>
        /// <param name="e">第二个事件。</param>
        /// <returns>组合后的事件。</returns>
        public static OrEvent Or(this IEventSource self, IEventSource e)
        {
            return new OrEvent().Or(self).Or(e);
        }
    }
}
