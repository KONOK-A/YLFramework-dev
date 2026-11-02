using UnityEngine;

namespace YLFramework.Core
{

    /// <summary>按当前 Canvas 与自身尺寸等比设置背景缩放，覆盖倍率不低于 1，不修改锚点或尺寸。</summary>
    [DisallowMultipleComponent]
    public class BackgroundAdaptation : MonoBehaviour
    {
        private RectTransform _rect;
        private string _lastError;

        /// <summary>重新计算 cover；依赖或尺寸无效时保留原缩放、报告一次错误并返回 false。</summary>
        public bool Refresh()
        {
            if (!_rect)
            {
                _rect = GetComponent<RectTransform>();
            }

            if (!_rect)
            {
                return Reject("BackgroundAdaptation requires a RectTransform.");
            }

            var canvas = GetComponentInParent<Canvas>();
            if (!canvas || canvas.transform == transform)
            {
                return Reject("BackgroundAdaptation requires a parent Canvas.");
            }

            var canvasRect = canvas.transform as RectTransform;
            if (!canvasRect)
            {
                return Reject("BackgroundAdaptation requires a Canvas RectTransform.");
            }

            Vector2 size = canvasRect.rect.size;
            Vector2 contentSize = _rect.rect.size;
            if (!IsPositiveFinite(size) || !IsPositiveFinite(contentSize))
            {
                return Reject("BackgroundAdaptation requires positive finite Canvas and content sizes.");
            }

            float scale = Mathf.Max(1, Mathf.Max(size.x / contentSize.x, size.y / contentSize.y));
            if (float.IsInfinity(scale))
            {
                return Reject("BackgroundAdaptation cover scale is not finite.");
            }

            var target = new Vector3(scale, scale, scale);
            if (_rect.localScale != target)
            {
                _rect.localScale = target;
            }

            _lastError = null;
            return true;
        }

        /// <summary>保留最近的校验失败原因，同一连续错误不重复报告。</summary>
        private bool Reject(string error)
        {
            if (_lastError != error)
            {
                Debug.LogError(error, this);
            }

            _lastError = error;
            return false;
        }

        /// <summary>检查 Canvas 与内容尺寸能否参与有限的覆盖比例计算。</summary>
        private static bool IsPositiveFinite(Vector2 value) => value.x > 0 && value.y > 0 &&
            !float.IsInfinity(value.x) && !float.IsInfinity(value.y);

        /// <summary>启用时按当前父 Canvas 更新背景覆盖。</summary>
        private void OnEnable() => Refresh();
        /// <summary>在 LateUpdate 重新读取当前尺寸并应用覆盖倍率。</summary>
        private void LateUpdate() => Refresh();
        /// <summary>启用期间换父级后改用新的 Canvas 计算背景覆盖。</summary>
        private void OnTransformParentChanged()
        {
            if (isActiveAndEnabled)
            {
                Refresh();
            }
        }
    }
}
