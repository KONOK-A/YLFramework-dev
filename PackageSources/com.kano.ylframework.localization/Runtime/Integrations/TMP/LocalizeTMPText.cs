#if UNITY_6000_0_OR_NEWER || YL_LOCALIZATION_TMP
using TMPro;
using UnityEngine;
using UnityEngine.Localization;

namespace YLFramework.Localization
{
    /// <summary>TMP 文本和字体的强类型绑定，不要求消费者为 text/font 反射成员添加保留规则。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TMP_Text))]
    [AddComponentMenu("YLFramework/Localization/Localize TMP Text")]
    public sealed class LocalizeTMPText : LocalizationBehaviour
    {
        [SerializeField] private LocalizedString _stringReference = new LocalizedString();
        [SerializeField] private LocalizedTmpFont _fontReference = new LocalizedTmpFont();
        private TMP_Text _target;
        private string _originalText;
        private string _lastText;
        private TMP_FontAsset _originalFont;
        private TMP_FontAsset _lastFont;
        private bool _wroteText;
        private bool _wroteFont;

        /// <summary>官方字符串模板。</summary>
        public LocalizedString StringReference
        {
            get => _stringReference;
            set { _stringReference = value; Rebind(); }
        }

        /// <summary>可选官方 TMP 字体模板。</summary>
        public LocalizedTmpFont FontReference
        {
            get => _fontReference;
            set { _fontReference = value; Rebind(); }
        }

        /// <summary>本轮字符串绑定，可在组配置回调中连接业务变量。</summary>
        public LocalizedStringBinding StringBinding { get; private set; }

        /// <inheritdoc/>
        protected override void CreateBindings(LocalizationSession session)
        {
            _target = GetComponent<TMP_Text>();
            _originalText = _target.text;
            _originalFont = _target.font;
            if (_fontReference != null && !_fontReference.IsEmpty)
                Own(session.BindAsset(_fontReference, font =>
                {
                    if (!this || !isActiveAndEnabled || !_target) return;
                    _target.font = _lastFont = font;
                    _wroteFont = true;
                }));
            StringBinding = session.BindString(_stringReference, value =>
            {
                if (!this || !isActiveAndEnabled || !_target) return;
                _target.text = _lastText = value;
                _wroteText = true;
            });
            Own(StringBinding);
        }

        /// <inheritdoc/>
        protected override void RestoreValues()
        {
            if (_target)
            {
                if (_wroteText && _target.text == _lastText) _target.text = _originalText;
                if (_wroteFont && _target.font == _lastFont) _target.font = _originalFont;
            }
            _wroteText = _wroteFont = false;
            StringBinding = null;
        }
    }
}
#endif
