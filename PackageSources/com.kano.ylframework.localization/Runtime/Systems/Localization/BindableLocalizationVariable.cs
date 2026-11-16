using System;
using UnityEngine.Localization.SmartFormat.Core.Extensions;
using UnityEngine.Localization.SmartFormat.PersistentVariables;
using YLFramework.Binding;
using YLFramework.Events;

namespace YLFramework.Localization
{
    internal interface IBoundLocalizationVariable : IVariable, IDisposable
    {
        bool IsDisposed
        {
            get;
        }
        void Suspend();
        void Resume();
    }

    internal sealed class BindableLocalizationVariable<T> : IBoundLocalizationVariable, IVariableValueChanged
    {
        private IReadOnlyBindableProperty<T> _source;
        private readonly Action _checkThread;
        private readonly Action<Exception> _onError;
        private IUnregister _registration;
        private T _value;
        private bool _isSuspended;

        public bool IsDisposed
        {
            get; private set;
        }
        public event Action<IVariable> ValueChanged;

        public BindableLocalizationVariable(IReadOnlyBindableProperty<T> source, Action checkThread, Action<Exception> onError)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _checkThread = checkThread;
            _onError = onError;
            _value = source.Value;
            Resume();
        }

        public object GetSourceValue(ISelectorInfo selector)
        {
            _checkThread();
            if (!IsDisposed && !_isSuspended)
                _value = _source.Value;
            return _value;
        }

        private void HandleChanged(T value)
        {
            if (IsDisposed || _isSuspended)
                return;
            _checkThread();
            _value = value;
            try
            {
                ValueChanged?.Invoke(this);
            }
            catch (Exception error) { _onError(error); }
        }

        public void Suspend()
        {
            _isSuspended = true;
            // 注销异常时保留句柄，后续 Dispose 仍可重试，而不丢掉清理所有权。
            _registration?.Unregister();
            _registration = null;
        }

        public void Resume()
        {
            if (IsDisposed)
                return;
            _isSuspended = false;
            if (_registration == null)
                _registration = _source.Register(HandleChanged);
        }

        public void Dispose()
        {
            _checkThread();
            IsDisposed = true;
            _source = null;
            ValueChanged = null;
            Suspend();
        }
    }
}
