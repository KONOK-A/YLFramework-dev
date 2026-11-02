using System;
using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.Core
{
    /// <summary>提示使用的预配置承载容器，不在显示时自行计算屏幕坐标。</summary>
    public enum UITipPos
    {
        /// <summary>使用中部容器。</summary>
        Middle,
        /// <summary>使用左上容器。</summary>
        LeftTop,
        /// <summary>使用左下容器。</summary>
        LeftDown,
        /// <summary>使用右上容器。</summary>
        RightTop,
        /// <summary>使用右下容器。</summary>
        RightDown
    }
    /// <summary>提示会话的结束原因；须结合句柄 IsDone 判断是否已经结束。</summary>
    public enum UITipResult
    {
        /// <summary>持续时长届满，或零时长提示在显示入口立即完成。</summary>
        Completed,
        /// <summary>主动取消，或会话未被标记为其他结束原因便关闭。</summary>
        Canceled,
        /// <summary>Presenter 停用或销毁时要求提示结束。</summary>
        HostEnded
    }

    /// <summary>一次提示的取消句柄。结束后不再持有调用方回调，也不操作后续复用会话。</summary>
    public sealed class UITipHandle : IDisposable
    {
        /// <summary>本次显示使用的物理组件引用；提示结束后可能被池复用，不代表旧会话仍有效。</summary>
        public UITipsItem Item
        {
            get;
        }
        /// <summary>提示已进入移除或宿主兜底收尾；为 true 不代表完成回调已经返回。</summary>
        public bool IsDone
        {
            get; private set;
        }
        /// <summary>保存的结束原因，未完成时的初值 Canceled 不是终态通知。</summary>
        public UITipResult Result { get; private set; } = UITipResult.Canceled;
        internal object Session;
        internal Action<UITipHandle> Callback;
        /// <summary>保存本次借用项和回调，具体 Session 由该项加载时补入。</summary>
        internal UITipHandle(UITipsItem item, Action<UITipHandle> callback)
        {
            Item = item;
            Callback = callback;
        }
        /// <summary>以 Canceled 原因关闭仍匹配的提示会话，不操作后续复用的同一组件。</summary>
        public void Dispose() => Finish(UITipResult.Canceled);
        /// <summary>在尚未结束时记录原因，仅对仍持有原 Session 的提示发起 Close。</summary>
        internal void Finish(UITipResult result)
        {
            if (IsDone)
            {
                return;
            }

            Result = result;
            if (Item && ReferenceEquals(Item.Session, Session))
            {
                Item.Close();
            }
        }
        internal void End() => IsDone = true;
        /// <summary>先放下回调引用再通知调用方；回调异常由当前调用栈处理，不在这里吞掉。</summary>
        internal void Notify()
        {
            var callback = Callback;
            Callback = null;
            callback?.Invoke(this);
        }
    }

    /// <summary>正式短提示呈现项；每次显示沿 Module/View 会话退出，池归还仅在框架收尾后发生。</summary>
    public sealed class UITipsItem : Panel
    {
        [SerializeField] private Image bg;
        [SerializeField] private Text text;
        private Sprite _originalSprite;
        private Color _originalTextColor;
        private Color _originalBgColor;
        private CanvasGroup _group;
        private bool _isPrepared;
        private float _elapsed;
        private float _duration;
        private bool _fade;
        private UITipHandle _handle;
        private UITipsPresenter _presenter;

        /// <summary>借读本次显示参数，绑定句柄会话并恢复实例默认样式，不保留参数容器。</summary>
        public override void OnLoaded(LoadViewArgs args)
        {
            if (!bg || !text)
            {
                throw new InvalidOperationException("UITipsItem requires background and text bindings.");
            }

            if (!_isPrepared)
            {
                _originalSprite = bg.sprite;
                _originalTextColor = text.color;
                _originalBgColor = bg.color;
                _group = GetComponent<CanvasGroup>();
                if (!_group)
                {
                    _group = gameObject.AddComponent<CanvasGroup>();
                }

                _isPrepared = true;
            }
            _presenter = (UITipsPresenter)args.GetObject("presenter");
            _handle = (UITipHandle)args.GetObject("handle");
            _handle.Session = Session;
            _duration = args.GetFloat("duration");
            _elapsed = 0;
            _fade = args.GetBool("fade");
            text.text = args.GetString("content");
            text.color = args.GetObject("textColor") is Color color ? color : _originalTextColor;
            bg.sprite = args.GetObject("sprite") as Sprite;
            if (!bg.sprite)
            {
                bg.sprite = _originalSprite;
            }

            bg.color = _originalBgColor;
            text.raycastTarget = bg.raycastTarget = false;
            _group.blocksRaycasts = false;
            _group.alpha = _fade ? 0 : 1;
        }

        /// <summary>用 unscaled 时间推进持续时长与渐隐渐显，到期通过句柄结束原会话。</summary>
        private void Update()
        {
            if (!IsLoaded || _handle == null || _handle.IsDone)
            {
                return;
            }

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed >= _duration)
            {
                _handle.Finish(UITipResult.Completed);
                return;
            }
            if (_fade)
            {
                _group.alpha = Mathf.Clamp01(Mathf.Min(_elapsed, _duration - _elapsed) / (_duration * 0.25f));
            }
        }

        /// <summary>先标记句柄已结束，再清空文本并恢复可复用样式，此阶段不归还 Presenter 池。</summary>
        public override void OnRemove()
        {
            _handle?.End();
            if (_isPrepared && this && bg && text)
            {
                text.text = string.Empty;
                text.color = _originalTextColor;
                bg.sprite = _originalSprite;
                bg.color = _originalBgColor;
                _group.alpha = 1;
            }
        }

        /// <summary>框架完成会话与实例收尾后向 Presenter 归还借用，宿主已失效时直接通知原句柄。</summary>
        protected override void OnClosed()
        {
            var previous = _handle;
            var host = _presenter;
            _handle = null;
            _presenter = null;
            if (host)
            {
                host.Return(this, previous);
            }
            else
            {
                previous?.Notify();
            }
        }
    }
}
