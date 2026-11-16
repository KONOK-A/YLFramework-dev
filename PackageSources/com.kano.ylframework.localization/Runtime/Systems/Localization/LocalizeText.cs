using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

namespace YLFramework.Localization
{
    /// <summary>UGUI Text 的官方字符串及可选字体绑定，无反射或 ResourceImage 依赖。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Text))]
    [AddComponentMenu("YLFramework/Localization/Localize Text")]
    public sealed class LocalizeText : LocalizationBehaviour
    {
        [SerializeField] private LocalizedString _stringReference = new LocalizedString();
        [SerializeField] private LocalizedFont _fontReference = new LocalizedFont();
        private Text _target;
        private string _originalText;
        private string _lastText;
        private Font _originalFont;
        private Font _lastFont;
        private bool _wroteText;
        private bool _wroteFont;

        /// <summary>官方字符串模板；替换时重建本组件绑定。</summary>
        public LocalizedString StringReference
        {
            get => _stringReference;
            set
            {
                _stringReference = value;
                Rebind();
            }
        }

        /// <summary>可选官方字体引用；为空则保留 Text 原字体。</summary>
        public LocalizedFont FontReference
        {
            get => _fontReference;
            set
            {
                _fontReference = value;
                Rebind();
            }
        }

        /// <summary>本轮私有字符串绑定，用于配置业务局部变量。</summary>
        public LocalizedStringBinding StringBinding
        {
            get; private set;
        }

        /// <inheritdoc/>
        protected override void CreateBindings(LocalizationSession session)
        {
            _target = GetComponent<Text>();
            _originalText = _target.text;
            _originalFont = _target.font;
            if (_fontReference != null && !_fontReference.IsEmpty)
                Own(session.BindAsset(_fontReference, font =>
                {
                    if (!this || !isActiveAndEnabled || !_target)
                        return;
                    _target.font = _lastFont = font;
                    _wroteFont = true;
                }));
            StringBinding = session.BindString(_stringReference, value =>
            {
                if (!this || !isActiveAndEnabled || !_target)
                    return;
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
                if (_wroteText && _target.text == _lastText)
                    _target.text = _originalText;
                if (_wroteFont && _target.font == _lastFont)
                    _target.font = _originalFont;
            }
            _wroteText = _wroteFont = false;
            StringBinding = null;
        }
    }
}
