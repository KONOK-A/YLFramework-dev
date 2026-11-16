using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Localization
{
    internal interface ILocalizationGroupOwner
    {
        void RequestRebind();
        bool Owns(LocalizationBehaviour member);
    }

    /// <summary>类型化表现绑定的共同生命周期；独立使用时自管会话，组内则借用组会话。</summary>
    public abstract class LocalizationBehaviour : MonoBehaviour
    {
        private readonly List<IDisposable> _bindings = new List<IDisposable>();
        private LocalizationSession _session;
        private LocalizationSession _standalone;
        private MonoBehaviour _groupOwner;
        private Exception _error;

        /// <summary>配置或当前加载的错误。</summary>
        public Exception Error => _error ?? _session?.Error;

        /// <summary>当前所在消费者会话已就绪。</summary>
        public bool IsReady => _session != null && _session.IsReady;

        /// <summary>显式借用会话，替换本组件的旧绑定；不取得外部会话所有权。</summary>
        public void Bind(LocalizationSession session)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            session.CheckActive();
            Unbind();
            _session = session;
            _error = null;
            try
            {
                CreateBindings(session);
            }
            catch (Exception error)
            {
                Unbind();
                _error = error;
                throw;
            }
        }

        /// <summary>归还本组件的绑定并恢复仍由本组件持有的显示值。</summary>
        public void Unbind()
        {
            RestoreValues();
            List<Exception> errors = null;
            foreach (var binding in _bindings)
            {
                try
                {
                    binding.Dispose();
                }
                catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
            }
            _bindings.Clear();
            _session = null;
            if (_standalone != null)
            {
                _standalone.Changed -= CommitStandalone;
                _standalone.Dispose();
                _standalone = null;
            }
            if (errors != null)
                throw new AggregateException(errors);
        }

        /// <summary>创建具体目标的绑定；通过 Own 登记返回的释放句柄。</summary>
        protected abstract void CreateBindings(LocalizationSession session);
        /// <summary>仅恢复仍等于最后一次本方写入的值，不能覆盖业务后续修改。</summary>
        protected abstract void RestoreValues();
        /// <summary>登记本组件拥有的单个绑定。</summary>
        protected void Own(IDisposable binding) => _bindings.Add(binding);

        /// <summary>引用通过属性被替换后，重新建立当前有效绑定。</summary>
        protected void Rebind()
        {
            if (!isActiveAndEnabled)
                return;
            if (FindGroup() is ILocalizationGroupOwner group)
            {
                group.RequestRebind();
                return;
            }
            var borrowed = _standalone == null ? _session : null;
            if (borrowed != null)
                Bind(borrowed);
            else
                StartStandalone();
        }

        internal MonoBehaviour FindGroup()
        {
            foreach (var component in GetComponentsInParent<MonoBehaviour>(true))
                if (component is ILocalizationGroupOwner group)
                    return group.Owns(this) ? component : null;
            return null;
        }

        private void StartStandalone()
        {
            Unbind();
            var session = new LocalizationSession { DeferDelivery = true };
            try
            {
                Bind(session);
                _standalone = session;
                _standalone.Changed += CommitStandalone;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private void CommitStandalone()
        {
            if (_standalone != null && _standalone.IsReady)
                _standalone.Commit();
        }

        /// <summary>启用独立绑定，或通知最近的表现组重新收集参与者。</summary>
        protected virtual void OnEnable()
        {
            _groupOwner = FindGroup();
            if (_groupOwner is ILocalizationGroupOwner group)
                group.RequestRebind();
            else
            {
                try
                {
                    StartStandalone();
                }
                catch (Exception error)
                {
                    _error = error;
                    Debug.LogException(error, this);
                }
            }
        }

        /// <summary>停用后不保留本组件监听或资源引用。</summary>
        protected virtual void OnDisable()
        {
            var previous = _groupOwner;
            _groupOwner = null;
            Unbind();
            if (previous && previous is ILocalizationGroupOwner group)
                group.RequestRebind();
        }

        /// <summary>成员移出组时立即撤销旧所有权，不能让旧面板继续等待或写入新层级。</summary>
        protected virtual void OnTransformParentChanged()
        {
            if (!Application.isPlaying || !isActiveAndEnabled)
                return;
            var current = FindGroup();
            if (current == _groupOwner)
                return;
            var previous = _groupOwner;
            _groupOwner = current;
            Unbind();
            if (previous && previous is ILocalizationGroupOwner oldGroup)
                oldGroup.RequestRebind();
            if (current is ILocalizationGroupOwner group)
                group.RequestRebind();
            else
            {
                try
                {
                    StartStandalone();
                }
                catch (Exception error)
                {
                    _error = error;
                    Debug.LogException(error, this);
                }
            }
        }
    }
}
