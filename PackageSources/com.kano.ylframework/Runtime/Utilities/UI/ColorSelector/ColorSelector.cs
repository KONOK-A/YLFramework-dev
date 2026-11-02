using System;
using UnityEngine;
using UnityEngine.EventSystems;
using YLFramework.Core;

namespace YLFramework.UI
{
    /// <summary>按需使用的 HSV 取色工具；遵守祖先 Panel 的输入策略，不拥有窗口生命周期。</summary>
    [RequireComponent(typeof(ColorWheelGraphic))]
    public sealed class ColorSelector : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler, IInitializePotentialDragHandler
    {
        [SerializeField] private RectTransform outerCursor;
        [SerializeField] private RectTransform innerCursor;
        private ColorWheelGraphic _wheel;
        private Panel _inputPanel;
        private Action<Color> _changed;
        private float _hue;
        private float _saturation;
        private float _value;
        private float _alpha;
        private bool _isSessionActive;
        private int? _pointerId;
        private bool _isDraggingHue;
        /// <summary>最近一次初始化或取色得到的颜色；结束会话后保留，尚未初始化时为默认 Color。</summary>
        public Color CurrentColor
        {
            get; private set;
        }

        /// <summary>替换 SDR 初值和调用方回调；RGB 必须在 [0,1] 内。灰色采用 Hue=0，不继承上一轮任意 Hue。</summary>
        /// <param name="initial">有限的初始颜色；RGB 不接受 HDR 值，alpha 只检查有限性并在取色时原样保留。</param>
        /// <param name="onChanged">颜色改变时同步调用的回调，可为 null；初始化本身不触发通知。</param>
        /// <remarks>
        /// 重新开始会替换旧回调并放开旧指针。按 Unity 的祖先查询规则缓存本次 Panel，之后不随层级变化重新查找；
        /// 未找到 Panel 或缓存的 Panel 已销毁时，按独立控件处理输入，不再改查其他祖先。
        /// </remarks>
        /// <exception cref="ArgumentException">颜色分量不是有限数值，或 RGB 超出 [0,1]。</exception>
        public void BeginSession(Color initial, Action<Color> onChanged)
        {
            if (!IsFinite(initial.r) || !IsFinite(initial.g) || !IsFinite(initial.b) || !IsFinite(initial.a))
            {
                throw new ArgumentException("Color must be finite.", nameof(initial));
            }

            if (initial.r < 0 || initial.r > 1 || initial.g < 0 || initial.g > 1 || initial.b < 0 || initial.b > 1)
            {
                throw new ArgumentException("ColorSelector edits SDR RGB values in [0,1].", nameof(initial));
            }

            _wheel = GetComponent<ColorWheelGraphic>();
            _inputPanel = GetComponentInParent<Panel>();
            Color.RGBToHSV(initial, out _hue, out _saturation, out _value);
            if (_saturation == 0)
            {
                _hue = 0;
            }

            _alpha = initial.a;
            CurrentColor = initial;
            _changed = onChanged;
            _pointerId = null;
            _isSessionActive = true;
            UpdateVisual();
        }

        /// <summary>检查颜色分量或几何尺寸是否为有限数值，拒绝 NaN 和无穷值。</summary>
        private static bool IsFinite(float number) => !float.IsNaN(number) && !float.IsInfinity(number);
        /// <summary>结束取色会话并释放指针、回调及 Panel 引用，保留当前颜色，不关闭或销毁窗口。</summary>
        public void EndSession()
        {
            _isSessionActive = false;
            _pointerId = null;
            _changed = null;
            _inputPanel = null;
        }
        /// <summary>停用时只放开当前指针，保留会话与回调以供重新启用后继续取色。</summary>
        private void OnDisable() => _pointerId = null;
        /// <summary>销毁控件时结束会话，解除对调用方回调与 Panel 的引用。</summary>
        private void OnDestroy() => EndSession();
        /// <summary>尺寸改变时按当前颜色重排游标，仅处理已开始且色轮仍存在的会话。</summary>
        private void OnRectTransformDimensionsChange()
        {
            if (_isSessionActive && _wheel)
            {
                UpdateVisual();
            }
        }

        private bool CanInput => _isSessionActive && isActiveAndEnabled && (!_inputPanel || _inputPanel.IsLoaded && !_inputPanel.IsInputBlocked);

