using System;
using System.Collections.Generic;
using YLFramework.Singletons;
using YLFramework.Events;

namespace YLFramework.RedDots
{
    /// <summary>主线程路径红点。普通路径为集合，独立来源持有自己的释放凭据。</summary>
    /// <remarks>
    /// 按大小写敏感的完整路径段匹配，自身或任一后代存在来源即可点亮祖先；祖先登记不会反向点亮后代。
    /// 不维护额外的树节点缓存，变更时同步通知观察者重新查询；通知异常不在本管理器内吞掉。
    /// </remarks>
    public static class ReddotManager
    {
        private static readonly HashSet<string> _paths = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _sources = new(StringComparer.Ordinal);
        private static EventChannel _changed = new();
        private static long _generation;

        /// <summary>注册会话重置，避免关闭 Domain Reload 时沿用上一轮的路径或订阅。</summary>
        static ReddotManager() => SingletonSessionState.RegisterSessionReset(ResetSession);

        /// <summary>普通与独立来源路径的去重、按 Ordinal 排序只读快照，不能绕过通知修改内部集合。</summary>
        public static IReadOnlyList<string> ReddotPath
        {
            get
            {
                var paths = new HashSet<string>(_paths, StringComparer.Ordinal);
                paths.UnionWith(_sources.Keys);
                var result = new List<string>(paths);
                result.Sort(StringComparer.Ordinal);
                return result.AsReadOnly();
            }
        }

        /// <summary>订阅路径状态变化，不补发初始状态；初始显示由观察者自行查询，并在所属生命周期结束时解绑。</summary>
        /// <param name="callback">变更后重新查询聚合状态的同步回调。</param>
        /// <returns>用于解除本次订阅的凭据。</returns>
        public static IUnregister RegisterChanged(Action callback) => _changed.Register(callback);

        /// <summary>斜杠分段，忽略空段与段外空白；空路径不表示根节点。</summary>
        /// <param name="path">原始业务路径，允许为 null 或空白。</param>
        /// <returns>空字符串，或以斜杠结尾的规范路径。</returns>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
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

            // 末尾保留分隔符，使 Mail/ 的前缀匹配不会误命中 Mailbox/。
            return result.Count == 0 ? string.Empty : string.Join("/", result) + "/";
        }

        /// <summary>加入普通路径集合，重复或空路径不改变状态，也不发通知。</summary>
        /// <param name="path">要登记的业务路径，将先规范化。</param>
        public static void AddPath(string path)
        {
            path = NormalizePath(path);
            if (path.Length != 0 && _paths.Add(path))
            {
                _changed.TriggerAll();
            }
        }

        /// <summary>批量加入普通路径集合，仅在至少一个路径实际新增时统一通知一次。</summary>
        /// <param name="paths">要登记的路径列表；null 不执行任何操作。</param>
        public static void AddPath(IList<string> paths)
        {
            if (paths == null)
            {
                return;
            }

            var changed = false;
            foreach (var value in paths)
            {
                var path = NormalizePath(value);
                if (path.Length != 0)
                {
                    changed |= _paths.Add(path);
                }
            }
            if (changed)
            {
                _changed.TriggerAll();
            }
        }

        /// <summary>只撤销 AddPath 的集合来源，不夺取其他所有者的登记；无匹配项时不通知。</summary>
        /// <param name="path">要移除的业务路径，将先规范化。</param>
        public static void RemovePath(string path)
        {
            if (_paths.Remove(NormalizePath(path)))
            {
                _changed.TriggerAll();
            }
        }

        /// <summary>同路径可以独立登记多次；所有者退出必须 Dispose，Reset 会使旧凭据失效。</summary>
        /// <param name="path">至少包含一个非空段的业务路径。</param>
        /// <returns>仅释放本次来源的幂等凭据；普通集合来源与其他凭据不受影响。</returns>
        /// <remarks>登记通知失败时撤销本次来源并通知回滚；两次通知均失败时合并异常向外抛出。</remarks>
        public static IDisposable RegisterPath(string path)
        {
            path = NormalizePath(path);
            if (path.Length == 0)
            {
                throw new ArgumentException("红点路径至少需要一个非空段。", nameof(path));
            }

            _sources.TryGetValue(path, out var count);
            _sources[path] = count + 1;
            var registration = new Registration(path, _generation);
            try
            {
                _changed.TriggerAll();
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
        public static bool ShouldBeDisplayed(string path)
        {
            path = NormalizePath(path);
            if (path.Length == 0)
            {
                return false;
            }

            foreach (var item in _paths)
            {
                if (item.StartsWith(path, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            foreach (var item in _sources.Keys)
            {
                if (item.StartsWith(path, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>清空全部来源并使旧凭据失效，保留订阅；原状态非空时才发出一次变更通知。</summary>
        public static void Reset()
        {
            var changed = _paths.Count != 0 || _sources.Count != 0;
            _generation++;
            _paths.Clear();
            _sources.Clear();
            if (changed)
            {
                _changed.TriggerAll();
            }
        }

        /// <summary>切换会话时清空来源、使旧凭据失效，并释放旧事件通道以移除上一轮订阅。</summary>
        private static void ResetSession()
        {
            _generation++;
            _paths.Clear();
            _sources.Clear();
            _changed.Dispose();
            _changed = new EventChannel();
        }

        /// <summary>绑定路径与登记代际的独立来源凭据，避免重置后的旧释放影响新来源。</summary>
        private sealed class Registration : IDisposable
        {
            private string _path;
            private readonly long _generation;
            /// <summary>记录本次登记路径和代际，由管理器预先增加对应来源计数。</summary>
            internal Registration(string path, long generation)
            {
                _path = path;
                _generation = generation;
            }
            /// <summary>仅首次在同代际释放自己的来源计数并通知；重置后的旧凭据直接失效。</summary>
            public void Dispose()
            {
                var path = _path;
                _path = null;
                if (path == null || _generation != ReddotManager._generation)
                {
                    return;
                }

                if (_sources[path] == 1)
                {
                    _sources.Remove(path);
                }
                else
                {
                    _sources[path]--;
                }

                _changed.TriggerAll();
            }
        }
    }
}
