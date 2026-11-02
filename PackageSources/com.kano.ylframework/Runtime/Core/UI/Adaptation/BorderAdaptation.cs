using UnityEngine;

namespace YLFramework.Core
{

    /// <summary>水平边距对应的贴边方向，用于决定 anchoredPosition.x 的符号。</summary>
    public enum BorderType
    {
        /// <summary>左侧边距向右为正。</summary>
        Left,
        /// <summary>右侧边距向左为负。</summary>
        Right
    }

    /// <summary>
    /// 按宽高比调整左右边距，不改变垂直位置；不是 Safe Area。
    /// </summary>
    [DisallowMultipleComponent]
    public class BorderAdaptation : MonoBehaviour
    {
        /// <summary>
        /// 边界类型
        /// </summary>
        [SerializeField]
        private BorderType borderType = BorderType.Right;

        /// <summary>
        /// 最小偏移
        /// </summary>
        [SerializeField]
        private float minOffset = 0;

        /// <summary>
        /// 最大偏移
        /// </summary>
        [SerializeField]
        private float maxOffset = 100;

        [SerializeField, Tooltip("达到最小偏移的 Canvas 宽高比；无 Canvas 时使用屏幕尺寸。")]
        private float minAspect = 1.77f;
        [SerializeField, Tooltip("达到最大偏移的 Canvas 宽高比，必须大于最小宽高比。")]
        private float maxAspect = 2.22f;
        private RectTransform _rect;
        private string _lastError;

        /// <summary>刷新水平边距，保留 Y；非法配置或尺寸报告一次错误并返回 false。</summary>
        public bool Refresh()
        {
            if (!_rect)
            {
                _rect = GetComponent<RectTransform>();
            }

            if (!_rect)
            {
                return Reject("BorderAdaptation requires a RectTransform.");
            }

            if (!IsFinite(minAspect) || !IsFinite(maxAspect) || minAspect <= 0 || maxAspect <= minAspect ||
                !IsFinite(minOffset) || !IsFinite(maxOffset) || maxOffset < minOffset ||
                (borderType != BorderType.Left && borderType != BorderType.Right))
            {
                return Reject("BorderAdaptation requires increasing positive aspect bounds and finite ordered offsets.");
            }

            var canvas = GetComponentInParent<Canvas>();
            Vector2 size = canvas ? ((RectTransform)canvas.transform).rect.size : new Vector2(Screen.width, Screen.height);
            if (!IsFinite(size.x) || !IsFinite(size.y) || size.x <= 0 || size.y <= 0)
            {
                return Reject("BorderAdaptation requires a positive finite Canvas or screen size.");
            }

            float value = Mathf.InverseLerp(minAspect, maxAspect, size.x / size.y);
            float offset = Mathf.Lerp(minOffset, maxOffset, value);
            Vector2 position = _rect.anchoredPosition;
            position.x = borderType == BorderType.Left ? offset : -offset;
            if (_rect.anchoredPosition != position)
            {
                _rect.anchoredPosition = position;
            }

            _lastError = null;
            return true;
        }

        /// <summary>保存当前失败原因并返回 false，相同连续错误不重复写日志。</summary>
        private bool Reject(string error)
        {
            if (_lastError != error)
            {
                Debug.LogError(error, this);
            }

            _lastError = error;
            return false;
        }

        /// <summary>检查宽高比或偏移能否参与插值，排除 NaN 与无限值。</summary>
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>首次运行时补做边距刷新，读取当时可用的 Canvas 或屏幕尺寸。</summary>
        private void Start() => Refresh();
        /// <summary>启用后立即应用当前边距配置。</summary>
        private void OnEnable() => Refresh();
        /// <summary>每帧跟随 Canvas 或屏幕宽高比变化刷新水平位置。</summary>
        private void LateUpdate() => Refresh();
        /// <summary>启用期间换父级时重新选择用于宽高比计算的 Canvas。</summary>
        private void OnTransformParentChanged()
        {
            if (isActiveAndEnabled)
            {
                Refresh();
            }
        }
    }

}
