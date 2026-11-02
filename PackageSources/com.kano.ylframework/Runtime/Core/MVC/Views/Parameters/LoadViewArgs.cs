using System;
using System.Collections.Generic;
using System.Threading;
using YLFramework.Pooling;
using YLFramework.Core.RoslynAnalyzers;
using YLFramework.Singletons;



namespace YLFramework.Core
{
    /// <summary>
    /// 加载 View 的参数，只能在主线程使用 New 创建，并在同一线程完成使用和归还。
    /// 接管前可读写；接管后只有 OnLoaded 回调的借用窗口可读，操作终态立即归还并清引用。
    /// </summary>
    /// <remarks>
    /// 只能通过 New 和加载操作的专用入口管理生命周期，不支持通用池接管。
    /// 不为每次租用分配 token：原始池对象重新租用后无法识别非法旧别名，禁止保存参数引用。
    /// </remarks>
    [DisableNew("请使用静态方法 LoadViewArgs.New() 来创建对象!")]
    [MethodLocalOnlyAttribute("该类会在加载操作终态自动回收,请勿保存参数引用!")]
    public class LoadViewArgs
    {
        #region 字段

        private static readonly ObjectPool<LoadViewArgs> _pool = new(
            () => new LoadViewArgs(), onAllocate: args => args.BeginLease(),
            reset: args => args.ResetLease(), cacheOrder: PoolCacheOrder.Lifo);

        /// <summary>在执行会话重置时清空闲置缓存，仍在使用的参数继续由原操作归还。</summary>
        static LoadViewArgs()
        {
            SingletonSessionState.RegisterSessionReset(_pool.Clear);
        }

#pragma warning disable IDE0044 // 添加只读修饰符
        private Dictionary<string, string> _strings = new Dictionary<string, string>();

        private Dictionary<string, int> _ints = new Dictionary<string, int>();

        private Dictionary<string, bool> _bools = new Dictionary<string, bool>();

        private Dictionary<string, float> _floats = new Dictionary<string, float>();

        private Dictionary<string, long> _longs = new Dictionary<string, long>();

        private Dictionary<string, double> _doubles = new Dictionary<string, double>();

        private Dictionary<string, object> _objects = new Dictionary<string, object>();

        private bool _isLeased;
        private bool _isOwned;
        private bool _isReading;
        private int _threadId;

#pragma warning restore IDE0044 // 添加只读修饰符

        #endregion

        #region 查询

        /// <summary>在可读窗口按键读取字符串；该类型分区缺失此键时返回空字符串。</summary>
        public string GetString(string name)
        {
            EnsureReadable();
            if (_strings.ContainsKey(name))
            {
                return _strings[name];
            }

            return string.Empty;
        }

        /// <summary>在可读窗口按键读取 int；缺失时返回 0，不向其他数值分区转换。</summary>
        public int GetInt(string name)
        {
            EnsureReadable();
            if (_ints.ContainsKey(name))
            {
                return _ints[name];
            }

            return 0;
        }

        /// <summary>在可读窗口按键读取 bool；缺失时返回 false。</summary>
        public bool GetBool(string name)
        {
            EnsureReadable();
            if (_bools.ContainsKey(name))
            {
                return _bools[name];
            }

            return false;
        }

        /// <summary>在可读窗口按键读取 float；缺失时返回 0，不向其他数值分区转换。</summary>
        public float GetFloat(string name)
        {
            EnsureReadable();
            if (_floats.ContainsKey(name))
            {
                return _floats[name];
            }

            return 0.0f;
        }

        /// <summary>在可读窗口按键读取 long；缺失时返回 0，不向其他数值分区转换。</summary>
        public long GetLong(string name)
        {
            EnsureReadable();
            if (_longs.ContainsKey(name))
            {
                return _longs[name];
            }

            return 0;
        }

        /// <summary>在可读窗口按键读取 double；缺失时返回 0，不向其他数值分区转换。</summary>
        public double GetDouble(string name)
        {
            EnsureReadable();
            if (_doubles.ContainsKey(name))
            {
                return _doubles[name];
            }

            return 0.0;
        }

        /// <summary>在可读窗口按键借读对象引用；缺失时返回 null，不转移对象所有权。</summary>
        public object GetObject(string name)
        {
            EnsureReadable();
            if (_objects.ContainsKey(name))
            {
                return _objects[name];
            }

            return null;
        }

        #endregion

        #region 修改

        /// <summary>在加载操作接管前写入或覆盖字符串分区的键值。</summary>
        public void SetString(string name, string value)
        {
            EnsureWritable();
            _strings[name] = value;
        }

        /// <summary>在加载操作接管前写入或覆盖 int 分区的键值。</summary>
        public void SetInt(string name, int value)
        {
            EnsureWritable();
            _ints[name] = value;
        }

        /// <summary>在加载操作接管前写入或覆盖 bool 分区的键值。</summary>
        public void SetBool(string name, bool value)
        {
            EnsureWritable();
            _bools[name] = value;
        }

