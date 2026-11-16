using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace YLFramework.Localization
{
    internal sealed class LocalizedAssetBinding<T> : ILocalizationBinding where T : UnityEngine.Object
    {
        private readonly LocalizationSession _session;
        private readonly LocalizedAsset<T> _reference;
        private readonly Action<T> _apply;
        private AsyncOperationHandle<T> _loading;
        private AsyncOperationHandle<T> _displayed;
        private Action<AsyncOperationHandle<T>> _completed;
        private T _value;
        private bool _isPending;
        private int _generation;
        private bool _cleanupComplete;

        public bool IsReady
        {
            get; private set;
        }
        public bool IsDisposed
        {
            get; private set;
        }
        public Exception Error
        {
            get; private set;
        }
        public int CommitOrder => 0;

        internal LocalizedAssetBinding(LocalizationSession session, LocalizedAsset<T> reference, Action<T> apply)
        {
            if (reference == null)
                throw new ArgumentNullException(nameof(reference));
            if (reference.IsEmpty)
                throw new ArgumentException("Choose an Asset Table entry.", nameof(reference));
            _session = session;
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _reference = new LocalizedAsset<T>
            {
                TableReference = reference.TableReference,
                TableEntryReference = reference.TableEntryReference,
                LocaleOverride = reference.LocaleOverride,
                FallbackState = reference.FallbackState,
                WaitForCompletion = reference.WaitForCompletion
            };
        }

        public void Refresh(Locale locale)
        {
            if (IsDisposed)
                return;
            ClearLoading();
            IsReady = false;
            Error = null;
            _isPending = false;
            var generation = ++_generation;
            _session.NotifyChanged();
            try
            {
                var operation = LocalizationSettings.AssetDatabase.GetLocalizedAssetAsync<T>(
                    _reference.TableReference, _reference.TableEntryReference,
                    _reference.LocaleOverride ? _reference.LocaleOverride : locale, _reference.FallbackState);
                _loading = Application.isPlaying ? Addressables.ResourceManager.Acquire(operation) : operation;
                _completed = result =>
                {
                    if (IsDisposed || generation != _generation)
                        return;
                    if (result.Status != AsyncOperationStatus.Succeeded || !result.Result)
                    {
                        Fail(result.OperationException ?? new InvalidOperationException("The required localized asset is missing."));
                        return;
                    }
                    _value = result.Result;
                    IsReady = true;
                    _isPending = true;
                    _session.NotifyChanged();
                };
                if (_loading.IsDone)
                    _completed(_loading);
                else
                    _loading.Completed += _completed;
            }
            catch (Exception error) { Fail(error); }
        }

        public void Commit()
        {
            if (IsDisposed || !IsReady || !_isPending)
                return;
            _isPending = false;
            var previous = _displayed;
            _displayed = Addressables.ResourceManager.Acquire(_loading);
            try
            {
                _apply(_value);
            }
            catch (Exception error) { Fail(error); }
            finally
            {
                if (previous.IsValid())
                    Addressables.Release(previous);
            }
        }

        private void Fail(Exception error)
        {
            Error = error;
            IsReady = false;
            _isPending = false;
            _session.NotifyChanged();
        }

        private void ClearLoading()
        {
            if (_loading.IsValid())
            {
                _loading.Completed -= _completed;
                Addressables.Release(_loading);
            }
            _loading = default;
            _completed = null;
        }

        public void Suspend()
        {
            ++_generation;
            ClearLoading();
            if (_displayed.IsValid())
                Addressables.Release(_displayed);
            _displayed = default;
            _isPending = false;
            IsReady = false;
        }

        public void Dispose()
        {
            if (_cleanupComplete)
                return;
            _session.CheckThread();
            IsDisposed = true;
            Suspend();
            _cleanupComplete = true;
            _session.Remove(this);
        }
    }
}
