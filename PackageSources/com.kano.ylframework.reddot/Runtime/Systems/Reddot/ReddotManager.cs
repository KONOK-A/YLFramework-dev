using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using YLFramework.Singletons;

namespace YLFramework.RedDots
{
    /// <summary>主线程路径红点。普通路径、独立租约和数值来源共同维护逐层聚合的路径树。</summary>
    /// <remarks>
    /// 所有入口和凭据均须在主线程串行使用。读取只查规范路径的缓存，写入预校验自身及祖先后才提交。
    /// Batch 合并通知而不是跨调用事务；回调内变更进入下一轮通知，观察者异常逐个收集后向调用者传播。
    /// </remarks>
    public static class ReddotManager
    {
        private static readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
        private static readonly HashSet<Node> _dirty = new();
        private static readonly List<Subscription> _initial = new();
        private static object _generation = new();
        private static object _session = new();
        private static int _batchDepth;
        private static bool _isFlushing;
        private static long _diagnosticGeneration = 1;
        private static long _diagnosticSequence;

        /// <summary>注册会话重置，避免关闭 Domain Reload 时沿用上一轮的路径或订阅。</summary>
        static ReddotManager() => SingletonSessionState.RegisterSessionReset(ResetSession);

        /// <summary>持有来源的规范路径去重、按 Ordinal 排序只读快照；零值数值来源也保留其登记路径。</summary>
        public static IReadOnlyList<string> ReddotPath
        {
            get
            {
                var result = new List<string>();
                foreach (var node in _nodes.Values)
                {
                    if (node.HasPath || node.LeaseCount != 0 || node.Sources.Count != 0)
                    {
                        result.Add(node.Path);
                    }
                }
                result.Sort(StringComparer.Ordinal);
                return result.AsReadOnly();
            }
        }

        /// <summary>读取节点聚合值；空路径不是默认根，空路径或未登记节点返回零。</summary>
        public static long GetValue(string path)
        {
            path = NormalizePath(path);
            return _nodes.TryGetValue(path, out var node) ? node.Value : 0;
        }

        /// <summary>读取仅含规范路径与当前聚合值的不可变快照，不构造整棵树的调试数据。</summary>
        public static ReddotSnapshot GetSnapshot(string path)
        {
            path = NormalizePath(path);
            return new ReddotSnapshot(path, _nodes.TryGetValue(path, out var node) ? node.Value : 0);
        }

        /// <summary>显式复制整棵树的只读诊断数据。仅本入口扫描，不刷新 Batch、不登记订阅或运行采样器。</summary>
        /// <remarks>主线程串行调用；包含已创建的零值节点，不创建空路径根。来源 ID 没有人为附加标签。</remarks>
        public static ReddotDiagnosticSnapshot CaptureDiagnostics()
        {
            var ordered = new List<Node>(_nodes.Values);
            ordered.Sort((first, second) => StringComparer.Ordinal.Compare(first.Path, second.Path));
            var nodes = new List<ReddotDiagnosticNode>(ordered.Count);
            foreach (var node in ordered)
            {
                var sources = new List<ReddotDiagnosticSource>(node.Sources.Count);
                foreach (var source in node.Sources.Values)
                {
                    sources.Add(new ReddotDiagnosticSource(source.SourceId, source.Value));
                }
                sources.Sort((first, second) => StringComparer.Ordinal.Compare(first.SourceId, second.SourceId));
                nodes.Add(new ReddotDiagnosticNode(node.Path, node.Value, node.OwnValue, node.Aggregation,
                    node.HasPath, node.LeaseCount, node.Listeners.Count, _dirty.Contains(node), sources.AsReadOnly()));
            }
            return new ReddotDiagnosticSnapshot(_diagnosticGeneration, ++_diagnosticSequence, _batchDepth,
                _dirty.Count, _initial.Count, _isFlushing, nodes.AsReadOnly());
        }

