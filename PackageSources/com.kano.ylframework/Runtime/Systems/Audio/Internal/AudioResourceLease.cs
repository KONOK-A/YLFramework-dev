using System;
using UnityEngine;

namespace YLFramework.Audio
{
    /// <summary>播放内核的终结原因；取消等待和未接纳由外层命名请求记录。</summary>
    internal enum AudioRequestEnd
    {
        None,
        Completed,
        Stopped,
        Replaced,
        Unavailable,
        Failed,
        Shutdown
    }

    // 仅取得自有资源的内部入口使用；直接 Clip 播放不创建租约，也不释放 Clip。
    /// <summary>连接命名资源责任与原 Voice 的接纳、终态通知，防止跨会话或重复绑定。</summary>
    internal sealed class AudioResourceLease : IDisposable
    {
        internal readonly ulong Session = AudioKit.SoundSession;
        internal bool IsBound
        {
            get; private set;
        }
        internal bool IsReleased
        {
            get; private set;
        }
        private IDisposable _resource;
        private Action<AudioRequestEnd> _ended;
        private Action<AudioHandle> _accepted;
        internal bool CanBind => !IsBound && !IsReleased && Session == AudioKit.SoundSession;

        /// <summary>保存本次取得的资源责任及可选通知，并捕获所属 Audio 会话。</summary>
        internal AudioResourceLease(IDisposable resource, Action<AudioRequestEnd> ended = null, Action<AudioHandle> accepted = null)
        {
            _resource = resource ?? throw new ArgumentNullException(nameof(resource));
            _ended = ended;
            _accepted = accepted;
        }

        /// <summary>恰一次交付已接纳的原播放句柄，允许通知同步重入。</summary>
        internal void Accept(AudioHandle handle)
        {
            var accepted = _accepted;
            _accepted = null;
            accepted?.Invoke(handle);
        }

        /// <summary>恰一次向外层请求通知播放终态，不在此处释放资源。</summary>
        internal void End(AudioRequestEnd reason)
        {
            var ended = _ended;
            _ended = null;
            ended?.Invoke(reason);
        }

        /// <summary>验证责任尚可使用后，将其独占绑定给一个 Voice。</summary>
        internal void Bind()
        {
            Validate();
            IsBound = true;
        }

        /// <summary>拒绝已绑定、已释放或不属于当前 Audio 会话的责任。</summary>
        internal void Validate()
        {
            if (IsBound || IsReleased || Session != AudioKit.SoundSession)
            {
                throw new InvalidOperationException("Audio resource lease is no longer available.");
            }
        }

        /// <summary>恰一次归还封装的资源责任并记录释放异常，不自行销毁共享 Clip。</summary>
        public void Dispose()
        {
            if (IsReleased)
            {
                return;
            }

            IsReleased = true;
            _accepted = null;
            var resource = _resource;
            _resource = null;
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }
}
