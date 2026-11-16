using System;
using System.Collections.Generic;
using UnityEngine.AddressableAssets;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.SmartFormat.PersistentVariables;
using UnityEngine.ResourceManagement.AsyncOperations;
using YLFramework.Binding;

namespace YLFramework.Localization
{
    /// <summary>会话私有的官方字符串订阅；模板和已有变量只借用、不写入。</summary>
    public sealed class LocalizedStringBinding : IDisposable, ILocalizationBinding
    {
        private readonly LocalizationSession _session;
        private readonly LocalizedString _template;
        private readonly Action<string> _apply;
        private readonly Dictionary<string, IBoundLocalizationVariable> _variables = new Dictionary<string, IBoundLocalizationVariable>(StringComparer.Ordinal);
        private LocalizedString _reference;
        private LocalizedString.ChangeHandler _changed;
        private AsyncOperationHandle<LocalizedStringDatabase.TableEntryResult> _monitor;
        private Action<AsyncOperationHandle<LocalizedStringDatabase.TableEntryResult>> _completed;
        private int _generation;
        private string _value;
        private bool _isPending;
        private Locale _locale;
        private bool _cleanupComplete;

        /// <summary>当前值已完成加载/格式化。</summary>
        public bool IsReady
        {
            get; private set;
        }
        /// <summary>本绑定已释放。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>当前绑定的加载或交付错误。</summary>
        public Exception Error
        {
            get; private set;
        }
        int ILocalizationBinding.CommitOrder => 20;

        internal LocalizedStringBinding(LocalizationSession session, LocalizedString reference, Action<string> apply)
        {
            _session = session;
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _template = Copy(reference ?? throw new ArgumentNullException(nameof(reference)));
            if (_template.IsEmpty)
                throw new ArgumentException("Choose a String Table entry.", nameof(reference));
        }

        /// <summary>
        /// 绑定一个命名业务变量，保留 T 交给官方数值/复数格式化。释放句柄会退订并冻结最后值；
        /// 不允许覆盖模板变量或仍有效的本方变量，已释放的本方槽位可以显式重新绑定。
        /// </summary>
        public IDisposable BindVariable<T>(string name, IReadOnlyBindableProperty<T> source)
        {
            _session.CheckActive();
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(LocalizedStringBinding));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A variable needs a name.", nameof(name));
            foreach (var character in name)
                if (char.IsWhiteSpace(character))
                    throw new ArgumentException("Variable names cannot contain whitespace.", nameof(name));
            if (_template.ContainsKey(name))
            {
                if (!_variables.TryGetValue(name, out var existing) || !existing.IsDisposed)
                    throw new ArgumentException("The local variable already exists: " + name, nameof(name));
                existing.Dispose();
                _template.Remove(name);
            }
            var variable = new BindableLocalizationVariable<T>(source, _session.CheckThread, Fail);
            try
            {
                _template.Add(name, variable);
                _variables[name] = variable;
                if (_reference != null)
                    Refresh(_locale);
                return variable;
            }
            catch
            {
                _template.Remove(name);
                _variables.Remove(name);
                variable.Dispose();
                throw;
            }
        }

        private static LocalizedString Copy(LocalizedString source)
        {
            var result = new LocalizedString
            {
                TableReference = source.TableReference,
                TableEntryReference = source.TableEntryReference,
                FallbackState = source.FallbackState,
                WaitForCompletion = source.WaitForCompletion,
                LocaleOverride = source.LocaleOverride,
                Arguments = source.Arguments == null ? null : new List<object>(source.Arguments)
            };
            foreach (var variable in (IEnumerable<KeyValuePair<string, IVariable>>)source)
                result.Add(variable.Key, variable.Value);
            return result;
        }

        void ILocalizationBinding.Refresh(Locale locale) => Refresh(locale);

        internal void Refresh(Locale locale)
        {
            if (IsDisposed)
                return;
            _locale = locale;
            ClearReference();
            IsReady = false;
            Error = null;
            _isPending = false;
            var generation = ++_generation;
            _session.NotifyChanged();
            try
            {
                foreach (var variable in _variables.Values)
                    variable.Resume();
                _reference = Copy(_template);
                _changed = value =>
                {
                    if (IsDisposed || generation != _generation)
                        return;
                    _value = value;
                    Error = null;
                    IsReady = true;
                    _isPending = true;
                    _session.NotifyChanged();
                };
                _reference.StringChanged += _changed;
                var operation = _reference.CurrentLoadingOperationHandle;
                if (!operation.IsValid())
                {
                    if (!IsReady)
                        Fail(new InvalidOperationException("The String Table entry could not be loaded."));
                    return;
                }
                _monitor = Addressables.ResourceManager.Acquire(operation);
                _completed = result =>
                {
                    if (IsDisposed || generation != _generation)
                        return;
                    if (result.Status != AsyncOperationStatus.Succeeded)
                        Fail(result.OperationException ?? new InvalidOperationException("String Table loading failed."));
                    else if (!IsReady)
                        Fail(new FormatException("The String Table entry did not produce a localized value."));
                };
                if (_monitor.IsDone)
                    _completed(_monitor);
                else
                    _monitor.Completed += _completed;
            }
            catch (Exception error) { Fail(error); }
        }

        private void Fail(Exception error)
        {
            Error = error;
            IsReady = false;
            _isPending = false;
            _session.NotifyChanged();
        }

        void ILocalizationBinding.Commit()
        {
            if (IsDisposed || !IsReady || !_isPending)
                return;
            _isPending = false;
            try
            {
                _apply(_value);
            }
            catch (Exception error) { Fail(error); }
        }

        void ILocalizationBinding.Suspend()
        {
            if (IsDisposed)
                return;
            ++_generation;
            ClearReference();
            foreach (var variable in _variables.Values)
                variable.Suspend();
            IsReady = false;
            _isPending = false;
        }

        private void ClearReference()
        {
            var reference = _reference;
            _reference = null;
            if (_monitor.IsValid())
            {
                _monitor.Completed -= _completed;
                Addressables.Release(_monitor);
            }
            _monitor = default;
            _completed = null;
            if (reference != null)
            {
                reference.StringChanged -= _changed;
                _session.RetireReference(reference);
            }
            _changed = null;
        }

        /// <summary>精确退订本引用，不清除模板或全局监听。</summary>
        public void Dispose()
        {
            if (_cleanupComplete)
                return;
            _session.CheckThread();
            IsDisposed = true;
            ++_generation;
            List<Exception> errors = null;
            try
            {
                ClearReference();
            }
            catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
            foreach (var variable in _variables.Values)
            {
                try
                {
                    variable.Dispose();
                }
                catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
            }
            try
            {
                ((IDisposable)_template).Dispose();
            }
            catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
            IsReady = false;
            if (errors != null)
                throw new AggregateException(errors);
            _cleanupComplete = true;
            _session.Remove(this);
        }
    }
}
