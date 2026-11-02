using UnityEngine;

namespace YLFramework.Core
{
    /// <summary>
    /// 从每次启用开始计时，到期请求关闭同对象的 View 会话，不直接销毁对象或移除 Model。
    /// </summary>
    [RequireComponent(typeof(View))]
    public class CloseView : MonoBehaviour
    {
        /// <summary>每轮启用后的关闭间隔，单位为秒；计时达到该值时调用 View.Close。</summary>
        [Header("持续时间")]
        [Tooltip("持续时间")]
        public float duration = 1;

        /// <summary>为 true 时使用 unscaledDeltaTime，否则使用受 timeScale 影响的 deltaTime。</summary>
        [Header("忽略时间缩放")]
        [Tooltip("忽略时间缩放")]
        public bool ingoreTimeScale;

        private float _timer = 0;


        private View _view;

        /// <summary>缓存同对象的 View，后续计时只向该组件发送关闭请求。</summary>
        private void Awake()
        {
            _view = GetComponent<View>();
        }

        /// <summary>每次启用重新计时，不沿用上次启用或缓存前的剩余时长。</summary>
        private void OnEnable()
        {
            _timer = 0;
        }

        /// <summary>按选定时间尺度累计时长，到期请求关闭并重置计时；未停用时仍会开始下一轮。</summary>
        private void Update()
        {
            _timer += ingoreTimeScale ? Time.unscaledDeltaTime : Time.deltaTime;

            if (_timer >= duration)
            {
                _view.Close();
                _timer = 0;
            }
        }
    }
}
