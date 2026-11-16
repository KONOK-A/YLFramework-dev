using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.ResourceManagement.AsyncOperations;
using YLFramework.Singletons;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Localization
{
    /// <summary>主线程消费者的本地化绑定作用域；不拥有官方全局设置、表或业务数据。</summary>
    public sealed class LocalizationSession : IDisposable
    {
        private static readonly HashSet<LocalizationSession> _sessions = new HashSet<LocalizationSession>();
        private static bool _isClosing;
        private readonly SynchronizationContext _context;
        private readonly int _threadId;
        private readonly List<ILocalizationBinding> _bindings = new List<ILocalizationBinding>();
        private readonly List<LocalizedString> _retiredReferences = new List<LocalizedString>();
        private AsyncOperationHandle<LocalizationSettings> _initialization;
        private bool _isInitialized;
        private bool _isSuspended;
        private bool _isPosted;
        private int _configurationDepth;
        private bool _localeRefreshPending;
        private bool _retirementPosted;
        private Locale _locale;
        private Exception _error;

        internal bool DeferDelivery
        {
            get; set;
        }
        internal event Action Changed;

        /// <summary>本会话已释放后不能再次绑定或恢复。</summary>
        public bool IsDisposed
        {
            get; private set;
        }

        /// <summary>初始化或当前绑定的第一个失败；失败不会被伪装成就绪。</summary>
        public Exception Error
        {
            get
            {
                if (_error != null)
                    return _error;
                foreach (var binding in _bindings)
                    if (!binding.IsDisposed && binding.Error != null)
                        return binding.Error;
                return null;
            }
        }

        /// <summary>官方初始化及本轮所有有效绑定均已完成。</summary>
        public bool IsReady
        {
            get
            {
                if (IsDisposed || _isSuspended || _localeRefreshPending || !_isInitialized || Error != null)
                    return false;
                foreach (var binding in _bindings)
                    if (!binding.IsDisposed && !binding.IsReady)
                        return false;
                return true;
            }
        }

        static LocalizationSession()
        {
            SingletonSessionState.RegisterSessionReset(CloseSessions);
            Application.quitting += CloseSessions;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.ExitingEditMode)
                    CloseSessions();
            };
#endif
        }

        /// <summary>在 Unity 主线程创建；项目必须已显式配置官方 Localization Settings。</summary>
        public LocalizationSession()
        {
            if (_isClosing || SingletonSessionState.IsApplicationQuitting)
                throw new InvalidOperationException("Localization sessions are closing.");
            _context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("Create localization sessions on the Unity main thread.");
            _threadId = Thread.CurrentThread.ManagedThreadId;
            if (!LocalizationSettings.HasSettings)
                throw new InvalidOperationException("Configure Unity Localization Settings before creating a session.");
            _sessions.Add(this);
            LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
            try
            {
                _initialization = LocalizationSettings.InitializationOperation;
                if (_initialization.IsDone)
                    HandleInitialized(_initialization);
                else
                    _initialization.Completed += HandleInitialized;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>复制引用模板，先配置局部变量，再激活官方字符串订阅。</summary>
        public LocalizedStringBinding BindString(LocalizedString reference, Action<string> apply,
            Action<LocalizedStringBinding> configure = null)
        {
            CheckActive();
            var binding = new LocalizedStringBinding(this, reference, apply);
            _bindings.Add(binding);
            try
            {
                configure?.Invoke(binding);
                CheckActive();
                if (_isInitialized && !_isSuspended && !_localeRefreshPending && _configurationDepth == 0)
                    binding.Refresh(_locale);
                return binding;
            }
            catch
            {
                binding.Dispose();
                throw;
            }
        }

        /// <summary>绑定类型化官方资产引用；本会话只归还自己持有的句柄引用。</summary>
        public IDisposable BindAsset<T>(LocalizedAsset<T> reference, Action<T> apply) where T : UnityEngine.Object
        {
            CheckActive();
            var binding = new LocalizedAssetBinding<T>(this, reference, apply);
            _bindings.Add(binding);
            if (_isInitialized && !_isSuspended && !_localeRefreshPending && _configurationDepth == 0)
                binding.Refresh(_locale);
            return binding;
        }

        /// <summary>重新请求本会话内容，不清空官方数据库或其他消费者。</summary>
        public void Refresh()
        {
            CheckActive();
            if (!_isInitialized || _isSuspended || _configurationDepth != 0)
                return;
            _error = null;
            foreach (var binding in _bindings.ToArray())
                if (!binding.IsDisposed)
                    binding.Refresh(_locale);
            NotifyChanged();
        }

        internal void BeginConfiguration()
        {
            CheckActive();
            _configurationDepth++;
        }

        internal void EndConfiguration()
        {
            CheckActive();
            if (_configurationDepth == 0)
                throw new InvalidOperationException("No localization configuration is active.");
            _configurationDepth--;
            if (_configurationDepth == 0)
                Refresh();
        }

        internal void NotifyChanged()
        {
            if (IsDisposed || _isPosted)
                return;
            _isPosted = true;
            // 官方 CallbackArray 正在派发时退订存在延迟；退出该栈后才向消费者交付。
            _context.Post(_ =>
            {
                _isPosted = false;
                if (IsDisposed || _isSuspended)
                    return;
                Changed?.Invoke();
                if (!DeferDelivery)
                    Commit();
            }, null);
        }

        internal void Commit()
        {
            if (IsDisposed || _isSuspended || _localeRefreshPending)
                return;
            var bindings = _bindings.ToArray();
            Array.Sort(bindings, (first, second) => first.CommitOrder.CompareTo(second.CommitOrder));
            foreach (var binding in bindings)
            {
                if (IsDisposed || _isSuspended)
                    break;
                if (!binding.IsDisposed && binding.IsReady)
                    binding.Commit();
            }
        }

        internal void Suspend()
        {
            CheckActive();
            if (_isSuspended)
                return;
            _isSuspended = true;
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
            foreach (var binding in _bindings.ToArray())
                binding.Suspend();
        }

        internal void Resume()
        {
            CheckActive();
            if (!_isSuspended)
                return;
            _isSuspended = false;
            LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
            var selected = LocalizationSettings.SelectedLocaleAsync;
            if (selected.IsDone)
                HandleLocaleChanged(selected.Result);
            else
                HandleInitialized(_initialization);
        }

        internal void Remove(ILocalizationBinding binding)
        {
            _bindings.Remove(binding);
            RemoveClosedOwner();
            NotifyChanged();
        }

        internal void RetireReference(LocalizedString reference)
        {
            _retiredReferences.Add(reference);
            PostRetirement();
        }

        private void PostRetirement()
        {
            if (_retirementPosted || _retiredReferences.Count == 0)
                return;
            _retirementPosted = true;
            // 官方 locale CallbackArray 的退订延后生效；在派发退出后做最终 Dispose，仍保留失败项的所有权。
            _context.Post(_ =>
            {
                try
                {
                    DrainRetiredReferences();
                }
                catch (Exception error)
                {
                    _error = error;
                    Debug.LogException(_error);
                    NotifyChanged();
                }
            }, null);
        }

        private void DrainRetiredReferences()
        {
            _retirementPosted = false;
            List<Exception> errors = null;
            foreach (var reference in _retiredReferences.ToArray())
            {
                try
                {
                    ((IDisposable)reference).Dispose();
                    _retiredReferences.Remove(reference);
                }
                catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
            }
            RemoveClosedOwner();
            if (errors != null)
                throw new AggregateException(errors);
        }

        private void RemoveClosedOwner()
        {
            if (IsDisposed && _bindings.Count == 0 && _retiredReferences.Count == 0)
                _sessions.Remove(this);
        }

        internal void CheckActive()
        {
            CheckThread();
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(LocalizationSession));
        }

        internal void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
                throw new InvalidOperationException("Localization bindings must be used on the Unity main thread.");
        }

        private void HandleInitialized(AsyncOperationHandle<LocalizationSettings> operation)
        {
            if (IsDisposed)
                return;
            if (operation.Status != AsyncOperationStatus.Succeeded)
            {
                _error = operation.OperationException ?? new InvalidOperationException("Localization initialization failed.");
                NotifyChanged();
                return;
            }
            _isInitialized = true;
            var selected = LocalizationSettings.SelectedLocaleAsync;
            if (!selected.IsDone)
            {
                _error = new InvalidOperationException("Localization initialization completed without a selected Locale.");
                NotifyChanged();
                return;
            }
            HandleLocaleChanged(selected.Result);
        }

        private void HandleLocaleChanged(Locale locale)
        {
            if (IsDisposed)
                return;
            _locale = locale;
            if (!_locale)
            {
                _error = new InvalidOperationException("Unity Localization has no selected Locale.");
                NotifyChanged();
                return;
            }
            if (_isInitialized && !_isSuspended)
            {
                if (_localeRefreshPending)
                    return;
                _localeRefreshPending = true;
                NotifyChanged();
                _context.Post(_ =>
                {
                    _localeRefreshPending = false;
                    if (!IsDisposed && !_isSuspended)
                        Refresh();
                }, null);
            }
        }

        /// <summary>幂等结束作用域；即使某个释放动作失败也继续清理其余绑定。</summary>
        public void Dispose()
        {
            if (IsDisposed && _bindings.Count == 0 && _retiredReferences.Count == 0)
                return;
            CheckThread();
            IsDisposed = true;
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
            if (_initialization.IsValid())
                _initialization.Completed -= HandleInitialized;
            Changed = null;
            List<Exception> errors = null;
            foreach (var binding in _bindings.ToArray())
            {
                try
                {
                    binding.Dispose();
                }
                catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
            }
            PostRetirement();
            RemoveClosedOwner();
            if (errors != null)
                throw new AggregateException(errors);
        }

        private static void CloseSessions()
        {
            if (_isClosing)
                return;
            _isClosing = true;
            List<Exception> errors = null;
            try
            {
                foreach (var session in new List<LocalizationSession>(_sessions))
                {
                    try
                    {
                        session.Dispose();
                    }
                    catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
                    // 生命周期 reset 不在官方 locale 派发中；旧上下文消失前必须同步清空本方退休队列。
                    try
                    {
                        session.DrainRetiredReferences();
                    }
                    catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
                }
            }
            finally { _isClosing = false; }
            if (errors != null)
                throw new AggregateException(errors);
        }
    }
}
