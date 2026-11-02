using UnityEngine;

namespace YLFramework.Core
{
    /// <summary>为最近 Panel 所属的每个 Canvas 过滤射线，包括 overrideSorting 的嵌套 Canvas。</summary>
    internal sealed class PanelRaycastGate : MonoBehaviour, ICanvasRaycastFilter
    {
        private Panel _owner;
        private object _session;

        /// <summary>绑定当前面板会话，令复用的门禁不再依赖旧 Session。</summary>
        internal void Bind(Panel panel, object currentSession)
        {
            _owner = panel;
            _session = currentSession;
        }
        /// <summary>仅解除匹配的旧会话，不能撤销同一组件上的后来绑定。</summary>
        internal void Release(object endedSession)
        {
            if (!ReferenceEquals(_session, endedSession))
            {
                return;
            }

            _owner = null;
            _session = null;
        }
        /// <summary>当前绑定会话被阻挡时拒绝射线；未绑定或旧绑定不再施加限制，不按坐标区分。</summary>
        public bool IsRaycastLocationValid(Vector2 point, Camera eventCamera) =>
            !_owner || !ReferenceEquals(_owner.Session, _session) || !_owner.IsInputBlocked;
    }
}
