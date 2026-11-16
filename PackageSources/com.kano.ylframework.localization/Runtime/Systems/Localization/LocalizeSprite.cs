using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

namespace YLFramework.Localization
{
    /// <summary>UGUI Image 的官方 Sprite 绑定，资产由 Localization/Addressables 管理。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Image))]
    [AddComponentMenu("YLFramework/Localization/Localize Sprite")]
    public sealed class LocalizeSprite : LocalizationBehaviour
    {
        [SerializeField] private LocalizedSprite _spriteReference = new LocalizedSprite();
        private Image _target;
        private Sprite _original;
        private Sprite _last;
        private bool _wroteValue;

        /// <summary>官方 Sprite 模板。</summary>
        public LocalizedSprite SpriteReference
        {
            get => _spriteReference;
            set
            {
                _spriteReference = value;
                Rebind();
            }
        }

        /// <inheritdoc/>
        protected override void CreateBindings(LocalizationSession session)
        {
            _target = GetComponent<Image>();
            _original = _target.sprite;
            Own(session.BindAsset(_spriteReference, value =>
            {
                if (!this || !isActiveAndEnabled || !_target)
                    return;
                _target.sprite = _last = value;
                _wroteValue = true;
            }));
        }

        /// <inheritdoc/>
        protected override void RestoreValues()
        {
            if (_target && _wroteValue && _target.sprite == _last)
                _target.sprite = _original;
            _wroteValue = false;
        }
    }
}
