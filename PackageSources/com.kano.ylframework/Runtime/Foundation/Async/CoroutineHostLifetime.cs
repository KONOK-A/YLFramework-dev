using UnityEngine;

namespace YLFramework.Async
{
    /// <summary>独立观察宿主 GameObject；CoroutineStarter 组件禁用后仍能收到对象停用消息。</summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    internal sealed class CoroutineHostLifetime : MonoBehaviour
    {
        internal CoroutineStarter Owner;

        private void OnDisable()
        {
            if (Owner && !gameObject.activeInHierarchy)
            {
                Owner.HostDeactivated();
            }
        }
    }
}
