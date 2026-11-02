using System;
using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.Core
{
    /// <summary>参考分辨率与宽高匹配的唯一配置入口，不改变 CanvasScaler 的缩放模式。</summary>
    [ExecuteInEditMode]
    [RequireComponent(typeof(CanvasScaler))]
    [DisallowMultipleComponent]
    public class AutomaticAdaptationCanvasScaler : MonoBehaviour
    {
        // 旧资源没有此字段，首次读取时采用其 CanvasScaler 已有配置。
        [SerializeField] private Vector2 referenceResolution;
        [SerializeField] private bool automaticMatch = true;
        [SerializeField, Range(0, 1)] private float manualMatch;
        private CanvasScaler _scaler;
        private Canvas _canvas;
        private string _lastError;

        /// <summary>设计参考分辨率；修改后立即刷新。</summary>
        public Vector2 ReferenceResolution
        {
            get
            {
                AdoptExistingConfiguration();
                return referenceResolution;
            }
            set => Configure(value, automaticMatch, ManualMatch);
        }

        /// <summary>自动选择宽或高匹配；关闭后使用 ManualMatch。</summary>
        public bool AutomaticMatch
        {
            get => automaticMatch;
            set => Configure(ReferenceResolution, value, ManualMatch);
        }

        /// <summary>手动宽高匹配值，范围为 0 到 1。</summary>
        public float ManualMatch
        {
            get
            {
                AdoptExistingConfiguration();
                return manualMatch;
            }
            set => Configure(ReferenceResolution, automaticMatch, value);
        }

        /// <summary>一次提交根的适配配置；非法参数不会改变已有配置。</summary>
        public void Configure(Vector2 referenceResolution, bool automaticMatch, float manualMatch)
        {
            if (!IsPositiveFinite(referenceResolution))
            {
                throw new ArgumentException("Reference resolution must contain positive finite values.", nameof(referenceResolution));
            }

            if (float.IsNaN(manualMatch) || manualMatch < 0 || manualMatch > 1)
            {
                throw new ArgumentException("Manual match must be between 0 and 1.", nameof(manualMatch));
            }

            this.referenceResolution = referenceResolution;
            this.automaticMatch = automaticMatch;
            this.manualMatch = manualMatch;
            Refresh();
        }

        /// <summary>重新应用配置；依赖或尺寸无效时报告一次错误并返回 false。</summary>
        public bool Refresh()
        {
            AdoptExistingConfiguration();
            if (!_scaler || !_canvas)
            {
                return Reject("AutomaticAdaptationCanvasScaler requires a Canvas and CanvasScaler.");
            }

            if (!IsPositiveFinite(referenceResolution) || float.IsNaN(manualMatch) || manualMatch < 0 || manualMatch > 1)
            {
                return Reject("AutomaticAdaptationCanvasScaler requires a positive finite reference resolution and a manual match between 0 and 1.");
            }

            float match = manualMatch;
            if (automaticMatch)
            {
                // 与已安装 CanvasScaler 使用同一显示尺寸，避免参考分辨率更新后沿用旧 match。
                Vector2 size = _canvas.renderingDisplaySize;
                int display = _canvas.targetDisplay;
                if (display > 0 && display < Display.displays.Length)
                {
                    size = new Vector2(Display.displays[display].renderingWidth, Display.displays[display].renderingHeight);
                }

                if (!IsPositiveFinite(size))
                {
                    return Reject("AutomaticAdaptationCanvasScaler requires a positive finite rendering size.");
                }

                match = size.x / size.y > referenceResolution.x / referenceResolution.y ? 1 : 0;
            }
            if (_scaler.referenceResolution != referenceResolution)
            {
                _scaler.referenceResolution = referenceResolution;
            }

            if (_scaler.matchWidthOrHeight != match)
            {
                _scaler.matchWidthOrHeight = match;
            }

            _lastError = null;
            return true;
        }

        /// <summary>缓存本对象组件；参考分辨率仍为旧资源零值时，承接 CanvasScaler 现有分辨率和匹配值。</summary>
        private void AdoptExistingConfiguration()
        {
            if (!_scaler)
            {
                _scaler = GetComponent<CanvasScaler>();
            }

            if (!_canvas)
            {
                _canvas = GetComponent<Canvas>();
            }

            if (referenceResolution.x == 0 && referenceResolution.y == 0 && _scaler)
            {
                referenceResolution = _scaler.referenceResolution;
                manualMatch = _scaler.matchWidthOrHeight;
            }
        }

        /// <summary>记录最近一次校验错误并返回失败，相同连续错误不重复写日志。</summary>
        private bool Reject(string error)
        {
            if (_lastError != error)
            {
                Debug.LogError(error, this);
            }

            _lastError = error;
            return false;
        }

        /// <summary>排除不可用于分辨率和比例计算的非正、NaN 或无限尺寸。</summary>
        private static bool IsPositiveFinite(Vector2 value) => value.x > 0 && value.y > 0 &&
            !float.IsInfinity(value.x) && !float.IsInfinity(value.y);

        /// <summary>唤醒时承接旧资源配置，不强制切换缩放模式。</summary>
        private void Awake() => AdoptExistingConfiguration();
        /// <summary>重新启用时立即应用当前适配配置。</summary>
        private void OnEnable() => Refresh();
        /// <summary>持续响应显示尺寸或 Inspector 配置变化，编辑模式也沿用同一刷新入口。</summary>
        private void Update() => Refresh();
        /// <summary>组件启用期间在矩形尺寸变化后重新计算宽高匹配。</summary>
        private void OnRectTransformDimensionsChange()
        {
            if (isActiveAndEnabled)
            {
                Refresh();
            }
        }
    }
}
