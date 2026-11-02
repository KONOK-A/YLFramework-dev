using UnityEngine;
using YLFramework.Events;

namespace YLFramework.RedDots
{
    /// <summary>只切换显式指示器，不批量启停业务子物体，也不持有业务路径来源。</summary>
    [AddComponentMenu("YLFramework/Reddot")]
    public sealed class Reddot : MonoBehaviour
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("_path")]
        [SerializeField, Tooltip("按段匹配，例如 Mail/Gift；空路径不显示。")]
        private string path;
        [global::UnityEngine.Serialization.FormerlySerializedAs("_indicator")]
        [SerializeField, Tooltip("只控制此指示器。不能是本物体或其祖先，避免关闭监听宿主。")]
        private GameObject indicator;
        private IUnregister _subscription;

        /// <summary>用于查询自身或后代状态的业务路径；设置后立即刷新，空路径不显示。</summary>
        public string Path
        {
            get => path; set
            {
                path = value;
                Refresh();
            }
        }
        /// <summary>唯一受控的指示器；设置后刷新新对象，不负责复位之前绑定的对象。</summary>
        public GameObject Indicator
        {
            get => indicator; set
            {
                indicator = value;
                Refresh();
            }
        }

        /// <summary>重新绑定路径变化通知并立即刷新，补上停用期间发生的状态变化。</summary>
        private void OnEnable()
        {
            _subscription?.Unregister();
            _subscription = ReddotManager.RegisterChanged(Refresh);
            Refresh();
        }

        /// <summary>解除路径变化订阅，不主动改变指示器当前显隐状态。</summary>
        private void OnDisable()
        {
            _subscription?.Unregister();
            _subscription = null;
        }

        /// <summary>销毁时补做订阅清理，不释放业务登记的红点来源。</summary>
        private void OnDestroy() => OnDisable();

        /// <summary>按组件启用状态与路径聚合结果更新指示器，拒绝控制本物体或其祖先以免关闭监听宿主。</summary>
        public void Refresh()
        {
            if (!indicator || indicator == gameObject || transform.IsChildOf(indicator.transform))
            {
                return;
            }

            var active = isActiveAndEnabled && ReddotManager.ShouldBeDisplayed(path);
            if (indicator.activeSelf != active)
            {
                indicator.SetActive(active);
            }
        }

        /// <summary>仅移除普通集合路径；独立来源须由业务所有者自行释放。</summary>
        public void Remove() => ReddotManager.RemovePath(path);
    }
}
