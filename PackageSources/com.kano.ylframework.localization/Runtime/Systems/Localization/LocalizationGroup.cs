using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YLFramework.Core;

namespace YLFramework.Localization
{
    /// <summary>只协调本地内容的就绪和布局，不接管 Panel 可见意图、根 CanvasGroup 或全局语言。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-900)]
    [AddComponentMenu("YLFramework/Localization/Localization Group")]
    public sealed class LocalizationGroup : MonoBehaviour, ILocalizationGroupOwner
    {
        [SerializeField] private CanvasGroup _content;
        [SerializeField] private GameObject _loadingRoot;
        [SerializeField] private GameObject _failureRoot;
        [SerializeField] private bool _autoStart = true;
        private readonly List<LocalizationBehaviour> _members = new List<LocalizationBehaviour>();
        private LocalizationSession _session;
        private View _owner;
        private Action<LocalizationSession> _configure;
        private long _epoch;
        private bool _wantsSession;
        private bool _rebindRequested;
        private bool _isDirty;
        private bool _captured;
        private float _alpha;
        private bool _interactable;
        private bool _blocksRaycasts;

        /// <summary>专属内容子节点，不能使用 Panel 根 CanvasGroup。</summary>
        public CanvasGroup Content
        {
            get => _content;
            set
            {
                if (_content == value)
                    return;
                if (_session != null)
                    throw new InvalidOperationException("End the current localization session before replacing its content gate.");
                RestoreGate();
                _content = value;
                _captured = false;
            }
        }
        /// <summary>内容门控之外的可选局部加载节点。</summary>
        public GameObject LoadingRoot
        {
            get => _loadingRoot; set => _loadingRoot = value;
        }
        /// <summary>内容门控之外的可选局部错误节点。</summary>
        public GameObject FailureRoot
        {
            get => _failureRoot; set => _failureRoot = value;
        }
        /// <summary>普通 GameObject 可自动开始；Panel 通常设为 false 并在 OnOpen 显式调用 BeginSession。</summary>
        public bool AutoStart
        {
            get => _autoStart; set => _autoStart = value;
        }
        /// <summary>当前内容状态，与 Panel 是否被业务隐藏无关。</summary>
        public LocalizationGroupState State
        {
            get; private set;
        }
        /// <summary>当前轮次错误。</summary>
        public Exception Error
        {
            get; private set;
        }
        /// <summary>状态变化通知；监听器异常会明确记录，不打断其他监听器或官方语言事件。</summary>
        public event Action<LocalizationGroupState> StateChanged;

        private void Awake()
        {
            if (_content)
                CaptureGate();
        }

        private void OnEnable()
        {
            if (_wantsSession)
                _rebindRequested = true;
        }

        private void Start()
        {
            if (_autoStart && !_wantsSession)
                TryBegin(null, null);
        }

        /// <summary>每次打开创建新的绑定轮次；同步配置变量后才激活加载，不保存 LoadViewArgs。</summary>
        public LocalizationSession BeginSession(View owner = null, Action<LocalizationSession> configure = null)
        {
            if (!ReferenceEquals(owner, null) && !owner)
                throw new ArgumentException("The View owner was destroyed.", nameof(owner));
            var epoch = ++_epoch;
            StopCurrent();
            if (_owner)
                _owner.ViewRemoved -= HandleOwnerRemoved;
            _owner = owner;
            _configure = configure;
            _wantsSession = true;
            _rebindRequested = false;
            Error = null;
            try
            {
                CaptureGate();
                ValidateStatusRoot(_loadingRoot);
                ValidateStatusRoot(_failureRoot);
                if (_owner)
                    _owner.ViewRemoved += HandleOwnerRemoved;
                SetState(LocalizationGroupState.Loading);
                if (epoch != _epoch)
                    return _session;
                var session = new LocalizationSession { DeferDelivery = true };
                _session = session;
                session.Changed += HandleChanged;
                session.BeginConfiguration();
                foreach (var member in GetComponentsInChildren<LocalizationBehaviour>(false))
                {
                    if (!member.isActiveAndEnabled || member.FindGroup() != this)
                        continue;
                    _members.Add(member);
                    member.Bind(session);
                }
                configure?.Invoke(session);
                if (epoch != _epoch || session.IsDisposed)
                    return session;
                session.EndConfiguration();
                _isDirty = true;
                return session;
            }
            catch (Exception error)
            {
                if (epoch == _epoch)
                {
                    try
                    {
                        StopCurrent();
                    }
                    catch (Exception cleanup) { error = new AggregateException(error, cleanup); }
                    SetState(LocalizationGroupState.Failed, error);
                }
                throw;
            }
        }

        /// <summary>显式重试本轮定义；不重置官方全局设置或其他面板。</summary>
        public void Retry()
        {
            if (!_wantsSession)
                throw new InvalidOperationException("There is no localization round to retry.");
            BeginSession(_owner, _configure);
        }

        /// <summary>结束本轮并忘记业务配置，不改变业务 GameObject 的活动状态。</summary>
        public void EndSession()
        {
            ++_epoch;
            _wantsSession = false;
            _rebindRequested = false;
            if (_owner)
                _owner.ViewRemoved -= HandleOwnerRemoved;
            _owner = null;
            _configure = null;
            try
            {
                StopCurrent();
            }
            finally { SetState(LocalizationGroupState.Inactive); }
        }

