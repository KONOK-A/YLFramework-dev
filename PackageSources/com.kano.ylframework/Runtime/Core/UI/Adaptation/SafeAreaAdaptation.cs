using UnityEngine;

namespace YLFramework.Core
{
    /// <summary>显式添加到内容子容器，将屏幕安全区转换为父 RectTransform 内的布局；不改变背景。</summary>
    [DisallowMultipleComponent]
    public class SafeAreaAdaptation : MonoBehaviour
    {
        private RectTransform _rect;
        private string _lastError;

        /// <summary>按当前设备安全区刷新；失败时保留已有布局并报告一次错误。</summary>
        public bool Refresh() => Refresh(Screen.safeArea, new Vector2(Screen.width, Screen.height));

        /// <summary>
        /// 按指定屏幕尺寸中的安全区刷新，可用于设备布局预览。
        /// 坐标先按 screenSize 归一化，再转换到实际 Canvas 显示尺寸与事件 Camera。
        /// 只支持主显示器的 ScreenSpaceOverlay/Camera 内容子容器；容器保持单位缩放且与 Canvas 轴向一致。
        /// </summary>
        public bool Refresh(Rect safeArea, Vector2 screenSize)
        {
            if (!_rect)
            {
                _rect = GetComponent<RectTransform>();
            }

            if (!_rect || !(_rect.parent is RectTransform parent))
            {
                return Reject("SafeAreaAdaptation requires a RectTransform with a RectTransform parent.");
            }

            var canvas = GetComponentInParent<Canvas>();
            if (!canvas || canvas.transform == transform)
            {
                return Reject("SafeAreaAdaptation requires a parent Canvas; apply it only to a content container.");
            }

            canvas = canvas.rootCanvas;
            if (canvas.renderMode == RenderMode.WorldSpace || canvas.targetDisplay != 0)
            {
                return Reject("SafeAreaAdaptation supports only screen-space UI on the main display.");
            }

            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (canvas.renderMode == RenderMode.ScreenSpaceCamera && !camera)
            {
                return Reject("SafeAreaAdaptation requires the screen-space Canvas camera.");
            }

            if (_rect.localScale != Vector3.one || _rect.localRotation != Quaternion.identity ||
                Quaternion.Inverse(canvas.transform.rotation) * parent.rotation != Quaternion.identity)
            {
                return Reject("SafeAreaAdaptation requires an unscaled content container aligned with the Canvas axes.");
            }

            if (!IsPositiveFinite(screenSize) || !IsPositiveFinite(safeArea.size) ||
                !IsFinite(safeArea.xMin) || !IsFinite(safeArea.yMin) || !IsFinite(safeArea.xMax) || !IsFinite(safeArea.yMax))
            {
                return Reject("SafeAreaAdaptation requires a positive finite screen size and safe area.");
            }

            if (!IsPositiveFinite(parent.rect.size))
            {
                return Reject("SafeAreaAdaptation requires a positive finite parent size.");
            }

            Vector2 displaySize = canvas.renderingDisplaySize;
            if (camera && camera.targetTexture)
            {
                displaySize = new Vector2(camera.targetTexture.width, camera.targetTexture.height);
            }

            if (!IsPositiveFinite(displaySize))
            {
                return Reject("SafeAreaAdaptation requires a positive finite rendering size.");
            }

            Vector2 screenMin = new Vector2(Mathf.Max(0, safeArea.xMin) / screenSize.x * displaySize.x,
                Mathf.Max(0, safeArea.yMin) / screenSize.y * displaySize.y);
            Vector2 screenMax = new Vector2(Mathf.Min(screenSize.x, safeArea.xMax) / screenSize.x * displaySize.x,
                Mathf.Min(screenSize.y, safeArea.yMax) / screenSize.y * displaySize.y);
            if (screenMax.x <= screenMin.x || screenMax.y <= screenMin.y)
            {
                return Reject("SafeAreaAdaptation safe area does not intersect the screen.");
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenMin, camera, out var localMin) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenMax, camera, out var localMax))
            {
                return Reject("SafeAreaAdaptation could not convert the safe area to the parent RectTransform.");
            }

            Rect bounds = parent.rect;
            var min = new Vector2(Mathf.Clamp01((localMin.x - bounds.xMin) / bounds.width),
                Mathf.Clamp01((localMin.y - bounds.yMin) / bounds.height));
            var max = new Vector2(Mathf.Clamp01((localMax.x - bounds.xMin) / bounds.width),
                Mathf.Clamp01((localMax.y - bounds.yMin) / bounds.height));
            if (!IsFinite(min.x) || !IsFinite(min.y) || !IsFinite(max.x) || !IsFinite(max.y) || max.x <= min.x || max.y <= min.y)
            {
                return Reject("SafeAreaAdaptation safe area does not intersect the parent layout.");
            }

            if (_rect.anchorMin != min)
            {
                _rect.anchorMin = min;
            }

            if (_rect.anchorMax != max)
            {
                _rect.anchorMax = max;
            }

            if (_rect.offsetMin != Vector2.zero)
            {
                _rect.offsetMin = Vector2.zero;
            }

            if (_rect.offsetMax != Vector2.zero)
            {
                _rect.offsetMax = Vector2.zero;
            }

            _lastError = null;
            return true;
        }

        /// <summary>保存最近校验错误并返回失败，相同连续错误不重复报告。</summary>
        private bool Reject(string error)
        {
            if (_lastError != error)
            {
                Debug.LogError(error, this);
            }

            _lastError = error;
            return false;
        }

        /// <summary>排除无法用于安全区坐标转换的 NaN 与无限值。</summary>
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        /// <summary>检查屏幕、安全区或父布局尺寸是否为正且有限。</summary>
        private static bool IsPositiveFinite(Vector2 value) => value.x > 0 && value.y > 0 && IsFinite(value.x) && IsFinite(value.y);

        /// <summary>重新启用内容容器时立即按当前设备安全区适配。</summary>
        private void OnEnable() => Refresh();
        /// <summary>跟随设备安全区、显示尺寸和父布局变化更新内容锚点。</summary>
        private void LateUpdate() => Refresh();
        /// <summary>启用期间换父级后重新转换安全区，避免继续沿用旧父坐标。</summary>
        private void OnTransformParentChanged()
        {
            if (isActiveAndEnabled)
            {
                Refresh();
            }
        }
    }
}