        /// <summary>登记唯一的 path + Ordinal sourceId；initialValue 非负，通知失败时回滚未交付的凭据。</summary>
        public static ReddotSource RegisterSource(string path, string sourceId, long initialValue = 0)
        {
            path = RequirePath(path);
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                throw new ArgumentException("红点来源标识不能是空白。", nameof(sourceId));
            }
            if (initialValue < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialValue));
            }

            var node = GetOrCreateNode(path);
            if (node.Sources.ContainsKey(sourceId))
            {
                throw new ArgumentException("同一路径的来源标识不能重复。", nameof(sourceId));
            }
            var ownValue = checked(node.OwnValue + initialValue);
            var changes = PrepareChange(node, ownValue, node.Aggregation);
            var source = new ReddotSource(path, sourceId, initialValue, _generation);
            node.Sources.Add(sourceId, source);
            node.OwnValue = ownValue;
            try
            {
                Publish(changes);
            }
            catch (Exception error)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception correction)
                {
                    throw new AggregateException("红点登记及回滚通知失败。", error, correction);
                }
                throw;
            }
            return source;
        }

        /// <summary>按路径订阅值变化；默认补一次当前快照，回调内订阅的补发排入下一轮而不递归。</summary>
        /// <remarks>空路径仅可收到一次零值初始快照。初始补发不受 Batch 延迟，失败时撤销未交付订阅。</remarks>
        public static IDisposable Subscribe(string path, Action<ReddotSnapshot> callback, bool emitInitial = true)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }
            path = NormalizePath(path);
            var node = path.Length == 0 ? null : GetOrCreateNode(path);
            var subscription = new Subscription(node, path, callback, _session);
            node?.Listeners.Add(subscription);
            if (emitInitial)
            {
                _initial.Add(subscription);
                try
                {
                    Flush();
                }
                catch
                {
                    subscription.Dispose();
                    throw;
                }
            }
            else if (node == null)
            {
                subscription.Invalidate();
            }
            return subscription;
        }

        /// <summary>开始可嵌套的通知批次；最后一个本代际 Scope 释放时刷新，异常退出也保留已完成的合法写入。</summary>
        public static IDisposable BeginBatch()
        {
            _batchDepth = checked(_batchDepth + 1);
            return new Batch(_generation);
        }

        /// <summary>仅改变指定节点本层规则；Reset 保留规则，新会话恢复默认 Sum。</summary>
        public static void SetAggregation(string path, ReddotAggregation rule)
        {
            path = RequirePath(path);
            if (rule != ReddotAggregation.Sum && rule != ReddotAggregation.Any &&
                rule != ReddotAggregation.Max)
            {
                throw new ArgumentOutOfRangeException(nameof(rule));
            }
            var node = GetOrCreateNode(path);
            if (node.Aggregation == rule)
            {
                return;
            }
            var changes = PrepareChange(node, node.OwnValue, rule);
            node.Aggregation = rule;
            Publish(changes);
        }

        /// <summary>斜杠分段，忽略空段与段外空白；空路径不表示根节点。</summary>
        /// <param name="path">原始业务路径，允许为 null 或空白。</param>
        /// <returns>空字符串，或以斜杠结尾的规范路径。</returns>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            // 高频读使用已规范的凭据路径时，不再 Split/Trim/Join 分配同一字符串。
            if (path[path.Length - 1] == '/')
            {
                var start = 0;
                var canonical = true;
                for (var index = 0; index < path.Length; index++)
                {
                    if (path[index] != '/')
                    {
                        continue;
                    }
                    if (index == start || char.IsWhiteSpace(path[start]) || char.IsWhiteSpace(path[index - 1]))
                    {
                        canonical = false;
                        break;
                    }
                    start = index + 1;
                }
                if (canonical)
                {
                    return path;
                }
            }

            var segments = path.Split('/');
            var result = new List<string>(segments.Length);
            foreach (var segment in segments)
            {
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    result.Add(segment.Trim());
                }
            }

            // 保留原规范路径和序列化消费者使用的末尾分隔符。
            return result.Count == 0 ? string.Empty : string.Join("/", result) + "/";
        }

        /// <summary>加入普通路径集合，重复或空路径不改变状态，也不发通知。</summary>
        /// <param name="path">要登记的业务路径，将先规范化。</param>
        public static void AddPath(string path)
        {
            path = NormalizePath(path);
            if (path.Length == 0)
            {
                return;
            }
            var node = GetOrCreateNode(path);
            if (node.HasPath)
            {
                return;
            }
            var ownValue = checked(node.OwnValue + 1);
            var changes = PrepareChange(node, ownValue, node.Aggregation);
            node.HasPath = true;
            node.OwnValue = ownValue;
            Publish(changes);
        }

        /// <summary>批量加入普通路径集合，仅在至少一个路径实际新增时统一通知一次。</summary>
        /// <param name="paths">要登记的路径列表；null 不执行任何操作。</param>
        public static void AddPath(IList<string> paths)
        {
            if (paths == null)
            {
                return;
            }

            using var batch = BeginBatch();
            foreach (var value in paths)
            {
                AddPath(value);
            }
        }

        /// <summary>只撤销 AddPath 的集合来源，不夺取其他所有者的登记；无匹配项时不通知。</summary>
        /// <param name="path">要移除的业务路径，将先规范化。</param>
        public static void RemovePath(string path)
        {
            if (!_nodes.TryGetValue(NormalizePath(path), out var node) || !node.HasPath)
            {
                return;
            }
            var ownValue = node.OwnValue - 1;
            var changes = PrepareChange(node, ownValue, node.Aggregation);
            node.HasPath = false;
            node.OwnValue = ownValue;
            Publish(changes);
        }

        /// <summary>同路径可以独立登记多次；所有者退出必须 Dispose，Reset 会使旧凭据失效。</summary>
        /// <param name="path">至少包含一个非空段的业务路径。</param>
        /// <returns>仅释放本次来源的幂等凭据；普通集合来源与其他凭据不受影响。</returns>
        /// <remarks>登记通知失败时撤销本次来源并通知回滚；两次通知均失败时合并异常向外抛出。</remarks>
        public static IDisposable RegisterPath(string path)
        {
            var node = GetOrCreateNode(RequirePath(path));
            var ownValue = checked(node.OwnValue + 1);
            var changes = PrepareChange(node, ownValue, node.Aggregation);
            var registration = new Registration(node, _generation);
            node.LeaseCount++;
            node.OwnValue = ownValue;
            try
            {
                Publish(changes);
            }
            catch (Exception error)
            {
                // 登记未能交付给所有者，不能遗留无法归还的来源。
                try
                {
                    registration.Dispose();
                }
                catch (Exception correction)
                {
                    throw new AggregateException("红点登记及回滚通知失败。", error, correction);
                }
                throw;
            }
            return registration;
        }

        /// <summary>查询路径自身或任一后代是否仍有普通或独立来源，不向下继承祖先状态。</summary>
        /// <param name="path">要查询的业务路径；规范化后为空时固定返回 false。</param>
        public static bool ShouldBeDisplayed(string path) => GetValue(path) > 0;

        /// <summary>清空来源并使旧来源及 Batch 失效，保留规则和路径订阅；只通知实际变化的路径。</summary>
        public static void Reset()
        {
            _generation = new object();
            _diagnosticGeneration++;
            _batchDepth = 0;
            foreach (var node in _nodes.Values)
            {
                if (node.Value != 0)
                {
                    _dirty.Add(node);
                }
                node.Value = 0;
                node.OwnValue = 0;
                node.HasPath = false;
                node.LeaseCount = 0;
                node.Sources.Clear();
            }
            Flush();
        }

        /// <summary>切换会话时清空来源并使旧凭据、旧节点路径订阅全部失效。</summary>
        private static void ResetSession()
        {
            _generation = new object();
            _diagnosticGeneration++;
            _session = new object();
            _batchDepth = 0;
            // 旧租约可能仍持有旧节点；仅丢弃字典会让它们继续保活整棵旧树的观察者。
            foreach (var node in _nodes.Values)
            {
                foreach (var subscription in node.Listeners)
                {
                    subscription.Invalidate();
                }
                node.Listeners.Clear();
                node.Sources.Clear();
            }
            foreach (var subscription in _initial)
            {
                subscription.Invalidate();
            }
            _nodes.Clear();
            _dirty.Clear();
            _initial.Clear();
        }

        internal static bool IsCurrent(ReddotSource source) => ReferenceEquals(source.Generation, _generation);

        internal static void SetSourceValue(ReddotSource source, long value)
        {
            if (!source.IsCurrent)
            {
                throw new ObjectDisposedException(nameof(ReddotSource));
            }
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (source.Value == value)
            {
                return;
            }
            var node = _nodes[source.Path];
            var ownValue = checked(node.OwnValue - source.Value + value);
            var changes = PrepareChange(node, ownValue, node.Aggregation);
            source.StoreValue(value);
            node.OwnValue = ownValue;
            Publish(changes);
        }

        internal static void ReleaseSource(ReddotSource source)
        {
            if (!source.IsCurrent)
            {
                source.Invalidate();
                return;
            }
            var node = _nodes[source.Path];
            var ownValue = node.OwnValue - source.Value;
            var changes = PrepareChange(node, ownValue, node.Aggregation);
            source.Invalidate();
            node.Sources.Remove(source.SourceId);
            node.OwnValue = ownValue;
            Publish(changes);
        }

        private static string RequirePath(string path)
        {
            path = NormalizePath(path);
            if (path.Length == 0)
            {
                throw new ArgumentException("红点路径至少需要一个非空段。", nameof(path));
            }
            return path;
        }

        private static Node GetOrCreateNode(string path)
        {
            if (_nodes.TryGetValue(path, out var existing))
            {
                return existing;
            }
            Node parent = null;
            for (var start = 0; start < path.Length;)
            {
                var end = path.IndexOf('/', start) + 1;
                var prefix = path.Substring(0, end);
                if (!_nodes.TryGetValue(prefix, out var node))
                {
                    node = new Node(prefix, parent);
                    _nodes.Add(prefix, node);
                    parent?.Children.Add(node);
                }
                parent = node;
                start = end;
            }
            return parent;
        }

        private static List<ValueChange> PrepareChange(Node node, long ownValue, ReddotAggregation aggregation)
        {
            var changes = new List<ValueChange>();
            Node changedChild = null;
            long childValue = 0;
            while (node != null)
            {
                var value = ownValue;
                foreach (var child in node.Children)
                {
                    var contribution = child == changedChild ? childValue : child.Value;
                    switch (aggregation)
                    {
                        case ReddotAggregation.Sum:
                            value = checked(value + contribution);
                            break;
                        case ReddotAggregation.Any:
                        case ReddotAggregation.Max:
                            value = Math.Max(value, contribution);
                            break;
                    }
                }
                if (aggregation == ReddotAggregation.Any)
                {
                    value = value > 0 ? 1 : 0;
                }
                if (node.Value == value)
                {
                    break;
                }
                changes.Add(new ValueChange(node, value));
                changedChild = node;
                childValue = value;
                node = node.Parent;
                if (node != null)
                {
                    ownValue = node.OwnValue;
                    aggregation = node.Aggregation;
                }
            }
            return changes;
        }

        private static void Publish(List<ValueChange> changes)
        {
            foreach (var change in changes)
            {
                change.Node.Value = change.Value;
                _dirty.Add(change.Node);
            }
            Flush();
        }

        private static void Flush()
        {
            if (_isFlushing)
            {
                return;
            }
            List<Exception> errors = null;
            _isFlushing = true;
            try
            {
                while (_initial.Count != 0 || (_batchDepth == 0 && _dirty.Count != 0))
                {
                    var generation = _generation;
                    var notifications = new List<Notification>();
                    foreach (var subscription in _initial)
                    {
                        notifications.Add(new Notification(subscription, GetSnapshot(subscription.Path), true));
                    }
                    _initial.Clear();
                    if (_batchDepth == 0)
                    {
                        foreach (var node in _dirty)
                        {
                            var snapshot = new ReddotSnapshot(node.Path, node.Value);
                            foreach (var subscription in node.Listeners)
                            {
                                notifications.Add(new Notification(subscription, snapshot, false));
                            }
                        }
                        _dirty.Clear();
                    }

                    // 整轮先冻结值和订阅身份，用户回调的变更只进入下一轮；重置截断旧值快照。
                    for (var index = 0; index < notifications.Count; index++)
                    {
                        if (!ReferenceEquals(generation, _generation))
                        {
                            // Reset 保留订阅：旧值作废，但尚未交付的零值变化和首次补发不能丢失。
                            for (; index < notifications.Count; index++)
                            {
                                var pending = notifications[index];
                                pending.Subscription.Requeue(pending.Force);
                            }
                            break;
                        }
                        var notification = notifications[index];
                        try
                        {
                            notification.Subscription.Notify(notification.Snapshot, notification.Force);
                        }
                        catch (Exception error)
                        {
                            (errors ??= new List<Exception>()).Add(error);
                        }
                    }
                }
            }
            finally
            {
                _isFlushing = false;
            }
            if (errors?.Count == 1)
            {
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }
            if (errors?.Count > 1)
            {
                throw new AggregateException("红点观察者通知失败。", errors);
            }
        }

        private sealed class Node
        {
            internal readonly string Path;
            internal readonly Node Parent;
            internal readonly List<Node> Children = new();
            internal readonly Dictionary<string, ReddotSource> Sources = new(StringComparer.Ordinal);
            internal readonly List<Subscription> Listeners = new();
            internal long OwnValue;
            internal long Value;
            internal long LeaseCount;
            internal bool HasPath;
            internal ReddotAggregation Aggregation;

            internal Node(string path, Node parent)
            {
                Path = path;
                Parent = parent;
            }
        }

        private readonly struct ValueChange
        {
            internal readonly Node Node;
            internal readonly long Value;

            internal ValueChange(Node node, long value)
            {
                Node = node;
                Value = value;
            }
        }

        private readonly struct Notification
        {
            internal readonly Subscription Subscription;
            internal readonly ReddotSnapshot Snapshot;
            internal readonly bool Force;

            internal Notification(Subscription subscription, ReddotSnapshot snapshot, bool force)
            {
                Subscription = subscription;
                Snapshot = snapshot;
                Force = force;
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Node _node;
            private readonly object _session;
            private Action<ReddotSnapshot> _listener;
            private long _lastValue;
            internal readonly string Path;

            internal Subscription(Node node, string path, Action<ReddotSnapshot> listener, object session)
            {
                _node = node;
                Path = path;
                _listener = listener;
                _session = session;
                _lastValue = node?.Value ?? 0;
            }

            internal void Notify(ReddotSnapshot snapshot, bool force)
            {
                if (_listener == null || !ReferenceEquals(_session, ReddotManager._session) ||
                    (!force && _lastValue == snapshot.Value))
                {
                    return;
                }
                _lastValue = snapshot.Value;
                try
                {
                    _listener(snapshot);
                }
                finally
                {
                    if (_node == null)
                    {
                        Invalidate();
                    }
                }
            }

            internal void Requeue(bool force)
            {
                if (_listener == null || !ReferenceEquals(_session, ReddotManager._session))
                {
                    Invalidate();
                    return;
                }
                if (force)
                {
                    _initial.Add(this);
                }
                else if (_node != null)
                {
                    _dirty.Add(_node);
                }
            }

            internal void Invalidate() => _listener = null;

            public void Dispose()
            {
                if (_listener == null)
                {
                    return;
                }
                _listener = null;
                _node?.Listeners.Remove(this);
            }
        }

        private sealed class Batch : IDisposable
        {
            private readonly object _generation;
            private bool _isDisposed;

            internal Batch(object generation) => _generation = generation;

            public void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }
                _isDisposed = true;
                if (ReferenceEquals(_generation, ReddotManager._generation))
                {
                    _batchDepth--;
                    Flush();
                }
            }
        }

        /// <summary>绑定路径与登记代际的独立来源凭据，避免重置后的旧释放影响新来源。</summary>
        private sealed class Registration : IDisposable
        {
            private Node _node;
            private readonly object _generation;
            /// <summary>记录本次登记路径和代际，由管理器预先增加对应来源计数。</summary>
            internal Registration(Node node, object generation)
            {
                _node = node;
                _generation = generation;
            }
            /// <summary>仅首次在同代际释放自己的来源计数并通知；重置后的旧凭据直接失效。</summary>
            public void Dispose()
            {
                var node = _node;
                _node = null;
                if (node == null || !ReferenceEquals(_generation, ReddotManager._generation))
                {
                    return;
                }

                var ownValue = node.OwnValue - 1;
                var changes = PrepareChange(node, ownValue, node.Aggregation);
                node.LeaseCount--;
                node.OwnValue = ownValue;
                Publish(changes);
            }
        }
    }

    /// <summary>显式采样的不可变诊断副本；数字 Generation 仅作观察，不是来源凭据。</summary>
    public sealed class ReddotDiagnosticSnapshot
    {
        /// <summary>Reset 或会话重置时递增的观察编号，不参与凭据有效性判断。</summary>
        public long Generation
        {
            get;
        }
        /// <summary>本次显式 CaptureDiagnostics 的进程内顺序号；普通读写不递增。</summary>
        public long CaptureSequence
        {
            get;
        }
        /// <summary>当前代际尚未释放的 Batch 嵌套深度。</summary>
        public int BatchDepth
        {
            get;
        }
        /// <summary>等待下一轮通知的脏节点数，不等同于本轮已冻结的通知数。</summary>
        public int DirtyNodeCount
        {
            get;
        }
        /// <summary>尚在初始补发队列内的登记项数。</summary>
        public int PendingInitialCount
        {
            get;
        }
        /// <summary>是否正位于迭代通知循环内。</summary>
        public bool IsFlushing
        {
            get;
        }
        /// <summary>按 Ordinal 路径排序的只读节点副本，含已创建的零值节点。</summary>
        public IReadOnlyList<ReddotDiagnosticNode> Nodes
        {
            get;
        }

        internal ReddotDiagnosticSnapshot(long generation, long captureSequence, int batchDepth, int dirtyNodeCount,
            int pendingInitialCount, bool isFlushing,
            IReadOnlyList<ReddotDiagnosticNode> nodes)
        {
            Generation = generation;
            CaptureSequence = captureSequence;
            BatchDepth = batchDepth;
            DirtyNodeCount = dirtyNodeCount;
            PendingInitialCount = pendingInitialCount;
            IsFlushing = isFlushing;
            Nodes = nodes;
        }
    }

    /// <summary>节点自身与本层来源的值副本；Sources 不含后代来源、委托或可写树引用。</summary>
    public sealed class ReddotDiagnosticNode
    {
        /// <summary>以斜杠结尾的规范路径；没有隐含空路径根。</summary>
        public string Path
        {
            get;
        }
        /// <summary>采样时的节点聚合值。</summary>
        public long Value
        {
            get;
        }
        /// <summary>本层集合、租约及数值来源之和，不含后代值。</summary>
        public long OwnValue
        {
            get;
        }
        /// <summary>本节点组合自身值与直接子节点值的规则。</summary>
        public ReddotAggregation Aggregation
        {
            get;
        }
        /// <summary>本节点是否持有 AddPath 的幂等集合来源。</summary>
        public bool HasPath
        {
            get;
        }
        /// <summary>本节点仍有效的 RegisterPath 独立来源数。</summary>
        public long LeaseCount
        {
            get;
        }
        /// <summary>本节点实际路径订阅数，不暴露回调或目标对象。</summary>
        public int SubscriberCount
        {
            get;
        }
        /// <summary>采样时是否在等待通知的脏节点集合中。</summary>
        public bool IsDirty
        {
            get;
        }
        /// <summary>仅本节点自有的数值来源副本，按 Ordinal ID 排序。</summary>
        public IReadOnlyList<ReddotDiagnosticSource> Sources
        {
            get;
        }

        internal ReddotDiagnosticNode(string path, long value, long ownValue, ReddotAggregation aggregation,
            bool hasPath, long leaseCount, int subscriberCount, bool isDirty, IReadOnlyList<ReddotDiagnosticSource> sources)
        {
            Path = path;
            Value = value;
            OwnValue = ownValue;
            Aggregation = aggregation;
            HasPath = hasPath;
            LeaseCount = leaseCount;
            SubscriberCount = subscriberCount;
            IsDirty = isDirty;
            Sources = sources;
        }
    }

    /// <summary>单个显式数值来源的 ID/值副本，不持有 ReddotSource 凭据。</summary>
    public sealed class ReddotDiagnosticSource
    {
        /// <summary>业务登记的原始、区分大小写来源 ID。</summary>
        public string SourceId
        {
            get;
        }
        /// <summary>采样时的数值来源值，与之后的修改或释放独立。</summary>
        public long Value
        {
            get;
        }

        internal ReddotDiagnosticSource(string sourceId, long value)
        {
            SourceId = sourceId;
            Value = value;
        }
    }

    /// <summary>节点本层聚合规则；自身来源先相加，再与直接子节点的聚合值组合。</summary>
    public enum ReddotAggregation
    {
        /// <summary>自身来源总和加所有直接子节点聚合值。</summary>
        Sum,
        /// <summary>自身来源或任一直接子节点为正时为 1，否则为 0。</summary>
        Any,
        /// <summary>自身来源总和与各直接子节点聚合值的最大值。</summary>
        Max
    }

    /// <summary>规范路径及聚合值的不可变快照，不持有树或来源的可写引用。</summary>
    public readonly struct ReddotSnapshot
    {
        /// <summary>规范化后的路径；空字符串不代表默认根。</summary>
        public string Path
        {
            get;
        }
        /// <summary>快照创建时的非负聚合值。</summary>
        public long Value
        {
            get;
        }

        internal ReddotSnapshot(string path, long value)
        {
            Path = path;
            Value = value;
        }
    }

    /// <summary>唯一数值来源的主线程凭据；释放幂等，重置后不得继续写入。</summary>
    public sealed class ReddotSource : IDisposable
    {
        private long _value;
        private bool _isDisposed;
        internal readonly string Path;
        internal readonly string SourceId;
        internal readonly object Generation;

        /// <summary>最后一次成功写入的来源值；释放或重置不会改写凭据中保存的历史值。</summary>
        public long Value => _value;

        /// <summary>仅供同程序集组合来源检查重置及释放边界，不扩展公开来源契约。</summary>
        internal bool IsCurrent => !_isDisposed && ReddotManager.IsCurrent(this);

        internal ReddotSource(string path, string sourceId, long value, object generation)
        {
            Path = path;
            SourceId = sourceId;
            _value = value;
            Generation = generation;
        }

        /// <summary>写入非负来源值；失效凭据抛 ObjectDisposedException，溢出时整条写入不生效。</summary>
        public void SetValue(long value) => ReddotManager.SetSourceValue(this, value);
        /// <summary>仅释放本次来源，通知失败仍已释放；旧代际及重复释放不影响后来来源。</summary>
        public void Dispose() => ReddotManager.ReleaseSource(this);

        internal void StoreValue(long value) => _value = value;
        internal void Invalidate() => _isDisposed = true;
    }
}
