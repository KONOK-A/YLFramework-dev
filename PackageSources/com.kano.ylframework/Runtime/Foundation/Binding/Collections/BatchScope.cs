using System;

namespace YLFramework.Binding
{
    /// <summary>
    /// 一次性批量作用域：Dispose 时结束批量，重复 Dispose 安全
    /// </summary>
    internal sealed class BatchScope : IDisposable
    {
        private Action _end;

        /// <summary>保存结束批量通知的回调；null 表示释放时无需执行动作。</summary>
        public BatchScope(Action end)
        {
            _end = end;
        }

        /// <summary>先消费结束回调再执行，使重入或重复释放不重复结束批量；回调异常直接向上传播。</summary>
        public void Dispose()
        {
            var end = _end;
            _end = null;
            end?.Invoke();
        }
    }
}
