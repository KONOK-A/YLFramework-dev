using System;
using YLFramework.Utilities.ZipTool.Zip;

namespace YLFramework.Compression
{
    /// <summary>
    /// nameCodePage 的 try/finally 临时切换作用域。ZipConstants.DefaultCodePage 为进程级静态，非线程安全。
    /// 嵌套使用时按 LIFO 恢复；重复 Dispose 安全。
    /// </summary>
    internal struct CodePageScope : IDisposable
    {
        private readonly bool _isChanged;
        private readonly int _originalCodePage;
        private bool _isDisposed;

        /// <summary>在指定编码时保存并切换进程级条目名编码；传入 null 时不改动编码。</summary>
        public CodePageScope(int? nameCodePage)
        {
            _isChanged = nameCodePage.HasValue;
            _originalCodePage = _isChanged ? ZipConstants.DefaultCodePage : 0;
            _isDisposed = false;
            if (_isChanged)
            {
                ZipConstants.DefaultCodePage = nameCodePage.Value;
            }
        }

        /// <summary>为当前作用域实例恢复一次原编码，不关闭归档或流，也不提供并发互斥。</summary>
        public void Dispose()
        {
            if (_isChanged && !_isDisposed)
            {
                _isDisposed = true;
                ZipConstants.DefaultCodePage = _originalCodePage;
            }
        }
    }
}