        /// <summary>把屏幕指针换算为以色轮中心为原点、矩形短边半长为单位的局部坐标。</summary>
        private bool Normalize(PointerEventData data, out Vector2 point)
        {
            point = default;
            var rect = _wheel.rectTransform.rect;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f;
            if (radius <= 0 || !IsFinite(radius))
            {
                throw new InvalidOperationException("ColorSelector requires a non-degenerate RectTransform.");
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_wheel.rectTransform, data.position, data.pressEventCamera, out var local))
            {
                return false;
            }

            point = (local - rect.center) / radius;
            return true;
        }

        /// <summary>关闭本次潜在拖拽的距离阈值，使移动不必先越过 EventSystem 的拖拽门槛。</summary>
        public void OnInitializePotentialDrag(PointerEventData data) => data.useDragThreshold = false;
        /// <summary>在允许输入时捕获首个左键指针，仅在色环或三角形内开始取色并固定拖拽区域。</summary>
        public void OnPointerDown(PointerEventData data)
        {
            if (!CanInput || _pointerId.HasValue || data.button != PointerEventData.InputButton.Left || !Normalize(data, out var point))
            {
                return;
            }

            float distance = point.magnitude;
            var weights = ColorWheelGraphic.Weights(point);
            bool ring = distance >= ColorWheelGraphic.InnerRadius && distance <= 1;
            if (!ring && (weights.x < 0 || weights.y < 0 || weights.z < 0))
            {
                return;
            }

            _pointerId = data.pointerId;
            _isDraggingHue = ring;
            Select(point);
        }

        /// <summary>沿按下时选定的区域处理已捕获指针，输入失效时释放捕获，不结束取色会话。</summary>
        public void OnDrag(PointerEventData data)
        {
            if (!CanInput)
            {
                _pointerId = null;
                return;
            }
            if (_pointerId != data.pointerId || !Normalize(data, out var point))
            {
                return;
            }

            Select(point);
        }

        /// <summary>放开匹配的指针，不发送颜色通知或结束会话。</summary>
        public void OnPointerUp(PointerEventData data)
        {
            if (_pointerId == data.pointerId)
            {
                _pointerId = null;
            }
        }

        /// <summary>按已选区域更新 HSV、保留初始 alpha，并在颜色变化时同步通知调用方。</summary>
        private void Select(Vector2 point)
        {
            if (_isDraggingHue)
            {
                if (point.sqrMagnitude < 0.000001f)
                {
                    return;
                }

                _hue = Mathf.Repeat(Mathf.Atan2(point.y, point.x) / (Mathf.PI * 2), 1);
            }
            else
            {
                // 三角形混合纯色、黑与白，因此 V 为纯色加白的权重，S 为纯色占这部分的比例。
                var weights = ColorWheelGraphic.Weights(ColorWheelGraphic.ClampTriangle(point));
                _value = Mathf.Clamp01(weights.x + weights.z);
                _saturation = _value > 0 ? Mathf.Clamp01(weights.x / _value) : 0;
            }
            var next = Color.HSVToRGB(_hue, _saturation, _value);
            next.a = _alpha;
            bool notify = next != CurrentColor;
            CurrentColor = next;
            UpdateVisual();
            // 回调可以 Close、开启新会话或销毁控件；通知后不再写旧状态。
            if (notify)
            {
                _changed?.Invoke(next);
            }
        }

        /// <summary>同步色轮的 Hue 与两个可选游标的位置，不发送业务通知。</summary>
        private void UpdateVisual()
        {
            _wheel.Hue = _hue;
            float radius = Mathf.Min(_wheel.rectTransform.rect.width, _wheel.rectTransform.rect.height) * 0.5f;
            var center = _wheel.rectTransform.rect.center;
            if (outerCursor)
            {
                outerCursor.localPosition = center + new Vector2(Mathf.Cos(_hue * Mathf.PI * 2), Mathf.Sin(_hue * Mathf.PI * 2)) * (radius * 0.88f);
            }

            if (innerCursor)
            {
                float colored = _saturation * _value;
                innerCursor.localPosition = center + radius * (ColorWheelGraphic.HueVertex * colored +
                    ColorWheelGraphic.BlackVertex * (1 - _value) + ColorWheelGraphic.WhiteVertex * (_value - colored));
            }
        }
    }
}
