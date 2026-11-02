using System;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource
{
    /// <summary>本次场景的 caller 身份；Dispose 不卸载场景，也不释放 SceneRecord 的 Bundle 图。</summary>
    public sealed class SceneLease : IDisposable
    {
        private SceneRecord _record;
        /// <summary>是否已放弃本次调用方身份；不表示真实场景已经卸载。</summary>
        public bool IsDisposed => _record == null;
        /// <summary>是否仍持有调用方身份、资源会话有效且真实场景仍已加载。</summary>
        public bool IsValid => _record != null && _record.Owner.IsSessionActive && _record.IsLoaded;
        /// <summary>本次加载的独立场景记录；可在租约归还前保存，用于后续显式卸载。</summary>
        /// <exception cref="ObjectDisposedException">本租约已归还。</exception>
        /// <exception cref="InvalidOperationException">不在 Unity 主线程读取。</exception>
        /// <exception cref="ResourceException">所属资源会话已经结束。</exception>
        public SceneRecord Record
        {
            get
            {
                if (_record == null)
                {
                    throw new ObjectDisposedException(nameof(SceneLease));
                }

                _record.Owner.CheckThread();
                if (!_record.Owner.IsSessionActive)
                {
                    throw new ResourceException(ResourceError.SessionUnavailable, "The Scene lease belongs to an ended session.");
                }

                return _record;
            }
        }
        /// <summary>借读本次加载且仍存活的真实 Unity 场景，不按名字寻找替身。</summary>
        /// <exception cref="ObjectDisposedException">本租约已归还。</exception>
        /// <exception cref="InvalidOperationException">不在 Unity 主线程读取。</exception>
        /// <exception cref="ResourceException">资源会话结束或该场景已经卸载。</exception>
        public Scene Scene
        {
            get
            {
                var value = Record;
                if (!value.IsLoaded)
                {
                    throw new ResourceException(ResourceError.SceneUnloaded, "The leased Scene has been unloaded.");
                }

                return value.Scene;
            }
        }
        /// <summary>关联本次调用方身份，不另增 SceneRecord 的 Bundle 图责任。</summary>
        internal SceneLease(SceneRecord record) => this._record = record;
        /// <summary>幂等放弃本调用方身份，真实场景及其依赖继续由独立记录管理。</summary>
        /// <exception cref="InvalidOperationException">尚未归还时在非 Unity 主线程调用。</exception>
        public void Dispose()
        {
            if (_record == null)
            {
                return;
            }

            _record.Owner.CheckThread();
            _record = null;
        }
    }
}
