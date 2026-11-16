using System.Collections;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using YLFramework.Binding;

namespace YLFramework.Localization.Samples
{
    /// <summary>独立 UI 闭环：官方 Locale、私有业务变量、绑定重开；不创建或替换全局设置。</summary>
    public sealed class LocalizationUIExample : MonoBehaviour
    {
        [SerializeField] private LocalizationGroup _group;
        [SerializeField] private LocalizeText _counter;
        [SerializeField] private Text _status;
        private readonly BindableProperty<int> _count = new BindableProperty<int>(5);
        private Coroutine _initialization;
        private bool _initialized;

        private void OnEnable()
        {
            _group.StateChanged += ShowState;
            _initialization = StartCoroutine(Initialize());
        }

        private IEnumerator Initialize()
        {
            if (!LocalizationSettings.HasSettings)
            {
                _status.text = "Configure official Localization Settings first.";
                yield break;
            }
            var initialization = LocalizationSettings.InitializationOperation;
            yield return initialization;
            if (initialization.Status != AsyncOperationStatus.Succeeded)
            {
                _status.text = initialization.OperationException?.Message ?? "Localization initialization failed.";
                yield break;
            }
            _initialized = true;
            RestartBindings();
        }

        /// <summary>普通业务数据更新，不手动刷新文本，也不修改共享表。</summary>
        public void Increase() => _count.Value++;
        /// <summary>显式重建当前展示轮次，迟到的旧结果不能写回。</summary>
        public void RestartBindings()
        {
            if (_initialized)
                _group.BeginSession(null, _ => _counter.StringBinding.BindVariable("count", _count));
        }
        /// <summary>使用官方 Locale 标识。</summary>
        public void English() => SelectLocale("en");
        /// <summary>使用官方 Locale 标识。</summary>
        public void French() => SelectLocale("fr");

        private void SelectLocale(string code)
        {
            if (!_initialized)
                return;
            var locale = LocalizationSettings.AvailableLocales.GetLocale(code);
            if (locale)
                LocalizationSettings.SelectedLocale = locale;
        }

        private void ShowState(LocalizationGroupState state)
        {
            _status.text = state == LocalizationGroupState.Failed ? _group.Error?.Message : "Content: " + state;
        }

        private void OnDisable()
        {
            if (_initialization != null)
                StopCoroutine(_initialization);
            _initialization = null;
            _initialized = false;
            _group.StateChanged -= ShowState;
            _group.EndSession();
        }
    }
}
