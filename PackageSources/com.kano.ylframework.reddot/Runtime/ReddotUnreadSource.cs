using System;

namespace YLFramework.RedDots
{
    /// <summary>Caller-owned unread contribution for one current content version; all operations are main-thread serial.</summary>
    /// <remarks>Reset/session invalidation is terminal. An old handle never re-registers a source.</remarks>
    public sealed class ReddotUnreadSource : IDisposable
    {
        private ReddotReadState _state;
        internal readonly ReddotSource Source;
        internal readonly string Key;
        internal string Version;

        internal ReddotUnreadSource(ReddotReadState state, ReddotSource source, string key, string version)
        {
            _state = state;
            Source = source;
            Key = key;
            Version = version;
        }

        /// <summary>Change the active content version, never its confirmation; invalid input/overflow changes nothing.</summary>
        /// <remarks>Observer errors propagate after the new version and source value have both committed.</remarks>
        public void SetVersion(string version)
        {
            ReddotReadState.RequireText(version, nameof(version));
            if (_state == null || !Source.IsCurrent)
            {
                throw new ObjectDisposedException(nameof(ReddotUnreadSource));
            }
            using var batch = ReddotManager.BeginBatch();
            Source.SetValue(_state.IsRead(Key, version) ? 0 : 1);
            Version = version;
        }

        /// <summary>Idempotently release only this registration; stale handles cannot affect a replacement.</summary>
        public void Dispose()
        {
            var state = _state;
            if (state == null)
            {
                return;
            }
            _state = null;
            state.Unbind(this);
            Source.Dispose();
        }
    }
}