        /// <summary>在加载操作接管前写入或覆盖 float 分区的键值。</summary>
        public void SetFloat(string name, float value)
        {
            EnsureWritable();
            _floats[name] = value;
        }

        /// <summary>在加载操作接管前写入或覆盖 long 分区的键值。</summary>
        public void SetLong(string name, long value)
        {
            EnsureWritable();
            _longs[name] = value;
        }

        /// <summary>在加载操作接管前写入或覆盖 double 分区的键值。</summary>
        public void SetDouble(string name, double value)
        {
            EnsureWritable();
            _doubles[name] = value;
        }

        /// <summary>在加载操作接管前保存对象引用；参数归还只清除引用，不释放对象。</summary>
        public void SetObject(string name, object value)
        {
            EnsureWritable();
            _objects[name] = value;
        }

        #endregion

        #region 生命周期

        /// <summary>开始一次干净的本地租用，记录线程并清除旧数据，不授予加载操作所有权。</summary>
        private void BeginLease()
        {
            if (_isLeased)
            {
                throw new InvalidOperationException("The pool returned leased LoadViewArgs.");
            }

            ClearValues();
            _threadId = Thread.CurrentThread.ManagedThreadId;
            _isOwned = false;
            _isReading = false;
            _isLeased = true;
        }

        /// <summary>归还时清空值与租用标记，使旧引用在再次租用前不可访问。</summary>
        private void ResetLease()
        {
            ClearValues();
            _isReading = false;
            _isOwned = false;
            _isLeased = false;
        }

        /// <summary>接管本次租用。失败不改变既有所有权或数据。</summary>
        internal void Claim()
        {
            EnsureWritable();
            _isOwned = true;
        }

        /// <summary>归还已接管参数一次；已归还时无操作。</summary>
        internal void Release()
        {
            EnsureThread();
            if (!_isLeased)
            {
                return;
            }

            if (!_isOwned)
            {
                throw new InvalidOperationException("LoadViewArgs has not been claimed.");
            }

            ReturnToPool();
        }

        /// <summary>仅在调用业务 OnLoaded 前开放临时只读借用，必须配对 EndRead。</summary>
        internal void BeginRead()
        {
            EnsureLeased();
            if (!_isOwned || _isReading)
            {
                throw new InvalidOperationException("LoadViewArgs must be owned without an active read window.");
            }

            _isReading = true;
        }

        /// <summary>结束业务回调的读取权，不归还参数。</summary>
        internal void EndRead()
        {
            EnsureLeased();
            if (!_isOwned || !_isReading)
            {
                throw new InvalidOperationException("LoadViewArgs has no active read window.");
            }

            _isReading = false;
        }

        /// <summary>拒绝从 New 所在线程之外访问这次参数租用。</summary>
        private void EnsureThread()
        {
            if (_threadId != 0 && _threadId != Thread.CurrentThread.ManagedThreadId)
            {
                throw new InvalidOperationException("LoadViewArgs must stay on the thread that called New.");
            }
        }

        /// <summary>要求参数仍处于租用期且未跨线程，归还后的引用不能继续使用。</summary>
        private void EnsureLeased()
        {
            if (!_isLeased)
            {
                throw new ObjectDisposedException(nameof(LoadViewArgs));
            }

            EnsureThread();
        }

        /// <summary>允许未接管租用读取；接管后只在加载回调开放的借读窗口内允许读取。</summary>
        private void EnsureReadable()
        {
            EnsureLeased();
            if (_isOwned && !_isReading)
            {
                throw new InvalidOperationException("LoadViewArgs is only readable during OnLoaded after being claimed.");
            }
        }

        /// <summary>只允许调用方修改或主动归还尚未被加载操作接管的租用。</summary>
        private void EnsureWritable()
        {
            EnsureLeased();
            if (_isOwned)
            {
                throw new InvalidOperationException("Claimed LoadViewArgs cannot be modified or recycled by the caller.");
            }
        }

        /// <summary>清空全部类型分区，解除参数对业务对象的持有，不负责这些对象的释放。</summary>
        private void ClearValues()
        {
            _strings.Clear();
            _ints.Clear();
            _bools.Clear();
            _floats.Clear();
            _longs.Clear();
            _doubles.Clear();
            _objects.Clear();
        }

        /// <summary>通过专用池归还当前参数，由池重置租用状态并清除引用。</summary>
        private void ReturnToPool()
        {
            _pool.Recycle(this);
        }

        #endregion

        #region 构造方法

        /// <summary>
        /// 在主线程开始本地租用。接管前由调用方读写，交给加载操作后不得保存引用。
        /// </summary>
        /// <returns>本次租用的空参数容器，不得跨加载操作保存或复用引用。</returns>
        public static LoadViewArgs New()
        {
            return _pool.Allocate();
        }

        /// <summary>归还未接管参数；已归还时无操作，已接管时拒绝。</summary>
        internal static void Recycle(LoadViewArgs args)
        {
            if (args == null)
            {
                return;
            }

            args.EnsureThread();
            if (!args._isLeased)
            {
                return;
            }

            args.EnsureWritable();
            args.ReturnToPool();
        }

        #endregion 

    }

}