        void ILocalizationGroupOwner.RequestRebind()
        {
            if (!isActiveAndEnabled || !_wantsSession)
                return;
            if (_members.RemoveAll(member => !member || !member.isActiveAndEnabled || member.FindGroup() != this) > 0)
            {
                _isDirty = true;
                return;
            }
            _rebindRequested = true;
            _isDirty = true;
        }

        bool ILocalizationGroupOwner.Owns(LocalizationBehaviour member) => HasOwnedGate && member && member.transform.IsChildOf(_content.transform);

        private void HandleChanged() => _isDirty = true;

        private void LateUpdate()
        {
            if (_session != null && _session.IsDisposed)
            {
                EndSession();
                return;
            }
            if (_rebindRequested)
            {
                _rebindRequested = false;
                TryBegin(_owner, _configure);
            }
            if (!_isDirty || _session == null)
                return;
            _isDirty = false;
            var session = _session;
            var epoch = _epoch;
            if (session.Error != null)
            {
                SetState(LocalizationGroupState.Failed, session.Error);
                return;
            }
            if (!session.IsReady)
            {
                SetState(LocalizationGroupState.Loading);
                return;
            }
            session.Commit();
            if (epoch != _epoch || session.IsDisposed)
                return;
            if (session.Error != null)
            {
                SetState(LocalizationGroupState.Failed, session.Error);
                return;
            }
            if (!session.IsReady)
            {
                SetState(LocalizationGroupState.Loading);
                return;
            }
            if (!HasOwnedGate)
            {
                SetState(LocalizationGroupState.Failed, new InvalidOperationException("The content gate was destroyed or moved outside this group."));
                return;
            }
            if (_content.transform is RectTransform layout)
                LayoutRebuilder.ForceRebuildLayoutImmediate(layout);
            SetState(LocalizationGroupState.Ready);
        }

        private void TryBegin(View owner, Action<LocalizationSession> configure)
        {
            try
            {
                BeginSession(owner, configure);
            }
            catch (Exception error) { Debug.LogException(error, this); }
        }

        private void CaptureGate()
        {
            if (!HasOwnedGate)
                throw new InvalidOperationException("Assign a dedicated child CanvasGroup as localization content.");
            if (_captured)
                return;
            _alpha = _content.alpha;
            _interactable = _content.interactable;
            _blocksRaycasts = _content.blocksRaycasts;
            _captured = true;
            _content.alpha = 0;
            _content.interactable = false;
        }

        private void ValidateStatusRoot(GameObject root)
        {
            if (root && !IsOwnedStatusRoot(root))
                throw new InvalidOperationException("Loading/error roots must be dedicated descendants outside the content gate.");
        }

        private bool HasOwnedGate => _content && _content.transform != transform && _content.transform.IsChildOf(transform);

        private bool IsOwnedStatusRoot(GameObject root) => root && root != gameObject &&
            root.transform.IsChildOf(transform) && HasOwnedGate && !root.transform.IsChildOf(_content.transform) && !_content.transform.IsChildOf(root.transform);

        private void RestoreGate()
        {
            if (!_captured || !HasOwnedGate)
                return;
            _content.alpha = _alpha;
            _content.interactable = _interactable;
            _content.blocksRaycasts = _blocksRaycasts;
        }

        private void SetState(LocalizationGroupState state, Exception error = null)
        {
            var epoch = _epoch;
            var changed = State != state;
            State = state;
            Error = error;
            if (state == LocalizationGroupState.Ready || state == LocalizationGroupState.Inactive)
                RestoreGate();
            else if (_captured && HasOwnedGate)
            {
                _content.alpha = 0;
                _content.interactable = false;
            }
            if (IsOwnedStatusRoot(_loadingRoot))
                _loadingRoot.SetActive(state == LocalizationGroupState.Loading);
            if (IsOwnedStatusRoot(_failureRoot))
                _failureRoot.SetActive(state == LocalizationGroupState.Failed);
            if (!changed || StateChanged == null)
                return;
            foreach (Action<LocalizationGroupState> callback in StateChanged.GetInvocationList())
            {
                if (_epoch != epoch || State != state)
                    break;
                try
                {
                    callback(state);
                }
                catch (Exception notificationError) { Debug.LogException(notificationError, this); }
            }
        }

        private void StopCurrent()
        {
            var session = _session;
            _session = null;
            if (session != null)
                session.Changed -= HandleChanged;
            List<Exception> errors = null;
            foreach (var member in _members.ToArray())
            {
                try
                {
                    if (member)
                        member.Unbind();
                }
                catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
            }
            _members.Clear();
            try
            {
                session?.Dispose();
            }
            catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
            _isDirty = false;
            if (errors != null)
                throw new AggregateException(errors);
        }

        private void HandleOwnerRemoved()
        {
            try
            {
                EndSession();
            }
            catch (Exception error) { Debug.LogException(error, this); }
        }

        private void OnDisable()
        {
            ++_epoch;
            try
            {
                StopCurrent();
            }
            finally { SetState(LocalizationGroupState.Inactive); }
        }

        private void OnDestroy() => HandleOwnerRemoved();
    }
}
