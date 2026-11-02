using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Singletons;

namespace YLFramework.DataAssets
{
    /// <summary>集中跟踪配置加载与登记的释放责任，在会话重置或退出时执行收尾。</summary>
    internal static class DataAssetLifetime
    {
        private static readonly HashSet<IDisposable> _owners = new HashSet<IDisposable>();
        private static bool _isClearing;
        /// <summary>每次清理推进的代次，用于拒绝跨越清理边界的同步加载结果。</summary>
        internal static int Generation
        {
            get; private set;
        }

        /// <summary>将配置责任域接入单例会话重置与应用退出通知。</summary>
        static DataAssetLifetime()
        {
            SingletonSessionState.RegisterSessionReset(Clear);
            Application.quitting += Clear;
        }

        /// <summary>纳入待释放持有者；清理回调期间拒绝接受新责任。</summary>
        internal static void Track(IDisposable owner)
        {
            if (_isClearing)
            {
                throw new InvalidOperationException("The DataAssets session is closing.");
            }

            _owners.Add(owner);
        }

        /// <summary>移除已结束责任的持有者，不再次调用其释放方法。</summary>
        internal static void Untrack(IDisposable owner) => _owners.Remove(owner);

        /// <summary>推进代次并逐一尝试释放快照中的持有者，最终清空集合并汇总释放异常。</summary>
        internal static void Clear()
        {
            if (_isClearing)
            {
                return;
            }

            _isClearing = true;
            unchecked
            {
                Generation++;
            }
            List<Exception> errors = null;
            try
            {
                foreach (var owner in new List<IDisposable>(_owners))
                {
                    try
                    {
                        owner.Dispose();
                    }
                    catch (Exception error)
                    {
                        (errors ??= new List<Exception>()).Add(error);
                    }
                }
            }
            finally
            {
                _owners.Clear();
                _isClearing = false;
            }
            if (errors != null)
            {
                throw new AggregateException("DataAssets cleanup failed.", errors);
            }
        }
    }
}
